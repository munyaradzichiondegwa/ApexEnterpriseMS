using System.Security.Cryptography;
using System.Text;
using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public sealed record DetectionSummary(int Raised, int AlreadyOpen, IReadOnlyDictionary<string, int> ByDetector);
    public sealed record ControlTestOutcome(string Code, string Title, bool Passed, string Details);

    /// <summary>
    /// Continuous control monitoring (spec 15), the exception life-cycle (spec 9.3) and the automated control self-tests (spec 28.4).
    /// A detector raises an exception for investigation; it never decides that fraud occurred.
    /// </summary>
    public class ControlMonitoringService
    {
        private readonly ApexErpDbContext _db;
        private readonly GovernanceService _gov;
        private readonly SettingsService _settings;
        private readonly AuditService _audit;

        public ControlMonitoringService(ApexErpDbContext db, GovernanceService gov, SettingsService settings, AuditService audit)
        {
            _db = db; _gov = gov; _settings = settings; _audit = audit;
        }

        // ---------------------------------------------------------------------------------------------
        // Detection
        // ---------------------------------------------------------------------------------------------
        public async Task<DetectionSummary> RunDetectorsAsync(Guid companyId, int lookbackDays = 35, CancellationToken ct = default)
        {
            var since = DateTime.UtcNow.AddDays(-lookbackDays);
            var thresholds = await _gov.GetActiveMaterialityAsync(companyId, ct);
            var found = new List<ControlException>();

            // 1. Risky manual journals (value, timing, round sums, suspense, closed-period attempts)
            var suspenseIds = (await _db.GlAccounts.Where(a => a.CompanyId == companyId && (
                    (a.SubType != null && a.SubType.ToLower().Contains("suspense")) || a.Name.ToLower().Contains("suspense")))
                .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            var manual = await _db.JournalEntries.Include(j => j.Lines)
                .Where(j => j.CompanyId == companyId && j.Source == JournalSource.Manual && j.CreatedAt >= since && j.Status != JournalStatus.Cancelled)
                .ToListAsync(ct);
            foreach (var j in manual)
            {
                var amount = j.Lines.Sum(l => l.BaseDebit);
                var localHour = (j.CreatedAt.ToUniversalTime().Hour + 2) % 24;       // Africa/Harare is UTC+2, no DST
                var input = new JournalRiskInput(amount, true, localHour < 6 || localHour >= 20, amount > 0 && amount % 1000m == 0m,
                    j.Lines.Any(l => suspenseIds.Contains(l.GlAccountId)), false, false);
                var score = ExceptionRisk.Score(input, thresholds);
                if (score >= 25)
                    found.Add(Make("MANUAL_JOURNAL_RISK", "Manual journal", nameof(JournalEntry), j.Id, score,
                        $"Manual journal {j.EntryNo} for {amount:N2} scored {score} (out-of-hours: {input.PostedOutsideBusinessHours}, round: {input.IsRoundAmount}, suspense: {input.TouchesSuspenseAccount})."));
            }

            // 2. Duplicate supplier invoices
            var invoices = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.Status != InvoiceStatus.Cancelled && i.CreatedAt >= since.AddDays(-60)).ToListAsync(ct);
            foreach (var g in invoices.Where(i => !string.IsNullOrWhiteSpace(i.SupplierInvoiceNo)).GroupBy(i => (i.SupplierId, N: ValuationRules.NormaliseInvoiceNo(i.SupplierInvoiceNo))).Where(g => g.Count() > 1))
            {
                var first = g.OrderBy(i => i.CreatedAt).First();
                found.Add(Make("DUPLICATE_SUPPLIER_INVOICE", "Accounts payable", nameof(ApInvoice), first.Id, 55,
                    $"{g.Count()} invoices share supplier invoice number {first.SupplierInvoiceNo}."));
            }

            // 2b. Same supplier, amount and date under different invoice numbers
            foreach (var g in invoices.GroupBy(i => (i.SupplierId, i.Total, Day: i.InvoiceDate.Date)).Where(g => g.Count() > 1))
                found.Add(Make("DUPLICATE_AMOUNT_DATE", "Accounts payable", nameof(ApInvoice), g.OrderBy(i => i.CreatedAt).First().Id, 45,
                    $"{g.Count()} invoices from the same supplier for {g.Key.Total:N2} are dated {g.Key.Day:yyyy-MM-dd}."));

            // 3. Duplicate payments (same supplier, amount and date)
            var payments = await _db.ApPayments.Where(p => p.CompanyId == companyId && p.PaymentDate >= since).ToListAsync(ct);
            foreach (var g in payments.GroupBy(p => (p.SupplierId, p.Amount, p.PaymentDate.Date)).Where(g => g.Count() > 1))
                found.Add(Make("DUPLICATE_PAYMENT", "Accounts payable", nameof(ApPayment), g.First().Id, 65,
                    $"{g.Count()} payments of {g.Key.Amount:N2} to the same supplier on {g.Key.Date:yyyy-MM-dd}."));

            // 4. Payment shortly after a bank-detail change
            var recentChanges = await _db.SupplierBankChanges.Where(c => c.CompanyId == companyId && c.CreatedAt >= since).ToListAsync(ct);
            foreach (var c in recentChanges)
            {
                var pay = payments.FirstOrDefault(p => p.SupplierId == c.SupplierId && p.CreatedAt >= c.CreatedAt && p.CreatedAt <= c.CreatedAt.AddDays(7));
                if (pay != null)
                    found.Add(Make("PAYMENT_AFTER_BANK_CHANGE", "Accounts payable", nameof(ApPayment), pay.Id, 70,
                        "A payment was made within 7 days of a change to the supplier's bank details."));
            }

            // 5. Stale unreconciled bank items
            var stale = await _db.BankTransactions.Where(b => b.CompanyId == companyId && !b.IsReconciled && b.TransactionDate < DateTime.UtcNow.AddDays(-30)).ToListAsync(ct);
            foreach (var g in stale.GroupBy(b => b.BankAccountId))
                found.Add(Make("STALE_BANK_ITEMS", "Bank", nameof(BankAccount), g.Key, 30,
                    $"{g.Count()} bank transactions older than 30 days are unreconciled."));

            // 6. Journals waiting for approval too long; periods left open after their end date
            var waiting = await _db.JournalEntries.Where(j => j.CompanyId == companyId && j.Status == JournalStatus.Submitted && j.CreatedAt < DateTime.UtcNow.AddDays(-3)).ToListAsync(ct);
            foreach (var j in waiting)
                found.Add(Make("APPROVAL_OVERDUE", "Workflow", nameof(JournalEntry), j.Id, 30, $"Journal {j.EntryNo} has awaited approval for over 3 days."));
            var overdueCloses = await _db.AccountingPeriods.Where(p => p.CompanyId == companyId && p.Status == PeriodStatus.Open && p.EndDate < DateTime.UtcNow.AddDays(-10)).ToListAsync(ct);
            foreach (var p in overdueCloses)
                found.Add(Make("PERIOD_NOT_CLOSED", "Period close", nameof(AccountingPeriod), p.Id, 30, $"Period {p.Year}-{p.PeriodNo:D2} ended more than 10 days ago and is still open."));

            // 7. Customers over their credit limit
            var customers = await _db.Customers.Where(c => c.CompanyId == companyId && c.CreditLimit > 0).ToListAsync(ct);
            var open = await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled)
                .GroupBy(i => i.CustomerId).Select(g => new { g.Key, Total = g.Sum(i => i.Outstanding) }).ToListAsync(ct);
            foreach (var c in customers)
            {
                var bal = open.FirstOrDefault(o => o.Key == c.Id)?.Total ?? 0m;
                if (bal > c.CreditLimit)
                    found.Add(Make("CREDIT_LIMIT_EXCEEDED", "Accounts receivable", nameof(Customer), c.Id, 35, $"{c.Name} owes {bal:N2} against a limit of {c.CreditLimit:N2}."));
            }

            // 8. Negative stock
            foreach (var i in await _db.InventoryItems.Where(i => i.CompanyId == companyId && i.QuantityOnHand < 0).ToListAsync(ct))
                found.Add(Make("NEGATIVE_STOCK", "Inventory", nameof(InventoryItem), i.Id, 35, $"{i.Name} has negative stock ({i.QuantityOnHand})."));

            // 9. Unexplained reconciliation differences
            foreach (var r in await _db.ReconciliationRuns.Where(r => r.CompanyId == companyId && r.Status == "Unreconciled").ToListAsync(ct))
                found.Add(Make("RECONCILIATION_DIFFERENCE", "Reconciliation", nameof(ReconciliationRun), r.Id, 45, $"{r.Area} sub-ledger differs from the GL by {r.Difference:N2} as at {r.AsOfDate:yyyy-MM-dd}."));

            // Persist only findings that are not already tracked (unique fingerprint)
            var fingerprints = found.Select(f => f.Fingerprint).Distinct().ToList();
            var existing = (await _db.ControlExceptions.Where(e => e.CompanyId == companyId && fingerprints.Contains(e.Fingerprint)).Select(e => e.Fingerprint).ToListAsync(ct)).ToHashSet();
            var byDetector = new Dictionary<string, int>(); var raised = 0;
            foreach (var f in found.GroupBy(x => x.Fingerprint).Select(g => g.First()))
            {
                if (existing.Contains(f.Fingerprint)) continue;
                f.CompanyId = companyId; f.Severity = Severity(f.RiskScore); f.Status = "CLASSIFIED";
                f.DueDate = DateTime.UtcNow.AddDays(f.Severity is "Critical" ? 2 : f.Severity is "High" ? 5 : 14);
                _db.ControlExceptions.Add(f); raised++;
                byDetector[f.DetectorCode] = byDetector.GetValueOrDefault(f.DetectorCode) + 1;
            }
            if (raised > 0) await _db.SaveChangesAsync(ct);
            return new DetectionSummary(raised, existing.Count, byDetector);
        }

        private static string Severity(int score) => ExceptionRisk.Classify(score).ToString();

        private static ControlException Make(string detector, string category, string sourceType, Guid sourceId, int score, string description) => new()
        {
            DetectorCode = detector, Category = category, SourceType = sourceType, SourceId = sourceId, RiskScore = score, Description = description,
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{detector}|{sourceType}|{sourceId}"))).Substring(0, 40)
        };

        // ---------------------------------------------------------------------------------------------
        // Life-cycle (9.3): DETECTED -> CLASSIFIED -> ASSIGNED -> INVESTIGATED -> RESOLVED -> INDEPENDENTLY_REVIEWED -> CLOSED
        // ---------------------------------------------------------------------------------------------
        public Task<List<ControlException>> ListAsync(Guid companyId, string? status, string? severity, CancellationToken ct = default)
        {
            var q = _db.ControlExceptions.Where(e => e.CompanyId == companyId);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
            if (!string.IsNullOrWhiteSpace(severity)) q = q.Where(e => e.Severity == severity);
            return q.OrderByDescending(e => e.RiskScore).ThenBy(e => e.DueDate).Take(500).ToListAsync(ct);
        }

        public async Task<ControlException> AssignAsync(Guid companyId, Guid id, Guid ownerUserId, DateTime? due, Guid actor, CancellationToken ct = default)
        {
            var e = await Load(companyId, id, ct);
            if (!await _db.Users.AnyAsync(u => u.Id == ownerUserId && u.CompanyId == companyId && u.IsActive, ct)) throw new InvalidOperationException("Owner not found.");
            Advance(e, "ASSIGNED", actor, allowStay: true);
            e.OwnerUserId = ownerUserId; if (due != null) e.DueDate = due;
            await _db.SaveChangesAsync(ct); return e;
        }

        public async Task<ControlException> InvestigateAsync(Guid companyId, Guid id, string notes, Guid actor, CancellationToken ct = default)
        {
            var e = await Load(companyId, id, ct);
            if (string.IsNullOrWhiteSpace(notes)) throw new InvalidOperationException("Investigation notes are required.");
            if (e.OwnerUserId == null) throw new InvalidOperationException("Assign an owner before investigating.");
            if (e.Status == "CLASSIFIED") Advance(e, "ASSIGNED", actor);
            Advance(e, "INVESTIGATED", actor, allowStay: true);
            e.InvestigationNotes = notes;
            await _db.SaveChangesAsync(ct); return e;
        }

        public async Task<ControlException> ResolveAsync(Guid companyId, Guid id, string resolution, Guid actor, CancellationToken ct = default)
        {
            var e = await Load(companyId, id, ct);
            if (string.IsNullOrWhiteSpace(resolution)) throw new InvalidOperationException("A resolution note is required.");
            if (e.Severity is "High" or "Critical")
            {
                var hasEvidence = await _db.AuditEvidenceReferences.AnyAsync(x => x.CompanyId == companyId && x.EntityType == nameof(ControlException) && x.EntityId == id, ct);
                if (!hasEvidence) throw new InvalidOperationException("High and critical exceptions need supporting evidence attached before they can be resolved.");
            }
            Advance(e, "RESOLVED", actor);
            e.ResolutionNote = resolution; e.ResolvedBy = actor;
            await _db.SaveChangesAsync(ct); return e;
        }

        public async Task<ControlException> ReviewAsync(Guid companyId, Guid id, Guid reviewer, CancellationToken ct = default)
        {
            var e = await Load(companyId, id, ct);
            if (e.ResolvedBy == reviewer) throw new InvalidOperationException("The reviewer must be someone other than the person who resolved it.");
            Advance(e, "INDEPENDENTLY_REVIEWED", reviewer);
            e.ReviewedBy = reviewer;
            await _db.SaveChangesAsync(ct); return e;
        }

        public async Task<ControlException> CloseAsync(Guid companyId, Guid id, Guid actor, CancellationToken ct = default)
        {
            var e = await Load(companyId, id, ct);
            Advance(e, "CLOSED", actor);
            e.ClosedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct); return e;
        }

        private async Task<ControlException> Load(Guid companyId, Guid id, CancellationToken ct) =>
            await _db.ControlExceptions.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Exception not found.");

        private void Advance(ControlException e, string to, Guid actor, bool allowStay = false)
        {
            if (allowStay && e.Status == to) return;
            // "ASSIGNED" may be (re)entered from DETECTED/CLASSIFIED; otherwise only the next state is allowed.
            var ok = ExceptionRisk.CanAdvance(e.Status, to) || (to == "ASSIGNED" && e.Status is "DETECTED" or "CLASSIFIED");
            if (!ok) throw new InvalidOperationException($"An exception cannot move from {e.Status} to {to}.");
            var before = e.Status; e.Status = to; e.UpdatedBy = actor; e.UpdatedAt = DateTime.UtcNow;
            _audit.Record(e.CompanyId, actor, "Exception." + to, "Controls", nameof(ControlException), e.Id, new { status = before }, new { status = to });
        }

        // ---------------------------------------------------------------------------------------------
        // Control self-tests (28.4 tests A-H) against live data, read-only
        // ---------------------------------------------------------------------------------------------
        public async Task<List<ControlTestOutcome>> RunSelfTestsAsync(Guid companyId, CancellationToken ct = default)
        {
            var results = new List<ControlTestOutcome>();
            var posted = _db.JournalEntries.Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed));

            // A. Every posted journal balances
            var unbalanced = await posted.Where(j => j.Lines.Sum(l => l.BaseDebit) != j.Lines.Sum(l => l.BaseCredit)).CountAsync(ct);
            results.Add(new("A", "Posted journals balance (debits = credits)", unbalanced == 0, unbalanced == 0 ? "All posted journals balance." : $"{unbalanced} posted journal(s) are out of balance."));

            // B. No duplicate postings from the same source document
            var dupRefs = (await posted.Where(j => j.Reference != null && j.Source != JournalSource.Manual && j.ReversalOfId == null)
                .GroupBy(j => new { j.Source, j.Reference }).Select(g => new { g.Key.Reference, N = g.Count() }).Where(x => x.N > 1).ToListAsync(ct)).Count;
            results.Add(new("B", "No duplicate postings for one source document", dupRefs == 0, dupRefs == 0 ? "No duplicate references found." : $"{dupRefs} reference(s) were posted more than once."));

            // C. Nothing posted into a period after it was closed
            var closed = await _db.AccountingPeriods.Where(p => p.CompanyId == companyId && p.ClosedAt != null).ToListAsync(ct);
            var violations = 0;
            foreach (var p in closed)
                violations += await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId && j.PeriodId == p.Id && j.CreatedAt > p.ClosedAt && j.Status != JournalStatus.Cancelled && j.Status != JournalStatus.Draft, ct);
            results.Add(new("C", "No postings after a period was closed", violations == 0, violations == 0 ? "No late postings found." : $"{violations} journal(s) were created after their period closed (reopened periods may explain this)."));

            // D. Tenant isolation: journal lines only use this company's accounts
            var accountIds = (await _db.GlAccounts.Where(a => a.CompanyId == companyId).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            var usedIds = await _db.JournalEntries.Where(j => j.CompanyId == companyId).SelectMany(j => j.Lines).Select(l => l.GlAccountId).Distinct().ToListAsync(ct);
            var foreign = usedIds.Count(id => !accountIds.Contains(id));
            results.Add(new("D", "Journal lines use only this company's accounts", foreign == 0, foreign == 0 ? "No cross-company account use." : $"{foreign} account(s) used belong to another company."));

            // E. Nobody approved their own journal (unless the company explicitly allows it)
            var allowSelf = await _settings.GetBoolAsync(companyId, SettingsService.AllowSelfApproval, false, ct);
            var selfApproved = 0;
            if (!allowSelf)
            {
                var approvals = await _db.AuditLogs.Where(a => a.CompanyId == companyId && a.Action == "Journal.Approved").ToListAsync(ct);
                var ids = approvals.Select(a => a.EntityId).Distinct().ToList();
                var created = await _db.JournalEntries.Where(j => ids.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.CreatedBy, ct);
                selfApproved = approvals.Count(a => a.UserId != null && created.TryGetValue(a.EntityId, out var by) && by == a.UserId);
            }
            results.Add(new("E", "No journal approved by its preparer", selfApproved == 0, selfApproved == 0 ? "No self-approvals." : $"{selfApproved} journal(s) were approved by their preparer."));

            // F. Atomicity: no posted journal without lines
            var empty = await posted.Where(j => !j.Lines.Any()).CountAsync(ct);
            results.Add(new("F", "No posted journal is missing its lines", empty == 0, empty == 0 ? "All posted journals have lines." : $"{empty} posted journal(s) have no lines."));

            // G. Reversals: each reversed journal has exactly one reversing entry
            var reversed = await _db.JournalEntries.Where(j => j.CompanyId == companyId && j.Status == JournalStatus.Reversed).Select(j => j.Id).ToListAsync(ct);
            var reversalCounts = await _db.JournalEntries.Where(j => j.CompanyId == companyId && j.ReversalOfId != null).GroupBy(j => j.ReversalOfId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            var badRev = reversed.Count(id => reversalCounts.FirstOrDefault(r => r.Key == id)?.N != 1) + reversalCounts.Count(r => r.N > 1 && !reversed.Contains(r.Key));
            results.Add(new("G", "Every reversal is recorded exactly once", badRev == 0, badRev == 0 ? "Reversals are consistent." : $"{badRev} reversal inconsistency(ies)."));

            // H. The ledger as a whole balances
            var totals = await _db.JournalEntries.Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed))
                .SelectMany(j => j.Lines).GroupBy(_ => 1).Select(g => new { D = g.Sum(l => l.BaseDebit), C = g.Sum(l => l.BaseCredit) }).FirstOrDefaultAsync(ct);
            var diff = totals == null ? 0m : totals.D - totals.C;
            results.Add(new("H", "Trial balance debits equal credits", diff == 0m, diff == 0m ? "Trial balance is in balance." : $"Trial balance differs by {diff:N2}."));

            foreach (var r in results)
                _db.ControlTestResults.Add(new ControlTestResult { CompanyId = companyId, TestCode = r.Code, Title = r.Title, Passed = r.Passed, Details = r.Details });
            await _db.SaveChangesAsync(ct);
            return results;
        }
    }
}
