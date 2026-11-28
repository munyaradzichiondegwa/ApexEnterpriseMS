using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record SettlementComponentInput(Guid InvoiceId, Guid? InvoiceLineId, Guid? TaxComponentId, decimal AmountApplied);

    public record ArInvoiceLineRequest(string Description, decimal Quantity, decimal UnitPrice, Guid? TaxCodeId, Guid GlAccountId, Guid? CostCentreId = null);

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
            Guid vatControlAccountId, Guid? grantId = null, CancellationToken ct = default, Guid? fundCentreId = null)
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId && c.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Customer not found.");

            var taxCodeIds = lineRequests.Where(l => l.TaxCodeId.HasValue).Select(l => l.TaxCodeId!.Value).Distinct().ToList();
            var taxCodes = await _db.TaxCodes.Where(t => t.CompanyId == companyId && taxCodeIds.Contains(t.Id) &&
                t.IsActive && t.EffectiveFrom <= invoiceDate && (!t.EffectiveTo.HasValue || t.EffectiveTo >= invoiceDate))
                .ToDictionaryAsync(t => t.Id, ct);
            if (taxCodeIds.Any(id => !taxCodes.ContainsKey(id)))
                throw new InvalidOperationException("One or more selected tax codes are inactive or not effective on the invoice date.");

            var lines = new List<ArInvoiceLine>();
            decimal subtotal = 0, vatTotal = 0;
            foreach (var lr in lineRequests)
            {
                if (lr.CostCentreId.HasValue && !await _db.CostCentres.AnyAsync(x =>
                    x.Id == lr.CostCentreId.Value && x.CompanyId == companyId && x.IsActive, ct))
                    throw new InvalidOperationException("Invoice line cost centre is not active in this company.");
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
                    GlAccountId = lr.GlAccountId,
                    CostCentreId = lr.CostCentreId
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
                FundCentreId = fundCentreId,
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

            if (invoice.VatAmount > 0m)
            {
                var taxableLines = invoice.Lines.Where(x => x.LineVat > 0m).ToList();
                if (taxableLines.Count == 0 || taxableLines.Any(x => !x.TaxCodeId.HasValue))
                    throw new InvalidOperationException("Every VAT-bearing AR invoice line must identify its tax code.");
                var taxCodeIds = taxableLines.Select(x => x.TaxCodeId!.Value).Distinct().ToArray();
                var taxCodes = await _db.TaxCodes.Where(x => x.CompanyId == companyId && taxCodeIds.Contains(x.Id)).ToListAsync(ct);
                if (taxCodes.Count != taxCodeIds.Length || taxCodes.Any(x => !x.IsActive || x.Type != TaxType.VAT ||
                    x.EffectiveFrom.Date > invoice.InvoiceDate.Date || (x.EffectiveTo.HasValue && x.EffectiveTo.Value.Date < invoice.InvoiceDate.Date)))
                    throw new InvalidOperationException("AR invoice VAT requires active, company-owned VAT tax codes effective on the invoice date.");
                foreach (var line in taxableLines)
                {
                    var taxCode = taxCodes.Single(x => x.Id == line.TaxCodeId!.Value);
                    var expectedVat = decimal.Round(line.LineSubtotal * taxCode.Rate, 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(expectedVat - line.LineVat) > 0.01m)
                        throw new InvalidOperationException("An AR invoice line VAT amount does not agree with its effective tax-code rate.");
                }
                if (taxableLines.Sum(x => x.LineVat) != invoice.VatAmount ||
                    invoice.Lines.Sum(x => x.LineSubtotal) != invoice.Subtotal)
                    throw new InvalidOperationException("AR invoice line tax and subtotal amounts must reconcile exactly to the invoice header.");
                var mappings = await _db.TaxAccountMappings.Where(x =>
                    x.CompanyId == companyId && x.Purpose == "AR-VAT-OUTPUT" &&
                    x.TaxCodeId.HasValue && taxCodeIds.Contains(x.TaxCodeId.Value) &&
                    x.Status == TaxConfigurationStatus.Approved && x.EffectiveFrom <= invoice.InvoiceDate &&
                    (!x.EffectiveTo.HasValue || x.EffectiveTo.Value > invoice.InvoiceDate))
                    .ToListAsync(ct);
                if (mappings.Count != taxCodeIds.Length ||
                    mappings.Select(x => x.TaxCodeId).Distinct().Count() != taxCodeIds.Length ||
                    mappings.Any(x => x.GlAccountId != vatOutputAccountId))
                    throw new InvalidOperationException("Each AR VAT tax code requires one approved, effective AR-VAT-OUTPUT account mapping matching the requested control account.");
                var taxAccount = await _db.GlAccounts.SingleOrDefaultAsync(x =>
                    x.Id == vatOutputAccountId && x.CompanyId == companyId && x.IsActive && x.IsPostable, ct);
                if (taxAccount == null || taxAccount.Type != GlAccountType.Liability)
                    throw new InvalidOperationException("VAT output must post to an active, postable liability account belonging to this company.");
                invoice.VatOutputAccountId = taxAccount.Id;
            }

            var glLines = new List<GlLineRequest>
            {
                new(arControlAccountId, invoice.Total, 0, invoice.Currency, invoice.ExchangeRate, null, $"AR: {invoice.InvoiceNo}")
            };
            foreach (var group in invoice.Lines.GroupBy(l => new { l.GlAccountId, l.CostCentreId }))
                glLines.Add(new GlLineRequest(group.Key.GlAccountId, 0, group.Sum(l => l.LineSubtotal), invoice.Currency,
                    invoice.ExchangeRate, group.Key.CostCentreId, "Sales income", invoice.FundCentreId, invoice.GrantId));
            if (invoice.VatAmount > 0)
            {
                var taxCodes = invoice.Lines.Where(x => x.LineVat > 0m).Select(x => x.TaxCodeId!.Value).Distinct().ToArray();
                foreach (var taxCodeId in taxCodes)
                {
                    var taxAmount = invoice.Lines.Where(x => x.TaxCodeId == taxCodeId).Sum(x => x.LineVat);
                    glLines.Add(new GlLineRequest(vatOutputAccountId, 0, taxAmount, invoice.Currency,
                        invoice.ExchangeRate, null, "VAT output", invoice.FundCentreId, invoice.GrantId, taxCodeId));
                }
            }

            var je = await _gl.PostAsync(companyId, invoice.InvoiceDate, JournalSource.AR, $"AR Invoice {invoice.InvoiceNo}", invoice.InvoiceNo, glLines, ct);
            invoice.JournalEntryId = je.Id;
            if (invoice.VatAmount > 0m)
            {
                var taxCodeIdsForComponents = invoice.Lines.Where(x => x.LineVat > 0m).Select(x => x.TaxCodeId!.Value).Distinct().ToArray();
                foreach (var taxCodeId in taxCodeIdsForComponents)
                {
                    var mapping = await _db.TaxAccountMappings.Where(x =>
                        x.CompanyId == companyId && x.Purpose == "AR-VAT-OUTPUT" && x.TaxCodeId == taxCodeId &&
                        x.Status == TaxConfigurationStatus.Approved && x.EffectiveFrom <= invoice.InvoiceDate &&
                        (!x.EffectiveTo.HasValue || x.EffectiveTo.Value > invoice.InvoiceDate))
                        .SingleAsync(ct);
                    var journalLine = je.Lines.Single(x => x.TaxCodeId == taxCodeId &&
                        x.GlAccountId == mapping.GlAccountId && x.Credit ==
                        invoice.Lines.Where(l => l.TaxCodeId == taxCodeId).Sum(l => l.LineVat));
                    foreach (var invoiceLine in invoice.Lines.Where(x => x.TaxCodeId == taxCodeId && x.LineVat > 0m))
                    {
                        _db.InvoiceTaxComponents.Add(new InvoiceTaxComponent
                        {
                            CompanyId = companyId, InvoiceKind = "AR", InvoiceId = invoice.Id,
                            InvoiceLineId = invoiceLine.Id, TaxCodeId = taxCodeId, TaxType = TaxType.VAT,
                            TaxableBase = invoiceLine.LineSubtotal,
                            Rate = invoiceLine.LineSubtotal == 0m ? 0m : invoiceLine.LineVat / invoiceLine.LineSubtotal,
                            Amount = invoiceLine.LineVat, GlAccountId = mapping.GlAccountId,
                            TaxAccountMappingId = mapping.Id, MappingVersionNumber = mapping.VersionNumber,
                            JournalEntryId = je.Id, JournalLineId = journalLine.Id
                        });
                    }
                }
            }
            invoice.Status = InvoiceStatus.Posted;
            await _db.SaveChangesAsync(ct);
            return invoice;
        }

        /// <summary>Records a customer receipt and allocates it against one or more open invoices,
        /// updating each invoice's outstanding balance and status. Posts Dr Bank/Cash, Cr Trade Debtors.</summary>
        public Task<ArReceipt> ReceiveAndAllocateAsync(Guid companyId, Guid customerId, DateTime receiptDate,
            string currency, decimal amount, PaymentMethod method, string? reference, Guid bankGlAccountId,
            Guid arControlAccountId, Dictionary<Guid, decimal> allocations, CancellationToken ct = default,
            IReadOnlyCollection<SettlementComponentInput>? componentAllocations = null)
        {
            return _db.InTransactionAsync(token => ReceiveAndAllocateCoreAsync(
                    companyId, customerId, receiptDate, currency, amount, method, reference, bankGlAccountId,
                    arControlAccountId, allocations, token, componentAllocations), ct);
        }

        private async Task<ArReceipt> ReceiveAndAllocateCoreAsync(Guid companyId, Guid customerId, DateTime receiptDate,
            string currency, decimal amount, PaymentMethod method, string? reference, Guid bankGlAccountId,
            Guid arControlAccountId, Dictionary<Guid, decimal> allocations, CancellationToken ct,
            IReadOnlyCollection<SettlementComponentInput>? componentAllocations = null)
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
                var parentAllocation = new ArReceiptAllocation { ArInvoiceId = invoiceId, AmountApplied = appliedAmount };
                receipt.Allocations.Add(parentAllocation);
                await AddSettlementComponentsAsync(companyId, invoice, parentAllocation.Id, appliedAmount, componentAllocations, ct);
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


        private async Task AddSettlementComponentsAsync(Guid companyId, ArInvoice invoice, Guid allocationId,
            decimal appliedAmount, IReadOnlyCollection<SettlementComponentInput>? requested, CancellationToken ct)
        {
            var invoiceLines = await _db.ArInvoiceLines.Where(x => x.ArInvoiceId == invoice.Id).ToListAsync(ct);
            var taxComponents = await _db.InvoiceTaxComponents.Where(x => x.CompanyId == companyId &&
                x.InvoiceKind == "AR" && x.InvoiceId == invoice.Id).ToListAsync(ct);
            var details = requested?.Where(x => x.InvoiceId == invoice.Id).ToList();
            if (details == null || details.Count == 0)
            {
                if (invoice.VatAmount != 0m)
                    throw new InvalidOperationException("VAT-bearing AR receipts require explicit settlement allocations to invoice lines and their tax components.");
                if (appliedAmount < invoice.Total && invoiceLines.Select(x => (x.GlAccountId, x.CostCentreId)).Distinct().Count() > 1)
                    throw new InvalidOperationException("A partial AR settlement across multiple dimensions requires explicit settlement component allocations.");
                if (invoiceLines.Count == 0 || invoiceLines.Sum(x => x.LineSubtotal) != invoice.Total)
                    throw new InvalidOperationException("Tax-free AR invoice lines must reconcile to the invoice total before automatic settlement allocation.");
                details = invoiceLines.Select(x => new SettlementComponentInput(invoice.Id, x.Id, null,
                    appliedAmount == invoice.Total ? x.LineSubtotal : appliedAmount)).ToList();
            }
            if (details.Any(x => x.AmountApplied <= 0m || x.InvoiceLineId.HasValue == x.TaxComponentId.HasValue) ||
                details.Sum(x => x.AmountApplied) != appliedAmount ||
                details.Select(x => (x.InvoiceLineId, x.TaxComponentId)).Distinct().Count() != details.Count)
                throw new InvalidOperationException("AR settlement component allocations must be positive, unique and sum exactly to the invoice settlement amount.");

            foreach (var detail in details)
            {
                if (detail.InvoiceLineId.HasValue)
                {
                    var sourceLine = invoiceLines.SingleOrDefault(x => x.Id == detail.InvoiceLineId.Value)
                        ?? throw new InvalidOperationException("An AR settlement line must belong to the allocated invoice.");
                    var prior = await _db.SettlementComponentAllocations.Where(x => x.CompanyId == companyId &&
                        x.SettlementKind == "AR" && x.InvoiceLineId == sourceLine.Id).SumAsync(x => (decimal?)x.AmountApplied, ct) ?? 0m;
                    if (prior + details.Where(x => x.InvoiceLineId == sourceLine.Id).Sum(x => x.AmountApplied) > sourceLine.LineSubtotal)
                        throw new InvalidOperationException("AR settlements cannot allocate more than the invoice-line revenue amount.");
                }
                else
                {
                    var tax = taxComponents.SingleOrDefault(x => x.Id == detail.TaxComponentId.Value)
                        ?? throw new InvalidOperationException("An AR settlement tax component must belong to the allocated invoice.");
                    if (tax.TaxType != TaxType.VAT)
                        throw new InvalidOperationException("Only VAT tax components can be settled through an AR customer receipt.");
                    var prior = await _db.SettlementComponentAllocations.Where(x => x.CompanyId == companyId &&
                        x.SettlementKind == "AR" && x.TaxComponentId == tax.Id).SumAsync(x => (decimal?)x.AmountApplied, ct) ?? 0m;
                    if (prior + details.Where(x => x.TaxComponentId == tax.Id).Sum(x => x.AmountApplied) > tax.Amount)
                        throw new InvalidOperationException("AR settlements cannot allocate more than the posted VAT component amount.");
                }
                _db.SettlementComponentAllocations.Add(new SettlementComponentAllocation
                {
                    CompanyId = companyId, SettlementKind = "AR", SettlementAllocationId = allocationId,
                    InvoiceId = invoice.Id,
                    ComponentKind = detail.InvoiceLineId.HasValue ? SettlementComponentKind.InvoiceLine : SettlementComponentKind.TaxComponent,
                    InvoiceLineId = detail.InvoiceLineId, TaxComponentId = detail.TaxComponentId,
                    AmountApplied = detail.AmountApplied
                });
            }
        }

        private async Task<string> GenerateNoAsync(Guid companyId, string prefix, CancellationToken ct)
        {
            var yearPrefix = $"{prefix}-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:{yearPrefix}", ct);
            var count = prefix == "INV"
                ? await _db.ArInvoices.IgnoreQueryFilters().CountAsync(i => i.CompanyId == companyId && i.InvoiceNo.StartsWith(yearPrefix), ct)
                : await _db.ArReceipts.IgnoreQueryFilters().CountAsync(r => r.CompanyId == companyId && r.ReceiptNo.StartsWith(yearPrefix), ct);
            return $"{yearPrefix}{(count + 1):D5}";
        }
    }
}
