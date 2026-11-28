using System.Security.Cryptography;
using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>Period-close checklist (spec 24.2 D). Automatic tasks are evaluated from live data; manual tasks need a named person.</summary>
    public class PeriodCloseService
    {
        private readonly ApexErpDbContext _db;
        private readonly SettingsService _settings;
        private readonly AuditService _audit;

        public PeriodCloseService(ApexErpDbContext db, SettingsService settings, AuditService audit) { _db = db; _settings = settings; _audit = audit; }

        private static readonly (string Code, string Description, bool Auto)[] Standard =
        {
            ("NO_DRAFT_JOURNALS", "No draft or submitted journals remain in the period", true),
            ("SUBLEDGERS_RECONCILED", "AP, AR, inventory, fixed assets, payroll and bank reconciled to the GL (no unexplained differences)", true),
            ("BANK_RECS_CERTIFIED", "Every bank account has a certified reconciliation dated on or after period end", true),
            ("SCHEDULES_POSTED", "Accrual, prepayment and revenue schedules due in the period are posted", true),
            ("NO_CRITICAL_EXCEPTIONS", "No open critical control exceptions", true),
            ("TRIAL_BALANCE_BALANCES", "Trial balance debits equal credits", true),
            ("DEPRECIATION_REVIEWED", "Depreciation for the period has been run and reviewed", false),
            ("MANAGER_CERTIFICATION", "Finance manager certifies the period is complete and accurate", false),
        };

        public async Task<List<PeriodCloseTask>> EnsureChecklistAsync(Guid companyId, Guid periodId, CancellationToken ct = default)
        {
            if (!await _db.AccountingPeriods.AnyAsync(p => p.Id == periodId && p.CompanyId == companyId, ct)) throw new InvalidOperationException("Period not found.");
            var existing = await _db.PeriodCloseTasks.Where(t => t.CompanyId == companyId && t.PeriodId == periodId).ToListAsync(ct);
            if (existing.Count == 0)
            {
                var i = 0;
                foreach (var (code, desc, auto) in Standard)
                    _db.PeriodCloseTasks.Add(new PeriodCloseTask { CompanyId = companyId, PeriodId = periodId, Code = code, Description = desc, IsAutomatic = auto, SortOrder = ++i });
                await _db.SaveChangesAsync(ct);
            }
            return await EvaluateAsync(companyId, periodId, ct);
        }

        public async Task<List<PeriodCloseTask>> EvaluateAsync(Guid companyId, Guid periodId, CancellationToken ct = default)
        {
            var period = await _db.AccountingPeriods.FirstAsync(p => p.Id == periodId && p.CompanyId == companyId, ct);
            var tasks = await _db.PeriodCloseTasks.Where(t => t.CompanyId == companyId && t.PeriodId == periodId).OrderBy(t => t.SortOrder).ToListAsync(ct);
            foreach (var t in tasks.Where(t => t.IsAutomatic && t.Status != "Waived"))
            {
                var (ok, note) = await CheckAsync(companyId, period, t.Code, ct);
                t.Status = ok ? "Done" : "Blocked"; t.Note = note;
                if (ok) { t.CompletedAt ??= DateTime.UtcNow; } else { t.CompletedAt = null; }
            }
            await _db.SaveChangesAsync(ct);
            return tasks;
        }

        private async Task<(bool, string)> CheckAsync(Guid companyId, AccountingPeriod p, string code, CancellationToken ct)
        {
            switch (code)
            {
                case "NO_DRAFT_JOURNALS":
                {
                    var n = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId && j.PeriodId == p.Id && (j.Status == JournalStatus.Draft || j.Status == JournalStatus.Submitted || j.Status == JournalStatus.Approved), ct);
                    return (n == 0, n == 0 ? "None outstanding." : $"{n} journal(s) are not yet posted or cancelled.");
                }
                case "SUBLEDGERS_RECONCILED":
                {
                    var bad = new List<string>();
                    foreach (var area in ReconciliationService.Areas)
                    {
                        var latest = await _db.ReconciliationRuns.Where(r => r.CompanyId == companyId && r.Area == area && r.AsOfDate >= p.EndDate.Date).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
                        if (latest == null) bad.Add($"{area}: not run");
                        else if (latest.Status == "Unreconciled") bad.Add($"{area}: difference {latest.Difference:N2}");
                    }
                    return (bad.Count == 0, bad.Count == 0 ? "All areas reconciled." : string.Join("; ", bad));
                }
                case "BANK_RECS_CERTIFIED":
                {
                    var banks = await _db.BankAccounts.Where(b => b.CompanyId == companyId).Select(b => b.Id).ToListAsync(ct);
                    var missing = 0;
                    foreach (var b in banks)
                        if (!await _db.BankReconciliations.AnyAsync(r => r.CompanyId == companyId && r.BankAccountId == b && r.Status == "Certified" && r.StatementDate >= p.EndDate.Date, ct)) missing++;
                    return (missing == 0, missing == 0 ? "All bank accounts certified." : $"{missing} bank account(s) lack a certified reconciliation.");
                }
                case "SCHEDULES_POSTED":
                {
                    var acc = await _db.AccrualSchedules.CountAsync(a => a.CompanyId == companyId && a.Status == "Approved" && a.AccrualDate <= p.EndDate, ct);
                    var pre = await _db.PrepaymentSchedules.Where(s => s.CompanyId == companyId && s.Status == "Active").SelectMany(s => s.Lines).CountAsync(l => l.JournalEntryId == null && l.PeriodStart <= p.EndDate, ct);
                    var rev = await _db.RevenueContracts.Where(c => c.CompanyId == companyId && c.Status == "Active").SelectMany(c => c.Obligations).SelectMany(o => o.Schedule).CountAsync(l => l.JournalEntryId == null && l.PeriodStart <= p.EndDate, ct);
                    var total = acc + pre + rev;
                    return (total == 0, total == 0 ? "All due schedules posted." : $"{acc} accrual(s), {pre} prepayment line(s), {rev} revenue line(s) are due but not posted.");
                }
                case "NO_CRITICAL_EXCEPTIONS":
                {
                    var n = await _db.ControlExceptions.CountAsync(e => e.CompanyId == companyId && e.Severity == "Critical" && e.Status != "CLOSED", ct);
                    return (n == 0, n == 0 ? "None open." : $"{n} critical exception(s) are open.");
                }
                case "TRIAL_BALANCE_BALANCES":
                {
                    var t = await _db.JournalEntries.Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed) && j.EntryDate <= p.EndDate)
                        .SelectMany(j => j.Lines).GroupBy(_ => 1).Select(g => new { D = g.Sum(l => l.BaseDebit), C = g.Sum(l => l.BaseCredit) }).FirstOrDefaultAsync(ct);
                    var diff = t == null ? 0m : t.D - t.C;
                    return (diff == 0m, diff == 0m ? "In balance." : $"Out of balance by {diff:N2}.");
                }
                default: return (false, "Unknown automatic task.");
            }
        }

        public async Task<PeriodCloseTask> CompleteManualAsync(Guid companyId, Guid taskId, Guid userId, string note, bool canCertify = false, CancellationToken ct = default)
        {
            var t = await _db.PeriodCloseTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Task not found.");
            if (t.IsAutomatic) throw new InvalidOperationException("Automatic tasks are completed by the system when their condition is met.");
            if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("A note is required.");
            if (t.Code == "MANAGER_CERTIFICATION")
            {
                if (!canCertify) throw new InvalidOperationException("Only a person with period-certification authority can certify the period.");
                var others = await _db.PeriodCloseTasks.Where(x => x.CompanyId == companyId && x.PeriodId == t.PeriodId && x.Id != t.Id && x.Status != "Done" && x.Status != "Waived").CountAsync(ct);
                if (others > 0) throw new InvalidOperationException("Certification is the last step: other tasks are still open.");
            }
            t.Status = "Done"; t.CompletedBy = userId; t.CompletedAt = DateTime.UtcNow; t.Note = note;
            _audit.Record(companyId, userId, "PeriodClose.TaskCompleted", "GL", nameof(PeriodCloseTask), t.Id, null, new { t.Code, note });
            await _db.SaveChangesAsync(ct); return t;
        }

        public async Task<PeriodCloseTask> WaiveAsync(Guid companyId, Guid taskId, Guid userId, string reason, CancellationToken ct = default)
        {
            var t = await _db.PeriodCloseTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Task not found.");
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 15) throw new InvalidOperationException("A waiver needs a documented reason (at least 15 characters).");
            if (t.Code is "TRIAL_BALANCE_BALANCES" or "NO_DRAFT_JOURNALS") throw new InvalidOperationException("This task protects ledger integrity and cannot be waived.");
            t.Status = "Waived"; t.CompletedBy = userId; t.CompletedAt = DateTime.UtcNow; t.Note = "Waived: " + reason.Trim();
            _audit.Record(companyId, userId, "PeriodClose.TaskWaived", "GL", nameof(PeriodCloseTask), t.Id, null, new { t.Code, reason });
            await _db.SaveChangesAsync(ct); return t;
        }

        /// <summary>Throws if a checklist exists (or is required) and not every task is Done or Waived. Called before a period is closed.</summary>
        public async Task AssertReadyAsync(Guid companyId, Guid periodId, CancellationToken ct = default)
        {
            var required = await _settings.GetBoolAsync(companyId, "period.close.requireChecklist", false, ct);
            var hasTasks = await _db.PeriodCloseTasks.AnyAsync(t => t.CompanyId == companyId && t.PeriodId == periodId, ct);
            if (!hasTasks && !required) return;
            var tasks = hasTasks ? await EvaluateAsync(companyId, periodId, ct) : await EnsureChecklistAsync(companyId, periodId, ct);
            var open = tasks.Where(t => t.Status is not ("Done" or "Waived")).ToList();
            if (open.Count > 0) throw new InvalidOperationException("The period cannot be closed yet: " + string.Join("; ", open.Select(t => $"{t.Description} ({t.Note ?? t.Status})")));
        }

        public Task<List<PeriodCloseTask>> GetAsync(Guid companyId, Guid periodId, CancellationToken ct = default) =>
            _db.PeriodCloseTasks.Where(t => t.CompanyId == companyId && t.PeriodId == periodId).OrderBy(t => t.SortOrder).ToListAsync(ct);
    }

    /// <summary>Supporting documents with tamper detection, plus statement-to-source drill-down (spec 12.3, 13.4).</summary>
    public class EvidenceService
    {
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;
        public const int MaxBytes = 5 * 1024 * 1024;
        private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf", "image/png", "image/jpeg", "text/csv", "text/plain",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
        private static readonly HashSet<string> AllowedEntities = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(JournalEntry), nameof(ApInvoice), nameof(ArInvoice), nameof(ApPayment), nameof(ApPaymentRun), nameof(ControlException), nameof(ReconciliationRun),
            nameof(BankReconciliation), nameof(ImpairmentAssessment), nameof(InventoryNrvAssessment), nameof(AccrualSchedule), nameof(PrepaymentSchedule), nameof(RevenueContract),
            nameof(ArCreditNote), nameof(SupplierBankChange), nameof(PeriodCloseTask), nameof(PettyCashVoucher)
        };

        public EvidenceService(ApexErpDbContext db, AuditService audit) { _db = db; _audit = audit; }

        private void AssertCompanyContext(Guid companyId)
        {
            if (companyId == Guid.Empty || _db.CurrentCompanyId != companyId)
                throw new UnauthorizedAccessException("Evidence operations must use the authenticated company context.");
        }

        private async Task<bool> TargetExistsInCompanyAsync(Guid companyId, string entityType, Guid entityId, CancellationToken ct)
        {
            // The allow-list is also an explicit map: never accept an arbitrary entity name or
            // create evidence links to a record owned by a different company.
            return entityType.ToLowerInvariant() switch
            {
                "journalentry" => await _db.JournalEntries.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "apinvoice" => await _db.ApInvoices.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "arinvoice" => await _db.ArInvoices.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "appayment" => await _db.ApPayments.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "appaymentrun" => await _db.ApPaymentRuns.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "controlexception" => await _db.ControlExceptions.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "reconciliationrun" => await _db.ReconciliationRuns.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "bankreconciliation" => await _db.BankReconciliations.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "impairmentassessment" => await _db.ImpairmentAssessments.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "inventorynrvassessment" => await _db.InventoryNrvAssessments.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "accrualschedule" => await _db.AccrualSchedules.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "prepaymentschedule" => await _db.PrepaymentSchedules.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "revenuecontract" => await _db.RevenueContracts.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "arcreditnote" => await _db.ArCreditNotes.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "supplierbankchange" => await _db.SupplierBankChanges.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "periodclosetask" => await _db.PeriodCloseTasks.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                "pettycashvoucher" => await _db.PettyCashVouchers.AnyAsync(x => x.Id == entityId && x.CompanyId == companyId, ct),
                _ => false
            };
        }

        public async Task<AuditEvidenceReference> AttachAsync(Guid companyId, string entityType, Guid entityId, string fileName, string contentType, byte[] content, string? description, Guid userId, CancellationToken ct = default)
        {
            AssertCompanyContext(companyId);
            if (!AllowedEntities.Contains(entityType)) throw new InvalidOperationException("Evidence cannot be attached to that record type.");
            if (!await TargetExistsInCompanyAsync(companyId, entityType, entityId, ct))
                throw new InvalidOperationException("The target record does not exist in the authenticated company.");
            if (content.Length == 0 || content.Length > MaxBytes) throw new InvalidOperationException($"Files must be between 1 byte and {MaxBytes / 1024 / 1024} MB.");
            if (!AllowedTypes.Contains(contentType)) throw new InvalidOperationException("Allowed types: PDF, PNG, JPEG, CSV, text, Excel (.xlsx) and Word (.docx).");
            var safeName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("A file name is required.");
            var e = new AuditEvidenceReference
            {
                CompanyId = companyId, EntityType = entityType, EntityId = entityId, FileName = safeName, ContentType = contentType, SizeBytes = content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(content)), Content = content, Description = description, UploadedBy = userId, CreatedBy = userId
            };
            _db.AuditEvidenceReferences.Add(e);
            _audit.Record(companyId, userId, "Evidence.Attached", "Audit", entityType, entityId, null, new { safeName, e.Sha256, e.SizeBytes });
            await _db.SaveChangesAsync(ct); return e;
        }

        public Task<List<object>> ListAsync(Guid companyId, string entityType, Guid entityId, CancellationToken ct = default)
        {
            AssertCompanyContext(companyId);
            return _db.AuditEvidenceReferences.Where(e => e.CompanyId == companyId && e.EntityType == entityType && e.EntityId == entityId).OrderBy(e => e.CreatedAt)
                .Select(e => (object)new { e.Id, e.FileName, e.ContentType, e.SizeBytes, e.Sha256, e.Description, e.UploadedBy, e.CreatedAt }).ToListAsync(ct);
        }

        /// <summary>Returns the file and whether its content still matches the hash recorded at upload.</summary>
        public async Task<(AuditEvidenceReference File, bool IntegrityOk)> DownloadAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default)
        {
            AssertCompanyContext(companyId);
            var e = await _db.AuditEvidenceReferences.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Evidence not found.");
            var ok = string.Equals(Convert.ToHexString(SHA256.HashData(e.Content)), e.Sha256, StringComparison.OrdinalIgnoreCase);
            _audit.Record(companyId, userId, "Evidence.Downloaded", "Audit", e.EntityType, e.EntityId, null, new { e.FileName, ok });
            await _db.SaveChangesAsync(ct);
            return (e, ok);
        }

        // ---------------------------------------------------------------------------------------------
        // Drill-down (13.4): statement line -> account -> journal lines -> journal -> source document + audit trail + evidence
        // ---------------------------------------------------------------------------------------------
        public async Task<object> AccountActivityAsync(Guid companyId, Guid accountId, DateTime from, DateTime to, int page, int pageSize, CancellationToken ct = default)
        {
            pageSize = Math.Clamp(pageSize, 10, 200); page = Math.Max(1, page);
            var account = await _db.GlAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Account not found.");
            var q = _db.JournalEntries.Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed) && j.EntryDate >= from && j.EntryDate <= to)
                .SelectMany(j => j.Lines.Where(l => l.GlAccountId == accountId).Select(l => new { j.Id, j.EntryNo, j.EntryDate, j.Source, j.Reference, j.Description, l.BaseDebit, l.BaseCredit }));
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(x => x.EntryDate).ThenBy(x => x.EntryNo).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var opening = await _db.JournalEntries.Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed) && j.EntryDate < from)
                .SelectMany(j => j.Lines).Where(l => l.GlAccountId == accountId).GroupBy(_ => 1).Select(g => g.Sum(l => l.BaseDebit) - g.Sum(l => l.BaseCredit)).FirstOrDefaultAsync(ct);
            return new { Account = new { account.Id, account.Code, account.Name }, From = from, To = to, OpeningBalance = opening, Total = total, Page = page, PageSize = pageSize, Items = items };
        }

        public async Task<object> TraceJournalAsync(Guid companyId, Guid journalId, CancellationToken ct = default)
        {
            var j = await _db.JournalEntries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == journalId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Journal not found.");
            var lineAccountIds = j.Lines.Select(l => l.GlAccountId).Distinct().ToList();
            var accounts = await _db.GlAccounts.Where(a => a.CompanyId == companyId && lineAccountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code + " " + a.Name, ct);

            object? source = null;
            if (!string.IsNullOrWhiteSpace(j.Reference))
            {
                source = j.Source switch
                {
                    JournalSource.AR => await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.InvoiceNo == j.Reference).Select(i => (object)new { Type = "ArInvoice", i.Id, i.InvoiceNo, i.Total }).FirstOrDefaultAsync(ct)
                                        ?? await _db.ArReceipts.Where(r => r.CompanyId == companyId && r.ReceiptNo == j.Reference).Select(r => (object)new { Type = "ArReceipt", r.Id, r.ReceiptNo, r.Amount }).FirstOrDefaultAsync(ct),
                    JournalSource.AP => await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.InvoiceNo == j.Reference).Select(i => (object)new { Type = "ApInvoice", i.Id, i.InvoiceNo, i.Total }).FirstOrDefaultAsync(ct),
                    _ => null
                };
            }
            var audit = await _db.AuditLogs.Where(a => a.CompanyId == companyId && a.EntityId == journalId).OrderBy(a => a.Timestamp)
                .Select(a => new { a.Timestamp, a.Action, a.UserId, a.IpAddress }).ToListAsync(ct);
            var evidence = await ListAsync(companyId, nameof(JournalEntry), journalId, ct);
            return new
            {
                j.Id, j.EntryNo, j.EntryDate, Source = j.Source.ToString(), j.Reference, j.Description, Status = j.Status.ToString(), j.ReversalOfId,
                Lines = j.Lines.Select(l => new { l.GlAccountId, Account = accounts.GetValueOrDefault(l.GlAccountId), l.BaseDebit, l.BaseCredit }),
                SourceDocument = source, AuditTrail = audit, Evidence = evidence
            };
        }
    }

    /// <summary>Executive dashboard (spec 14): financial position, control health, close status, compliance and operations in one call.</summary>
    public class DashboardService
    {
        private readonly ApexErpDbContext _db;
        public DashboardService(ApexErpDbContext db) { _db = db; }

        private static object Ageing(IEnumerable<(DateTime Due, decimal Amount)> items, DateTime asOf)
        {
            decimal current = 0, d30 = 0, d60 = 0, d90 = 0, over = 0;
            foreach (var (due, amt) in items)
            {
                var days = (asOf.Date - due.Date).Days;
                if (days <= 0) current += amt; else if (days <= 30) d30 += amt; else if (days <= 60) d60 += amt; else if (days <= 90) d90 += amt; else over += amt;
            }
            return new { Current = current, Days1to30 = d30, Days31to60 = d60, Days61to90 = d90, Over90 = over, Total = current + d30 + d60 + d90 + over };
        }

        public async Task<object> GetAsync(Guid companyId, DateTime asOf, CancellationToken ct = default)
        {
            asOf = asOf.Date;
            var since90 = asOf.AddDays(-90);

            // A. Financial position
            var cash = await _db.BankAccounts.Where(b => b.CompanyId == companyId).SumAsync(b => (decimal?)b.CurrentBalance, ct) ?? 0m;
            var ar = (await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).ToListAsync(ct)).Select(i => (i.DueDate, i.Outstanding * i.ExchangeRate)).ToList();
            var ap = (await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).ToListAsync(ct)).Select(i => (i.DueDate, i.Outstanding * i.ExchangeRate)).ToList();
            var sales90 = await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.InvoiceDate >= since90 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).SumAsync(i => (decimal?)(i.Total * i.ExchangeRate), ct) ?? 0m;
            var purch90 = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.InvoiceDate >= since90 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).SumAsync(i => (decimal?)(i.Total * i.ExchangeRate), ct) ?? 0m;
            var arTotal = ar.Sum(x => x.Item2); var apTotal = ap.Sum(x => x.Item2);
            var financial = new
            {
                Cash = cash, Receivables = arTotal, Payables = apTotal, NetWorkingCapital = cash + arTotal - apTotal,
                DaysSalesOutstanding = sales90 <= 0 ? (decimal?)null : Math.Round(arTotal / sales90 * 90m, 1),
                DaysPayablesOutstanding = purch90 <= 0 ? (decimal?)null : Math.Round(apTotal / purch90 * 90m, 1),
                ReceivablesAgeing = Ageing(ar, asOf), PayablesAgeing = Ageing(ap, asOf)
            };

            // B. Control health
            var exc = await _db.ControlExceptions.Where(e => e.CompanyId == companyId && e.Status != "CLOSED").ToListAsync(ct);
            var latestTests = (await _db.ControlTestResults.Where(t => t.CompanyId == companyId).OrderByDescending(t => t.RunAt).Take(100).ToListAsync(ct)).GroupBy(t => t.TestCode).Select(g => g.First()).ToList();
            var latestRecs = (await _db.ReconciliationRuns.Where(r => r.CompanyId == companyId).OrderByDescending(r => r.CreatedAt).Take(60).ToListAsync(ct)).GroupBy(r => r.Area).Select(g => g.First()).ToList();
            var controls = new
            {
                OpenExceptions = exc.Count, Critical = exc.Count(e => e.Severity == "Critical"), High = exc.Count(e => e.Severity == "High"),
                Overdue = exc.Count(e => e.DueDate != null && e.DueDate < DateTime.UtcNow),
                UnassignedExceptions = exc.Count(e => e.OwnerUserId == null),
                SelfTests = latestTests.Select(t => new { t.TestCode, t.Title, t.Passed, t.RunAt }),
                SelfTestsFailing = latestTests.Count(t => !t.Passed),
                Reconciliations = latestRecs.Select(r => new { r.Area, r.Status, r.Difference, r.AsOfDate }),
                UnreconciledAreas = latestRecs.Count(r => r.Status == "Unreconciled"),
                PendingJournalApprovals = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId && j.Status == JournalStatus.Submitted, ct),
                PaymentRunsAwaitingAuthorisation = await _db.ApPaymentRuns.CountAsync(r => r.CompanyId == companyId && r.Status == "Draft", ct),
                SupplierBankChangesPending = await _db.SupplierBankChanges.CountAsync(c => c.CompanyId == companyId && c.Status == "Pending", ct),
                EmergencyAccessUnreviewed = await _db.ApprovalDelegations.CountAsync(d => d.CompanyId == companyId && d.IsEmergency && d.ReviewedAt == null, ct)
            };

            // C. Period close
            var period = await _db.AccountingPeriods.Where(p => p.CompanyId == companyId && p.Status == PeriodStatus.Open).OrderBy(p => p.StartDate).FirstOrDefaultAsync(ct);
            object? close = null;
            if (period != null)
            {
                var tasks = await _db.PeriodCloseTasks.Where(t => t.CompanyId == companyId && t.PeriodId == period.Id).ToListAsync(ct);
                close = new { Period = $"{period.Year}-{period.PeriodNo:D2}", period.EndDate, Tasks = tasks.Count, Done = tasks.Count(t => t.Status is "Done" or "Waived"), Blocked = tasks.Count(t => t.Status == "Blocked") };
            }

            // D. Tax and compliance (current calendar month)
            var monthStart = new DateTime(asOf.Year, asOf.Month, 1);
            var compliance = new
            {
                VatOutputThisMonth = await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.InvoiceDate >= monthStart && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).SumAsync(i => (decimal?)(i.VatAmount * i.ExchangeRate), ct) ?? 0m,
                VatInputThisMonth = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.InvoiceDate >= monthStart && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).SumAsync(i => (decimal?)(i.VatAmount * i.ExchangeRate), ct) ?? 0m,
                WithholdingThisMonth = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.InvoiceDate >= monthStart && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).SumAsync(i => (decimal?)(i.WhtAmount * i.ExchangeRate), ct) ?? 0m
            };

            // E. Operations and integrations
            var weekAgo = DateTime.UtcNow.AddDays(-7);
            var operations = new
            {
                IntegrationPending = await _db.IntegrationMessages.CountAsync(m => m.CompanyId == companyId && m.Status == "Pending", ct),
                IntegrationProcessing = await _db.IntegrationMessages.CountAsync(m => m.CompanyId == companyId && m.Status == "Processing", ct),
                IntegrationDeadLetter = await _db.IntegrationMessages.CountAsync(m => m.CompanyId == companyId && m.Status == "DeadLetter", ct),
                BackgroundJobFailures7d = await _db.JobExecutions.CountAsync(j => j.CompanyId == companyId && j.Status == "Failed" && j.StartedAt >= weekAgo, ct),
                LastJobSuccess = await _db.JobExecutions.Where(j => j.CompanyId == companyId && j.Status == "Succeeded").MaxAsync(j => (DateTime?)j.FinishedAt, ct)
            };

            return new { AsOf = asOf, Financial = financial, Controls = controls, PeriodClose = close, Compliance = compliance, Operations = operations };
        }
    }

    /// <summary>Statutory payroll rates are configuration with effective dates and a recorded source (spec 25.2).</summary>
    public class StatutoryRateService
    {
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;
        public StatutoryRateService(ApexErpDbContext db, AuditService audit) { _db = db; _audit = audit; }
        private static readonly HashSet<string> Codes = new() { "NSSA_EMPLOYEE", "NSSA_EMPLOYER", "AIDS_LEVY", "ZIMDEF", "NEC_LEVY" };

        public async Task<StatutoryRate> ProposeAsync(Guid companyId, string code, decimal ratePct, decimal? ceiling, string currency, DateTime effectiveFrom, string sourceReference, Guid userId, CancellationToken ct = default)
        {
            if (!Codes.Contains(code)) throw new InvalidOperationException("Code must be one of: " + string.Join(", ", Codes));
            if (ratePct < 0 || ratePct > 100) throw new InvalidOperationException("The rate must be between 0 and 100.");
            if (ceiling is < 0) throw new InvalidOperationException("The ceiling cannot be negative.");
            if (string.IsNullOrWhiteSpace(sourceReference)) throw new InvalidOperationException("Record the notice or instrument this rate comes from.");
            var r = new StatutoryRate { CompanyId = companyId, Code = code, RatePct = ratePct, Ceiling = ceiling, Currency = currency, EffectiveFrom = effectiveFrom.Date, SourceReference = sourceReference, CreatedBy = userId };
            _db.StatutoryRates.Add(r);
            _audit.Record(companyId, userId, "StatutoryRate.Proposed", "Payroll", nameof(StatutoryRate), r.Id, null, new { code, ratePct, ceiling, effectiveFrom });
            await _db.SaveChangesAsync(ct); return r;
        }

        public async Task<StatutoryRate> ApproveAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var r = await _db.StatutoryRates.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Rate not found.");
            if (r.CreatedBy == approver) throw new InvalidOperationException("A statutory rate must be approved by someone other than its proposer.");
            // close the previous open-ended rate for this code the day before this one starts
            foreach (var prev in await _db.StatutoryRates.Where(x => x.CompanyId == companyId && x.Code == r.Code && x.ApprovedBy != null && x.EffectiveTo == null && x.EffectiveFrom < r.EffectiveFrom).ToListAsync(ct))
                prev.EffectiveTo = r.EffectiveFrom.AddDays(-1);
            r.ApprovedBy = approver;
            _audit.Record(companyId, approver, "StatutoryRate.Approved", "Payroll", nameof(StatutoryRate), r.Id, null, new { r.Code, r.RatePct });
            await _db.SaveChangesAsync(ct); return r;
        }

        public Task<List<StatutoryRate>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            _db.StatutoryRates.Where(r => r.CompanyId == companyId).OrderBy(r => r.Code).ThenByDescending(r => r.EffectiveFrom).ToListAsync(ct);
    }
}
