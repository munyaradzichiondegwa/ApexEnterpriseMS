using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    /// <summary>The tenant. Every other business table hangs off Company via CompanyId.</summary>
    public class Company : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string? RegistrationNo { get; set; }
        public string? VatNo { get; set; }
        public string BaseCurrency { get; set; } = "USD";
        public string FunctionalCurrency { get; set; } = "USD";
        public int FiscalYearStartMonth { get; set; } = 1;
        public string? Address { get; set; }
        public string? LogoUrl { get; set; }
        public string Timezone { get; set; } = "Africa/Harare";
        public string? Sector { get; set; }
        public bool IsActive { get; set; } = true;

        /// <summary>Group/consolidation support (PRD Module 2). Null = this company is not a
        /// subsidiary of anything in this system (it may still be a standalone or a group parent).</summary>
        public Guid? ParentCompanyId { get; set; }
        /// <summary>Parent's ownership % of this company, e.g. 80.00 for an 80%-owned subsidiary.
        /// The remaining (100 - this) is the Non-Controlling Interest share on consolidation.</summary>
        public decimal? OwnershipPercentage { get; set; }

        /// <summary>IPSAS cash-basis vs IFRS accrual-basis presentation for Public Sector entities
        /// (PRD Module 16). Commercial entities should leave this as Accrual.</summary>
        public AccountingBasis AccountingBasis { get; set; } = AccountingBasis.Accrual;
        /// <summary>True for government/public-sector entities, enabling Fund Centre / commitment
        /// accounting screens and budget-vs-actual-vs-committed reporting.</summary>
        public bool IsPublicSectorEntity { get; set; }
        /// <summary>True for NGOs/donor-funded organisations, enabling the Grants & NGO module.</summary>
        public bool IsNgoEntity { get; set; }

        public ICollection<User> Users { get; set; } = new List<User>();
    }

    public class Role : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>JSONB list of permission keys e.g. "gl.journal.post", "ar.invoice.create".</summary>
        public string PermissionsJson { get; set; } = "[]";
        public bool IsSystemRole { get; set; }

        public ICollection<User> Users { get; set; } = new List<User>();
    }

    public class User : TenantEntity
    {
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string PasswordHash { get; set; } = string.Empty;
        public Guid RoleId { get; set; }
        public Role? Role { get; set; }
        public bool IsActive { get; set; } = true;
        public string? TotpSecret { get; set; }
        public bool TotpEnabled { get; set; }
        public DateTime? LastLogin { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
    }

    /// <summary>Immutable append-only audit trail. Never updated or deleted, only inserted.</summary>
    public class AuditLogEntry : TenantEntity
    {
        public Guid? UserId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? IpAddress { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Module { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public Guid EntityId { get; set; }
        public string? BeforeValueJson { get; set; }
        public string? AfterValueJson { get; set; }
    }

    /// <summary>Generic multi-step approval workflow (PO, journal entry, payroll, leave, etc).
    /// Supports up to 5 sequential tiers per PRD Module 16 (Public Sector governance requirement,
    /// applied generically to any entity type that needs multi-tier sign-off).</summary>
    public class WorkflowRequest : TenantEntity
    {
        public string RequestType { get; set; } = string.Empty;
        public Guid ReferenceId { get; set; }
        public string ReferenceTable { get; set; } = string.Empty;
        public Guid SubmittedBy { get; set; }
        public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;
        public Guid? CurrentApproverId { get; set; }
        public string? Notes { get; set; }

        /// <summary>Optional link to a configured multi-tier ApprovalChain. Null = simple single-approver flow.</summary>
        public Guid? ApprovalChainId { get; set; }
        public int CurrentTier { get; set; } = 1;
        public DateTime? CurrentTierStartedAt { get; set; }
        public ICollection<WorkflowApproval> Approvals { get; set; } = new List<WorkflowApproval>();
    }

    /// <summary>One tier's decision within a multi-tier WorkflowRequest -- preserves full approval
    /// history in the audit trail as required by PRD 16 ("Full approval history in audit log").</summary>
    public class WorkflowApproval : BaseEntity
    {
        public Guid WorkflowRequestId { get; set; }
        public int Tier { get; set; }
        public Guid ApproverId { get; set; }
        public bool Approved { get; set; }
        public string? Comments { get; set; }
        public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Configurable approval chain: entity type + amount threshold + up to 5 sequential
    /// tiers. E.g. "Purchase Order over $10,000 requires Dept Head -> Finance Director -> CFO".</summary>
    public class ApprovalChain : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty; // "PurchaseOrder", "JournalEntry", "ApPayment", etc.
        public decimal AmountThreshold { get; set; }
        public Guid? FundCentreId { get; set; } // optional: chain applies only within this fund centre (Public Sector)
        public bool IsActive { get; set; } = true;

        public ICollection<ApprovalChainTier> Tiers { get; set; } = new List<ApprovalChainTier>();
    }

    public class ApprovalChainTier : BaseEntity
    {
        public Guid ApprovalChainId { get; set; }
        public int TierOrder { get; set; } // 1-5
        public Guid ApproverRoleId { get; set; }
        public int SlaHours { get; set; } = 24;
    }
}
