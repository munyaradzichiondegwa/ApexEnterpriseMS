using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>Three-way match of purchase order, goods received and supplier invoice (spec 16).</summary>
    public class ApMatchService
    {
        private readonly ApexErpDbContext _db;
        private readonly SettingsService _settings;
        private readonly AuditService _audit;
        private readonly GovernanceService _gov;

        public ApMatchService(ApexErpDbContext db, SettingsService settings, AuditService audit, GovernanceService gov)
        { _db = db; _settings = settings; _audit = audit; _gov = gov; }

        public async Task<MatchResult> EvaluateAsync(Guid companyId, ApInvoice invoice, CancellationToken ct = default)
        {
            if (invoice.PurchaseOrderId == null) return new MatchResult(MatchOutcome.Matched, Array.Empty<MatchIssue>());
            var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == invoice.PurchaseOrderId && p.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Purchase order not found.");
            var poLines = await _db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == po.Id).ToListAsync(ct);
            var lines = invoice.Lines ?? new List<ApInvoiceLine>();

            // quantities already invoiced against this PO by other live invoices
            var others = await _db.ApInvoices.Include(i => i.Lines)
                .Where(i => i.CompanyId == companyId && i.PurchaseOrderId == po.Id && i.Id != invoice.Id && i.Status != InvoiceStatus.Cancelled).ToListAsync(ct);

            var issues = new List<MatchIssue>();
            var matchLines = new List<MatchLine>();
            foreach (var g in lines.GroupBy(l => l.InventoryItemId))
            {
                if (g.Key == null) { issues.Add(new MatchIssue("(no item)", "A line has no inventory item, so it cannot be matched to the purchase order.")); continue; }
                var po4Item = poLines.Where(l => l.InventoryItemId == g.Key).ToList();
                if (po4Item.Count == 0) { issues.Add(new MatchIssue(g.Key.ToString()!, "This item is not on the purchase order.")); continue; }
                var ordered = po4Item.Sum(l => l.QuantityOrdered);
                var received = po4Item.Sum(l => l.QuantityReceived);
                var priorInvoiced = others.SelectMany(i => i.Lines).Where(l => l.InventoryItemId == g.Key).Sum(l => l.Quantity);
                var qty = g.Sum(l => l.Quantity);
                var avgInvoicedPrice = qty == 0 ? 0m : g.Sum(l => l.Quantity * l.UnitCost) / qty;
                var orderPrice = po4Item.Sum(l => l.QuantityOrdered * l.UnitCost) / Math.Max(1m, ordered);
                matchLines.Add(new MatchLine(g.Key.ToString()!, ordered, received, qty + priorInvoiced, Math.Round(orderPrice, 6), Math.Round(avgInvoicedPrice, 6)));
            }

            var qtyTol = await _settings.GetDecimalAsync(companyId, SettingsService.ThreeWayQtyTolerancePct, 0m, ct);
            var priceTol = await _settings.GetDecimalAsync(companyId, SettingsService.ThreeWayPriceTolerancePct, 0m, ct);
            var result = ThreeWayMatch.Evaluate(matchLines, qtyTol, priceTol);
            if (issues.Count == 0) return result;
            return new MatchResult(MatchOutcome.Discrepancy, issues.Concat(result.Issues).ToList());
        }

        /// <summary>Runs the match and stores the outcome on the invoice. Called when an invoice is posted and on demand.</summary>
        public async Task<MatchResult> EvaluateAndStoreAsync(Guid companyId, ApInvoice invoice, CancellationToken ct = default)
        {
            var r = await EvaluateAsync(companyId, invoice, ct);
            if (invoice.PurchaseOrderId != null && invoice.MatchStatus != "Overridden")
            {
                invoice.MatchStatus = r.Outcome == MatchOutcome.Matched ? "Matched" : r.Outcome == MatchOutcome.WithinTolerance ? "WithinTolerance" : "Discrepancy";
                invoice.MatchNotes = r.Issues.Count == 0 ? null : string.Join(" | ", r.Issues.Select(i => i.Message));
            }
            return r;
        }

        /// <summary>A person with authority may release a discrepancy for payment. The reason and the person are recorded.</summary>
        public async Task<ApInvoice> OverrideAsync(Guid companyId, Guid invoiceId, Guid approver, string reason, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required to override a matching discrepancy.");
            var inv = await _db.ApInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Invoice not found.");
            if (inv.MatchStatus != "Discrepancy") throw new InvalidOperationException("Only invoices with a discrepancy can be overridden.");
            var d = await _gov.CheckApprovalAuthorityAsync(companyId, approver, "MatchOverride", inv.Total, inv.CreatedBy, ct);
            if (!d.Allowed) throw new InvalidOperationException(d.Reason ?? "Not authorised.");
            inv.MatchStatus = "Overridden"; inv.MatchOverriddenBy = approver; inv.MatchNotes = (inv.MatchNotes ?? "") + " | Override: " + reason;
            _audit.Record(companyId, approver, "ApInvoice.MatchOverridden", "AP", nameof(ApInvoice), inv.Id, null, new { reason });
            await _db.SaveChangesAsync(ct); return inv;
        }
    }

    public sealed record StatementLine(string Reference, decimal Amount);

    /// <summary>Payment runs, independent payment authorisation, supplier bank-detail control and statement reconciliation (spec 16).</summary>
    public class ApControlsService
    {
        private readonly ApexErpDbContext _db;
        private readonly ApService _ap;
        private readonly GovernanceService _gov;
        private readonly SettingsService _settings;
        private readonly AuditService _audit;

        public ApControlsService(ApexErpDbContext db, ApService ap, GovernanceService gov, SettingsService settings, AuditService audit)
        { _db = db; _ap = ap; _gov = gov; _settings = settings; _audit = audit; }

        /// <summary>Checks applied to every payment, direct or in a run: supplier hold, pending bank change, unresolved match discrepancy, balance.</summary>
        public async Task ValidatePaymentAsync(Guid companyId, Guid supplierId, IReadOnlyDictionary<Guid, decimal> allocations, CancellationToken ct = default)
        {
            var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId && s.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Supplier not found.");
            if (!supplier.IsActive) throw new InvalidOperationException($"{supplier.Name} is inactive.");
            if (supplier.PaymentHold) throw new InvalidOperationException($"Payments to {supplier.Name} are on hold while a bank-detail change is verified.");
            var ids = allocations.Keys.ToList();
            var invoices = await _db.ApInvoices.Where(i => i.CompanyId == companyId && ids.Contains(i.Id)).ToListAsync(ct);
            foreach (var (id, amount) in allocations)
            {
                var inv = invoices.FirstOrDefault(i => i.Id == id) ?? throw new InvalidOperationException("Invoice not found.");
                if (inv.SupplierId != supplierId) throw new InvalidOperationException($"Invoice {inv.InvoiceNo} belongs to a different supplier.");
                if (inv.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled) throw new InvalidOperationException($"Invoice {inv.InvoiceNo} is not payable.");
                if (inv.MatchStatus == "Discrepancy") throw new InvalidOperationException($"Invoice {inv.InvoiceNo} has an unresolved matching discrepancy: {inv.MatchNotes}");
                if (amount <= 0 || amount > inv.Outstanding) throw new InvalidOperationException($"Invalid amount for invoice {inv.InvoiceNo}.");
            }
        }

        // ---- payment runs ----
        public async Task<ApPaymentRun> CreateRunAsync(Guid companyId, DateTime date, string currency, Guid bankGlAccountId, Guid apControlAccountId,
            IReadOnlyList<(Guid InvoiceId, decimal Amount)> items, Guid userId, CancellationToken ct = default)
        {
            if (items.Count == 0) throw new InvalidOperationException("Select at least one invoice.");
            var ids = items.Select(i => i.InvoiceId).ToList();
            if (ids.Distinct().Count() != ids.Count) throw new InvalidOperationException("An invoice can appear only once in a run.");
            var invoices = await _db.ApInvoices.Where(i => i.CompanyId == companyId && ids.Contains(i.Id)).ToListAsync(ct);
            if (invoices.Count != ids.Count) throw new InvalidOperationException("One or more invoices were not found.");
            if (invoices.Any(i => i.Currency != currency)) throw new InvalidOperationException($"Every invoice in the run must be in {currency}.");
            var alreadyInRun = await _db.ApPaymentRunItems.Where(i => ids.Contains(i.ApInvoiceId)).Join(_db.ApPaymentRuns.Where(r => r.Status == "Draft" || r.Status == "Authorised"), i => i.ApPaymentRunId, r => r.Id, (i, r) => i.ApInvoiceId).ToListAsync(ct);
            if (alreadyInRun.Count > 0) throw new InvalidOperationException("Some invoices are already in an open payment run.");

            foreach (var g in items.GroupBy(i => invoices.First(v => v.Id == i.InvoiceId).SupplierId))
                await ValidatePaymentAsync(companyId, g.Key, g.ToDictionary(x => x.InvoiceId, x => x.Amount), ct);

            var count = await _db.ApPaymentRuns.IgnoreQueryFilters().CountAsync(r => r.CompanyId == companyId, ct);
            var run = new ApPaymentRun
            {
                CompanyId = companyId, RunNo = $"RUN-{DateTime.UtcNow:yyyy}-{count + 1:D4}", PaymentDate = date.Date, Currency = currency, BankGlAccountId = bankGlAccountId,
                ApControlAccountId = apControlAccountId, TotalAmount = items.Sum(i => i.Amount), PreparedBy = userId, CreatedBy = userId
            };
            foreach (var i in items) run.Items.Add(new ApPaymentRunItem { SupplierId = invoices.First(v => v.Id == i.InvoiceId).SupplierId, ApInvoiceId = i.InvoiceId, Amount = i.Amount });
            _db.ApPaymentRuns.Add(run);
            _audit.Record(companyId, userId, "PaymentRun.Created", "AP", nameof(ApPaymentRun), run.Id, null, new { run.RunNo, run.TotalAmount });
            await _db.SaveChangesAsync(ct); return run;
        }

        public async Task<ApPaymentRun> AuthoriseRunAsync(Guid companyId, Guid runId, Guid approver, CancellationToken ct = default)
        {
            var run = await LoadRun(companyId, runId, ct);
            if (run.Status != "Draft") throw new InvalidOperationException("Only draft runs can be authorised.");
            var d = await _gov.CheckApprovalAuthorityAsync(companyId, approver, SettingsService.PaymentRunAuthorityEntityType, run.TotalAmount, run.PreparedBy, ct);
            if (!d.Allowed) throw new InvalidOperationException(d.Reason ?? "Not authorised.");
            run.Status = "Authorised"; run.AuthorisedBy = approver; run.AuthorisedAt = DateTime.UtcNow;
            _audit.Record(companyId, approver, "PaymentRun.Authorised", "AP", nameof(ApPaymentRun), run.Id, null, new { run.RunNo, run.TotalAmount });
            await _db.SaveChangesAsync(ct); return run;
        }

        public Task<ApPaymentRun> ReleaseRunAsync(Guid companyId, Guid runId, Guid releasedBy, CancellationToken ct = default) =>
            _db.InTransactionAsync(async token =>
            {
                var run = await LoadRun(companyId, runId, token);
                if (run.Status != "Authorised") throw new InvalidOperationException("A run must be authorised before it can be released.");
                if (run.AuthorisedBy == run.PreparedBy) throw new InvalidOperationException("The run was prepared and authorised by the same person.");
                foreach (var g in run.Items.GroupBy(i => i.SupplierId))
                {
                    var alloc = g.ToDictionary(x => x.ApInvoiceId, x => x.Amount);
                    await ValidatePaymentAsync(companyId, g.Key, alloc, token);      // re-check at release: things change between authorise and release
                    var payment = await _ap.PayAndAllocateAsync(companyId, g.Key, run.PaymentDate, run.Currency, alloc.Values.Sum(), PaymentMethod.BankTransfer,
                        run.BankGlAccountId, run.ApControlAccountId, alloc, token);
                    payment.PreparedBy = run.PreparedBy; payment.AuthorisedBy = run.AuthorisedBy; payment.AuthorisedAt = run.AuthorisedAt; payment.PaymentRunId = run.Id;
                    foreach (var item in g) item.ApPaymentId = payment.Id;
                }
                run.Status = "Released";
                _audit.Record(companyId, releasedBy, "PaymentRun.Released", "AP", nameof(ApPaymentRun), run.Id, null, new { run.RunNo, run.TotalAmount });
                await _db.SaveChangesAsync(token); return run;
            }, ct);

        public async Task CancelRunAsync(Guid companyId, Guid runId, Guid userId, CancellationToken ct = default)
        {
            var run = await LoadRun(companyId, runId, ct);
            if (run.Status is "Released" or "Cancelled") throw new InvalidOperationException("This run can no longer be cancelled.");
            run.Status = "Cancelled";
            _audit.Record(companyId, userId, "PaymentRun.Cancelled", "AP", nameof(ApPaymentRun), run.Id, null, new { run.RunNo });
            await _db.SaveChangesAsync(ct);
        }

        public Task<List<ApPaymentRun>> ListRunsAsync(Guid companyId, CancellationToken ct = default) =>
            _db.ApPaymentRuns.Include(r => r.Items).Where(r => r.CompanyId == companyId).OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);

        private async Task<ApPaymentRun> LoadRun(Guid companyId, Guid id, CancellationToken ct) =>
            await _db.ApPaymentRuns.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Payment run not found.");

        // ---- supplier bank-detail change control ----
        public async Task<SupplierBankChange> RequestBankChangeAsync(Guid companyId, Guid supplierId, string bankName, string accountNo, string? branch, Guid userId, CancellationToken ct = default)
        {
            var s = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == supplierId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Supplier not found.");
            if (string.IsNullOrWhiteSpace(bankName) || string.IsNullOrWhiteSpace(accountNo)) throw new InvalidOperationException("Bank name and account number are required.");
            if (await _db.SupplierBankChanges.AnyAsync(c => c.CompanyId == companyId && c.SupplierId == supplierId && c.Status == "Pending", ct)) throw new InvalidOperationException("A change is already awaiting verification for this supplier.");
            var c = new SupplierBankChange { CompanyId = companyId, SupplierId = supplierId, NewBankName = bankName.Trim(), NewBankAccountNo = accountNo.Trim(), NewBankBranch = branch, RequestedBy = userId, CreatedBy = userId };
            s.PaymentHold = true;     // no payments until someone else verifies the new details
            _db.SupplierBankChanges.Add(c);
            _audit.Record(companyId, userId, "SupplierBank.ChangeRequested", "AP", nameof(Supplier), s.Id, new { s.BankName, MaskedAccount = Mask(s.BankAccountNo) }, new { bankName, MaskedAccount = Mask(accountNo) });
            await _db.SaveChangesAsync(ct); return c;
        }

        public async Task<SupplierBankChange> VerifyBankChangeAsync(Guid companyId, Guid changeId, Guid verifier, string note, bool approve, CancellationToken ct = default)
        {
            var c = await _db.SupplierBankChanges.FirstOrDefaultAsync(x => x.Id == changeId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Change request not found.");
            if (c.Status != "Pending") throw new InvalidOperationException("This request has already been decided.");
            if (c.RequestedBy == verifier) throw new InvalidOperationException("The person who requested the change cannot verify it.");
            if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("Record how the details were verified (e.g. call-back to the number on file).");
            var s = await _db.Suppliers.FirstAsync(x => x.Id == c.SupplierId && x.CompanyId == companyId, ct);
            c.VerifiedBy = verifier; c.VerificationNote = note; c.Status = approve ? "Verified" : "Rejected";
            if (approve) { s.BankName = c.NewBankName; s.BankAccountNo = c.NewBankAccountNo; s.BankBranch = c.NewBankBranch; }
            s.PaymentHold = false;
            _audit.Record(companyId, verifier, approve ? "SupplierBank.ChangeVerified" : "SupplierBank.ChangeRejected", "AP", nameof(Supplier), s.Id, null, new { note });
            await _db.SaveChangesAsync(ct); return c;
        }

        public Task<List<SupplierBankChange>> ListBankChangesAsync(Guid companyId, CancellationToken ct = default) =>
            _db.SupplierBankChanges.Where(c => c.CompanyId == companyId).OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct);

        private static string Mask(string? s) => string.IsNullOrEmpty(s) ? "" : new string('*', Math.Max(0, s.Length - 4)) + s[^Math.Min(4, s.Length)..];

        // ---- supplier statement reconciliation ----
        public async Task<object> ReconcileStatementAsync(Guid companyId, Guid supplierId, IReadOnlyList<StatementLine> statement, DateTime asOf, CancellationToken ct = default)
        {
            var open = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.SupplierId == supplierId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).ToListAsync(ct);
            var matched = new List<object>(); var onlyOnStatement = new List<object>(); var usedIds = new HashSet<Guid>();
            foreach (var line in statement)
            {
                var key = ValuationRules.NormaliseInvoiceNo(line.Reference);
                var inv = open.FirstOrDefault(i => !usedIds.Contains(i.Id) && (ValuationRules.NormaliseInvoiceNo(i.SupplierInvoiceNo) == key || ValuationRules.NormaliseInvoiceNo(i.InvoiceNo) == key));
                if (inv == null) { onlyOnStatement.Add(new { line.Reference, line.Amount }); continue; }
                usedIds.Add(inv.Id);
                matched.Add(new { line.Reference, StatementAmount = line.Amount, LedgerAmount = inv.Outstanding, Difference = line.Amount - inv.Outstanding, inv.InvoiceNo });
            }
            var onlyInLedger = open.Where(i => !usedIds.Contains(i.Id)).Select(i => new { i.InvoiceNo, i.SupplierInvoiceNo, i.Outstanding }).ToList();
            var statementTotal = statement.Sum(s => s.Amount); var ledgerTotal = open.Sum(i => i.Outstanding);
            return new { AsOf = asOf.Date, StatementTotal = statementTotal, LedgerTotal = ledgerTotal, Difference = statementTotal - ledgerTotal, Matched = matched, OnlyOnStatement = onlyOnStatement, OnlyInLedger = onlyInLedger };
        }

        /// <summary>Withholding tax withheld in a period per supplier: the schedule behind each withholding certificate and the return to ZIMRA.</summary>
        public async Task<List<object>> WithholdingScheduleAsync(Guid companyId, DateTime from, DateTime to, CancellationToken ct = default) =>
            (await _db.ApInvoices.Include(i => i.Supplier).Where(i => i.CompanyId == companyId && i.WhtAmount > 0 && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled && i.InvoiceDate >= from && i.InvoiceDate <= to).ToListAsync(ct))
                .GroupBy(i => i.SupplierId).Select(g => (object)new { SupplierId = g.Key, Supplier = g.First().Supplier?.Name, TaxableAmount = g.Sum(i => i.Subtotal), WithheldAmount = g.Sum(i => i.WhtAmount), Invoices = g.Select(i => i.InvoiceNo).ToList() }).ToList();
    }
}
