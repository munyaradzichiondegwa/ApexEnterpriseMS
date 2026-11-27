using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities;

/// <summary>Company-level organisational structure. Branches never create a new tenant.</summary>
public sealed class OrganisationalBranch : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? ParentBranchId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Explicit user-to-company authorisation. A user may hold memberships in many companies within one tenant.</summary>
public sealed class CompanyMembership : TenantEntity
{
    public Guid UserId { get; set; }
    public string MembershipRole { get; set; } = "Member";
    public Guid? RoleId { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
}

/// <summary>Reusable product plan; module codes are stored as JSON and validated by the application.</summary>
public sealed class SubscriptionPlan : BaseEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int MaxSeats { get; set; }
    public int MaxCompanies { get; set; }
    public string ModuleCodesJson { get; set; } = "[]";
    public decimal? MonthlyPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public bool IsPubliclyAvailable { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Tenant-level commercial lifecycle; never inferred from a user's company role.</summary>
public sealed class TenantSubscription : BaseEntity
{
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public string Status { get; set; } = "Trial";
    public DateTime StartsAt { get; set; } = DateTime.UtcNow;
    public DateTime? TrialEndsAt { get; set; }
    public DateTime? CurrentPeriodEndsAt { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? ExternalBillingReference { get; set; }
    public int SeatOverride { get; set; }
    public int CompanyOverride { get; set; }
}

/// <summary>Country defaults are reference data, not an assertion that a tax rule is legally current.</summary>
public sealed class JurisdictionProfile : BaseEntity
{
    public string CountryCode { get; set; } = "";
    public string CountryName { get; set; } = "";
    public string DefaultCurrency { get; set; } = "";
    public string DefaultTimeZone { get; set; } = "UTC";
    public string DefaultReportingFramework { get; set; } = "IFRS";
    public bool IsActive { get; set; } = true;
}

/// <summary>Tenant's chosen country, fiscal calendar and reporting configuration.</summary>
public sealed class TenantJurisdiction : BaseEntity
{
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string CountryCode { get; set; } = "";
    public string FunctionalCurrency { get; set; } = "USD";
    public int FiscalYearStartMonth { get; set; } = 1;
    public string ReportingFramework { get; set; } = "IFRS";
    public string TaxConfigurationJson { get; set; } = "{}";
    public bool IsPrimary { get; set; }
    public string ConfigurationStatus { get; set; } = "PendingReview";
}

/// <summary>Reusable sector deployment template. Selecting a profile does not fabricate statutory rules.</summary>
public sealed class DeploymentProfile : BaseEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sector { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}

/// <summary>Onboarding progress and explicit lifecycle state for each tenant.</summary>
public sealed class TenantOnboarding : BaseEntity
{
    public Guid TenantId { get; set; }
    public string Status { get; set; } = "Provisioned";
    public string CompletedStepsJson { get; set; } = "[]";
    public Guid? DeploymentProfileId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? BlockedReason { get; set; }
}

/// <summary>Invitation workflow for the first tenant administrator; tokens are stored as hashes only.</summary>
public sealed class TenantAdministratorInvitation : BaseEntity
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
}

/// <summary>Separate platform control-plane audit. This entity is intentionally not a tenant-owned business record.</summary>
public sealed class PlatformAuditEvent : BaseEntity
{
    public Guid? ActorUserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }
    public string Outcome { get; set; } = "Succeeded";
    public string? CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
}

/// <summary>Time-bounded, reason-coded support access request. Approval and execution are separate actions.</summary>
public sealed class TenantSupportGrant : BaseEntity
{
    public Guid TenantId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "Requested";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Operational check record; provider-specific monitoring can populate these records asynchronously.</summary>
public sealed class TenantOperationalCheck : BaseEntity
{
    public Guid TenantId { get; set; }
    public string CheckCode { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public long? DurationMilliseconds { get; set; }
    public string? DetailsJson { get; set; }
}

/// <summary>Tenant-level provisioning/restore job metadata. Secrets and backup payloads are never stored here.</summary>
public sealed class TenantLifecycleJob : BaseEntity
{
    public Guid TenantId { get; set; }
    public string JobType { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string RequestedBy { get; set; } = "";
    public string? ExternalJobReference { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
}

/// <summary>Expiring invitation for a tenant user to join a specific legal company and role.</summary>
public sealed class CompanyUserInvitation : BaseEntity
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid RoleId { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
}

/// <summary>Contracted service-level objectives for a tenant, measured from operational probes and incidents.</summary>
public sealed class TenantSlaPolicy : BaseEntity
{
    public Guid TenantId { get; set; }
    public string SupportTier { get; set; } = "Standard";
    public decimal AvailabilityTargetPercent { get; set; } = 99.5m;
    public int ResponseTargetMinutes { get; set; } = 240;
    public int ResolutionTargetMinutes { get; set; } = 2880;
    public int MonitoringWindowDays { get; set; } = 30;
    public string BusinessTimeZone { get; set; } = "UTC";
    public string BusinessHoursJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}

/// <summary>Support incident with response and resolution timestamps for SLA measurement.</summary>
public sealed class TenantSlaIncident : BaseEntity
{
    public Guid TenantId { get; set; }
    public string IncidentNumber { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    public string Status { get; set; } = "Open";
    public string Summary { get; set; } = "";
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FirstResponseAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int ResponseTargetMinutes { get; set; }
    public int ResolutionTargetMinutes { get; set; }
    public Guid? AssignedTo { get; set; }
    public string? ResolutionNotes { get; set; }
}
