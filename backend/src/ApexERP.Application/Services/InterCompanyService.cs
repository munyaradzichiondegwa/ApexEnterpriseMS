using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record NettingProposal(Guid CompanyAId, Guid CompanyBId, decimal GrossAToB, decimal GrossBToA, decimal NetAmount, Guid PayerCompanyId, List<Guid> TransactionIds);

    /// <summary>
    /// PRD Module 2 (GL including Inter-Company): when Company A invoices Company B (a fellow
    /// group entity), automatically creates a mirror AP invoice in Company B, links both sides via
    /// IntercompanyTransaction, and supports periodic netting batches that settle multiple open
    /// inter-entity balances in one atomic transaction. All cross-company reads/writes here use
    /// IgnoreQueryFilters() deliberately -- this service's whole job is to act across tenant
    /// boundaries within a single group, which the per-request tenant filter would otherwise block.
    /// </summary>
    public class InterCompanyService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;
        private readonly ExceptionQueueService _exceptions;

        public InterCompanyService(ApexErpDbContext db, GlPostingService gl, ExceptionQueueService exceptions)
        {
            _db = db;
            _gl = gl;
            _exceptions = exceptions;
        }

        /// <summary>
        /// Outcome of trying to mirror an invoice into the counterparty's books. Status is one of
        /// NotApplicable (customer is not a group company), Completed, or Failed (an exception item was raised and
        /// can be retried from the exception queue). Callers must surface a Failed status to the user.
        /// </summary>
        public record MirrorOutcome(string Status, Guid? TransactionId, Guid? ExceptionId, string? Message);

        /// <summary>Call immediately after ArService.PostInvoiceAsync when the invoice's customer is
        /// LinkedCompanyId-flagged. Creates the mirror AP invoice in the counterparty entity and logs
        /// the IntercompanyTransaction. Must complete within 5 seconds per the PRD's NFR.
        /// All-or-nothing and idempotent: the mirror invoice, its journal and the link row commit together or not
        /// at all, and calling it again for an invoice that is already mirrored returns the existing link
        /// instead of creating a duplicate (audit finding 8).</summary>
        public Task<IntercompanyTransaction> CreateMirrorPostingAsync(Guid fromCompanyId, Guid arInvoiceId, CancellationToken ct = default)
        {
            return _db.InTransactionAsync(async token =>
            {
                // One mirror per invoice: serialise concurrent attempts, and make a repeat a harmless no-op.
                await _db.TryAcquireTransactionLockAsync($"icmirror:{arInvoiceId:N}", token);
                var already = await _db.IntercompanyTransactions.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.FromCompanyId == fromCompanyId && t.SourceArInvoiceId == arInvoiceId, token);
                if (already != null) return already;

                var invoice = await _db.ArInvoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Customer)
                    .FirstOrDefaultAsync(i => i.Id == arInvoiceId && i.CompanyId == fromCompanyId, token)
                    ?? throw new InvalidOperationException("Source AR invoice not found.");

                var toCompanyId = invoice.Customer?.LinkedCompanyId
                    ?? throw new InvalidOperationException("Customer is not linked to a group company -- not an inter-company transaction.");

                var rule = await _db.InterCompanyRules.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(r => r.CompanyId == fromCompanyId && r.FromCompanyId == fromCompanyId && r.ToCompanyId == toCompanyId && r.IsActive, token)
                    ?? throw new InvalidOperationException("No active inter-company rule configured for this entity pair. Configure one under Group > Inter-Company Rules first.");

                // Find (or require) a Supplier record in the target company representing the sending entity.
                var mirrorSupplier = await _db.Suppliers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.CompanyId == toCompanyId && s.LinkedCompanyId == fromCompanyId, token)
                    ?? throw new InvalidOperationException("Target company has no Supplier record linked back to the sending company. Create one first (Supplier.LinkedCompanyId = sending company).");

                var mirrorLines = invoice.Lines.Select(l => new ApInvoiceLine
                {
                    Description = $"Inter-company mirror: {l.Description}",
                    Quantity = l.Quantity,
                    UnitCost = l.UnitPrice,
                    LineSubtotal = l.LineSubtotal,
                    LineVat = 0, // VAT is not re-charged on inter-company mirror postings
                    LineTotal = l.LineSubtotal,
                    GlAccountId = rule.DefaultReceiverApAccountId
                }).ToList();

                var prefix = $"ICO-{DateTime.UtcNow:yyyy}-";
                await _db.TryAcquireTransactionLockAsync($"docno:{toCompanyId:N}:{prefix}", token);
                var count = await _db.ApInvoices.IgnoreQueryFilters().CountAsync(i => i.CompanyId == toCompanyId && i.InvoiceNo.StartsWith(prefix), token);

                var mirrorInvoice = new ApInvoice
                {
                    CompanyId = toCompanyId,
                    InvoiceNo = $"{prefix}{(count + 1):D5}",
                    SupplierId = mirrorSupplier.Id,
                    InvoiceDate = invoice.InvoiceDate,
                    DueDate = invoice.DueDate,
                    Currency = invoice.Currency,
                    ExchangeRate = invoice.ExchangeRate,
                    Subtotal = invoice.Subtotal,
                    VatAmount = 0,
                    Total = invoice.Subtotal,
                    Outstanding = invoice.Subtotal,
                    Status = InvoiceStatus.Draft,
                    IsIntercompanyMirror = true,
                    Lines = mirrorLines
                };
                _db.ApInvoices.Add(mirrorInvoice);

                // Post the mirror invoice to Company B's GL: Dr expense/clearing, Cr Trade Creditors.
                var glLines = new List<GlLineRequest>
                {
                    new(rule.DefaultReceiverApAccountId, mirrorInvoice.Subtotal, 0, mirrorInvoice.Currency, mirrorInvoice.ExchangeRate, null, "Inter-company mirror expense/clearing"),
                    new(mirrorSupplier.ApAccountId ?? rule.DefaultReceiverApAccountId, 0, mirrorInvoice.Subtotal, mirrorInvoice.Currency, mirrorInvoice.ExchangeRate, null, $"Inter-company: {mirrorInvoice.InvoiceNo}")
                };
                var je = await _gl.PostAsync(toCompanyId, invoice.InvoiceDate, JournalSource.AP,
                    $"Inter-company mirror of {invoice.InvoiceNo}", invoice.InvoiceNo, glLines, token);
                mirrorInvoice.JournalEntryId = je.Id;
                mirrorInvoice.Status = InvoiceStatus.Posted;

                var transaction = new IntercompanyTransaction
                {
                    CompanyId = fromCompanyId,
                    FromCompanyId = fromCompanyId,
                    ToCompanyId = toCompanyId,
                    TransactionDate = invoice.InvoiceDate,
                    Description = $"Mirror of {invoice.InvoiceNo}",
                    Amount = invoice.Subtotal,
                    Currency = invoice.Currency,
                    Status = IntercompanyStatus.Matched,
                    SourceArInvoiceId = invoice.Id,
                    SourceJournalEntryId = invoice.JournalEntryId,
                    MirrorApInvoiceId = mirrorInvoice.Id,
                    MirrorJournalEntryId = je.Id
                };
                _db.IntercompanyTransactions.Add(transaction);
                await _db.SaveChangesAsync(token);
                return transaction;
            }, ct);
        }

        /// <summary>
        /// Mirrors an already-posted AR invoice WITHOUT letting a failure disappear. The local invoice stays posted;
        /// if the counterparty side cannot be posted (closed period, missing rule or supplier link, ...) that attempt
        /// is rolled back as a unit (savepoint), an exception item is raised for follow-up, and the caller receives a
        /// Failed outcome to show the user. Safe to call again (retry): an existing mirror is returned as Completed and
        /// any open exception for it is closed.
        /// </summary>
        public async Task<MirrorOutcome> TryMirrorAsync(Guid fromCompanyId, Guid arInvoiceId, Guid? userId, CancellationToken ct = default)
        {
            var invoice = await _db.ArInvoices.IgnoreQueryFilters().Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == arInvoiceId && i.CompanyId == fromCompanyId, ct)
                ?? throw new InvalidOperationException("Source AR invoice not found.");

            if (invoice.Customer?.LinkedCompanyId == null)
                return new MirrorOutcome("NotApplicable", null, null, null);

            // Remember what is already tracked so a failed attempt can be discarded from the change tracker.
            var trackedBefore = _db.ChangeTracker.Entries().Select(e => e.Entity)
                .ToHashSet<object>(ReferenceEqualityComparer.Instance);

            try
            {
                var transaction = await RunIsolatedAsync(token => CreateMirrorPostingAsync(fromCompanyId, arInvoiceId, token), ct);

                await _exceptions.ResolveForSourceAsync(fromCompanyId, ExceptionCategory.InterCompanyMirror, nameof(ArInvoice),
                    arInvoiceId, userId, "Inter-company mirror posted successfully.", ct);
                await _db.SaveChangesAsync(ct);
                return new MirrorOutcome("Completed", transaction.Id, null, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
            {
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    if (!trackedBefore.Contains(entry.Entity)) entry.State = EntityState.Detached;

                var message = ex is DbUpdateException ? "Database error while posting the mirror: " + ex.GetBaseException().Message : ex.Message;
                var item = await _exceptions.RaiseAsync(fromCompanyId, ExceptionCategory.InterCompanyMirror, nameof(ArInvoice),
                    arInvoiceId, message, userId, ct);
                await _db.SaveChangesAsync(ct);
                return new MirrorOutcome("Failed", null, item.Id, message);
            }
        }

        /// <summary>
        /// Runs <paramref name="attempt"/> so that, if it throws, everything it wrote is undone while the work done
        /// earlier in the same request (the posted invoice) is kept: a savepoint inside the request transaction, or its
        /// own transaction when none is open.
        /// </summary>
        private async Task<T> RunIsolatedAsync<T>(Func<CancellationToken, Task<T>> attempt, CancellationToken ct)
        {
            var ambient = _db.Database.CurrentTransaction;
            if (ambient == null || !ambient.SupportsSavepoints)
                return await _db.InTransactionAsync(attempt, ct);

            const string savepoint = "intercompany_mirror";
            await ambient.CreateSavepointAsync(savepoint, ct);
            try
            {
                var result = await attempt(ct);
                await ambient.ReleaseSavepointAsync(savepoint, ct);
                return result;
            }
            catch
            {
                await ambient.RollbackToSavepointAsync(savepoint, ct);
                throw;
            }
        }

        /// <summary>Inter-company reconciliation report: every transaction between any two group
        /// entities with its matching status, for verification before running eliminations.</summary>
        public async Task<List<IntercompanyTransaction>> GetReconciliationReportAsync(Guid companyId, CancellationToken ct = default)
        {
            return await _db.IntercompanyTransactions.IgnoreQueryFilters()
                .Where(t => t.FromCompanyId == companyId || t.ToCompanyId == companyId)
                .OrderByDescending(t => t.TransactionDate)
                .ToListAsync(ct);
        }

        /// <summary>Computes the net open balance between two entities without posting anything --
        /// review this before calling ExecuteNettingAsync.</summary>
        public async Task<NettingProposal> ProposeNettingAsync(Guid companyAId, Guid companyBId, CancellationToken ct = default)
        {
            var open = await _db.IntercompanyTransactions.IgnoreQueryFilters()
                .Where(t => t.Status == IntercompanyStatus.Matched &&
                            ((t.FromCompanyId == companyAId && t.ToCompanyId == companyBId) ||
                             (t.FromCompanyId == companyBId && t.ToCompanyId == companyAId)))
                .ToListAsync(ct);

            var aToB = open.Where(t => t.FromCompanyId == companyAId).Sum(t => t.Amount);
            var bToA = open.Where(t => t.FromCompanyId == companyBId).Sum(t => t.Amount);
            var net = Math.Abs(aToB - bToA);
            var payer = aToB > bToA ? companyBId : companyAId; // whoever owes more, pays the net

            return new NettingProposal(companyAId, companyBId, aToB, bToA, net, payer, open.Select(t => t.Id).ToList());
        }

        /// <summary>Posts a single settlement journal in each entity for the net amount, atomically,
        /// and marks all included transactions Settled. In the payer entity: Dr the inter-company
        /// control account (clearing the payable), Cr the bank/clearing account (cash out). In the
        /// payee entity: Dr the bank/clearing account (cash in), Cr the inter-company control
        /// account (clearing the receivable).</summary>
        public async Task<NettingBatch> ExecuteNettingAsync(Guid companyAId, Guid companyBId, DateTime settlementDate,
            Guid companyABankAccountId, Guid companyBBankAccountId, CancellationToken ct = default)
        {
            var proposal = await ProposeNettingAsync(companyAId, companyBId, ct);
            if (proposal.NetAmount <= 0) throw new InvalidOperationException("No open inter-company balance to net between these entities.");

            var payerCompanyId = proposal.PayerCompanyId;
            var payeeCompanyId = payerCompanyId == companyAId ? companyBId : companyAId;
            var payerBankAccountId = payerCompanyId == companyAId ? companyABankAccountId : companyBBankAccountId;
            var payeeBankAccountId = payeeCompanyId == companyAId ? companyABankAccountId : companyBBankAccountId;

            // The inter-company control account on each side comes from whichever rule governs that
            // direction; rules are configured both ways when mirror-posting is bidirectional.
            var payerRule = await _db.InterCompanyRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.CompanyId == payerCompanyId && r.IsActive, ct)
                ?? throw new InvalidOperationException($"No inter-company rule configured for company {payerCompanyId}.");
            var payeeRule = await _db.InterCompanyRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.CompanyId == payeeCompanyId && r.IsActive, ct)
                ?? throw new InvalidOperationException($"No inter-company rule configured for company {payeeCompanyId}.");

            var payerJe = await _gl.PostAsync(payerCompanyId, settlementDate, JournalSource.AP,
                $"Inter-company netting settlement (payer)", $"NET-{settlementDate:yyyyMMdd}",
                new[]
                {
                    new GlLineRequest(payerRule.DefaultReceiverApAccountId, proposal.NetAmount, 0, "USD", 1m, null, "Clear inter-company payable"),
                    new GlLineRequest(payerBankAccountId, 0, proposal.NetAmount, "USD", 1m, null, "Cash paid in settlement")
                }, ct);

            var payeeJe = await _gl.PostAsync(payeeCompanyId, settlementDate, JournalSource.AR,
                $"Inter-company netting settlement (payee)", $"NET-{settlementDate:yyyyMMdd}",
                new[]
                {
                    new GlLineRequest(payeeBankAccountId, proposal.NetAmount, 0, "USD", 1m, null, "Cash received in settlement"),
                    new GlLineRequest(payeeRule.DefaultSenderArAccountId, 0, proposal.NetAmount, "USD", 1m, null, "Clear inter-company receivable")
                }, ct);

            var batch = new NettingBatch
            {
                CompanyId = companyAId,
                CompanyAId = companyAId,
                CompanyBId = companyBId,
                BatchDate = settlementDate,
                NetAmount = proposal.NetAmount,
                PayerCompanyId = payerCompanyId,
                SettlementJournalEntryIdPayer = payerJe.Id,
                SettlementJournalEntryIdPayee = payeeJe.Id
            };
            _db.NettingBatches.Add(batch);

            var transactions = await _db.IntercompanyTransactions.IgnoreQueryFilters()
                .Where(t => proposal.TransactionIds.Contains(t.Id)).ToListAsync(ct);
            foreach (var t in transactions)
            {
                t.Status = IntercompanyStatus.Settled;
                t.NettingBatchId = batch.Id;
            }

            await _db.SaveChangesAsync(ct);
            return batch;
        }
    }
}
