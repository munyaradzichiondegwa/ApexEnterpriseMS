using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities;

public enum ZimbabweDigitalServiceKind { ElectronicService, PhysicalGoods, NonElectronicImportedService, LocalService, OtherNonResidentPayment }
public enum ZimbabweSupplierVatStatus { NotRegistered, Registered }
public enum ZimbabweDigitalPaymentRoute { ZimbabweIntermediary, PaidOutsideZimbabwe, LocalPayment }

public enum ZimbabweDswtRecordStatus
{
    Recorded,
    CertificateIssued,
    RemittedToZimra,
    Reversed
}

/// <summary>
/// Company-scoped evidence register for Digital Services Withholding Tax deducted by a
/// Zimbabwean intermediary. This is a tax evidence/control record, not itself a GL posting.
/// </summary>
public sealed class ZimbabweDswtWithholdingRecord : TenantEntity
{
    public string PaymentReference { get; set; } = string.Empty;
    public string IntermediaryName { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierCountryCode { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public decimal GrossPaymentAmount { get; set; }
    public decimal WithholdingRate { get; set; }
    public decimal WithheldAmount { get; set; }
    public decimal SupplierRemittanceAmount { get; set; }
    public ZimbabweDigitalServiceKind ServiceKind { get; set; }
    public ZimbabweSupplierVatStatus SupplierVatStatus { get; set; }
    public ZimbabweDigitalPaymentRoute PaymentRoute { get; set; }
    public bool ConsumedInZimbabwe { get; set; }
    public bool ZeroRatedOrExempt { get; set; }
    public bool RecipientAlreadySelfAccountedVat { get; set; }
    public DateTime WithheldAt { get; set; }
    public int TaxPeriodYear { get; set; }
    public int TaxPeriodMonth { get; set; }
    public string? CertificateNumber { get; set; }
    public DateTime? CertificateIssuedAt { get; set; }
    public DateTime? RemittedToZimraAt { get; set; }
    public string? EvidenceReference { get; set; }
    public Guid? ApPaymentId { get; set; }
    public Guid? BankStatementTransactionId { get; set; }
    public DateTime? BankReconciledAt { get; set; }
    public ZimbabweDswtRecordStatus Status { get; set; } = ZimbabweDswtRecordStatus.Recorded;
}


public enum ZimbabweDswtFilingStatus
{
    Prepared,
    Submitted,
    Acknowledged,
    Rejected,
    PaymentConfirmed
}

/// <summary>
/// Immutable, company-scoped snapshot of a DSWT return prepared for manual TaRMS filing.
/// Submission and payment evidence are recorded separately; this entity never claims
/// an electronic submission occurred.
/// </summary>
public sealed class ZimbabweDswtFiling : TenantEntity
{
    public int TaxYear { get; set; }
    public int TaxPeriod { get; set; }
    public int ReturnVersion { get; set; }
    public string TaxpayerTin { get; set; } = string.Empty;
    public string TaxpayerName { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public decimal TaxableIncome { get; set; }
    public decimal TaxAmount { get; set; }
    public int PaymentCount { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public ZimbabweDswtFilingStatus Status { get; set; } = ZimbabweDswtFilingStatus.Prepared;
    public string SubmissionChannel { get; set; } = "ManualTaRMS";
    public DateTime? SubmittedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public string? SubmissionReference { get; set; }
    public string? SubmissionEvidenceReference { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgementReference { get; set; }
    public string? AcknowledgementEvidenceReference { get; set; }
    public DateTime? PaymentConfirmedAt { get; set; }
    public string? PaymentReference { get; set; }
    public decimal? PaymentAmount { get; set; }
    public string? PaymentCurrency { get; set; }
    public string? PaymentEvidenceReference { get; set; }
}
