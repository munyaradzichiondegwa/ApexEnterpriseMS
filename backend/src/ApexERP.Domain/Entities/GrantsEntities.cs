using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    /// <summary>Grant master record: donor, award, restrictions, indirect cost rate
    /// (PRD Module 15).</summary>
    public class Grant : TenantEntity
    {
        public string GrantNo { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public Guid DonorCustomerId { get; set; } // donor modelled as a Customer per PRD ("donor linked to CRM customer")
        public decimal AwardAmount { get; set; }
        public string Currency { get; set; } = "USD";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public decimal IndirectCostRatePct { get; set; }
        /// <summary>JSONB-equivalent: eligible cost categories, geographic restrictions, donor-specific rules.</summary>
        public string RestrictionsJson { get; set; } = "{}";
        /// <summary>If true, postings that would exceed the grant's GL-account budget are blocked outright.</summary>
        public bool EnforceRestriction { get; set; } = true;
        public GrantStatus Status { get; set; } = GrantStatus.Active;

        public ICollection<GrantBudgetLine> BudgetLines { get; set; } = new List<GrantBudgetLine>();
    }

    public class GrantBudgetLine : BaseEntity
    {
        public Guid GrantId { get; set; }
        public Guid GlAccountId { get; set; }
        public int PeriodYear { get; set; }
        public int? PeriodMonth { get; set; } // null = full-year budget line
        public decimal BudgetAmount { get; set; }
    }

    /// <summary>Disbursement tracking to an implementing partner (PRD: "sub-award management").</summary>
    public class SubAward : TenantEntity
    {
        public Guid GrantId { get; set; }
        public string SubAwardeeName { get; set; } = string.Empty;
        public Guid? SubAwardeeVendorId { get; set; } // links to Vendor if also a procurement supplier
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string? ReportingRequirements { get; set; }
        public SubAwardStatus Status { get; set; } = SubAwardStatus.Active;
        public decimal TotalDisbursed { get; set; }
    }

    public class SubAwardDisbursement : BaseEntity
    {
        public Guid SubAwardId { get; set; }
        public Guid? ApPaymentId { get; set; }
        public DateTime DisbursementDate { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>Beneficiary register for programme delivery / donor-report demographic breakdowns.</summary>
    public class Beneficiary : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public string? IdNo { get; set; }
        public string? Location { get; set; }
        public string? Gender { get; set; }
        public string? VulnerabilityCategory { get; set; }
    }

    public class Distribution : TenantEntity
    {
        public Guid GrantId { get; set; }
        public Guid BeneficiaryId { get; set; }
        public DateTime DistributionDate { get; set; }
        public string ItemsOrServices { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
    }

    /// <summary>Grant closeout checklist -- structured sign-off before a grant is marked Closed
    /// (PRD: "Finance Director acknowledges each item").</summary>
    public class GrantCloseoutChecklist : TenantEntity
    {
        public Guid GrantId { get; set; }
        public bool AllExpendituresPosted { get; set; }
        public bool SubAwardsClosed { get; set; }
        public bool IndirectCostCalculated { get; set; }
        public bool FinalDonorReportGenerated { get; set; }
        public bool SurplusIdentified { get; set; }
        public decimal? SurplusAmount { get; set; }
        public string? SurplusDisposition { get; set; } // "Refund to donor" | "Carry forward" | null
        public Guid? AcknowledgedBy { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
    }
}
