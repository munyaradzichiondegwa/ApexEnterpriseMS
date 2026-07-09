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
        List<TrialBalanceLine> Assets, List<TrialBalanceLine> Liabilities, List<TrialBalanceLine> Equity);

    /// <summary>PRD Module 10 (Reporting & BI): sub-5-second core financial statements computed
    /// directly from posted journal lines as of any date, satisfying the "financial statements at
    /// any date, for any historical period" strategic goal (Section 1.1).</summary>
    public class ReportingService
    {
        private readonly ApexErpDbContext _db;

        public ReportingService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<List<TrialBalanceLine>> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
        {
            var accounts = await _db.GlAccounts.Where(a => a.CompanyId == companyId && a.IsActive).ToListAsync(ct);

            var movementsByAccount = await _db.JournalLines
                .Where(l => l.JournalEntry!.CompanyId == companyId
                            && l.JournalEntry.Status == JournalStatus.Posted
                            && l.JournalEntry.EntryDate <= asOfDate)
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
                .Where(a => a.CompanyId == companyId && a.IsActive && (a.Type == GlAccountType.Income || a.Type == GlAccountType.Expense))
                .ToListAsync(ct);

            var movements = await _db.JournalLines
                .Where(l => l.JournalEntry!.CompanyId == companyId && l.JournalEntry.Status == JournalStatus.Posted
                            && l.JournalEntry.EntryDate >= fromDate && l.JournalEntry.EntryDate <= toDate)
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

            var ociLines = await GetOciLinesAsync(companyId, fromDate, toDate, ct);
            var totalOci = ociLines.Sum(l => l.Amount);
            var netProfit = totalIncome - totalExpense;

            return new IncomeStatementResult(totalIncome, totalExpense, netProfit, incomeLines, expenseLines,
                ociLines, totalOci, netProfit + totalOci);
        }

        private async Task<List<OciLine>> GetOciLinesAsync(Guid companyId, DateTime fromDate, DateTime toDate, CancellationToken ct)
        {
            var ociAccounts = await _db.GlAccounts
                .Where(a => a.CompanyId == companyId && a.OciCategory != OciCategory.NotApplicable)
                .ToListAsync(ct);
            if (!ociAccounts.Any()) return new List<OciLine>();

            var accountIds = ociAccounts.Select(a => a.Id).ToList();
            var movements = await _db.JournalLines
                .Where(l => accountIds.Contains(l.GlAccountId) && l.JournalEntry!.CompanyId == companyId
                            && l.JournalEntry.Status == JournalStatus.Posted
                            && l.JournalEntry.EntryDate >= fromDate && l.JournalEntry.EntryDate <= toDate)
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
            var accounts = await _db.GlAccounts.Where(a => a.CompanyId == companyId).ToDictionaryAsync(a => a.Code, ct);

            var incomeStatement = await GetIncomeStatementAsync(companyId,
                new DateTime(asOfDate.Year, 1, 1), asOfDate, ct);

            var assets = tb.Where(l => l.Type == GlAccountType.Asset).ToList();
            var liabilities = tb.Where(l => l.Type == GlAccountType.Liability).ToList();
            var equity = tb.Where(l => l.Type == GlAccountType.Equity).ToList();

            // Retained earnings for the year (current year profit) rolls into equity for presentation purposes.
            equity.Add(new TrialBalanceLine("RE-YTD", "Retained Earnings (Current Year)", GlAccountType.Equity, 0, incomeStatement.NetProfit));

            var totalAssets = assets.Sum(a => a.Debit - a.Credit);
            var totalLiabilities = liabilities.Sum(l => l.Credit - l.Debit);
            var totalEquity = equity.Sum(e => e.Credit - e.Debit);

            return new BalanceSheetResult(totalAssets, totalLiabilities, totalEquity, assets, liabilities, equity);
        }

        private static bool IsDebitNormal(GlAccountType type) => type is GlAccountType.Asset or GlAccountType.Expense;
    }
}
