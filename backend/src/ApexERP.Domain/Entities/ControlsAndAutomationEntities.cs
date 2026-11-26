using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    // =================================================================================================
    // Request integrity and session security (spec 11, GL-007)
    // =================================================================================================

    /// <summary>A completed idempotent request. Replaying the same key returns the stored response instead of repeating the work.</summary>
    public class IdempotencyRecord : TenantEntity
    {
        public string Scope { get; set; } = string.Empty;        // "POST /api/v1/ap/payments"
        public string Key { get; set; } = string.Empty;          // client-supplied Idempotency-Key
        public string RequestHash { get; set; } = string.Empty;  // SHA-256 of the request body: the same key with a different body is refused
        public int ResponseStatus { get; set; }
        public string? ResponseBody { get; set; }
        public Guid? UserId { get; set; }
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
    }

    /// <summary>Opaque refresh token, stored only as a hash. Each use rotates it; reuse of a rotated token revokes the whole family.</summary>
    public class RefreshToken : TenantEntity
    {
        public Guid UserId { get; set; }
        public Guid FamilyId { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? ReplacedByTokenHash { get; set; }
        public string? CreatedByIp { get; set; }
        public string? RevokedReason { get; set; }
    }

    /// <summary>One-time MFA recovery code (stored hashed).</summary>
    public class MfaRecoveryCode : TenantEntity
    {
        public Guid UserId { get; set; }
        public string CodeHash { get; set; } = string.Empty;
        public DateTime? UsedAt { get; set; }
    }

    /// <summary>Per-company key/value settings (capitalisation threshold, tolerances, feature switches).</summary>
    public class CompanySetting : TenantEntity
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    // =================================================================================================
    // Governance (spec 7, 8, 9, 15)
    // =================================================================================================

    /// <summary>A pair of permissions one person must not hold together (spec 7.3). Tenant rules add to the built-in defaults.</summary>
    public class SoDConflictRule : TenantEntity
    {
        public string PermissionA { get; set; } = string.Empty;
        public string PermissionB { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    /// <summary>Delegation of authority and emergency access (spec 7.4). Always time-bound, always logged, always reviewed.</summary>
    public class ApprovalDelegation : TenantEntity
    {
        public Guid DelegatorUserId { get; set; }
        public Guid DelegateUserId { get; set; }
        public string? EntityType { get; set; }          // null = every type
        public decimal? MaxAmount { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime ValidTo { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool IsEmergency { get; set; }
        public DateTime? RevokedAt { get; set; }
        public Guid? ReviewedBy { get; set; }            // emergency access must be reviewed afterwards
        public DateTime? ReviewedAt { get; set; }
    }

    /// <summary>Delegation-of-authority tier: transactions up to <see cref="UpToAmount"/> need <see cref="RequiredRoleName"/> (spec 8.3).</summary>
    public class ApprovalMatrixTier : TenantEntity
    {
        public string EntityType { get; set; } = string.Empty;   // JournalEntry | ApPayment | ArCreditNote | InventoryWriteOff | ...
        public decimal UpToAmount { get; set; }                  // decimal.MaxValue-sized catch-all for the last tier
        public string RequiredRoleName { get; set; } = string.Empty;
        public int SlaHours { get; set; } = 24;
        public string? EscalateToRoleName { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>Versioned, approved materiality thresholds (spec 9.1). Changing them creates a new version.</summary>
    public class MaterialityConfiguration : TenantEntity
    {
        public int Version { get; set; } = 1;
        public string Currency { get; set; } = "USD";
        public decimal MediumThreshold { get; set; } = 1000m;
        public decimal HighThreshold { get; set; } = 10000m;
        public decimal CriticalThreshold { get; set; } = 100000m;
        public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// A control exception with a full life-cycle (spec 9.3): DETECTED -> CLASSIFIED -> ASSIGNED -> INVESTIGATED -> RESOLVED
    /// -> INDEPENDENTLY_REVIEWED -> CLOSED. Detectors create these; a unique fingerprint stops the same finding being raised twice.
    /// </summary>
    public class ControlException : TenantEntity
    {
        public string DetectorCode { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = "Low";             // Low | Medium | High | Critical
        public int RiskScore { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public Guid? SourceId { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = "DETECTED";
        public Guid? OwnerUserId { get; set; }
        public DateTime? DueDate { get; set; }
        public string? InvestigationNotes { get; set; }
        public string? ResolutionNote { get; set; }
        public Guid? ResolvedBy { get; set; }
        public Guid? ReviewedBy { get; set; }                     // must differ from ResolvedBy
        public DateTime? ClosedAt { get; set; }
    }

    // =================================================================================================
    // Accounting policy, accruals, prepayments, revenue (spec 4, 5, 13.5)
    // =================================================================================================

    public class AccountingPolicyVersion : TenantEntity
    {
        public string Area { get; set; } = string.Empty;          // "Depreciation", "RevenueRecognition", "ECL", ...
        public int Version { get; set; } = 1;
        public string Description { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public bool IsActive { get; set; }
    }

    public class AccrualSchedule : TenantEntity
    {
        public string Description { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public DateTime AccrualDate { get; set; }                  // period-end the expense is accrued to
        public Guid ExpenseAccountId { get; set; }
        public Guid AccruedLiabilityAccountId { get; set; }
        public Guid? SupplierId { get; set; }
        public bool AutoReverse { get; set; } = true;
        public string Status { get; set; } = "Draft";             // Draft | Approved | Posted | Reversed | Matched | Cancelled
        public Guid? ApprovedBy { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? ReversalJournalEntryId { get; set; }
        public Guid? MatchedApInvoiceId { get; set; }
        public Guid? CostCentreId { get; set; }
    }

    public class PrepaymentSchedule : TenantEntity
    {
        public string Description { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public string Currency { get; set; } = "USD";
        public DateTime StartDate { get; set; }
        public int Months { get; set; }
        public Guid PrepaidAssetAccountId { get; set; }
        public Guid ExpenseAccountId { get; set; }
        public Guid? SupplierId { get; set; }
        public string Status { get; set; } = "Draft";             // Draft | Approved | Active | Completed | Cancelled
        public Guid? ApprovedBy { get; set; }
        public Guid? CostCentreId { get; set; }
        public ICollection<PrepaymentScheduleLine> Lines { get; set; } = new List<PrepaymentScheduleLine>();
    }

    public class PrepaymentScheduleLine : BaseEntity
    {
        public Guid PrepaymentScheduleId { get; set; }
        public int Sequence { get; set; }
        public DateTime PeriodStart { get; set; }
        public decimal Amount { get; set; }
        public Guid? JournalEntryId { get; set; }
        public DateTime? PostedAt { get; set; }
    }

    /// <summary>IFRS 15 contract with a customer (spec 5): five-step model, simplified to the controls an auditor tests.</summary>
    public class RevenueContract : TenantEntity
    {
        public string ContractNo { get; set; } = string.Empty;
        public Guid CustomerId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public decimal TransactionPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public Guid ReceivableAccountId { get; set; }
        public Guid ContractLiabilityAccountId { get; set; }
        public Guid? ContractAssetAccountId { get; set; }
        public string Status { get; set; } = "Draft";             // Draft | Active | Modified | Completed | Cancelled
        public Guid? ModifiedFromContractId { get; set; }
        public string? ModificationNote { get; set; }
        public decimal BilledToDate { get; set; }
        public Guid? BillingJournalEntryId { get; set; }
        public Guid? ApprovedBy { get; set; }
        public ICollection<PerformanceObligation> Obligations { get; set; } = new List<PerformanceObligation>();
    }

    public class PerformanceObligation : BaseEntity
    {
        public Guid RevenueContractId { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal StandaloneSellingPrice { get; set; }
        public decimal AllocatedPrice { get; set; }                // transaction price allocated pro-rata to standalone selling price
        public string Pattern { get; set; } = "Ratable";          // Ratable (over time) | PointInTime
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public Guid RevenueAccountId { get; set; }
        public DateTime? SatisfiedOn { get; set; }                 // point-in-time obligations
        public Guid? SatisfactionJournalEntryId { get; set; }
        public ICollection<RevenueScheduleLine> Schedule { get; set; } = new List<RevenueScheduleLine>();
    }

    public class RevenueScheduleLine : BaseEntity
    {
        public Guid PerformanceObligationId { get; set; }
        public int Sequence { get; set; }
        public DateTime PeriodStart { get; set; }
        public decimal Amount { get; set; }
        public Guid? JournalEntryId { get; set; }
        public DateTime? RecognisedAt { get; set; }
    }

    // =================================================================================================
    // Assets, inventory, receivables (spec 6, 17, 19)
    // =================================================================================================

    /// <summary>IAS 36 impairment assessment with evidence and assumptions (spec 6).</summary>
    public class ImpairmentAssessment : TenantEntity
    {
        public Guid FixedAssetId { get; set; }
        public DateTime AssessmentDate { get; set; }
        public decimal CarryingAmount { get; set; }
        public decimal FairValueLessCostsToSell { get; set; }
        public decimal ValueInUse { get; set; }
        public decimal DiscountRatePct { get; set; }
        public decimal RecoverableAmount { get; set; }             // higher of the two
        public decimal ImpairmentLoss { get; set; }                // carrying - recoverable, never negative
        public string Indicators { get; set; } = string.Empty;
        public string Assumptions { get; set; } = string.Empty;
        public string Status { get; set; } = "Draft";             // Draft | Approved | Posted | Rejected
        public Guid? ApprovedBy { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? ImpairmentLossAccountId { get; set; }
    }

    /// <summary>Lower of cost and net realisable value (IAS 2) and approved write-offs (spec 19).</summary>
    public class InventoryNrvAssessment : TenantEntity
    {
        public Guid InventoryItemId { get; set; }
        public DateTime AssessmentDate { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal EstimatedSellingPrice { get; set; }
        public decimal CostsToSell { get; set; }
        public decimal NetRealisableValue { get; set; }            // per unit
        public decimal WriteDownAmount { get; set; }               // total
        public string Kind { get; set; } = "NrvWriteDown";        // NrvWriteDown | Obsolescence | WriteOff
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = "Draft";             // Draft | Approved | Posted | Rejected
        public Guid? PreparedBy { get; set; }
        public Guid? ApprovedBy { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? WriteDownExpenseAccountId { get; set; }
    }

    public class EclLossRate : TenantEntity
    {
        public int FromDays { get; set; }
        public int ToDays { get; set; }
        public decimal LossRatePct { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public Guid? ApprovedBy { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>IFRS 9 provision run: the required allowance, the change booked, and who approved it.</summary>
    public class EclProvisionRun : TenantEntity
    {
        public DateTime AsOfDate { get; set; }
        public decimal TotalReceivables { get; set; }
        public decimal RequiredAllowance { get; set; }
        public decimal PreviousAllowance { get; set; }
        public decimal Movement { get; set; }
        public string MatrixSnapshotJson { get; set; } = "[]";
        public string Status { get; set; } = "Draft";             // Draft | Approved | Posted
        public Guid? ApprovedBy { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? BadDebtExpenseAccountId { get; set; }
        public Guid? AllowanceAccountId { get; set; }
    }

    public class CreditLimitOverride : TenantEntity
    {
        public Guid CustomerId { get; set; }
        public decimal TemporaryLimit { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public Guid? ApprovedBy { get; set; }
    }

    public class ArCreditNote : TenantEntity
    {
        public string CreditNoteNo { get; set; } = string.Empty;
        public Guid ArInvoiceId { get; set; }
        public Guid CustomerId { get; set; }
        public decimal Amount { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = "Draft";             // Draft | Approved | Posted | Rejected
        public Guid? PreparedBy { get; set; }
        public Guid? ApprovedBy { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? RevenueReversalAccountId { get; set; }
        public Guid? ArControlAccountId { get; set; }
        public Guid? VatOutputAccountId { get; set; }
    }

    // =================================================================================================
    // Accounts payable controls (spec 16)
    // =================================================================================================

    public class ApPaymentRun : TenantEntity
    {
        public string RunNo { get; set; } = string.Empty;
        public DateTime PaymentDate { get; set; }
        public string Currency { get; set; } = "USD";
        public Guid BankGlAccountId { get; set; }
        public Guid ApControlAccountId { get; set; }
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } = "Draft";             // Draft | Authorised | Released | Cancelled
        public Guid? PreparedBy { get; set; }
        public Guid? AuthorisedBy { get; set; }
        public DateTime? AuthorisedAt { get; set; }
        public ICollection<ApPaymentRunItem> Items { get; set; } = new List<ApPaymentRunItem>();
    }

    public class ApPaymentRunItem : BaseEntity
    {
        public Guid ApPaymentRunId { get; set; }
        public Guid SupplierId { get; set; }
        public Guid ApInvoiceId { get; set; }
        public decimal Amount { get; set; }
        public Guid? ApPaymentId { get; set; }
    }

    public class SupplierBankChange : TenantEntity
    {
        public Guid SupplierId { get; set; }
        public string NewBankName { get; set; } = string.Empty;
        public string NewBankAccountNo { get; set; } = string.Empty;
        public string? NewBankBranch { get; set; }
        public Guid RequestedBy { get; set; }
        public string? VerificationNote { get; set; }              // e.g. "Called supplier on the number held on file"
        public Guid? VerifiedBy { get; set; }
        public string Status { get; set; } = "Pending";           // Pending | Verified | Rejected
    }

    // =================================================================================================
    // Bank, reconciliation, period close, evidence (spec 12, 13, 18, 24)
    // =================================================================================================

    public class BankReconciliation : TenantEntity
    {
        public Guid BankAccountId { get; set; }
        public DateTime StatementDate { get; set; }
        public decimal StatementBalance { get; set; }
        public decimal BookBalance { get; set; }
        public decimal UnreconciledItemsTotal { get; set; }
        public decimal Difference { get; set; }
        public Guid? PreparedBy { get; set; }
        public Guid? ReviewedBy { get; set; }                      // must differ from preparer
        public DateTime? CertifiedAt { get; set; }
        public string Status { get; set; } = "Draft";             // Draft | Certified | Rejected
        public string? Notes { get; set; }
    }

    public class PettyCashFund : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public Guid GlAccountId { get; set; }
        public decimal ImprestAmount { get; set; }
        public Guid? CustodianUserId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class PettyCashVoucher : TenantEntity
    {
        public Guid PettyCashFundId { get; set; }
        public DateTime VoucherDate { get; set; }
        public decimal Amount { get; set; }
        public Guid ExpenseAccountId { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = "Draft";             // Draft | Approved | Posted | Rejected
        public Guid? PreparedBy { get; set; }
        public Guid? ApprovedBy { get; set; }
        public Guid? JournalEntryId { get; set; }
    }

    /// <summary>Sub-ledger to general-ledger reconciliation (spec 13.3). One row per run; differences must be explained before sign-off.</summary>
    public class ReconciliationRun : TenantEntity
    {
        public string Area { get; set; } = string.Empty;          // AP | AR | Inventory | FixedAssets | Payroll | Bank
        public DateTime AsOfDate { get; set; }
        public decimal SubledgerTotal { get; set; }
        public decimal GlTotal { get; set; }
        public decimal Difference { get; set; }
        public string Status { get; set; } = "Unreconciled";      // Reconciled | Unreconciled | Explained | SignedOff
        public string? Explanation { get; set; }
        public Guid? RunBy { get; set; }
        public Guid? SignedOffBy { get; set; }
        public DateTime? SignedOffAt { get; set; }
    }

    public class PeriodCloseTask : TenantEntity
    {
        public Guid PeriodId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool IsAutomatic { get; set; }                      // evaluated by the system (e.g. "no draft journals")
        public string Status { get; set; } = "Open";              // Open | Done | Blocked | Waived
        public Guid? AssignedTo { get; set; }
        public Guid? CompletedBy { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>Supporting document attached to any record (spec 12.3). Content is stored with a SHA-256 so alteration is detectable.</summary>
    public class AuditEvidenceReference : TenantEntity
    {
        public string EntityType { get; set; } = string.Empty;
        public Guid EntityId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string? Description { get; set; }
        public Guid? UploadedBy { get; set; }
    }

    /// <summary>Result of an automated control self-test (spec 28.4 tests A-H, run on demand and on schedule).</summary>
    public class ControlTestResult : TenantEntity
    {
        public string TestCode { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public string Details { get; set; } = string.Empty;
        public DateTime RunAt { get; set; } = DateTime.UtcNow;
    }

    // =================================================================================================
    // Operations (spec 25, 26)
    // =================================================================================================

    /// <summary>Once-per-key record of a background job run, so a job can never apply the same period twice.</summary>
    public class JobExecution : TenantEntity
    {
        public string JobCode { get; set; } = string.Empty;
        public string RunKey { get; set; } = string.Empty;        // e.g. "2026-11" for a monthly job
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public string Status { get; set; } = "Running";           // Running | Succeeded | Failed
        public int Attempt { get; set; } = 1;
        public string? Message { get; set; }
    }

    /// <summary>Statutory payroll rate with effective dates (NSSA, AIDS levy, ZIMDEF, ...). Never hard-coded (spec 25.2).</summary>
    public class StatutoryRate : TenantEntity
    {
        public string Code { get; set; } = string.Empty;          // NSSA_EMPLOYEE | NSSA_EMPLOYER | AIDS_LEVY | ZIMDEF | ...
        public decimal RatePct { get; set; }
        public decimal? Ceiling { get; set; }                      // monthly insurable earnings ceiling
        public string Currency { get; set; } = "USD";
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string? SourceReference { get; set; }              // e.g. the statutory instrument or notice relied on
        public Guid? ApprovedBy { get; set; }
    }

    /// <summary>A configured external payment/bank integration. Secrets are referenced by name (environment), never stored here.</summary>
    public class IntegrationEndpoint : TenantEntity
    {
        public string Provider { get; set; } = string.Empty;      // EcoCash | OneMoney | PayNow | ZimSwitch | BankApi
        public string Name { get; set; } = string.Empty;
        public string? BaseUrl { get; set; }
        public string SecretName { get; set; } = string.Empty;    // name of the environment variable holding the webhook/API secret
        public bool IsActive { get; set; } = true;
        public string? SettlementBankGlAccountId { get; set; }
    }

    /// <summary>Inbound or outbound integration message with retry and dead-letter handling (spec 25.5).</summary>
    public class IntegrationMessage : TenantEntity
    {
        public string Provider { get; set; } = string.Empty;
        public string Direction { get; set; } = "Inbound";        // Inbound | Outbound
        public string ExternalRef { get; set; } = string.Empty;   // provider's transaction id: unique per provider, makes webhooks idempotent
        public string PayloadJson { get; set; } = "{}";
        public bool SignatureValid { get; set; }
        public string Status { get; set; } = "Pending";           // Pending | Processed | Failed | DeadLetter | Rejected
        public int Attempts { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string? LastError { get; set; }
        public Guid? ResultReceiptId { get; set; }
    }
}
