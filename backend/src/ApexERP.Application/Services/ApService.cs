using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record ApInvoiceLineRequest(string Description, decimal Quantity, decimal UnitCost, Guid? TaxCodeId, Guid GlAccountId, Guid? InventoryItemId = null);

    /// <summary>PRD Module 4 (Accounts Payable): supplier invoicing with VAT input and Withholding
    /// Tax (WHT) calculation on qualifying non-compliant suppliers, payments, and GL auto-posting.</summary>
    public class ApService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;
        private readonly ApMatchService? _match;
        private readonly PublicSectorService? _publicSector;

        public ApService(ApexErpDbContext db, GlPostingService gl, ApMatchService? match = null, PublicSectorService? publicSector = null)
        {
            _db = db;
            _gl = gl;
            _match = match;
            _publicSector = publicSector;
        }

        public async Task<ApInvoice> CreateInvoiceAsync(Guid companyId, Guid supplierId, DateTime invoiceDate,
            string currency, decimal exchangeRate, IEnumerable<ApInvoiceLineRequest> lineRequests,
            Guid? fundCentreId = null, Guid? grantId = null, Guid? commitmentId = null, CancellationToken ct = default,
            string? supplierInvoiceNo = null, Guid? purchaseOrderId = null)
        {
            var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId && s.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Supplier not found.");

            if (purchaseOrderId.HasValue)
            {
                var purchaseOrder = await _db.PurchaseOrders.FirstOrDefaultAsync(p =>
                    p.Id == purchaseOrderId.Value && p.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("The linked purchase order was not found in this company.");
                if (purchaseOrder.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.Partial or PurchaseOrderStatus.Fulfilled))
                    throw new InvalidOperationException("An AP invoice can only be linked to an approved, partially received or fulfilled purchase order.");
                if (fundCentreId.HasValue && fundCentreId != purchaseOrder.FundCentreId)
                    throw new InvalidOperationException("The invoice fund centre does not match its purchase order.");
                if (grantId.HasValue && grantId != purchaseOrder.GrantId)
                    throw new InvalidOperationException("The invoice grant does not match its purchase order.");

                fundCentreId ??= purchaseOrder.FundCentreId;
                grantId ??= purchaseOrder.GrantId;
                var poCommitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrder.Id, ct);
                if (poCommitment != null)
                {
                    if (poCommitment.Status != CommitmentStatus.Active)
                        throw new InvalidOperationException("The purchase order's budget commitment is no longer active.");
                    if (poCommitment.FundCentreId != purchaseOrder.FundCentreId)
                        throw new InvalidOperationException("The purchase order and its budget commitment have inconsistent fund centres.");
                    if (commitmentId.HasValue && commitmentId != poCommitment.Id)
                        throw new InvalidOperationException("The supplied commitment does not belong to the linked purchase order.");
                    commitmentId = poCommitment.Id;
                }
                else if (commitmentId.HasValue)
                {
                    throw new InvalidOperationException("The supplied budget commitment does not belong to the linked purchase order.");
                }
            }
            else if (commitmentId.HasValue)
            {
                throw new InvalidOperationException("A commitment-linked AP invoice must reference its purchase order.");
            }

            if (!string.IsNullOrWhiteSpace(supplierInvoiceNo))
            {
                // Duplicate control (spec 16): same supplier + same invoice number (ignoring case/punctuation/leading zeros), or same amount and date.
                var existing = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.SupplierId == supplierId && i.Status != InvoiceStatus.Cancelled).ToListAsync(ct);
                var normalised = ApexERP.Domain.Controls.ValuationRules.NormaliseInvoiceNo(supplierInvoiceNo);
                var clash = existing.FirstOrDefault(i => ApexERP.Domain.Controls.ValuationRules.NormaliseInvoiceNo(i.SupplierInvoiceNo) == normalised);
                if (clash != null)
                    throw new InvalidOperationException($"Supplier invoice {supplierInvoiceNo} has already been captured as {clash.InvoiceNo}.");
            }

            var taxCodeIds = lineRequests.Where(l => l.TaxCodeId.HasValue).Select(l => l.TaxCodeId!.Value).Distinct().ToList();
            var taxCodes = await _db.TaxCodes.Where(t => t.CompanyId == companyId && taxCodeIds.Contains(t.Id) &&
                t.IsActive && t.EffectiveFrom <= invoiceDate && (!t.EffectiveTo.HasValue || t.EffectiveTo >= invoiceDate))
                .ToDictionaryAsync(t => t.Id, ct);
            if (taxCodeIds.Any(id => !taxCodes.ContainsKey(id)))
                throw new InvalidOperationException("One or more selected tax codes are inactive or not effective on the invoice date.");

            var lines = new List<ApInvoiceLine>();
            decimal subtotal = 0, vatTotal = 0;
            foreach (var lr in lineRequests)
            {
                var lineSubtotal = Math.Round(lr.Quantity * lr.UnitCost, 6);
                var rate = lr.TaxCodeId.HasValue && taxCodes.TryGetValue(lr.TaxCodeId.Value, out var tc) ? tc.Rate : 0m;
                var lineVat = Math.Round(lineSubtotal * rate, 6);
                subtotal += lineSubtotal;
                vatTotal += lineVat;

                lines.Add(new ApInvoiceLine
                {
                    Description = lr.Description,
                    Quantity = lr.Quantity,
                    UnitCost = lr.UnitCost,
                    TaxCodeId = lr.TaxCodeId,
                    LineSubtotal = lineSubtotal,
                    LineVat = lineVat,
                    LineTotal = lineSubtotal + lineVat,
                    GlAccountId = lr.GlAccountId,
                    InventoryItemId = lr.InventoryItemId
                });
            }

            // WHT is applied on the VAT-exclusive amount for qualifying suppliers per ZIMRA rules.
            var whtAmount = supplier.WhtApplicable ? Math.Round(subtotal * supplier.WhtRate, 6) : 0m;
            var total = subtotal + vatTotal;

            var invoiceNo = await GenerateNoAsync(companyId, "PINV", ct);
            var invoice = new ApInvoice
            {
                CompanyId = companyId,
                InvoiceNo = invoiceNo,
                SupplierId = supplierId,
                InvoiceDate = invoiceDate,
                DueDate = invoiceDate.AddDays(supplier.PaymentTermsDays),
                Currency = currency,
                ExchangeRate = exchangeRate,
                Subtotal = subtotal,
                VatAmount = vatTotal,
                WhtAmount = whtAmount,
                Total = total,
                Outstanding = total - whtAmount, // WHT withheld from supplier, reduces amount owed to them
                Status = InvoiceStatus.Draft,
                FundCentreId = fundCentreId,
                GrantId = grantId,
                CommitmentId = commitmentId,
                SupplierInvoiceNo = string.IsNullOrWhiteSpace(supplierInvoiceNo) ? null : supplierInvoiceNo.Trim(),
                PurchaseOrderId = purchaseOrderId,
                MatchStatus = purchaseOrderId.HasValue ? "Pending" : "NotRequired",
                Lines = lines
            };
            _db.ApInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);
            return invoice;
        }

        /// <summary>Posts: Dr Expense/Asset per line, Dr VAT Input, Cr Trade Creditors (net of WHT), Cr WHT Payable.
        /// If the invoice carries a FundCentreId/GrantId, every GL line is tagged with them so Public
        /// Sector budget utilisation and Grant budget-vs-actual reporting pick up the actual spend.</summary>
        public Task<ApInvoice> PostInvoiceAsync(Guid companyId, Guid invoiceId, Guid apControlAccountId,
            Guid vatInputAccountId, Guid whtPayableAccountId, CancellationToken ct = default)
        {
            return _db.InTransactionAsync(async token =>
            {
                var invoice = await _db.ApInvoices.Include(i => i.Lines)
                    .FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Invoice not found.");
                if (invoice.Status != InvoiceStatus.Draft)
                    throw new InvalidOperationException("Only draft invoices can be posted.");

                if (_match != null && invoice.PurchaseOrderId != null)
                {
                    var result = await _match.EvaluateAndStoreAsync(companyId, invoice, token);
                    if (result.Outcome == ApexERP.Domain.Controls.MatchOutcome.Discrepancy && invoice.MatchStatus != "Overridden")
                    {
                        await _db.SaveChangesAsync(token);
                        throw new InvalidOperationException("Three-way match failed: " + string.Join("; ", result.Issues.Select(i => i.Message)));
                    }
                }

                if (invoice.CommitmentId.HasValue)
                {
                    if (_publicSector == null)
                        throw new InvalidOperationException("Public-sector commitment realisation is not configured.");
                    if (!invoice.PurchaseOrderId.HasValue || !invoice.FundCentreId.HasValue)
                        throw new InvalidOperationException("A commitment-linked invoice must retain its purchase order and fund centre.");
                    var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                        c.CompanyId == companyId && c.Id == invoice.CommitmentId.Value, token)
                        ?? throw new InvalidOperationException("The linked budget commitment was not found.");
                    if (commitment.PurchaseOrderId != invoice.PurchaseOrderId.Value ||
                        commitment.FundCentreId != invoice.FundCentreId.Value)
                        throw new InvalidOperationException("The invoice purchase order and fund centre do not match its commitment.");
                }

                var glLines = new List<GlLineRequest>();
                foreach (var group in invoice.Lines.GroupBy(l => l.GlAccountId))
                    glLines.Add(new GlLineRequest(group.Key, group.Sum(l => l.LineSubtotal), 0, invoice.Currency, invoice.ExchangeRate, null,
                        "Purchase expense/asset", invoice.FundCentreId, invoice.GrantId));
                if (invoice.VatAmount > 0)
                    glLines.Add(new GlLineRequest(vatInputAccountId, invoice.VatAmount, 0, invoice.Currency, invoice.ExchangeRate, null, "VAT input"));
                if (invoice.WhtAmount > 0)
                    glLines.Add(new GlLineRequest(whtPayableAccountId, 0, invoice.WhtAmount, invoice.Currency, invoice.ExchangeRate, null, "WHT withheld, payable to ZIMRA"));
                glLines.Add(new GlLineRequest(apControlAccountId, 0, invoice.Total - invoice.WhtAmount, invoice.Currency, invoice.ExchangeRate, null, $"AP: {invoice.InvoiceNo}"));

                var journal = await _gl.PostAsync(companyId, invoice.InvoiceDate, JournalSource.AP,
                    $"AP Invoice {invoice.InvoiceNo}", invoice.InvoiceNo, glLines, token);
                invoice.JournalEntryId = journal.Id;
                invoice.Status = InvoiceStatus.Posted;
                await _db.SaveChangesAsync(token);

                if (invoice.CommitmentId.HasValue)
                    await _publicSector!.RealiseCommitmentAsync(companyId, invoice.CommitmentId.Value,
                        decimal.Round(invoice.Subtotal * invoice.ExchangeRate, 2, MidpointRounding.AwayFromZero), token);

                return invoice;
            }, ct);
        }

        public async Task<ApPayment> PayAndAllocateAsync(Guid companyId, Guid supplierId, DateTime paymentDate,
            string currency, decimal amount, PaymentMethod method, Guid bankGlAccountId, Guid apControlAccountId,
            Dictionary<Guid, decimal> allocations, CancellationToken ct = default)
        {
            if (allocations.Values.Sum() > amount)
                throw new InvalidOperationException("Total allocated amount exceeds the payment amount.");

            var paymentNo = await GenerateNoAsync(companyId, "PAY", ct);
            var payment = new ApPayment
            {
                CompanyId = companyId,
                PaymentNo = paymentNo,
                SupplierId = supplierId,
                PaymentDate = paymentDate,
                Currency = currency,
                Amount = amount,
                PaymentMethod = method
            };

            foreach (var (invoiceId, appliedAmount) in allocations)
            {
                var invoice = await _db.ApInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");
                if (appliedAmount > invoice.Outstanding)
                    throw new InvalidOperationException($"Allocation exceeds outstanding on invoice {invoice.InvoiceNo}.");

                invoice.Outstanding -= appliedAmount;
                invoice.Status = invoice.Outstanding == 0 ? InvoiceStatus.Paid : InvoiceStatus.Partial;
                payment.Allocations.Add(new ApPaymentAllocation { ApInvoiceId = invoiceId, AmountApplied = appliedAmount });
            }

            var je = await _gl.PostAsync(companyId, paymentDate, JournalSource.AP, $"Payment {paymentNo}", paymentNo,
                new[]
                {
                    new GlLineRequest(apControlAccountId, amount, 0, currency, 1m, null, "Trade creditors settled"),
                    new GlLineRequest(bankGlAccountId, 0, amount, currency, 1m, null, "Cash/bank paid out")
                }, ct);

            payment.JournalEntryId = je.Id;
            _db.ApPayments.Add(payment);
            await _db.SaveChangesAsync(ct);
            return payment;
        }

        /// <summary>AP ageing analysis: buckets outstanding invoice balances into current/30/60/90+ days.</summary>
        public async Task<List<(Guid SupplierId, string SupplierName, decimal Current, decimal Days30, decimal Days60, decimal Days90Plus, decimal Total)>>
            GetAgeingAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
        {
            var openInvoices = await _db.ApInvoices
                .Include(i => i.Supplier)
                .Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled)
                .ToListAsync(ct);

            return openInvoices
                .GroupBy(i => i.SupplierId)
                .Select(g =>
                {
                    var name = g.First().Supplier?.Name ?? "Unknown";
                    decimal current = 0, d30 = 0, d60 = 0, d90 = 0;
                    foreach (var inv in g)
                    {
                        var daysOverdue = (asOfDate - inv.DueDate).Days;
                        if (daysOverdue <= 0) current += inv.Outstanding;
                        else if (daysOverdue <= 30) d30 += inv.Outstanding;
                        else if (daysOverdue <= 60) d60 += inv.Outstanding;
                        else d90 += inv.Outstanding;
                    }
                    return (g.Key, name, current, d30, d60, d90, current + d30 + d60 + d90);
                }).ToList();
        }

        private async Task<string> GenerateNoAsync(Guid companyId, string prefix, CancellationToken ct)
        {
            var yearPrefix = $"{prefix}-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:AP", ct);
            var count = await _db.ApInvoices.IgnoreQueryFilters().CountAsync(i => i.CompanyId == companyId, ct)
                        + await _db.ApPayments.IgnoreQueryFilters().CountAsync(p => p.CompanyId == companyId, ct);
            return $"{yearPrefix}{(count + 1):D5}";
        }
    }
}
