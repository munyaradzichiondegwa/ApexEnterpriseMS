using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record PpeMovementLine(string CategoryName, decimal OpeningCost, decimal Additions, decimal DisposalsAtCost,
        decimal Revaluations, decimal ClosingCost, decimal OpeningAccDep, decimal DepreciationCharge,
        decimal AccDepOnDisposals, decimal ClosingAccDep, decimal ClosingNbv);
    public record InventoryNoteLine(string ItemName, string ValuationMethod, decimal QuantityOnHand, decimal AverageCost, decimal TotalValue);
    public record ShareCapitalNoteLine(DateTime Date, string Description, decimal Amount);
    public record RelatedPartyNoteLine(string CounterpartyCompanyName, decimal Amount, string Direction, IntercompanyStatus Status);
    public record SegmentNoteLine(string SegmentName, decimal Revenue, decimal Expense, decimal Result);

    public record NotesToFinancialStatementsResult(
        string AccountingPoliciesNote,
        List<PpeMovementLine> PropertyPlantEquipment,
        List<InventoryNoteLine> Inventory,
        object TradeReceivablesAgeing,
        object TradePayablesAgeing,
        List<ShareCapitalNoteLine> ShareCapital,
        List<RelatedPartyNoteLine> RelatedPartyTransactions,
        List<SegmentNoteLine> SegmentReporting,
        List<FinancialStatementNote> OtherDisclosures);

    /// <summary>
    /// Necessary Financial Report #5: Notes to the Financial Statements. Every section here is
    /// derived from real posted data (not lorem-ipsum placeholders) except free-text disclosures
    /// (contingent liabilities, subsequent events) which are inherently qualitative and captured via
    /// FinancialStatementNote for the preparer to complete.
    /// </summary>
    public class NotesToFinancialStatementsService
    {
        private readonly ApexErpDbContext _db;
        private readonly ArService _ar;
        private readonly ApService _ap;

        public NotesToFinancialStatementsService(ApexErpDbContext db, ArService ar, ApService ap)
        {
            _db = db;
            _ar = ar;
            _ap = ap;
        }

        public async Task<NotesToFinancialStatementsResult> GetNotesAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default)
        {
            var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                ?? throw new InvalidOperationException("Company not found.");

            var policies = BuildAccountingPoliciesNote(company);
            var ppe = await BuildPpeMovementAsync(companyId, periodStart, periodEnd, ct);
            var inventory = await _db.InventoryItems
                .Where(i => i.CompanyId == companyId && i.IsActive && i.QuantityOnHand != 0)
                .Select(i => new InventoryNoteLine(i.Name, i.ValuationMethod.ToString(), i.QuantityOnHand, i.AverageCost, i.QuantityOnHand * i.AverageCost))
                .ToListAsync(ct);

            var arAgeing = await _ar.GetAgeingAsync(companyId, periodEnd, ct);
            var apAgeing = await _ap.GetAgeingAsync(companyId, periodEnd, ct);

            var shareCapital = await BuildShareCapitalNoteAsync(companyId, periodStart, periodEnd, ct);
            var relatedParty = await BuildRelatedPartyNoteAsync(companyId, periodStart, periodEnd, ct);
            var segments = await BuildSegmentNoteAsync(companyId, periodStart, periodEnd, ct);

            var otherDisclosures = await _db.FinancialStatementNotes
                .Where(n => n.CompanyId == companyId && n.FinancialYear == periodEnd.Year)
                .OrderBy(n => n.DisplayOrder)
                .ToListAsync(ct);

            return new NotesToFinancialStatementsResult(policies, ppe, inventory, arAgeing, apAgeing, shareCapital, relatedParty, segments, otherDisclosures);
        }

        private static string BuildAccountingPoliciesNote(Company company)
        {
            return
                $"Basis of preparation: these financial statements are prepared on the {(company.AccountingBasis == AccountingBasis.Accrual ? "accrual" : "cash")} " +
                $"basis of accounting under the historical cost convention, on a going concern basis. " +
                $"Functional and presentation currency: {company.FunctionalCurrency}. " +
                "Revenue is recognised when control of goods or services transfers to the customer. " +
                "Inventories are stated at the lower of cost (determined on a FIFO or weighted-average basis, " +
                "per item as configured) and net realisable value. Property, plant and equipment is stated at cost " +
                "(or, where an asset has been revalued under the IAS 16 revaluation model, at fair value) less " +
                "accumulated depreciation; depreciation is charged on a straight-line or reducing-balance basis " +
                "over each asset's estimated useful life. Foreign currency balances are translated at rates ruling " +
                "at the reporting date; the resulting exchange differences are recognised in profit or loss, except " +
                "for differences arising on translation of a foreign operation, which are recognised in other " +
                "comprehensive income.";
        }

        private async Task<List<PpeMovementLine>> BuildPpeMovementAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var categories = await _db.AssetCategories.Where(c => c.CompanyId == companyId).ToListAsync(ct);
            var assets = await _db.FixedAssets.Where(a => a.CompanyId == companyId).ToListAsync(ct);
            var result = new List<PpeMovementLine>();

            foreach (var cat in categories)
            {
                var inCategory = assets.Where(a => a.CategoryId == cat.Id).ToList();
                var openingCost = inCategory.Where(a => a.AcquisitionDate < periodStart).Sum(a => a.AcquisitionCost);
                var additions = inCategory.Where(a => a.AcquisitionDate >= periodStart && a.AcquisitionDate <= periodEnd).Sum(a => a.AcquisitionCost);
                var disposalsAtCost = inCategory.Where(a => a.DisposalDate != null && a.DisposalDate >= periodStart && a.DisposalDate <= periodEnd).Sum(a => a.AcquisitionCost);
                var revaluations = await _db.AssetRevaluations
                    .Where(r => r.CompanyId == companyId && inCategory.Select(a => a.Id).Contains(r.FixedAssetId)
                                && r.RevaluationDate >= periodStart && r.RevaluationDate <= periodEnd)
                    .SumAsync(r => r.SurplusAmount, ct);
                var closingCost = openingCost + additions - disposalsAtCost + revaluations;

                var depreciationCharge = await _db.DepreciationEntries
                    .Where(d => d.CompanyId == companyId && inCategory.Select(a => a.Id).Contains(d.FixedAssetId)
                                && d.PeriodDate >= periodStart && d.PeriodDate <= periodEnd)
                    .SumAsync(d => d.Amount, ct);
                var openingAccDep = inCategory.Where(a => a.AcquisitionDate < periodStart).Sum(a => a.AccumulatedDepreciation) - depreciationCharge;
                var accDepOnDisposals = 0m; // disposals already zero out AccumulatedDepreciation in FixedAssetService.DisposeAsync
                var closingAccDep = inCategory.Where(a => a.Status == AssetStatus.Active).Sum(a => a.AccumulatedDepreciation);
                var closingNbv = inCategory.Where(a => a.Status == AssetStatus.Active).Sum(a => a.CurrentNbv);

                if (openingCost == 0 && additions == 0 && inCategory.Count == 0) continue;

                result.Add(new PpeMovementLine(cat.Name, openingCost, additions, disposalsAtCost, revaluations, closingCost,
                    Math.Max(openingAccDep, 0), depreciationCharge, accDepOnDisposals, closingAccDep, closingNbv));
            }
            return result;
        }

        private async Task<List<ShareCapitalNoteLine>> BuildShareCapitalNoteAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var shareCapitalAccountIds = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && a.Type == GlAccountType.Equity && a.Name.Contains("Share Capital"))
                .Select(a => a.Id).ToListAsync(ct);

            var lines = await _db.JournalLines
                .Where(l => shareCapitalAccountIds.Contains(l.GlAccountId) && l.JournalEntry!.CompanyId == companyId
                            && l.JournalEntry.Status == JournalStatus.Posted
                            && l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate <= periodEnd)
                .Select(l => new ShareCapitalNoteLine(l.JournalEntry!.EntryDate, l.Narrative ?? l.JournalEntry.Description ?? "Share capital movement", l.BaseCredit - l.BaseDebit))
                .ToListAsync(ct);
            return lines;
        }

        private async Task<List<RelatedPartyNoteLine>> BuildRelatedPartyNoteAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var transactions = await _db.IntercompanyTransactions.IgnoreQueryFilters()
                .Where(t => (t.FromCompanyId == companyId || t.ToCompanyId == companyId)
                            && t.TransactionDate >= periodStart && t.TransactionDate <= periodEnd)
                .ToListAsync(ct);

            var companyIds = transactions.Select(t => t.FromCompanyId).Concat(transactions.Select(t => t.ToCompanyId)).Distinct().ToList();
            var companies = await _db.Companies.Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

            return transactions.Select(t =>
            {
                var isSender = t.FromCompanyId == companyId;
                var counterpartyId = isSender ? t.ToCompanyId : t.FromCompanyId;
                var counterpartyName = companies.TryGetValue(counterpartyId, out var c) ? c.Name : "Unknown group entity";
                return new RelatedPartyNoteLine(counterpartyName, t.Amount, isSender ? "Owed by" : "Owed to", t.Status);
            }).ToList();
        }

        private async Task<List<SegmentNoteLine>> BuildSegmentNoteAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var segments = await _db.Segments.Where(s => s.CompanyId == companyId && s.IsActive).ToListAsync(ct);
            if (!segments.Any()) return new List<SegmentNoteLine>();

            var costCentresBySegment = await _db.CostCentres.Where(c => c.CompanyId == companyId && c.SegmentId != null).ToListAsync(ct);

            var movements = await _db.JournalLines
                .Where(l => l.JournalEntry!.CompanyId == companyId && l.JournalEntry.Status == JournalStatus.Posted
                            && l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate <= periodEnd
                            && l.CostCentreId != null)
                .Select(l => new { l.CostCentreId, l.BaseDebit, l.BaseCredit, l.GlAccountId })
                .ToListAsync(ct);

            var accountTypes = await _db.GlAccounts.Where(a => a.CompanyId == companyId).ToDictionaryAsync(a => a.Id, a => a.Type, ct);

            var result = new List<SegmentNoteLine>();
            foreach (var seg in segments)
            {
                var ccIds = costCentresBySegment.Where(c => c.SegmentId == seg.Id).Select(c => c.Id).ToList();
                var segMovements = movements.Where(m => ccIds.Contains(m.CostCentreId!.Value)).ToList();
                var revenue = segMovements.Where(m => accountTypes.GetValueOrDefault(m.GlAccountId) == GlAccountType.Income).Sum(m => m.BaseCredit - m.BaseDebit);
                var expense = segMovements.Where(m => accountTypes.GetValueOrDefault(m.GlAccountId) == GlAccountType.Expense).Sum(m => m.BaseDebit - m.BaseCredit);
                result.Add(new SegmentNoteLine(seg.Name, revenue, expense, revenue - expense));
            }
            return result;
        }
    }
}
