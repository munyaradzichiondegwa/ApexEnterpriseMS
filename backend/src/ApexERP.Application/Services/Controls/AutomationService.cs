using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public sealed record RunSummary(int Posted, int Skipped, IReadOnlyList<string> Problems);

    /// <summary>Shared helpers: account validation and approval checks used by every schedule/assessment service.</summary>
    public abstract class PostingServiceBase
    {
        protected readonly ApexErpDbContext Db;
        protected readonly GlPostingService Gl;
        protected readonly GovernanceService Gov;
        protected readonly AuditService Audit;

        protected PostingServiceBase(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit)
        { Db = db; Gl = gl; Gov = gov; Audit = audit; }

        protected async Task EnsureAccountsAsync(Guid companyId, CancellationToken ct, params Guid?[] ids)
        {
            var wanted = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
            var found = await Db.GlAccounts.Where(a => a.CompanyId == companyId && wanted.Contains(a.Id) && a.IsActive).CountAsync(ct);
            if (found != wanted.Count) throw new InvalidOperationException("One or more accounts do not exist, are inactive, or belong to another company.");
        }

        protected async Task RequireApprovalAsync(Guid companyId, Guid approver, string entityType, decimal amount, Guid? preparedBy, CancellationToken ct)
        {
            var d = await Gov.CheckApprovalAuthorityAsync(companyId, approver, entityType, amount, preparedBy, ct);
            if (!d.Allowed) throw new InvalidOperationException(d.Reason ?? "You are not authorised to approve this.");
        }

        protected static DateTime MonthEnd(DateTime d) => new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddDays(-1);
    }

    // =================================================================================================
    // Accruals (spec 4)
    // =================================================================================================
    public class AccrualService : PostingServiceBase
    {
        public AccrualService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit) : base(db, gl, gov, audit) { }

        public async Task<AccrualSchedule> CreateAsync(Guid companyId, string description, decimal amount, string currency, DateTime accrualDate,
            Guid expenseAccountId, Guid liabilityAccountId, Guid? supplierId, bool autoReverse, Guid? costCentreId, Guid userId, CancellationToken ct = default)
        {
            if (amount <= 0) throw new InvalidOperationException("The accrual amount must be positive.");
            if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("A description is required.");
            await EnsureAccountsAsync(companyId, ct, expenseAccountId, liabilityAccountId);
            var a = new AccrualSchedule
            {
                CompanyId = companyId, Description = description, Amount = amount, Currency = currency, AccrualDate = accrualDate.Date,
                ExpenseAccountId = expenseAccountId, AccruedLiabilityAccountId = liabilityAccountId, SupplierId = supplierId,
                AutoReverse = autoReverse, CostCentreId = costCentreId, CreatedBy = userId
            };
            Db.AccrualSchedules.Add(a);
            Audit.Record(companyId, userId, "Accrual.Created", "Accruals", nameof(AccrualSchedule), a.Id, null, new { amount, accrualDate });
            await Db.SaveChangesAsync(ct);
            return a;
        }

        public async Task<AccrualSchedule> ApproveAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var a = await Load(companyId, id, ct);
            if (a.Status != "Draft") throw new InvalidOperationException("Only draft accruals can be approved.");
            await RequireApprovalAsync(companyId, approver, "Accrual", a.Amount, a.CreatedBy, ct);
            a.Status = "Approved"; a.ApprovedBy = approver;
            Audit.Record(companyId, approver, "Accrual.Approved", "Accruals", nameof(AccrualSchedule), a.Id, null, new { a.Amount });
            await Db.SaveChangesAsync(ct); return a;
        }

        public Task<AccrualSchedule> PostAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var a = await Load(companyId, id, token);
                if (a.Status != "Approved") throw new InvalidOperationException("Only approved accruals can be posted.");
                var je = await Gl.PostAsync(companyId, a.AccrualDate, JournalSource.Accrual, $"Accrual: {a.Description}", $"ACR-{a.Id.ToString("N")[..8]}",
                    new[]
                    {
                        new GlLineRequest(a.ExpenseAccountId, a.Amount, 0, a.Currency, 1m, a.CostCentreId, a.Description),
                        new GlLineRequest(a.AccruedLiabilityAccountId, 0, a.Amount, a.Currency, 1m, null, "Accrued liability")
                    }, token, userId);
                a.JournalEntryId = je.Id; a.Status = "Posted";
                Audit.Record(companyId, userId, "Accrual.Posted", "Accruals", nameof(AccrualSchedule), a.Id, null, new { je.EntryNo });
                await Db.SaveChangesAsync(token); return a;
            }, ct);

        /// <summary>Auto-reverses posted accruals on the first day after their period (spec 4.2). Safe to re-run: reversed accruals are skipped.</summary>
        public async Task<RunSummary> ReverseDueAsync(Guid companyId, DateTime asOf, Guid? userId, CancellationToken ct = default)
        {
            var due = await Db.AccrualSchedules.Where(a => a.CompanyId == companyId && a.Status == "Posted" && a.AutoReverse && a.JournalEntryId != null).ToListAsync(ct);
            int done = 0, skipped = 0; var problems = new List<string>();
            foreach (var a in due)
            {
                var reversalDate = new DateTime(a.AccrualDate.Year, a.AccrualDate.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                if (reversalDate > asOf) { skipped++; continue; }
                try { await ReverseOneAsync(companyId, a.Id, reversalDate, "Auto-reversal of accrual", userId, ct); done++; }
                catch (InvalidOperationException ex) { problems.Add($"{a.Description}: {ex.Message}"); }
            }
            return new RunSummary(done, skipped, problems);
        }

        private Task ReverseOneAsync(Guid companyId, Guid id, DateTime date, string reason, Guid? userId, CancellationToken ct) =>
            Db.InTransactionAsync<bool>(async token =>
            {
                var a = await Load(companyId, id, token);
                if (a.Status != "Posted" || a.JournalEntryId == null) return false;
                var rev = await Gl.ReverseAsync(companyId, a.JournalEntryId.Value, date, reason, token, userId);
                a.ReversalJournalEntryId = rev.Id; a.Status = "Reversed";
                await Db.SaveChangesAsync(token); return true;
            }, ct);

        /// <summary>Matches an accrual to the supplier invoice that settles it and reverses the accrual so the cost is not counted twice.</summary>
        public Task<AccrualSchedule> MatchToInvoiceAsync(Guid companyId, Guid accrualId, Guid apInvoiceId, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var a = await Load(companyId, accrualId, token);
                var inv = await Db.ApInvoices.FirstOrDefaultAsync(i => i.Id == apInvoiceId && i.CompanyId == companyId, token) ?? throw new InvalidOperationException("Invoice not found.");
                if (a.SupplierId != null && a.SupplierId != inv.SupplierId) throw new InvalidOperationException("The invoice is from a different supplier.");
                if (a.Status == "Posted") await ReverseOneAsync(companyId, a.Id, inv.InvoiceDate, "Matched to supplier invoice " + inv.InvoiceNo, userId, token);
                a = await Load(companyId, accrualId, token);
                a.MatchedApInvoiceId = apInvoiceId; if (a.Status is "Posted" or "Reversed") a.Status = "Matched";
                Audit.Record(companyId, userId, "Accrual.Matched", "Accruals", nameof(AccrualSchedule), a.Id, null, new { apInvoiceId });
                await Db.SaveChangesAsync(token); return a;
            }, ct);

        public async Task<List<object>> AgeingAsync(Guid companyId, DateTime asOf, CancellationToken ct = default) =>
            (await Db.AccrualSchedules.Where(a => a.CompanyId == companyId && a.Status == "Posted" && a.MatchedApInvoiceId == null).ToListAsync(ct))
                .Select(a => (object)new { a.Id, a.Description, a.Amount, a.AccrualDate, DaysOld = (asOf.Date - a.AccrualDate).Days, Bucket = (asOf.Date - a.AccrualDate).Days switch { <= 30 => "0-30", <= 60 => "31-60", <= 90 => "61-90", _ => "90+" } }).ToList();

        public Task<List<AccrualSchedule>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            Db.AccrualSchedules.Where(a => a.CompanyId == companyId).OrderByDescending(a => a.AccrualDate).Take(500).ToListAsync(ct);

        private async Task<AccrualSchedule> Load(Guid companyId, Guid id, CancellationToken ct) =>
            await Db.AccrualSchedules.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Accrual not found.");
    }

    // =================================================================================================
    // Prepayments (spec 4.4)
    // =================================================================================================
    public class PrepaymentService : PostingServiceBase
    {
        public PrepaymentService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit) : base(db, gl, gov, audit) { }

        public async Task<PrepaymentSchedule> CreateAsync(Guid companyId, string description, decimal total, string currency, DateTime start, int months,
            Guid prepaidAssetAccountId, Guid expenseAccountId, Guid? supplierId, Guid? costCentreId, Guid userId, CancellationToken ct = default)
        {
            if (months < 1 || months > 120) throw new InvalidOperationException("The period must be between 1 and 120 months.");
            if (total <= 0) throw new InvalidOperationException("The amount must be positive.");
            await EnsureAccountsAsync(companyId, ct, prepaidAssetAccountId, expenseAccountId);
            var p = new PrepaymentSchedule
            {
                CompanyId = companyId, Description = description, TotalAmount = total, Currency = currency, StartDate = start.Date, Months = months,
                PrepaidAssetAccountId = prepaidAssetAccountId, ExpenseAccountId = expenseAccountId, SupplierId = supplierId, CostCentreId = costCentreId, CreatedBy = userId
            };
            foreach (var l in RecognitionSchedule.StraightLine(total, start, months))
                p.Lines.Add(new PrepaymentScheduleLine { Sequence = l.Sequence, PeriodStart = l.PeriodStart, Amount = l.Amount });
            Db.PrepaymentSchedules.Add(p);
            Audit.Record(companyId, userId, "Prepayment.Created", "Prepayments", nameof(PrepaymentSchedule), p.Id, null, new { total, months });
            await Db.SaveChangesAsync(ct);
            return p;
        }

        public async Task<PrepaymentSchedule> ApproveAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var p = await Load(companyId, id, ct);
            if (p.Status != "Draft") throw new InvalidOperationException("Only draft schedules can be approved.");
            await RequireApprovalAsync(companyId, approver, "Prepayment", p.TotalAmount, p.CreatedBy, ct);
            p.Status = "Active"; p.ApprovedBy = approver;
            Audit.Record(companyId, approver, "Prepayment.Approved", "Prepayments", nameof(PrepaymentSchedule), p.Id, null, new { p.TotalAmount });
            await Db.SaveChangesAsync(ct); return p;
        }

        /// <summary>Posts every due, unposted amortisation line (Dr expense / Cr prepaid asset). Idempotent: a line with a journal is never posted again.</summary>
        public async Task<RunSummary> RunDueAsync(Guid companyId, DateTime asOf, Guid? userId, CancellationToken ct = default)
        {
            var ids = await Db.PrepaymentSchedules.Where(p => p.CompanyId == companyId && p.Status == "Active").Select(p => p.Id).ToListAsync(ct);
            int posted = 0, skipped = 0; var problems = new List<string>();
            foreach (var id in ids)
            {
                var lineIds = await Db.PrepaymentScheduleLines.Where(l => l.PrepaymentScheduleId == id && l.JournalEntryId == null && l.PeriodStart <= asOf)
                    .OrderBy(l => l.Sequence).Select(l => l.Id).ToListAsync(ct);
                foreach (var lineId in lineIds)
                {
                    try { await PostLineAsync(companyId, id, lineId, userId, ct); posted++; }
                    catch (InvalidOperationException ex) { skipped++; problems.Add($"{id.ToString("N")[..8]} line: {ex.Message}"); break; }  // keep order: stop at the first blocked month
                }
            }
            return new RunSummary(posted, skipped, problems);
        }

        private Task PostLineAsync(Guid companyId, Guid scheduleId, Guid lineId, Guid? userId, CancellationToken ct) =>
            Db.InTransactionAsync<bool>(async token =>
            {
                var p = await Db.PrepaymentSchedules.Include(x => x.Lines).FirstAsync(x => x.Id == scheduleId && x.CompanyId == companyId, token);
                var line = p.Lines.First(l => l.Id == lineId);
                if (line.JournalEntryId != null) return false;
                var je = await Gl.PostAsync(companyId, MonthEnd(line.PeriodStart), JournalSource.Prepayment, $"Prepayment amortisation: {p.Description} ({line.Sequence}/{p.Months})",
                    $"PPD-{p.Id.ToString("N")[..8]}-{line.Sequence}",
                    new[]
                    {
                        new GlLineRequest(p.ExpenseAccountId, line.Amount, 0, p.Currency, 1m, p.CostCentreId, p.Description),
                        new GlLineRequest(p.PrepaidAssetAccountId, 0, line.Amount, p.Currency, 1m, null, "Prepayment released")
                    }, token, userId);
                line.JournalEntryId = je.Id; line.PostedAt = DateTime.UtcNow;
                if (p.Lines.All(l => l.JournalEntryId != null)) p.Status = "Completed";
                await Db.SaveChangesAsync(token); return true;
            }, ct);

        public async Task<object> BalanceAsync(Guid companyId, Guid id, CancellationToken ct = default)
        {
            var p = await Load(companyId, id, ct);
            var recognised = p.Lines.Where(l => l.JournalEntryId != null).Sum(l => l.Amount);
            return new { p.Id, p.Description, p.TotalAmount, Recognised = recognised, Remaining = p.TotalAmount - recognised, p.Status };
        }

        public Task<List<PrepaymentSchedule>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            Db.PrepaymentSchedules.Include(p => p.Lines).Where(p => p.CompanyId == companyId).OrderByDescending(p => p.StartDate).Take(300).ToListAsync(ct);

        private async Task<PrepaymentSchedule> Load(Guid companyId, Guid id, CancellationToken ct) =>
            await Db.PrepaymentSchedules.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Schedule not found.");
    }

    // =================================================================================================
    // IFRS 15 revenue contracts (spec 5)
    // =================================================================================================
    public record ObligationRequest(string Description, decimal StandaloneSellingPrice, string Pattern, DateTime StartDate, DateTime? EndDate, Guid RevenueAccountId);

    public class RevenueService : PostingServiceBase
    {
        public RevenueService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit) : base(db, gl, gov, audit) { }

        private static int MonthsInclusive(DateTime a, DateTime b) => (b.Year - a.Year) * 12 + b.Month - a.Month + 1;

        public async Task<RevenueContract> CreateContractAsync(Guid companyId, string contractNo, Guid customerId, DateTime start, DateTime? end, decimal transactionPrice,
            string currency, Guid receivableAccountId, Guid contractLiabilityAccountId, Guid? contractAssetAccountId, IReadOnlyList<ObligationRequest> obligations,
            Guid userId, Guid? modifiedFrom = null, string? modificationNote = null, CancellationToken ct = default)
        {
            if (transactionPrice <= 0) throw new InvalidOperationException("The transaction price must be positive.");
            if (string.IsNullOrWhiteSpace(contractNo)) throw new InvalidOperationException("A contract number is required.");
            if (!await Db.Customers.AnyAsync(c => c.Id == customerId && c.CompanyId == companyId, ct)) throw new InvalidOperationException("Customer not found.");
            await EnsureAccountsAsync(companyId, ct, new Guid?[] { receivableAccountId, contractLiabilityAccountId, contractAssetAccountId }.Concat(obligations.Select(o => (Guid?)o.RevenueAccountId)).ToArray());

            var keyed = obligations.Select((o, i) => (Key: i.ToString(), O: o)).ToList();
            var alloc = ValuationRules.AllocateTransactionPrice(transactionPrice, keyed.Select(k => new ObligationPrice(k.Key, k.O.StandaloneSellingPrice)).ToList());

            var c = new RevenueContract
            {
                CompanyId = companyId, ContractNo = contractNo.Trim(), CustomerId = customerId, StartDate = start.Date, EndDate = end?.Date, TransactionPrice = transactionPrice,
                Currency = currency, ReceivableAccountId = receivableAccountId, ContractLiabilityAccountId = contractLiabilityAccountId, ContractAssetAccountId = contractAssetAccountId,
                ModifiedFromContractId = modifiedFrom, ModificationNote = modificationNote, CreatedBy = userId
            };
            foreach (var (key, o) in keyed)
            {
                var ob = new PerformanceObligation
                {
                    Description = o.Description, StandaloneSellingPrice = o.StandaloneSellingPrice, AllocatedPrice = alloc[key], Pattern = o.Pattern,
                    StartDate = o.StartDate.Date, EndDate = o.EndDate?.Date, RevenueAccountId = o.RevenueAccountId
                };
                if (o.Pattern == "Ratable")
                {
                    if (o.EndDate == null || o.EndDate < o.StartDate) throw new InvalidOperationException($"'{o.Description}' is recognised over time and needs an end date after its start.");
                    foreach (var l in RecognitionSchedule.StraightLine(ob.AllocatedPrice, o.StartDate, MonthsInclusive(o.StartDate, o.EndDate.Value)))
                        ob.Schedule.Add(new RevenueScheduleLine { Sequence = l.Sequence, PeriodStart = l.PeriodStart, Amount = l.Amount });
                }
                else if (o.Pattern != "PointInTime") throw new InvalidOperationException("Pattern must be Ratable or PointInTime.");
                c.Obligations.Add(ob);
            }
            Db.RevenueContracts.Add(c);
            Audit.Record(companyId, userId, "RevenueContract.Created", "Revenue", nameof(RevenueContract), c.Id, null, new { contractNo, transactionPrice });
            await Db.SaveChangesAsync(ct);
            return c;
        }

        public async Task<RevenueContract> ApproveAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var c = await Load(companyId, id, ct);
            if (c.Status != "Draft") throw new InvalidOperationException("Only draft contracts can be approved.");
            await RequireApprovalAsync(companyId, approver, "RevenueContract", c.TransactionPrice, c.CreatedBy, ct);
            c.Status = "Active"; c.ApprovedBy = approver;
            Audit.Record(companyId, approver, "RevenueContract.Approved", "Revenue", nameof(RevenueContract), c.Id, null, new { c.ContractNo });
            await Db.SaveChangesAsync(ct); return c;
        }

        /// <summary>Bill the customer (typically in advance): Dr receivable, Cr contract liability. The liability is released as obligations are satisfied.</summary>
        public Task<RevenueContract> BillAsync(Guid companyId, Guid id, decimal amount, DateTime date, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var c = await Load(companyId, id, token);
                if (c.Status != "Active") throw new InvalidOperationException("Only active contracts can be billed.");
                if (amount <= 0 || c.BilledToDate + amount > c.TransactionPrice) throw new InvalidOperationException("The billing amount must be positive and cannot exceed the unbilled contract price.");
                var je = await Gl.PostAsync(companyId, date, JournalSource.Revenue, $"Contract billing {c.ContractNo}", $"{c.ContractNo}-BILL-{c.BilledToDate + amount:0.##}",
                    new[]
                    {
                        new GlLineRequest(c.ReceivableAccountId, amount, 0, c.Currency, 1m, null, "Contract billing"),
                        new GlLineRequest(c.ContractLiabilityAccountId, 0, amount, c.Currency, 1m, null, "Contract liability")
                    }, token, userId);
                c.BilledToDate += amount; c.BillingJournalEntryId = je.Id;
                Audit.Record(companyId, userId, "RevenueContract.Billed", "Revenue", nameof(RevenueContract), c.Id, null, new { amount });
                await Db.SaveChangesAsync(token); return c;
            }, ct);

        /// <summary>Recognises revenue for every due ratable line. Revenue is taken from the contract liability first; any excess over billings is a contract asset (spec 5.5).</summary>
        public async Task<RunSummary> RecogniseDueAsync(Guid companyId, DateTime asOf, Guid? userId, CancellationToken ct = default)
        {
            var ids = await Db.RevenueContracts.Where(c => c.CompanyId == companyId && c.Status == "Active").Select(c => c.Id).ToListAsync(ct);
            int posted = 0, skipped = 0; var problems = new List<string>();
            foreach (var id in ids)
            {
                var lines = await Db.PerformanceObligations.Where(o => o.RevenueContractId == id && o.Pattern == "Ratable")
                    .SelectMany(o => o.Schedule).Where(l => l.JournalEntryId == null && l.PeriodStart <= asOf).OrderBy(l => l.PeriodStart).Select(l => l.Id).ToListAsync(ct);
                foreach (var lineId in lines)
                {
                    try { await RecogniseLineAsync(companyId, id, lineId, userId, ct); posted++; }
                    catch (InvalidOperationException ex) { skipped++; problems.Add($"Contract {id.ToString("N")[..8]}: {ex.Message}"); break; }
                }
            }
            return new RunSummary(posted, skipped, problems);
        }

        private Task RecogniseLineAsync(Guid companyId, Guid contractId, Guid lineId, Guid? userId, CancellationToken ct) =>
            Db.InTransactionAsync<bool>(async token =>
            {
                var c = await Load(companyId, contractId, token);
                var ob = c.Obligations.First(o => o.Schedule.Any(l => l.Id == lineId));
                var line = ob.Schedule.First(l => l.Id == lineId);
                if (line.JournalEntryId != null) return false;
                var je = await PostRecognitionAsync(c, ob, line.Amount, MonthEnd(line.PeriodStart), $"{c.ContractNo}-REV-{ob.Id.ToString("N")[..6]}-{line.Sequence}", userId, token);
                line.JournalEntryId = je.Id; line.RecognisedAt = DateTime.UtcNow;
                if (c.Obligations.All(o => o.Pattern == "PointInTime" ? o.SatisfiedOn != null : o.Schedule.All(l => l.JournalEntryId != null))) c.Status = "Completed";
                await Db.SaveChangesAsync(token); return true;
            }, ct);

        /// <summary>Satisfy a point-in-time obligation (e.g. delivery of goods) and recognise its full allocated price.</summary>
        public Task<RevenueContract> SatisfyPointInTimeAsync(Guid companyId, Guid contractId, Guid obligationId, DateTime date, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var c = await Load(companyId, contractId, token);
                if (c.Status != "Active") throw new InvalidOperationException("Only active contracts can be recognised.");
                var ob = c.Obligations.FirstOrDefault(o => o.Id == obligationId && o.Pattern == "PointInTime") ?? throw new InvalidOperationException("Point-in-time obligation not found.");
                if (ob.SatisfiedOn != null) throw new InvalidOperationException("This obligation is already satisfied.");
                var reference = $"{c.ContractNo}-PIT-{ob.Id.ToString("N")[..6]}";
                var je = await PostRecognitionAsync(c, ob, ob.AllocatedPrice, date, reference, userId, token);
                ob.SatisfiedOn = date; ob.SatisfactionJournalEntryId = je.Id;
                if (c.Obligations.All(o => o.Pattern == "PointInTime" ? o.SatisfiedOn != null : o.Schedule.All(l => l.JournalEntryId != null))) c.Status = "Completed";
                Audit.Record(companyId, userId, "RevenueContract.ObligationSatisfied", "Revenue", nameof(RevenueContract), c.Id, null, new { obligationId, date });
                await Db.SaveChangesAsync(token); return c;
            }, ct);

        private async Task<JournalEntry> PostRecognitionAsync(RevenueContract c, PerformanceObligation ob, decimal amount, DateTime date, string reference, Guid? userId, CancellationToken ct)
        {
            var recognisedBefore = c.Obligations.Sum(o => o.Schedule.Where(l => l.JournalEntryId != null).Sum(l => l.Amount)) + c.Obligations.Where(o => o.SatisfiedOn != null).Sum(o => o.AllocatedPrice);
            var liabilityAvailable = Math.Max(0m, c.BilledToDate - recognisedBefore);
            var fromLiability = Math.Min(amount, liabilityAvailable);
            var fromAsset = amount - fromLiability;
            if (fromAsset > 0 && c.ContractAssetAccountId == null)
                throw new InvalidOperationException($"Revenue of {amount:N2} exceeds billings and no contract-asset account is configured. Bill the customer or set a contract-asset account.");

            var lines = new List<GlLineRequest>();
            if (fromLiability > 0) lines.Add(new GlLineRequest(c.ContractLiabilityAccountId, fromLiability, 0, c.Currency, 1m, null, "Contract liability released"));
            if (fromAsset > 0) lines.Add(new GlLineRequest(c.ContractAssetAccountId!.Value, fromAsset, 0, c.Currency, 1m, null, "Contract asset (revenue ahead of billing)"));
            lines.Add(new GlLineRequest(ob.RevenueAccountId, 0, amount, c.Currency, 1m, null, $"Revenue: {ob.Description}"));
            return await Gl.PostAsync(c.CompanyId, date, JournalSource.Revenue, $"Revenue {c.ContractNo}: {ob.Description}", reference, lines, ct, userId);
        }

        /// <summary>
        /// Prospective contract modification (IFRS 15.21(b)): the old contract is closed as Modified, its unrecognised price plus any
        /// addition becomes a new contract recognised over the new term. Posted history is never changed.
        /// </summary>
        public Task<RevenueContract> ModifyAsync(Guid companyId, Guid id, string newContractNo, decimal additionalPrice, DateTime newEnd, string note, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var old = await Load(companyId, id, token);
                if (old.Status != "Active") throw new InvalidOperationException("Only active contracts can be modified.");
                if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("A modification note is required.");
                var recognised = old.Obligations.Sum(o => o.Schedule.Where(l => l.JournalEntryId != null).Sum(l => l.Amount)) + old.Obligations.Where(o => o.SatisfiedOn != null).Sum(o => o.AllocatedPrice);
                var remaining = old.TransactionPrice - recognised + additionalPrice;
                var nextMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                if (newEnd < nextMonth) throw new InvalidOperationException("The new end date must be after next month's start.");
                var revenueAccount = old.Obligations.First().RevenueAccountId;

                foreach (var l in old.Obligations.SelectMany(o => o.Schedule).Where(l => l.JournalEntryId == null).ToList()) Db.RevenueScheduleLines.Remove(l);
                old.Status = "Modified"; old.ModificationNote = note;
                await Db.SaveChangesAsync(token);

                var created = await CreateContractAsync(companyId, newContractNo, old.CustomerId, nextMonth, newEnd, remaining, old.Currency, old.ReceivableAccountId, old.ContractLiabilityAccountId,
                    old.ContractAssetAccountId, new[] { new ObligationRequest("Modified: remaining performance", remaining, "Ratable", nextMonth, newEnd, revenueAccount) }, userId, old.Id, note, token);
                created.BilledToDate = Math.Max(0m, old.BilledToDate - recognised);   // billed-but-unrecognised carries over as the contract liability
                created.Status = "Active"; created.ApprovedBy = old.ApprovedBy;
                await Db.SaveChangesAsync(token);
                return created;
            }, ct);

        public async Task<List<object>> PositionAsync(Guid companyId, CancellationToken ct = default)
        {
            var contracts = await Db.RevenueContracts.Include(c => c.Obligations).ThenInclude(o => o.Schedule).Where(c => c.CompanyId == companyId && c.Status != "Draft").ToListAsync(ct);
            return contracts.Select(c =>
            {
                var rec = c.Obligations.Sum(o => o.Schedule.Where(l => l.JournalEntryId != null).Sum(l => l.Amount)) + c.Obligations.Where(o => o.SatisfiedOn != null).Sum(o => o.AllocatedPrice);
                return (object)new { c.Id, c.ContractNo, c.Status, c.TransactionPrice, c.BilledToDate, Recognised = rec, ContractLiability = Math.Max(0m, c.BilledToDate - rec), ContractAsset = Math.Max(0m, rec - c.BilledToDate) };
            }).ToList();
        }

        public Task<List<RevenueContract>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            Db.RevenueContracts.Include(c => c.Obligations).ThenInclude(o => o.Schedule).Where(c => c.CompanyId == companyId).OrderByDescending(c => c.StartDate).Take(300).ToListAsync(ct);

        private async Task<RevenueContract> Load(Guid companyId, Guid id, CancellationToken ct) =>
            await Db.RevenueContracts.Include(c => c.Obligations).ThenInclude(o => o.Schedule).FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Contract not found.");
    }
}
