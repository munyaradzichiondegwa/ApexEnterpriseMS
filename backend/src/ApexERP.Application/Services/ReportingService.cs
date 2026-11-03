using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record TrialBalanceLine(string Code, string Name, GlAccountType Type, decimal Debit, decimal Credit);
    public record OciLine(string AccountName, OciCategory Category, decimal Amount);
    public record IncomeStatementResult(decimal TotalIncome, decimal TotalExpense, decimal NetProfit, List<TrialBalanceLine> IncomeLines, List<TrialBalanceLine> ExpenseLines,
        List<OciLine> OciLines, decimal TotalOci, decimal TotalComprehensiveIncome);

    public record BalanceSheetResult(decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity,
        List<TrialBalanceLine> Assets, List<TrialBalanceLine> Liabilities, List<TrialBalanceLine> Equity)
    {
        /// <summary>Assets minus (Liabilities + Equity). The accounting equation requires this to be zero.</summary>
        public decimal Difference => Math.Round(TotalAssets - TotalLiabilities - TotalEquity, 6);
        public bool IsBalanced => Difference == 0m;
    }

    /// <summary>Control checks behind the "reconciliation" KPIs: a non-zero value anywhere means the books need attention.</summary>
    public record IntegrityCheckResult(DateTime AsOf, decimal TrialBalanceDebit, decimal TrialBalanceCredit, decimal TrialBalanceDifference,
        bool TrialBalanceBalanced, decimal BalanceSheetDifference, bool BalanceSheetBalanced, int UnbalancedPostedJournals, int PendingJournals);

    /// <summary>PRD Module 10 (Reporting & BI): sub-5-second core financial statements computed
    /// directly from posted journal lines as of any date, satisfying the "financial statements at
    /// any date, for any historical period" strategic goal (Section 1.1).
    ///
    /// Reporting rules (repository audit finding 3 and follow-up review):
    ///   * Ledger activity means Posted AND Reversed journals. A reversed original stays in the ledger; it is
    ///     offset by its (posted) reversal entry, so excluding it would show the reversal without what it offsets.
    ///   * Inactive accounts are never dropped: a deactivated account keeps its historical balances.
    ///   * "As of" / period-end dates include the whole day, not just midnight.
    ///   * The balance sheet carries ALL unclosed profit (prior periods and current fiscal year), so it balances
    ///     whether or not year-end closing entries have been posted.</summary>
    public class ReportingService
    {
        private readonly ApexErpDbContext _db;

        public ReportingService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<List<TrialBalanceLine>> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
        {
            // No IsActive filter: see class remarks. Accounts with no activity at all are skipped below anyway.
            var accounts = await _db.GlAccounts.Where(a => a.CompanyId == companyId).ToListAsync(ct);

            var asOfExclusive = ReportingDates.EndExclusive(asOfDate);
            var movementsByAccount = await _db.JournalLines
                .Where(l => l.JournalEntry!.CompanyId == companyId
                            && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed)
                            && l.JournalEntry.EntryDate < asOfExclusive)
                .GroupBy(l => l.GlAccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.BaseDebit), Credit = g.Sum(x => x.BaseCredit) })
                .ToListAsync(ct);

            var lookup = movementsByAccount.ToDictionary(m => m.AccountId, m => (m.Debit, m.Credit));

            var result = new List<TrialBalanceLine>();
            foreach (var acc in accounts.OrderBy(a => a.Code))
            {
                var (debit, credit) = lookup.TryGetValue(acc.Id, out var m) ? m : (0m, 0m);
                var net = IsDebitNormal(acc.Type) ? debit - credit : credit - debit;
                if (net == 0 && debit == 0 && credit == 0) continue;

                if (IsDebitNormal(acc.Type))
                    result.Add(new TrialBalanceLine(acc.Code, acc.Name, acc.Type, Math.Max(net, 0), Math.Max(-net, 0)));
                else
                    result.Add(new TrialBalanceLine(acc.Code, acc.Name, acc.Type, Math.Max(-net, 0), Math.Max(net, 0)));
            }
            return result;
        }

        public async Task<IncomeStatementResult> GetIncomeStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate, CancellationToken ct = default)
        {
            var accounts = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && (a.Type == GlAccountType.Income || a.Type == GlAccountType.Expense))
                .ToListAsync(ct);

            var start = ReportingDates.StartInclusive(fromDate);
            var endExclusive = ReportingDates.EndExclusive(toDate);
            var movements = await _db.JournalLines
                .Where(l => l.JournalEntry!.CompanyId == companyId
                            && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed)
                            && l.JournalEntry.EntryDate >= start && l.JournalEntry.EntryDate < endExclusive)
                .GroupBy(l => l.GlAccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.BaseDebit), Credit = g.Sum(x => x.BaseCredit) })
                .ToListAsync(ct);
            var lookup = movements.ToDictionary(m => m.AccountId, m => (m.Debit, m.Credit));

            var incomeLines = new List<TrialBalanceLine>();
            var expenseLines = new List<TrialBalanceLine>();
            decimal totalIncome = 0, totalExpense = 0;

            foreach (var acc in accounts.OrderBy(a => a.Code))
            {
                var (debit, credit) = lookup.TryGetValue(acc.Id, out var m) ? m : (0m, 0m);
                if (acc.Type == GlAccountType.Income)
                {
                    var amount = credit - debit;
                    if (amount == 0) continue;
                    incomeLines.Add(new TrialBalanceLine(acc.Code, acc.Name, acc.Type, 0, amount));
                    totalIncome += amount;
                }
                else
                {
                    var amount = debit - credit;
                    if (amount == 0) continue;
                    expenseLines.Add(new TrialBalanceLine(acc.Code, acc.Name, acc.Type, amount, 0));
                    totalExpense += amount;
                }
            }

            var ociLines = await GetOciLinesAsync(companyId, start, endExclusive, ct);
            var totalOci = ociLines.Sum(l => l.Amount);
            var netProfit = totalIncome - totalExpense;

            return new IncomeStatementResult(totalIncome, totalExpense, netProfit, incomeLines, expenseLines,
                ociLines, totalOci, netProfit + totalOci);
        }

        private async Task<List<OciLine>> GetOciLinesAsync(Guid companyId, DateTime start, DateTime endExclusive, CancellationToken ct)
        {
            var ociAccounts = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && a.OciCategory != OciCategory.NotApplicable)
                .ToListAsync(ct);
            if (!ociAccounts.Any()) return new List<OciLine>();

            var accountIds = ociAccounts.Select(a => a.Id).ToList();
            var movements = await _db.JournalLines
                .Where(l => accountIds.Contains(l.GlAccountId) && l.JournalEntry!.CompanyId == companyId
                            && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed)
                            && l.JournalEntry.EntryDate >= start && l.JournalEntry.EntryDate < endExclusive)
                .GroupBy(l => l.GlAccountId)
                .Select(g => new { AccountId = g.Key, Net = g.Sum(x => x.BaseCredit - x.BaseDebit) })
                .ToListAsync(ct);

            return ociAccounts
                .Select(a => new OciLine(a.Name, a.OciCategory, movements.FirstOrDefault(m => m.AccountId == a.Id)?.Net ?? 0m))
                .Where(l => l.Amount != 0)
                .ToList();
        }

        public async Task<BalanceSheetResult> GetBalanceSheetAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
        {
            var tb = await GetTrialBalanceAsync(companyId, asOfDate, ct);

            // Current fiscal year (honours the company's fiscal-year start month, not just 1 January).
            var startMonth = await _db.Companies.Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync(ct);
            var fiscalYearStart = FiscalCalendar.FiscalYearStart(asOfDate, startMonth);
            var currentYear = await GetIncomeStatementAsync(companyId, fiscalYearStart, asOfDate, ct);

            var assets = tb.Where(l => l.Type == GlAccountType.Asset).ToList();
            var liabilities = tb.Where(l => l.Type == GlAccountType.Liability).ToList();
            var equity = tb.Where(l => l.Type == GlAccountType.Equity).ToList();

            // Income and expense accounts are cumulative until a year-end closing entry moves them to retained
            // earnings. Whatever has NOT been closed is still part of equity, so it is presented here: prior periods
            // first, then the current fiscal year. If closing entries have been posted these balances are simply zero.
            var cumulativeProfit = tb.Where(l => l.Type == GlAccountType.Income).Sum(l => l.Credit - l.Debit)
                                 - tb.Where(l => l.Type == GlAccountType.Expense).Sum(l => l.Debit - l.Credit);
            var priorProfit = Math.Round(cumulativeProfit - currentYear.NetProfit, 6);

            if (priorProfit != 0m)
                equity.Add(new TrialBalanceLine("RE-PY", "Retained Earnings (Prior Periods, not yet closed)", GlAccountType.Equity,
                    Math.Max(-priorProfit, 0), Math.Max(priorProfit, 0)));
            equity.Add(new TrialBalanceLine("RE-YTD", "Retained Earnings (Current Year)", GlAccountType.Equity,
                Math.Max(-currentYear.NetProfit, 0), Math.Max(currentYear.NetProfit, 0)));

            var totalAssets = assets.Sum(a => a.Debit - a.Credit);
            var totalLiabilities = liabilities.Sum(l => l.Credit - l.Debit);
            var totalEquity = equity.Sum(e => e.Credit - e.Debit);

            return new BalanceSheetResult(totalAssets, totalLiabilities, totalEquity, assets, liabilities, equity);
        }

        /// <summary>
        /// Control totals for the books: trial balance debits vs credits, the accounting equation, journals whose lines do
        /// not balance, and journals still waiting in the approval workflow. Every difference should be zero.
        /// </summary>
        public async Task<IntegrityCheckResult> GetIntegrityCheckAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
        {
            var tb = await GetTrialBalanceAsync(companyId, asOfDate, ct);
            var totalDebit = tb.Sum(l => l.Debit);
            var totalCredit = tb.Sum(l => l.Credit);
            var tbDifference = Math.Round(totalDebit - totalCredit, 6);

            var balanceSheet = await GetBalanceSheetAsync(companyId, asOfDate, ct);

            var unbalanced = await _db.JournalEntries
                .Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed))
                .Where(j => j.Lines.Sum(l => l.BaseDebit) != j.Lines.Sum(l => l.BaseCredit))
                .CountAsync(ct);

            var pending = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId &&
                (j.Status == JournalStatus.Draft || j.Status == JournalStatus.Submitted || j.Status == JournalStatus.Approved), ct);

            return new IntegrityCheckResult(asOfDate, totalDebit, totalCredit, tbDifference, tbDifference == 0m,
                balanceSheet.Difference, balanceSheet.IsBalanced, unbalanced, pending);
        }

        private static bool IsDebitNormal(GlAccountType type) => type is GlAccountType.Asset or GlAccountType.Expense;
    }
}
