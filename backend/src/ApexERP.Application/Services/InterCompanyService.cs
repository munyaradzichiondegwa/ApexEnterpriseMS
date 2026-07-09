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

        public InterCompanyService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        /// <summary>Call immediately after ArService.PostInvoiceAsync when the invoice's customer is
        /// LinkedCompanyId-flagged. Creates the mirror AP invoice in the counterparty entity and logs
        /// the IntercompanyTransaction. Must complete within 5 seconds per the PRD's NFR.</summary>
        public async Task<IntercompanyTransaction> CreateMirrorPostingAsync(Guid fromCompanyId, Guid arInvoiceId, CancellationToken ct = default)
        {
            var invoice = await _db.ArInvoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == arInvoiceId && i.CompanyId == fromCompanyId, ct)
                ?? throw new InvalidOperationException("Source AR invoice not found.");

            var toCompanyId = invoice.Customer?.LinkedCompanyId
                ?? throw new InvalidOperationException("Customer is not linked to a group company -- not an inter-company transaction.");

            var rule = await _db.InterCompanyRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.CompanyId == fromCompanyId && r.FromCompanyId == fromCompanyId && r.ToCompanyId == toCompanyId && r.IsActive, ct)
                ?? throw new InvalidOperationException("No active inter-company rule configured for this entity pair. Configure one under Group > Inter-Company Rules first.");

            // Find (or require) a Supplier record in the target company representing the sending entity.
            var mirrorSupplier = await _db.Suppliers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.CompanyId == toCompanyId && s.LinkedCompanyId == fromCompanyId, ct)
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
            var count = await _db.ApInvoices.IgnoreQueryFilters().CountAsync(i => i.CompanyId == toCompanyId && i.InvoiceNo.StartsWith(prefix), ct);

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
            await _db.SaveChangesAsync(ct);

            // Post the mirror invoice to Company B's GL: Dr expense/clearing, Cr Trade Creditors.
            var glLines = new List<GlLineRequest>
            {
                new(rule.DefaultReceiverApAccountId, mirrorInvoice.Subtotal, 0, mirrorInvoice.Currency, mirrorInvoice.ExchangeRate, null, "Inter-company mirror expense/clearing"),
                new(mirrorSupplier.ApAccountId ?? rule.DefaultReceiverApAccountId, 0, mirrorInvoice.Subtotal, mirrorInvoice.Currency, mirrorInvoice.ExchangeRate, null, $"Inter-company: {mirrorInvoice.InvoiceNo}")
            };
            var je = await _gl.PostAsync(toCompanyId, invoice.InvoiceDate, JournalSource.AP,
                $"Inter-company mirror of {invoice.InvoiceNo}", invoice.InvoiceNo, glLines, ct);
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
            await _db.SaveChangesAsync(ct);
            return transaction;
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
