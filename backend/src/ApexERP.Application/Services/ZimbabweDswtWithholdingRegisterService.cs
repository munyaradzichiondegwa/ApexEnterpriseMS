using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services;

public sealed record ZimbabweDswtRecordRequest(
    string PaymentReference,
    string IntermediaryName,
    string SupplierName,
    string SupplierCountryCode,
    string Currency,
    DateTime WithheldAt,
    string? EvidenceReference,
    Guid? ApPaymentId,
    ZimbabweDigitalServicesTaxRequest TaxRequest);

public sealed record ZimbabweDswtMonthlyReturnLine(
    Guid RecordId,
    string PaymentReference,
    string IntermediaryName,
    string SupplierName,
    string SupplierCountryCode,
    decimal GrossPaymentAmount,
    decimal WithholdingRate,
    decimal WithheldAmount,
    string Currency,
    DateTime WithheldAt,
    string? CertificateNumber,
    ZimbabweDswtRecordStatus Status);

/// <summary>
/// Persists intermediary-confirmed DSWT withholding evidence and produces a company-scoped
/// monthly register. It deliberately does not post GL journals or infer an AP cash settlement:
/// those require an approved tax-control-account mapping and explicit bank evidence.
/// </summary>
public sealed class ZimbabweDswtWithholdingRegisterService
{
    private readonly ApexErpDbContext _db;

    public ZimbabweDswtWithholdingRegisterService(ApexErpDbContext db) => _db = db;

    public async Task<ZimbabweDswtWithholdingRecord> RecordWithholdingAsync(
        Guid companyId, ZimbabweDswtRecordRequest request, CancellationToken ct = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("A company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(request.PaymentReference))
            throw new ArgumentException("The intermediary payment reference is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IntermediaryName) || string.IsNullOrWhiteSpace(request.SupplierName))
            throw new ArgumentException("Intermediary and supplier names are required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SupplierCountryCode) || request.SupplierCountryCode.Length != 2 || request.SupplierCountryCode.Any(c => !char.IsAsciiLetter(c)))
            throw new ArgumentException("Supplier country must be a two-letter ISO country code.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3 || request.Currency.Any(c => !char.IsAsciiLetter(c)))
            throw new ArgumentException("Currency must be a three-letter ISO currency code.", nameof(request));
        if (request.WithheldAt == default || request.WithheldAt.Date < new DateTime(2026, 1, 1))
            throw new ArgumentException("The actual withholding date must be on or after 1 January 2026.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EvidenceReference))
            throw new ArgumentException("Intermediary/bank evidence is required before recording a withholding.", nameof(request));
        if (request.TaxRequest.PaymentRoute != ZimbabweDigitalPaymentRoute.ZimbabweIntermediary)
            throw new InvalidOperationException("The DSWT register accepts only withholding confirmed by a Zimbabwe intermediary.");

        var evaluated = ZimbabweDigitalServicesTaxEvaluator.Evaluate(request.TaxRequest);
        if (!evaluated.WithholdingRequired)
            throw new InvalidOperationException($"No DSWT withholding can be recorded: {evaluated.Reason}");
        if (evaluated.TaxablePaymentAmount != request.TaxRequest.GrossPaymentAmount)
            throw new InvalidOperationException("The evaluated taxable amount does not reconcile to the gross payment.");

        if (request.ApPaymentId.HasValue)
            await ValidateApPaymentLinkAsync(companyId, request.ApPaymentId.Value,
                request.SupplierName, request.Currency, evaluated.TaxablePaymentAmount, ct);

        var paymentReference = request.PaymentReference.Trim();
        var duplicate = await _db.ZimbabweDswtWithholdingRecords.AnyAsync(x =>
            x.CompanyId == companyId && x.PaymentReference == paymentReference, ct);
        if (duplicate)
            throw new InvalidOperationException("This intermediary payment reference has already been recorded for the company.");

        var localDate = request.WithheldAt.Date;
        var record = new ZimbabweDswtWithholdingRecord
        {
            CompanyId = companyId,
            PaymentReference = paymentReference,
            IntermediaryName = request.IntermediaryName.Trim(),
            SupplierName = request.SupplierName.Trim(),
            SupplierCountryCode = request.SupplierCountryCode.ToUpperInvariant(),
            Currency = request.Currency.ToUpperInvariant(),
            GrossPaymentAmount = evaluated.TaxablePaymentAmount,
            WithholdingRate = evaluated.WithholdingRate,
            WithheldAmount = evaluated.WithholdingAmount,
            SupplierRemittanceAmount = evaluated.SupplierRemittanceAmount,
            ServiceKind = request.TaxRequest.ServiceKind,
            SupplierVatStatus = request.TaxRequest.SupplierVatStatus,
            PaymentRoute = request.TaxRequest.PaymentRoute,
            ConsumedInZimbabwe = request.TaxRequest.ConsumedInZimbabwe,
            ZeroRatedOrExempt = request.TaxRequest.ZeroRatedOrExempt,
            RecipientAlreadySelfAccountedVat = request.TaxRequest.RecipientAlreadySelfAccountedVat,
            WithheldAt = request.WithheldAt,
            TaxPeriodYear = localDate.Year,
            TaxPeriodMonth = localDate.Month,
            EvidenceReference = string.IsNullOrWhiteSpace(request.EvidenceReference) ? null : request.EvidenceReference.Trim(),
            ApPaymentId = request.ApPaymentId,
            Status = ZimbabweDswtRecordStatus.Recorded
        };
        _db.ZimbabweDswtWithholdingRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }


    /// <summary>
    /// Links a DSWT record to the gross AP settlement. The payment must be a posted,
    /// bank-account-backed payment for the same supplier, currency and gross amount.
    /// The withheld tax is not posted as a second expense or payable: it is part of the
    /// gross settlement already represented by the AP debit and bank credit.
    /// </summary>
    public Task<ZimbabweDswtWithholdingRecord> ReconcileToApPaymentAsync(
        Guid companyId, Guid recordId, Guid apPaymentId, CancellationToken ct = default)
    {
        if (companyId == Guid.Empty || recordId == Guid.Empty || apPaymentId == Guid.Empty)
            throw new ArgumentException("Company, DSWT record and AP payment identifiers are required.");

        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"dswt-ap-payment:{companyId:N}:{apPaymentId:N}", token);
            var record = await _db.ZimbabweDswtWithholdingRecords.FirstOrDefaultAsync(
                x => x.CompanyId == companyId && x.Id == recordId, token)
                ?? throw new InvalidOperationException("DSWT record not found in this company.");
            if (record.Status == ZimbabweDswtRecordStatus.Reversed)
                throw new InvalidOperationException("A reversed DSWT record cannot be linked to an AP payment.");
            if (record.ApPaymentId.HasValue)
                throw new InvalidOperationException("This DSWT record is already linked to an AP payment.");

            await ValidateApPaymentLinkAsync(companyId, apPaymentId,
                record.SupplierName, record.Currency, record.GrossPaymentAmount, token);
            if (await _db.ZimbabweDswtWithholdingRecords.AnyAsync(x =>
                x.CompanyId == companyId && x.ApPaymentId == apPaymentId, token))
                throw new InvalidOperationException("The AP payment is already linked to another DSWT record.");

            record.ApPaymentId = apPaymentId;
            await _db.SaveChangesAsync(token);
            return record;
        }, ct);
    }

    private async Task ValidateApPaymentLinkAsync(
        Guid companyId, Guid apPaymentId, string supplierName, string currency,
        decimal grossPaymentAmount, CancellationToken ct)
    {
        var payment = await _db.ApPayments.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == apPaymentId, ct)
            ?? throw new InvalidOperationException("The linked AP payment does not exist in this company.");
        if (payment.Amount != grossPaymentAmount)
            throw new InvalidOperationException("The AP payment must equal the gross payment before intermediary withholding, not the supplier's net remittance.");
        if (!string.Equals(payment.Currency, currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AP payment currency does not match the DSWT payment currency.");
        if (!payment.BankAccountId.HasValue)
            throw new InvalidOperationException("The AP payment must identify its originating bank account before DSWT reconciliation.");
        var bank = await _db.BankAccounts.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == payment.BankAccountId.Value, ct)
            ?? throw new InvalidOperationException("The AP payment's bank account does not exist in this company.");
        if (!string.Equals(bank.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AP payment and originating bank account currencies do not match.");
        if (!payment.JournalEntryId.HasValue ||
            !await _db.JournalEntries.AnyAsync(x => x.CompanyId == companyId &&
                x.Id == payment.JournalEntryId.Value && x.Status == JournalStatus.Posted, ct))
            throw new InvalidOperationException("The AP payment must have a posted GL journal before DSWT reconciliation.");
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == payment.SupplierId, ct)
            ?? throw new InvalidOperationException("The AP payment supplier does not exist in this company.");
        if (!string.Equals(supplier.Name.Trim(), supplierName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AP payment supplier does not match the DSWT supplier.");
    }

    public async Task<ZimbabweDswtWithholdingRecord> IssueCertificateAsync(
        Guid companyId, Guid recordId, string certificateNumber, DateTime issuedAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(certificateNumber))
            throw new ArgumentException("The intermediary certificate number is required.", nameof(certificateNumber));
        if (issuedAt == default) throw new ArgumentException("Certificate issue date is required.", nameof(issuedAt));

        var record = await _db.ZimbabweDswtWithholdingRecords.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == recordId, ct)
            ?? throw new InvalidOperationException("DSWT record not found in this company.");
        if (record.Status != ZimbabweDswtRecordStatus.Recorded)
            throw new InvalidOperationException("A certificate can only be issued once for a recorded withholding.");
        if (issuedAt < record.WithheldAt)
            throw new InvalidOperationException("The certificate issue date cannot precede the withholding date.");
        var certificate = certificateNumber.Trim();
        if (await _db.ZimbabweDswtWithholdingRecords.AnyAsync(x =>
            x.CompanyId == companyId && x.CertificateNumber == certificate, ct))
            throw new InvalidOperationException("The certificate number is already used in this company.");

        record.CertificateNumber = certificate;
        record.CertificateIssuedAt = issuedAt;
        record.Status = ZimbabweDswtRecordStatus.CertificateIssued;
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<ZimbabweDswtWithholdingRecord> MarkRemittedToZimraAsync(
        Guid companyId, Guid recordId, DateTime remittedAt, string evidenceReference, CancellationToken ct = default)
    {
        if (remittedAt == default || string.IsNullOrWhiteSpace(evidenceReference))
            throw new ArgumentException("A remittance date and bank/ZIMRA evidence reference are required.");
        var record = await _db.ZimbabweDswtWithholdingRecords.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == recordId, ct)
            ?? throw new InvalidOperationException("DSWT record not found in this company.");
        if (record.Status != ZimbabweDswtRecordStatus.CertificateIssued)
            throw new InvalidOperationException("Only certificate-issued withholding can be marked as remitted.");
        if (remittedAt < record.WithheldAt || (record.CertificateIssuedAt.HasValue && remittedAt < record.CertificateIssuedAt.Value))
            throw new InvalidOperationException("The ZIMRA remittance date cannot precede the withholding or certificate issue date.");

        record.RemittedToZimraAt = remittedAt;
        record.EvidenceReference = string.IsNullOrWhiteSpace(record.EvidenceReference)
            ? evidenceReference.Trim()
            : $"{record.EvidenceReference}; ZIMRA remittance: {evidenceReference.Trim()}";
        record.Status = ZimbabweDswtRecordStatus.RemittedToZimra;
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public Task<List<ZimbabweDswtMonthlyReturnLine>> GenerateMonthlyRegisterAsync(
        Guid companyId, int year, int month, CancellationToken ct = default)
    {
        if (year < 2026 || month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "DSWT reporting period must be a valid month from 2026 onward.");

        return _db.ZimbabweDswtWithholdingRecords
            .Where(x => x.CompanyId == companyId && x.TaxPeriodYear == year && x.TaxPeriodMonth == month &&
                        x.Status != ZimbabweDswtRecordStatus.Reversed)
            .OrderBy(x => x.WithheldAt).ThenBy(x => x.PaymentReference)
            .Select(x => new ZimbabweDswtMonthlyReturnLine(
                x.Id, x.PaymentReference, x.IntermediaryName, x.SupplierName, x.SupplierCountryCode,
                x.GrossPaymentAmount, x.WithholdingRate, x.WithheldAmount, x.Currency,
                x.WithheldAt, x.CertificateNumber, x.Status))
            .ToListAsync(ct);
    }
}
