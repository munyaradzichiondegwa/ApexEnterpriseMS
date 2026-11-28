using System;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    public enum TaxConfigurationStatus { Draft, PendingApproval, Approved, Rejected }
    public enum TaxBudgetTreatment { IncludeInBudgetActual, ExcludeFromBudgetActual, SeparateClassification }
    public enum SettlementComponentKind { InvoiceLine, TaxComponent }

    /// <summary>Company-owned, effective-dated tax control-account mapping. Approved versions are immutable.</summary>
    public class TaxAccountMapping : TenantEntity
    {
        public TaxType TaxType { get; set; }
        public Guid? TaxCodeId { get; set; }
        public Guid GlAccountId { get; set; }
        public int VersionNumber { get; set; } = 1;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public TaxConfigurationStatus Status { get; set; } = TaxConfigurationStatus.Draft;
        public Guid? ApprovalWorkflowId { get; set; }
        public Guid? SubmittedBy { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
    }

    /// <summary>Immutable invoice tax component linked to its exact posted journal line and mapping version.</summary>
    public class InvoiceTaxComponent : TenantEntity
    {
        public string InvoiceKind { get; set; } = string.Empty; // AR | AP
        public Guid InvoiceId { get; set; }
        public Guid? InvoiceLineId { get; set; }
        public Guid? TaxCodeId { get; set; }
        public TaxType TaxType { get; set; }
        public decimal TaxableBase { get; set; }
        public decimal Rate { get; set; }
        public decimal Amount { get; set; }
        public Guid GlAccountId { get; set; }
        public Guid TaxAccountMappingId { get; set; }
        public int MappingVersionNumber { get; set; }
        public Guid JournalEntryId { get; set; }
        public Guid JournalLineId { get; set; }
    }

    /// <summary>Independently approved, effective-dated budget treatment for a tax component.</summary>
    public class PublicSectorTaxBudgetPolicy : TenantEntity
    {
        public TaxType TaxType { get; set; }
        public Guid? TaxCodeId { get; set; }
        public TaxBudgetTreatment Treatment { get; set; }
        public Guid? BudgetGlAccountId { get; set; }
        public Guid? FundCentreId { get; set; }
        public Guid? CostCentreId { get; set; }
        public int VersionNumber { get; set; } = 1;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public TaxConfigurationStatus Status { get; set; } = TaxConfigurationStatus.Draft;
        public Guid? ApprovalWorkflowId { get; set; }
        public Guid? SubmittedBy { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
    }

    /// <summary>Explicit allocation of a cash settlement to an invoice revenue/expense line or tax component.</summary>
    public class SettlementComponentAllocation : TenantEntity
    {
        public string SettlementKind { get; set; } = string.Empty; // AR | AP
        public Guid SettlementAllocationId { get; set; }
        public Guid InvoiceId { get; set; }
        public SettlementComponentKind ComponentKind { get; set; }
        public Guid? InvoiceLineId { get; set; }
        public Guid? TaxComponentId { get; set; }
        public decimal AmountApplied { get; set; }
    }
}
