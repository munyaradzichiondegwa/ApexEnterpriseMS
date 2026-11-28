using System;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities;

public enum ModifiedCashPolicyStatus { Draft, PendingApproval, Approved, Rejected, Superseded }
public enum ModifiedCashRecognitionTreatment { IncludeInBudgetActual, DiscloseSeparately }
public enum ModifiedCashLedgerEventKind { Recognition, Liquidation, Reversal }
public enum ModifiedCashLedgerEventStatus { Draft, PendingCertification, Certified, Rejected }

/// <summary>Company-scoped, approved policy defining which commitment events qualify for
/// modified-cash recognition and how each event maps to the approved budget classification.</summary>
public sealed class ModifiedCashRecognitionPolicy : TenantEntity
{
    public Guid BudgetVersionId { get; set; }
    public int FinancialYear { get; set; }
    public int VersionNumber { get; set; }
    public string PolicyReference { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime ReportingPeriodStart { get; set; }
    public DateTime ReportingPeriodEnd { get; set; }
    public int PostPeriodRecognitionWindowDays { get; set; }
    public ModifiedCashRecognitionTreatment Treatment { get; set; }
    public ModifiedCashPolicyStatus Status { get; set; } = ModifiedCashPolicyStatus.Draft;
    public Guid PreparedBy { get; set; }
    public Guid? ApprovalWorkflowId { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>Approved mapping from a policy-eligible commitment event class to a specific
/// immutable budget-version line. Event classes are explicit; unknown classes fail closed.</summary>
public sealed class ModifiedCashRecognitionMapping : TenantEntity
{
    public Guid PolicyId { get; set; }
    public string SourceEventKind { get; set; } = string.Empty;
    public Guid BudgetVersionLineId { get; set; }
}

/// <summary>Append-only modified-cash recognition ledger. Recognition adds an evidenced unpaid
/// obligation to modified-cash actuals; liquidation offsets that recognition when the ordinary
/// cash settlement is recognised; reversal cancels an unliquidated recognition. Every event has
/// a source identity and separate certification lifecycle.</summary>
public sealed class ModifiedCashRecognitionLedgerEvent : TenantEntity
{
    public Guid PolicyId { get; set; }
    public Guid CommitmentId { get; set; }
    public Guid SourceEventId { get; set; }
    public string SourceEventKind { get; set; } = string.Empty;
    public ModifiedCashLedgerEventKind EventKind { get; set; }
    public Guid? OriginalEventId { get; set; }
    public decimal Amount { get; set; }
    public DateTime EventDate { get; set; }
    public string EvidenceReference { get; set; } = string.Empty;
    public ModifiedCashLedgerEventStatus Status { get; set; } = ModifiedCashLedgerEventStatus.Draft;
    public Guid PreparedBy { get; set; }
    public Guid? ApprovalWorkflowId { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public Guid? CertifiedBy { get; set; }
    public DateTime? CertifiedAt { get; set; }
}
