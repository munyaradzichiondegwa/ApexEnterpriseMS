using System.Diagnostics;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record GlLineRequest(Guid GlAccountId, decimal Debit, decimal Credit, string Currency,
        decimal ExchangeRate, Guid? CostCentreId = null, string? Narrative = null, Guid? FundCentreId = null, Guid? GrantId = null);

    /// <summary>Settings for the manual-journal controls (bound from configuration "Controls:JournalApproval").</summary>
    public sealed class JournalControlOptions
    {
        /// <summary>
        /// Segregation of duties. When false (the default) the person who prepared a manual journal cannot
        /// approve it. Set true only for single-user development or very small entities, and accept the
        /// reduced control.
        /// </summary>
        public bool AllowSelfApproval { get; set; }
    }

    /// <summary>
    /// The single place in the system that ever writes to journal_entries / journal_lines.
    /// Every sub-ledger (AR, AP, Payroll, Inventory, Fixed Assets, Bank, Manufacturing) calls
    /// this to auto-post to the GL, satisfying the PRD's "GL auto-posting >= 98%" success metric
    /// and the "sub-10-second auto-posting" strategic goal (Section 1.1 / 1.2).
    ///
    /// Integrity guarantees (repository audit findings 4-7, design document "atomic ledger posting"):
    ///   * every public operation runs inside one database transaction (joining the request's transaction);
    ///   * journal numbers are allocated under a transaction-scoped lock, with a unique index as backstop;
    ///   * a journal can be reversed at most once, atomically, and the reversal keeps every dimension tag
    ///     (cost centre, fund centre, grant) of the original;
    ///   * exchange rates must be positive, currencies valid, lines single-sided and the entry balanced;
    ///   * posting and period closing are serialised so a period cannot close mid-posting;
    ///   * account running balances are updated with atomic SQL increments (no lost updates);
    ///   * manual journals follow Draft -> Submitted -> Approved -> Posted with segregation of duties.
    /// </summary>
    public class GlPostingService
    {
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;
        private readonly JournalControlOptions _controls;

        public GlPostingService(ApexErpDbContext db, AuditService audit, JournalControlOptions controls)
        {
            _db = db;
            _audit = audit;
            _controls = controls;
        }

        // =============================================================================================
        // System postings (AR, AP, payroll, inventory...) -- posted immediately
        // =============================================================================================

        /// <summary>
        /// Validates that debits == credits (in base/functional currency) and that the target
        /// accounting period is open, then persists the journal, updates GL account running
        /// balances, and returns the posted JournalEntry. Throws InvalidOperationException on
        /// any imbalance -- the system must never allow an unbalanced entry to post.
        /// </summary>
        public Task<JournalEntry> PostAsync(
            Guid companyId,
            DateTime entryDate,
            JournalSource source,
            string? description,
            string? reference,
            IEnumerable<GlLineRequest> lines,
            CancellationToken ct = default,
            Guid? postedBy = null)
        {
            var lineList = lines.ToList();
            return _db.InTransactionAsync(
                token => PostCoreAsync(companyId, entryDate, source, description, reference, lineList,
                    postedBy, reversalOfId: null, allowInactiveAccounts: false, token),
                ct);
        }

        private async Task<JournalEntry> PostCoreAsync(
            Guid companyId, DateTime entryDate, JournalSource source, string? description, string? reference,
            IReadOnlyCollection<GlLineRequest> lines, Guid? postedBy, Guid? reversalOfId, bool allowInactiveAccounts,
            CancellationToken ct)
        {
            var validated = JournalValidator.ValidateLines(lines, allowZeroLines: true);

            // Lock order is always: period -> entry number. Keeping one order avoids deadlocks.
            var period = await GetPeriodUnderLockAsync(companyId, entryDate, ct);
            EnsurePeriodOpen(period);

            var accounts = await LoadPostableAccountsAsync(companyId, validated.Select(v => v.Source.GlAccountId), allowInactiveAccounts, ct);

            var entryNo = await NextEntryNoAsync(companyId, entryDate, "JE", ct);

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
                ReversalOfId = reversalOfId,
                CreatedBy = postedBy,
                Lines = validated.Select(ToJournalLine).ToList()
            };

            _db.JournalEntries.Add(journal);
            await ApplyBalancesAsync(companyId, accounts, journal.Lines, ct);

            _audit.Record(companyId, postedBy, reversalOfId == null ? "Journal.Posted" : "Journal.ReversalPosted", "GL",
                nameof(JournalEntry), journal.Id, before: null,
                after: AuditPayload(journal.Status, journal.EntryNo, reversalOfId == null ? null : $"reverses {reversalOfId}"));

            await _db.SaveChangesAsync(ct);
            return journal;
        }

        // =============================================================================================
        // Reversal (finding 4: atomic; finding 6: keeps dimension tags)
        // =============================================================================================

        /// <summary>
        /// Reverses a posted journal entry with an equal-and-opposite entry (period-end corrections,
        /// cancellations). All-or-nothing: the reversal journal, the balance updates, the link to the
        /// original and the original's Reversed status commit together or not at all. A journal can be
        /// reversed only once.
        /// </summary>
        public Task<JournalEntry> ReverseAsync(Guid companyId, Guid journalEntryId, DateTime reversalDate, string reason,
            CancellationToken ct = default, Guid? userId = null)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("A reason is required to reverse a journal entry.");

            return _db.InTransactionAsync(async token =>
            {
                // Serialise concurrent attempts to reverse the same journal.
                await _db.AcquireTransactionLockAsync($"journal:{journalEntryId:N}", token);

                var original = await LoadJournalAsync(companyId, journalEntryId, includeLines: true, token);

                if (original.Status == JournalStatus.Reversed)
                    throw new InvalidOperationException($"Journal entry {original.EntryNo} has already been reversed.");
                if (original.Status != JournalStatus.Posted)
                    throw new InvalidOperationException("Only posted journal entries can be reversed.");

                // Belt and braces for data written before the unique index existed.
                var alreadyReversed = await _db.JournalEntries.IgnoreQueryFilters()
                    .AnyAsync(j => j.CompanyId == companyId && j.ReversalOfId == original.Id, token);
                if (alreadyReversed)
                    throw new InvalidOperationException($"Journal entry {original.EntryNo} already has a reversal.");

                if (reversalDate.Date < original.EntryDate.Date)
                    throw new InvalidOperationException("The reversal date cannot be earlier than the original entry date.");

                // Equal and opposite -- INCLUDING every dimension tag, so cost-centre, fund-centre and grant
                // reports are neutralised exactly as the original was recorded.
                var reversedLines = original.Lines.Select(l => new GlLineRequest(
                    l.GlAccountId, l.Credit, l.Debit, l.Currency, l.ExchangeRate,
                    l.CostCentreId, $"Reversal: {reason}", l.FundCentreId, l.GrantId)).ToList();

                var reversal = await PostCoreAsync(companyId, reversalDate, original.Source,
                    $"Reversal of {original.EntryNo}: {reason}", original.Reference, reversedLines,
                    userId, reversalOfId: original.Id, allowInactiveAccounts: true, token);

                original.Status = JournalStatus.Reversed;
                _audit.Record(companyId, userId, "Journal.Reversed", "GL", nameof(JournalEntry), original.Id,
                    before: new { status = JournalStatus.Posted.ToString() },
                    after: AuditPayload(JournalStatus.Reversed, original.EntryNo, $"reason: {reason}; reversal {reversal.EntryNo}"));
                await _db.SaveChangesAsync(token);

                return reversal;
            }, ct);
        }

        // =============================================================================================
        // Manual journal lifecycle: Draft -> Submitted -> Approved -> Posted
        // =============================================================================================

        /// <summary>Creates a Draft manual journal. Nothing touches the ledger or account balances yet.</summary>
        public Task<JournalEntry> CreateDraftAsync(Guid companyId, Guid userId, DateTime entryDate, string? description,
            string? reference, IEnumerable<GlLineRequest> lines, CancellationToken ct = default)
        {
            var lineList = lines.ToList();
            return _db.InTransactionAsync(async token =>
            {
                var validated = JournalValidator.ValidateLines(lineList, allowZeroLines: false);

                var period = await GetPeriodUnderLockAsync(companyId, entryDate, token);
                EnsurePeriodOpen(period);
                await LoadPostableAccountsAsync(companyId, validated.Select(v => v.Source.GlAccountId), allowInactiveAccounts: false, token);

                var draftNo = await NextEntryNoAsync(companyId, entryDate, "DR", token);

                var draft = new JournalEntry
                {
                    CompanyId = companyId,
                    EntryNo = draftNo,
                    Description = description,
                    EntryDate = entryDate,
                    PeriodId = period.Id,
                    Source = JournalSource.Manual,
                    Reference = reference,
                    Status = JournalStatus.Draft,
                    CreatedBy = userId,
                    Lines = validated.Select(ToJournalLine).ToList()
                };
                _db.JournalEntries.Add(draft);

                _audit.Record(companyId, userId, "Journal.DraftCreated", "GL", nameof(JournalEntry), draft.Id,
                    before: null, after: AuditPayload(draft.Status, draft.EntryNo, null));
                await _db.SaveChangesAsync(token);
                return draft;
            }, ct);
        }

        public Task<JournalEntry> SubmitAsync(Guid companyId, Guid userId, Guid journalId, CancellationToken ct = default) =>
            TransitionAsync(companyId, journalId, JournalStatus.Submitted, userId, "Journal.Submitted", note: null, guard: null, ct);

        public Task<JournalEntry> ApproveAsync(Guid companyId, Guid approverId, Guid journalId, string? comment, CancellationToken ct = default) =>
            TransitionAsync(companyId, journalId, JournalStatus.Approved, approverId, "Journal.Approved", comment,
                entry => JournalWorkflow.EnsureSeparationOfDuties(entry.CreatedBy, approverId, _controls.AllowSelfApproval), ct);

        public Task<JournalEntry> RejectAsync(Guid companyId, Guid approverId, Guid journalId, string reason, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("A reason is required to reject a journal entry.");
            return TransitionAsync(companyId, journalId, JournalStatus.Rejected, approverId, "Journal.Rejected", reason, guard: null, ct);
        }

        public Task<JournalEntry> CancelAsync(Guid companyId, Guid userId, Guid journalId, string? reason, CancellationToken ct = default) =>
            TransitionAsync(companyId, journalId, JournalStatus.Cancelled, userId, "Journal.Cancelled", reason, guard: null, ct);

        private Task<JournalEntry> TransitionAsync(Guid companyId, Guid journalId, JournalStatus target, Guid userId,
            string action, string? note, Action<JournalEntry>? guard, CancellationToken ct)
        {
            return _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"journal:{journalId:N}", token);
                var entry = await LoadJournalAsync(companyId, journalId, includeLines: false, token);

                if (entry.Status == target) return entry;                       // repeated request: idempotent no-op
                if (entry.Source != JournalSource.Manual)
                    throw new InvalidOperationException("Only manual journals follow the approval workflow.");

                JournalWorkflow.EnsureTransition(entry.Status, target);
                guard?.Invoke(entry);

                var before = entry.Status;
                entry.Status = target;
                entry.UpdatedBy = userId;

                _audit.Record(companyId, userId, action, "GL", nameof(JournalEntry), entry.Id,
                    before: new { status = before.ToString() }, after: AuditPayload(target, entry.EntryNo, note));
                await _db.SaveChangesAsync(token);
                return entry;
            }, ct);
        }

        /// <summary>
        /// Posts an Approved manual journal to the ledger. Totals are re-validated from the persisted lines
        /// (never from anything the browser sent), the period must still be open, the final JE number is
        /// allocated, and balances are updated -- all in one transaction. Posting twice is a no-op.
        /// </summary>
        public Task<JournalEntry> PostApprovedAsync(Guid companyId, Guid userId, Guid journalId, CancellationToken ct = default)
        {
            return _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"journal:{journalId:N}", token);
                var entry = await LoadJournalAsync(companyId, journalId, includeLines: true, token);

                if (entry.Status == JournalStatus.Posted) return entry;         // repeated request: idempotent no-op
                JournalWorkflow.EnsureTransition(entry.Status, JournalStatus.Posted);   // only Approved -> Posted

                var persisted = entry.Lines.Select(l => new GlLineRequest(l.GlAccountId, l.Debit, l.Credit, l.Currency,
                    l.ExchangeRate, l.CostCentreId, l.Narrative, l.FundCentreId, l.GrantId)).ToList();
                JournalValidator.ValidateLines(persisted, allowZeroLines: false);

                var period = await GetPeriodUnderLockAsync(companyId, entry.EntryDate, token);
                EnsurePeriodOpen(period);
                var accounts = await LoadPostableAccountsAsync(companyId, persisted.Select(p => p.GlAccountId), allowInactiveAccounts: false, token);

                var before = entry.Status;
                entry.EntryNo = await NextEntryNoAsync(companyId, entry.EntryDate, "JE", token);
                entry.PeriodId = period.Id;
                entry.Status = JournalStatus.Posted;
                entry.UpdatedBy = userId;

                await ApplyBalancesAsync(companyId, accounts, entry.Lines, token);

                _audit.Record(companyId, userId, "Journal.Posted", "GL", nameof(JournalEntry), entry.Id,
                    before: new { status = before.ToString() }, after: AuditPayload(JournalStatus.Posted, entry.EntryNo, null));
                await _db.SaveChangesAsync(token);
                return entry;
            }, ct);
        }

        /// <summary>Immutable audit history (who did what, when) for one journal, oldest first.</summary>
        public async Task<List<AuditLogEntry>> GetJournalHistoryAsync(Guid companyId, Guid journalId, CancellationToken ct = default)
        {
            return await _db.AuditLogs.IgnoreQueryFilters()
                .Where(a => a.CompanyId == companyId && a.EntityType == nameof(JournalEntry) && a.EntityId == journalId)
                .OrderBy(a => a.Timestamp)
                .ToListAsync(ct);
        }

        // =============================================================================================
        // Accounting periods
        // =============================================================================================

        /// <summary>
        /// Closes a period. Serialised against posting with the same lock, so no journal can slip into a period
        /// that is closing; refuses while journals are still Draft/Submitted/Approved in the period.
        /// Closing an already-closed period is a no-op.
        /// </summary>
        public Task<AccountingPeriod> ClosePeriodAsync(Guid companyId, Guid periodId, Guid userId, CancellationToken ct = default)
        {
            return _db.InTransactionAsync(async token =>
            {
                var period = await LockPeriodByIdAsync(companyId, periodId, token);
                if (period.Status != PeriodStatus.Open) return period;

                var pending = await _db.JournalEntries.IgnoreQueryFilters().CountAsync(j =>
                    j.CompanyId == companyId && j.PeriodId == period.Id &&
                    (j.Status == JournalStatus.Draft || j.Status == JournalStatus.Submitted || j.Status == JournalStatus.Approved), token);
                if (pending > 0)
                    throw new InvalidOperationException(
                        $"Period {period.Year}-{period.PeriodNo:D2} still has {pending} unposted journal(s). Post, reject or cancel them before closing.");

                period.Status = PeriodStatus.Closed;
                period.ClosedBy = userId;
                period.ClosedAt = DateTime.UtcNow;

                _audit.Record(companyId, userId, "Period.Closed", "GL", nameof(AccountingPeriod), period.Id,
                    before: new { status = PeriodStatus.Open.ToString() },
                    after: new { status = PeriodStatus.Closed.ToString(), period = $"{period.Year}-{period.PeriodNo:D2}", correlationId = CorrelationId() });
                await _db.SaveChangesAsync(token);
                return period;
            }, ct);
        }

        /// <summary>Controlled re-opening of a Closed (not Locked) period. A reason is mandatory and audited.</summary>
        public Task<AccountingPeriod> ReopenPeriodAsync(Guid companyId, Guid periodId, Guid userId, string reason, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("A reason is required to reopen a period.");

            return _db.InTransactionAsync(async token =>
            {
                var period = await LockPeriodByIdAsync(companyId, periodId, token);
                if (period.Status == PeriodStatus.Open) return period;
                if (period.Status == PeriodStatus.Locked)
                    throw new InvalidOperationException("A locked period cannot be reopened.");

                period.Status = PeriodStatus.Open;
                period.ClosedBy = null;
                period.ClosedAt = null;

                _audit.Record(companyId, userId, "Period.Reopened", "GL", nameof(AccountingPeriod), period.Id,
                    before: new { status = PeriodStatus.Closed.ToString() },
                    after: new { status = PeriodStatus.Open.ToString(), period = $"{period.Year}-{period.PeriodNo:D2}", reason, correlationId = CorrelationId() });
                await _db.SaveChangesAsync(token);
                return period;
            }, ct);
        }

        private async Task<AccountingPeriod> LockPeriodByIdAsync(Guid companyId, Guid periodId, CancellationToken ct)
        {
            var meta = await _db.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == periodId && p.CompanyId == companyId)
                .Select(p => new { p.Year, p.PeriodNo })
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Accounting period not found.");

            await _db.AcquireTransactionLockAsync(JournalNumbering.PeriodLockKey(companyId, meta.Year, meta.PeriodNo), ct);

            // Re-read after the lock is held so the status is current.
            return await _db.AccountingPeriods.IgnoreQueryFilters()
                .FirstAsync(p => p.Id == periodId && p.CompanyId == companyId, ct);
        }

        // =============================================================================================
        // Helpers
        // =============================================================================================

        private static JournalLine ToJournalLine(ValidatedLine v) => new()
        {
            GlAccountId = v.Source.GlAccountId,
            Debit = v.Debit,
            Credit = v.Credit,
            Currency = v.Currency,
            ExchangeRate = v.Source.ExchangeRate,
            BaseDebit = v.BaseDebit,
            BaseCredit = v.BaseCredit,
            CostCentreId = v.Source.CostCentreId,
            Narrative = v.Source.Narrative,
            FundCentreId = v.Source.FundCentreId,
            GrantId = v.Source.GrantId
        };

        private static void EnsurePeriodOpen(AccountingPeriod period)
        {
            if (period.Status != PeriodStatus.Open)
                throw new InvalidOperationException(
                    $"Accounting period {period.Year}-{period.PeriodNo:D2} is {period.Status} and cannot accept new postings.");
        }

        private async Task<JournalEntry> LoadJournalAsync(Guid companyId, Guid journalId, bool includeLines, CancellationToken ct)
        {
            var query = _db.JournalEntries.IgnoreQueryFilters().Where(j => j.Id == journalId && j.CompanyId == companyId);
            if (includeLines) query = query.Include(j => j.Lines);
            return await query.FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("Journal entry not found.");
        }

        /// <summary>
        /// Takes the period lock, then returns the period, creating it on first use. The period is read without
        /// tracking so its status is always current for the check that follows.
        /// </summary>
        private async Task<AccountingPeriod> GetPeriodUnderLockAsync(Guid companyId, DateTime date, CancellationToken ct)
        {
            await _db.AcquireTransactionLockAsync(JournalNumbering.PeriodLockKey(companyId, date.Year, date.Month), ct);

            var existing = await _db.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Year == date.Year && p.PeriodNo == date.Month, ct);
            if (existing != null) return existing;

            var start = new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var period = new AccountingPeriod
            {
                CompanyId = companyId,
                Year = date.Year,
                PeriodNo = date.Month,
                StartDate = start,
                EndDate = start.AddMonths(1).AddDays(-1),
                Status = PeriodStatus.Open
            };
            _db.AccountingPeriods.Add(period);
            await _db.SaveChangesAsync(ct);
            return period;
        }

        private async Task<Dictionary<Guid, GlAccount>> LoadPostableAccountsAsync(Guid companyId, IEnumerable<Guid> accountIds,
            bool allowInactiveAccounts, CancellationToken ct)
        {
            var ids = accountIds.Distinct().ToList();
            var accounts = await _db.GlAccounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => ids.Contains(a.Id) && a.CompanyId == companyId)
                .ToDictionaryAsync(a => a.Id, ct);

            foreach (var id in ids)
            {
                if (!accounts.TryGetValue(id, out var account))
                    throw new InvalidOperationException($"GL account {id} not found.");
                if (!allowInactiveAccounts && !account.IsActive)
                    throw new InvalidOperationException($"GL account {account.Code} - {account.Name} is inactive and cannot be posted to.");
            }
            return accounts;
        }

        /// <summary>
        /// Allocates the next journal number for the month. The number lock is held until the surrounding
        /// transaction ends, so two concurrent postings can never read the same "last number"; the unique
        /// (CompanyId, EntryNo) index remains as a final safeguard.
        /// </summary>
        private async Task<string> NextEntryNoAsync(Guid companyId, DateTime date, string kind, CancellationToken ct)
        {
            var prefix = JournalNumbering.Prefix(kind, date);
            await _db.AcquireTransactionLockAsync(JournalNumbering.NumberLockKey(companyId, prefix), ct);

            // Highest existing number for the prefix (shorter strings sort first, so order by length then value).
            var last = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => j.CompanyId == companyId && j.EntryNo.StartsWith(prefix))
                .OrderByDescending(j => j.EntryNo.Length)
                .ThenByDescending(j => j.EntryNo)
                .Select(j => j.EntryNo)
                .FirstOrDefaultAsync(ct);

            return JournalNumbering.Format(prefix, JournalNumbering.NextSequence(last, prefix));
        }

        /// <summary>
        /// Updates running account balances with atomic SQL increments (not read-modify-write), in a fixed
        /// account order so concurrent postings that touch the same accounts cannot deadlock.
        /// (Asset/Expense: debit increases; Liability/Equity/Income: credit increases.)
        /// </summary>
        private async Task ApplyBalancesAsync(Guid companyId, IDictionary<Guid, GlAccount> accounts,
            IEnumerable<JournalLine> lines, CancellationToken ct)
        {
            var deltas = new SortedDictionary<Guid, decimal>();
            foreach (var line in lines)
            {
                var account = accounts[line.GlAccountId];
                var net = IsDebitNormal(account.Type)
                    ? line.BaseDebit - line.BaseCredit
                    : line.BaseCredit - line.BaseDebit;
                deltas[line.GlAccountId] = (deltas.TryGetValue(line.GlAccountId, out var running) ? running : 0m) + net;
            }

            foreach (var item in deltas)
            {
                var accountId = item.Key;
                var delta = item.Value;
                if (delta == 0m) continue;

                await _db.GlAccounts.IgnoreQueryFilters()
                    .Where(a => a.Id == accountId && a.CompanyId == companyId)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.CurrentBalance, a => a.CurrentBalance + delta), ct);
            }
        }

        private static bool IsDebitNormal(GlAccountType type) =>
            type is GlAccountType.Asset or GlAccountType.Expense;

        private static string? CorrelationId() => Activity.Current?.TraceId.ToString();

        private static object AuditPayload(JournalStatus status, string entryNo, string? note) =>
            new { status = status.ToString(), entryNo, note, correlationId = CorrelationId() };
    }
}
