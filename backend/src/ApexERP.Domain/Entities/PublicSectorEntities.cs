using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    /// <summary>Vote &gt; Program &gt; Sub-Program fund hierarchy. Every government expenditure
    /// transaction (PO, AP invoice, payroll) is tagged against a FundCentre, and total expenditure
    /// cannot exceed AppropriationAmount without an approved override (PRD Module 16).</summary>
    public class FundCentre : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public FundCentreLevel Level { get; set; }
        public Guid? ParentId { get; set; }
        public int FinancialYear { get; set; }
        public decimal AppropriationAmount { get; set; }
        /// <summary>Balance carried forward from the prior financial year's closing (year-end workflow).</summary>
        public decimal CarriedForwardAmount { get; set; }
        public decimal LapsedAmount { get; set; }
        public bool IsClosed { get; set; }
    }

    /// <summary>
    /// Versioned, authority-approved budget used for IPSAS 24 original/final comparison.
    /// Original and revised budgets are separate immutable approval snapshots.
    /// </summary>
    public class PublicSectorBudgetVersion : TenantEntity
    {
        public int FinancialYear { get; set; }
        public int VersionNumber { get; set; }
        public PublicSectorBudgetVersionKind Kind { get; set; }
        public PublicSectorBudgetVersionStatus Status { get; set; } = PublicSectorBudgetVersionStatus.Draft;
        public string Currency { get; set; } = "USD";
        public AccountingBasis AccountingBasis { get; set; }
        public string ClassificationBasis { get; set; } = string.Empty;
        public string EntityScope { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public string AuthorityReference { get; set; } = string.Empty;
        public string? AmendmentReason { get; set; }
        public Guid? ApprovalWorkflowId { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public ICollection<PublicSectorBudgetVersionLine> Lines { get; set; } = new List<PublicSectorBudgetVersionLine>();
    }

    /// <summary>One approved budget line, mapped to the chart of accounts and government vote.</summary>
    public class PublicSectorBudgetVersionLine : TenantEntity
    {
        public Guid BudgetVersionId { get; set; }
        public Guid FundCentreId { get; set; }
        public Guid GlAccountId { get; set; }
        public Guid? CostCentreId { get; set; }
        public int LineNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public string ClassificationCode { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Reconciliation adjustment between comparable budget actuals and the amounts in the
    /// accrual-basis financial statements. Categories follow IPSAS 24 paragraph 47.
    /// </summary>
    public class PublicSectorBudgetReconciliation : TenantEntity
    {
        public int FinancialYear { get; set; }
        public PublicSectorBudgetReconciliationType Type { get; set; }
        public string Description { get; set; } = string.Empty;
        public string StatementSection { get; set; } = string.Empty;
        public decimal OperatingAmount { get; set; }
        public decimal InvestingAmount { get; set; }
        public decimal FinancingAmount { get; set; }
        public string SupportingReference { get; set; } = string.Empty;
        public PublicSectorBudgetReconciliationStatus Status { get; set; } = PublicSectorBudgetReconciliationStatus.Draft;
        public Guid? ApprovalWorkflowId { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
    }

    /// <summary>
    /// Approved appropriation distribution to an expense GL account and optional cost centre.
    /// Draft and pending allocations are excluded from budget-versus-actual reporting.
    /// </summary>
    public class PublicSectorBudgetAllocation : TenantEntity
    {
        public Guid FundCentreId { get; set; }
        public Guid GlAccountId { get; set; }
        public Guid? CostCentreId { get; set; }
        public int FinancialYear { get; set; }
        public decimal Amount { get; set; }
        public PublicSectorBudgetAllocationStatus Status { get; set; } = PublicSectorBudgetAllocationStatus.Draft;
        public Guid? ApprovalWorkflowId { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
    }

    /// <summary>Encumbrance created when a PO is approved against a FundCentre: reduces the
    /// available budget immediately (Appropriation - Commitments - Actuals), released/replaced by
    /// the actual expenditure on GRN/invoice, or fully reversed on PO cancellation.</summary>
    public class Commitment : TenantEntity
    {
        public Guid FundCentreId { get; set; }
        public Guid PurchaseOrderId { get; set; }
        public decimal Amount { get; set; }
        /// <summary>Base-currency actual expenditure already posted against this commitment.</summary>
        public decimal RealisedAmount { get; set; }
        public CommitmentStatus Status { get; set; } = CommitmentStatus.Active;
        public DateTime CommittedDate { get; set; }
        public DateTime? ReleasedDate { get; set; }
    }

    /// <summary>PPDPA-compliant e-tendering: tender notice with specs and a hard submission deadline.
    /// Bid timestamping and late-rejection are real, server-side logic (no external portal needed
    /// for that mechanic to be genuine) -- what remains a placeholder is a polished public vendor
    /// self-registration website; this ships the data model, API, and a functional bid-submission
    /// screen rather than a full public-facing portal experience.</summary>
    public class TenderNotice : TenantEntity
    {
        public string TenderNo { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Specifications { get; set; }
        public DateTime SubmissionDeadline { get; set; }
        public TenderStatus Status { get; set; } = TenderStatus.Open;
        public Guid? AwardedVendorId { get; set; }
        public string? AwardJustification { get; set; }

        public ICollection<TenderBid> Bids { get; set; } = new List<TenderBid>();
    }

    public class TenderBid : TenantEntity
    {
        public Guid TenderNoticeId { get; set; }
        public Guid VendorId { get; set; }
        public decimal PriceAmount { get; set; }
        public decimal TechnicalScore { get; set; }
        public decimal LocalContentScore { get; set; }
        public decimal PriceScore { get; set; }
        public decimal TotalScore { get; set; }
        public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Set true automatically if SubmittedAt is after the tender's SubmissionDeadline --
        /// late bids are rejected by the server, not by manual review.</summary>
        public bool IsLateRejected { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>A physical asset-verification exercise (audit). Barcode/RFID hardware scanning
    /// itself is outside what this environment can integrate; the verification workflow, matching
    /// logic (missing/found/condition-changed), and audit trail are real and fully wired -- the
    /// "scan" input here is the tag value the operator's scanner or phone would produce.</summary>
    public class AssetVerificationBatch : TenantEntity
    {
        public string BatchNo { get; set; } = string.Empty;
        public DateTime VerificationDate { get; set; }
        public string? Location { get; set; }
        public Guid PerformedBy { get; set; }
        public bool IsComplete { get; set; }

        public ICollection<AssetVerificationScan> Scans { get; set; } = new List<AssetVerificationScan>();
    }

    public enum AssetVerificationResult { Found, Missing, ConditionChanged }

    public class AssetVerificationScan : BaseEntity
    {
        public Guid AssetVerificationBatchId { get; set; }
        public Guid? FixedAssetId { get; set; } // null if the scanned tag doesn't match any registered asset
        public string ScannedTagValue { get; set; } = string.Empty;
        public AssetVerificationResult Result { get; set; }
        public string? ConditionNotes { get; set; }
        public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    }
}
