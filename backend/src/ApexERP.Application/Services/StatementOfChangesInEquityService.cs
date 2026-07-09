using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record EquityAccountMovement(string AccountName, decimal OpeningBalance, decimal ProfitForPeriod,
        decimal OciMovement, decimal DividendsDeclared, decimal ShareCapitalMovement, decimal OtherMovement, decimal ClosingBalance);
    public record StatementOfChangesInEquityResult(DateTime PeriodStart, DateTime PeriodEnd,
        List<EquityAccountMovement> Accounts, decimal TotalOpening, decimal TotalClosing);

    /// <summary>
    /// Necessary Financial Report #3 (IAS 1): reconstructs the movement in every equity account
    /// across a period -- opening balance, profit/loss transferred to retained earnings, OCI
    /// (revaluation surplus, FX translation reserve), dividends declared, share capital raised/
    /// redeemed, and any other equity-account postings -- reconciling to the closing balance shown
    /// on the Balance Sheet.
    /// </summary>
    public class StatementOfChangesInEquityService
    {
        private readonly ApexErpDbContext _db;
        private readonly ReportingService _reporting;

        public StatementOfChangesInEquityService(ApexErpDbContext db, ReportingService reporting)
        {
            _db = db;
            _reporting = reporting;
        }

        public async Task<StatementOfChangesInEquityResult> GetStatementAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default)
        {
            var equityAccounts = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && a.Type == GlAccountType.Equity)
                .ToListAsync(ct);

            // Opening balances: trial balance as of the day before the period starts.
            var openingTb = await _reporting.GetTrialBalanceAsync(companyId, periodStart.AddDays(-1), ct);
            var openingByCode = openingTb.Where(l => l.Type == GlAccountType.Equity).ToDictionary(l => l.Code, l => l.Credit - l.Debit);

            // Period movements per equity account, split by nature using JournalSource/OciCategory.
            var movements = await _db.JournalLines
                .Where(l => equityAccounts.Select(a => a.Id).Contains(l.GlAccountId)
                            && l.JournalEntry!.CompanyId == companyId
                            && l.JournalEntry.Status == JournalStatus.Posted
                            && l.JournalEntry.EntryDate >= periodStart && l.JournalEntry.EntryDate <= periodEnd)
                .Select(l => new { l.GlAccountId, l.BaseDebit, l.BaseCredit, Source = l.JournalEntry!.Source })
                .ToListAsync(ct);

            var incomeStatement = await _reporting.GetIncomeStatementAsync(companyId, periodStart, periodEnd, ct);
            var dividends = await _db.DividendDeclarations
                .Where(d => d.CompanyId == companyId && d.DeclarationDate >= periodStart && d.DeclarationDate <= periodEnd)
                .SumAsync(d => d.Amount, ct);

            var result = new List<EquityAccountMovement>();
            decimal totalOpening = 0, totalClosing = 0;

            foreach (var acc in equityAccounts)
            {
                var opening = openingByCode.TryGetValue(acc.Code, out var ob) ? ob : 0m;
                var accMovements = movements.Where(m => m.GlAccountId == acc.Id).ToList();
                var netMovement = accMovements.Sum(m => m.BaseCredit - m.BaseDebit);

                // Retained Earnings absorbs the period's profit/loss and dividend declarations.
                var isRetainedEarnings = acc.SubType?.Contains("Retained", StringComparison.OrdinalIgnoreCase) == true
                                          || acc.Name.Contains("Retained Earnings", StringComparison.OrdinalIgnoreCase);
                var profitForPeriod = isRetainedEarnings ? incomeStatement.NetProfit : 0m;
                var dividendsHere = isRetainedEarnings ? -dividends : 0m;

                var ociMovement = acc.OciCategory != OciCategory.NotApplicable ? netMovement : 0m;
                var shareCapitalMovement = acc.Name.Contains("Share Capital", StringComparison.OrdinalIgnoreCase) ? netMovement : 0m;
                var otherMovement = netMovement - ociMovement - shareCapitalMovement - (isRetainedEarnings ? netMovement : 0m);
                var closing = opening + profitForPeriod + ociMovement + dividendsHere + shareCapitalMovement + (isRetainedEarnings ? 0 : otherMovement);
                // For non-retained-earnings accounts, otherMovement already equals netMovement when not OCI/share capital.
                if (!isRetainedEarnings) closing = opening + ociMovement + shareCapitalMovement + otherMovement;

                result.Add(new EquityAccountMovement(acc.Name, opening, profitForPeriod, ociMovement, dividendsHere, shareCapitalMovement, otherMovement, closing));
                totalOpening += opening;
                totalClosing += closing;
            }

            return new StatementOfChangesInEquityResult(periodStart, periodEnd, result, totalOpening, totalClosing);
        }
    }
}
