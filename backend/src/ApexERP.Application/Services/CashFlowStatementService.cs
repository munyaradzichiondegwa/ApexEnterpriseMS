using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record CashFlowLine(string Label, decimal Amount);
    public record CashFlowStatementResult(
        DateTime PeriodStart, DateTime PeriodEnd,
        decimal NetProfit, List<CashFlowLine> OperatingAdjustments, decimal NetCashFromOperating,
        List<CashFlowLine> InvestingActivities, decimal NetCashFromInvesting,
        List<CashFlowLine> FinancingActivities, decimal NetCashFromFinancing,
        decimal NetIncreaseInCash, decimal CashAtStart, decimal CashAtEnd, decimal ReconciliationDifference);

    /// <summary>
    /// Necessary Financial Report #4 (IAS 7): Statement of Cash Flows, indirect method. Generic
    /// over any chart of accounts -- classification comes from GlAccount.CashFlowCategory (set at
    /// CoA setup time), not hardcoded account codes, so it works for any company's custom accounts.
    /// Investing activity purchases/proceeds are taken directly from the Fixed Asset register for
    /// precision (a gross-movement read of the GL asset account alone can't cleanly separate a
    /// period's purchases from its disposals).
    /// </summary>
    public class CashFlowStatementService
    {
        private readonly ApexErpDbContext _db;
        private readonly ReportingService _reporting;

        public CashFlowStatementService(ApexErpDbContext db, ReportingService reporting)
        {
            _db = db;
            _reporting = reporting;
        }

        public async Task<CashFlowStatementResult> GetStatementAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default)
        {
            var incomeStatement = await _reporting.GetIncomeStatementAsync(companyId, periodStart, periodEnd, ct);
            var netProfit = incomeStatement.NetProfit;

            var accounts = await _db.GlAccounts.Where(a => a.CompanyId == companyId).ToListAsync(ct);
            var openingTb = (await _reporting.GetTrialBalanceAsync(companyId, periodStart.AddDays(-1), ct))
                .ToDictionary(l => l.Code, l => l.Debit - l.Credit); // net debit balance
            var closingTb = (await _reporting.GetTrialBalanceAsync(companyId, periodEnd, ct))
                .ToDictionary(l => l.Code, l => l.Debit - l.Credit);

            // ---- Operating activities ----
            var operatingAdjustments = new List<CashFlowLine>();

            var depreciationCharge = await _db.DepreciationEntries
                .Where(d => d.CompanyId == companyId && d.PeriodDate >= periodStart && d.PeriodDate <= periodEnd)
                .SumAsync(d => d.Amount, ct);
            if (depreciationCharge != 0) operatingAdjustments.Add(new CashFlowLine("Depreciation (non-cash add-back)", depreciationCharge));

            decimal workingCapitalMovement = 0;
            foreach (var acc in accounts.Where(a => a.CashFlowCategory == CashFlowCategory.Operating))
            {
                var opening = openingTb.TryGetValue(acc.Code, out var ob) ? ob : 0m;
                var closing = closingTb.TryGetValue(acc.Code, out var cb) ? cb : 0m;
                var change = closing - opening; // net debit-balance change

                // Asset increase = cash used (outflow); liability "debit balance" is presented as
                // negative in our net-debit convention, so its increase (more negative) already
                // correctly flips sign below.
                var cashImpact = -change;
                if (cashImpact == 0) continue;

                var label = acc.Type == GlAccountType.Asset
                    ? $"{(cashImpact < 0 ? "Increase" : "Decrease")} in {acc.Name}"
                    : $"{(cashImpact > 0 ? "Increase" : "Decrease")} in {acc.Name}";
                operatingAdjustments.Add(new CashFlowLine(label, cashImpact));
                workingCapitalMovement += cashImpact;
            }

            var netCashFromOperating = netProfit + depreciationCharge + (workingCapitalMovement);

            // ---- Investing activities: precise figures straight from the Fixed Asset register ----
            var investing = new List<CashFlowLine>();
            var purchases = await _db.FixedAssets
                .Where(a => a.CompanyId == companyId && a.AcquisitionDate >= periodStart && a.AcquisitionDate <= periodEnd)
                .SumAsync(a => a.AcquisitionCost, ct);
            if (purchases != 0) investing.Add(new CashFlowLine("Purchase of property, plant & equipment", -purchases));

            var disposalProceeds = await _db.FixedAssets
                .Where(a => a.CompanyId == companyId && a.DisposalDate != null && a.DisposalDate >= periodStart && a.DisposalDate <= periodEnd)
                .SumAsync(a => a.DisposalProceeds ?? 0, ct);
            if (disposalProceeds != 0) investing.Add(new CashFlowLine("Proceeds from disposal of property, plant & equipment", disposalProceeds));

            var netCashFromInvesting = investing.Sum(i => i.Amount);

            // ---- Financing activities: accounts tagged Financing (share capital, loans, dividends payable) ----
            var financing = new List<CashFlowLine>();
            foreach (var acc in accounts.Where(a => a.CashFlowCategory == CashFlowCategory.Financing))
            {
                var opening = openingTb.TryGetValue(acc.Code, out var ob) ? ob : 0m;
                var closing = closingTb.TryGetValue(acc.Code, out var cb) ? cb : 0m;
                var change = closing - opening;
                var cashImpact = -change; // liability/equity increase (more negative net-debit) = cash inflow
                if (cashImpact == 0) continue;
                financing.Add(new CashFlowLine(acc.Name, cashImpact));
            }
            var netCashFromFinancing = financing.Sum(f => f.Amount);

            var netIncrease = netCashFromOperating + netCashFromInvesting + netCashFromFinancing;

            var cashAccounts = accounts.Where(a => a.CashFlowCategory == CashFlowCategory.CashAndCashEquivalents || a.IsBankAccount).ToList();
            var cashAtStart = cashAccounts.Sum(a => openingTb.TryGetValue(a.Code, out var ob) ? ob : 0m);
            var cashAtEnd = cashAccounts.Sum(a => closingTb.TryGetValue(a.Code, out var cb) ? cb : 0m);
            var reconciliationDifference = Math.Round((cashAtStart + netIncrease) - cashAtEnd, 2);

            return new CashFlowStatementResult(periodStart, periodEnd, netProfit, operatingAdjustments, netCashFromOperating,
                investing, netCashFromInvesting, financing, netCashFromFinancing, netIncrease, cashAtStart, cashAtEnd, reconciliationDifference);
        }
    }
}
