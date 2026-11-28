using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities;

public enum ZimbabweDigitalServiceKind { ElectronicService, PhysicalGoods, NonElectronicImportedService, LocalService }
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
    public ZimbabweDswtRecordStatus Status { get; set; } = ZimbabweDswtRecordStatus.Recorded;
}
