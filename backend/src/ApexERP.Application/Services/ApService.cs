using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record ApInvoiceLineRequest(string Description, decimal Quantity, decimal UnitCost, Guid? TaxCodeId, Guid GlAccountId, Guid? InventoryItemId = null, Guid? CostCentreId = null);

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
                else
                {
                    if (commitmentId.HasValue)
                        throw new InvalidOperationException("The supplied budget commitment does not belong to the linked purchase order.");
                    if (purchaseOrder.FundCentreId.HasValue)
                        throw new InvalidOperationException("A public-sector purchase order must have an active budget commitment before its invoice can be captured.");
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
                if (lr.CostCentreId.HasValue && !await _db.CostCentres.AnyAsync(x =>
                    x.Id == lr.CostCentreId.Value && x.CompanyId == companyId && x.IsActive, ct))
                    throw new InvalidOperationException("Invoice line cost centre is not active in this company.");
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
                    InventoryItemId = lr.InventoryItemId,
                    CostCentreId = lr.CostCentreId
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

                if (invoice.VatAmount > 0m)
                {
                    var taxableLines = invoice.Lines.Where(x => x.LineVat > 0m).ToList();
                    if (taxableLines.Count == 0 || taxableLines.Any(x => !x.TaxCodeId.HasValue))
                        throw new InvalidOperationException("Every VAT-bearing AP invoice line must identify its tax code.");
                    var taxCodeIds = taxableLines.Select(x => x.TaxCodeId!.Value).Distinct().ToArray();
                    var taxCodes = await _db.TaxCodes.Where(x => x.CompanyId == companyId && taxCodeIds.Contains(x.Id)).ToListAsync(token);
                    if (taxCodes.Count != taxCodeIds.Length || taxCodes.Any(x => !x.IsActive || x.Type != TaxType.VAT ||
                        x.EffectiveFrom.Date > invoice.InvoiceDate.Date || (x.EffectiveTo.HasValue && x.EffectiveTo.Value.Date < invoice.InvoiceDate.Date)))
                        throw new InvalidOperationException("AP invoice VAT requires active, company-owned VAT tax codes effective on the invoice date.");
                    foreach (var line in taxableLines)
                    {
                        var taxCode = taxCodes.Single(x => x.Id == line.TaxCodeId!.Value);
                        var expectedVat = decimal.Round(line.LineSubtotal * taxCode.Rate, 2, MidpointRounding.AwayFromZero);
                        if (Math.Abs(expectedVat - line.LineVat) > 0.01m)
                            throw new InvalidOperationException("An AP invoice line VAT amount does not agree with its effective tax-code rate.");
                    }
                    if (taxableLines.Sum(x => x.LineVat) != invoice.VatAmount ||
                        invoice.Lines.Sum(x => x.LineSubtotal) != invoice.Subtotal)
                        throw new InvalidOperationException("AP invoice line tax and subtotal amounts must reconcile exactly to the invoice header.");
                    var mappingCandidates = await _db.TaxAccountMappings.Where(x =>
                        x.CompanyId == companyId && x.Purpose == "AP-VAT-INPUT" &&
                        x.TaxCodeId.HasValue && taxCodeIds.Contains(x.TaxCodeId.Value) &&
                        x.Status == TaxConfigurationStatus.Approved && x.EffectiveFrom <= invoice.InvoiceDate &&
                        (!x.EffectiveTo.HasValue || x.EffectiveTo.Value > invoice.InvoiceDate))
                        .ToListAsync(token);
                    var mappings = mappingCandidates.GroupBy(x => x.TaxCodeId)
                        .Select(g => g.OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.VersionNumber).First()).ToList();
                    if (mappings.Count != taxCodeIds.Length ||
                        mappings.Select(x => x.TaxCodeId).Distinct().Count() != taxCodeIds.Length ||
                        mappings.Any(x => x.GlAccountId != vatInputAccountId))
                        throw new InvalidOperationException("Each AP VAT tax code requires one approved, effective AP-VAT-INPUT account mapping matching the requested control account.");
                    var taxAccount = await _db.GlAccounts.SingleOrDefaultAsync(x =>
                        x.Id == vatInputAccountId && x.CompanyId == companyId && x.IsActive && x.IsPostable, token);
                    if (taxAccount == null || taxAccount.Type != GlAccountType.Asset)
                        throw new InvalidOperationException("VAT input must post to an active, postable asset account belonging to this company.");
                    invoice.VatInputAccountId = taxAccount.Id;
                }
                if (invoice.WhtAmount > 0m)
                {
                    var mapping = await _db.TaxAccountMappings.Where(x =>
                        x.CompanyId == companyId && x.Purpose == "AP-WHT-PAYABLE" && x.TaxType == TaxType.WHT &&
                        !x.TaxCodeId.HasValue && x.Status == TaxConfigurationStatus.Approved &&
                        x.EffectiveFrom <= invoice.InvoiceDate && (!x.EffectiveTo.HasValue || x.EffectiveTo.Value > invoice.InvoiceDate))
                        .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.VersionNumber).FirstOrDefaultAsync(token);
                    if (mapping == null || mapping.GlAccountId != whtPayableAccountId)
                        throw new InvalidOperationException("AP withholding requires one approved, effective AP-WHT-PAYABLE mapping matching the requested control account.");
                    var taxAccount = await _db.GlAccounts.SingleOrDefaultAsync(x =>
                        x.Id == whtPayableAccountId && x.CompanyId == companyId && x.IsActive && x.IsPostable, token);
                    if (taxAccount == null || taxAccount.Type != GlAccountType.Liability)
                        throw new InvalidOperationException("WHT payable must post to an active, postable liability account belonging to this company.");
                    invoice.WhtPayableAccountId = taxAccount.Id;
                }

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
                foreach (var group in invoice.Lines.GroupBy(l => new { l.GlAccountId, l.CostCentreId }))
                    glLines.Add(new GlLineRequest(group.Key.GlAccountId, group.Sum(l => l.LineSubtotal), 0, invoice.Currency,
                        invoice.ExchangeRate, group.Key.CostCentreId, "Purchase expense/asset", invoice.FundCentreId, invoice.GrantId));
                if (invoice.VatAmount > 0)
                {
                    foreach (var taxCodeId in invoice.Lines.Where(x => x.LineVat > 0m).Select(x => x.TaxCodeId!.Value).Distinct())
                    {
                        var taxAmount = invoice.Lines.Where(x => x.TaxCodeId == taxCodeId).Sum(x => x.LineVat);
                        glLines.Add(new GlLineRequest(vatInputAccountId, taxAmount, 0, invoice.Currency,
                            invoice.ExchangeRate, null, "VAT input", invoice.FundCentreId, invoice.GrantId, taxCodeId));
                    }
                }
                if (invoice.WhtAmount > 0)
                    glLines.Add(new GlLineRequest(whtPayableAccountId, 0, invoice.WhtAmount, invoice.Currency,
                        invoice.ExchangeRate, null, "WHT withheld, payable to ZIMRA", invoice.FundCentreId, invoice.GrantId));
                glLines.Add(new GlLineRequest(apControlAccountId, 0, invoice.Total - invoice.WhtAmount, invoice.Currency, invoice.ExchangeRate, null, $"AP: {invoice.InvoiceNo}"));

                var journal = await _gl.PostAsync(companyId, invoice.InvoiceDate, JournalSource.AP,
                    $"AP Invoice {invoice.InvoiceNo}", invoice.InvoiceNo, glLines, token);
                invoice.JournalEntryId = journal.Id;
                if (invoice.VatAmount > 0m)
                {
                    foreach (var taxCodeId in invoice.Lines.Where(x => x.LineVat > 0m).Select(x => x.TaxCodeId!.Value).Distinct())
                    {
                        var mapping = await _db.TaxAccountMappings.Where(x =>
                            x.CompanyId == companyId && x.Purpose == "AP-VAT-INPUT" && x.TaxCodeId == taxCodeId &&
                            x.Status == TaxConfigurationStatus.Approved && x.EffectiveFrom <= invoice.InvoiceDate &&
                            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value > invoice.InvoiceDate))
                            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.VersionNumber).FirstAsync(token);
                        var journalLine = journal.Lines.Single(x => x.TaxCodeId == taxCodeId &&
                            x.GlAccountId == mapping.GlAccountId && x.Debit ==
                            invoice.Lines.Where(l => l.TaxCodeId == taxCodeId).Sum(l => l.LineVat));
                        foreach (var invoiceLine in invoice.Lines.Where(x => x.TaxCodeId == taxCodeId && x.LineVat > 0m))
                        {
                            _db.InvoiceTaxComponents.Add(new InvoiceTaxComponent
                            {
                                CompanyId = companyId, InvoiceKind = "AP", InvoiceId = invoice.Id,
                                InvoiceLineId = invoiceLine.Id, TaxCodeId = taxCodeId, TaxType = TaxType.VAT,
                                TaxableBase = invoiceLine.LineSubtotal,
                                Rate = await _db.TaxCodes.Where(x => x.Id == taxCodeId && x.CompanyId == companyId).Select(x => x.Rate).SingleAsync(token),
                                Amount = invoiceLine.LineVat, GlAccountId = mapping.GlAccountId,
                                TaxAccountMappingId = mapping.Id, MappingVersionNumber = mapping.VersionNumber,
                                JournalEntryId = journal.Id, JournalLineId = journalLine.Id
                            });
                        }
                    }
                }
                if (invoice.WhtAmount > 0m)
                {
                    var mapping = await _db.TaxAccountMappings.Where(x =>
                        x.CompanyId == companyId && x.Purpose == "AP-WHT-PAYABLE" && x.TaxType == TaxType.WHT &&
                        !x.TaxCodeId.HasValue && x.Status == TaxConfigurationStatus.Approved &&
                        x.EffectiveFrom <= invoice.InvoiceDate && (!x.EffectiveTo.HasValue || x.EffectiveTo.Value > invoice.InvoiceDate))
                        .SingleAsync(token);
                    var journalLine = journal.Lines.Single(x => x.GlAccountId == mapping.GlAccountId &&
                        x.Credit == invoice.WhtAmount && x.TaxCodeId == null);
                    var rate = await _db.Suppliers.Where(x => x.Id == invoice.SupplierId && x.CompanyId == companyId).Select(x => x.WhtRate).SingleAsync(token);
                    _db.InvoiceTaxComponents.Add(new InvoiceTaxComponent
                    {
                        CompanyId = companyId, InvoiceKind = "AP", InvoiceId = invoice.Id,
                        TaxType = TaxType.WHT, TaxableBase = invoice.Subtotal, Rate = rate,
                        Amount = invoice.WhtAmount, GlAccountId = mapping.GlAccountId,
                        TaxAccountMappingId = mapping.Id, MappingVersionNumber = mapping.VersionNumber,
                        JournalEntryId = journal.Id, JournalLineId = journalLine.Id
                    });
                }
                invoice.Status = InvoiceStatus.Posted;
                await _db.SaveChangesAsync(token);

                if (invoice.CommitmentId.HasValue)
                    await _publicSector!.RealiseCommitmentAsync(companyId, invoice.CommitmentId.Value,
                        decimal.Round(invoice.Subtotal * invoice.ExchangeRate, 2, MidpointRounding.AwayFromZero), token);

                return invoice;
            }, ct);
        }

        public Task<ApPayment> PayAndAllocateAsync(Guid companyId, Guid supplierId, DateTime paymentDate,
            string currency, decimal amount, PaymentMethod method, Guid bankGlAccountId, Guid apControlAccountId,
            Dictionary<Guid, decimal> allocations, CancellationToken ct = default,
            IReadOnlyCollection<SettlementComponentInput>? componentAllocations = null)
        {
            return _db.InTransactionAsync(token => PayAndAllocateCoreAsync(
                    companyId, supplierId, paymentDate, currency, amount, method, bankGlAccountId,
                    apControlAccountId, allocations, token, componentAllocations), ct);
        }

        private async Task<ApPayment> PayAndAllocateCoreAsync(Guid companyId, Guid supplierId, DateTime paymentDate,
            string currency, decimal amount, PaymentMethod method, Guid bankGlAccountId, Guid apControlAccountId,
            Dictionary<Guid, decimal> allocations, CancellationToken ct,
            IReadOnlyCollection<SettlementComponentInput>? componentAllocations = null)
        {
            if (allocations.Values.Sum() > amount)
                throw new InvalidOperationException("Total allocated amount exceeds the payment amount.");

            // Lock every affected invoice in deterministic order so concurrent partial payments
            // cannot consume the same remaining line/tax-component balance.
            foreach (var invoiceId in allocations.Keys.OrderBy(x => x))
                await _db.TryAcquireTransactionLockAsync($"ap-settlement:{companyId:N}:{invoiceId:N}", ct);

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
                var parentAllocation = new ApPaymentAllocation { ApInvoiceId = invoiceId, AmountApplied = appliedAmount };
                payment.Allocations.Add(parentAllocation);
                await AddSettlementComponentsAsync(companyId, invoice, parentAllocation.Id, appliedAmount, componentAllocations, ct);
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


        private async Task AddSettlementComponentsAsync(Guid companyId, ApInvoice invoice, Guid allocationId,
            decimal appliedAmount, IReadOnlyCollection<SettlementComponentInput>? requested, CancellationToken ct)
        {
            var invoiceLines = await _db.ApInvoiceLines.Where(x => x.ApInvoiceId == invoice.Id).ToListAsync(ct);
            var taxComponents = await _db.InvoiceTaxComponents.Where(x => x.CompanyId == companyId &&
                x.InvoiceKind == "AP" && x.InvoiceId == invoice.Id).ToListAsync(ct);
            var details = requested?.Where(x => x.InvoiceId == invoice.Id).ToList();
            if (details == null || details.Count == 0)
            {
                if (invoice.VatAmount != 0m || invoice.WhtAmount != 0m)
                    throw new InvalidOperationException("VAT/WHT-bearing AP payments require explicit settlement allocations. WHT is a separate liability and must not be treated as supplier cash.");
                if (appliedAmount < invoice.Total && invoiceLines.Count > 1)
                    throw new InvalidOperationException("A partial AP settlement across multiple dimensions requires explicit settlement component allocations.");
                if (invoiceLines.Count == 0 || invoiceLines.Sum(x => x.LineSubtotal) != invoice.Total)
                    throw new InvalidOperationException("Tax-free AP invoice lines must reconcile to the invoice total before automatic settlement allocation.");
                details = invoiceLines.Select(x => new SettlementComponentInput(invoice.Id, x.Id, null,
                    appliedAmount == invoice.Total ? x.LineSubtotal : appliedAmount)).ToList();
            }
            if (details.Any(x => x.AmountApplied <= 0m || x.InvoiceLineId.HasValue == x.TaxComponentId.HasValue) ||
                details.Sum(x => x.AmountApplied) != appliedAmount ||
                details.Select(x => (x.InvoiceLineId, x.TaxComponentId)).Distinct().Count() != details.Count)
                throw new InvalidOperationException("AP settlement component allocations must be positive, unique and sum exactly to the supplier cash payment allocation.");

            foreach (var detail in details)
            {
                if (detail.InvoiceLineId.HasValue)
                {
                    var sourceLine = invoiceLines.SingleOrDefault(x => x.Id == detail.InvoiceLineId.Value)
                        ?? throw new InvalidOperationException("An AP settlement line must belong to the allocated invoice.");
                    var prior = await _db.SettlementComponentAllocations.Where(x => x.CompanyId == companyId &&
                        x.SettlementKind == "AP" && x.InvoiceLineId == sourceLine.Id).SumAsync(x => (decimal?)x.AmountApplied, ct) ?? 0m;
                    if (prior + details.Where(x => x.InvoiceLineId == sourceLine.Id).Sum(x => x.AmountApplied) > sourceLine.LineSubtotal)
                        throw new InvalidOperationException("AP settlements cannot allocate more than the posted expense/asset invoice-line amount.");
                }
                else
                {
                    var tax = taxComponents.SingleOrDefault(x => x.Id == detail.TaxComponentId.Value)
                        ?? throw new InvalidOperationException("An AP settlement tax component must belong to the allocated invoice.");
                    if (tax.TaxType != TaxType.VAT)
                        throw new InvalidOperationException("WHT is not supplier cash. WHT settlement to the tax authority must be captured from its own bank/payment evidence.");
                    var prior = await _db.SettlementComponentAllocations.Where(x => x.CompanyId == companyId &&
                        x.SettlementKind == "AP" && x.TaxComponentId == tax.Id).SumAsync(x => (decimal?)x.AmountApplied, ct) ?? 0m;
                    if (prior + details.Where(x => x.TaxComponentId == tax.Id).Sum(x => x.AmountApplied) > tax.Amount)
                        throw new InvalidOperationException("AP settlements cannot allocate more than the posted VAT input component amount.");
                }
                _db.SettlementComponentAllocations.Add(new SettlementComponentAllocation
                {
                    CompanyId = companyId, SettlementKind = "AP", SettlementAllocationId = allocationId,
                    InvoiceId = invoice.Id,
                    ComponentKind = detail.InvoiceLineId.HasValue ? SettlementComponentKind.InvoiceLine : SettlementComponentKind.TaxComponent,
                    InvoiceLineId = detail.InvoiceLineId, TaxComponentId = detail.TaxComponentId,
                    AmountApplied = detail.AmountApplied
                });
            }
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
