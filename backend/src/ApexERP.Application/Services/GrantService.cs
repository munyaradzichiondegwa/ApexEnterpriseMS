using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record GrantBudgetVsActualLine(Guid GlAccountId, string AccountName, decimal Budget, decimal Actual, decimal Variance, decimal PercentConsumed, bool OverBudget);
    public record DonorReport(Grant Grant, DateTime PeriodStart, DateTime PeriodEnd, List<GrantBudgetVsActualLine> BudgetLines,
        decimal TotalBudget, decimal TotalActual, decimal IndirectCostRecovered, decimal FxVariance,
        int BeneficiaryCount, decimal TotalDistributed);

    /// <summary>PRD Module 15 (Grants, Donors & NGO Management): grant master, GL-account-level
    /// budgets with real-time restriction enforcement, indirect cost recovery posting, sub-award
    /// disbursement tracking, beneficiary/distribution tracking, and donor financial reports.</summary>
    public class GrantService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;
        private readonly TaxComplianceService _tax;

        public GrantService(ApexErpDbContext db, GlPostingService gl, TaxComplianceService tax)
        {
            _db = db;
            _gl = gl;
            _tax = tax;
        }

        public async Task<Grant> CreateGrantAsync(Guid companyId, string grantNo, string title, Guid donorCustomerId,
            decimal awardAmount, string currency, DateTime startDate, DateTime endDate, decimal indirectCostRatePct,
            string restrictionsJson, bool enforceRestriction, CancellationToken ct = default)
        {
            var grant = new Grant
            {
                CompanyId = companyId,
                GrantNo = grantNo,
                Title = title,
                DonorCustomerId = donorCustomerId,
                AwardAmount = awardAmount,
                Currency = currency,
                StartDate = startDate,
                EndDate = endDate,
                IndirectCostRatePct = indirectCostRatePct,
                RestrictionsJson = restrictionsJson,
                EnforceRestriction = enforceRestriction,
                Status = GrantStatus.Active
            };
            _db.Grants.Add(grant);
            await _db.SaveChangesAsync(ct);
            return grant;
        }

        public async Task<GrantBudgetLine> SetBudgetLineAsync(Guid companyId, Guid grantId, Guid glAccountId, int year, int? month, decimal amount, CancellationToken ct = default)
        {
            var grant = await _db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Grant not found.");
            var line = new GrantBudgetLine { GrantId = grantId, GlAccountId = glAccountId, PeriodYear = year, PeriodMonth = month, BudgetAmount = amount };
            _db.GrantBudgetLines.Add(line);
            await _db.SaveChangesAsync(ct);
            return line;
        }

        /// <summary>Checks whether posting `amount` against `glAccountId` for this grant would
        /// exceed its budget. Called before committing grant-tagged expenditure when the grant's
        /// EnforceRestriction flag is set (PRD: "prevents postings that would exceed grant budget").</summary>
        public async Task<bool> WouldExceedBudgetAsync(Guid companyId, Guid grantId, Guid glAccountId, decimal amount, CancellationToken ct = default)
        {
            var grant = await _db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Grant not found.");
            if (!grant.EnforceRestriction) return false;

            var budget = await _db.GrantBudgetLines
                .Where(l => l.GrantId == grantId && l.GlAccountId == glAccountId)
                .SumAsync(l => l.BudgetAmount, ct);
            if (budget == 0) return false; // no budget configured for this line = unrestricted

            var actual = await _db.JournalLines
                .Where(l => l.GrantId == grantId && l.GlAccountId == glAccountId && l.JournalEntry!.CompanyId == companyId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);

            return actual + amount > budget;
        }

        public async Task<List<GrantBudgetVsActualLine>> GetBudgetVsActualAsync(Guid companyId, Guid grantId, CancellationToken ct = default)
        {
            var budgetLines = await _db.GrantBudgetLines.Where(l => l.GrantId == grantId).ToListAsync(ct);
            var glAccountIds = budgetLines.Select(l => l.GlAccountId).Distinct().ToList();
            var accounts = await _db.GlAccounts.Where(a => glAccountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

            var result = new List<GrantBudgetVsActualLine>();
            foreach (var accountId in glAccountIds)
            {
                var budget = budgetLines.Where(l => l.GlAccountId == accountId).Sum(l => l.BudgetAmount);
                var actual = await _db.JournalLines
                    .Where(l => l.GrantId == grantId && l.GlAccountId == accountId && l.JournalEntry!.CompanyId == companyId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                    .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);
                var variance = budget - actual;
                var pct = budget == 0 ? 0 : Math.Round(actual / budget * 100, 2);
                result.Add(new GrantBudgetVsActualLine(accountId, accounts.GetValueOrDefault(accountId)?.Name ?? "Unknown", budget, actual, variance, pct, actual > budget));
            }
            return result;
        }

        /// <summary>Applies the grant's indirect cost rate to its eligible direct costs for the
        /// period and posts the recovery: Dr Grant Indirect Cost Expense, Cr Overhead Recovery Income.</summary>
        public async Task<JournalEntry> CalculateAndPostIndirectCostRecoveryAsync(Guid companyId, Guid grantId, DateTime periodDate,
            Guid indirectCostExpenseAccountId, Guid overheadRecoveryIncomeAccountId, CancellationToken ct = default)
        {
            var grant = await _db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Grant not found.");

            var directCosts = await _db.JournalLines
                .Where(l => l.GrantId == grantId && l.JournalEntry!.CompanyId == companyId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed)
                            && l.JournalEntry.EntryDate.Month == periodDate.Month && l.JournalEntry.EntryDate.Year == periodDate.Year)
                .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);

            var recoveryAmount = Math.Round(directCosts * (grant.IndirectCostRatePct / 100m), 6);
            if (recoveryAmount <= 0) throw new InvalidOperationException("No eligible direct costs to recover indirect costs against for this period.");

            return await _gl.PostAsync(companyId, periodDate, JournalSource.Manual,
                $"Indirect cost recovery - {grant.GrantNo}", grant.GrantNo,
                new[]
                {
                    new GlLineRequest(indirectCostExpenseAccountId, recoveryAmount, 0, "USD", 1m, null, "Indirect cost allocation"),
                    new GlLineRequest(overheadRecoveryIncomeAccountId, 0, recoveryAmount, "USD", 1m, null, "Overhead recovery income")
                }, ct);
        }

        public async Task<SubAward> CreateSubAwardAsync(Guid companyId, Guid grantId, string subAwardeeName, Guid? vendorId,
            decimal amount, string currency, DateTime startDate, DateTime endDate, string? reportingRequirements, CancellationToken ct = default)
        {
            var subAward = new SubAward
            {
                CompanyId = companyId,
                GrantId = grantId,
                SubAwardeeName = subAwardeeName,
                SubAwardeeVendorId = vendorId,
                Amount = amount,
                Currency = currency,
                StartDate = startDate,
                EndDate = endDate,
                ReportingRequirements = reportingRequirements,
                Status = SubAwardStatus.Active
            };
            _db.SubAwards.Add(subAward);
            await _db.SaveChangesAsync(ct);
            return subAward;
        }

        public async Task<SubAwardDisbursement> RecordDisbursementAsync(Guid companyId, Guid subAwardId, decimal amount, DateTime date, Guid? apPaymentId, CancellationToken ct = default)
        {
            var subAward = await _db.SubAwards.FirstOrDefaultAsync(s => s.Id == subAwardId && s.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Sub-award not found.");
            if (subAward.TotalDisbursed + amount > subAward.Amount)
                throw new InvalidOperationException("Disbursement would exceed the sub-award's total amount.");

            var disbursement = new SubAwardDisbursement { SubAwardId = subAwardId, Amount = amount, DisbursementDate = date, ApPaymentId = apPaymentId };
            _db.SubAwardDisbursements.Add(disbursement);
            subAward.TotalDisbursed += amount;
            await _db.SaveChangesAsync(ct);
            return disbursement;
        }

        /// <summary>Compiles a donor financial report: budget vs actual by GL category, indirect
        /// cost recovery, FX variance (if the grant currency differs from the functional currency),
        /// and beneficiary/distribution summary.</summary>
        public async Task<DonorReport> GenerateDonorReportAsync(Guid companyId, Guid grantId, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default)
        {
            var grant = await _db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Grant not found.");

            var budgetLines = await GetBudgetVsActualAsync(companyId, grantId, ct);
            var totalBudget = budgetLines.Sum(l => l.Budget);
            var totalActual = budgetLines.Sum(l => l.Actual);

            var indirectRecovered = await _db.JournalLines
                .Where(l => l.GrantId == grantId && l.JournalEntry!.CompanyId == companyId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed)
                            && l.JournalEntry.Reference == grant.GrantNo && l.JournalEntry.Description!.Contains("Indirect cost recovery"))
                .SumAsync(l => l.BaseDebit, ct);

            var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
            decimal fxVariance = 0;
            if (company != null && grant.Currency != company.FunctionalCurrency)
            {
                // Illustrative: real variance requires the rate at budget-set time vs. period-end rate.
                var periodEndRate = await _db.ExchangeRates
                    .Where(r => r.CompanyId == companyId && r.FromCurrency == grant.Currency && r.RateDate <= periodEnd)
                    .OrderByDescending(r => r.RateDate).FirstOrDefaultAsync(ct);
                if (periodEndRate != null)
                    fxVariance = _tax.CalculateRealisedFxGainLoss(totalActual, 1m, periodEndRate.Rate);
            }

            var distributions = await _db.Distributions.Where(d => d.GrantId == grantId && d.DistributionDate >= periodStart && d.DistributionDate <= periodEnd).ToListAsync(ct);
            var beneficiaryCount = distributions.Select(d => d.BeneficiaryId).Distinct().Count();
            var totalDistributed = distributions.Sum(d => d.Quantity);

            return new DonorReport(grant, periodStart, periodEnd, budgetLines, totalBudget, totalActual, indirectRecovered, fxVariance, beneficiaryCount, totalDistributed);
        }

        public async Task<GrantCloseoutChecklist> InitiateCloseoutAsync(Guid companyId, Guid grantId, CancellationToken ct = default)
        {
            var existing = await _db.GrantCloseoutChecklists.FirstOrDefaultAsync(c => c.GrantId == grantId && c.CompanyId == companyId, ct);
            if (existing != null) return existing;

            var checklist = new GrantCloseoutChecklist { CompanyId = companyId, GrantId = grantId };
            _db.GrantCloseoutChecklists.Add(checklist);
            await _db.SaveChangesAsync(ct);
            return checklist;
        }

        public async Task<GrantCloseoutChecklist> UpdateCloseoutChecklistAsync(Guid companyId, Guid checklistId,
            bool allExpendituresPosted, bool subAwardsClosed, bool indirectCostCalculated, bool finalReportGenerated,
            decimal? surplusAmount, string? surplusDisposition, Guid acknowledgedBy, CancellationToken ct = default)
        {
            var checklist = await _db.GrantCloseoutChecklists.FirstOrDefaultAsync(c => c.Id == checklistId && c.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Closeout checklist not found.");

            checklist.AllExpendituresPosted = allExpendituresPosted;
            checklist.SubAwardsClosed = subAwardsClosed;
            checklist.IndirectCostCalculated = indirectCostCalculated;
            checklist.FinalDonorReportGenerated = finalReportGenerated;
            checklist.SurplusIdentified = surplusAmount.HasValue && surplusAmount > 0;
            checklist.SurplusAmount = surplusAmount;
            checklist.SurplusDisposition = surplusDisposition;

            if (allExpendituresPosted && subAwardsClosed && indirectCostCalculated && finalReportGenerated)
            {
                checklist.AcknowledgedBy = acknowledgedBy;
                checklist.AcknowledgedAt = DateTime.UtcNow;
                var grant = await _db.Grants.FirstOrDefaultAsync(g => g.Id == checklist.GrantId && g.CompanyId == companyId, ct);
                if (grant != null) grant.Status = GrantStatus.Closed;
            }

            await _db.SaveChangesAsync(ct);
            return checklist;
        }
    }
}
