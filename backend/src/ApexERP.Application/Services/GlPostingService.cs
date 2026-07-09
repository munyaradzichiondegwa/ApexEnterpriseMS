using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record GlLineRequest(Guid GlAccountId, decimal Debit, decimal Credit, string Currency,
        decimal ExchangeRate, Guid? CostCentreId = null, string? Narrative = null, Guid? FundCentreId = null, Guid? GrantId = null);

    /// <summary>
    /// The single place in the system that ever writes to journal_entries / journal_lines.
    /// Every sub-ledger (AR, AP, Payroll, Inventory, Fixed Assets, Bank, Manufacturing) calls
    /// this to auto-post to the GL, satisfying the PRD's "GL auto-posting >= 98%" success metric
    /// and the "sub-10-second auto-posting" strategic goal (Section 1.1 / 1.2).
    /// </summary>
    public class GlPostingService
    {
        private readonly ApexErpDbContext _db;

        public GlPostingService(ApexErpDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Validates that debits == credits (in base/functional currency) and that the target
        /// accounting period is open, then persists the journal, updates GL account running
        /// balances, and returns the posted JournalEntry. Throws InvalidOperationException on
        /// any imbalance -- the system must never allow an unbalanced entry to post.
        /// </summary>
        public async Task<JournalEntry> PostAsync(
            Guid companyId,
            DateTime entryDate,
            JournalSource source,
            string? description,
            string? reference,
            IEnumerable<GlLineRequest> lines,
            CancellationToken ct = default)
        {
            var lineList = lines.ToList();
            if (lineList.Count < 2)
                throw new InvalidOperationException("A journal entry requires at least two lines.");

            // Compute base-currency debit/credit for every line and validate balance.
            decimal totalBaseDebit = 0m, totalBaseCredit = 0m;
            var journalLines = new List<JournalLine>();
            foreach (var l in lineList)
            {
                if (l.Debit < 0 || l.Credit < 0)
                    throw new InvalidOperationException("Debit/credit amounts cannot be negative.");
                if (l.Debit > 0 && l.Credit > 0)
                    throw new InvalidOperationException("A single journal line cannot carry both a debit and a credit.");

                var baseDebit = Math.Round(l.Debit * l.ExchangeRate, 6);
                var baseCredit = Math.Round(l.Credit * l.ExchangeRate, 6);
                totalBaseDebit += baseDebit;
                totalBaseCredit += baseCredit;

                journalLines.Add(new JournalLine
                {
                    GlAccountId = l.GlAccountId,
                    Debit = l.Debit,
                    Credit = l.Credit,
                    Currency = l.Currency,
                    ExchangeRate = l.ExchangeRate,
                    BaseDebit = baseDebit,
                    BaseCredit = baseCredit,
                    CostCentreId = l.CostCentreId,
                    Narrative = l.Narrative,
                    FundCentreId = l.FundCentreId,
                    GrantId = l.GrantId
                });
            }

            if (Math.Round(totalBaseDebit - totalBaseCredit, 6) != 0m)
                throw new InvalidOperationException(
                    $"Journal entry is not balanced: base debit total {totalBaseDebit:N6} != base credit total {totalBaseCredit:N6}.");

            var period = await GetOrCreatePeriodAsync(companyId, entryDate, ct);
            if (period.Status != PeriodStatus.Open)
                throw new InvalidOperationException($"Accounting period {period.Year}-{period.PeriodNo:D2} is {period.Status} and cannot accept new postings.");

            var entryNo = await GenerateEntryNoAsync(companyId, entryDate, ct);

            var journal = new JournalEntry
            {
                CompanyId = companyId,
                EntryNo = entryNo,
                Description = description,
                EntryDate = entryDate,
                PeriodId = period.Id,
                Source = source,
                Reference = reference,
                Status = JournalStatus.Posted,
                Lines = journalLines
            };

            _db.JournalEntries.Add(journal);

            // Update running GL account balances (Asset/Expense: debit increases; Liability/Equity/Income: credit increases)
            var accountIds = journalLines.Select(l => l.GlAccountId).Distinct().ToList();
            var accounts = await _db.GlAccounts.IgnoreQueryFilters()
                .Where(a => accountIds.Contains(a.Id) && a.CompanyId == companyId).ToDictionaryAsync(a => a.Id, ct);
            foreach (var line in journalLines)
            {
                if (!accounts.TryGetValue(line.GlAccountId, out var account))
                    throw new InvalidOperationException($"GL account {line.GlAccountId} not found.");

                var netMovement = IsDebitNormal(account.Type)
                    ? line.BaseDebit - line.BaseCredit
                    : line.BaseCredit - line.BaseDebit;
                account.CurrentBalance += netMovement;
            }

            await _db.SaveChangesAsync(ct);
            return journal;
        }

        /// <summary>Reverses a posted journal entry with an equal-and-opposite entry (period-end corrections, cancellations).</summary>
        public async Task<JournalEntry> ReverseAsync(Guid companyId, Guid journalEntryId, DateTime reversalDate, string reason, CancellationToken ct = default)
        {
            var original = await _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.Id == journalEntryId && j.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Journal entry not found.");

            if (original.Status != JournalStatus.Posted)
                throw new InvalidOperationException("Only posted journal entries can be reversed.");

            var reversedLines = original.Lines.Select(l => new GlLineRequest(
                l.GlAccountId, l.Credit, l.Debit, l.Currency, l.ExchangeRate, l.CostCentreId, $"Reversal: {reason}"));

            var reversal = await PostAsync(companyId, reversalDate, original.Source,
                $"Reversal of {original.EntryNo}: {reason}", original.Reference, reversedLines, ct);

            reversal.ReversalOfId = original.Id;
            original.Status = JournalStatus.Reversed;
            await _db.SaveChangesAsync(ct);
            return reversal;
        }

        private static bool IsDebitNormal(GlAccountType type) =>
            type is GlAccountType.Asset or GlAccountType.Expense;

        private async Task<AccountingPeriod> GetOrCreatePeriodAsync(Guid companyId, DateTime date, CancellationToken ct)
        {
            var existing = await _db.AccountingPeriods.IgnoreQueryFilters().FirstOrDefaultAsync(
                p => p.CompanyId == companyId && p.Year == date.Year && p.PeriodNo == date.Month, ct);
            if (existing != null) return existing;

            var period = new AccountingPeriod
            {
                CompanyId = companyId,
                Year = date.Year,
                PeriodNo = date.Month,
                StartDate = new DateTime(date.Year, date.Month, 1),
                EndDate = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)),
                Status = PeriodStatus.Open
            };
            _db.AccountingPeriods.Add(period);
            await _db.SaveChangesAsync(ct);
            return period;
        }

        private async Task<string> GenerateEntryNoAsync(Guid companyId, DateTime date, CancellationToken ct)
        {
            var prefix = $"JE-{date:yyyyMM}-";
            var countThisMonth = await _db.JournalEntries.IgnoreQueryFilters()
                .CountAsync(j => j.CompanyId == companyId && j.EntryNo.StartsWith(prefix), ct);
            return $"{prefix}{(countThisMonth + 1):D5}";
        }
    }
}
