using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record ArInvoiceLineRequest(string Description, decimal Quantity, decimal UnitPrice, Guid? TaxCodeId, Guid GlAccountId);

    /// <summary>PRD Module 3 (Accounts Receivable): invoicing, VAT calculation on standard/zero/exempt
    /// codes, receipting with multi-invoice allocation, ageing, and GL auto-posting.</summary>
    public class ArService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        public ArService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        public async Task<ArInvoice> CreateInvoiceAsync(Guid companyId, Guid customerId, DateTime invoiceDate,
            string currency, decimal exchangeRate, IEnumerable<ArInvoiceLineRequest> lineRequests,
            Guid vatControlAccountId, Guid? grantId = null, CancellationToken ct = default)
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId && c.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Customer not found.");

            var taxCodeIds = lineRequests.Where(l => l.TaxCodeId.HasValue).Select(l => l.TaxCodeId!.Value).Distinct().ToList();
            var taxCodes = await _db.TaxCodes.Where(t => taxCodeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

            var lines = new List<ArInvoiceLine>();
            decimal subtotal = 0, vatTotal = 0;
            foreach (var lr in lineRequests)
            {
                var lineSubtotal = Math.Round(lr.Quantity * lr.UnitPrice, 6);
                var rate = lr.TaxCodeId.HasValue && taxCodes.TryGetValue(lr.TaxCodeId.Value, out var tc) ? tc.Rate : 0m;
                var lineVat = Math.Round(lineSubtotal * rate, 6);
                subtotal += lineSubtotal;
                vatTotal += lineVat;

                lines.Add(new ArInvoiceLine
                {
                    Description = lr.Description,
                    Quantity = lr.Quantity,
                    UnitPrice = lr.UnitPrice,
                    TaxCodeId = lr.TaxCodeId,
                    LineSubtotal = lineSubtotal,
                    LineVat = lineVat,
                    LineTotal = lineSubtotal + lineVat,
                    GlAccountId = lr.GlAccountId
                });
            }

            var invoiceNo = await GenerateNoAsync(companyId, "INV", ct);
            var invoice = new ArInvoice
            {
                CompanyId = companyId,
                InvoiceNo = invoiceNo,
                CustomerId = customerId,
                InvoiceDate = invoiceDate,
                DueDate = invoiceDate.AddDays(customer.PaymentTermsDays),
                Currency = currency,
                ExchangeRate = exchangeRate,
                Subtotal = subtotal,
                VatAmount = vatTotal,
                Total = subtotal + vatTotal,
                Outstanding = subtotal + vatTotal,
                Status = InvoiceStatus.Draft,
                GrantId = grantId,
                Lines = lines
            };
            _db.ArInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);
            return invoice;
        }

        /// <summary>Posts a draft invoice: Dr Trade Debtors (AR control), Cr Income per line, Cr VAT Output.
        /// GL lines are tagged with the invoice's GrantId (if any) so grant budget-vs-actual and
        /// donor reporting reflect actual revenue/reimbursement against that grant.</summary>
        public async Task<ArInvoice> PostInvoiceAsync(Guid companyId, Guid invoiceId, Guid arControlAccountId,
            Guid vatOutputAccountId, CancellationToken ct = default)
        {
            var invoice = await _db.ArInvoices.Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Invoice not found.");
            if (invoice.Status != InvoiceStatus.Draft)
                throw new InvalidOperationException("Only draft invoices can be posted.");

            var glLines = new List<GlLineRequest>
            {
                new(arControlAccountId, invoice.Total, 0, invoice.Currency, invoice.ExchangeRate, null, $"AR: {invoice.InvoiceNo}")
            };
            foreach (var group in invoice.Lines.GroupBy(l => l.GlAccountId))
                glLines.Add(new GlLineRequest(group.Key, 0, group.Sum(l => l.LineSubtotal), invoice.Currency, invoice.ExchangeRate, null,
                    "Sales income", null, invoice.GrantId));
            if (invoice.VatAmount > 0)
                glLines.Add(new GlLineRequest(vatOutputAccountId, 0, invoice.VatAmount, invoice.Currency, invoice.ExchangeRate, null, "VAT output"));

            var je = await _gl.PostAsync(companyId, invoice.InvoiceDate, JournalSource.AR, $"AR Invoice {invoice.InvoiceNo}", invoice.InvoiceNo, glLines, ct);
            invoice.JournalEntryId = je.Id;
            invoice.Status = InvoiceStatus.Posted;
            await _db.SaveChangesAsync(ct);
            return invoice;
        }

        /// <summary>Records a customer receipt and allocates it against one or more open invoices,
        /// updating each invoice's outstanding balance and status. Posts Dr Bank/Cash, Cr Trade Debtors.</summary>
        public async Task<ArReceipt> ReceiveAndAllocateAsync(Guid companyId, Guid customerId, DateTime receiptDate,
            string currency, decimal amount, PaymentMethod method, string? reference, Guid bankGlAccountId,
            Guid arControlAccountId, Dictionary<Guid, decimal> allocations, CancellationToken ct = default)
        {
            if (allocations.Values.Sum() > amount)
                throw new InvalidOperationException("Total allocated amount exceeds the receipt amount.");

            var receiptNo = await GenerateNoAsync(companyId, "RCT", ct);
            var receipt = new ArReceipt
            {
                CompanyId = companyId,
                ReceiptNo = receiptNo,
                CustomerId = customerId,
                ReceiptDate = receiptDate,
                Currency = currency,
                Amount = amount,
                PaymentMethod = method,
                Reference = reference
            };

            foreach (var (invoiceId, appliedAmount) in allocations)
            {
                var invoice = await _db.ArInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");
                if (appliedAmount > invoice.Outstanding)
                    throw new InvalidOperationException($"Allocation of {appliedAmount} exceeds outstanding {invoice.Outstanding} on invoice {invoice.InvoiceNo}.");

                invoice.Outstanding -= appliedAmount;
                invoice.Status = invoice.Outstanding == 0 ? InvoiceStatus.Paid : InvoiceStatus.Partial;
                receipt.Allocations.Add(new ArReceiptAllocation { ArInvoiceId = invoiceId, AmountApplied = appliedAmount });
            }

            var je = await _gl.PostAsync(companyId, receiptDate, JournalSource.AR, $"Receipt {receiptNo}", receiptNo,
                new[]
                {
                    new GlLineRequest(bankGlAccountId, amount, 0, currency, 1m, null, "Cash/bank received"),
                    new GlLineRequest(arControlAccountId, 0, amount, currency, 1m, null, "Trade debtors settled")
                }, ct);

            receipt.JournalEntryId = je.Id;
            _db.ArReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);
            return receipt;
        }

        /// <summary>AR ageing analysis: buckets outstanding invoice balances into current/30/60/90+ days.</summary>
        public async Task<List<(Guid CustomerId, string CustomerName, decimal Current, decimal Days30, decimal Days60, decimal Days90Plus, decimal Total)>>
            GetAgeingAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
        {
            var openInvoices = await _db.ArInvoices
                .Include(i => i.Customer)
                .Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled)
                .ToListAsync(ct);

            return openInvoices
                .GroupBy(i => i.CustomerId)
                .Select(g =>
                {
                    var name = g.First().Customer?.Name ?? "Unknown";
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
            var count = prefix == "INV"
                ? await _db.ArInvoices.IgnoreQueryFilters().CountAsync(i => i.CompanyId == companyId && i.InvoiceNo.StartsWith(yearPrefix), ct)
                : await _db.ArReceipts.IgnoreQueryFilters().CountAsync(r => r.CompanyId == companyId && r.ReceiptNo.StartsWith(yearPrefix), ct);
            return $"{yearPrefix}{(count + 1):D5}";
        }
    }
}
