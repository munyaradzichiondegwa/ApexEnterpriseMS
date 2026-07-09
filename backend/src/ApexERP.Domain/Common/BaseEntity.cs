using System;

namespace ApexERP.Domain.Common
{
    /// <summary>
    /// Base class for every entity in the system. Provides the audit trail columns
    /// (created/updated by + timestamp) mandated by PRD Section 5 "Core Database Schema".
    /// </summary>
    public abstract class BaseEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public Guid? CreatedBy { get; set; }
        public Guid? UpdatedBy { get; set; }
    }

    /// <summary>
    /// Marker interface for every table that must be isolated per-tenant (company).
    /// EF Core global query filters in ApexErpDbContext use CompanyId to enforce
    /// row-level isolation equivalent to PostgreSQL RLS described in the PRD.
    /// </summary>
    public interface ITenantEntity
    {
        Guid CompanyId { get; set; }
    }

    public abstract class TenantEntity : BaseEntity, ITenantEntity
    {
        public Guid CompanyId { get; set; }
    }

    public enum GlAccountType
    {
        Asset,
        Liability,
        Equity,
        Income,
        Expense
    }

    public enum PeriodStatus
    {
        Open,
        Closed,
        Locked
    }

    public enum JournalSource
    {
        Manual,
        AR,
        AP,
        Payroll,
        Inventory,
        Assets,
        Bank,
        Procurement,
        Manufacturing
    }

    public enum JournalStatus
    {
        Draft,
        Posted,
        Reversed
    }

    public enum InvoiceStatus
    {
        Draft,
        Posted,
        Paid,
        Partial,
        Cancelled
    }

    public enum PaymentMethod
    {
        Cash,
        BankTransfer,
        EcoCash,
        OneMoney,
        Cheque,
        Card,
        Other
    }

    public enum ValuationMethod
    {
        FIFO,
        AVCO
    }

    public enum MovementType
    {
        GRN,
        Sale,
        Adjustment,
        Transfer,
        ProductionIssue,
        ProductionReceipt
    }

    public enum DepreciationMethod
    {
        StraightLine,
        ReducingBalance
    }

    public enum AssetStatus
    {
        Active,
        Disposed
    }

    public enum TaxType
    {
        VAT,
        WHT,
        DST
    }

    public enum PayrollRunStatus
    {
        Draft,
        Approved,
        Posted
    }

    public enum BomStatus
    {
        Draft,
        Active,
        Obsolete
    }

    public enum WorkOrderStatus
    {
        Draft,
        Released,
        InProgress,
        Completed,
        Cancelled
    }

    public enum PurchaseOrderStatus
    {
        Draft,
        Submitted,
        Approved,
        Partial,
        Fulfilled,
        Cancelled
    }

    public enum LeadStatus
    {
        New,
        Contacted,
        Qualified,
        Won,
        Lost
    }

    public enum SalesOrderStatus
    {
        Draft,
        Confirmed,
        Fulfilled,
        Invoiced,
        Cancelled
    }

    public enum ProjectStatus
    {
        Planning,
        Active,
        OnHold,
        Completed
    }

    public enum WorkflowStatus
    {
        Pending,
        Approved,
        Rejected
    }

    // ---------- Added for full IFRS statement suite / Group / Public Sector / Grants ----------

    /// <summary>Classifies a GL account for the indirect-method Statement of Cash Flows.</summary>
    public enum CashFlowCategory
    {
        NotApplicable,
        Operating,
        Investing,
        Financing,
        CashAndCashEquivalents
    }

    /// <summary>Marks whether a GL account's movements are Other Comprehensive Income (IAS 1) rather
    /// than profit or loss -- e.g. revaluation surplus, foreign operation translation reserve.</summary>
    public enum OciCategory
    {
        NotApplicable,
        AssetRevaluationSurplus,
        ForeignOperationTranslationReserve
    }

    public enum IntercompanyStatus
    {
        Matched,
        Unmatched,
        NettingPending,
        Settled
    }

    public enum FundCentreLevel
    {
        Vote,
        Program,
        SubProgram
    }

    public enum CommitmentStatus
    {
        Active,
        Released,
        Cancelled
    }

    public enum GrantStatus
    {
        Active,
        Suspended,
        Closed
    }

    public enum SubAwardStatus
    {
        Active,
        Closed
    }

    public enum TenderStatus
    {
        Open,
        Evaluation,
        Awarded,
        Cancelled
    }

    public enum AccountingBasis
    {
        Accrual,
        Cash
    }
}
