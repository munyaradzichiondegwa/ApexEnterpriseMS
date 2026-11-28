using ApexERP.Application.Security;
using ApexERP.API.Authorization;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateFundCentreRequest(string Code, string Name, FundCentreLevel Level, Guid? ParentId, int FinancialYear, decimal AppropriationAmount, decimal CarriedForward);
    public record CreateBudgetAllocationRequest(Guid FundCentreId, Guid GlAccountId, Guid? CostCentreId, int FinancialYear, decimal Amount);

    public record CreateBudgetVersionLineRequest(Guid FundCentreId, Guid GlAccountId, Guid? CostCentreId,
        string Description, string ClassificationCode, decimal Amount);
    public record CreateBudgetVersionRequest(int FinancialYear, PublicSectorBudgetVersionKind Kind, string Currency,
        AccountingBasis AccountingBasis, string ClassificationBasis, string EntityScope, DateTime PeriodStart,
        DateTime PeriodEnd, string AuthorityReference, string? AmendmentReason,
        IReadOnlyCollection<CreateBudgetVersionLineRequest> Lines,
        PublicSectorBudgetAmendmentNature? AmendmentNature = null);
    public record CreateBudgetVarianceExplanationRequest(Guid BudgetVersionId, Guid BudgetVersionLineId,
        string Explanation, string EvidenceReference);
    public record ReviewBudgetVarianceExplanationRequest(bool Accepted, string? ReviewNote);
    public record CreateBudgetReconciliationRequest(int FinancialYear, PublicSectorBudgetReconciliationType Type,
        string Description, string StatementSection, decimal OperatingAmount, decimal InvestingAmount,
        decimal FinancingAmount, string SupportingReference, decimal RevenueAmount = 0m, decimal ExpenseAmount = 0m);
    public record CreateBudgetExecutionActualRequest(Guid BudgetVersionId, Guid BudgetVersionLineId,
        PublicSectorBudgetActualEventKind EventKind, PublicSectorBudgetActualSourceKind SourceKind,
        Guid SourceRecordId, DateTime EffectiveDate, CashFlowCategory CashFlowSection);
    public record CreateCashPeriodAttestationRequest(Guid BudgetVersionId, string EvidenceReference);
    public record CreateExpectedCashSourceRequest(Guid BudgetVersionId, string SourceKind, string SourceCode, string SourceName, string? SourceIdentifier, bool IsRequired, DateTime EffectiveFrom, DateTime EffectiveTo);
    public record CreateCashSourceSubmissionRequest(Guid ExpectedCashSourceId, int RecordCount,
        decimal ControlTotal, decimal OpeningBalance, decimal ClosingBalance, string Currency, string EvidenceReference);
    public record ReviewCashSourceSubmissionRequest(bool Accepted, string? ReviewNote);
    public record CreateCashSourceReconciliationLineRequest(Guid SourceRecordId, string SourceReference,
        Guid? BudgetExecutionActualId, bool IsExcluded, string? ExclusionReason);
    public record ReviewCashSourceRecordReconciliationRequest(bool Accepted, string? ReviewNote);
    public record CreateExpectedCashSourceRegisterAttestationRequest(Guid BudgetVersionId, int ExpectedSourceCount,
        string EntityScope, string EvidenceReference);
    public record ReviewExpectedCashSourceRegisterAttestationRequest(bool Accepted, string? ReviewNote);

    public record CommitRequest(Guid FundCentreId, Guid PurchaseOrderId, decimal Amount, bool AllowOverride, string? OverrideReason = null);
    public record SubmitBudgetOverrideApprovalRequest(Guid FundCentreId, Guid PurchaseOrderId, decimal Amount, string Reason);
    public record CreateTenderRequest(string Title, string? Specifications, DateTime SubmissionDeadline);
    public record SubmitBidRequest(Guid VendorId, decimal PriceAmount);
    public record ScoreBidRequest(decimal PriceScore, decimal TechnicalScore, decimal LocalContentScore, decimal PriceWeight, decimal TechnicalWeight, decimal LocalContentWeight);
    public record AwardTenderRequest(Guid WinningVendorId, string Justification);
    public record SubmitTenderAwardApprovalRequest(Guid WinningVendorId);
    public record RecordScanRequest(string ScannedTagValue, string? ConditionNotes);

    [Route("api/v1/public-sector")]
    public class PublicSectorController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly PublicSectorService _publicSector;
        private readonly TenderService _tender;
        private readonly AssetVerificationService _assetVerification;
        private readonly ApprovalChainService _approvalChains;
        private readonly CashFlowStatementService _cashFlow;

        public PublicSectorController(ApexErpDbContext db, PublicSectorService publicSector, TenderService tender,
            AssetVerificationService assetVerification, ApprovalChainService approvalChains, CashFlowStatementService cashFlow)
        {
            _db = db;
            _publicSector = publicSector;
            _tender = tender;
            _assetVerification = assetVerification;
            _approvalChains = approvalChains;
            _cashFlow = cashFlow;
        }

        // ---- Fund Centres & Commitments ----

        [HttpGet("fund-centres")]
        public async Task<IActionResult> GetFundCentres([FromQuery] int? year)
        {
            var q = _db.FundCentres.Where(f => f.CompanyId == CompanyId);
            if (year.HasValue) q = q.Where(f => f.FinancialYear == year.Value);
            return Ok(await q.OrderBy(f => f.Code).ToListAsync());
        }

        [HttpPost("fund-centres")]
        public async Task<IActionResult> CreateFundCentre(CreateFundCentreRequest req)
        {
            try
            {
                var fc = await _publicSector.CreateFundCentreAsync(CompanyId, req.Code, req.Name, req.Level, req.ParentId, req.FinancialYear, req.AppropriationAmount, req.CarriedForward);
                return Ok(fc);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("fund-centres/{id}/utilization")]
        public async Task<IActionResult> GetUtilization(Guid id)
        {
            try { return Ok(await _publicSector.GetBudgetUtilizationAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("fund-centres/utilization")]
        public async Task<IActionResult> GetAllUtilization([FromQuery] int year) => Ok(await _publicSector.GetAllBudgetUtilizationAsync(CompanyId, year));

        /// <summary>Vote/program/sub-program expenditure reconciled to posted general-ledger actuals.</summary>
        [HttpGet("fund-centres/expenditure-report")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetExpenditureReport([FromQuery] int year)
        {
            try { return Ok(await _publicSector.GetExpenditureReportAsync(CompanyId, year)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("budget-allocations/variance-report")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetBudgetVarianceReport([FromQuery] int year, [FromQuery] Guid? fundCentreId)
        {
            try { return Ok(await _publicSector.GetBudgetVarianceReportAsync(CompanyId, year, fundCentreId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>
        /// Consolidated public-sector schedule using tenant-configured GL SubType economic
        /// classifications, Vote/Programme/Sub-Programme fund centres and cost-centre objectives.
        /// </summary>
        [HttpGet("budget-allocations/classification-schedule")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetClassificationSchedule([FromQuery] int year, [FromQuery] Guid? fundCentreId,
            CancellationToken ct)
        {
            try { return Ok(await _publicSector.GetClassificationScheduleAsync(CompanyId, year, fundCentreId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("budget-reconciliations")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetBudgetReconciliations([FromQuery] int year, CancellationToken ct)
        {
            if (year < 2000 || year > 2200) return BadRequest(new { error = "A valid financial year is required." });
            return Ok(await _db.PublicSectorBudgetReconciliations.Where(x =>
                x.CompanyId == CompanyId && x.FinancialYear == year)
                .OrderByDescending(x => x.CreatedAt).ToListAsync(ct));
        }

        [HttpPost("budget-reconciliations")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateBudgetReconciliation(CreateBudgetReconciliationRequest req, CancellationToken ct)
        {
            try
            {
                var item = await _publicSector.CreateBudgetReconciliationAsync(CompanyId, req.FinancialYear,
                    req.Type, req.Description, req.StatementSection, req.OperatingAmount,
                    req.InvestingAmount, req.FinancingAmount, req.SupportingReference, UserId, ct,
                    req.RevenueAmount, req.ExpenseAmount);
                return Ok(item);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-reconciliations/{id}/submit-for-approval")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitBudgetReconciliationForApproval(Guid id, CancellationToken ct)
        {
            try
            {
                var item = await _db.PublicSectorBudgetReconciliations.FirstOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId, ct);
                if (item == null) return NotFound(new { error = "Budget reconciliation row not found." });
                if (item.Status != PublicSectorBudgetReconciliationStatus.Draft)
                    return BadRequest(new { error = "Only a draft reconciliation row can be submitted." });
                var amount = Math.Abs(item.RevenueAmount) + Math.Abs(item.ExpenseAmount) +
                    Math.Abs(item.OperatingAmount) + Math.Abs(item.InvestingAmount) + Math.Abs(item.FinancingAmount);
                var workflow = await _db.InTransactionAsync(async token =>
                {
                    var request = await _approvalChains.SubmitForApprovalAsync(CompanyId,
                        "PublicSectorBudgetReconciliation", item.Id, "PublicSectorBudgetReconciliation",
                        UserId, amount, null, $"Approve IPSAS 24 {item.Type} reconciliation: {item.Description}", token);
                    await _publicSector.MarkBudgetReconciliationPendingAsync(CompanyId, item.Id, request.Id, token);
                    return request;
                }, ct);
                return Ok(workflow);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-reconciliations/{id}/finalise-approval")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> FinaliseBudgetReconciliationApproval(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.FinaliseBudgetReconciliationApprovalAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-versions")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateBudgetVersion(CreateBudgetVersionRequest req, CancellationToken ct)
        {
            try
            {
                var version = await _publicSector.CreateBudgetVersionAsync(CompanyId, req.FinancialYear, req.Kind,
                    req.Currency, req.AccountingBasis, req.ClassificationBasis, req.EntityScope,
                    req.PeriodStart, req.PeriodEnd, req.AuthorityReference, req.AmendmentReason,
                    req.Lines.Select(x => new PublicSectorService.BudgetVersionLineInput(x.FundCentreId,
                        x.GlAccountId, x.CostCentreId, x.Description, x.ClassificationCode, x.Amount)).ToArray(),
                    UserId, ct, req.AmendmentNature);
                return Ok(version);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-versions/{id}/submit-for-approval")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitBudgetVersionForApproval(Guid id, CancellationToken ct)
        {
            try
            {
                var version = await _db.PublicSectorBudgetVersions.Include(x => x.Lines)
                    .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == CompanyId, ct);
                if (version == null) return NotFound(new { error = "Budget version not found." });
                if (version.Status != PublicSectorBudgetVersionStatus.Draft)
                    return BadRequest(new { error = "Only a draft budget version can be submitted." });
                var total = version.Lines.Sum(x => x.Amount);
                var workflow = await _db.InTransactionAsync(async token =>
                {
                    var request = await _approvalChains.SubmitForApprovalAsync(CompanyId,
                        "PublicSectorBudgetVersion", version.Id, "PublicSectorBudgetVersion", UserId,
                        total, null, $"Approve {version.Kind} public-sector budget version {version.VersionNumber} for FY{version.FinancialYear}.", token);
                    await _publicSector.MarkBudgetVersionPendingAsync(CompanyId, version.Id, request.Id, token);
                    return request;
                }, ct);
                return Ok(workflow);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-versions/{id}/finalise-approval")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> FinaliseBudgetVersionApproval(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.FinaliseBudgetVersionApprovalAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("budget-versions/approved")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetApprovedBudgetVersions([FromQuery] int year, CancellationToken ct)
        {
            if (year < 2000 || year > 2200) return BadRequest(new { error = "A valid financial year is required." });
            var latest = await _db.PublicSectorBudgetVersions.Include(x => x.Lines)
                .Where(x => x.CompanyId == CompanyId && x.FinancialYear == year &&
                    x.Status == PublicSectorBudgetVersionStatus.Approved && x.AccountingBasis == AccountingBasis.Cash)
                .OrderByDescending(x => x.Kind).ThenByDescending(x => x.VersionNumber)
                .FirstOrDefaultAsync(ct);
            return Ok(latest == null ? Array.Empty<PublicSectorBudgetVersion>() : new[] { latest });
        }

        /// <summary>IPSAS 24 statement: approved original and final budgets, comparable actuals and variance explanations.</summary>
        [HttpGet("budget-versions/comparison")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetBudgetComparison([FromQuery] int year, CancellationToken ct)
        {
            try { return Ok(await _publicSector.GetBudgetComparisonAsync(CompanyId, year, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // IPSAS 24 paragraph 14: explain material differences between final budget and actual.
        // This implementation requires an explanation for every non-zero variance (stricter than
        // a configurable materiality threshold) until an approved entity materiality policy exists.
        [HttpPost("budget-variance-explanations")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateBudgetVarianceExplanation(
            CreateBudgetVarianceExplanationRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.CreateBudgetVarianceExplanationAsync(CompanyId,
                req.BudgetVersionId, req.BudgetVersionLineId, req.Explanation,
                req.EvidenceReference, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-variance-explanations/{id}/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitBudgetVarianceExplanation(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitBudgetVarianceExplanationAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-variance-explanations/{id}/review")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> ReviewBudgetVarianceExplanation(Guid id,
            ReviewBudgetVarianceExplanationRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.ReviewBudgetVarianceExplanationAsync(CompanyId, id,
                UserId, req.Accepted, req.ReviewNote, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-allocations")]
        public async Task<IActionResult> CreateBudgetAllocation(CreateBudgetAllocationRequest req)
        {
            try
            {
                var allocation = await _publicSector.CreateBudgetAllocationAsync(CompanyId, req.FundCentreId,
                    req.GlAccountId, req.CostCentreId, req.FinancialYear, req.Amount, UserId);
                return Ok(allocation);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-allocations/{id}/submit-for-approval")]
        public async Task<IActionResult> SubmitBudgetAllocationForApproval(Guid id)
        {
            try
            {
                var allocation = await _db.PublicSectorBudgetAllocations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == CompanyId);
                if (allocation == null) return NotFound(new { error = "Budget allocation not found." });
                if (allocation.Status != PublicSectorBudgetAllocationStatus.Draft)
                    return BadRequest(new { error = "Only a draft allocation can be submitted." });
                var workflow = await _db.InTransactionAsync(async token =>
                {
                    var request = await _approvalChains.SubmitForApprovalAsync(CompanyId, "PublicSectorBudgetAllocation",
                        allocation.Id, "PublicSectorBudgetAllocation", UserId, allocation.Amount, allocation.FundCentreId,
                        $"Approve account {allocation.GlAccountId} / cost centre {allocation.CostCentreId?.ToString() ?? "Unallocated"} budget allocation.", token);
                    await _publicSector.MarkBudgetAllocationPendingAsync(CompanyId, allocation.Id, request.Id, token);
                    return request;
                });
                return Ok(workflow);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-allocations/{id}/finalise-approval")]
        public async Task<IActionResult> FinaliseBudgetAllocationApproval(Guid id)
        {
            try { return Ok(await _publicSector.FinaliseBudgetAllocationApprovalAsync(CompanyId, id, UserId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("commitments/override/submit-for-approval")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitBudgetOverrideForApproval(SubmitBudgetOverrideApprovalRequest req)
        {
            try
            {
                if (req.Amount <= 0 || string.IsNullOrWhiteSpace(req.Reason))
                    return BadRequest(new { error = "A positive override amount and documented reason are required." });
                var fundCentre = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == req.FundCentreId && f.CompanyId == CompanyId);
                var purchaseOrder = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == req.PurchaseOrderId && p.CompanyId == CompanyId);
                if (fundCentre == null || purchaseOrder == null)
                    return NotFound(new { error = "The fund centre or purchase order was not found in this company." });
                if (purchaseOrder.Status != PurchaseOrderStatus.Approved || purchaseOrder.FundCentreId != req.FundCentreId ||
                    purchaseOrder.Total != req.Amount)
                    return BadRequest(new { error = "The override request must match an approved purchase order assigned to the selected fund centre and its exact total." });
                var workflow = await _approvalChains.SubmitForApprovalAsync(CompanyId, "PublicSectorBudgetOverride",
                    purchaseOrder.Id, "PurchaseOrder", UserId, req.Amount, req.FundCentreId, req.Reason.Trim());
                return Ok(workflow);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("commitments")]
        public async Task<IActionResult> Commit(CommitRequest req)
        {
            try
            {
                if (req.AllowOverride && !HasPermission(Permissions.PublicSectorBudgetOverride))
                    return Forbid();
                if (req.AllowOverride && string.IsNullOrWhiteSpace(req.OverrideReason))
                    return BadRequest(new { error = "An override justification is required." });
                var commitment = await _publicSector.CommitAsync(CompanyId, req.FundCentreId, req.PurchaseOrderId,
                    req.Amount, req.AllowOverride, req.OverrideReason, UserId,
                    HttpContext.Connection.RemoteIpAddress?.ToString());
                return Ok(commitment);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("fund-centres/{id}/year-end-close")]
        [HasPermission(Permissions.PublicSectorYearEndClose)]
        public async Task<IActionResult> YearEndClose(Guid id, [FromQuery] bool carryForward)
        {
            try { return Ok(await _publicSector.CloseYearEndAsync(CompanyId, id, carryForward)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---- E-Tendering ----

        [HttpGet("tenders")]
        public async Task<IActionResult> GetTenders() => Ok(await _db.TenderNotices.Include(t => t.Bids).Where(t => t.CompanyId == CompanyId).ToListAsync());

        [HttpPost("tenders")]
        public async Task<IActionResult> CreateTender(CreateTenderRequest req) =>
            Ok(await _tender.CreateTenderAsync(CompanyId, req.Title, req.Specifications, req.SubmissionDeadline));

        [HttpPost("tenders/{id}/bids")]
        public async Task<IActionResult> SubmitBid(Guid id, SubmitBidRequest req) =>
            Ok(await _tender.SubmitBidAsync(CompanyId, id, req.VendorId, req.PriceAmount));

        [HttpPost("tenders/{id}/evaluation")]
        [HasPermission(Permissions.PublicSectorTendersAward)]
        public async Task<IActionResult> BeginTenderEvaluation(Guid id)
        {
            try { return Ok(await _tender.BeginEvaluationAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("tenders/bids/{bidId}/score")]
        public async Task<IActionResult> ScoreBid(Guid bidId, ScoreBidRequest req)
        {
            try
            {
                var bid = await _tender.ScoreBidAsync(CompanyId, bidId, req.PriceScore, req.TechnicalScore, req.LocalContentScore,
                    req.PriceWeight, req.TechnicalWeight, req.LocalContentWeight);
                return Ok(bid);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("tenders/{id}/award/submit-for-approval")]
        [HasPermission(Permissions.PublicSectorTendersAward)]
        public async Task<IActionResult> SubmitTenderAwardForApproval(Guid id, SubmitTenderAwardApprovalRequest req)
        {
            try { return Ok(await _tender.SubmitAwardForApprovalAsync(CompanyId, id, req.WinningVendorId, UserId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("tenders/{id}/award")]
        [HasPermission(Permissions.PublicSectorTendersAward)]
        public async Task<IActionResult> AwardTender(Guid id, AwardTenderRequest req) =>
            Ok(await _tender.AwardAsync(CompanyId, id, req.WinningVendorId, req.Justification));

        // ---- Asset Verification ----

        [HttpPost("asset-verification/batches")]
        public async Task<IActionResult> StartVerificationBatch([FromQuery] string? location) =>
            Ok(await _assetVerification.StartBatchAsync(CompanyId, location, UserId));

        [HttpPost("asset-verification/batches/{id}/scan")]
        public async Task<IActionResult> RecordScan(Guid id, RecordScanRequest req) =>
            Ok(await _assetVerification.RecordScanAsync(CompanyId, id, req.ScannedTagValue, req.ConditionNotes));

        [HttpPost("asset-verification/batches/{id}/complete")]
        public async Task<IActionResult> CompleteBatch(Guid id)
        {
            try { return Ok(await _assetVerification.CompleteBatchAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("asset-verification/batches")]
        public async Task<IActionResult> GetBatches() =>
            Ok(await _db.AssetVerificationBatches.Include(b => b.Scans).Where(b => b.CompanyId == CompanyId).OrderByDescending(b => b.VerificationDate).ToListAsync());

        // ---- IPSAS 24: expected source population register ----

        [HttpGet("expected-cash-sources")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetExpectedCashSources([FromQuery] Guid budgetVersionId, CancellationToken ct)
        {
            if (budgetVersionId == Guid.Empty) return BadRequest(new { error = "A budget version is required." });
            return Ok(await _publicSector.GetExpectedCashSourcesAsync(CompanyId, budgetVersionId, ct));
        }

        [HttpPost("expected-cash-sources")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateExpectedCashSource(CreateExpectedCashSourceRequest req, CancellationToken ct)
        {
            try
            {
                var item = await _publicSector.CreateExpectedCashSourceAsync(CompanyId, req.BudgetVersionId,
                    req.SourceKind, req.SourceCode, req.SourceName, req.SourceIdentifier, req.IsRequired,
                    req.EffectiveFrom, req.EffectiveTo, UserId, ct);
                return Ok(item);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("expected-cash-sources/{id}/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitExpectedCashSource(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitExpectedCashSourceAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("expected-cash-sources/{id}/approve")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> ApproveExpectedCashSource(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.ApproveExpectedCashSourceAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---- IPSAS 24: expected source period submissions ----

        [HttpGet("cash-source-submissions")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetCashSourceSubmissions([FromQuery] Guid budgetVersionId, CancellationToken ct)
        {
            if (budgetVersionId == Guid.Empty) return BadRequest(new { error = "A budget version is required." });
            return Ok(await _publicSector.GetCashSourceSubmissionsAsync(CompanyId, budgetVersionId, ct));
        }

        [HttpPost("cash-source-submissions")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateCashSourceSubmission(CreateCashSourceSubmissionRequest req, CancellationToken ct)
        {
            try
            {
                var item = await _publicSector.CreateCashSourceSubmissionAsync(CompanyId, req.ExpectedCashSourceId,
                    req.RecordCount, req.ControlTotal, req.OpeningBalance, req.ClosingBalance,
                    req.Currency, req.EvidenceReference, UserId, ct);
                return Ok(item);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("cash-source-submissions/{id}/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitCashSourceSubmission(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitCashSourceSubmissionAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("cash-source-submissions/{id}/review")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> ReviewCashSourceSubmission(Guid id, ReviewCashSourceSubmissionRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.ReviewCashSourceSubmissionAsync(CompanyId, id, UserId,
                req.Accepted, req.ReviewNote, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }


        // Independent certification of the expected-source population itself.
        [HttpGet("expected-cash-sources/completeness-attestation")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetExpectedCashSourceRegisterAttestation([FromQuery] Guid budgetVersionId, CancellationToken ct)
        {
            if (budgetVersionId == Guid.Empty) return BadRequest(new { error = "A budget version is required." });
            return Ok(await _publicSector.GetExpectedCashSourceRegisterAttestationAsync(CompanyId, budgetVersionId, ct));
        }

        [HttpPost("expected-cash-sources/completeness-attestation")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateExpectedCashSourceRegisterAttestation(
            CreateExpectedCashSourceRegisterAttestationRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.CreateExpectedCashSourceRegisterAttestationAsync(CompanyId,
                req.BudgetVersionId, req.ExpectedSourceCount, req.EntityScope, req.EvidenceReference, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("expected-cash-sources/completeness-attestation/{id}/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitExpectedCashSourceRegisterAttestation(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitExpectedCashSourceRegisterAttestationAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("expected-cash-sources/completeness-attestation/{id}/review")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> ReviewExpectedCashSourceRegisterAttestation(Guid id,
            ReviewExpectedCashSourceRegisterAttestationRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.ReviewExpectedCashSourceRegisterAttestationAsync(CompanyId, id,
                UserId, req.Accepted, req.ReviewNote, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // Transaction-level source-to-actual reconciliation. Source control totals are reviewed
        // first; these rows then prove that every source record is allocated or explicitly excluded.
        [HttpGet("cash-source-submissions/{id}/reconciliation-lines")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetCashSourceReconciliationLines(Guid id, CancellationToken ct)
        {
            if (!await _db.PublicSectorCashSourceSubmissions.AnyAsync(x =>
                x.Id == id && x.CompanyId == CompanyId, ct)) return NotFound();
            return Ok(await _publicSector.GetCashSourceReconciliationLinesAsync(CompanyId, id, ct));
        }

        [HttpPost("cash-source-submissions/{id}/reconciliation-lines")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> AddCashSourceReconciliationLine(Guid id,
            CreateCashSourceReconciliationLineRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.AddCashSourceReconciliationLineAsync(CompanyId, id,
                req.SourceRecordId, req.SourceReference, req.BudgetExecutionActualId,
                req.IsExcluded, req.ExclusionReason, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("cash-source-submissions/{id}/record-reconciliation/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitCashSourceRecordReconciliation(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitCashSourceRecordReconciliationAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("cash-source-submissions/{id}/record-reconciliation/review")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> ReviewCashSourceRecordReconciliation(Guid id,
            ReviewCashSourceRecordReconciliationRequest req, CancellationToken ct)
        {
            try { return Ok(await _publicSector.ReviewCashSourceRecordReconciliationAsync(CompanyId, id,
                UserId, req.Accepted, req.ReviewNote, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }


        // ---- IPSAS 24: source-backed cash budget actuals ----

        [HttpGet("budget-execution-actuals")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetBudgetExecutionActuals([FromQuery] int year, CancellationToken ct)
        {
            if (year < 2000 || year > 2200) return BadRequest(new { error = "A valid financial year is required." });
            return Ok(await _publicSector.GetBudgetExecutionActualsAsync(CompanyId, year, ct));
        }

        [HttpGet("budget-execution-actuals/reconciliation")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetBudgetExecutionReconciliation([FromQuery] int year, CancellationToken ct)
        {
            if (year < 2000 || year > 2200) return BadRequest(new { error = "A valid financial year is required." });
            var version = await _db.PublicSectorBudgetVersions.Where(x => x.CompanyId == CompanyId &&
                    x.FinancialYear == year && x.AccountingBasis == AccountingBasis.Cash &&
                    x.Status == PublicSectorBudgetVersionStatus.Approved)
                .OrderByDescending(x => x.Kind).ThenByDescending(x => x.VersionNumber).FirstOrDefaultAsync(ct);
            if (version == null) return BadRequest(new { error = "An approved cash-basis budget version is required." });

            var actuals = await _db.PublicSectorBudgetExecutionActuals.Where(x =>
                x.CompanyId == CompanyId && x.BudgetVersionId == version.Id &&
                x.EffectiveDate >= version.PeriodStart && x.EffectiveDate < version.PeriodEnd).ToListAsync(ct);
            var capturedBankSourceIds = actuals.Where(x => x.SourceKind == PublicSectorBudgetActualSourceKind.BankTransaction)
                .Select(x => x.SourceRecordId).Distinct().ToArray();
            var arReceiptIds = actuals.Where(x => x.SourceKind == PublicSectorBudgetActualSourceKind.ArReceipt)
                .Select(x => x.SourceRecordId).Distinct().ToArray();
            var apPaymentIds = actuals.Where(x => x.SourceKind == PublicSectorBudgetActualSourceKind.ApPayment)
                .Select(x => x.SourceRecordId).Distinct().ToArray();
            var reviewedExclusions = await _publicSector.GetReviewedCashSourceExclusionsAsync(
                CompanyId, version.Id, ct);
            var excludedBankSourceIds = reviewedExclusions.Where(x => x.SourceKind == "BankAccount")
                .Select(x => x.SourceRecordId).Distinct().ToArray();
            var excludedArReceiptIds = reviewedExclusions.Where(x => x.SourceKind == "ReceivablesControl")
                .Select(x => x.SourceRecordId).Distinct().ToArray();
            var excludedApPaymentIds = reviewedExclusions.Where(x => x.SourceKind == "PayablesControl")
                .Select(x => x.SourceRecordId).Distinct().ToArray();
            var capturedSettlementJournalIds = await _db.ArReceipts
                .Where(x => x.CompanyId == CompanyId && arReceiptIds.Contains(x.Id) && x.JournalEntryId.HasValue)
                .Select(x => x.JournalEntryId!.Value)
                .Concat(_db.ApPayments.Where(x => x.CompanyId == CompanyId &&
                    apPaymentIds.Contains(x.Id) && x.JournalEntryId.HasValue)
                    .Select(x => x.JournalEntryId!.Value))
                .Distinct().ToArrayAsync(ct);

            var capturedBankJournalIds = capturedBankSourceIds.Length == 0
                ? Array.Empty<Guid>()
                : await _db.BankTransactions.Where(x => x.CompanyId == CompanyId &&
                        capturedBankSourceIds.Contains(x.Id) && x.JournalEntryId.HasValue)
                    .Select(x => x.JournalEntryId!.Value).Distinct().ToArrayAsync(ct);
            var postedArReceipts = await _db.ArReceipts.Where(x =>
                    x.CompanyId == CompanyId && x.ReceiptDate >= version.PeriodStart &&
                    x.ReceiptDate < version.PeriodEnd && x.JournalEntryId.HasValue &&
                    _db.JournalEntries.Any(j => j.Id == x.JournalEntryId.Value &&
                        j.CompanyId == CompanyId && j.Status == JournalStatus.Posted))
                .Select(x => new { x.Id, JournalEntryId = x.JournalEntryId!.Value }).ToListAsync(ct);
            var postedApPayments = await _db.ApPayments.Where(x =>
                    x.CompanyId == CompanyId && x.PaymentDate >= version.PeriodStart &&
                    x.PaymentDate < version.PeriodEnd && x.JournalEntryId.HasValue &&
                    _db.JournalEntries.Any(j => j.Id == x.JournalEntryId.Value &&
                        j.CompanyId == CompanyId && j.Status == JournalStatus.Posted))
                .Select(x => new { x.Id, JournalEntryId = x.JournalEntryId!.Value }).ToListAsync(ct);
            var missingArSettlementCoverage = postedArReceipts.Count(x =>
                !arReceiptIds.Contains(x.Id) && !excludedArReceiptIds.Contains(x.Id) &&
                !capturedBankJournalIds.Contains(x.JournalEntryId));
            var missingApSettlementCoverage = postedApPayments.Count(x =>
                !apPaymentIds.Contains(x.Id) && !excludedApPaymentIds.Contains(x.Id) &&
                !capturedBankJournalIds.Contains(x.JournalEntryId));
            // AR/AP actuals cover the same posted bank movement through their settlement journal.
            var periodBankMovements = _db.BankTransactions.Where(x => x.CompanyId == CompanyId &&
                x.TransactionDate >= version.PeriodStart && x.TransactionDate < version.PeriodEnd);
            var uncapturedPostedMovements = await periodBankMovements.CountAsync(x =>
                !x.IsReconciled || !x.JournalEntryId.HasValue ||
                !_db.JournalEntries.Any(j => j.Id == x.JournalEntryId.Value &&
                    j.CompanyId == CompanyId && j.Status == JournalStatus.Posted) ||
                (!capturedBankSourceIds.Contains(x.Id) && !excludedBankSourceIds.Contains(x.Id) &&
                 !capturedSettlementJournalIds.Contains(x.JournalEntryId.Value)), ct);
            var pendingActuals = actuals.Count(x => x.Status != PublicSectorBudgetActualStatus.Certified);
            var certifiedActualsForValidation = actuals.Where(x => x.Status == PublicSectorBudgetActualStatus.Certified).ToList();
            var invalidCertifiedActuals = 0;
            foreach (var actual in certifiedActualsForValidation)
            {
                if (!await _publicSector.IsCertifiedCashActualSourceValidAsync(CompanyId, actual, ct))
                    invalidCertifiedActuals++;
            }
            var certified = actuals.Where(x => x.Status == PublicSectorBudgetActualStatus.Certified).ToList();
            decimal Actual(CashFlowCategory section) => certified.Where(x => x.CashFlowSection == section)
                .Sum(x => x.EventKind == PublicSectorBudgetActualEventKind.CashReceipt ? x.Amount : -x.Amount);

            // CashFlowStatementService currently uses an inclusive period end; pass the final
            // representable instant before the budget's exclusive end to preserve the budget cut-off.
            var cashFlow = await _cashFlow.GetStatementAsync(CompanyId, version.PeriodStart,
                version.PeriodEnd.AddTicks(-1), ct);
            var adjustments = await _db.PublicSectorBudgetReconciliations.Where(x =>
                x.CompanyId == CompanyId && x.FinancialYear == year &&
                x.Status == PublicSectorBudgetReconciliationStatus.Approved).ToListAsync(ct);
            decimal Adjustment(Func<PublicSectorBudgetReconciliation, decimal> selector) =>
                adjustments.Sum(selector);
            var budgetOperating = Actual(CashFlowCategory.Operating);
            var budgetInvesting = Actual(CashFlowCategory.Investing);
            var budgetFinancing = Actual(CashFlowCategory.Financing);
            var operatingDifference = cashFlow.NetCashFromOperating - budgetOperating;
            var investingDifference = cashFlow.NetCashFromInvesting - budgetInvesting;
            var financingDifference = cashFlow.NetCashFromFinancing - budgetFinancing;
            var operatingAdjustment = Adjustment(x => x.OperatingAmount);
            var investingAdjustment = Adjustment(x => x.InvestingAmount);
            var financingAdjustment = Adjustment(x => x.FinancingAmount);
            var noSourceBackedCashActuals = actuals.Count == 0;
            // When there are no actuals, the period is acceptable only if it is genuinely nil
            // and has a current nil-return, or if every posted source record is covered by a
            // reason-coded exclusion that has passed independent record-level review.
            var certifiedZeroActivity = noSourceBackedCashActuals &&
                await _publicSector.HasValidCertifiedZeroActivityAttestationAsync(CompanyId, version.Id, ct);
            var periodBankPopulationCount = await _db.BankTransactions.CountAsync(x =>
                x.CompanyId == CompanyId && x.TransactionDate >= version.PeriodStart &&
                x.TransactionDate < version.PeriodEnd, ct);
            var sourceActivityCoveredByExclusions = noSourceBackedCashActuals &&
                reviewedExclusions.Count > 0 &&
                periodBankPopulationCount == excludedBankSourceIds.Length &&
                postedArReceipts.Count == excludedArReceiptIds.Length &&
                postedApPayments.Count == excludedApPaymentIds.Length;
            // A source-backed cash bridge also requires every required entry in the approved
            // expected-source register to have an independently reviewed period submission.
            // This checks the registered population only; it does not certify that the register
            // itself is complete. That separate control remains a release blocker.
            var expectedSources = await _db.PublicSectorExpectedCashSources.Where(x =>
                x.CompanyId == CompanyId && x.BudgetVersionId == version.Id).ToListAsync(ct);
            var sourceSubmissions = (await _db.PublicSectorCashSourceSubmissions.Where(x =>
                x.CompanyId == CompanyId && x.BudgetVersionId == version.Id).ToListAsync(ct))
                .GroupBy(x => x.ExpectedCashSourceId)
                .Select(group => group.OrderByDescending(x => x.SubmissionRevision).First()).ToList();
            var expectedSourceRegisterPresent = expectedSources.Count > 0;
            var expectedSourceRegisterApproved = expectedSourceRegisterPresent &&
                expectedSources.All(x => x.Status == "Approved");
            var requiredExpectedSources = expectedSources.Where(x => x.IsRequired).ToList();
            var requiredSourceSubmissionsReviewed = requiredExpectedSources.All(source =>
                sourceSubmissions.Any(submission => submission.ExpectedCashSourceId == source.Id &&
                    submission.Status == "Reviewed"));
            var allExpectedSourceSubmissionsReviewed = expectedSources.All(source =>
                sourceSubmissions.Any(submission => submission.ExpectedCashSourceId == source.Id &&
                    submission.Status == "Reviewed" && submission.RecordLevelStatus == "Reviewed"));
            var requiredSourceRecordReconciliationsReviewed = requiredExpectedSources.All(source =>
                sourceSubmissions.Any(submission => submission.ExpectedCashSourceId == source.Id &&
                    submission.Status == "Reviewed" && submission.RecordLevelStatus == "Reviewed"));
            var noUnreviewedSourceSubmissions = sourceSubmissions.All(x =>
                x.Status == "Reviewed" && x.RecordLevelStatus == "Reviewed");
            var registeredSourceSubmissionCoverageComplete = expectedSourceRegisterPresent &&
                expectedSourceRegisterApproved && requiredSourceSubmissionsReviewed &&
                allExpectedSourceSubmissionsReviewed && noUnreviewedSourceSubmissions;
            var registerAttestation = await _db.PublicSectorExpectedCashSourceRegisterAttestations
                .Where(x => x.CompanyId == CompanyId && x.BudgetVersionId == version.Id)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var expectedSourceRegisterCompletenessCertified = registerAttestation != null &&
                registerAttestation.Status == "Certified" && registerAttestation.CertifiedBy.HasValue &&
                registerAttestation.CertifiedAt.HasValue &&
                registerAttestation.ExpectedSourceCount == expectedSources.Count &&
                registerAttestation.EntityScope == version.EntityScope &&
                expectedSourceRegisterApproved && allExpectedSourceSubmissionsReviewed;

            // A source is record-complete only after every registered population has a reviewed
            // transaction-level reconciliation, including zero-record submissions.
            var recordLevelSourceReconciliationComplete = expectedSourceRegisterPresent &&
                allExpectedSourceSubmissionsReviewed && noUnreviewedSourceSubmissions;
            var liveSourceRegisterComplete = await _publicSector.HasCompleteCashSourceRegisterAsync(
                CompanyId, version.Id, ct);
            var sourceCoverageComplete = (noSourceBackedCashActuals
                    ? certifiedZeroActivity || sourceActivityCoveredByExclusions
                    : true) &&
                uncapturedPostedMovements == 0 && missingArSettlementCoverage == 0 &&
                missingApSettlementCoverage == 0 && pendingActuals == 0 && invalidCertifiedActuals == 0 &&
                registeredSourceSubmissionCoverageComplete &&
                recordLevelSourceReconciliationComplete &&
                expectedSourceRegisterCompletenessCertified && liveSourceRegisterComplete;
            var operatingBridgeDifference = operatingDifference - operatingAdjustment;
            var investingBridgeDifference = investingDifference - investingAdjustment;
            var financingBridgeDifference = financingDifference - financingAdjustment;
            var reconciled = sourceCoverageComplete && operatingBridgeDifference == 0m &&
                investingBridgeDifference == 0m && financingBridgeDifference == 0m;

            return Ok(new
            {
                version.Id, version.FinancialYear, version.Currency,
                SourceCoverageComplete = sourceCoverageComplete,
                NoSourceBackedCashActuals = noSourceBackedCashActuals,
                CertifiedZeroActivityAttestationValid = certifiedZeroActivity,
                SourceActivityCoveredByReviewedExclusions = sourceActivityCoveredByExclusions,
                ExpectedSourceRegisterPresent = expectedSourceRegisterPresent,
                ExpectedSourceRegisterApproved = expectedSourceRegisterApproved,
                RequiredExpectedSources = requiredExpectedSources.Count,
                RequiredSourceSubmissionsReviewed = requiredExpectedSources.Count(x =>
                    sourceSubmissions.Any(s => s.ExpectedCashSourceId == x.Id && s.Status == "Reviewed")),
                RequiredSourceRecordReconciliationsReviewed = requiredExpectedSources.Count(x =>
                    sourceSubmissions.Any(s => s.ExpectedCashSourceId == x.Id &&
                        s.Status == "Reviewed" && s.RecordLevelStatus == "Reviewed")),
                RegisteredSourceSubmissionCoverageComplete = registeredSourceSubmissionCoverageComplete,
                RecordLevelSourceReconciliationComplete = recordLevelSourceReconciliationComplete,
                ExpectedSourceRegisterCompletenessCertified = expectedSourceRegisterCompletenessCertified,
                LiveSourceRegisterComplete = liveSourceRegisterComplete,
                ExpectedSourceRegisterAttestationStatus = registerAttestation?.Status,
                ExpectedSourceRegisterAttestationEvidence = registerAttestation?.EvidenceReference,
                UncapturedOrUnreconciledPostedBankMovements = uncapturedPostedMovements,
                MissingPostedArSettlementCoverage = missingArSettlementCoverage,
                MissingPostedApSettlementCoverage = missingApSettlementCoverage,
                PendingOrDraftActuals = pendingActuals,
                InvalidCertifiedActuals = invalidCertifiedActuals,
                Reconciled = reconciled,
                Sections = new[]
                {
                    new { Section = "Operating", BudgetBasisActual = budgetOperating,
                        StatementOfCashFlowsActual = cashFlow.NetCashFromOperating,
                        RequiredAdjustment = operatingDifference, ApprovedAdjustment = operatingAdjustment,
                        UnreconciledDifference = operatingBridgeDifference },
                    new { Section = "Investing", BudgetBasisActual = budgetInvesting,
                        StatementOfCashFlowsActual = cashFlow.NetCashFromInvesting,
                        RequiredAdjustment = investingDifference, ApprovedAdjustment = investingAdjustment,
                        UnreconciledDifference = investingBridgeDifference },
                    new { Section = "Financing", BudgetBasisActual = budgetFinancing,
                        StatementOfCashFlowsActual = cashFlow.NetCashFromFinancing,
                        RequiredAdjustment = financingDifference, ApprovedAdjustment = financingAdjustment,
                        UnreconciledDifference = financingBridgeDifference }
                },
                ApprovedAdjustments = adjustments
            });
        }

        [HttpGet("cash-period-attestations")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetCashPeriodAttestations([FromQuery] int year, CancellationToken ct)
        {
            var items = await _db.PublicSectorCashPeriodAttestations
                .Where(x => x.CompanyId == CompanyId && x.PeriodStart.Year == year)
                .OrderBy(x => x.PeriodStart).ToListAsync(ct);
            return Ok(items);
        }

        [HttpPost("cash-period-attestations")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateCashPeriodAttestation(CreateCashPeriodAttestationRequest req, CancellationToken ct)
        {
            try
            {
                return Ok(await _publicSector.CreateCashPeriodAttestationAsync(CompanyId,
                    req.BudgetVersionId, req.EvidenceReference, UserId, ct));
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("cash-period-attestations/{id}/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitCashPeriodAttestation(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitCashPeriodAttestationAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("cash-period-attestations/{id}/certify")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CertifyCashPeriodAttestation(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.CertifyCashPeriodAttestationAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-execution-actuals")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CreateBudgetExecutionActual(CreateBudgetExecutionActualRequest req, CancellationToken ct)
        {
            try
            {
                var actual = await _publicSector.CreateBudgetExecutionActualAsync(CompanyId,
                    req.BudgetVersionId, req.BudgetVersionLineId, req.EventKind, req.SourceKind,
                    req.SourceRecordId, req.EffectiveDate, req.CashFlowSection, UserId, ct);
                return Ok(actual);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-execution-actuals/{id}/submit")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitBudgetExecutionActual(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.SubmitBudgetExecutionActualAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("budget-execution-actuals/{id}/certify")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> CertifyBudgetExecutionActual(Guid id, CancellationToken ct)
        {
            try { return Ok(await _publicSector.CertifyBudgetExecutionActualAsync(CompanyId, id, UserId, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
