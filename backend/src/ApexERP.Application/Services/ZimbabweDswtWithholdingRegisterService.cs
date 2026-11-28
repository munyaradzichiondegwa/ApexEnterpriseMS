using ApexERP.Domain.Common;
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
    Guid? ApPaymentId,
    Guid? BankStatementTransactionId,
    DateTime? BankReconciledAt,
    ZimbabweDswtRecordStatus Status);


public sealed record ZimbabweDswtMonthlyReturnSummary(
    string TaxpayerTin,
    string TaxpayerName,
    int TaxYear,
    int TaxPeriod,
    string CurrencyOfDeclaration,
    decimal GrossPaymentsSubjectToDswt,
    decimal TaxableIncome,
    decimal CalculatedTaxAmount,
    decimal SupplierNetRemittance,
    decimal RemittedToZimra,
    decimal NotYetRemitted,
    int PaymentCount);

/// <summary>
/// Persists intermediary-confirmed DSWT withholding evidence and produces a company-scoped
/// monthly register. It deliberately does not post GL journals or infer an AP cash settlement:
/// those require an approved tax-control-account mapping and explicit bank evidence.
/// </summary>
public sealed class ZimbabweDswtWithholdingRegisterService
{
    private readonly ApexErpDbContext _db;
    private readonly AuditService _audit;

    public ZimbabweDswtWithholdingRegisterService(ApexErpDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

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
        _audit.Record(companyId, _db.CurrentUserId, "Dswt.WithholdingRecorded", "Tax",
            nameof(ZimbabweDswtWithholdingRecord), record.Id, null,
            new { record.PaymentReference, record.GrossPaymentAmount, record.WithheldAmount, record.Currency, record.EvidenceReference });
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
            _audit.Record(companyId, _db.CurrentUserId, "Dswt.ApPaymentReconciled", "Tax",
                nameof(ZimbabweDswtWithholdingRecord), record.Id, null,
                new { record.ApPaymentId, record.GrossPaymentAmount, record.SupplierRemittanceAmount });
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
        var bank = payment.BankAccountId.HasValue
            ? await _db.BankAccounts.FirstOrDefaultAsync(
                x => x.CompanyId == companyId && x.Id == payment.BankAccountId.Value, ct)
            : null;
        if (payment.BankAccountId.HasValue && bank == null)
            throw new InvalidOperationException("The AP payment's bank account does not exist in this company.");
        var journalPosted = payment.JournalEntryId.HasValue &&
            await _db.JournalEntries.AnyAsync(x => x.CompanyId == companyId &&
                x.Id == payment.JournalEntryId.Value && x.Status == JournalStatus.Posted, ct);
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == payment.SupplierId, ct)
            ?? throw new InvalidOperationException("The AP payment supplier does not exist in this company.");

        ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
            grossPaymentAmount, currency, supplierName,
            new ZimbabweDswtApPaymentEvidence(
                payment.Amount, payment.Currency, supplier.Name,
                bank != null, bank?.Currency, journalPosted));
        var allocatedAmount = await _db.ApPaymentAllocations
            .Where(x => x.ApPaymentId == payment.Id)
            .SumAsync(x => (decimal?)x.AmountApplied, ct) ?? 0m;
        if (allocatedAmount != grossPaymentAmount)
            throw new InvalidOperationException("The AP payment allocations must reconcile to the gross bank debit before DSWT evidence can be linked.");
    }


    /// <summary>
    /// Reconciles the DSWT evidence to an authenticated and one-to-one matched bank statement row.
    /// The statement row must already be matched to the exact posted AP bank-ledger movement.
    /// </summary>
    public Task<ZimbabweDswtWithholdingRecord> ReconcileToBankStatementAsync(
        Guid companyId, Guid recordId, Guid statementTransactionId, CancellationToken ct = default)
    {
        if (companyId == Guid.Empty || recordId == Guid.Empty || statementTransactionId == Guid.Empty)
            throw new ArgumentException("Company, DSWT record and bank statement transaction identifiers are required.");

        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"dswt-bank-statement:{companyId:N}:{statementTransactionId:N}", token);
            var record = await _db.ZimbabweDswtWithholdingRecords.FirstOrDefaultAsync(
                x => x.CompanyId == companyId && x.Id == recordId, token)
                ?? throw new InvalidOperationException("DSWT record not found in this company.");
            if (record.Status == ZimbabweDswtRecordStatus.Reversed)
                throw new InvalidOperationException("A reversed DSWT record cannot be bank reconciled.");
            if (!record.ApPaymentId.HasValue)
                throw new InvalidOperationException("Reconcile the DSWT record to its gross AP payment before matching bank evidence.");
            if (record.BankStatementTransactionId.HasValue)
                throw new InvalidOperationException("This DSWT record is already linked to a bank statement transaction.");

            var payment = await _db.ApPayments.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.Id == record.ApPaymentId.Value, token)
                ?? throw new InvalidOperationException("The linked AP payment no longer exists in this company.");
            if (!payment.BankAccountId.HasValue || !payment.JournalEntryId.HasValue)
                throw new InvalidOperationException("The AP payment has no originating bank account or posted journal.");

            var statementLine = await _db.BankStatementImportedTransactions.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.Id == statementTransactionId &&
                x.Status == BankStatementImportedTransactionStatus.Matched &&
                x.MatchedBankTransactionId.HasValue, token)
                ?? throw new InvalidOperationException("The bank statement row must be independently authenticated and matched to a posted ledger transaction first.");
            var ledgerTransaction = await _db.BankTransactions.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.Id == statementLine.MatchedBankTransactionId.Value &&
                x.BankAccountId == payment.BankAccountId.Value && x.IsReconciled &&
                x.MatchedStatementLineHash == statementLine.Id &&
                x.JournalEntryId == payment.JournalEntryId, token)
                ?? throw new InvalidOperationException("The matched bank-ledger transaction does not belong to the linked AP payment.");
            var authenticatedBatch = await _db.BankStatementImportBatches.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.Id == statementLine.ImportBatchId &&
                x.AuthenticationMethod == BankStatementOriginAuthenticationMethod.IndependentBankConfirmation &&
                x.AuthenticatedAt.HasValue &&
                (x.Status == BankStatementImportStatus.Authenticated ||
                 x.Status == BankStatementImportStatus.Certified), token)
                ?? throw new InvalidOperationException("The statement row must belong to an independently authenticated bank statement batch.");
            var bank = await _db.BankAccounts.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.Id == payment.BankAccountId.Value, token)
                ?? throw new InvalidOperationException("The AP payment bank account does not exist in this company.");

            if (ledgerTransaction.Debit != record.GrossPaymentAmount || ledgerTransaction.Credit != 0m ||
                statementLine.Debit != record.GrossPaymentAmount || statementLine.Credit != 0m ||
                !string.Equals(statementLine.Currency, record.Currency, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(bank.Currency, record.Currency, StringComparison.OrdinalIgnoreCase) ||
                payment.Amount != record.GrossPaymentAmount)
                throw new InvalidOperationException("The reconciled bank debit, AP payment and DSWT gross amount/currency do not agree.");

            if (await _db.ZimbabweDswtWithholdingRecords.AnyAsync(x =>
                x.CompanyId == companyId && x.BankStatementTransactionId == statementTransactionId, token))
                throw new InvalidOperationException("This bank statement transaction is already linked to another DSWT record.");

            record.BankStatementTransactionId = statementLine.Id;
            record.BankReconciledAt = DateTime.UtcNow;
            _audit.Record(companyId, _db.CurrentUserId, "Dswt.BankStatementReconciled", "Tax",
                nameof(ZimbabweDswtWithholdingRecord), record.Id, null,
                new { record.BankStatementTransactionId, record.BankReconciledAt, Statement = authenticatedBatch.StatementReference });
            await _db.SaveChangesAsync(token);
            return record;
        }, ct);
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
        _audit.Record(companyId, _db.CurrentUserId, "Dswt.CertificateIssued", "Tax",
            nameof(ZimbabweDswtWithholdingRecord), record.Id, null,
            new { record.CertificateNumber, record.CertificateIssuedAt });
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
        _audit.Record(companyId, _db.CurrentUserId, "Dswt.RemittedToZimra", "Tax",
            nameof(ZimbabweDswtWithholdingRecord), record.Id, null,
            new { record.RemittedToZimraAt, EvidenceReference = evidenceReference.Trim() });
        await _db.SaveChangesAsync(ct);
        return record;
    }


    /// <summary>
    /// Produces a form-aligned monthly preparation summary. The published ZIMRA return
    /// expresses the declaration in USD; this implementation refuses to invent FX
    /// conversions when any source payment is denominated in another currency.
    /// This is a preparation/export schedule, not TaRMS submission.
    /// </summary>
    public async Task<ZimbabweDswtMonthlyReturnSummary> GenerateMonthlyReturnSummaryAsync(
        Guid companyId, int year, int month, string taxpayerTin, CancellationToken ct = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("A company is required.", nameof(companyId));
        if (year < 2026 || month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "DSWT reporting period must be a valid month from 2026 onward.");
        if (string.IsNullOrWhiteSpace(taxpayerTin) || taxpayerTin.Trim().Length > 40)
            throw new ArgumentException("The registered ZIMRA TIN is required and must not exceed 40 characters.", nameof(taxpayerTin));

        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == companyId, ct)
            ?? throw new InvalidOperationException("Company not found.");
        if (string.IsNullOrWhiteSpace(company.ZimbabweTaxpayerTin))
            throw new InvalidOperationException("The company's Zimbabwe ZIMRA TIN has not been configured in its tax profile.");
        if (!string.Equals(company.ZimbabweTaxpayerTin.Trim(), taxpayerTin.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied TIN does not match the company's registered Zimbabwe tax profile.");
        var records = await _db.ZimbabweDswtWithholdingRecords
            .Where(x => x.CompanyId == companyId && x.TaxPeriodYear == year &&
                        x.TaxPeriodMonth == month && x.Status != ZimbabweDswtRecordStatus.Reversed)
            .ToListAsync(ct);
        if (records.Any(x => !string.Equals(x.Currency, "USD", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The ZIMRA DSWT return requires a USD declaration. At least one source payment is non-USD; no return has been produced because transaction-level approved USD conversion evidence is not yet captured.");

        var gross = records.Sum(x => x.GrossPaymentAmount);
        var tax = records.Sum(x => x.WithheldAmount);
        var net = records.Sum(x => x.SupplierRemittanceAmount);
        var remitted = records.Where(x => x.Status == ZimbabweDswtRecordStatus.RemittedToZimra)
            .Sum(x => x.WithheldAmount);
        return new ZimbabweDswtMonthlyReturnSummary(
            company.ZimbabweTaxpayerTin.Trim(), company.Name, year, month, "USD",
            gross, gross, tax, net, remitted, tax - remitted, records.Count);
    }

    public async Task<string> GenerateMonthlyReturnCsvAsync(
        Guid companyId, int year, int month, string taxpayerTin, CancellationToken ct = default)
    {
        var summary = await GenerateMonthlyReturnSummaryAsync(companyId, year, month, taxpayerTin, ct);
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var rows = new[]
        {
            "TIN,Name of tax payer,Tax year,Tax period,Currency of declaration,Gross payments subject to DSWT (USD),Taxable income (USD),Calculated tax amount (USD),Supplier net remittance (USD),Remitted to ZIMRA (USD),Not yet remitted (USD),Payment count",
            string.Join(",", new[]
            {
                Q(summary.TaxpayerTin), Q(summary.TaxpayerName), summary.TaxYear.ToString(),
                summary.TaxPeriod.ToString("00"), Q(summary.CurrencyOfDeclaration),
                summary.GrossPaymentsSubjectToDswt.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                summary.TaxableIncome.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                summary.CalculatedTaxAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                summary.SupplierNetRemittance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                summary.RemittedToZimra.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                summary.NotYetRemitted.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                summary.PaymentCount.ToString()
            })
        };
        return string.Join(Environment.NewLine, rows) + Environment.NewLine;
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
                x.WithheldAt, x.CertificateNumber, x.ApPaymentId,
                x.BankStatementTransactionId, x.BankReconciledAt, x.Status))
            .ToListAsync(ct);
    }
}


/// <summary>
/// Validates only safe storage constraints for a ZIMRA TIN. ZIMRA's published guidance
/// reviewed for this implementation does not define a universal character pattern, so
/// the application must not invent one or reject an identifier based on an assumed format.
/// </summary>
public static class ZimbabweTaxpayerTinValidator
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var tin = value.Trim();
        return tin.Length <= 40 && !tin.Any(char.IsControl);
    }
}


public sealed record ZimbabweDswtFilingPreparationRequest(int TaxYear, int TaxPeriod, int ReturnVersion, string TaxpayerTin);
public sealed record ZimbabweDswtFilingSubmissionRequest(DateTime SubmittedAt, string SubmissionReference, string EvidenceReference);
public sealed record ZimbabweDswtFilingAcknowledgementRequest(DateTime AcknowledgedAt, bool Accepted, string AcknowledgementReference, string EvidenceReference);
public sealed record ZimbabweDswtFilingPaymentRequest(DateTime PaidAt, string PaymentReference, decimal Amount, string Currency, string EvidenceReference);

/// <summary>
/// Controlled manual TaRMS filing workflow. The service stores an immutable payload snapshot,
/// records evidence supplied by an authorised operator, and never transmits to TaRMS.
/// </summary>
public sealed class ZimbabweDswtFilingWorkflowService
{
    private readonly ApexErpDbContext _db;
    private readonly ZimbabweDswtWithholdingRegisterService _register;
    private readonly AuditService _audit;

    public ZimbabweDswtFilingWorkflowService(
        ApexErpDbContext db, ZimbabweDswtWithholdingRegisterService register, AuditService audit)
    {
        _db = db;
        _register = register;
        _audit = audit;
    }

    public Task<ZimbabweDswtFiling> PrepareAsync(
        Guid companyId, ZimbabweDswtFilingPreparationRequest request, CancellationToken ct = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("A company is required.", nameof(companyId));
        if (request.ReturnVersion < 1) throw new ArgumentOutOfRangeException(nameof(request.ReturnVersion));
        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync(
                $"dswt-filing:{companyId:N}:{request.TaxYear}:{request.TaxPeriod}:{request.ReturnVersion}", token);
            var summary = await _register.GenerateMonthlyReturnSummaryAsync(
                companyId, request.TaxYear, request.TaxPeriod, request.TaxpayerTin, token);
            var payload = System.Text.Json.JsonSerializer.Serialize(summary);
            var payloadHash = Sha256(payload);
            var idempotencyKey = Sha256(
                $"{companyId:N}|{request.TaxYear}|{request.TaxPeriod}|{request.ReturnVersion}|{payloadHash}");

            var existing = await _db.ZimbabweDswtFilings.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.TaxYear == request.TaxYear &&
                x.TaxPeriod == request.TaxPeriod && x.ReturnVersion == request.ReturnVersion, token);
            if (existing != null)
            {
                if (!string.Equals(existing.PayloadSha256, payloadHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "This return version already has a different immutable snapshot. Prepare a new return version after reviewing the changed source register.");
                return existing;
            }

            var filing = new ZimbabweDswtFiling
            {
                CompanyId = companyId,
                TaxYear = request.TaxYear,
                TaxPeriod = request.TaxPeriod,
                ReturnVersion = request.ReturnVersion,
                TaxpayerTin = summary.TaxpayerTin,
                TaxpayerName = summary.TaxpayerName,
                Currency = summary.CurrencyOfDeclaration,
                TaxableIncome = summary.TaxableIncome,
                TaxAmount = summary.CalculatedTaxAmount,
                PaymentCount = summary.PaymentCount,
                PayloadJson = payload,
                PayloadSha256 = payloadHash,
                IdempotencyKey = idempotencyKey,
                Status = ZimbabweDswtFilingStatus.Prepared,
                SubmissionChannel = "ManualTaRMS"
            };
            _db.ZimbabweDswtFilings.Add(filing);
            _audit.Record(companyId, _db.CurrentUserId, "Dswt.FilingPrepared", "Tax",
                nameof(ZimbabweDswtFiling), filing.Id, null,
                new { filing.TaxYear, filing.TaxPeriod, filing.ReturnVersion, filing.PayloadSha256, filing.IdempotencyKey });
            await _db.SaveChangesAsync(token);
            return filing;
        }, ct);
    }

    public async Task<ZimbabweDswtFiling> MarkSubmittedAsync(
        Guid companyId, Guid filingId, ZimbabweDswtFilingSubmissionRequest request, CancellationToken ct = default)
    {
        if (request.SubmittedAt == default || string.IsNullOrWhiteSpace(request.SubmissionReference) ||
            string.IsNullOrWhiteSpace(request.EvidenceReference))
            throw new ArgumentException("A submission date, TaRMS reference and evidence reference are required.");
        var filing = await FindAsync(companyId, filingId, ct);
        if (filing.Status != ZimbabweDswtFilingStatus.Prepared)
            throw new InvalidOperationException("Only a prepared filing can be marked as submitted.");
        var reference = request.SubmissionReference.Trim();
        if (await _db.ZimbabweDswtFilings.AnyAsync(x =>
            x.CompanyId == companyId && x.Id != filingId && x.SubmissionReference == reference, ct))
            throw new InvalidOperationException("The TaRMS submission reference is already recorded for this company.");
        filing.SubmittedAt = request.SubmittedAt;
        filing.SubmittedBy = _db.CurrentUserId;
        filing.SubmissionReference = reference;
        filing.SubmissionEvidenceReference = request.EvidenceReference.Trim();
        filing.Status = ZimbabweDswtFilingStatus.Submitted;
        _audit.Record(companyId, _db.CurrentUserId, "Dswt.FilingSubmittedManually", "Tax",
            nameof(ZimbabweDswtFiling), filing.Id, null,
            new { filing.SubmittedAt, filing.SubmissionReference, filing.SubmissionEvidenceReference, filing.SubmissionChannel });
        await _db.SaveChangesAsync(ct);
        return filing;
    }

    public async Task<ZimbabweDswtFiling> RecordAcknowledgementAsync(
        Guid companyId, Guid filingId, ZimbabweDswtFilingAcknowledgementRequest request, CancellationToken ct = default)
    {
        if (request.AcknowledgedAt == default || string.IsNullOrWhiteSpace(request.AcknowledgementReference) ||
            string.IsNullOrWhiteSpace(request.EvidenceReference))
            throw new ArgumentException("An acknowledgement date, reference and evidence reference are required.");
        var filing = await FindAsync(companyId, filingId, ct);
        if (filing.Status != ZimbabweDswtFilingStatus.Submitted)
            throw new InvalidOperationException("Only a manually submitted filing can receive a TaRMS acknowledgement.");
        if (filing.SubmittedAt.HasValue && request.AcknowledgedAt < filing.SubmittedAt.Value)
            throw new InvalidOperationException("The acknowledgement cannot precede submission.");
        filing.AcknowledgedAt = request.AcknowledgedAt;
        filing.AcknowledgementReference = request.AcknowledgementReference.Trim();
        filing.AcknowledgementEvidenceReference = request.EvidenceReference.Trim();
        filing.Status = request.Accepted
            ? ZimbabweDswtFilingStatus.Acknowledged
            : ZimbabweDswtFilingStatus.Rejected;
        _audit.Record(companyId, _db.CurrentUserId,
            request.Accepted ? "Dswt.FilingAcknowledged" : "Dswt.FilingRejected", "Tax",
            nameof(ZimbabweDswtFiling), filing.Id, null,
            new { filing.AcknowledgedAt, request.Accepted, filing.AcknowledgementReference, filing.AcknowledgementEvidenceReference });
        await _db.SaveChangesAsync(ct);
        return filing;
    }

    public async Task<ZimbabweDswtFiling> ConfirmPaymentAsync(
        Guid companyId, Guid filingId, ZimbabweDswtFilingPaymentRequest request, CancellationToken ct = default)
    {
        if (request.PaidAt == default || string.IsNullOrWhiteSpace(request.PaymentReference) ||
            string.IsNullOrWhiteSpace(request.EvidenceReference))
            throw new ArgumentException("A payment date, bank/ZIMRA payment reference and evidence reference are required.");
        var filing = await FindAsync(companyId, filingId, ct);
        if (filing.Status != ZimbabweDswtFilingStatus.Acknowledged)
            throw new InvalidOperationException("Payment can only be confirmed for an acknowledged return.");
        if (filing.TaxAmount <= 0 || request.Amount <= 0 || request.Amount != filing.TaxAmount)
            throw new InvalidOperationException("The evidenced payment amount must exactly equal the DSWT return tax amount.");
        if (!string.Equals(request.Currency?.Trim(), filing.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The payment currency must match the declared return currency.");
        if (filing.AcknowledgedAt.HasValue && request.PaidAt < filing.AcknowledgedAt.Value)
            throw new InvalidOperationException("The payment date cannot precede the TaRMS acknowledgement.");
        filing.PaymentConfirmedAt = request.PaidAt;
        filing.PaymentReference = request.PaymentReference.Trim();
        filing.PaymentAmount = request.Amount;
        filing.PaymentCurrency = request.Currency.Trim().ToUpperInvariant();
        filing.PaymentEvidenceReference = request.EvidenceReference.Trim();
        filing.Status = ZimbabweDswtFilingStatus.PaymentConfirmed;
        _audit.Record(companyId, _db.CurrentUserId, "Dswt.FilingPaymentConfirmed", "Tax",
            nameof(ZimbabweDswtFiling), filing.Id, null,
            new { filing.PaymentConfirmedAt, filing.PaymentReference, filing.PaymentAmount, filing.PaymentCurrency, filing.PaymentEvidenceReference });
        await _db.SaveChangesAsync(ct);
        return filing;
    }

    public Task<ZimbabweDswtFiling?> GetAsync(Guid companyId, Guid filingId, CancellationToken ct = default) =>
        _db.ZimbabweDswtFilings.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == filingId, ct);

    private async Task<ZimbabweDswtFiling> FindAsync(Guid companyId, Guid filingId, CancellationToken ct) =>
        await _db.ZimbabweDswtFilings.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.Id == filingId, ct)
        ?? throw new InvalidOperationException("DSWT filing was not found in this company.");

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
