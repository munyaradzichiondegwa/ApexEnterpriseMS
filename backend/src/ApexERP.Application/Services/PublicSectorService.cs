using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record BudgetUtilization(Guid FundCentreId, string Code, string Name, decimal Appropriation,
        decimal Commitments, decimal Actuals, decimal Available, decimal PercentUtilised, bool OverNinetyPercent);
    public record PublicSectorExpenditureLine(Guid FundCentreId, string Code, string Name,
        FundCentreLevel Level, Guid? ParentId, string? ParentCode, string? ParentName,
        decimal Appropriation, decimal CarriedForward, decimal TotalBudget,
        decimal CommittedToDate, decimal RealisedAgainstCommitments, decimal OutstandingCommitments,
        decimal PostedActuals, decimal ActualVariance, decimal ActualVariancePercent,
        decimal AvailableBudget, decimal PercentUtilised, decimal LapsedAmount, bool IsClosed);
    public record YearEndCloseResult(Guid FundCentreId, decimal UnexpendedBalance, decimal CarriedForward, decimal Lapsed, Guid? NewFundCentreId);
    public record CreateBudgetExecutionActualResult(Guid Id, string SourceReference, decimal Amount, string Currency,
        PublicSectorBudgetActualEventKind EventKind, PublicSectorBudgetActualStatus Status);
    public record PublicSectorBudgetVarianceLine(Guid FundCentreId, string FundCentreCode, string FundCentreName,
        int FinancialYear, Guid GlAccountId, string AccountCode, string AccountName,
        Guid? CostCentreId, string? CostCentreCode, string? CostCentreName,
        decimal ApprovedBudget, decimal PostedActuals, decimal Variance, decimal VariancePercent);


    /// <summary>
    /// PRD Module 16 (Public Sector & Government): Vote/Program/Sub-Program fund hierarchy with
    /// commitment (encumbrance) accounting. Commitments are tracked as a budgetary-control ledger
    /// separate from the double-entry GL (the standard approach -- encumbrances are not GAAP/IFRS
    /// balance sheet transactions, they are an internal appropriation-control mechanism), so
    /// Available Budget = Appropriation - Commitments - Actuals is always computable in real time
    /// without risking the integrity of the real accounting ledger.
    /// </summary>
    public class PublicSectorService
    {
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;

        public PublicSectorService(ApexErpDbContext db, AuditService audit)
        {
            _db = db;
            _audit = audit;
        }


        public sealed record BudgetVersionLineInput(Guid FundCentreId, Guid GlAccountId, Guid? CostCentreId,
            string Description, string ClassificationCode, decimal Amount, string? NoteReference = null,
            string? TreasuryClassificationCode = null);
        public sealed record BudgetComparisonLine(int LineNumber, string Description, string FundCentreCode,
            string FundCentreName, string FundCentreLevel, string CostCentreCode, string CostCentreName,
            string AccountCode, string AccountName, string AccountType, string ClassificationCode,
            string? TreasuryClassificationCode, string? NoteReference,
            decimal OriginalBudget, decimal? FinalBudget, decimal? ActualOnComparableBasis,
            decimal? Variance, decimal? VariancePercent, string? OriginalToFinalExplanation,
            Guid BudgetVersionLineId);
        public sealed record BudgetFinancialStatementReconciliation(bool Required, bool Complete, string Message,
            decimal? ComparableRevenueActual, decimal RevenueAdjustment, decimal? FinancialStatementRevenueActual,
            bool RevenueCovered, decimal? ComparableExpenseActual, decimal ExpenseAdjustment,
            decimal? FinancialStatementExpenseActual, bool ExpenseCovered);
        public sealed record BudgetComparisonReport(Guid OriginalBudgetVersionId, Guid? FinalBudgetVersionId,
            int FinancialYear, string Currency, string AccountingBasis, string ClassificationBasis,
            string EntityScope, DateTime PeriodStart, DateTime PeriodEnd, bool FinalBudgetAvailable,
            bool ComparableActualsAvailable, string ComparabilityMessage,
            IReadOnlyList<BudgetComparisonLine> Lines,
            IReadOnlyList<PublicSectorBudgetReconciliation> Reconciliations,
            IReadOnlyList<PublicSectorBudgetVarianceExplanation> VarianceExplanations,
            bool VarianceExplanationsComplete, bool OriginalToFinalExplanationsComplete,
            BudgetFinancialStatementReconciliation FinancialStatementReconciliation);

        public async Task<PublicSectorBudgetVersion> CreateBudgetVersionAsync(Guid companyId, int financialYear,
            PublicSectorBudgetVersionKind kind, string currency, AccountingBasis accountingBasis,
            string classificationBasis, string entityScope, DateTime periodStart, DateTime periodEnd,
            string authorityReference, string? amendmentReason, IReadOnlyCollection<BudgetVersionLineInput> lines,
            Guid actorId, CancellationToken ct = default,
            PublicSectorBudgetAmendmentNature? amendmentNature = null)
        {
            if (financialYear < 2000 || financialYear > 2200 || periodStart >= periodEnd ||
                periodStart.Year > financialYear || periodEnd.Year < financialYear ||
                string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 ||
                string.IsNullOrWhiteSpace(classificationBasis) || string.IsNullOrWhiteSpace(entityScope) ||
                string.IsNullOrWhiteSpace(authorityReference) || lines.Count == 0)
                throw new InvalidOperationException("A valid reporting year, period, currency, classification, entity scope, authority reference and at least one budget line are required.");
            if (kind == PublicSectorBudgetVersionKind.Revised &&
                (string.IsNullOrWhiteSpace(amendmentReason) || !amendmentNature.HasValue ||
                 !Enum.IsDefined(amendmentNature.Value)))
                throw new InvalidOperationException("A revised budget requires both an explanation and a classification stating whether changes are reallocations within the budget, other authorised factors, or a mixture.");
            if (kind == PublicSectorBudgetVersionKind.Original && amendmentNature.HasValue)
                throw new InvalidOperationException("An original budget cannot carry an amendment nature.");
            if (lines.Any(x => x.Amount < 0 || string.IsNullOrWhiteSpace(x.Description) || string.IsNullOrWhiteSpace(x.ClassificationCode)))
                throw new InvalidOperationException("Budget lines require non-negative amounts, descriptions and GL SubType classification codes.");
            var requiresTreasuryClassification =
                classificationBasis.Contains("ZFRM", StringComparison.OrdinalIgnoreCase) ||
                classificationBasis.Contains("Zimbabwe Financial Reporting Manual", StringComparison.OrdinalIgnoreCase);
            if (requiresTreasuryClassification && lines.Any(x => string.IsNullOrWhiteSpace(x.TreasuryClassificationCode)))
                throw new InvalidOperationException("A ZFRM Appendix 5 Treasury classification is required for every budget line.");

            var originalExists = await _db.PublicSectorBudgetVersions.AnyAsync(x =>
                x.CompanyId == companyId && x.FinancialYear == financialYear &&
                x.Kind == PublicSectorBudgetVersionKind.Original &&
                x.Status != PublicSectorBudgetVersionStatus.Rejected, ct);
            if (kind == PublicSectorBudgetVersionKind.Original && originalExists)
                throw new InvalidOperationException("Only one original budget version is permitted for a company and financial year.");
            if (kind == PublicSectorBudgetVersionKind.Revised && !await _db.PublicSectorBudgetVersions.AnyAsync(x =>
                x.CompanyId == companyId && x.FinancialYear == financialYear &&
                x.Kind == PublicSectorBudgetVersionKind.Original && x.Status == PublicSectorBudgetVersionStatus.Approved, ct))
                throw new InvalidOperationException("A revised budget can only be created after the original budget has been approved.");

            var duplicateDimension = lines.GroupBy(x =>
                $"{x.FundCentreId:N}|{x.GlAccountId:N}|{x.CostCentreId?.ToString("N") ?? "none"}|{x.ClassificationCode.Trim()}",
                StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
            if (duplicateDimension)
                throw new InvalidOperationException("A budget version cannot contain duplicate fund centre, account, cost centre and classification dimensions.");

            var versionNumber = (await _db.PublicSectorBudgetVersions
                .Where(x => x.CompanyId == companyId && x.FinancialYear == financialYear && x.Kind == kind)
                .Select(x => (int?)x.VersionNumber).MaxAsync(ct) ?? 0) + 1;

            foreach (var line in lines)
            {
                if (!await _db.FundCentres.AnyAsync(x => x.Id == line.FundCentreId &&
                    x.CompanyId == companyId && x.FinancialYear == financialYear, ct))
                    throw new InvalidOperationException("Every budget line must reference a fund centre in the same company and financial year.");
                var account = await _db.GlAccounts.FirstOrDefaultAsync(x => x.Id == line.GlAccountId &&
                    x.CompanyId == companyId && x.IsActive &&
                    (x.Type == GlAccountType.Income || x.Type == GlAccountType.Expense ||
                     (accountingBasis == AccountingBasis.Cash &&
                      (x.CashFlowCategory == CashFlowCategory.Operating ||
                       x.CashFlowCategory == CashFlowCategory.Investing ||
                       x.CashFlowCategory == CashFlowCategory.Financing))), ct)
                    ?? throw new InvalidOperationException("Budget lines must map to an active income/expense GL account, or for cash-basis budgets an active account classified to operating, investing or financing cash flows, in the same company.");
                if (string.IsNullOrWhiteSpace(account.SubType) ||
                    !string.Equals(account.SubType.Trim(), line.ClassificationCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Each GL SubType classification code must match the selected GL account's configured SubType classification.");
                if (requiresTreasuryClassification &&
                    account.Type != GlAccountType.Income && account.Type != GlAccountType.Expense)
                    throw new InvalidOperationException("ZFRM Appendix 5 budget lines must map to income or expense GL accounts.");
                if (line.CostCentreId.HasValue && !await _db.CostCentres.AnyAsync(x =>
                    x.Id == line.CostCentreId.Value && x.CompanyId == companyId && x.IsActive, ct))
                    throw new InvalidOperationException("A budget line references an inactive or foreign cost centre.");
            }

            var version = new PublicSectorBudgetVersion
            {
                CompanyId = companyId, FinancialYear = financialYear, VersionNumber = versionNumber,
                Kind = kind, Currency = currency.Trim().ToUpperInvariant(), AccountingBasis = accountingBasis,
                ClassificationBasis = classificationBasis.Trim(), EntityScope = entityScope.Trim(),
                PeriodStart = periodStart, PeriodEnd = periodEnd, AuthorityReference = authorityReference.Trim(),
                AmendmentReason = amendmentReason?.Trim(), AmendmentNature = amendmentNature, CreatedBy = actorId,
                Lines = lines.Select((line, index) => new PublicSectorBudgetVersionLine
                {
                    CompanyId = companyId, FundCentreId = line.FundCentreId, GlAccountId = line.GlAccountId,
                    CostCentreId = line.CostCentreId, LineNumber = index + 1, Description = line.Description.Trim(),
                    ClassificationCode = line.ClassificationCode.Trim(),
                    TreasuryClassificationCode = string.IsNullOrWhiteSpace(line.TreasuryClassificationCode)
                        ? null : line.TreasuryClassificationCode.Trim(),
                    NoteReference = string.IsNullOrWhiteSpace(line.NoteReference) ? null : line.NoteReference.Trim(),
                    Amount = line.Amount, CreatedBy = actorId
                }).ToList()
            };
            _db.PublicSectorBudgetVersions.Add(version);
            _audit.Record(companyId, actorId, "PublicSectorBudgetVersion.Created", "PublicSector",
                nameof(PublicSectorBudgetVersion), version.Id, null,
                new { version.FinancialYear, version.Kind, version.VersionNumber, version.Currency,
                    version.AccountingBasis, version.ClassificationBasis, version.EntityScope,
                    version.AuthorityReference, LineCount = version.Lines.Count });
            await _db.SaveChangesAsync(ct);
            return version;
        }

        public async Task MarkBudgetVersionPendingAsync(Guid companyId, Guid versionId, Guid workflowId,
            CancellationToken ct = default)
        {
            var version = await _db.PublicSectorBudgetVersions.Include(x => x.Lines).FirstOrDefaultAsync(x =>
                x.Id == versionId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Budget version not found.");
            if (version.Status != PublicSectorBudgetVersionStatus.Draft)
                throw new InvalidOperationException("Only a draft budget version can be submitted for approval.");
            var total = version.Lines.Sum(x => x.Amount);
            var workflow = await _db.WorkflowRequests.FirstOrDefaultAsync(x =>
                x.Id == workflowId && x.CompanyId == companyId && x.RequestType == "PublicSectorBudgetVersion" &&
                x.ReferenceId == version.Id && x.Amount == total && x.Status == WorkflowStatus.Pending, ct)
                ?? throw new InvalidOperationException("A matching pending approval workflow is required.");
            version.ApprovalWorkflowId = workflow.Id;
            version.Status = PublicSectorBudgetVersionStatus.PendingApproval;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<PublicSectorBudgetVersion> FinaliseBudgetVersionApprovalAsync(Guid companyId,
            Guid versionId, Guid actorId, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                var version = await _db.PublicSectorBudgetVersions.Include(x => x.Lines).FirstOrDefaultAsync(x =>
                    x.Id == versionId && x.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Budget version not found.");
                if (version.Status != PublicSectorBudgetVersionStatus.PendingApproval || !version.ApprovalWorkflowId.HasValue)
                    throw new InvalidOperationException("Only a pending budget version with an approval workflow can be approved.");
                var workflow = await _db.WorkflowRequests.Include(x => x.Approvals).FirstOrDefaultAsync(x =>
                    x.Id == version.ApprovalWorkflowId.Value && x.CompanyId == companyId &&
                    x.RequestType == "PublicSectorBudgetVersion" && x.ReferenceId == version.Id &&
                    x.Status == WorkflowStatus.Approved && x.Approvals.Any(a => a.Approved && a.ApproverId == actorId), token)
                    ?? throw new InvalidOperationException("The independent budget approval workflow has not been approved.");
                if (workflow.SubmittedBy == actorId)
                    throw new InvalidOperationException("The budget submitter cannot finalise their own approval.");
                if (version.Kind == PublicSectorBudgetVersionKind.Revised &&
                    !await _db.PublicSectorBudgetVersions.AnyAsync(x => x.CompanyId == companyId &&
                        x.FinancialYear == version.FinancialYear && x.Kind == PublicSectorBudgetVersionKind.Original &&
                        x.Status == PublicSectorBudgetVersionStatus.Approved, token))
                    throw new InvalidOperationException("The original budget must remain approved before a revised budget can be approved.");
                version.Status = PublicSectorBudgetVersionStatus.Approved;
                version.ApprovedBy = actorId;
                version.ApprovedAt = DateTime.UtcNow;
                _audit.Record(companyId, actorId, "PublicSectorBudgetVersion.Approved", "PublicSector",
                    nameof(PublicSectorBudgetVersion), version.Id,
                    new { Status = PublicSectorBudgetVersionStatus.PendingApproval },
                    new { version.Status, version.ApprovedBy, version.ApprovedAt, version.ApprovalWorkflowId,
                        version.Kind, version.VersionNumber, Total = version.Lines.Sum(x => x.Amount) });
                await _db.SaveChangesAsync(token);
                return version;
            }, ct);
        }

        public async Task<PublicSectorBudgetReconciliation> CreateBudgetReconciliationAsync(Guid companyId,
            int financialYear, PublicSectorBudgetReconciliationType type, string description, string statementSection,
            decimal operatingAmount, decimal investingAmount, decimal financingAmount, string supportingReference,
            Guid actorId, CancellationToken ct = default, decimal revenueAmount = 0m, decimal expenseAmount = 0m,
            bool isNoDifferenceAttestation = false)
        {
            if (financialYear < 2000 || financialYear > 2200 || string.IsNullOrWhiteSpace(description) ||
                string.IsNullOrWhiteSpace(statementSection) || string.IsNullOrWhiteSpace(supportingReference))
                throw new InvalidOperationException("A valid year, description, statement section and supporting reference are required.");
            statementSection = statementSection.Trim();
            if (new[] { "Revenue", "Expense", "Operating", "Investing", "Financing" }
                .Contains(statementSection, StringComparer.OrdinalIgnoreCase))
                statementSection = new[] { "Revenue", "Expense", "Operating", "Investing", "Financing" }
                    .First(x => string.Equals(x, statementSection, StringComparison.OrdinalIgnoreCase));
            var approvedBudget = await _db.PublicSectorBudgetVersions.Where(x =>
                    x.CompanyId == companyId && x.FinancialYear == financialYear &&
                    x.Status == PublicSectorBudgetVersionStatus.Approved)
                .OrderByDescending(x => x.Kind).ThenByDescending(x => x.VersionNumber)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("An approved IPSAS 24 budget version is required before recording a reconciliation.");
            if (approvedBudget.AccountingBasis != AccountingBasis.Accrual &&
                approvedBudget.AccountingBasis != AccountingBasis.Cash)
                throw new InvalidOperationException("This budget accounting basis does not yet have an approved IPSAS 24 reconciliation adapter.");
            var allAmountsZero = revenueAmount == 0m && expenseAmount == 0m &&
                operatingAmount == 0m && investingAmount == 0m && financingAmount == 0m;
            if (allAmountsZero != isNoDifferenceAttestation)
                throw new InvalidOperationException("A zero-value reconciliation must be explicitly recorded as a no-difference attestation; a normal reconciliation must contain a signed adjustment.");
            if (isNoDifferenceAttestation &&
                !new[] { "Revenue", "Expense" }.Contains(statementSection.Trim(), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("A no-difference attestation must explicitly cover Revenue or Expense.");
            if (string.Equals(statementSection.Trim(), "Revenue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(statementSection.Trim(), "Expense", StringComparison.OrdinalIgnoreCase))
            {
                var existingSectionRows = await _db.PublicSectorBudgetReconciliations.Where(x =>
                    x.CompanyId == companyId && x.FinancialYear == financialYear &&
                    x.StatementSection == statementSection.Trim() &&
                    x.Status != PublicSectorBudgetReconciliationStatus.Rejected).ToListAsync(ct);
                if (isNoDifferenceAttestation && existingSectionRows.Count > 0)
                    throw new InvalidOperationException("A no-difference attestation cannot coexist with another active reconciliation for the same financial-statement section.");
                if (!isNoDifferenceAttestation && existingSectionRows.Any(x => x.IsNoDifferenceAttestation))
                    throw new InvalidOperationException("An active no-difference attestation already covers this section; reject it before recording an adjustment.");
            }
            // Cash-budget actuals are on the budget's cash basis. Revenue/expense adjustments
            // reconcile those comparable amounts to accrual financial-statement actuals;
            // operating/investing/financing adjustments remain available for the separate cash-flow bridge.
            var reconciliation = new PublicSectorBudgetReconciliation
            {
                CompanyId = companyId, FinancialYear = financialYear, Type = type,
                Description = description.Trim(), StatementSection = statementSection.Trim(),
                RevenueAmount = revenueAmount, ExpenseAmount = expenseAmount,
                OperatingAmount = operatingAmount, InvestingAmount = investingAmount,
                FinancingAmount = financingAmount, SupportingReference = supportingReference.Trim(),
                IsNoDifferenceAttestation = isNoDifferenceAttestation,
                Status = PublicSectorBudgetReconciliationStatus.Draft, CreatedBy = actorId
            };
            _db.PublicSectorBudgetReconciliations.Add(reconciliation);
            _audit.Record(companyId, actorId, "PublicSectorBudgetReconciliation.Created", "PublicSector",
                nameof(PublicSectorBudgetReconciliation), reconciliation.Id, null,
                new { reconciliation.FinancialYear, reconciliation.Type, reconciliation.Description,
                    reconciliation.StatementSection, reconciliation.RevenueAmount,
                    reconciliation.ExpenseAmount, reconciliation.OperatingAmount,
                    reconciliation.InvestingAmount, reconciliation.FinancingAmount,
                    reconciliation.SupportingReference, reconciliation.IsNoDifferenceAttestation });
            await _db.SaveChangesAsync(ct);
            return reconciliation;
        }

        public async Task MarkBudgetReconciliationPendingAsync(Guid companyId, Guid reconciliationId,
            Guid workflowId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorBudgetReconciliations.FirstOrDefaultAsync(x =>
                x.Id == reconciliationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Budget reconciliation row not found.");
            if (item.Status != PublicSectorBudgetReconciliationStatus.Draft)
                throw new InvalidOperationException("Only a draft reconciliation row can be submitted for approval.");
            var approvalAmount = Math.Abs(item.RevenueAmount) + Math.Abs(item.ExpenseAmount) +
                Math.Abs(item.OperatingAmount) + Math.Abs(item.InvestingAmount) + Math.Abs(item.FinancingAmount);
            var workflow = await _db.WorkflowRequests.FirstOrDefaultAsync(x =>
                x.Id == workflowId && x.CompanyId == companyId && x.RequestType == "PublicSectorBudgetReconciliation" &&
                x.ReferenceId == item.Id && x.Amount == approvalAmount && x.Status == WorkflowStatus.Pending, ct)
                ?? throw new InvalidOperationException("A matching pending reconciliation approval workflow is required.");
            item.ApprovalWorkflowId = workflow.Id;
            item.Status = PublicSectorBudgetReconciliationStatus.PendingApproval;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<PublicSectorBudgetReconciliation> FinaliseBudgetReconciliationApprovalAsync(
            Guid companyId, Guid reconciliationId, Guid actorId, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                var item = await _db.PublicSectorBudgetReconciliations.FirstOrDefaultAsync(x =>
                    x.Id == reconciliationId && x.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Budget reconciliation row not found.");
                if (item.Status != PublicSectorBudgetReconciliationStatus.PendingApproval || !item.ApprovalWorkflowId.HasValue)
                    throw new InvalidOperationException("Only a pending reconciliation row with an approval workflow can be approved.");
                var workflow = await _db.WorkflowRequests.Include(x => x.Approvals).FirstOrDefaultAsync(x =>
                    x.Id == item.ApprovalWorkflowId.Value && x.CompanyId == companyId &&
                    x.RequestType == "PublicSectorBudgetReconciliation" && x.ReferenceId == item.Id &&
                    x.Status == WorkflowStatus.Approved && x.Approvals.Any(a => a.Approved && a.ApproverId == actorId), token)
                    ?? throw new InvalidOperationException("The independent reconciliation approval workflow has not been approved.");
                if (workflow.SubmittedBy == actorId)
                    throw new InvalidOperationException("The reconciliation preparer cannot approve their own reconciliation.");
                item.Status = PublicSectorBudgetReconciliationStatus.Approved;
                item.ApprovedBy = actorId;
                item.ApprovedAt = DateTime.UtcNow;
                _audit.Record(companyId, actorId, "PublicSectorBudgetReconciliation.Approved", "PublicSector",
                    nameof(PublicSectorBudgetReconciliation), item.Id,
                    new { Status = PublicSectorBudgetReconciliationStatus.PendingApproval },
                    new { item.Status, item.ApprovedBy, item.ApprovedAt, item.ApprovalWorkflowId,
                        item.Type, item.OperatingAmount, item.InvestingAmount, item.FinancingAmount });
                await _db.SaveChangesAsync(token);
                return item;
            }, ct);
        }

        private async Task<(int Bank, int Ar, int Ap)> GetCashPeriodSourceCountsAsync(
            Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var bank = await _db.BankTransactions.CountAsync(x => x.CompanyId == companyId &&
                x.TransactionDate >= periodStart && x.TransactionDate < periodEnd, ct);
            var ar = await _db.ArReceipts.CountAsync(x => x.CompanyId == companyId &&
                x.ReceiptDate >= periodStart && x.ReceiptDate < periodEnd, ct);
            var ap = await _db.ApPayments.CountAsync(x => x.CompanyId == companyId &&
                x.PaymentDate >= periodStart && x.PaymentDate < periodEnd, ct);
            return (bank, ar, ap);
        }

        public async Task<bool> HasValidCertifiedZeroActivityAttestationAsync(
            Guid companyId, Guid budgetVersionId, CancellationToken ct = default)
        {
            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved &&
                x.AccountingBasis == AccountingBasis.Cash, ct);
            if (version == null) return false;

            var attestation = await _db.PublicSectorCashPeriodAttestations.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.BudgetVersionId == version.Id &&
                x.PeriodStart == version.PeriodStart && x.PeriodEnd == version.PeriodEnd &&
                x.Status == "Certified" && x.CertifiedBy != null && x.CertifiedAt != null, ct);
            if (attestation == null || attestation.ObservedBankTransactionCount != 0 ||
                attestation.ObservedArReceiptCount != 0 || attestation.ObservedApPaymentCount != 0)
                return false;

            var counts = await GetCashPeriodSourceCountsAsync(companyId, version.PeriodStart, version.PeriodEnd, ct);
            return counts.Bank == 0 && counts.Ar == 0 && counts.Ap == 0;
        }

        public async Task<PublicSectorCashPeriodAttestation> CreateCashPeriodAttestationAsync(
            Guid companyId, Guid budgetVersionId, string evidenceReference, Guid actorId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(evidenceReference))
                throw new InvalidOperationException("A supporting evidence reference is required for a zero-activity declaration.");
            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved &&
                x.AccountingBasis == AccountingBasis.Cash, ct)
                ?? throw new InvalidOperationException("An approved cash-basis budget version is required.");
            var latestApprovedVersionId = await _db.PublicSectorBudgetVersions.Where(x =>
                    x.CompanyId == companyId && x.FinancialYear == version.FinancialYear &&
                    x.Status == PublicSectorBudgetVersionStatus.Approved && x.AccountingBasis == AccountingBasis.Cash)
                .OrderByDescending(x => x.Kind).ThenByDescending(x => x.VersionNumber)
                .Select(x => x.Id).FirstAsync(ct);
            if (latestApprovedVersionId != version.Id)
                throw new InvalidOperationException("The zero-activity attestation must reference the latest approved cash budget version.");
            var counts = await GetCashPeriodSourceCountsAsync(companyId, version.PeriodStart, version.PeriodEnd, ct);
            if (counts.Bank + counts.Ar + counts.Ap != 0)
                throw new InvalidOperationException($"Zero-activity attestation is not permitted: source records exist (bank {counts.Bank}, AR receipts {counts.Ar}, AP payments {counts.Ap}). Capture and certify the actual source events instead.");
            if (await _db.PublicSectorCashPeriodAttestations.AnyAsync(x =>
                x.CompanyId == companyId && x.BudgetVersionId == version.Id &&
                x.PeriodStart == version.PeriodStart && x.PeriodEnd == version.PeriodEnd, ct))
                throw new InvalidOperationException("A period attestation already exists for this approved budget version.");
            var item = new PublicSectorCashPeriodAttestation
            {
                CompanyId = companyId, BudgetVersionId = version.Id,
                PeriodStart = version.PeriodStart, PeriodEnd = version.PeriodEnd,
                ObservedBankTransactionCount = counts.Bank, ObservedArReceiptCount = counts.Ar,
                ObservedApPaymentCount = counts.Ap, EvidenceReference = evidenceReference.Trim(),
                Status = "Draft", CreatedBy = actorId
            };
            _db.PublicSectorCashPeriodAttestations.Add(item);
            _audit.Record(companyId, actorId, "PublicSectorCashPeriodAttestation.Created", "PublicSector",
                nameof(PublicSectorCashPeriodAttestation), item.Id, null,
                new { item.BudgetVersionId, item.PeriodStart, item.PeriodEnd, counts.Bank, counts.Ar, counts.Ap, item.EvidenceReference });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorCashPeriodAttestation> SubmitCashPeriodAttestationAsync(
            Guid companyId, Guid attestationId, Guid actorId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorCashPeriodAttestations.FirstOrDefaultAsync(x =>
                x.Id == attestationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash period attestation not found.");
            if (item.Status != "Draft" || item.CreatedBy != actorId)
                throw new InvalidOperationException("Only the preparer may submit a draft cash period attestation.");
            var counts = await GetCashPeriodSourceCountsAsync(companyId, item.PeriodStart, item.PeriodEnd, ct);
            if (counts.Bank + counts.Ar + counts.Ap != 0)
                throw new InvalidOperationException("Source activity has appeared since preparation; the zero-activity attestation cannot be submitted.");
            item.ObservedBankTransactionCount = counts.Bank;
            item.ObservedArReceiptCount = counts.Ar;
            item.ObservedApPaymentCount = counts.Ap;
            item.SubmittedBy = actorId;
            item.SubmittedAt = DateTime.UtcNow;
            item.Status = "PendingCertification";
            _audit.Record(companyId, actorId, "PublicSectorCashPeriodAttestation.Submitted", "PublicSector",
                nameof(PublicSectorCashPeriodAttestation), item.Id, new { Status = "Draft" },
                new { item.Status, item.SubmittedBy, item.SubmittedAt });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorCashPeriodAttestation> CertifyCashPeriodAttestationAsync(
            Guid companyId, Guid attestationId, Guid certifierId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorCashPeriodAttestations.FirstOrDefaultAsync(x =>
                x.Id == attestationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash period attestation not found.");
            if (item.Status != "PendingCertification")
                throw new InvalidOperationException("Only a pending cash period attestation can be certified.");
            if (item.CreatedBy == certifierId || item.SubmittedBy == certifierId)
                throw new InvalidOperationException("The preparer or submitter cannot certify their own zero-activity attestation.");
            var counts = await GetCashPeriodSourceCountsAsync(companyId, item.PeriodStart, item.PeriodEnd, ct);
            if (counts.Bank + counts.Ar + counts.Ap != 0)
            {
                item.Status = "Rejected";
                item.RejectionReason = $"Source activity detected before certification (bank {counts.Bank}, AR receipts {counts.Ar}, AP payments {counts.Ap}).";
                await _db.SaveChangesAsync(ct);
                throw new InvalidOperationException("Zero-activity certification rejected because source records now exist. Capture and certify the actual transactions.");
            }
            if (item.ObservedBankTransactionCount != 0 || item.ObservedArReceiptCount != 0 ||
                item.ObservedApPaymentCount != 0)
                throw new InvalidOperationException("The recorded source-count evidence is inconsistent with a zero-activity declaration.");
            item.Status = "Certified";
            item.CertifiedBy = certifierId;
            item.CertifiedAt = DateTime.UtcNow;
            _audit.Record(companyId, certifierId, "PublicSectorCashPeriodAttestation.Certified", "PublicSector",
                nameof(PublicSectorCashPeriodAttestation), item.Id,
                new { Status = "PendingCertification" },
                new { item.Status, item.CertifiedBy, item.CertifiedAt, item.ObservedBankTransactionCount,
                    item.ObservedArReceiptCount, item.ObservedApPaymentCount, item.EvidenceReference });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        private static readonly HashSet<string> ExpectedCashSourceKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            "BankAccount", "ReceivablesControl", "PayablesControl"
        };

        private sealed record ExpectedCashSourceRecord(
            decimal Amount, string Reference, DateTime EffectiveDate, string Currency,
            Guid? JournalEntryId, bool IsReconciled);

        private sealed record ExpectedCashSourcePopulation(
            HashSet<Guid> RecordIds, Dictionary<Guid, ExpectedCashSourceRecord> Records,
            decimal ControlTotal, bool CurrencyMatches, decimal? NetMovement);

        // Re-read the live source population rather than trusting a previously submitted count.
        // SourceIdentifier is the internal BankAccount ID or the AR/AP control GL account ID.
        private async Task<ExpectedCashSourcePopulation> GetExpectedCashSourcePopulationAsync(
            Guid companyId, PublicSectorExpectedCashSource expected, DateTime periodStart,
            DateTime periodEnd, string currency, CancellationToken ct)
        {
            if (!Guid.TryParse(expected.SourceIdentifier, out var sourceId))
                throw new InvalidOperationException($"Expected source {expected.SourceCode} has no valid internal source identifier.");

            if (expected.SourceKind == "BankAccount")
            {
                var account = await _db.BankAccounts.FirstOrDefaultAsync(x =>
                    x.Id == sourceId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException($"Registered bank account {expected.SourceCode} no longer exists in this company.");
                var rows = await _db.BankTransactions.Where(x =>
                    x.CompanyId == companyId && x.BankAccountId == sourceId &&
                    x.TransactionDate >= periodStart && x.TransactionDate < periodEnd)
                    .Select(x => new { x.Id, x.Debit, x.Credit, x.Reference, x.TransactionDate, x.JournalEntryId, x.IsReconciled })
                    .ToListAsync(ct);
                if (rows.Any(x => x.Debit < 0m || x.Credit < 0m ||
                    (x.Debit > 0m && x.Credit > 0m) || Math.Max(x.Debit, x.Credit) <= 0m))
                    throw new InvalidOperationException($"Bank source {expected.SourceCode} contains a transaction with invalid debit/credit amounts.");
                var records = rows.ToDictionary(x => x.Id, x => new ExpectedCashSourceRecord(
                    Math.Max(x.Debit, x.Credit),
                    string.IsNullOrWhiteSpace(x.Reference) ? x.Id.ToString("N") : x.Reference!,
                    x.TransactionDate, account.Currency, x.JournalEntryId, x.IsReconciled));
                return new ExpectedCashSourcePopulation(records.Keys.ToHashSet(), records,
                    rows.Sum(x => Math.Max(x.Debit, x.Credit)),
                    string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase),
                    rows.Sum(x => x.Credit - x.Debit));
            }

            if (expected.SourceKind == "ReceivablesControl")
            {
                var controlCurrency = await _db.GlAccounts.Where(x => x.Id == sourceId &&
                    x.CompanyId == companyId && x.Type == GlAccountType.Asset)
                    .Select(x => x.Currency).SingleOrDefaultAsync(ct);
                if (string.IsNullOrWhiteSpace(controlCurrency))
                    throw new InvalidOperationException($"Receivables control account for {expected.SourceCode} is not a valid asset account in this company.");
                var rows = await _db.ArReceipts.Where(x => x.CompanyId == companyId &&
                    x.ReceiptDate >= periodStart && x.ReceiptDate < periodEnd &&
                    _db.Customers.Any(c => c.Id == x.CustomerId && c.CompanyId == companyId &&
                        c.ArAccountId == sourceId))
                    .Select(x => new { x.Id, x.Amount, x.Currency, x.ReceiptNo, x.ReceiptDate, x.JournalEntryId })
                    .ToListAsync(ct);
                var records = rows.ToDictionary(x => x.Id, x => new ExpectedCashSourceRecord(
                    x.Amount, string.IsNullOrWhiteSpace(x.ReceiptNo) ? x.Id.ToString("N") : x.ReceiptNo,
                    x.ReceiptDate, x.Currency, x.JournalEntryId, true));
                return new ExpectedCashSourcePopulation(records.Keys.ToHashSet(), records,
                    rows.Sum(x => x.Amount),
                    string.Equals(controlCurrency, currency, StringComparison.OrdinalIgnoreCase) &&
                    rows.All(x => string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase)), null);
            }

            if (expected.SourceKind == "PayablesControl")
            {
                var controlCurrency = await _db.GlAccounts.Where(x => x.Id == sourceId &&
                    x.CompanyId == companyId && x.Type == GlAccountType.Liability)
                    .Select(x => x.Currency).SingleOrDefaultAsync(ct);
                if (string.IsNullOrWhiteSpace(controlCurrency))
                    throw new InvalidOperationException($"Payables control account for {expected.SourceCode} is not a valid liability account in this company.");
                var rows = await _db.ApPayments.Where(x => x.CompanyId == companyId &&
                    x.PaymentDate >= periodStart && x.PaymentDate < periodEnd &&
                    _db.Suppliers.Any(s => s.Id == x.SupplierId && s.CompanyId == companyId &&
                        s.ApAccountId == sourceId))
                    .Select(x => new { x.Id, x.Amount, x.Currency, x.PaymentNo, x.PaymentDate, x.JournalEntryId })
                    .ToListAsync(ct);
                var records = rows.ToDictionary(x => x.Id, x => new ExpectedCashSourceRecord(
                    x.Amount, string.IsNullOrWhiteSpace(x.PaymentNo) ? x.Id.ToString("N") : x.PaymentNo,
                    x.PaymentDate, x.Currency, x.JournalEntryId, true));
                return new ExpectedCashSourcePopulation(records.Keys.ToHashSet(), records,
                    rows.Sum(x => x.Amount),
                    string.Equals(controlCurrency, currency, StringComparison.OrdinalIgnoreCase) &&
                    rows.All(x => string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase)), null);
            }

            throw new InvalidOperationException($"Source kind {expected.SourceKind} has no configured source adapter.");
        }

        private async Task<bool> IsCurrentCashSourceReconciliationAsync(
            Guid companyId, PublicSectorExpectedCashSource expected,
            PublicSectorCashSourceSubmission submission, ExpectedCashSourcePopulation population,
            CancellationToken ct)
        {
            var lines = await _db.PublicSectorCashSourceReconciliationLines.Where(x =>
                x.CompanyId == companyId && x.CashSourceSubmissionId == submission.Id).ToListAsync(ct);
            var groups = lines.GroupBy(x => x.SourceRecordId).ToList();
            if (groups.Count != submission.RecordCount ||
                !population.RecordIds.SetEquals(groups.Select(x => x.Key)) ||
                groups.Any(group => !population.Records.ContainsKey(group.Key)))
                return false;

            var expectedActualSourceKind = expected.SourceKind switch
            {
                "BankAccount" => PublicSectorBudgetActualSourceKind.BankTransaction,
                "ReceivablesControl" => PublicSectorBudgetActualSourceKind.ArReceipt,
                "PayablesControl" => PublicSectorBudgetActualSourceKind.ApPayment,
                _ => (PublicSectorBudgetActualSourceKind?)null
            };
            if (!expectedActualSourceKind.HasValue) return false;

            foreach (var group in groups)
            {
                var source = population.Records[group.Key];
                if (source.Amount <= 0m || source.EffectiveDate < submission.PeriodStart ||
                    source.EffectiveDate >= submission.PeriodEnd ||
                    !string.Equals(source.Currency, submission.Currency, StringComparison.OrdinalIgnoreCase) ||
                    !source.JournalEntryId.HasValue ||
                    (expected.SourceKind == "BankAccount" && !source.IsReconciled) ||
                    !await _db.JournalEntries.AnyAsync(j => j.Id == source.JournalEntryId.Value &&
                        j.CompanyId == companyId && j.Status == JournalStatus.Posted, ct))
                    return false;

                if (group.Any(line => line.SourceAmount != source.Amount ||
                    !string.Equals(line.SourceReference, source.Reference, StringComparison.Ordinal)))
                    return false;

                if (group.Any(line => line.IsExcluded))
                {
                    if (group.Count() != 1 || group.Any(line => !line.IsExcluded ||
                        line.BudgetExecutionActualId.HasValue || line.AllocatedAmount != 0m ||
                        string.IsNullOrWhiteSpace(line.ExclusionReason)))
                        return false;
                    continue;
                }

                if (group.Any(line => line.IsExcluded || !line.BudgetExecutionActualId.HasValue) ||
                    group.Sum(line => line.AllocatedAmount) != source.Amount)
                    return false;

                foreach (var line in group)
                {
                    var actual = await _db.PublicSectorBudgetExecutionActuals.FirstOrDefaultAsync(x =>
                        x.Id == line.BudgetExecutionActualId!.Value && x.CompanyId == companyId, ct);
                    if (actual == null || actual.Status != PublicSectorBudgetActualStatus.Certified ||
                        actual.BudgetVersionId != submission.BudgetVersionId ||
                        actual.SourceKind != expectedActualSourceKind.Value ||
                        actual.SourceRecordId != group.Key || actual.Amount != line.AllocatedAmount ||
                        actual.Amount <= 0m || actual.SourceDate.Date != source.EffectiveDate.Date ||
                        actual.EffectiveDate.Date != source.EffectiveDate.Date ||
                        !string.Equals(actual.SourceReference, source.Reference, StringComparison.Ordinal) ||
                        !string.Equals(actual.Currency, source.Currency, StringComparison.OrdinalIgnoreCase) ||
                        !await IsCertifiedCashActualSourceValidAsync(companyId, actual, ct))
                        return false;
                }
            }
            return true;
        }

        public async Task<IReadOnlyList<(string SourceKind, Guid SourceRecordId)>> GetReviewedCashSourceExclusionsAsync(
            Guid companyId, Guid budgetVersionId, CancellationToken ct = default)
        {
            var latestSubmissions = (await _db.PublicSectorCashSourceSubmissions.Where(x =>
                x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId).ToListAsync(ct))
                .GroupBy(x => x.ExpectedCashSourceId)
                .Select(group => group.OrderByDescending(x => x.SubmissionRevision).First())
                .Where(x => x.Status == "Reviewed" && x.RecordLevelStatus == "Reviewed")
                .Select(x => x.Id).ToArray();
            if (latestSubmissions.Length == 0)
                return Array.Empty<(string SourceKind, Guid SourceRecordId)>();

            var rows = await (
                from line in _db.PublicSectorCashSourceReconciliationLines
                join submission in _db.PublicSectorCashSourceSubmissions
                    on line.CashSourceSubmissionId equals submission.Id
                join expected in _db.PublicSectorExpectedCashSources
                    on submission.ExpectedCashSourceId equals expected.Id
                where line.CompanyId == companyId && submission.CompanyId == companyId &&
                    expected.CompanyId == companyId && latestSubmissions.Contains(submission.Id) &&
                    line.IsExcluded
                select new { expected.SourceKind, line.SourceRecordId }
            ).ToListAsync(ct);
            return rows.Select(x => (x.SourceKind, x.SourceRecordId)).Distinct().ToList();
        }

        public async Task<bool> HasCompleteCashSourceRegisterAsync(
            Guid companyId, Guid budgetVersionId, CancellationToken ct = default)
        {
            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved &&
                x.AccountingBasis == AccountingBasis.Cash, ct);
            if (version == null) return false;

            var sources = await _db.PublicSectorExpectedCashSources.Where(x =>
                x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId).ToListAsync(ct);
            if (sources.Count == 0 || sources.Any(x => x.Status != "Approved" ||
                !x.ApprovedBy.HasValue || !x.ApprovedAt.HasValue)) return false;

            var submissions = (await _db.PublicSectorCashSourceSubmissions.Where(x =>
                x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId).ToListAsync(ct))
                .GroupBy(x => x.ExpectedCashSourceId)
                .Select(group => group.OrderByDescending(x => x.SubmissionRevision).First()).ToList();
            if (submissions.Count != sources.Count || submissions.Any(x =>
                x.Status != "Reviewed" || !x.ReviewedBy.HasValue || !x.ReviewedAt.HasValue ||
                x.RecordLevelStatus != "Reviewed" || !x.RecordLevelReviewedBy.HasValue ||
                !x.RecordLevelReviewedAt.HasValue || !x.RecordLevelSubmittedBy.HasValue ||
                !x.RecordLevelSubmittedAt.HasValue)) return false;

            var attestation = await _db.PublicSectorExpectedCashSourceRegisterAttestations
                .Where(x => x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (attestation == null || attestation.Status != "Certified" ||
                !attestation.CertifiedBy.HasValue || !attestation.CertifiedAt.HasValue ||
                !attestation.SubmittedBy.HasValue || !attestation.SubmittedAt.HasValue ||
                attestation.ExpectedSourceCount != sources.Count ||
                attestation.EntityScope != version.EntityScope) return false;

            foreach (var source in sources)
            {
                var submission = submissions.SingleOrDefault(x => x.ExpectedCashSourceId == source.Id);
                if (submission == null) return false;
                try
                {
                    var population = await GetExpectedCashSourcePopulationAsync(companyId, source,
                        submission.PeriodStart, submission.PeriodEnd, submission.Currency, ct);
                    if (!population.CurrencyMatches || population.RecordIds.Count != submission.RecordCount ||
                        population.ControlTotal != submission.ControlTotal ||
                        (population.NetMovement.HasValue &&
                            submission.OpeningBalance + population.NetMovement.Value != submission.ClosingBalance) ||
                        !await IsCurrentCashSourceReconciliationAsync(companyId, source, submission, population, ct))
                        return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
            return true;
        }

        public async Task<IReadOnlyList<PublicSectorExpectedCashSource>> GetExpectedCashSourcesAsync(
            Guid companyId, Guid budgetVersionId, CancellationToken ct = default)
        {
            return await _db.PublicSectorExpectedCashSources
                .Where(x => x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId)
                .OrderBy(x => x.SourceKind).ThenBy(x => x.SourceCode).ToListAsync(ct);
        }

        public async Task<PublicSectorExpectedCashSource> CreateExpectedCashSourceAsync(
            Guid companyId, Guid budgetVersionId, string sourceKind, string sourceCode,
            string sourceName, string? sourceIdentifier, bool isRequired,
            DateTime effectiveFrom, DateTime effectiveTo, Guid actorId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceKind) || !ExpectedCashSourceKinds.Contains(sourceKind.Trim()) ||
                string.IsNullOrWhiteSpace(sourceCode) || string.IsNullOrWhiteSpace(sourceName) ||
                effectiveFrom >= effectiveTo)
                throw new InvalidOperationException("A supported source kind, source code, source name and valid effective period are required.");

            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved &&
                x.AccountingBasis == AccountingBasis.Cash, ct)
                ?? throw new InvalidOperationException("An approved cash-basis budget version is required for the expected-source register.");
            if (effectiveFrom < version.PeriodStart || effectiveTo > version.PeriodEnd)
                throw new InvalidOperationException("The expected source effective period must be contained within the approved budget reporting period.");

            var kind = ExpectedCashSourceKinds.Single(x => string.Equals(x, sourceKind.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!Guid.TryParse(sourceIdentifier, out _))
                throw new InvalidOperationException("Bank, receivables-control and payables-control sources require an internal BankAccount or control GL account identifier.");
            var code = sourceCode.Trim();
            if (await _db.PublicSectorExpectedCashSources.AnyAsync(x =>
                x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId &&
                x.SourceKind == kind && x.SourceCode == code, ct))
                throw new InvalidOperationException("An expected source with this kind and code is already registered for the budget version.");

            var item = new PublicSectorExpectedCashSource
            {
                CompanyId = companyId, BudgetVersionId = budgetVersionId,
                SourceKind = kind, SourceCode = code, SourceName = sourceName.Trim(),
                SourceIdentifier = string.IsNullOrWhiteSpace(sourceIdentifier) ? null : sourceIdentifier.Trim(),
                IsRequired = isRequired, EffectiveFrom = effectiveFrom, EffectiveTo = effectiveTo,
                Status = "Draft", CreatedBy = actorId
            };
            _db.PublicSectorExpectedCashSources.Add(item);
            _audit.Record(companyId, actorId, "PublicSectorExpectedCashSource.Created", "PublicSector",
                nameof(PublicSectorExpectedCashSource), item.Id, null,
                new { item.BudgetVersionId, item.SourceKind, item.SourceCode, item.SourceName,
                    item.SourceIdentifier, item.IsRequired, item.EffectiveFrom, item.EffectiveTo });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorExpectedCashSource> SubmitExpectedCashSourceAsync(
            Guid companyId, Guid sourceId, Guid actorId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorExpectedCashSources.FirstOrDefaultAsync(x =>
                x.Id == sourceId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Expected cash source not found.");
            if (item.Status != "Draft" || item.CreatedBy != actorId)
                throw new InvalidOperationException("Only the preparer may submit a draft expected cash source.");
            item.Status = "PendingApproval";
            item.SubmittedBy = actorId;
            item.SubmittedAt = DateTime.UtcNow;
            _audit.Record(companyId, actorId, "PublicSectorExpectedCashSource.Submitted", "PublicSector",
                nameof(PublicSectorExpectedCashSource), item.Id, new { Status = "Draft" },
                new { item.Status, item.SubmittedBy, item.SubmittedAt });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorExpectedCashSource> ApproveExpectedCashSourceAsync(
            Guid companyId, Guid sourceId, Guid approverId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorExpectedCashSources.FirstOrDefaultAsync(x =>
                x.Id == sourceId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Expected cash source not found.");
            if (item.Status != "PendingApproval")
                throw new InvalidOperationException("Only a pending expected cash source can be approved.");
            if (item.CreatedBy == approverId || item.SubmittedBy == approverId)
                throw new InvalidOperationException("The preparer or submitter cannot approve their own expected cash source.");
            item.Status = "Approved";
            item.ApprovedBy = approverId;
            item.ApprovedAt = DateTime.UtcNow;
            _audit.Record(companyId, approverId, "PublicSectorExpectedCashSource.Approved", "PublicSector",
                nameof(PublicSectorExpectedCashSource), item.Id, new { Status = "PendingApproval" },
                new { item.Status, item.ApprovedBy, item.ApprovedAt });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<IReadOnlyList<PublicSectorCashSourceSubmission>> GetCashSourceSubmissionsAsync(
            Guid companyId, Guid budgetVersionId, CancellationToken ct = default)
        {
            return await _db.PublicSectorCashSourceSubmissions
                .Where(x => x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId)
                .OrderBy(x => x.PeriodStart).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        }

        public async Task<PublicSectorCashSourceSubmission> CreateCashSourceSubmissionAsync(
            Guid companyId, Guid expectedSourceId, int recordCount, decimal controlTotal,
            decimal openingBalance, decimal closingBalance, string currency,
            string evidenceReference, Guid actorId, CancellationToken ct = default,
            string? revisionReason = null)
        {
            if (recordCount < 0 || string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 ||
                string.IsNullOrWhiteSpace(evidenceReference))
                throw new InvalidOperationException("A non-negative record count, three-letter currency and evidence reference are required.");

            var source = await _db.PublicSectorExpectedCashSources.FirstOrDefaultAsync(x =>
                x.Id == expectedSourceId && x.CompanyId == companyId && x.Status == "Approved", ct)
                ?? throw new InvalidOperationException("An approved expected cash source is required before submitting source controls.");
            var previousSubmission = await _db.PublicSectorCashSourceSubmissions
                .Where(x => x.CompanyId == companyId && x.ExpectedCashSourceId == source.Id)
                .OrderByDescending(x => x.SubmissionRevision).FirstOrDefaultAsync(ct);
            if (previousSubmission != null)
            {
                var previousCanBeSuperseded = previousSubmission.Status == "Rejected" ||
                    previousSubmission.RecordLevelStatus == "Rejected" ||
                    (previousSubmission.Status == "Reviewed" &&
                        previousSubmission.RecordLevelStatus != "PendingReview");
                if (!previousCanBeSuperseded)
                    throw new InvalidOperationException("A source submission is still in draft or review. Complete or reject that review before preparing a new revision.");
                if (string.IsNullOrWhiteSpace(revisionReason))
                    throw new InvalidOperationException("A documented reason is required for every replacement source-submission revision.");
            }

            if (controlTotal < 0m)
                throw new InvalidOperationException("A source activity control total cannot be negative.");
            var normalisedCurrency = currency.Trim().ToUpperInvariant();
            var population = await GetExpectedCashSourcePopulationAsync(companyId, source,
                source.EffectiveFrom, source.EffectiveTo, normalisedCurrency, ct);
            if (population.RecordIds.Count != recordCount || population.ControlTotal != controlTotal ||
                !population.CurrencyMatches ||
                (population.NetMovement.HasValue && openingBalance + population.NetMovement.Value != closingBalance))
                throw new InvalidOperationException("The submitted record count, control total, currency or opening-to-closing balance movement does not agree with the live registered source population.");

            var item = new PublicSectorCashSourceSubmission
            {
                CompanyId = companyId, ExpectedCashSourceId = source.Id, BudgetVersionId = source.BudgetVersionId,
                SubmissionRevision = (previousSubmission?.SubmissionRevision ?? 0) + 1,
                PeriodStart = source.EffectiveFrom, PeriodEnd = source.EffectiveTo, RecordCount = recordCount,
                ControlTotal = controlTotal, OpeningBalance = openingBalance, ClosingBalance = closingBalance,
                Currency = normalisedCurrency, EvidenceReference = evidenceReference.Trim(),
                Status = "Draft", CreatedBy = actorId
            };
            _db.PublicSectorCashSourceSubmissions.Add(item);
            _audit.Record(companyId, actorId, "PublicSectorCashSourceSubmission.Created", "PublicSector",
                nameof(PublicSectorCashSourceSubmission), item.Id,
                previousSubmission == null ? null : new
                {
                    PreviousSubmissionId = previousSubmission.Id,
                    previousSubmission.SubmissionRevision,
                    previousSubmission.Status,
                    previousSubmission.RecordLevelStatus
                },
                new { item.ExpectedCashSourceId, item.BudgetVersionId, item.SubmissionRevision, item.PeriodStart, item.PeriodEnd,
                    item.RecordCount, item.ControlTotal, item.OpeningBalance, item.ClosingBalance,
                    item.Currency, item.EvidenceReference,
                    RevisionReason = string.IsNullOrWhiteSpace(revisionReason) ? null : revisionReason.Trim() });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorCashSourceSubmission> SubmitCashSourceSubmissionAsync(
            Guid companyId, Guid submissionId, Guid actorId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorCashSourceSubmissions.FirstOrDefaultAsync(x =>
                x.Id == submissionId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash source submission not found.");
            if (item.Status != "Draft" || item.CreatedBy != actorId)
                throw new InvalidOperationException("Only the preparer may submit a draft cash source submission.");
            item.Status = "PendingReview";
            item.SubmittedBy = actorId;
            item.SubmittedAt = DateTime.UtcNow;
            _audit.Record(companyId, actorId, "PublicSectorCashSourceSubmission.Submitted", "PublicSector",
                nameof(PublicSectorCashSourceSubmission), item.Id, new { Status = "Draft" },
                new { item.Status, item.SubmittedBy, item.SubmittedAt });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorCashSourceSubmission> ReviewCashSourceSubmissionAsync(
            Guid companyId, Guid submissionId, Guid reviewerId, bool accepted,
            string? reviewNote, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorCashSourceSubmissions.FirstOrDefaultAsync(x =>
                x.Id == submissionId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash source submission not found.");
            if (item.Status != "PendingReview")
                throw new InvalidOperationException("Only a pending cash source submission can be reviewed.");
            if (item.CreatedBy == reviewerId || item.SubmittedBy == reviewerId)
                throw new InvalidOperationException("The preparer or submitter cannot independently review their own cash source submission.");
            if (!accepted && string.IsNullOrWhiteSpace(reviewNote))
                throw new InvalidOperationException("A reason is required when rejecting a cash source submission.");
            if (accepted)
            {
                var expected = await _db.PublicSectorExpectedCashSources.FirstAsync(x =>
                    x.Id == item.ExpectedCashSourceId && x.CompanyId == companyId, ct);
                var population = await GetExpectedCashSourcePopulationAsync(companyId, expected,
                    item.PeriodStart, item.PeriodEnd, item.Currency, ct);
                if (population.RecordIds.Count != item.RecordCount ||
                    population.ControlTotal != item.ControlTotal || !population.CurrencyMatches ||
                    (population.NetMovement.HasValue &&
                        item.OpeningBalance + population.NetMovement.Value != item.ClosingBalance))
                    throw new InvalidOperationException("The live source population no longer agrees with the submitted record count, control total, currency or opening-to-closing balance movement.");
            }

            item.Status = accepted ? "Reviewed" : "Rejected";
            item.ReviewedBy = reviewerId;
            item.ReviewedAt = DateTime.UtcNow;
            item.ReviewNote = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim();
            _audit.Record(companyId, reviewerId, "PublicSectorCashSourceSubmission.Reviewed", "PublicSector",
                nameof(PublicSectorCashSourceSubmission), item.Id, new { Status = "PendingReview" },
                new { item.Status, item.ReviewedBy, item.ReviewedAt, item.ReviewNote });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<IReadOnlyList<PublicSectorCashSourceReconciliationLine>> GetCashSourceReconciliationLinesAsync(
            Guid companyId, Guid submissionId, CancellationToken ct = default)
        {
            return await _db.PublicSectorCashSourceReconciliationLines
                .Where(x => x.CompanyId == companyId && x.CashSourceSubmissionId == submissionId)
                .OrderBy(x => x.SourceRecordId).ThenBy(x => x.BudgetExecutionActualId)
                .ToListAsync(ct);
        }

        public async Task<PublicSectorCashSourceReconciliationLine> AddCashSourceReconciliationLineAsync(
            Guid companyId, Guid submissionId, Guid sourceRecordId, string sourceReference,
            Guid? actualId, bool isExcluded, string? exclusionReason, Guid actorId,
            CancellationToken ct = default)
        {
            var submission = await _db.PublicSectorCashSourceSubmissions.FirstOrDefaultAsync(x =>
                x.Id == submissionId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash source submission not found.");
            if (submission.Status != "Reviewed" || submission.RecordLevelStatus != "NotStarted")
                throw new InvalidOperationException("Transaction mappings can only be added after source controls are reviewed and before record-level reconciliation is submitted.");
            if (sourceRecordId == Guid.Empty || string.IsNullOrWhiteSpace(sourceReference))
                throw new InvalidOperationException("A source record identifier and source reference are required.");

            var expected = await _db.PublicSectorExpectedCashSources.FirstOrDefaultAsync(x =>
                x.Id == submission.ExpectedCashSourceId && x.CompanyId == companyId && x.Status == "Approved", ct)
                ?? throw new InvalidOperationException("The approved expected source for this submission is unavailable.");
            var sourceKind = expected.SourceKind switch
            {
                "BankAccount" => PublicSectorBudgetActualSourceKind.BankTransaction,
                "ReceivablesControl" => PublicSectorBudgetActualSourceKind.ArReceipt,
                "PayablesControl" => PublicSectorBudgetActualSourceKind.ApPayment,
                _ => throw new InvalidOperationException("Cashbook sources require a separately configured adapter and cannot yet be reconciled to these source types.")
            };

            decimal sourceAmount;
            DateTime sourceDate;
            string sourceCurrency;
            string expectedSourceReference;
            Guid? sourceJournalEntryId;
            if (sourceKind == PublicSectorBudgetActualSourceKind.BankTransaction)
            {
                var source = await _db.BankTransactions.FirstOrDefaultAsync(x =>
                    x.Id == sourceRecordId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The bank transaction does not exist in this company.");
                sourceAmount = Math.Max(source.Debit, source.Credit);
                sourceDate = source.TransactionDate;
                expectedSourceReference = string.IsNullOrWhiteSpace(source.Reference)
                    ? source.Id.ToString("N") : source.Reference!;
                sourceJournalEntryId = source.JournalEntryId;
                sourceCurrency = await _db.BankAccounts.Where(x => x.Id == source.BankAccountId && x.CompanyId == companyId)
                    .Select(x => x.Currency).SingleOrDefaultAsync(ct)
                    ?? throw new InvalidOperationException("The bank account currency is unavailable.");
                if (source.Debit > 0m && source.Credit > 0m || sourceAmount <= 0m)
                    throw new InvalidOperationException("The bank transaction must contain one positive debit or credit amount.");
                if (!Guid.TryParse(expected.SourceIdentifier, out var bankAccountId) || source.BankAccountId != bankAccountId)
                    throw new InvalidOperationException("The bank transaction does not belong to the registered expected bank account.");
            }
            else if (sourceKind == PublicSectorBudgetActualSourceKind.ArReceipt)
            {
                var source = await _db.ArReceipts.FirstOrDefaultAsync(x =>
                    x.Id == sourceRecordId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The AR receipt does not exist in this company.");
                if (!Guid.TryParse(expected.SourceIdentifier, out var receivablesControlId) ||
                    !await _db.Customers.AnyAsync(x => x.Id == source.CustomerId && x.CompanyId == companyId &&
                        x.ArAccountId == receivablesControlId, ct))
                    throw new InvalidOperationException("The AR receipt does not belong to the registered receivables control account.");
                sourceAmount = source.Amount;
                sourceDate = source.ReceiptDate;
                expectedSourceReference = string.IsNullOrWhiteSpace(source.ReceiptNo)
                    ? source.Id.ToString("N") : source.ReceiptNo;
                sourceJournalEntryId = source.JournalEntryId;
                sourceCurrency = source.Currency;
            }
            else
            {
                var source = await _db.ApPayments.FirstOrDefaultAsync(x =>
                    x.Id == sourceRecordId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The AP payment does not exist in this company.");
                if (!Guid.TryParse(expected.SourceIdentifier, out var payablesControlId) ||
                    !await _db.Suppliers.AnyAsync(x => x.Id == source.SupplierId && x.CompanyId == companyId &&
                        x.ApAccountId == payablesControlId, ct))
                    throw new InvalidOperationException("The AP payment does not belong to the registered payables control account.");
                sourceAmount = source.Amount;
                sourceDate = source.PaymentDate;
                expectedSourceReference = string.IsNullOrWhiteSpace(source.PaymentNo)
                    ? source.Id.ToString("N") : source.PaymentNo;
                sourceJournalEntryId = source.JournalEntryId;
                sourceCurrency = source.Currency;
            }

            if (!string.Equals(sourceReference.Trim(), expectedSourceReference, StringComparison.Ordinal))
                throw new InvalidOperationException("The supplied source reference does not match the company-owned source record.");
            if (sourceDate < submission.PeriodStart || sourceDate >= submission.PeriodEnd)
                throw new InvalidOperationException("The source record falls outside the submitted reporting period.");
            if (!sourceJournalEntryId.HasValue || !await _db.JournalEntries.AnyAsync(j =>
                j.Id == sourceJournalEntryId.Value && j.CompanyId == companyId &&
                j.Status == JournalStatus.Posted, ct))
                throw new InvalidOperationException("Only source records linked to a posted settlement journal can be allocated or excluded from the cash-basis budget population.");
            if (!string.Equals(sourceCurrency, submission.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source record currency must match the reviewed source submission currency; no implicit FX conversion is permitted.");
            if (isExcluded)
            {
                if (actualId.HasValue || string.IsNullOrWhiteSpace(exclusionReason))
                    throw new InvalidOperationException("An exclusion must not reference a budget actual and must include a reason.");
                if (await _db.PublicSectorCashSourceReconciliationLines.AnyAsync(x =>
                    x.CompanyId == companyId && x.CashSourceSubmissionId == submissionId &&
                    x.SourceRecordId == sourceRecordId, ct))
                    throw new InvalidOperationException("A source record already has an allocation or exclusion.");
            }
            else
            {
                if (!actualId.HasValue || !string.IsNullOrWhiteSpace(exclusionReason))
                    throw new InvalidOperationException("An allocated source record must reference a certified actual and cannot carry an exclusion reason.");
                var actual = await _db.PublicSectorBudgetExecutionActuals.FirstOrDefaultAsync(x =>
                    x.Id == actualId.Value && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The budget execution actual does not exist in this company.");
                if (actual.Status != PublicSectorBudgetActualStatus.Certified ||
                    actual.BudgetVersionId != submission.BudgetVersionId ||
                    actual.SourceKind != sourceKind || actual.SourceRecordId != sourceRecordId ||
                    actual.EffectiveDate < submission.PeriodStart || actual.EffectiveDate >= submission.PeriodEnd ||
                    actual.Currency != submission.Currency || actual.Amount <= 0m ||
                    !string.Equals(actual.SourceReference, sourceReference.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("The actual must be certified, belong to this budget version and source record, and match the source period and currency.");
                if (await _db.PublicSectorCashSourceReconciliationLines.AnyAsync(line =>
                    line.CompanyId == companyId && line.BudgetExecutionActualId == actual.Id &&
                    _db.PublicSectorCashSourceSubmissions.Any(submission =>
                        submission.Id == line.CashSourceSubmissionId && submission.CompanyId == companyId &&
                        submission.Status != "Rejected" && submission.RecordLevelStatus != "Rejected" &&
                        submission.SubmissionRevision == _db.PublicSectorCashSourceSubmissions
                            .Where(latest => latest.CompanyId == companyId &&
                                latest.ExpectedCashSourceId == submission.ExpectedCashSourceId)
                            .Max(latest => latest.SubmissionRevision)), ct))
                    throw new InvalidOperationException("A certified budget actual cannot be reconciled more than once in the latest active source revision.");
                var existing = await _db.PublicSectorCashSourceReconciliationLines.Where(x =>
                    x.CompanyId == companyId && x.CashSourceSubmissionId == submissionId &&
                    x.SourceRecordId == sourceRecordId).ToListAsync(ct);
                if (existing.Any(x => x.IsExcluded) ||
                    existing.Any(x => x.SourceAmount != sourceAmount))
                    throw new InvalidOperationException("A source record cannot mix exclusions with allocations, and its source amount must be consistent across split allocations.");
                if (existing.Any(x => x.BudgetExecutionActualId == actual.Id))
                    throw new InvalidOperationException("This actual is already allocated to the source record.");
            }

            var line = new PublicSectorCashSourceReconciliationLine
            {
                CompanyId = companyId, CashSourceSubmissionId = submissionId,
                SourceRecordId = sourceRecordId, SourceReference = sourceReference.Trim(),
                SourceAmount = sourceAmount, BudgetExecutionActualId = actualId,
                AllocatedAmount = actualId.HasValue
                    ? await _db.PublicSectorBudgetExecutionActuals.Where(x => x.Id == actualId.Value && x.CompanyId == companyId)
                        .Select(x => x.Amount).SingleAsync(ct)
                    : 0m,
                IsExcluded = isExcluded,
                ExclusionReason = isExcluded ? exclusionReason!.Trim() : null,
                CreatedBy = actorId
            };
            _db.PublicSectorCashSourceReconciliationLines.Add(line);
            _audit.Record(companyId, actorId, "PublicSectorCashSourceReconciliationLine.Created", "PublicSector",
                nameof(PublicSectorCashSourceReconciliationLine), line.Id, null,
                new { line.CashSourceSubmissionId, line.SourceRecordId, line.SourceReference,
                    line.SourceAmount, line.BudgetExecutionActualId, line.AllocatedAmount,
                    line.IsExcluded, line.ExclusionReason });
            await _db.SaveChangesAsync(ct);
            return line;
        }

        public async Task<PublicSectorCashSourceSubmission> SubmitCashSourceRecordReconciliationAsync(
            Guid companyId, Guid submissionId, Guid actorId, CancellationToken ct = default)
        {
            var submission = await _db.PublicSectorCashSourceSubmissions.FirstOrDefaultAsync(x =>
                x.Id == submissionId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash source submission not found.");
            if (submission.Status != "Reviewed" || submission.RecordLevelStatus != "NotStarted" ||
                submission.ReviewedBy == actorId || submission.CreatedBy == actorId)
                throw new InvalidOperationException("Only an independent record-reconciliation preparer may submit a reviewed source population.");
            var lines = await _db.PublicSectorCashSourceReconciliationLines.Where(x =>
                x.CompanyId == companyId && x.CashSourceSubmissionId == submissionId).ToListAsync(ct);
            var groups = lines.GroupBy(x => x.SourceRecordId).ToList();
            var expected = await _db.PublicSectorExpectedCashSources.FirstAsync(x =>
                x.Id == submission.ExpectedCashSourceId && x.CompanyId == companyId, ct);
            var livePopulation = await GetExpectedCashSourcePopulationAsync(companyId, expected,
                submission.PeriodStart, submission.PeriodEnd, submission.Currency, ct);
            if (!livePopulation.CurrencyMatches || livePopulation.RecordIds.Count != submission.RecordCount ||
                livePopulation.ControlTotal != submission.ControlTotal ||
                (livePopulation.NetMovement.HasValue &&
                    submission.OpeningBalance + livePopulation.NetMovement.Value != submission.ClosingBalance) ||
                !livePopulation.RecordIds.SetEquals(groups.Select(x => x.Key)))
                throw new InvalidOperationException("The record-level mapping does not match the current source-system record population and currency.");
            if (groups.Count != submission.RecordCount)
                throw new InvalidOperationException($"Record-level reconciliation has {groups.Count} distinct source records; the submitted control count is {submission.RecordCount}.");
            if (groups.Any(g => g.Select(x => x.SourceAmount).Distinct().Count() != 1 ||
                (g.Any(x => x.IsExcluded) && (g.Count() != 1 || g.Any(x => !x.IsExcluded))) ||
                (!g.Any(x => x.IsExcluded) && g.Sum(x => x.AllocatedAmount) != g.First().SourceAmount)))
                throw new InvalidOperationException("Each source record must be fully allocated across certified actuals or recorded once as an evidenced exclusion; partial and mixed mappings are not permitted.");
            if (groups.Sum(g => g.First().SourceAmount) != submission.ControlTotal)
                throw new InvalidOperationException("The distinct source-record amounts do not agree exactly with the submitted source control total.");
            if (lines.Any(x => x.IsExcluded && string.IsNullOrWhiteSpace(x.ExclusionReason)))
                throw new InvalidOperationException("Every excluded source record requires a reason.");
            submission.RecordLevelStatus = "PendingReview";
            submission.RecordLevelSubmittedBy = actorId;
            submission.RecordLevelSubmittedAt = DateTime.UtcNow;
            _audit.Record(companyId, actorId, "PublicSectorCashSourceSubmission.RecordReconciliationSubmitted", "PublicSector",
                nameof(PublicSectorCashSourceSubmission), submission.Id, new { RecordLevelStatus = "NotStarted" },
                new { submission.RecordLevelStatus, submission.RecordLevelSubmittedBy, submission.RecordLevelSubmittedAt,
                    DistinctSourceRecordCount = groups.Count, ReconciledControlTotal = groups.Sum(g => g.First().SourceAmount) });
            await _db.SaveChangesAsync(ct);
            return submission;
        }

        public async Task<PublicSectorCashSourceSubmission> ReviewCashSourceRecordReconciliationAsync(
            Guid companyId, Guid submissionId, Guid reviewerId, bool accepted,
            string? reviewNote, CancellationToken ct = default)
        {
            var submission = await _db.PublicSectorCashSourceSubmissions.FirstOrDefaultAsync(x =>
                x.Id == submissionId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Cash source submission not found.");
            if (submission.Status != "Reviewed" || submission.RecordLevelStatus != "PendingReview" ||
                submission.CreatedBy == reviewerId || submission.RecordLevelSubmittedBy == reviewerId ||
                submission.ReviewedBy == reviewerId)
                throw new InvalidOperationException("Record-level reconciliation requires a reviewer independent of the source preparer, source reviewer and reconciliation submitter.");
            if (!accepted && string.IsNullOrWhiteSpace(reviewNote))
                throw new InvalidOperationException("A reason is required when rejecting record-level reconciliation.");
            if (accepted)
            {
                var expected = await _db.PublicSectorExpectedCashSources.FirstAsync(x =>
                    x.Id == submission.ExpectedCashSourceId && x.CompanyId == companyId, ct);
                var population = await GetExpectedCashSourcePopulationAsync(companyId, expected,
                    submission.PeriodStart, submission.PeriodEnd, submission.Currency, ct);
                var lines = await _db.PublicSectorCashSourceReconciliationLines.Where(x =>
                    x.CompanyId == companyId && x.CashSourceSubmissionId == submissionId).ToListAsync(ct);
                if (!population.CurrencyMatches || population.RecordIds.Count != submission.RecordCount ||
                    population.ControlTotal != submission.ControlTotal ||
                    (population.NetMovement.HasValue &&
                        submission.OpeningBalance + population.NetMovement.Value != submission.ClosingBalance) ||
                    !population.RecordIds.SetEquals(lines.Select(x => x.SourceRecordId).Distinct()))
                    throw new InvalidOperationException("The live source population changed after submission; record-level reconciliation cannot be approved.");
            }
            submission.RecordLevelStatus = accepted ? "Reviewed" : "Rejected";
            submission.RecordLevelReviewedBy = reviewerId;
            submission.RecordLevelReviewedAt = DateTime.UtcNow;
            submission.RecordLevelReviewNote = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim();
            _audit.Record(companyId, reviewerId, "PublicSectorCashSourceSubmission.RecordReconciliationReviewed", "PublicSector",
                nameof(PublicSectorCashSourceSubmission), submission.Id, new { RecordLevelStatus = "PendingReview" },
                new { submission.RecordLevelStatus, submission.RecordLevelReviewedBy, submission.RecordLevelReviewedAt,
                    submission.RecordLevelReviewNote });
            await _db.SaveChangesAsync(ct);
            return submission;
        }



        public async Task<PublicSectorExpectedCashSourceRegisterAttestation?> GetExpectedCashSourceRegisterAttestationAsync(
            Guid companyId, Guid budgetVersionId, CancellationToken ct = default)
        {
            return await _db.PublicSectorExpectedCashSourceRegisterAttestations
                .Where(x => x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        }

        public async Task<PublicSectorExpectedCashSourceRegisterAttestation> CreateExpectedCashSourceRegisterAttestationAsync(
            Guid companyId, Guid budgetVersionId, int expectedSourceCount, string entityScope,
            string evidenceReference, Guid actorId, CancellationToken ct = default)
        {
            if (expectedSourceCount <= 0 || string.IsNullOrWhiteSpace(entityScope) ||
                string.IsNullOrWhiteSpace(evidenceReference))
                throw new InvalidOperationException("A positive source count, reporting entity scope and completeness evidence reference are required.");
            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved &&
                x.AccountingBasis == AccountingBasis.Cash, ct)
                ?? throw new InvalidOperationException("An approved cash-basis budget version is required.");
            if (!string.Equals(entityScope.Trim(), version.EntityScope, StringComparison.Ordinal))
                throw new InvalidOperationException("The attested reporting entity scope must exactly match the approved budget version.");
            if (await _db.PublicSectorExpectedCashSourceRegisterAttestations.AnyAsync(x =>
                x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId && x.Status != "Rejected", ct))
                throw new InvalidOperationException("A draft, pending or certified source-register completeness attestation already exists for this budget version. A rejected attestation may be replaced with a new submission.");
            var sources = await _db.PublicSectorExpectedCashSources.Where(x =>
                x.CompanyId == companyId && x.BudgetVersionId == budgetVersionId).ToListAsync(ct);
            if (sources.Count != expectedSourceCount || sources.Count == 0 ||
                sources.Any(x => x.Status != "Approved"))
                throw new InvalidOperationException("The declared source count must match a non-empty register containing only approved expected sources.");
            var item = new PublicSectorExpectedCashSourceRegisterAttestation
            {
                CompanyId = companyId, BudgetVersionId = budgetVersionId,
                ExpectedSourceCount = expectedSourceCount, EntityScope = entityScope.Trim(),
                EvidenceReference = evidenceReference.Trim(), Status = "Draft", CreatedBy = actorId
            };
            _db.PublicSectorExpectedCashSourceRegisterAttestations.Add(item);
            _audit.Record(companyId, actorId, "PublicSectorExpectedCashSourceRegisterAttestation.Created",
                "PublicSector", nameof(PublicSectorExpectedCashSourceRegisterAttestation), item.Id, null,
                new { item.BudgetVersionId, item.ExpectedSourceCount, item.EntityScope, item.EvidenceReference });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorExpectedCashSourceRegisterAttestation> SubmitExpectedCashSourceRegisterAttestationAsync(
            Guid companyId, Guid attestationId, Guid actorId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorExpectedCashSourceRegisterAttestations.FirstOrDefaultAsync(x =>
                x.Id == attestationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Source-register completeness attestation not found.");
            if (item.Status != "Draft" || item.CreatedBy != actorId)
                throw new InvalidOperationException("Only the attestation preparer may submit it for independent certification.");
            var sources = await _db.PublicSectorExpectedCashSources.Where(x =>
                x.CompanyId == companyId && x.BudgetVersionId == item.BudgetVersionId).ToListAsync(ct);
            if (sources.Count != item.ExpectedSourceCount || sources.Any(x => x.Status != "Approved"))
                throw new InvalidOperationException("The approved source register changed or contains unapproved entries; recreate the attestation.");
            item.Status = "PendingCertification";
            item.SubmittedBy = actorId;
            item.SubmittedAt = DateTime.UtcNow;
            _audit.Record(companyId, actorId, "PublicSectorExpectedCashSourceRegisterAttestation.Submitted",
                "PublicSector", nameof(PublicSectorExpectedCashSourceRegisterAttestation), item.Id,
                new { Status = "Draft" }, new { item.Status, item.SubmittedBy, item.SubmittedAt });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorExpectedCashSourceRegisterAttestation> ReviewExpectedCashSourceRegisterAttestationAsync(
            Guid companyId, Guid attestationId, Guid certifierId, bool accepted,
            string? reviewNote, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                var item = await _db.PublicSectorExpectedCashSourceRegisterAttestations.FirstOrDefaultAsync(x =>
                    x.Id == attestationId && x.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Source-register completeness attestation not found.");
                if (item.Status != "PendingCertification")
                    throw new InvalidOperationException("Only a pending source-register completeness attestation can be certified or rejected.");
                if (item.CreatedBy == certifierId || item.SubmittedBy == certifierId)
                    throw new InvalidOperationException("The preparer or submitter cannot certify their own source-register completeness declaration.");
                if (!accepted && string.IsNullOrWhiteSpace(reviewNote))
                    throw new InvalidOperationException("A reason is required when rejecting source-register completeness.");
                var sources = await _db.PublicSectorExpectedCashSources.Where(x =>
                    x.CompanyId == companyId && x.BudgetVersionId == item.BudgetVersionId).ToListAsync(token);
                if (sources.Count != item.ExpectedSourceCount || sources.Count == 0 ||
                    sources.Any(x => x.Status != "Approved"))
                    throw new InvalidOperationException("The expected-source register no longer matches the submitted completeness declaration.");
                var submissions = (await _db.PublicSectorCashSourceSubmissions.Where(x =>
                    x.CompanyId == companyId && x.BudgetVersionId == item.BudgetVersionId).ToListAsync(token))
                    .GroupBy(x => x.ExpectedCashSourceId)
                    .Select(group => group.OrderByDescending(x => x.SubmissionRevision).First()).ToList();
                var everySourceReconciled = sources.All(source => submissions.Any(submission =>
                    submission.ExpectedCashSourceId == source.Id && submission.Status == "Reviewed" &&
                    submission.RecordLevelStatus == "Reviewed"));
                if (accepted && (!everySourceReconciled || submissions.Count != sources.Count ||
                    submissions.Any(x => x.Status != "Reviewed" || x.RecordLevelStatus != "Reviewed")))
                    throw new InvalidOperationException("Every expected source, including zero-activity sources, must have an independently reviewed source submission and record-level reconciliation before register completeness can be certified.");
                if (accepted)
                {
                    foreach (var submission in submissions)
                    {
                        var expected = sources.Single(x => x.Id == submission.ExpectedCashSourceId);
                        var population = await GetExpectedCashSourcePopulationAsync(companyId, expected,
                            submission.PeriodStart, submission.PeriodEnd, submission.Currency, token);
                        var lines = await _db.PublicSectorCashSourceReconciliationLines.Where(x =>
                            x.CompanyId == companyId && x.CashSourceSubmissionId == submission.Id)
                            .Select(x => x.SourceRecordId).Distinct().ToListAsync(token);
                        if (!population.CurrencyMatches || population.RecordIds.Count != submission.RecordCount ||
                            population.ControlTotal != submission.ControlTotal ||
                            (population.NetMovement.HasValue &&
                                submission.OpeningBalance + population.NetMovement.Value != submission.ClosingBalance) ||
                            !population.RecordIds.SetEquals(lines))
                            throw new InvalidOperationException($"The live source population for {expected.SourceCode} changed or does not match its reviewed control totals and record-level reconciliation.");
                    }
                }
                item.Status = accepted ? "Certified" : "Rejected";
                item.CertifiedBy = certifierId;
                item.CertifiedAt = DateTime.UtcNow;
                item.ReviewNote = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim();
                _audit.Record(companyId, certifierId, "PublicSectorExpectedCashSourceRegisterAttestation.Reviewed",
                    "PublicSector", nameof(PublicSectorExpectedCashSourceRegisterAttestation), item.Id,
                    new { Status = "PendingCertification" },
                    new { item.Status, item.CertifiedBy, item.CertifiedAt, item.ReviewNote });
                await _db.SaveChangesAsync(token);
                return item;
            }, ct);
        }

        public async Task<BudgetComparisonReport> GetBudgetComparisonAsync(Guid companyId, int financialYear,
            CancellationToken ct = default)
        {
            if (financialYear < 2000 || financialYear > 2200)
                throw new InvalidOperationException("A valid financial year is required.");
            var original = await _db.PublicSectorBudgetVersions.Include(x => x.Lines)
                .Where(x => x.CompanyId == companyId && x.FinancialYear == financialYear &&
                    x.Kind == PublicSectorBudgetVersionKind.Original && x.Status == PublicSectorBudgetVersionStatus.Approved)
                .OrderBy(x => x.VersionNumber).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("An approved original budget is required before the IPSAS 24 comparison can be produced.");
            var final = await _db.PublicSectorBudgetVersions.Include(x => x.Lines)
                .Where(x => x.CompanyId == companyId && x.FinancialYear == financialYear &&
                    x.Kind == PublicSectorBudgetVersionKind.Revised && x.Status == PublicSectorBudgetVersionStatus.Approved)
                .OrderByDescending(x => x.VersionNumber).FirstOrDefaultAsync(ct);
            // Where no revised budget was approved, the final budget is the original budget.
            // A draft/unapproved revision never displaces the last legally approved budget.
            final ??= original;

            // Company is tenant-filtered by CurrentTenantId. Resolve the selected company explicitly
            // because this service already receives the authenticated CompanyId; PostgreSQL RLS remains active.
            var companyProfile = await _db.Companies.IgnoreQueryFilters().Where(x => x.Id == companyId)
                .Select(x => new { x.Name, x.FunctionalCurrency }).FirstOrDefaultAsync(ct);
            var comparabilityIssues = new List<string>();
            var cashActualsByBudgetLine = new Dictionary<Guid, decimal>();
            var usesZfrmClassification =
                original.ClassificationBasis.Contains("ZFRM", StringComparison.OrdinalIgnoreCase) ||
                original.ClassificationBasis.Contains("Zimbabwe Financial Reporting Manual", StringComparison.OrdinalIgnoreCase);
            if (usesZfrmClassification)
            {
                var allBudgetLines = original.Lines.Concat(final?.Lines ?? Array.Empty<PublicSectorBudgetVersionLine>()).ToList();
                var accountTypes = await _db.GlAccounts.Where(x =>
                    x.CompanyId == companyId && allBudgetLines.Select(line => line.GlAccountId).Contains(x.Id))
                    .Select(x => new { x.Id, x.Type }).ToDictionaryAsync(x => x.Id, x => x.Type, ct);
                if (allBudgetLines.Any(line => string.IsNullOrWhiteSpace(line.TreasuryClassificationCode) ||
                    !accountTypes.TryGetValue(line.GlAccountId, out var accountType) ||
                    (accountType != GlAccountType.Income && accountType != GlAccountType.Expense)))
                    comparabilityIssues.Add("ZFRM Appendix 5 comparability requires a Treasury category on every budget line and income/expense GL accounts only.");
            }
            if (original.AccountingBasis != AccountingBasis.Accrual &&
                original.AccountingBasis != AccountingBasis.Cash)
                comparabilityIssues.Add("This non-accrual budget basis does not yet have an approved source adapter. Accrual GL actuals cannot be substituted.");

            if (original.AccountingBasis == AccountingBasis.Cash)
            {
                // Cash actuals must come from certified source-linked events attached to the
                // latest approved cash budget version. Never substitute accrual journal totals.
                var actualVersion = final ?? original;
                var cashActuals = await _db.PublicSectorBudgetExecutionActuals.Where(x =>
                    x.CompanyId == companyId && x.BudgetVersionId == actualVersion.Id &&
                    x.EffectiveDate >= actualVersion.PeriodStart && x.EffectiveDate < actualVersion.PeriodEnd)
                    .ToListAsync(ct);
                var reviewedExclusions = await GetReviewedCashSourceExclusionsAsync(
                    companyId, actualVersion.Id, ct);
                if (cashActuals.Count == 0)
                {
                    var zeroAttestation = await _db.PublicSectorCashPeriodAttestations.FirstOrDefaultAsync(x =>
                        x.CompanyId == companyId && x.BudgetVersionId == actualVersion.Id &&
                        x.PeriodStart == actualVersion.PeriodStart && x.PeriodEnd == actualVersion.PeriodEnd &&
                        x.Status == "Certified", ct);
                    var liveCounts = await GetCashPeriodSourceCountsAsync(companyId, actualVersion.PeriodStart,
                        actualVersion.PeriodEnd, ct);
                    var liveSourceCount = liveCounts.Bank + liveCounts.Ar + liveCounts.Ap;
                    var excludedBankCount = reviewedExclusions.Count(x => x.SourceKind == "BankAccount");
                    var excludedArCount = reviewedExclusions.Count(x => x.SourceKind == "ReceivablesControl");
                    var excludedApCount = reviewedExclusions.Count(x => x.SourceKind == "PayablesControl");
                    var allActivityExplicitlyExcluded = liveSourceCount > 0 &&
                        liveCounts.Bank == excludedBankCount && liveCounts.Ar == excludedArCount &&
                        liveCounts.Ap == excludedApCount &&
                        await HasCompleteCashSourceRegisterAsync(companyId, actualVersion.Id, ct);
                    if (liveSourceCount == 0 && zeroAttestation == null)
                        comparabilityIssues.Add("No source-backed cash actuals or independently certified zero-activity attestation are available.");
                    else if (liveSourceCount > 0 && !allActivityExplicitlyExcluded)
                        comparabilityIssues.Add("Cash source activity exists, so a zero-activity attestation is no longer valid; no cash actuals were captured and the complete source population is not covered by independently reviewed exclusions.");
                    else if (zeroAttestation != null && liveSourceCount == 0 &&
                        (zeroAttestation.ObservedBankTransactionCount != 0 ||
                         zeroAttestation.ObservedArReceiptCount != 0 ||
                         zeroAttestation.ObservedApPaymentCount != 0))
                        comparabilityIssues.Add("The zero-activity attestation is no longer valid: its recorded source counts are inconsistent.");
                }

                var capturedSourceIds = cashActuals
                    .Where(x => x.SourceKind == PublicSectorBudgetActualSourceKind.BankTransaction)
                    .Select(x => x.SourceRecordId).Distinct().ToArray();
                var arReceiptIds = cashActuals.Where(x => !x.ReversalOfId.HasValue && x.SourceKind == PublicSectorBudgetActualSourceKind.ArReceipt)
                    .Select(x => x.SourceRecordId).Distinct().ToArray();
                var apPaymentIds = cashActuals.Where(x => !x.ReversalOfId.HasValue && x.SourceKind == PublicSectorBudgetActualSourceKind.ApPayment)
                    .Select(x => x.SourceRecordId).Distinct().ToArray();
                // A source record may instead be explicitly excluded, with a mandatory reason,
                // in the latest record-level reconciliation revision independently reviewed.
                var excludedBankSourceIds = reviewedExclusions.Where(x => x.SourceKind == "BankAccount")
                    .Select(x => x.SourceRecordId).Distinct().ToArray();
                var excludedArReceiptIds = reviewedExclusions.Where(x => x.SourceKind == "ReceivablesControl")
                    .Select(x => x.SourceRecordId).Distinct().ToArray();
                var excludedApPaymentIds = reviewedExclusions.Where(x => x.SourceKind == "PayablesControl")
                    .Select(x => x.SourceRecordId).Distinct().ToArray();
                var capturedSettlementJournalIds = await _db.ArReceipts
                    .Where(x => x.CompanyId == companyId && arReceiptIds.Contains(x.Id) && x.JournalEntryId.HasValue)
                    .Select(x => x.JournalEntryId!.Value)
                    .Concat(_db.ApPayments.Where(x => x.CompanyId == companyId &&
                        apPaymentIds.Contains(x.Id) && x.JournalEntryId.HasValue)
                        .Select(x => x.JournalEntryId!.Value))
                    .Distinct().ToArrayAsync(ct);

                // Every posted AR receipt/AP payment in the period must be represented by
                // its own certified source actual, or by a certified bank actual from the
                // same settlement journal. Otherwise a partial source capture could make
                // the period appear complete merely because some cash actuals exist.
                var capturedBankJournalIds = capturedSourceIds.Length == 0
                    ? Array.Empty<Guid>()
                    : await _db.BankTransactions.Where(x => x.CompanyId == companyId &&
                            capturedSourceIds.Contains(x.Id) && x.JournalEntryId.HasValue)
                        .Select(x => x.JournalEntryId!.Value).Distinct().ToArrayAsync(ct);
                var postedArReceipts = await _db.ArReceipts.Where(x =>
                        x.CompanyId == companyId && x.ReceiptDate >= actualVersion.PeriodStart &&
                        x.ReceiptDate < actualVersion.PeriodEnd && x.JournalEntryId.HasValue &&
                        _db.JournalEntries.Any(j => j.Id == x.JournalEntryId.Value &&
                            j.CompanyId == companyId && j.Status == JournalStatus.Posted))
                    .Select(x => new { x.Id, JournalEntryId = x.JournalEntryId!.Value }).ToListAsync(ct);
                var postedApPayments = await _db.ApPayments.Where(x =>
                        x.CompanyId == companyId && x.PaymentDate >= actualVersion.PeriodStart &&
                        x.PaymentDate < actualVersion.PeriodEnd && x.JournalEntryId.HasValue &&
                        _db.JournalEntries.Any(j => j.Id == x.JournalEntryId.Value &&
                            j.CompanyId == companyId && j.Status == JournalStatus.Posted))
                    .Select(x => new { x.Id, JournalEntryId = x.JournalEntryId!.Value }).ToListAsync(ct);
                var missingArSettlementCoverage = postedArReceipts.Count(x =>
                    !arReceiptIds.Contains(x.Id) && !excludedArReceiptIds.Contains(x.Id) &&
                    !capturedBankJournalIds.Contains(x.JournalEntryId));
                var missingApSettlementCoverage = postedApPayments.Count(x =>
                    !apPaymentIds.Contains(x.Id) && !excludedApPaymentIds.Contains(x.Id) &&
                    !capturedBankJournalIds.Contains(x.JournalEntryId));

                // A reconciled bank movement may be represented by its posted AR receipt/AP
                // payment source. Match those settlements through their posted journal identity;
                // do not require duplicate capture of the same underlying cash movement.
                var periodBankMovements = _db.BankTransactions.Where(x => x.CompanyId == companyId &&
                    x.TransactionDate >= actualVersion.PeriodStart && x.TransactionDate < actualVersion.PeriodEnd);
                var uncapturedMovements = await periodBankMovements.CountAsync(x =>
                    !x.IsReconciled || !x.JournalEntryId.HasValue ||
                    !_db.JournalEntries.Any(j => j.Id == x.JournalEntryId.Value &&
                        j.CompanyId == companyId && j.Status == JournalStatus.Posted) ||
                    (!capturedSourceIds.Contains(x.Id) && !excludedBankSourceIds.Contains(x.Id) &&
                     !capturedSettlementJournalIds.Contains(x.JournalEntryId.Value)), ct);
                var pendingCashActuals = cashActuals.Count(x => x.Status != PublicSectorBudgetActualStatus.Certified);
                var certifiedActuals = cashActuals.Where(x => x.Status == PublicSectorBudgetActualStatus.Certified).ToList();
                var invalidCertifiedCashActuals = 0;
                foreach (var certifiedActual in certifiedActuals)
                {
                    if (certifiedActual.ReversalOfId.HasValue)
                    {
                        var reversalTarget = await _db.PublicSectorBudgetExecutionActuals.FirstOrDefaultAsync(x =>
                            x.Id == certifiedActual.ReversalOfId.Value && x.CompanyId == companyId, ct);
                        if (reversalTarget == null || reversalTarget.Status != PublicSectorBudgetActualStatus.Reversed ||
                            reversalTarget.Amount != certifiedActual.Amount || reversalTarget.EventKind == certifiedActual.EventKind ||
                            string.IsNullOrWhiteSpace(certifiedActual.ReversalReason))
                            invalidCertifiedCashActuals++;
                    }
                    else if (!await IsCertifiedCashActualSourceValidAsync(companyId, certifiedActual, ct))
                        invalidCertifiedCashActuals++;
                }

                if (uncapturedMovements > 0)
                    comparabilityIssues.Add($"Cash actual source coverage is incomplete: {uncapturedMovements} posted bank movements are unreconciled or have not been captured.");
                if (missingArSettlementCoverage > 0)
                    comparabilityIssues.Add($"Cash actual source coverage is incomplete: {missingArSettlementCoverage} posted AR receipts are neither captured directly nor covered by a captured bank settlement.");
                if (missingApSettlementCoverage > 0)
                    comparabilityIssues.Add($"Cash actual source coverage is incomplete: {missingApSettlementCoverage} posted AP payments are neither captured directly nor covered by a captured bank settlement.");
                if (pendingCashActuals > 0)
                    comparabilityIssues.Add($"Cash actual certification is incomplete: {pendingCashActuals} actual records remain draft or pending certification.");
                if (invalidCertifiedCashActuals > 0)
                    comparabilityIssues.Add($"Cash actual evidence has changed or is no longer valid for {invalidCertifiedCashActuals} certified records.");

                // A source-backed comparison is not complete merely because captured actuals
                // agree with posted settlements. Require a complete approved source register,
                // independently reviewed control totals and independently reviewed record-level
                // allocations/exclusions for every registered source (including zero-record sources).
                var expectedSources = await _db.PublicSectorExpectedCashSources.Where(x =>
                    x.CompanyId == companyId && x.BudgetVersionId == actualVersion.Id).ToListAsync(ct);
                var sourceSubmissions = (await _db.PublicSectorCashSourceSubmissions.Where(x =>
                    x.CompanyId == companyId && x.BudgetVersionId == actualVersion.Id).ToListAsync(ct))
                    .GroupBy(x => x.ExpectedCashSourceId)
                    .Select(group => group.OrderByDescending(x => x.SubmissionRevision).First()).ToList();
                var sourceRegisterApproved = expectedSources.Count > 0 &&
                    expectedSources.All(x => x.Status == "Approved");
                var everyExpectedSourceReviewed = expectedSources.All(source =>
                    sourceSubmissions.Any(submission => submission.ExpectedCashSourceId == source.Id &&
                        submission.Status == "Reviewed" && submission.RecordLevelStatus == "Reviewed"));
                var noOrphanOrUnreviewedSubmissions = sourceSubmissions.Count == expectedSources.Count &&
                    sourceSubmissions.All(submission => submission.Status == "Reviewed" &&
                        submission.RecordLevelStatus == "Reviewed" &&
                        expectedSources.Any(source => source.Id == submission.ExpectedCashSourceId));
                var liveSourcePopulationsComplete = sourceRegisterApproved && noOrphanOrUnreviewedSubmissions;
                if (liveSourcePopulationsComplete)
                {
                    foreach (var expected in expectedSources)
                    {
                        var submission = sourceSubmissions.Single(x => x.ExpectedCashSourceId == expected.Id);
                        var population = await GetExpectedCashSourcePopulationAsync(companyId, expected,
                            submission.PeriodStart, submission.PeriodEnd, submission.Currency, ct);
                        if (!population.CurrencyMatches || population.RecordIds.Count != submission.RecordCount ||
                            population.ControlTotal != submission.ControlTotal ||
                            (population.NetMovement.HasValue &&
                                submission.OpeningBalance + population.NetMovement.Value != submission.ClosingBalance) ||
                            !await IsCurrentCashSourceReconciliationAsync(companyId, expected, submission, population, ct))
                        {
                            liveSourcePopulationsComplete = false;
                            break;
                        }
                    }
                }
                var registerAttestation = await _db.PublicSectorExpectedCashSourceRegisterAttestations
                    .Where(x => x.CompanyId == companyId && x.BudgetVersionId == actualVersion.Id)
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
                var registerCompletenessCertified = registerAttestation != null &&
                    registerAttestation.Status == "Certified" &&
                    registerAttestation.CertifiedBy.HasValue && registerAttestation.CertifiedAt.HasValue &&
                    registerAttestation.ExpectedSourceCount == expectedSources.Count &&
                    registerAttestation.EntityScope == actualVersion.EntityScope &&
                    sourceRegisterApproved && everyExpectedSourceReviewed && noOrphanOrUnreviewedSubmissions;
                if (!sourceRegisterApproved)
                    comparabilityIssues.Add("The approved expected cash-source register is missing or contains unapproved entries.");
                if (!everyExpectedSourceReviewed || !noOrphanOrUnreviewedSubmissions)
                    comparabilityIssues.Add("One or more expected cash sources lack independently reviewed source control totals or record-level reconciliation.");
                if (!registerCompletenessCertified)
                    comparabilityIssues.Add("Independent expected-source register completeness certification is missing, stale or inconsistent with the approved budget scope.");

                var liveSourceRegisterComplete = await HasCompleteCashSourceRegisterAsync(
                    companyId, actualVersion.Id, ct);
                var cashSourceControlsComplete = sourceRegisterApproved && everyExpectedSourceReviewed &&
                    noOrphanOrUnreviewedSubmissions && liveSourcePopulationsComplete &&
                    registerCompletenessCertified && liveSourceRegisterComplete;
                if (!liveSourcePopulationsComplete || !liveSourceRegisterComplete)
                    comparabilityIssues.Add("The live source record population, control total, currency or independent certification no longer agrees with the reviewed source reconciliation.");
                if (uncapturedMovements == 0 && missingArSettlementCoverage == 0 &&
                    missingApSettlementCoverage == 0 && pendingCashActuals == 0 &&
                    invalidCertifiedCashActuals == 0 && cashSourceControlsComplete)
                {
                    foreach (var group in cashActuals.GroupBy(x => x.BudgetVersionLineId))
                        cashActualsByBudgetLine[group.Key] = group.Sum(x => x.Amount);
                }
            }
            if (companyProfile == null)
                comparabilityIssues.Add("The registered company profile could not be resolved.");
            else
            {
                if (!string.Equals(original.EntityScope.Trim(), companyProfile.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    comparabilityIssues.Add("The budget entity scope does not exactly match the registered company name.");
                if (!string.Equals(original.Currency, companyProfile.FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
                    comparabilityIssues.Add("The budget currency does not match the company's functional currency.");
            }
            if (final != null)
            {
                if (final.AccountingBasis != original.AccountingBasis)
                    comparabilityIssues.Add("Original and final budgets use different accounting bases.");
                if (final.PeriodStart != original.PeriodStart || final.PeriodEnd != original.PeriodEnd)
                    comparabilityIssues.Add("Original and final budgets cover different reporting periods.");
                if (final.Currency != original.Currency)
                    comparabilityIssues.Add("Original and final budgets use different currencies.");
                if (final.ClassificationBasis != original.ClassificationBasis)
                    comparabilityIssues.Add("Original and final budgets use different classification bases.");
                if (final.EntityScope != original.EntityScope)
                    comparabilityIssues.Add("Original and final budgets use different entity scopes.");
            }
            if (original.AccountingBasis == AccountingBasis.Accrual)
            {
                // Do not silently omit posted actuals whose fund-centre/cost-centre dimensions
                // cannot be matched to an approved budget line. Such rows would otherwise
                // disappear from the IPSAS 24 comparison while the report appeared comparable.
                var budgetScopeLines = final!.Lines;
                var budgetAccountIds = budgetScopeLines.Select(x => x.GlAccountId).Distinct().ToArray();
                var postedBudgetScopeActuals = await _db.JournalLines.Where(line =>
                    budgetAccountIds.Contains(line.GlAccountId) &&
                    line.JournalEntry!.CompanyId == companyId &&
                    line.JournalEntry.EntryDate >= original.PeriodStart &&
                    line.JournalEntry.EntryDate < original.PeriodEnd &&
                    (line.JournalEntry.Status == JournalStatus.Posted ||
                     line.JournalEntry.Status == JournalStatus.Reversed))
                    .Select(line => new { line.GlAccountId, line.FundCentreId, line.CostCentreId })
                    .ToListAsync(ct);
                var unmappedBudgetScopeActuals = postedBudgetScopeActuals.Count(actual =>
                    !actual.FundCentreId.HasValue ||
                    !budgetScopeLines.Any(budgetLine =>
                        budgetLine.GlAccountId == actual.GlAccountId &&
                        budgetLine.FundCentreId == actual.FundCentreId.Value &&
                        budgetLine.CostCentreId == actual.CostCentreId));
                if (unmappedBudgetScopeActuals > 0)
                    comparabilityIssues.Add($"Accrual actual coverage is incomplete: {unmappedBudgetScopeActuals} posted GL lines on budgeted accounts have missing or unmatched Fund Centre/cost-centre dimensions.");
            }
            if (original.AccountingBasis == AccountingBasis.Cash && final != null)
            {
                var omittedOriginalDimensions = original.Lines.Count(originalLine =>
                    !final.Lines.Any(finalLine =>
                        finalLine.FundCentreId == originalLine.FundCentreId &&
                        finalLine.GlAccountId == originalLine.GlAccountId &&
                        finalLine.CostCentreId == originalLine.CostCentreId &&
                        string.Equals(finalLine.ClassificationCode, originalLine.ClassificationCode,
                            StringComparison.OrdinalIgnoreCase)));
                if (omittedOriginalDimensions > 0)
                    comparabilityIssues.Add($"The final cash budget omits {omittedOriginalDimensions} original budget dimensions. Retain zero-value lines for removed classifications so execution actuals can still be captured and compared without misreporting them as zero.");
            }

            var comparable = comparabilityIssues.Count == 0;
            var comparabilityMessage = comparable
                ? original.AccountingBasis == AccountingBasis.Cash
                    ? "Cash-basis actuals are sourced from certified bank transactions, AR receipts and AP payments mapped to approved budget lines; independently reviewed exclusions account for out-of-budget source records. Source register completeness, live record populations, control totals, entity, currency, classification and reporting-period checks passed. Complete the separate Statement of Cash Flows reconciliation before statutory issue."
                    : "Accrual-basis ledger actuals are comparable because the budget basis, registered entity, functional currency, classification basis and reporting period agree."
                : string.Join(" ", comparabilityIssues);
            var rows = new List<BudgetComparisonLine>();
            var dimensions = original.Lines.Concat(final?.Lines ?? Array.Empty<PublicSectorBudgetVersionLine>())
                .GroupBy(x => $"{x.FundCentreId:N}|{x.GlAccountId:N}|{x.CostCentreId?.ToString("N") ?? "none"}|{x.ClassificationCode}")
                .Select(g => new
                {
                    OriginalLine = g.FirstOrDefault(x => original.Lines.Any(o => o.Id == x.Id)),
                    FinalLine = final?.Lines.FirstOrDefault(x => x.FundCentreId == g.First().FundCentreId &&
                        x.GlAccountId == g.First().GlAccountId && x.CostCentreId == g.First().CostCentreId &&
                        x.ClassificationCode == g.First().ClassificationCode)
                }).ToList();
            var costCentreIds = dimensions.Select(x => (x.FinalLine ?? x.OriginalLine!).CostCentreId)
                .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var costCentres = await _db.CostCentres.Where(x =>
                x.CompanyId == companyId && costCentreIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
            foreach (var dimension in dimensions)
            {
                var line = dimension.OriginalLine ?? dimension.FinalLine!;
                var account = await _db.GlAccounts.FirstAsync(x => x.Id == line.GlAccountId && x.CompanyId == companyId, ct);
                var fund = await _db.FundCentres.FirstAsync(x => x.Id == line.FundCentreId && x.CompanyId == companyId, ct);
                var costCentre = line.CostCentreId.HasValue && costCentres.TryGetValue(line.CostCentreId.Value, out var foundCostCentre)
                    ? foundCostCentre : null;
                decimal? actual = null;
                if (comparable)
                {
                    if (original.AccountingBasis == AccountingBasis.Cash)
                    {
                        var actualLine = dimension.FinalLine ?? dimension.OriginalLine!;
                        actual = cashActualsByBudgetLine.TryGetValue(actualLine.Id, out var cashAmount)
                            ? cashAmount : 0m;
                    }
                    else
                    {
                        var raw = await _db.JournalLines.Where(l => l.GlAccountId == line.GlAccountId &&
                            l.FundCentreId == line.FundCentreId && l.CostCentreId == line.CostCentreId &&
                            l.JournalEntry!.CompanyId == companyId &&
                            l.JournalEntry.EntryDate >= original.PeriodStart && l.JournalEntry.EntryDate < original.PeriodEnd &&
                            (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                            .SumAsync(l => account.Type == GlAccountType.Income ? l.BaseCredit - l.BaseDebit : l.BaseDebit - l.BaseCredit, ct);
                        actual = raw;
                    }
                }
                var originalAmount = dimension.OriginalLine?.Amount ?? 0m;
                var amendmentExplanation = final?.Kind != PublicSectorBudgetVersionKind.Revised
                    ? null
                    : final.AmendmentNature switch
                    {
                        PublicSectorBudgetAmendmentNature.ReallocationWithinBudget =>
                            $"Reallocation within the approved budget: {final.AmendmentReason}",
                        PublicSectorBudgetAmendmentNature.OtherAuthorisedFactors =>
                            $"Other authorised factors: {final.AmendmentReason}",
                        PublicSectorBudgetAmendmentNature.Mixed =>
                            $"Reallocation and other authorised factors: {final.AmendmentReason}",
                        _ => $"Amendment nature not recorded. {final.AmendmentReason}"
                    };
                // A dimension absent from the final approved budget has a final amount of zero,
                // not an unknown/null amount. The same applies to a line introduced after the
                // original budget: its original amount is zero.
                var finalAmount = dimension.FinalLine?.Amount ?? 0m;
                var variance = actual.HasValue ? finalAmount - actual.Value : (decimal?)null;
                rows.Add(new BudgetComparisonLine(dimension.OriginalLine?.LineNumber ?? dimension.FinalLine!.LineNumber,
                    line.Description, fund.Code, fund.Name, fund.Level.ToString(),
                    costCentre?.Code ?? string.Empty, costCentre?.Name ?? string.Empty,
                    account.Code, account.Name, account.Type.ToString(), line.ClassificationCode,
                    dimension.FinalLine != null ? dimension.FinalLine.TreasuryClassificationCode : dimension.OriginalLine?.TreasuryClassificationCode,
                    dimension.FinalLine != null ? dimension.FinalLine.NoteReference : dimension.OriginalLine?.NoteReference,
                    originalAmount, finalAmount, actual, variance,
                    actual.HasValue && finalAmount != 0m
                        ? Math.Round(variance!.Value / finalAmount * 100m, 2) : null,
                    dimension.OriginalLine == null
                        ? $"Line introduced in the final approved budget. {amendmentExplanation}"
                        : dimension.FinalLine == null
                            ? $"Line removed from the final approved budget. {amendmentExplanation}"
                            : amendmentExplanation,
                    (dimension.FinalLine ?? dimension.OriginalLine!).Id));
            }

            var reconciliations = await _db.PublicSectorBudgetReconciliations
                .Where(x => x.CompanyId == companyId && x.FinancialYear == financialYear && x.Status == PublicSectorBudgetReconciliationStatus.Approved)
                .OrderBy(x => x.Type).ThenBy(x => x.Description).ToListAsync(ct);
            var varianceExplanations = await _db.PublicSectorBudgetVarianceExplanations
                .Where(x => x.CompanyId == companyId && x.BudgetVersionId == final!.Id)
                .OrderBy(x => x.CreatedAt).ToListAsync(ct);
            var explanationComplete = comparable && rows.Where(x =>
                x.ActualOnComparableBasis.HasValue && x.Variance.HasValue && x.Variance.Value != 0m)
                .All(line => varianceExplanations.Any(explanation =>
                    explanation.BudgetVersionLineId == line.BudgetVersionLineId &&
                    explanation.Status == "Approved" && explanation.VarianceAmount == line.Variance!.Value));
            var originalToFinalExplanationsComplete = final!.Kind != PublicSectorBudgetVersionKind.Revised ||
                (final.AmendmentNature.HasValue && !string.IsNullOrWhiteSpace(final.AmendmentReason));

            var comparableRevenueActual = comparable
                ? rows.Where(x => x.AccountType == GlAccountType.Income.ToString()).Sum(x => x.ActualOnComparableBasis ?? 0m)
                : (decimal?)null;
            var comparableExpenseActual = comparable
                ? rows.Where(x => x.AccountType == GlAccountType.Expense.ToString()).Sum(x => x.ActualOnComparableBasis ?? 0m)
                : (decimal?)null;
            var revenueReconciliationRows = reconciliations.Where(x =>
                string.Equals(x.StatementSection, "Revenue", StringComparison.OrdinalIgnoreCase)).ToList();
            var expenseReconciliationRows = reconciliations.Where(x =>
                string.Equals(x.StatementSection, "Expense", StringComparison.OrdinalIgnoreCase)).ToList();
            var revenueHasAdjustment = revenueReconciliationRows.Any(x => x.RevenueAmount != 0m);
            var expenseHasAdjustment = expenseReconciliationRows.Any(x => x.ExpenseAmount != 0m);
            var revenueHasNoDifference = revenueReconciliationRows.Any(x => x.IsNoDifferenceAttestation);
            var expenseHasNoDifference = expenseReconciliationRows.Any(x => x.IsNoDifferenceAttestation);
            var revenueCovered = (revenueHasAdjustment || revenueHasNoDifference) &&
                !(revenueHasAdjustment && revenueHasNoDifference);
            var expenseCovered = (expenseHasAdjustment || expenseHasNoDifference) &&
                !(expenseHasAdjustment && expenseHasNoDifference);
            var revenueAdjustment = revenueReconciliationRows.Sum(x => x.RevenueAmount);
            var expenseAdjustment = expenseReconciliationRows.Sum(x => x.ExpenseAmount);
            var financialStatementReconciliationRequired = original.AccountingBasis == AccountingBasis.Cash;
            var financialStatementReconciliationComplete = comparable &&
                (!financialStatementReconciliationRequired || (revenueCovered && expenseCovered));
            var financialStatementReconciliationMessage = !comparable
                ? "Comparable budget actuals are not available, so the financial-statement reconciliation cannot be completed."
                : !financialStatementReconciliationRequired
                    ? "Accrual-basis actuals are already on the financial-statement basis. No separate basis reconciliation is required unless an approved difference is recorded."
                    : financialStatementReconciliationComplete
                        ? "Revenue and expense actuals on the budget's cash basis are reconciled to financial-statement actuals using independently approved adjustments or explicit no-difference attestations."
                        : $"Cash-basis budget actuals require approved financial-statement reconciliation for: {string.Join(" and ", new[] { !revenueCovered ? "Revenue" : null, !expenseCovered ? "Expense" : null }.Where(x => x != null))}.";
            var financialStatementReconciliation = new BudgetFinancialStatementReconciliation(
                financialStatementReconciliationRequired, financialStatementReconciliationComplete,
                financialStatementReconciliationMessage, comparableRevenueActual, revenueAdjustment,
                financialStatementReconciliationComplete && comparableRevenueActual.HasValue
                    ? comparableRevenueActual.Value + revenueAdjustment : null,
                revenueCovered, comparableExpenseActual, expenseAdjustment,
                financialStatementReconciliationComplete && comparableExpenseActual.HasValue
                    ? comparableExpenseActual.Value + expenseAdjustment : null,
                expenseCovered);

            return new BudgetComparisonReport(original.Id, final.Id, financialYear, original.Currency,
                original.AccountingBasis.ToString(), original.ClassificationBasis, original.EntityScope,
                original.PeriodStart, original.PeriodEnd, true, comparable,
                comparabilityMessage, rows, reconciliations, varianceExplanations, explanationComplete,
                originalToFinalExplanationsComplete, financialStatementReconciliation);
        }


        private async Task<BudgetComparisonLine> GetCurrentBudgetVarianceLineAsync(
            Guid companyId, Guid budgetVersionId, Guid budgetVersionLineId, CancellationToken ct)
        {
            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved, ct)
                ?? throw new InvalidOperationException("An approved final budget version is required.");
            var report = await GetBudgetComparisonAsync(companyId, version.FinancialYear, ct);
            if (report.FinalBudgetVersionId != budgetVersionId || !report.ComparableActualsAvailable)
                throw new InvalidOperationException("Variance explanations require the current approved final budget and comparable actuals.");
            return report.Lines.FirstOrDefault(x => x.BudgetVersionLineId == budgetVersionLineId)
                ?? throw new InvalidOperationException("The budget line is not part of the current final budget comparison.");
        }

        public async Task<PublicSectorBudgetVarianceExplanation> CreateBudgetVarianceExplanationAsync(
            Guid companyId, Guid budgetVersionId, Guid budgetVersionLineId,
            string explanation, string evidenceReference, Guid actorId, CancellationToken ct = default,
            string? commentReference = null)
        {
            if (string.IsNullOrWhiteSpace(explanation) || string.IsNullOrWhiteSpace(evidenceReference))
                throw new InvalidOperationException("A variance explanation and supporting evidence reference are required.");
            var line = await GetCurrentBudgetVarianceLineAsync(companyId, budgetVersionId, budgetVersionLineId, ct);
            if (!line.Variance.HasValue || line.Variance.Value == 0m)
                throw new InvalidOperationException("A variance explanation is only required for a non-zero final-budget-to-actual variance.");
            if (await _db.PublicSectorBudgetVarianceExplanations.AnyAsync(x =>
                x.CompanyId == companyId && x.BudgetVersionLineId == budgetVersionLineId &&
                x.VarianceAmount == line.Variance.Value && x.Status != "Rejected", ct))
                throw new InvalidOperationException("An active explanation already exists for this exact variance. If the actual amount has changed, a new explanation may be prepared while the prior approved explanation remains immutable audit history.");
            var item = new PublicSectorBudgetVarianceExplanation
            {
                CompanyId = companyId, BudgetVersionId = budgetVersionId,
                BudgetVersionLineId = budgetVersionLineId, VarianceAmount = line.Variance.Value,
                Explanation = explanation.Trim(), EvidenceReference = evidenceReference.Trim(),
                CommentReference = string.IsNullOrWhiteSpace(commentReference)
                    ? evidenceReference.Trim() : commentReference.Trim(),
                Status = "Draft", CreatedBy = actorId
            };
            _db.PublicSectorBudgetVarianceExplanations.Add(item);
            _audit.Record(companyId, actorId, "PublicSectorBudgetVarianceExplanation.Created", "PublicSector",
                nameof(PublicSectorBudgetVarianceExplanation), item.Id, null,
                new { item.BudgetVersionId, item.BudgetVersionLineId, item.VarianceAmount,
                    item.Explanation, item.EvidenceReference, item.CommentReference });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorBudgetVarianceExplanation> SubmitBudgetVarianceExplanationAsync(
            Guid companyId, Guid explanationId, Guid actorId, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorBudgetVarianceExplanations.FirstOrDefaultAsync(x =>
                x.Id == explanationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Budget variance explanation not found.");
            if (item.Status != "Draft" || item.CreatedBy != actorId)
                throw new InvalidOperationException("Only the preparer may submit a draft variance explanation.");
            var current = await GetCurrentBudgetVarianceLineAsync(companyId, item.BudgetVersionId, item.BudgetVersionLineId, ct);
            if (current.Variance != item.VarianceAmount)
                throw new InvalidOperationException("The actual variance has changed since the explanation was prepared. Recreate the explanation against the current comparison.");
            item.Status = "PendingReview";
            item.SubmittedBy = actorId;
            item.SubmittedAt = DateTime.UtcNow;
            _audit.Record(companyId, actorId, "PublicSectorBudgetVarianceExplanation.Submitted", "PublicSector",
                nameof(PublicSectorBudgetVarianceExplanation), item.Id, new { Status = "Draft" },
                new { item.Status, item.SubmittedBy, item.SubmittedAt });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorBudgetVarianceExplanation> ReviewBudgetVarianceExplanationAsync(
            Guid companyId, Guid explanationId, Guid reviewerId, bool accepted,
            string? reviewNote, CancellationToken ct = default)
        {
            var item = await _db.PublicSectorBudgetVarianceExplanations.FirstOrDefaultAsync(x =>
                x.Id == explanationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Budget variance explanation not found.");
            if (item.Status != "PendingReview" || item.CreatedBy == reviewerId || item.SubmittedBy == reviewerId)
                throw new InvalidOperationException("A pending variance explanation requires an independent reviewer.");
            if (!accepted && string.IsNullOrWhiteSpace(reviewNote))
                throw new InvalidOperationException("A reason is required when rejecting a variance explanation.");
            if (accepted)
            {
                var current = await GetCurrentBudgetVarianceLineAsync(companyId, item.BudgetVersionId, item.BudgetVersionLineId, ct);
                if (current.Variance != item.VarianceAmount)
                    throw new InvalidOperationException("The actual variance changed after submission. Reject and recreate the explanation against the current comparison.");
            }
            item.Status = accepted ? "Approved" : "Rejected";
            item.ReviewedBy = reviewerId;
            item.ReviewedAt = DateTime.UtcNow;
            item.ReviewNote = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim();
            _audit.Record(companyId, reviewerId, "PublicSectorBudgetVarianceExplanation.Reviewed", "PublicSector",
                nameof(PublicSectorBudgetVarianceExplanation), item.Id, new { Status = "PendingReview" },
                new { item.Status, item.ReviewedBy, item.ReviewedAt, item.ReviewNote });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<PublicSectorBudgetAllocation> CreateBudgetAllocationAsync(Guid companyId, Guid fundCentreId,
            Guid glAccountId, Guid? costCentreId, int financialYear, decimal amount, Guid actorId, CancellationToken ct = default)
        {
            if (amount <= 0 || financialYear < 2000 || financialYear > 2200)
                throw new InvalidOperationException("A positive allocation amount and valid financial year are required.");

            var fundCentre = await _db.FundCentres.FirstOrDefaultAsync(x =>
                x.Id == fundCentreId && x.CompanyId == companyId && x.FinancialYear == financialYear, ct)
                ?? throw new InvalidOperationException("Fund centre was not found for the selected company and financial year.");
            if (fundCentre.IsClosed) throw new InvalidOperationException("A closed fund centre cannot receive budget allocations.");

            var account = await _db.GlAccounts.FirstOrDefaultAsync(x =>
                x.Id == glAccountId && x.CompanyId == companyId && x.IsActive && x.Type == GlAccountType.Expense, ct)
                ?? throw new InvalidOperationException("Budget allocations must reference an active expense account in this company.");
            if (costCentreId.HasValue && !await _db.CostCentres.AnyAsync(x =>
                x.Id == costCentreId.Value && x.CompanyId == companyId && x.IsActive, ct))
                throw new InvalidOperationException("Cost centre was not found or is inactive in this company.");

            if (await _db.PublicSectorBudgetAllocations.AnyAsync(x =>
                x.CompanyId == companyId && x.FundCentreId == fundCentreId && x.GlAccountId == glAccountId &&
                x.CostCentreId == costCentreId && x.FinancialYear == financialYear, ct))
                throw new InvalidOperationException("A budget allocation already exists for this fund centre, account, cost centre and financial year.");

            var allocation = new PublicSectorBudgetAllocation
            {
                CompanyId = companyId, FundCentreId = fundCentreId, GlAccountId = account.Id,
                CostCentreId = costCentreId, FinancialYear = financialYear, Amount = amount,
                Status = PublicSectorBudgetAllocationStatus.Draft, CreatedBy = actorId
            };
            _db.PublicSectorBudgetAllocations.Add(allocation);
            _audit.Record(companyId, actorId, "PublicSectorBudgetAllocation.Created", "PublicSector",
                nameof(PublicSectorBudgetAllocation), allocation.Id, null,
                new { allocation.FundCentreId, allocation.GlAccountId, allocation.CostCentreId,
                    allocation.FinancialYear, allocation.Amount, allocation.Status });
            await _db.SaveChangesAsync(ct);
            return allocation;
        }

        public async Task MarkBudgetAllocationPendingAsync(Guid companyId, Guid allocationId, Guid workflowId, CancellationToken ct = default)
        {
            var allocation = await _db.PublicSectorBudgetAllocations.FirstOrDefaultAsync(x =>
                x.Id == allocationId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Budget allocation not found.");
            if (allocation.Status != PublicSectorBudgetAllocationStatus.Draft)
                throw new InvalidOperationException("Only a draft budget allocation can be submitted for approval.");
            var workflow = await _db.WorkflowRequests.FirstOrDefaultAsync(x =>
                x.Id == workflowId && x.CompanyId == companyId && x.RequestType == "PublicSectorBudgetAllocation" &&
                x.ReferenceId == allocation.Id && x.Amount == allocation.Amount &&
                x.FundCentreId == allocation.FundCentreId && x.Status == WorkflowStatus.Pending, ct)
                ?? throw new InvalidOperationException("A matching pending approval workflow is required.");
            allocation.ApprovalWorkflowId = workflow.Id;
            allocation.Status = PublicSectorBudgetAllocationStatus.PendingApproval;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<PublicSectorBudgetAllocation> FinaliseBudgetAllocationApprovalAsync(Guid companyId,
            Guid allocationId, Guid actorId, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                var allocation = await _db.PublicSectorBudgetAllocations.FirstOrDefaultAsync(x =>
                    x.Id == allocationId && x.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Budget allocation not found.");
                if (allocation.Status != PublicSectorBudgetAllocationStatus.PendingApproval || !allocation.ApprovalWorkflowId.HasValue)
                    throw new InvalidOperationException("Only a pending allocation with an approval workflow can be approved.");
                var workflow = await _db.WorkflowRequests.Include(x => x.Approvals).FirstOrDefaultAsync(x =>
                    x.Id == allocation.ApprovalWorkflowId.Value && x.CompanyId == companyId &&
                    x.RequestType == "PublicSectorBudgetAllocation" && x.ReferenceId == allocation.Id &&
                    x.Status == WorkflowStatus.Approved && x.Approvals.Any(a => a.Approved && a.ApproverId == actorId), token)
                    ?? throw new InvalidOperationException("The independent approval workflow has not been approved.");
                if (workflow.SubmittedBy == actorId)
                    throw new InvalidOperationException("The allocation submitter cannot finalise their own approval.");
                await _db.AcquireTransactionLockAsync($"public-sector-budget-allocation:{companyId:N}:{allocation.FundCentreId:N}", token);
                var fundCentre = await _db.FundCentres.FirstOrDefaultAsync(x =>
                    x.Id == allocation.FundCentreId && x.CompanyId == companyId && !x.IsClosed, token)
                    ?? throw new InvalidOperationException("The allocation's fund centre is closed or unavailable.");
                var approvedTotal = await _db.PublicSectorBudgetAllocations
                    .Where(x => x.CompanyId == companyId && x.FundCentreId == allocation.FundCentreId &&
                        x.FinancialYear == allocation.FinancialYear && x.Status == PublicSectorBudgetAllocationStatus.Approved &&
                        x.Id != allocation.Id)
                    .SumAsync(x => (decimal?)x.Amount, token) ?? 0m;
                if (approvedTotal + allocation.Amount > fundCentre.AppropriationAmount + fundCentre.CarriedForwardAmount)
                    throw new InvalidOperationException("Approved allocations cannot exceed the fund centre's appropriation plus carried-forward balance.");
                var beforeApproval = new { allocation.Status, allocation.ApprovedBy, allocation.ApprovedAt };
                allocation.Status = PublicSectorBudgetAllocationStatus.Approved;
                allocation.ApprovedBy = actorId;
                allocation.ApprovedAt = DateTime.UtcNow;
                _audit.Record(companyId, actorId, "PublicSectorBudgetAllocation.Approved", "PublicSector",
                    nameof(PublicSectorBudgetAllocation), allocation.Id, beforeApproval,
                    new { allocation.Status, allocation.ApprovedBy, allocation.ApprovedAt,
                        allocation.ApprovalWorkflowId, allocation.Amount });
                await _db.SaveChangesAsync(token);
                return allocation;
            }, ct);
        }

        public async Task<List<PublicSectorBudgetVarianceLine>> GetBudgetVarianceReportAsync(Guid companyId,
            int financialYear, Guid? fundCentreId = null, CancellationToken ct = default)
        {
            if (financialYear < 2000 || financialYear > 2200)
                throw new InvalidOperationException("A valid financial year is required.");
            var allocations = await _db.PublicSectorBudgetAllocations
                .Where(x => x.CompanyId == companyId && x.FinancialYear == financialYear &&
                    x.Status == PublicSectorBudgetAllocationStatus.Approved &&
                    (!fundCentreId.HasValue || x.FundCentreId == fundCentreId.Value))
                .Join(_db.FundCentres, a => a.FundCentreId, f => f.Id, (a, f) => new { Allocation = a, FundCentre = f })
                .Join(_db.GlAccounts, x => x.Allocation.GlAccountId, g => g.Id, (x, g) => new { x.Allocation, x.FundCentre, Account = g })
                .GroupJoin(_db.CostCentres, x => x.Allocation.CostCentreId, cc => (Guid?)cc.Id,
                    (x, costCentres) => new { x.Allocation, x.FundCentre, x.Account, CostCentre = costCentres.FirstOrDefault() })
                .ToListAsync(ct);

            var fiscalStartMonth = await _db.Companies.Where(x => x.Id == companyId)
                .Select(x => x.FiscalYearStartMonth).FirstOrDefaultAsync(ct);
            var periodStart = FiscalCalendar.FiscalYearStart(new DateTime(financialYear, 12, 31), fiscalStartMonth);
            var periodEnd = periodStart.AddYears(1);
            var result = new List<PublicSectorBudgetVarianceLine>(allocations.Count);
            foreach (var item in allocations)
            {
                var actuals = await _db.JournalLines
                    .Where(l => l.GlAccountId == item.Allocation.GlAccountId &&
                        l.FundCentreId == item.Allocation.FundCentreId &&
                        l.CostCentreId == item.Allocation.CostCentreId &&
                        l.JournalEntry!.CompanyId == companyId &&
                        l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate < periodEnd &&
                        (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                    .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);
                var variance = item.Allocation.Amount - actuals;
                result.Add(new PublicSectorBudgetVarianceLine(item.FundCentre.Id, item.FundCentre.Code,
                    item.FundCentre.Name, financialYear, item.Account.Id, item.Account.Code, item.Account.Name,
                    item.Allocation.CostCentreId, item.CostCentre?.Code, item.CostCentre?.Name,
                    item.Allocation.Amount, actuals, variance,
                    item.Allocation.Amount == 0 ? 0m : Math.Round(variance / item.Allocation.Amount * 100m, 2)));
            }
            return result.OrderBy(x => x.FundCentreCode).ThenBy(x => x.AccountCode).ThenBy(x => x.CostCentreCode).ToList();
        }

        public sealed record PublicSectorClassificationLine(
            string ProgrammeCode, string ProgrammeName, string EconomicClassificationCode,
            string EconomicClassificationName, string? ObjectiveCode, string? ObjectiveName,
            decimal ApprovedBudget, decimal PostedActuals, decimal Variance, decimal VariancePercent);

        /// <summary>
        /// Government expenditure schedule across the dimensions already maintained by the ledger:
        /// fund-centre hierarchy (programme), GL account SubType (economic classification), and
        /// cost centre (administrative/objective dimension). Classification codes are tenant-defined;
        /// no national statutory code set is assumed or fabricated.
        /// </summary>
        public async Task<List<PublicSectorClassificationLine>> GetClassificationScheduleAsync(
            Guid companyId, int financialYear, Guid? fundCentreId = null, CancellationToken ct = default)
        {
            if (financialYear < 2000 || financialYear > 2200)
                throw new InvalidOperationException("A valid financial year is required.");

            var approved = await _db.PublicSectorBudgetAllocations
                .Where(a => a.CompanyId == companyId && a.FinancialYear == financialYear &&
                    a.Status == PublicSectorBudgetAllocationStatus.Approved &&
                    (!fundCentreId.HasValue || a.FundCentreId == fundCentreId.Value))
                .Join(_db.FundCentres, a => a.FundCentreId, f => f.Id,
                    (a, f) => new { Allocation = a, FundCentre = f })
                .Join(_db.GlAccounts, x => x.Allocation.GlAccountId, g => g.Id,
                    (x, g) => new { x.Allocation, x.FundCentre, Account = g })
                .GroupJoin(_db.CostCentres, x => x.Allocation.CostCentreId, cc => (Guid?)cc.Id,
                    (x, costCentres) => new { x.Allocation, x.FundCentre, x.Account, CostCentre = costCentres.FirstOrDefault() })
                .ToListAsync(ct);

            var fiscalStartMonth = await _db.Companies.Where(x => x.Id == companyId)
                .Select(x => x.FiscalYearStartMonth).FirstOrDefaultAsync(ct);
            var periodStart = FiscalCalendar.FiscalYearStart(new DateTime(financialYear, 12, 31), fiscalStartMonth);
            var periodEnd = periodStart.AddYears(1);

            var lines = new List<PublicSectorClassificationLine>(approved.Count);
            foreach (var item in approved)
            {
                var actuals = await _db.JournalLines
                    .Where(l => l.GlAccountId == item.Allocation.GlAccountId &&
                        l.FundCentreId == item.Allocation.FundCentreId &&
                        l.CostCentreId == item.Allocation.CostCentreId &&
                        l.JournalEntry!.CompanyId == companyId &&
                        l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate < periodEnd &&
                        (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                    .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);
                var code = string.IsNullOrWhiteSpace(item.Account.SubType) ? "UNCLASSIFIED" : item.Account.SubType.Trim();
                var variance = item.Allocation.Amount - actuals;
                lines.Add(new PublicSectorClassificationLine(
                    item.FundCentre.Code, item.FundCentre.Name, code, code,
                    item.CostCentre?.Code, item.CostCentre?.Name,
                    item.Allocation.Amount, actuals, variance,
                    item.Allocation.Amount == 0m ? 0m : Math.Round(variance / item.Allocation.Amount * 100m, 2)));
            }

            return lines.OrderBy(x => x.ProgrammeCode)
                .ThenBy(x => x.EconomicClassificationCode).ThenBy(x => x.ObjectiveCode)
                .ToList();
        }

        public async Task<FundCentre> CreateFundCentreAsync(Guid companyId, string code, string name, FundCentreLevel level,
            Guid? parentId, int financialYear, decimal appropriationAmount, decimal carriedForward, CancellationToken ct = default)
        {
            if (parentId.HasValue)
            {
                var parent = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == parentId && f.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("Parent fund centre not found.");
                if (level != (FundCentreLevel)((int)parent.Level + 1)) throw new InvalidOperationException("A child fund centre must be exactly one level below its parent (Vote > Program > Sub-Program).");
                if (parent.FinancialYear != financialYear) throw new InvalidOperationException("A child fund centre must belong to the same financial year as its parent.");
            }

            else if (level != FundCentreLevel.Vote)
                throw new InvalidOperationException("A root fund centre must be a Vote.");
            if (financialYear < 2000 || financialYear > 2200 || appropriationAmount < 0 || carriedForward < 0)
                throw new InvalidOperationException("Financial year and appropriation amounts are invalid.");

            var fc = new FundCentre
            {
                CompanyId = companyId,
                Code = code,
                Name = name,
                Level = level,
                ParentId = parentId,
                FinancialYear = financialYear,
                AppropriationAmount = appropriationAmount,
                CarriedForwardAmount = carriedForward
            };
            _db.FundCentres.Add(fc);
            await _db.SaveChangesAsync(ct);
            return fc;
        }

        /// <summary>Creates an encumbrance against a fund centre when a PO is approved. Blocks the
        /// commitment (unless overridden) if it would exceed available budget.</summary>
        public async Task<Commitment> CommitAsync(Guid companyId, Guid fundCentreId, Guid purchaseOrderId, decimal amount,
            bool allowOverride, string? overrideReason, Guid actorId, string? ipAddress = null, CancellationToken ct = default)
        {
            if (amount <= 0) throw new InvalidOperationException("A commitment amount must be greater than zero.");
            if (allowOverride && string.IsNullOrWhiteSpace(overrideReason))
                throw new InvalidOperationException("A budget override requires a recorded justification.");

            return await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-budget:{companyId:N}:{fundCentreId:N}", token);
                var fundCentre = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Fund centre not found.");
                if (fundCentre.IsClosed) throw new InvalidOperationException("A closed fund centre cannot accept new commitments.");

                var purchaseOrder = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == purchaseOrderId && p.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Purchase order not found in this company.");
                if (purchaseOrder.Status != PurchaseOrderStatus.Approved || purchaseOrder.FundCentreId != fundCentreId)
                    throw new InvalidOperationException("A commitment requires an approved purchase order assigned to the same fund centre.");
                if (purchaseOrder.Total != amount)
                    throw new InvalidOperationException("The commitment amount must equal the approved purchase order total.");
                if (allowOverride)
                {
                    var overrideApproval = await _db.WorkflowRequests.Include(w => w.Approvals)
                        .FirstOrDefaultAsync(w => w.CompanyId == companyId &&
                            w.RequestType == "PublicSectorBudgetOverride" && w.ReferenceTable == "PurchaseOrder" &&
                            w.ReferenceId == purchaseOrderId && w.SubmittedBy == actorId &&
                            w.Amount == amount && w.FundCentreId == fundCentreId &&
                            w.Notes == overrideReason!.Trim() && w.Status == WorkflowStatus.Approved &&
                            w.ApprovalChainId != null, token);
                    if (overrideApproval == null || overrideApproval.Approvals.Count == 0 ||
                        overrideApproval.Approvals.Any(a => !a.Approved))
                        throw new InvalidOperationException("A budget override requires independent approval through the configured PublicSectorBudgetOverride approval chain before commitment.");
                }
                if (amount != purchaseOrder.Total)
                    throw new InvalidOperationException("The commitment amount must equal the approved purchase order total.");
                if (await _db.Commitments.AnyAsync(c => c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId, token))
                    throw new InvalidOperationException("This purchase order already has a budget commitment.");

                var utilisation = await GetBudgetUtilizationAsync(companyId, fundCentreId, token);
                var exceedsBudget = utilisation.Available < amount;
                if (exceedsBudget && !allowOverride)
                    throw new InvalidOperationException(
                        $"Committing {amount:N2} would exceed available budget for {utilisation.Code} (available: {utilisation.Available:N2}). An authorised budget override is required.");

                var commitment = new Commitment
                {
                    CompanyId = companyId,
                    FundCentreId = fundCentreId,
                    PurchaseOrderId = purchaseOrderId,
                    Amount = amount,
                    Status = CommitmentStatus.Active,
                    CommittedDate = DateTime.UtcNow
                };
                _db.Commitments.Add(commitment);
                _audit.Record(companyId, actorId, allowOverride ? "BudgetCommitment.Override" : "BudgetCommitment.Created",
                    "PublicSector", nameof(Commitment), commitment.Id,
                    new { utilisation.Available, utilisation.Commitments, utilisation.Actuals },
                    new { amount, purchaseOrderId, fundCentreId, exceedsBudget, overrideReason }, ipAddress);
                await _db.SaveChangesAsync(token);
                return commitment;
            }, ct);
        }

        /// <summary>
        /// Applies a posted AP invoice's base-currency expense amount against its PO encumbrance.
        /// Partial invoices reduce the remaining commitment; the encumbrance closes only when fully realised.
        /// This operation shares the posting transaction so GL actuals and budget commitments cannot diverge.
        /// </summary>
        public Task RealiseCommitmentAsync(Guid companyId, Guid commitmentId, decimal realisedAmount, CancellationToken ct = default)
        {
            if (realisedAmount <= 0m) throw new InvalidOperationException("A realised commitment amount must be greater than zero.");
            return _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-commitment:{companyId:N}:{commitmentId:N}", token);
                var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.Id == commitmentId, token)
                    ?? throw new InvalidOperationException("Commitment not found in this company.");
                if (commitment.Status != CommitmentStatus.Active)
                    throw new InvalidOperationException("Only an active commitment can be realised.");
                var remaining = commitment.Amount - commitment.RealisedAmount;
                if (realisedAmount > remaining + 0.000001m)
                    throw new InvalidOperationException($"The invoice exceeds the remaining commitment ({remaining:N2}).");

                var previousRealisedAmount = commitment.RealisedAmount;
                commitment.RealisedAmount = Math.Min(commitment.Amount, commitment.RealisedAmount + realisedAmount);
                if (commitment.Amount - commitment.RealisedAmount <= 0.000001m)
                {
                    commitment.RealisedAmount = commitment.Amount;
                    commitment.Status = CommitmentStatus.Released;
                    commitment.ReleasedDate = DateTime.UtcNow;
                }
                _audit.Record(companyId, _db.CurrentUserId, "BudgetCommitment.Realised", "PublicSector",
                    nameof(Commitment), commitment.Id,
                    new { previousRealisedAmount },
                    new { invoiceRealisedAmount = realisedAmount, cumulativeRealisedAmount = commitment.RealisedAmount,
                        remainingCommitmentAmount = commitment.Amount - commitment.RealisedAmount });
                if (commitment.Status == CommitmentStatus.Released)
                    _audit.Record(companyId, _db.CurrentUserId, "BudgetCommitment.Released", "PublicSector",
                        nameof(Commitment), commitment.Id,
                        new { status = CommitmentStatus.Active, realisedAmount = previousRealisedAmount },
                        new { status = commitment.Status, realisedAmount = commitment.RealisedAmount,
                            releasedDate = commitment.ReleasedDate, reason = "Fully realised by posted AP invoices" });
                await _db.SaveChangesAsync(token);
                return true;
            }, ct);
        }

        /// <summary>Idempotently releases a PO encumbrance. Repeated release attempts are retained in the audit log.</summary>
        public Task ReleaseCommitmentForPoAsync(Guid companyId, Guid purchaseOrderId, CancellationToken ct = default) =>
            _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-po-commitment:{companyId:N}:{purchaseOrderId:N}", token);
                var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId, token);
                if (commitment == null) return false;
                await _db.AcquireTransactionLockAsync($"public-sector-commitment:{companyId:N}:{commitment.Id:N}", token);
                commitment = await _db.Commitments.FirstAsync(c => c.CompanyId == companyId && c.Id == commitment.Id, token);
                return await ReleaseCommitmentLockedAsync(commitment, "Purchase order release request", token);
            }, ct);

        /// <summary>Releases a specific encumbrance when explicitly requested, keyed by Commitment.Id.</summary>
        public Task ReleaseCommitmentAsync(Guid companyId, Guid commitmentId, CancellationToken ct = default) =>
            _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-commitment:{companyId:N}:{commitmentId:N}", token);
                var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.Id == commitmentId, token)
                    ?? throw new InvalidOperationException("Commitment not found in this company.");
                return await ReleaseCommitmentLockedAsync(commitment, "Explicit commitment release request", token);
            }, ct);

        private async Task<bool> ReleaseCommitmentLockedAsync(Commitment commitment, string reason, CancellationToken ct)
        {
            if (commitment.Status != CommitmentStatus.Active)
            {
                _audit.Record(commitment.CompanyId, _db.CurrentUserId, "BudgetCommitment.ReleaseNoOp", "PublicSector",
                    nameof(Commitment), commitment.Id,
                    new { status = commitment.Status, releasedDate = commitment.ReleasedDate },
                    new { reason, outcome = "No change; commitment is not active" });
                await _db.SaveChangesAsync(ct);
                return false;
            }

            var previousStatus = commitment.Status;
            var previousRealisedAmount = commitment.RealisedAmount;
            commitment.Status = CommitmentStatus.Released;
            commitment.ReleasedDate = DateTime.UtcNow;
            _audit.Record(commitment.CompanyId, _db.CurrentUserId, "BudgetCommitment.Released", "PublicSector",
                nameof(Commitment), commitment.Id,
                new { status = previousStatus, realisedAmount = previousRealisedAmount },
                new { status = commitment.Status, releasedDate = commitment.ReleasedDate, reason });
            await _db.SaveChangesAsync(ct);
            return true;
        }

        /// <summary>Fully reverses a commitment on PO cancellation; duplicate cancellation is audited as a no-op.</summary>
        public Task CancelCommitmentForPoAsync(Guid companyId, Guid purchaseOrderId, CancellationToken ct = default) =>
            _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-po-commitment:{companyId:N}:{purchaseOrderId:N}", token);
                var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId, token);
                if (commitment == null) return false;
                await _db.AcquireTransactionLockAsync($"public-sector-commitment:{companyId:N}:{commitment.Id:N}", token);
                commitment = await _db.Commitments.FirstAsync(c => c.CompanyId == companyId && c.Id == commitment.Id, token);
                if (commitment.Status != CommitmentStatus.Active)
                {
                    _audit.Record(companyId, _db.CurrentUserId, "BudgetCommitment.CancelNoOp", "PublicSector",
                        nameof(Commitment), commitment.Id,
                        new { status = commitment.Status, releasedDate = commitment.ReleasedDate },
                        new { reason = "Purchase order cancellation request; commitment is not active" });
                    await _db.SaveChangesAsync(token);
                    return false;
                }

                var previousStatus = commitment.Status;
                commitment.Status = CommitmentStatus.Cancelled;
                commitment.ReleasedDate = DateTime.UtcNow;
                _audit.Record(companyId, _db.CurrentUserId, "BudgetCommitment.Cancelled", "PublicSector",
                    nameof(Commitment), commitment.Id,
                    new { status = previousStatus, realisedAmount = commitment.RealisedAmount },
                    new { status = commitment.Status, releasedDate = commitment.ReleasedDate });
                await _db.SaveChangesAsync(token);
                return true;
            }, ct);

        public async Task<BudgetUtilization> GetBudgetUtilizationAsync(Guid companyId, Guid fundCentreId, CancellationToken ct = default)
        {
            var fc = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Fund centre not found.");

            var totalAppropriation = fc.AppropriationAmount + fc.CarriedForwardAmount;
            var commitments = await _db.Commitments
                .Where(c => c.CompanyId == companyId && c.FundCentreId == fundCentreId && c.Status == CommitmentStatus.Active)
                .SumAsync(c => c.Amount - c.RealisedAmount, ct);

            var fiscalStartMonth = await _db.Companies.Where(c => c.Id == companyId)
                .Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync(ct);
            var periodStart = FiscalCalendar.FiscalYearStart(new DateTime(fc.FinancialYear, 12, 31), fiscalStartMonth);
            var periodEnd = periodStart.AddYears(1);

            // The posted GL is the authoritative actuals ledger. Restrict it to this fund centre's
            // financial year; AP headers are not counted because their expense journals already are.
            var expenseAccountIds = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && a.Type == GlAccountType.Expense)
                .Select(a => a.Id)
                .ToListAsync(ct);
            var actuals = await _db.JournalLines
                .Where(l => expenseAccountIds.Contains(l.GlAccountId) &&
                    l.FundCentreId == fundCentreId && l.JournalEntry!.CompanyId == companyId &&
                    l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate < periodEnd &&
                    (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);

            // A closed financial-year fund centre has no spendable balance, even when the
            // unexpended appropriation is reported as lapsed or carried forward.
            var available = fc.IsClosed ? 0m : totalAppropriation - commitments - actuals;
            var pctUtilised = totalAppropriation == 0 ? 0 : Math.Round((commitments + actuals) / totalAppropriation * 100, 2);

            return new BudgetUtilization(fc.Id, fc.Code, fc.Name, totalAppropriation, commitments, actuals, available, pctUtilised, pctUtilised > 90);
        }

        /// <summary>
        /// Vote/program/sub-program expenditure schedule. Encumbrances are shown separately from
        /// realised commitments, while actual expenditure is sourced only from posted GL journals.
        /// This prevents AP headers or commitment realisations from being double-counted as actuals.
        /// </summary>
        public async Task<List<PublicSectorExpenditureLine>> GetExpenditureReportAsync(
            Guid companyId, int financialYear, CancellationToken ct = default)
        {
            if (financialYear < 2000 || financialYear > 2200)
                throw new InvalidOperationException("A valid financial year is required.");

            var fundCentres = await _db.FundCentres
                .Where(f => f.CompanyId == companyId && f.FinancialYear == financialYear)
                .OrderBy(f => f.Level).ThenBy(f => f.Code)
                .ToListAsync(ct);
            if (fundCentres.Count == 0) return new List<PublicSectorExpenditureLine>();

            var fundCentreIds = fundCentres.Select(f => f.Id).ToList();
            var commitmentTotals = await _db.Commitments
                .Where(c => c.CompanyId == companyId && fundCentreIds.Contains(c.FundCentreId) &&
                    c.Status != CommitmentStatus.Cancelled)
                .GroupBy(c => c.FundCentreId)
                .Select(g => new
                {
                    FundCentreId = g.Key,
                    Committed = g.Sum(c => c.Amount),
                    Realised = g.Sum(c => c.RealisedAmount),
                    Outstanding = g.Where(c => c.Status == CommitmentStatus.Active)
                        .Sum(c => c.Amount - c.RealisedAmount)
                })
                .ToDictionaryAsync(x => x.FundCentreId, ct);

            var fiscalStartMonth = await _db.Companies.Where(c => c.Id == companyId)
                .Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync(ct);
            var periodStart = FiscalCalendar.FiscalYearStart(new DateTime(financialYear, 12, 31), fiscalStartMonth);
            var periodEnd = periodStart.AddYears(1);
            var expenseAccountIds = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && a.Type == GlAccountType.Expense)
                .Select(a => a.Id)
                .ToListAsync(ct);
            var actualTotals = await _db.JournalLines
                .Where(l => l.FundCentreId.HasValue && fundCentreIds.Contains(l.FundCentreId.Value) &&
                    expenseAccountIds.Contains(l.GlAccountId) && l.JournalEntry!.CompanyId == companyId &&
                    l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate < periodEnd &&
                    (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                .GroupBy(l => l.FundCentreId!.Value)
                .Select(g => new { FundCentreId = g.Key, Actuals = g.Sum(l => l.BaseDebit - l.BaseCredit) })
                .ToDictionaryAsync(x => x.FundCentreId, x => x.Actuals, ct);

            var byId = fundCentres.ToDictionary(f => f.Id);
            var report = new List<PublicSectorExpenditureLine>(fundCentres.Count);
            foreach (var fundCentre in fundCentres)
            {
                commitmentTotals.TryGetValue(fundCentre.Id, out var commitments);
                actualTotals.TryGetValue(fundCentre.Id, out var actuals);
                byId.TryGetValue(fundCentre.ParentId ?? Guid.Empty, out var parent);
                var appropriation = fundCentre.AppropriationAmount;
                var carriedForward = fundCentre.CarriedForwardAmount;
                var totalBudget = appropriation + carriedForward;
                var committed = commitments?.Committed ?? 0m;
                var realised = commitments?.Realised ?? 0m;
                var outstanding = commitments?.Outstanding ?? 0m;
                // Actual variance is appropriation less posted expense actuals (positive is
                // favourable underspend; negative is an overspend). Outstanding encumbrances
                // remain separately visible and are reflected in AvailableBudget, not actuals.
                var actualVariance = totalBudget - actuals;
                var actualVariancePercent = totalBudget == 0m ? 0m :
                    Math.Round(actualVariance / totalBudget * 100m, 2);
                var available = fundCentre.IsClosed ? 0m : totalBudget - outstanding - actuals;
                var utilised = totalBudget == 0m ? 0m :
                    Math.Round((outstanding + actuals) / totalBudget * 100m, 2);

                report.Add(new PublicSectorExpenditureLine(
                    fundCentre.Id, fundCentre.Code, fundCentre.Name, fundCentre.Level,
                    fundCentre.ParentId, parent?.Code, parent?.Name,
                    appropriation, carriedForward, totalBudget, committed, realised, outstanding,
                    actuals, actualVariance, actualVariancePercent, available, utilised,
                    fundCentre.LapsedAmount, fundCentre.IsClosed));
            }
            return report;
        }

        public async Task<List<BudgetUtilization>> GetAllBudgetUtilizationAsync(Guid companyId, int financialYear, CancellationToken ct = default)
        {
            var fundCentres = await _db.FundCentres.Where(f => f.CompanyId == companyId && f.FinancialYear == financialYear).ToListAsync(ct);
            var result = new List<BudgetUtilization>();
            foreach (var fc in fundCentres) result.Add(await GetBudgetUtilizationAsync(companyId, fc.Id, ct));
            return result;
        }

        /// <summary>Year-end workflow: computes the unexpended balance and either carries it forward
        /// (creating next year's fund centre pre-seeded with that balance) or lapses it to the
        /// appropriation surplus, per policy.</summary>
        public async Task<YearEndCloseResult> CloseYearEndAsync(Guid companyId, Guid fundCentreId, bool carryForward, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-year-end:{companyId:N}:{fundCentreId:N}", token);
                var fc = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Fund centre not found.");
                if (fc.IsClosed) throw new InvalidOperationException("This fund centre is already closed.");

                var utilisation = await GetBudgetUtilizationAsync(companyId, fundCentreId, token);
                var unexpended = utilisation.Available;
                var beforeClose = new
                {
                    fc.Code,
                    fc.Name,
                    fc.Level,
                    fc.FinancialYear,
                    fc.AppropriationAmount,
                    fc.CarriedForwardAmount,
                    utilisation.Commitments,
                    utilisation.Actuals,
                    utilisation.Available,
                    RequestedCarryForward = carryForward
                };

                Guid? nextParentId = null;
                if (carryForward && unexpended > 0 && fc.ParentId.HasValue)
                {
                    var priorParent = await _db.FundCentres.FirstOrDefaultAsync(
                        f => f.Id == fc.ParentId.Value && f.CompanyId == companyId, token)
                        ?? throw new InvalidOperationException("The parent fund centre could not be found.");
                    var nextParent = await _db.FundCentres.FirstOrDefaultAsync(
                        f => f.CompanyId == companyId && f.Code == priorParent.Code &&
                             f.Level == priorParent.Level && f.FinancialYear == fc.FinancialYear + 1, token);
                    if (nextParent == null)
                        throw new InvalidOperationException(
                            "Carry forward the parent fund centre first so the next-year hierarchy remains intact.");
                    nextParentId = nextParent.Id;
                }

                Guid? newFundCentreId = null;
                if (carryForward && unexpended > 0)
                {
                    var alreadyExists = await _db.FundCentres.AnyAsync(
                        f => f.CompanyId == companyId && f.Code == fc.Code &&
                             f.FinancialYear == fc.FinancialYear + 1, token);
                    if (alreadyExists)
                        throw new InvalidOperationException("A next-year fund centre with this code already exists.");

                    var next = new FundCentre
                    {
                        CompanyId = companyId,
                        Code = fc.Code,
                        Name = fc.Name,
                        Level = fc.Level,
                        ParentId = nextParentId,
                        FinancialYear = fc.FinancialYear + 1,
                        AppropriationAmount = 0,
                        CarriedForwardAmount = unexpended
                    };
                    _db.FundCentres.Add(next);
                    fc.CarriedForwardAmount = 0; // moved forward, not double-counted in the old year
                    newFundCentreId = next.Id;
                }
                else if (unexpended > 0)
                {
                    fc.LapsedAmount = unexpended;
                }

                fc.IsClosed = true;
                _audit.Record(companyId, _db.CurrentUserId, "PublicSectorYearEnd.Closed", "PublicSector",
                    nameof(FundCentre), fc.Id,
                    beforeClose,
                    new
                    {
                        fc.IsClosed,
                        fc.CarriedForwardAmount,
                        fc.LapsedAmount,
                        UnexpendedBalance = unexpended,
                        CarriedForward = carryForward ? unexpended : 0m,
                        Lapsed = carryForward ? 0m : Math.Max(unexpended, 0m),
                        NewFundCentreId = newFundCentreId
                    });
                await _db.SaveChangesAsync(token);

                return new YearEndCloseResult(fundCentreId, unexpended,
                    carryForward ? unexpended : 0, carryForward ? 0 : Math.Max(unexpended, 0), newFundCentreId);
            }, ct);
        }

        /// <summary>
        /// Captures a cash-basis actual from a reconciled bank movement or an explicitly mapped
        /// AR/AP invoice-line allocation. Split AR/AP sources are recorded once per budget line;
        /// the complete allocation set must reconcile to the source amount before certification.
        /// </summary>
        public async Task<PublicSectorBudgetExecutionActual> CreateBudgetExecutionActualAsync(
            Guid companyId, Guid budgetVersionId, Guid budgetVersionLineId,
            PublicSectorBudgetActualEventKind eventKind, PublicSectorBudgetActualSourceKind sourceKind,
            Guid sourceRecordId, DateTime effectiveDate, CashFlowCategory cashFlowSection,
            Guid actorId, CancellationToken ct = default)
        {
            var version = await _db.PublicSectorBudgetVersions.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionId && x.CompanyId == companyId &&
                x.Status == PublicSectorBudgetVersionStatus.Approved, ct)
                ?? throw new InvalidOperationException("An approved budget version is required.");
            if (version.AccountingBasis != AccountingBasis.Cash)
                throw new InvalidOperationException("This capture endpoint is restricted to approved cash-basis budgets. Modified-cash treatment must be explicitly configured before it can be enabled.");
            var latestApprovedVersionId = await _db.PublicSectorBudgetVersions.Where(x =>
                    x.CompanyId == companyId && x.FinancialYear == version.FinancialYear &&
                    x.Status == PublicSectorBudgetVersionStatus.Approved && x.AccountingBasis == AccountingBasis.Cash)
                .OrderByDescending(x => x.Kind).ThenByDescending(x => x.VersionNumber)
                .Select(x => x.Id).FirstAsync(ct);
            if (latestApprovedVersionId != version.Id)
                throw new InvalidOperationException("Cash actuals must be mapped to the latest approved budget version. Complete and approve the final revision before capturing execution actuals.");
            var line = await _db.PublicSectorBudgetVersionLines.FirstOrDefaultAsync(x =>
                x.Id == budgetVersionLineId && x.BudgetVersionId == version.Id &&
                x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("The budget line does not belong to the selected approved budget version.");
            if (effectiveDate < version.PeriodStart || effectiveDate >= version.PeriodEnd)
                throw new InvalidOperationException("The effective date falls outside the approved budget reporting period.");
            if (cashFlowSection is not (CashFlowCategory.Operating or CashFlowCategory.Investing or CashFlowCategory.Financing))
                throw new InvalidOperationException("A valid operating, investing or financing cash-flow classification is required.");
            var account = await _db.GlAccounts.FirstOrDefaultAsync(x =>
                x.Id == line.GlAccountId && x.CompanyId == companyId && x.IsActive, ct)
                ?? throw new InvalidOperationException("The budget line GL account is unavailable.");
            if (account.CashFlowCategory != cashFlowSection)
                throw new InvalidOperationException("The selected cash-flow section does not match the budget line GL account's configured cash-flow classification.");

            var existingSourceAllocations = await _db.PublicSectorBudgetExecutionActuals.Where(x =>
                x.CompanyId == companyId && x.SourceKind == sourceKind &&
                x.SourceRecordId == sourceRecordId && !x.ReversalOfId.HasValue &&
                x.Status != PublicSectorBudgetActualStatus.Reversed).ToListAsync(ct);
            if (await _db.PublicSectorCashSourceReconciliationLines.AnyAsync(line =>
                line.CompanyId == companyId && line.SourceRecordId == sourceRecordId &&
                _db.PublicSectorCashSourceSubmissions.Any(submission =>
                    submission.Id == line.CashSourceSubmissionId && submission.CompanyId == companyId &&
                    submission.BudgetVersionId == version.Id && submission.Status != "Rejected" &&
                    submission.RecordLevelStatus != "Rejected" &&
                    submission.SubmissionRevision == _db.PublicSectorCashSourceSubmissions
                        .Where(latest => latest.CompanyId == companyId &&
                            latest.ExpectedCashSourceId == submission.ExpectedCashSourceId)
                        .Max(latest => latest.SubmissionRevision)), ct))
                throw new InvalidOperationException("This source record has already been reconciled or excluded in the latest active source revision. Complete a replacement source revision before creating a new actual.");
            if (sourceKind == PublicSectorBudgetActualSourceKind.BankTransaction && existingSourceAllocations.Count > 0)
                throw new InvalidOperationException("A bank transaction is captured once against its exact posted journal classification; only AR/AP invoice settlements support split source allocation.");
            if (existingSourceAllocations.Any(x => x.BudgetVersionLineId == line.Id))
                throw new InvalidOperationException("This source transaction has already been allocated to the selected budget line.");
            if (existingSourceAllocations.Any(x => x.Status == PublicSectorBudgetActualStatus.Certified))
                throw new InvalidOperationException("A source transaction cannot receive additional allocations after any allocation has been certified.");

            decimal amount;
            string currency;
            DateTime sourceDate;
            string sourceReference;
            if (eventKind != PublicSectorBudgetActualEventKind.CashReceipt &&
                eventKind != PublicSectorBudgetActualEventKind.CashPayment)
                throw new InvalidOperationException("A supported cash receipt or cash payment event type is required.");
            var isReceipt = eventKind == PublicSectorBudgetActualEventKind.CashReceipt;

            if (sourceKind == PublicSectorBudgetActualSourceKind.BankTransaction)
            {
                var source = await _db.BankTransactions.FirstOrDefaultAsync(x =>
                    x.Id == sourceRecordId && x.CompanyId == companyId && x.IsReconciled, ct)
                    ?? throw new InvalidOperationException("A reconciled bank transaction is required as cash evidence.");
                if (!source.JournalEntryId.HasValue || !await _db.JournalEntries.AnyAsync(x =>
                    x.Id == source.JournalEntryId.Value && x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct))
                    throw new InvalidOperationException("The reconciled bank transaction must be linked to a posted journal entry.");
                amount = isReceipt ? source.Credit : source.Debit;
                if (amount <= 0m || (isReceipt ? source.Debit != 0m : source.Credit != 0m))
                    throw new InvalidOperationException("The bank transaction direction and amount do not match the selected cash event type.");
                var mappedJournalAmount = await _db.JournalLines.Where(x =>
                    x.JournalEntryId == source.JournalEntryId.Value && x.GlAccountId == line.GlAccountId &&
                    x.FundCentreId == line.FundCentreId && x.CostCentreId == line.CostCentreId)
                    .SumAsync(x => isReceipt ? x.Credit - x.Debit : x.Debit - x.Credit, ct);
                if (mappedJournalAmount != amount)
                    throw new InvalidOperationException("The posted journal does not contain an exact matching amount for this budget line, Fund Centre and cost centre.");
                var bank = await _db.BankAccounts.FirstOrDefaultAsync(x =>
                    x.Id == source.BankAccountId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The source bank account is not available to this company.");
                currency = bank.Currency;
                sourceDate = source.TransactionDate;
                sourceReference = string.IsNullOrWhiteSpace(source.Reference) ? source.Id.ToString("N") : source.Reference!;
            }
            else if (sourceKind == PublicSectorBudgetActualSourceKind.ArReceipt && isReceipt)
            {
                var receipt = await _db.ArReceipts.FirstOrDefaultAsync(x =>
                    x.Id == sourceRecordId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The AR receipt was not found in this company.");
                if (!receipt.JournalEntryId.HasValue ||
                    !await _db.JournalEntries.AnyAsync(x => x.Id == receipt.JournalEntryId.Value &&
                        x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct))
                    throw new InvalidOperationException("The AR receipt must be posted to the general ledger before budget capture.");
                var allocations = await _db.ArReceiptAllocations.Where(x => x.ArReceiptId == receipt.Id).ToListAsync(ct);
                if (allocations.Count == 0 || allocations.Any(x => x.AmountApplied <= 0m) ||
                    allocations.Select(x => x.ArInvoiceId).Distinct().Count() != allocations.Count ||
                    allocations.Sum(x => x.AmountApplied) != receipt.Amount)
                    throw new InvalidOperationException("The AR receipt must be fully allocated across its invoice settlements before IPSAS 24 capture.");
                var invoiceIds = allocations.Select(x => x.ArInvoiceId).Distinct().ToArray();
                var invoices = await _db.ArInvoices.Where(x =>
                    x.CompanyId == companyId && invoiceIds.Contains(x.Id)).ToListAsync(ct);
                if (invoices.Count != invoiceIds.Length)
                    throw new InvalidOperationException("Every AR receipt allocation must reference an invoice belonging to this company.");
                amount = 0m;
                foreach (var allocation in allocations)
                {
                    var invoice = invoices.Single(x => x.Id == allocation.ArInvoiceId);
                    if (invoice.CustomerId != receipt.CustomerId ||
                        (invoice.Status != InvoiceStatus.Paid && invoice.Status != InvoiceStatus.Partial) ||
                        !invoice.FundCentreId.HasValue || invoice.Currency != receipt.Currency ||
                        invoice.Total < allocation.AmountApplied || invoice.Outstanding < 0m ||
                        (invoice.Status == InvoiceStatus.Paid && invoice.Outstanding > 0.000001m) ||
                        (invoice.Status == InvoiceStatus.Partial && invoice.Outstanding <= 0m) ||
                        invoice.VatAmount != 0m || !invoice.JournalEntryId.HasValue ||
                        !await _db.JournalEntries.AnyAsync(x => x.Id == invoice.JournalEntryId.Value &&
                            x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct))
                        throw new InvalidOperationException("Split AR capture currently requires fully settled, tax-free invoices with an explicit Fund Centre and posted revenue journals.");
                    var invoiceLines = await _db.ArInvoiceLines.Where(x => x.ArInvoiceId == invoice.Id).ToListAsync(ct);
                    if (invoiceLines.Count == 0 || invoiceLines.Any(x => x.LineVat != 0m) ||
                        invoiceLines.Sum(x => x.LineTotal) != invoice.Total)
                        throw new InvalidOperationException("AR invoice lines must reconcile exactly to the fully settled tax-free invoice total.");
                    var invoiceGlAccounts = invoiceLines.Select(x => x.GlAccountId).Distinct().ToArray();
                    if (allocation.AmountApplied < invoice.Total && invoiceGlAccounts.Length > 1)
                        throw new InvalidOperationException("A partial AR settlement across multiple GL classifications requires explicit invoice-line allocation. The automatic adapter supports partial settlement only when the invoice has one GL classification.");
                    var expectedByAccount = allocation.AmountApplied < invoice.Total
                        ? invoiceGlAccounts[0] == line.GlAccountId ? allocation.AmountApplied : 0m
                        : invoiceLines.Where(x => x.GlAccountId == line.GlAccountId).Sum(x => x.LineTotal);
                    if (invoice.FundCentreId == line.FundCentreId && expectedByAccount > 0m)
                    {
                        var postedRevenue = await _db.JournalLines.Where(x =>
                            x.JournalEntryId == invoice.JournalEntryId.Value &&
                            x.GlAccountId == line.GlAccountId && x.FundCentreId == line.FundCentreId &&
                            x.CostCentreId == null)
                            .SumAsync(x => x.Credit - x.Debit, ct);
                        if (postedRevenue != invoiceLines.Where(x => x.GlAccountId == line.GlAccountId)
                            .Sum(x => x.LineTotal))
                            throw new InvalidOperationException("The posted AR invoice revenue journal does not support the selected GL/Fund Centre allocation.");
                        amount += expectedByAccount;
                    }
                }
                if (amount <= 0m || line.CostCentreId.HasValue)
                    throw new InvalidOperationException("The selected budget line has no supported AR invoice-line allocation. Cost-centre, VAT and withholding-tax allocations require explicit line-level mapping.");
                var receiptBankCandidates = await _db.BankAccounts.Where(x =>
                    x.CompanyId == companyId && x.Currency == receipt.Currency &&
                    _db.JournalLines.Any(j => j.JournalEntryId == receipt.JournalEntryId.Value &&
                        j.GlAccountId == x.GlAccountId && j.Debit - j.Credit == receipt.Amount)).ToListAsync(ct);
                var bank = receiptBankCandidates.Count == 1 ? receiptBankCandidates[0] : null;
                if (bank == null)
                    throw new InvalidOperationException("The posted AR receipt must identify exactly one company bank account through its bank GL journal leg and currency.");
                var bankLeg = await _db.JournalLines.Where(x =>
                    x.JournalEntryId == receipt.JournalEntryId.Value && x.GlAccountId == bank.GlAccountId)
                    .SumAsync(x => x.Debit - x.Credit, ct);
                if (bankLeg != receipt.Amount)
                    throw new InvalidOperationException("The posted AR receipt journal does not contain the exact bank debit for the receipt amount.");
                currency = receipt.Currency;
                sourceDate = receipt.ReceiptDate;
                sourceReference = string.IsNullOrWhiteSpace(receipt.ReceiptNo) ? receipt.Id.ToString("N") : receipt.ReceiptNo;
            }
            else if (sourceKind == PublicSectorBudgetActualSourceKind.ApPayment && !isReceipt)
            {
                var payment = await _db.ApPayments.FirstOrDefaultAsync(x =>
                    x.Id == sourceRecordId && x.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The AP payment was not found in this company.");
                if (!payment.JournalEntryId.HasValue ||
                    !await _db.JournalEntries.AnyAsync(x => x.Id == payment.JournalEntryId.Value &&
                        x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct))
                    throw new InvalidOperationException("The AP payment must be posted to the general ledger before budget capture.");
                if (!payment.PreparedBy.HasValue || !payment.AuthorisedBy.HasValue ||
                    payment.PreparedBy == payment.AuthorisedBy || !payment.AuthorisedAt.HasValue)
                    throw new InvalidOperationException("The AP payment requires a recorded independent authorisation before budget capture.");
                var allocations = await _db.ApPaymentAllocations.Where(x => x.ApPaymentId == payment.Id).ToListAsync(ct);
                if (allocations.Count == 0 || allocations.Any(x => x.AmountApplied <= 0m) ||
                    allocations.Select(x => x.ApInvoiceId).Distinct().Count() != allocations.Count ||
                    allocations.Sum(x => x.AmountApplied) != payment.Amount)
                    throw new InvalidOperationException("The AP payment must be fully allocated across its invoice settlements before IPSAS 24 capture.");
                var invoiceIds = allocations.Select(x => x.ApInvoiceId).Distinct().ToArray();
                var invoices = await _db.ApInvoices.Where(x =>
                    x.CompanyId == companyId && invoiceIds.Contains(x.Id)).ToListAsync(ct);
                if (invoices.Count != invoiceIds.Length)
                    throw new InvalidOperationException("Every AP payment allocation must reference an invoice belonging to this company.");
                amount = 0m;
                foreach (var allocation in allocations)
                {
                    var invoice = invoices.Single(x => x.Id == allocation.ApInvoiceId);
                    if (invoice.SupplierId != payment.SupplierId ||
                        (invoice.Status != InvoiceStatus.Paid && invoice.Status != InvoiceStatus.Partial) ||
                        !invoice.FundCentreId.HasValue || invoice.Currency != payment.Currency ||
                        invoice.Total < allocation.AmountApplied || invoice.Outstanding < 0m ||
                        (invoice.Status == InvoiceStatus.Paid && invoice.Outstanding > 0.000001m) ||
                        (invoice.Status == InvoiceStatus.Partial && invoice.Outstanding <= 0m) ||
                        invoice.VatAmount != 0m || invoice.WhtAmount != 0m || !invoice.JournalEntryId.HasValue ||
                        !await _db.JournalEntries.AnyAsync(x => x.Id == invoice.JournalEntryId.Value &&
                            x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct))
                        throw new InvalidOperationException("Split AP capture currently requires fully settled, tax-free invoices with an explicit Fund Centre and posted expense journals.");
                    var invoiceLines = await _db.ApInvoiceLines.Where(x => x.ApInvoiceId == invoice.Id).ToListAsync(ct);
                    if (invoiceLines.Count == 0 || invoiceLines.Any(x => x.LineVat != 0m) ||
                        invoiceLines.Sum(x => x.LineTotal) != invoice.Total)
                        throw new InvalidOperationException("AP invoice lines must reconcile exactly to the fully settled tax-free invoice total.");
                    var invoiceGlAccounts = invoiceLines.Select(x => x.GlAccountId).Distinct().ToArray();
                    if (allocation.AmountApplied < invoice.Total && invoiceGlAccounts.Length > 1)
                        throw new InvalidOperationException("A partial AP settlement across multiple GL classifications requires explicit invoice-line allocation. The automatic adapter supports partial settlement only when the invoice has one GL classification.");
                    var expectedByAccount = allocation.AmountApplied < invoice.Total
                        ? invoiceGlAccounts[0] == line.GlAccountId ? allocation.AmountApplied : 0m
                        : invoiceLines.Where(x => x.GlAccountId == line.GlAccountId).Sum(x => x.LineTotal);
                    if (invoice.FundCentreId == line.FundCentreId && expectedByAccount > 0m)
                    {
                        var postedExpense = await _db.JournalLines.Where(x =>
                            x.JournalEntryId == invoice.JournalEntryId.Value &&
                            x.GlAccountId == line.GlAccountId && x.FundCentreId == line.FundCentreId &&
                            x.CostCentreId == null)
                            .SumAsync(x => x.Debit - x.Credit, ct);
                        if (postedExpense != invoiceLines.Where(x => x.GlAccountId == line.GlAccountId)
                            .Sum(x => x.LineTotal))
                            throw new InvalidOperationException("The posted AP invoice expense journal does not support the selected GL/Fund Centre allocation.");
                        amount += expectedByAccount;
                    }
                }
                if (amount <= 0m || line.CostCentreId.HasValue)
                    throw new InvalidOperationException("The selected budget line has no supported AP invoice-line allocation. Cost-centre, VAT and withholding-tax allocations require explicit line-level mapping.");
                var paymentBankCandidates = await _db.BankAccounts.Where(x =>
                    x.CompanyId == companyId && x.Currency == payment.Currency &&
                    _db.JournalLines.Any(j => j.JournalEntryId == payment.JournalEntryId.Value &&
                        j.GlAccountId == x.GlAccountId && j.Credit - j.Debit == payment.Amount)).ToListAsync(ct);
                var bank = paymentBankCandidates.Count == 1 ? paymentBankCandidates[0] : null;
                if (bank == null)
                    throw new InvalidOperationException("The posted AP payment must identify exactly one company bank account through its bank GL journal leg and currency.");
                var bankLeg = await _db.JournalLines.Where(x =>
                    x.JournalEntryId == payment.JournalEntryId.Value && x.GlAccountId == bank.GlAccountId)
                    .SumAsync(x => x.Credit - x.Debit, ct);
                if (bankLeg != payment.Amount)
                    throw new InvalidOperationException("The posted AP payment journal does not contain the exact bank credit for the payment amount.");
                currency = payment.Currency;
                sourceDate = payment.PaymentDate;
                sourceReference = string.IsNullOrWhiteSpace(payment.PaymentNo) ? payment.Id.ToString("N") : payment.PaymentNo;
            }
            else
            {
                throw new InvalidOperationException("The selected cash event type is not supported by the specified source adapter.");
            }

            if (effectiveDate.Date != sourceDate.Date)
                throw new InvalidOperationException("The budget effective date must match the reconciled bank transaction date. Report timing differences through the separately approved IPSAS 24 reconciliation workflow.");
            if (amount <= 0m || !string.Equals(currency, version.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source amount must be positive and its currency must match the approved budget currency. Foreign-currency conversion requires approved rate provenance and is not enabled in this phase.");
            var actual = new PublicSectorBudgetExecutionActual
            {
                CompanyId = companyId, FinancialYear = version.FinancialYear,
                BudgetVersionId = version.Id, BudgetVersionLineId = line.Id,
                FundCentreId = line.FundCentreId, GlAccountId = line.GlAccountId, CostCentreId = line.CostCentreId,
                EventKind = eventKind, SourceKind = sourceKind, SourceRecordId = sourceRecordId,
                SourceReference = sourceReference, SourceDate = sourceDate, EffectiveDate = effectiveDate,
                Currency = currency.ToUpperInvariant(), Amount = amount, ExchangeRateToBudgetCurrency = 1m,
                CashFlowSection = cashFlowSection, Status = PublicSectorBudgetActualStatus.Draft,
                CreatedBy = actorId
            };
            _db.PublicSectorBudgetExecutionActuals.Add(actual);
            _audit.Record(companyId, actorId, "PublicSectorBudgetExecutionActual.Created", "PublicSector",
                nameof(PublicSectorBudgetExecutionActual), actual.Id, null,
                new { actual.FinancialYear, actual.BudgetVersionId, actual.BudgetVersionLineId,
                    actual.EventKind, actual.SourceKind, actual.SourceRecordId, actual.SourceReference,
                    actual.SourceDate, actual.EffectiveDate, actual.Currency, actual.Amount, actual.CashFlowSection });
            await _db.SaveChangesAsync(ct);
            return actual;
        }

        public async Task<PublicSectorBudgetExecutionActual> SubmitBudgetExecutionActualAsync(
            Guid companyId, Guid actualId, Guid actorId, CancellationToken ct = default)
        {
            var actual = await _db.PublicSectorBudgetExecutionActuals.FirstOrDefaultAsync(x =>
                x.Id == actualId && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Budget execution actual not found.");
            if (actual.Status != PublicSectorBudgetActualStatus.Draft)
                throw new InvalidOperationException("Only a draft budget actual can be submitted for certification.");
            actual.Status = PublicSectorBudgetActualStatus.PendingCertification;
            _audit.Record(companyId, actorId, "PublicSectorBudgetExecutionActual.Submitted", "PublicSector",
                nameof(PublicSectorBudgetExecutionActual), actual.Id,
                new { Status = PublicSectorBudgetActualStatus.Draft },
                new { Status = actual.Status, actual.SourceKind, actual.SourceRecordId });
            await _db.SaveChangesAsync(ct);
            return actual;
        }

        public async Task<PublicSectorBudgetExecutionActual> CertifyBudgetExecutionActualAsync(
            Guid companyId, Guid actualId, Guid certifierId, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                var actual = await _db.PublicSectorBudgetExecutionActuals.FirstOrDefaultAsync(x =>
                    x.Id == actualId && x.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Budget execution actual not found.");
                if (actual.Status != PublicSectorBudgetActualStatus.PendingCertification)
                    throw new InvalidOperationException("Only a pending budget actual can be certified.");
                if (actual.CreatedBy == certifierId)
                    throw new InvalidOperationException("The person who captured the budget actual cannot certify it.");
                if (!actual.ReversalOfId.HasValue &&
                    actual.SourceKind is PublicSectorBudgetActualSourceKind.ArReceipt or PublicSectorBudgetActualSourceKind.ApPayment)
                {
                    var sourceAllocations = await _db.PublicSectorBudgetExecutionActuals.Where(x =>
                        x.CompanyId == companyId && x.SourceKind == actual.SourceKind &&
                        x.SourceRecordId == actual.SourceRecordId).ToListAsync(token);
                    var sourceAmount = actual.SourceKind == PublicSectorBudgetActualSourceKind.ArReceipt
                        ? await _db.ArReceipts.Where(x => x.Id == actual.SourceRecordId && x.CompanyId == companyId)
                            .Select(x => (decimal?)x.Amount).FirstOrDefaultAsync(token)
                        : await _db.ApPayments.Where(x => x.Id == actual.SourceRecordId && x.CompanyId == companyId)
                            .Select(x => (decimal?)x.Amount).FirstOrDefaultAsync(token);
                    if (!sourceAmount.HasValue || sourceAllocations.Sum(x => x.Amount) != sourceAmount.Value)
                        throw new InvalidOperationException("All invoice-line allocations for this receipt/payment must be captured and reconcile exactly to the source cash amount before any allocation can be certified.");
                }
                actual.Status = PublicSectorBudgetActualStatus.Certified;
                actual.CertifiedBy = certifierId;
                actual.CertifiedAt = DateTime.UtcNow;
                _audit.Record(companyId, certifierId, "PublicSectorBudgetExecutionActual.Certified", "PublicSector",
                    nameof(PublicSectorBudgetExecutionActual), actual.Id,
                    new { Status = PublicSectorBudgetActualStatus.PendingCertification },
                    new { actual.Status, actual.CertifiedBy, actual.CertifiedAt, actual.SourceKind, actual.SourceRecordId });
                await _db.SaveChangesAsync(token);
                return actual;
            }, ct);
        }

        public async Task<IReadOnlyList<PublicSectorBudgetExecutionActual>> GetBudgetExecutionActualsAsync(
            Guid companyId, int financialYear, CancellationToken ct = default)
        {
            return await _db.PublicSectorBudgetExecutionActuals.Where(x =>
                x.CompanyId == companyId && x.FinancialYear == financialYear)
                .OrderBy(x => x.EffectiveDate).ThenBy(x => x.SourceReference).ToListAsync(ct);
        }

        public async Task<bool> IsCertifiedCashActualSourceValidAsync(
            Guid companyId, PublicSectorBudgetExecutionActual actual, CancellationToken ct)
        {
            var isReceipt = actual.EventKind == PublicSectorBudgetActualEventKind.CashReceipt;
            if (actual.EventKind != PublicSectorBudgetActualEventKind.CashReceipt &&
                actual.EventKind != PublicSectorBudgetActualEventKind.CashPayment)
                return false;
            var accountValid = await _db.GlAccounts.AnyAsync(x =>
                x.Id == actual.GlAccountId && x.CompanyId == companyId && x.IsActive &&
                x.CashFlowCategory == actual.CashFlowSection, ct);
            if (!accountValid) return false;

            if (actual.SourceKind == PublicSectorBudgetActualSourceKind.BankTransaction)
            {
                var source = await _db.BankTransactions.FirstOrDefaultAsync(x =>
                    x.Id == actual.SourceRecordId && x.CompanyId == companyId && x.IsReconciled, ct);
                if (source == null || !source.JournalEntryId.HasValue) return false;
                var bank = await _db.BankAccounts.AnyAsync(x =>
                    x.Id == source.BankAccountId && x.CompanyId == companyId &&
                    x.Currency == actual.Currency, ct);
                if (!bank || (isReceipt
                    ? source.Credit != actual.Amount || source.Debit != 0m
                    : source.Debit != actual.Amount || source.Credit != 0m)) return false;
                var posted = await _db.JournalEntries.AnyAsync(x =>
                    x.Id == source.JournalEntryId.Value && x.CompanyId == companyId &&
                    x.Status == JournalStatus.Posted, ct);
                if (!posted) return false;
                var mapped = await _db.JournalLines.Where(x =>
                    x.JournalEntryId == source.JournalEntryId.Value && x.GlAccountId == actual.GlAccountId &&
                    x.FundCentreId == actual.FundCentreId && x.CostCentreId == actual.CostCentreId)
                    .SumAsync(x => isReceipt ? x.Credit - x.Debit : x.Debit - x.Credit, ct);
                var bankReference = string.IsNullOrWhiteSpace(source.Reference) ? source.Id.ToString("N") : source.Reference!;
                return mapped == actual.Amount && source.TransactionDate.Date == actual.SourceDate.Date &&
                    actual.EffectiveDate.Date == source.TransactionDate.Date &&
                    string.Equals(actual.SourceReference, bankReference, StringComparison.Ordinal);
            }

            if (actual.SourceKind == PublicSectorBudgetActualSourceKind.ArReceipt && isReceipt)
            {
                var receipt = await _db.ArReceipts.FirstOrDefaultAsync(x =>
                    x.Id == actual.SourceRecordId && x.CompanyId == companyId, ct);
                if (receipt == null || !receipt.JournalEntryId.HasValue ||
                    receipt.Currency != actual.Currency || receipt.ReceiptDate.Date != actual.SourceDate.Date ||
                    actual.EffectiveDate.Date != receipt.ReceiptDate.Date)
                    return false;
                var allocations = await _db.ArReceiptAllocations.Where(x => x.ArReceiptId == receipt.Id).ToListAsync(ct);
                if (allocations.Count == 0 || allocations.Any(x => x.AmountApplied <= 0m) ||
                    allocations.Sum(x => x.AmountApplied) != receipt.Amount) return false;
                var invoiceIds = allocations.Select(x => x.ArInvoiceId).Distinct().ToArray();
                var invoices = await _db.ArInvoices.Where(x => x.CompanyId == companyId && invoiceIds.Contains(x.Id)).ToListAsync(ct);
                if (invoices.Count != invoiceIds.Length) return false;
                decimal expectedAllocation = 0m;
                foreach (var allocation in allocations)
                {
                    var invoice = invoices.Single(x => x.Id == allocation.ArInvoiceId);
                    if (invoice.CustomerId != receipt.CustomerId ||
                        (invoice.Status != InvoiceStatus.Paid && invoice.Status != InvoiceStatus.Partial) ||
                        !invoice.FundCentreId.HasValue || invoice.Currency != receipt.Currency ||
                        invoice.Total < allocation.AmountApplied || invoice.Outstanding < 0m ||
                        (invoice.Status == InvoiceStatus.Paid && invoice.Outstanding > 0.000001m) ||
                        (invoice.Status == InvoiceStatus.Partial && invoice.Outstanding <= 0m) ||
                        invoice.VatAmount != 0m || !invoice.JournalEntryId.HasValue)
                        return false;
                    var invoiceLines = await _db.ArInvoiceLines.Where(x => x.ArInvoiceId == invoice.Id).ToListAsync(ct);
                    if (invoiceLines.Count == 0 || invoiceLines.Any(x => x.LineVat != 0m) ||
                        invoiceLines.Sum(x => x.LineTotal) != invoice.Total) return false;
                    var invoiceGlAccounts = invoiceLines.Select(x => x.GlAccountId).Distinct().ToArray();
                    var partialSettlement = allocation.AmountApplied < invoice.Total;
                    if (partialSettlement && invoiceGlAccounts.Length > 1) return false;
                    var postedLineAmount = invoiceLines.Where(x => x.GlAccountId == actual.GlAccountId).Sum(x => x.LineTotal);
                    var lineAmount = invoice.FundCentreId == actual.FundCentreId
                        ? partialSettlement
                            ? invoiceGlAccounts[0] == actual.GlAccountId ? allocation.AmountApplied : 0m
                            : postedLineAmount
                        : 0m;
                    if (lineAmount <= 0m) continue;
                    var invoicePosted = await _db.JournalEntries.AnyAsync(x =>
                        x.Id == invoice.JournalEntryId.Value && x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct);
                    if (!invoicePosted) return false;
                    var revenue = await _db.JournalLines.Where(x =>
                        x.JournalEntryId == invoice.JournalEntryId.Value && x.GlAccountId == actual.GlAccountId &&
                        x.FundCentreId == actual.FundCentreId && x.CostCentreId == null)
                        .SumAsync(x => x.Credit - x.Debit, ct);
                    if (revenue != postedLineAmount) return false;
                    expectedAllocation += lineAmount;
                }
                if (expectedAllocation != actual.Amount || actual.CostCentreId.HasValue) return false;
                var receiptBankCandidates = await _db.BankAccounts.Where(x =>
                    x.CompanyId == companyId && x.Currency == actual.Currency &&
                    _db.JournalLines.Any(j => j.JournalEntryId == receipt.JournalEntryId.Value &&
                        j.GlAccountId == x.GlAccountId && j.Debit - j.Credit == receipt.Amount)).ToListAsync(ct);
                var bank = receiptBankCandidates.Count == 1 ? receiptBankCandidates[0] : null;
                if (bank == null) return false;
                var receiptPosted = await _db.JournalEntries.AnyAsync(x =>
                    x.Id == receipt.JournalEntryId.Value && x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct);
                if (!receiptPosted) return false;
                var bankLeg = await _db.JournalLines.Where(x =>
                    x.JournalEntryId == receipt.JournalEntryId.Value && x.GlAccountId == bank.GlAccountId)
                    .SumAsync(x => x.Debit - x.Credit, ct);
                var receiptReference = string.IsNullOrWhiteSpace(receipt.ReceiptNo) ? receipt.Id.ToString("N") : receipt.ReceiptNo;
                return bankLeg == receipt.Amount &&
                    string.Equals(actual.SourceReference, receiptReference, StringComparison.Ordinal);
            }

            if (actual.SourceKind == PublicSectorBudgetActualSourceKind.ApPayment && !isReceipt)
            {
                var payment = await _db.ApPayments.FirstOrDefaultAsync(x =>
                    x.Id == actual.SourceRecordId && x.CompanyId == companyId, ct);
                if (payment == null || !payment.JournalEntryId.HasValue ||
                    payment.Currency != actual.Currency || payment.PaymentDate.Date != actual.SourceDate.Date ||
                    actual.EffectiveDate.Date != payment.PaymentDate.Date ||
                    !payment.PreparedBy.HasValue || !payment.AuthorisedBy.HasValue ||
                    payment.PreparedBy == payment.AuthorisedBy || !payment.AuthorisedAt.HasValue)
                    return false;
                var allocations = await _db.ApPaymentAllocations.Where(x => x.ApPaymentId == payment.Id).ToListAsync(ct);
                if (allocations.Count == 0 || allocations.Any(x => x.AmountApplied <= 0m) ||
                    allocations.Sum(x => x.AmountApplied) != payment.Amount) return false;
                var invoiceIds = allocations.Select(x => x.ApInvoiceId).Distinct().ToArray();
                var invoices = await _db.ApInvoices.Where(x => x.CompanyId == companyId && invoiceIds.Contains(x.Id)).ToListAsync(ct);
                if (invoices.Count != invoiceIds.Length) return false;
                decimal expectedAllocation = 0m;
                foreach (var allocation in allocations)
                {
                    var invoice = invoices.Single(x => x.Id == allocation.ApInvoiceId);
                    if (invoice.SupplierId != payment.SupplierId ||
                        (invoice.Status != InvoiceStatus.Paid && invoice.Status != InvoiceStatus.Partial) ||
                        !invoice.FundCentreId.HasValue || invoice.Currency != payment.Currency ||
                        invoice.Total < allocation.AmountApplied || invoice.Outstanding < 0m ||
                        (invoice.Status == InvoiceStatus.Paid && invoice.Outstanding > 0.000001m) ||
                        (invoice.Status == InvoiceStatus.Partial && invoice.Outstanding <= 0m) ||
                        invoice.VatAmount != 0m || invoice.WhtAmount != 0m || !invoice.JournalEntryId.HasValue) return false;
                    var invoiceLines = await _db.ApInvoiceLines.Where(x => x.ApInvoiceId == invoice.Id).ToListAsync(ct);
                    if (invoiceLines.Count == 0 || invoiceLines.Any(x => x.LineVat != 0m) ||
                        invoiceLines.Sum(x => x.LineTotal) != invoice.Total) return false;
                    var invoiceGlAccounts = invoiceLines.Select(x => x.GlAccountId).Distinct().ToArray();
                    var partialSettlement = allocation.AmountApplied < invoice.Total;
                    if (partialSettlement && invoiceGlAccounts.Length > 1) return false;
                    var postedLineAmount = invoiceLines.Where(x => x.GlAccountId == actual.GlAccountId).Sum(x => x.LineTotal);
                    var lineAmount = invoice.FundCentreId == actual.FundCentreId
                        ? partialSettlement
                            ? invoiceGlAccounts[0] == actual.GlAccountId ? allocation.AmountApplied : 0m
                            : postedLineAmount
                        : 0m;
                    if (lineAmount <= 0m) continue;
                    var invoicePosted = await _db.JournalEntries.AnyAsync(x =>
                        x.Id == invoice.JournalEntryId.Value && x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct);
                    if (!invoicePosted) return false;
                    var expense = await _db.JournalLines.Where(x =>
                        x.JournalEntryId == invoice.JournalEntryId.Value && x.GlAccountId == actual.GlAccountId &&
                        x.FundCentreId == actual.FundCentreId && x.CostCentreId == null)
                        .SumAsync(x => x.Debit - x.Credit, ct);
                    if (expense != postedLineAmount) return false;
                    expectedAllocation += lineAmount;
                }
                if (expectedAllocation != actual.Amount || actual.CostCentreId.HasValue) return false;
                var paymentBankCandidates = await _db.BankAccounts.Where(x =>
                    x.CompanyId == companyId && x.Currency == actual.Currency &&
                    _db.JournalLines.Any(j => j.JournalEntryId == payment.JournalEntryId.Value &&
                        j.GlAccountId == x.GlAccountId && j.Credit - j.Debit == payment.Amount)).ToListAsync(ct);
                var bank = paymentBankCandidates.Count == 1 ? paymentBankCandidates[0] : null;
                if (bank == null) return false;
                var paymentPosted = await _db.JournalEntries.AnyAsync(x =>
                    x.Id == payment.JournalEntryId.Value && x.CompanyId == companyId && x.Status == JournalStatus.Posted, ct);
                if (!paymentPosted) return false;
                var bankLeg = await _db.JournalLines.Where(x =>
                    x.JournalEntryId == payment.JournalEntryId.Value && x.GlAccountId == bank.GlAccountId)
                    .SumAsync(x => x.Credit - x.Debit, ct);
                var paymentReference = string.IsNullOrWhiteSpace(payment.PaymentNo) ? payment.Id.ToString("N") : payment.PaymentNo;
                return bankLeg == payment.Amount &&
                    string.Equals(actual.SourceReference, paymentReference, StringComparison.Ordinal);
            }

            return false;
        }
    }
}
