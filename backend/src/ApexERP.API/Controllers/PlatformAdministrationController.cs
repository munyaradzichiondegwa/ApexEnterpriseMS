using System.Security.Cryptography;
using System.Text;
using ApexERP.Domain.Entities;
using ApexERP.Application.Security;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ApexERP.API.Authorization;
using ApexERP.API.Services;
using System.Globalization;
using System.Text.Json;

namespace ApexERP.API.Controllers;

/// <summary>
/// Separate control-plane API. Tenant SUPER_ADMIN is not sufficient: callers must receive
/// the platform_admin=true claim from a separately controlled platform identity provider.
/// </summary>
[ApiController]
[Route("api/platform")]
[Authorize(AuthenticationSchemes = "PlatformBearer", Policy = "PlatformAdmin")]
[AuthenticatedOnly]
public sealed class PlatformAdministrationController : ControllerBase
{
    private readonly ApexErpDbContext _db;
    private readonly AuthService _auth;
    private readonly IEmailDeliveryService _email;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    public PlatformAdministrationController(ApexErpDbContext db, AuthService auth, IEmailDeliveryService email, IConfiguration configuration, IWebHostEnvironment environment) { _db = db; _auth = auth; _email = email; _configuration = configuration; _environment = environment; }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: list subscription plans");
        return Ok(await _db.SubscriptionPlans.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
    }

    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlan([FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name) ||
            request.MaxSeats < 1 || request.MaxCompanies < 1)
            return BadRequest(new { error = "Code, name and positive seat/company limits are required." });

        using var scope = _db.SystemAccessScope("platform admin: create subscription plan");
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.SubscriptionPlans.AnyAsync(x => x.Code == code, ct))
            return Conflict(new { error = "A plan with this code already exists." });

        var plan = new SubscriptionPlan
        {
            Code = code, Name = request.Name.Trim(), Description = request.Description ?? "",
            MaxSeats = request.MaxSeats, MaxCompanies = request.MaxCompanies,
            ModuleCodesJson = System.Text.Json.JsonSerializer.Serialize(request.ModuleCodes ?? Array.Empty<string>()),
            MonthlyPrice = request.MonthlyPrice, Currency = (request.Currency ?? "USD").ToUpperInvariant(),
            IsPubliclyAvailable = request.IsPubliclyAvailable, IsActive = true
        };
        _db.SubscriptionPlans.Add(plan);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("subscription-plan.created", "SubscriptionPlan", plan.Id.ToString(), null, ct);
        return Created($"/api/platform/plans/{plan.Id}", new { plan.Id, plan.Code });
    }

    [HttpPost("tenants")]
    public async Task<IActionResult> ProvisionTenant([FromBody] ProvisionTenantRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OrganisationName) || string.IsNullOrWhiteSpace(request.InitialCompanyName) ||
            string.IsNullOrWhiteSpace(request.AdministratorEmail))
            return BadRequest(new { error = "Organisation, initial company and administrator email are required." });
        if (request.FiscalYearStartMonth is < 1 or > 12)
            return BadRequest(new { error = "Fiscal year start month must be between 1 and 12." });

        using var scope = _db.SystemAccessScope("platform admin: provision customer tenant");
        var plan = await _db.SubscriptionPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId && x.IsActive, ct);
        if (plan is null) return BadRequest(new { error = "An active subscription plan is required." });

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var tenant = new CustomerOrganisation
        {
            Name = request.OrganisationName.Trim(), LegalName = request.LegalName?.Trim(), IsActive = true
        };
        var company = new Company
        {
            TenantId = tenant.Id, Name = request.InitialCompanyName.Trim(),
            BaseCurrency = (request.Currency ?? "USD").ToUpperInvariant(),
            FunctionalCurrency = (request.Currency ?? "USD").ToUpperInvariant(),
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            Timezone = request.TimeZone ?? "UTC", Sector = request.Sector
        };
        _db.CustomerOrganisations.Add(tenant);
        _db.Companies.Add(company);
        var adminRole = new Role { CompanyId = company.Id, Name = "Administrator", IsSystemRole = true, PermissionsJson = "[\\"*\\"]" };
        _db.Roles.Add(adminRole);
        _db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenant.Id, PlanId = plan.Id, Status = request.TrialDays > 0 ? "Trial" : "Active",
            StartsAt = DateTime.UtcNow, TrialEndsAt = request.TrialDays > 0 ? DateTime.UtcNow.AddDays(request.TrialDays) : null,
            CurrentPeriodEndsAt = request.TrialDays > 0 ? DateTime.UtcNow.AddDays(request.TrialDays) : null
        });
        _db.TenantOnboardings.Add(new TenantOnboarding { TenantId = tenant.Id, Status = "Provisioned" });
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        var adminInvitation = new TenantAdministratorInvitation
        {
            TenantId = tenant.Id, CompanyId = company.Id, Email = request.AdministratorEmail.Trim().ToLowerInvariant(),
            TokenHash = tokenHash, ExpiresAt = DateTime.UtcNow.AddHours(48)
        };
        _db.TenantAdministratorInvitations.Add(adminInvitation);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.provisioned", "CustomerOrganisation", tenant.Id.ToString(),
            new { companyId = company.Id, planId = plan.Id }, ct);
        await transaction.CommitAsync(ct);

        var emailSent = false;
        try
        {
            emailSent = await _email.SendAsync(adminInvitation.Email, "ApexERP organisation administrator invitation",
                $"You have been invited as the initial administrator for {tenant.Name}.\n\nInvitation token: {rawToken}\n\nAccept it at the configured ApexERP onboarding page within 48 hours.");
        }
        catch { emailSent = false; }
        return Created($"/api/platform/tenants/{tenant.Id}", new
        {
            tenantId = tenant.Id, companyId = company.Id, invitationToken = emailSent ? null : rawToken,
            invitationEmailSent = emailSent, invitationExpiresAt = adminInvitation.ExpiresAt, onboardingStatus = "Provisioned"
        });
    }

    [HttpGet("tenants/{tenantId:guid}")]
    public async Task<IActionResult> TenantOverview(Guid tenantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: view tenant operations");
        var tenant = await _db.CustomerOrganisations.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == tenantId)
            .Select(x => new { x.Id, x.Name, x.LegalName, x.IsActive })
            .SingleOrDefaultAsync(ct);
        if (tenant is null) return NotFound();
        var companies = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.Name, x.IsActive }).ToListAsync(ct);
        var subscription = await _db.TenantSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.PlanId, x.Status, x.TrialEndsAt, x.CurrentPeriodEndsAt, x.SuspendedAt, x.CancelledAt })
            .FirstOrDefaultAsync(ct);
        var onboarding = await _db.TenantOnboardings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Status, x.CompletedStepsJson, x.CompletedAt, x.BlockedReason })
            .SingleOrDefaultAsync(ct);
        return Ok(new { tenant, companies, subscription, onboarding });
    }

    [HttpPost("tenants/{tenantId:guid}/subscriptions/{subscriptionId:guid}/lifecycle")]
    public async Task<IActionResult> ChangeSubscription(Guid tenantId, Guid subscriptionId,
        [FromBody] SubscriptionLifecycleRequest request, CancellationToken ct)
    {
        var allowed = new[] { "Trial", "Active", "Suspended", "Cancelled" };
        if (!allowed.Contains(request.Status, StringComparer.Ordinal))
            return BadRequest(new { error = "Status must be Active, Suspended or Cancelled." });
        using var scope = _db.SystemAccessScope("platform admin: change subscription lifecycle");
        var sub = await _db.TenantSubscriptions.SingleOrDefaultAsync(
            x => x.Id == subscriptionId && x.TenantId == tenantId, ct);
        if (sub is null) return NotFound();
        sub.Status = request.Status;
        sub.SuspendedAt = request.Status == "Suspended" ? DateTime.UtcNow : null;
        sub.CancelledAt = request.Status == "Cancelled" ? DateTime.UtcNow : null;
        if (request.Status == "Active") sub.TrialEndsAt = null;
        if (request.Status == "Trial") sub.TrialEndsAt = request.PeriodEndsAt ?? DateTime.UtcNow.AddDays(30);
        if (request.PeriodEndsAt.HasValue) sub.CurrentPeriodEndsAt = request.PeriodEndsAt.Value;
        if (request.Status == "Trial" && !request.PeriodEndsAt.HasValue) sub.CurrentPeriodEndsAt = sub.TrialEndsAt;
        if (request.PlanId.HasValue)
        {
            if (!await _db.SubscriptionPlans.AnyAsync(x => x.Id == request.PlanId && x.IsActive, ct)) return BadRequest(new { error = "The selected plan is not active." });
            sub.PlanId = request.PlanId.Value;
        }
        if (request.SeatOverride.HasValue) { if (request.SeatOverride < 1) return BadRequest(new { error = "Seat override must be positive." }); sub.SeatOverride = request.SeatOverride.Value; }
        if (request.CompanyOverride.HasValue) { if (request.CompanyOverride < 1) return BadRequest(new { error = "Company override must be positive." }); sub.CompanyOverride = request.CompanyOverride.Value; }
        await _db.SaveChangesAsync(ct);
        await AuditAsync("subscription.status-changed", "TenantSubscription", sub.Id.ToString(),
            new { status = sub.Status }, ct);
        return Ok(new { sub.Id, sub.Status, sub.SuspendedAt, sub.CancelledAt });
    }


    [HttpPost("invitations/accept")]
    [AllowAnonymous]
    public async Task<IActionResult> AcceptAdministratorInvitation([FromBody] AcceptInvitationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            return BadRequest(new { error = "Invitation token, email, first name and last name are required." });
        var passwordProblem = PasswordPolicy.Validate(request.Password, request.Email);
        if (passwordProblem != null) return BadRequest(new { error = passwordProblem });

        using var scope = _db.SystemAccessScope("onboarding: accept initial administrator invitation");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
        var invitation = await _db.TenantAdministratorInvitations.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (invitation is null || invitation.AcceptedAt != null || invitation.ExpiresAt <= DateTime.UtcNow ||
            !string.Equals(invitation.Email, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Invitation is invalid, expired or already used." });

        var subscription = await _db.TenantSubscriptions.Where(x => x.TenantId == invitation.TenantId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (subscription is null || subscription.Status is not ("Active" or "Trial") ||
            (subscription.TrialEndsAt.HasValue && subscription.TrialEndsAt.Value <= DateTime.UtcNow) ||
            (subscription.CurrentPeriodEndsAt.HasValue && subscription.CurrentPeriodEndsAt.Value <= DateTime.UtcNow))
            return StatusCode(402, new { error = "The tenant subscription is inactive or expired." });
        var plan = await _db.SubscriptionPlans.SingleAsync(x => x.Id == subscription.PlanId, ct);
        var seatLimit = subscription.SeatOverride > 0 ? subscription.SeatOverride : plan.MaxSeats;
        if (seatLimit < 1) return StatusCode(402, new { error = "The subscription has no administrator seat available." });
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == invitation.Email, ct))
            return Conflict(new { error = "An account with this email already exists. Ask the platform administrator to issue a user invitation." });

        var role = await _db.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.CompanyId == invitation.CompanyId && x.Name == "Administrator" && x.IsSystemRole, ct);
        if (role is null) return StatusCode(500, new { error = "Initial administrator role is missing." });

        var user = new User
        {
            CompanyId = invitation.CompanyId, Email = invitation.Email,
            PasswordHash = AuthService.HashPassword(request.Password), FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(), RoleId = role.Id, IsActive = true
        };
        _db.Users.Add(user);
        _db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = invitation.CompanyId, UserId = user.Id, MembershipRole = "Administrator",
            IsDefault = true, IsActive = true, ValidFrom = DateTime.UtcNow, RoleId = role.Id
        });
        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.AcceptedUserId = user.Id;
        var onboarding = await _db.TenantOnboardings.SingleAsync(x => x.TenantId == invitation.TenantId, ct);
        onboarding.Status = "AdministratorCreated";
        onboarding.CompletedStepsJson = System.Text.Json.JsonSerializer.Serialize(new[] { "tenant-provisioned", "initial-company-created", "initial-administrator-created" });
        await _db.SaveChangesAsync(ct);
        var login = await _auth.IssueTokensAsync(user, HttpContext.Connection.RemoteIpAddress?.ToString(), null, ct);
        var sameSite = Enum.TryParse<SameSiteMode>(_configuration["Auth:CookieSameSite"], true, out var mode) ? mode : SameSiteMode.Strict;
        Response.Cookies.Append(AuthController.RefreshCookie, login.RefreshToken, new CookieOptions
        {
            HttpOnly = true, Secure = !_environment.IsDevelopment() || Request.IsHttps, SameSite = sameSite,
            Expires = DateTimeOffset.UtcNow.Add(AuthService.RefreshLifetime), Path = "/", IsEssential = true
        });
        return Ok(_configuration.GetValue("Auth:RefreshTokenCookieOnly", true) ? login with { RefreshToken = string.Empty } : login);
    }


    [HttpGet("tenants")]
    public async Task<IActionResult> Tenants([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        using var scope = _db.SystemAccessScope("platform admin: list customer tenants");
        var total = await _db.CustomerOrganisations.IgnoreQueryFilters().CountAsync(ct);
        var rows = await _db.CustomerOrganisations.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Name)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Name, x.LegalName, x.IsActive })
            .ToListAsync(ct);
        var tenantIds = rows.Select(x => x.Id).ToArray();
        var counts = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId.HasValue && tenantIds.Contains(x.TenantId.Value))
            .GroupBy(x => x.TenantId!.Value).Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);
        var items = rows.Select(x => new { x.Id, x.Name, x.LegalName, x.IsActive,
            CompanyCount = counts.TryGetValue(x.Id, out var count) ? count : 0 });
        return Ok(new { page, pageSize, total, items });
    }

    [HttpGet("deployment-profiles")]
    public async Task<IActionResult> PlatformDeploymentProfiles(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: list deployment templates");
        return Ok(await _db.DeploymentProfiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
    }

    [HttpPost("deployment-profiles")]
    public async Task<IActionResult> CreateDeploymentProfile([FromBody] DeploymentProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Sector)) return BadRequest(new { error = "Code, name and sector are required." });
        try { using var _ = System.Text.Json.JsonDocument.Parse(request.ConfigurationJson ?? "{}"); }
        catch { return BadRequest(new { error = "ConfigurationJson must be valid JSON." }); }
        using var scope = _db.SystemAccessScope("platform admin: create deployment template");
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.DeploymentProfiles.AnyAsync(x => x.Code == code, ct)) return Conflict();
        var row = new DeploymentProfile { Code = code, Name = request.Name.Trim(), Sector = request.Sector.Trim().ToUpperInvariant(),
            ConfigurationJson = request.ConfigurationJson ?? "{}", IsActive = true };
        _db.DeploymentProfiles.Add(row);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("deployment-profile.created", "DeploymentProfile", row.Id.ToString(), null, ct);
        return Created($"/api/platform/deployment-profiles/{row.Id}", new { row.Id, row.Code });
    }

    [HttpGet("jurisdictions")]
    public async Task<IActionResult> PlatformJurisdictions(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: list jurisdiction catalogue");
        return Ok(await _db.JurisdictionProfiles.AsNoTracking().OrderBy(x => x.CountryName).ToListAsync(ct));
    }

    [HttpPost("jurisdictions")]
    public async Task<IActionResult> CreateJurisdiction([FromBody] JurisdictionProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CountryCode) || request.CountryCode.Trim().Length != 2 ||
            string.IsNullOrWhiteSpace(request.CountryName) || string.IsNullOrWhiteSpace(request.DefaultCurrency) ||
            request.DefaultCurrency.Trim().Length != 3)
            return BadRequest(new { error = "ISO country code, country name and ISO currency are required." });
        using var scope = _db.SystemAccessScope("platform admin: register jurisdiction defaults");
        var code = request.CountryCode.Trim().ToUpperInvariant();
        if (await _db.JurisdictionProfiles.AnyAsync(x => x.CountryCode == code, ct)) return Conflict();
        var row = new JurisdictionProfile { CountryCode = code, CountryName = request.CountryName.Trim(),
            DefaultCurrency = request.DefaultCurrency.Trim().ToUpperInvariant(), DefaultTimeZone = request.DefaultTimeZone ?? "UTC",
            DefaultReportingFramework = request.DefaultReportingFramework ?? "IFRS", IsActive = true };
        _db.JurisdictionProfiles.Add(row);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("jurisdiction-profile.created", "JurisdictionProfile", row.Id.ToString(), null, ct);
        return Created($"/api/platform/jurisdictions/{row.Id}", new { row.Id, row.CountryCode });
    }

    [HttpPost("tenants/{tenantId:guid}/support-grants")]
    public async Task<IActionResult> RequestSupportGrant(Guid tenantId, [FromBody] SupportGrantRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.DurationMinutes is < 5 or > 240)
            return BadRequest(new { error = "A reason and duration between 5 and 240 minutes are required." });
        using var scope = _db.SystemAccessScope("platform admin: request time-limited customer support access");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var actor = CurrentActorId();
        var grant = new TenantSupportGrant { TenantId = tenantId, RequestedBy = actor, Reason = request.Reason.Trim(),
            Status = "PendingApproval", ExpiresAt = DateTime.UtcNow.AddMinutes(request.DurationMinutes) };
        _db.TenantSupportGrants.Add(grant);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("support-grant.requested", "TenantSupportGrant", grant.Id.ToString(), new { request.DurationMinutes }, ct);
        return Created($"/api/platform/tenants/{tenantId}/support-grants/{grant.Id}", new { grant.Id, grant.Status, grant.ExpiresAt });
    }

    [HttpPost("support-grants/{grantId:guid}/approve")]
    public async Task<IActionResult> ApproveSupportGrant(Guid grantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: approve customer support access");
        var grant = await _db.TenantSupportGrants.SingleOrDefaultAsync(x => x.Id == grantId, ct);
        if (grant is null) return NotFound();
        var actor = CurrentActorId();
        if (grant.RequestedBy == actor) return Forbid();
        if (grant.Status != "PendingApproval" || grant.ExpiresAt <= DateTime.UtcNow) return Conflict(new { error = "Grant is not pending or has expired." });
        grant.ApprovedBy = actor; grant.Status = "Approved";
        await _db.SaveChangesAsync(ct);
        await AuditAsync("support-grant.approved", "TenantSupportGrant", grant.Id.ToString(), null, ct);
        return Ok(new { grant.Id, grant.Status, grant.ExpiresAt });
    }

    [HttpPost("support-grants/{grantId:guid}/revoke")]
    public async Task<IActionResult> RevokeSupportGrant(Guid grantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: revoke customer support access");
        var grant = await _db.TenantSupportGrants.SingleOrDefaultAsync(x => x.Id == grantId, ct);
        if (grant is null) return NotFound();
        grant.Status = "Revoked"; grant.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("support-grant.revoked", "TenantSupportGrant", grant.Id.ToString(), null, ct);
        return Ok(new { grant.Id, grant.Status });
    }

    [HttpPost("tenants/{tenantId:guid}/operational-checks")]
    public async Task<IActionResult> RecordOperationalCheck(Guid tenantId, [FromBody] OperationalCheckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CheckCode) || !new[] { "Healthy", "Degraded", "Failed", "Unknown" }.Contains(request.Status))
            return BadRequest(new { error = "A check code and valid status are required." });
        using var scope = _db.SystemAccessScope("platform admin: record tenant operational diagnostic");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var row = new TenantOperationalCheck { TenantId = tenantId, CheckCode = request.CheckCode.Trim(),
            Status = request.Status, CheckedAt = DateTime.UtcNow, DurationMilliseconds = request.DurationMilliseconds,
            DetailsJson = request.DetailsJson };
        _db.TenantOperationalChecks.Add(row);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.operational-check.recorded", "TenantOperationalCheck", row.Id.ToString(), new { row.CheckCode, row.Status }, ct);
        return Ok(row);
    }

    [HttpPost("tenants/{tenantId:guid}/lifecycle-jobs")]
    public async Task<IActionResult> QueueLifecycleJob(Guid tenantId, [FromBody] LifecycleJobRequest request, CancellationToken ct)
    {
        var types = new[] { "Provision", "Diagnostics", "Backup", "Restore", "Export", "Offboard" };
        if (!types.Contains(request.JobType) || string.IsNullOrWhiteSpace(request.RequestedBy))
            return BadRequest(new { error = "A supported job type and requester are required." });
        using var scope = _db.SystemAccessScope("platform admin: queue tenant lifecycle operation");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var providerReady = !string.IsNullOrWhiteSpace(_configuration["Operations:WorkerUrl"]) && !string.IsNullOrWhiteSpace(_configuration["Operations:WorkerSecret"]);
        var job = new TenantLifecycleJob { TenantId = tenantId, JobType = request.JobType,
            RequestedBy = request.RequestedBy.Trim(), Status = providerReady ? "Queued" : "AwaitingProvider" };
        _db.TenantLifecycleJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.lifecycle-job.queued", "TenantLifecycleJob", job.Id.ToString(), new { job.JobType }, ct);
        return Accepted(new { job.Id, job.Status, job.JobType });
    }



    [HttpPut("tenants/{tenantId:guid}/sla-policy")]
    public async Task<IActionResult> ConfigureSlaPolicy(Guid tenantId, [FromBody] SlaPolicyRequest request, CancellationToken ct)
    {
        if (request.AvailabilityTargetPercent is <= 0 or > 100 || request.ResponseTargetMinutes < 1 ||
            request.ResolutionTargetMinutes < request.ResponseTargetMinutes || request.MonitoringWindowDays is < 1 or > 90)
            return BadRequest(new { error = "SLA targets are outside permitted ranges." });
        try { using var _ = JsonDocument.Parse(request.BusinessHoursJson ?? "{}"); }
        catch { return BadRequest(new { error = "BusinessHoursJson must be valid JSON." }); }
        using var scope = _db.SystemAccessScope("platform admin: configure tenant SLA policy");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var policy = await _db.TenantSlaPolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (policy is null) { policy = new TenantSlaPolicy { TenantId = tenantId }; _db.TenantSlaPolicies.Add(policy); }
        policy.SupportTier = request.SupportTier ?? "Standard";
        policy.AvailabilityTargetPercent = request.AvailabilityTargetPercent;
        policy.ResponseTargetMinutes = request.ResponseTargetMinutes;
        policy.ResolutionTargetMinutes = request.ResolutionTargetMinutes;
        policy.MonitoringWindowDays = request.MonitoringWindowDays;
        policy.BusinessTimeZone = request.BusinessTimeZone ?? "UTC";
        policy.BusinessHoursJson = request.BusinessHoursJson ?? "{}";
        policy.IsActive = true;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.sla-policy.updated", "TenantSlaPolicy", policy.Id.ToString(), null, ct);
        return Ok(policy);
    }

    [HttpGet("tenants/{tenantId:guid}/sla")]
    public async Task<IActionResult> TenantSla(Guid tenantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: inspect tenant SLA performance");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var policy = await _db.TenantSlaPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (policy is null) return NotFound(new { error = "No SLA policy is configured for this tenant." });
        var since = DateTime.UtcNow.AddDays(-policy.MonitoringWindowDays);
        var checks = await _db.TenantOperationalChecks.AsNoTracking().Where(x => x.TenantId == tenantId && x.CheckedAt >= since).ToListAsync(ct);
        var totalChecks = checks.Count;
        var healthyChecks = checks.Count(x => x.Status == "Healthy");
        var availability = totalChecks == 0 ? (decimal?)null : Math.Round((decimal)healthyChecks / totalChecks * 100m, 3);
        var incidents = await _db.TenantSlaIncidents.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.OpenedAt).Take(500).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var incidentMetrics = incidents.Select(x => new
        {
            x.Id, x.IncidentNumber, x.Severity, x.Status, x.Summary, x.OpenedAt, x.FirstResponseAt, x.ResolvedAt,
            responseBreached = x.FirstResponseAt == null && now > x.OpenedAt.AddMinutes(x.ResponseTargetMinutes),
            resolutionBreached = x.ResolvedAt == null && now > x.OpenedAt.AddMinutes(x.ResolutionTargetMinutes),
            responseMinutes = x.FirstResponseAt.HasValue ? (double?)(x.FirstResponseAt.Value - x.OpenedAt).TotalMinutes : null,
            resolutionMinutes = x.ResolvedAt.HasValue ? (double?)(x.ResolvedAt.Value - x.OpenedAt).TotalMinutes : null
        }).ToList();
        return Ok(new { policy, windowStart = since, availabilityPercent = availability,
            availabilityTargetMet = availability.HasValue && availability.Value >= policy.AvailabilityTargetPercent,
            checks = new { total = totalChecks, healthy = healthyChecks, failed = checks.Count(x => x.Status == "Failed"), degraded = checks.Count(x => x.Status == "Degraded") },
            incidents = incidentMetrics,
            breachedResponseCount = incidentMetrics.Count(x => x.responseBreached),
            breachedResolutionCount = incidentMetrics.Count(x => x.resolutionBreached) });
    }

    [HttpPost("tenants/{tenantId:guid}/sla-incidents")]
    public async Task<IActionResult> CreateSlaIncident(Guid tenantId, [FromBody] CreateSlaIncidentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Summary) || !new[] { "Critical", "High", "Medium", "Low" }.Contains(request.Severity))
            return BadRequest(new { error = "Incident summary and valid severity are required." });
        using var scope = _db.SystemAccessScope("platform admin: open tenant SLA incident");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var policy = await _db.TenantSlaPolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive, ct);
        var incident = new TenantSlaIncident
        {
            TenantId = tenantId, IncidentNumber = $"INC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Severity = request.Severity, Summary = request.Summary.Trim(), Status = "Open",
            ResponseTargetMinutes = policy?.ResponseTargetMinutes ?? 240,
            ResolutionTargetMinutes = policy?.ResolutionTargetMinutes ?? 2880,
            AssignedTo = request.AssignedTo
        };
        _db.TenantSlaIncidents.Add(incident);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.sla-incident.opened", "TenantSlaIncident", incident.Id.ToString(), new { incident.IncidentNumber, incident.Severity }, ct);
        return Created($"/api/platform/sla-incidents/{incident.Id}", incident);
    }

    [HttpPut("sla-incidents/{incidentId:guid}")]
    public async Task<IActionResult> UpdateSlaIncident(Guid incidentId, [FromBody] UpdateSlaIncidentRequest request, CancellationToken ct)
    {
        if (!new[] { "Open", "Investigating", "Resolved" }.Contains(request.Status))
            return BadRequest(new { error = "Incident status must be Open, Investigating or Resolved." });
        using var scope = _db.SystemAccessScope("platform admin: update tenant SLA incident");
        var incident = await _db.TenantSlaIncidents.SingleOrDefaultAsync(x => x.Id == incidentId, ct);
        if (incident is null) return NotFound();
        if (incident.Status == "Resolved") return Conflict(new { error = "Resolved incidents are immutable." });
        incident.Status = request.Status;
        if (request.Status is "Investigating" or "Resolved") incident.FirstResponseAt ??= DateTime.UtcNow;
        if (request.Status == "Resolved") { incident.ResolvedAt = DateTime.UtcNow; incident.ResolutionNotes = request.ResolutionNotes; }
        if (request.AssignedTo.HasValue) incident.AssignedTo = request.AssignedTo;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.sla-incident.updated", "TenantSlaIncident", incident.Id.ToString(), new { request.Status }, ct);
        return Ok(incident);
    }

    [HttpGet("overview")]
    public async Task<IActionResult> PlatformOverview(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: platform operations overview");
        var tenants = await _db.CustomerOrganisations.IgnoreQueryFilters().CountAsync(ct);
        var activeTenants = await _db.CustomerOrganisations.IgnoreQueryFilters().CountAsync(x => x.IsActive, ct);
        var subscriptions = await _db.TenantSubscriptions.AsNoTracking().GroupBy(x => x.Status)
            .Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(ct);
        var failedJobs = await _db.TenantLifecycleJobs.AsNoTracking().CountAsync(x => x.Status == "Failed", ct);
        var pendingSupport = await _db.TenantSupportGrants.AsNoTracking().CountAsync(x => x.Status == "PendingApproval", ct);
        return Ok(new { tenants, activeTenants, subscriptions, failedLifecycleJobs = failedJobs, pendingSupportApprovals = pendingSupport });
    }

    [HttpPut("plans/{planId:guid}")]
    public async Task<IActionResult> UpdatePlan(Guid planId, [FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.MaxSeats < 1 || request.MaxCompanies < 1)
            return BadRequest(new { error = "Plan name and positive seat/company limits are required." });
        using var scope = _db.SystemAccessScope("platform admin: update subscription plan");
        var plan = await _db.SubscriptionPlans.SingleOrDefaultAsync(x => x.Id == planId, ct);
        if (plan is null) return NotFound();
        plan.Name = request.Name.Trim(); plan.Description = request.Description ?? "";
        plan.MaxSeats = request.MaxSeats; plan.MaxCompanies = request.MaxCompanies;
        plan.ModuleCodesJson = System.Text.Json.JsonSerializer.Serialize(request.ModuleCodes ?? Array.Empty<string>());
        plan.MonthlyPrice = request.MonthlyPrice; plan.Currency = (request.Currency ?? "USD").ToUpperInvariant();
        plan.IsPubliclyAvailable = request.IsPubliclyAvailable;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("subscription-plan.updated", "SubscriptionPlan", plan.Id.ToString(), null, ct);
        return Ok(new { plan.Id, plan.Code, plan.MaxSeats, plan.MaxCompanies });
    }

    [HttpGet("tenants/{tenantId:guid}/operational-checks")]
    public async Task<IActionResult> OperationalChecks(Guid tenantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: inspect tenant operational checks");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        var rows = await _db.TenantOperationalChecks.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CheckedAt).Take(200).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("tenants/{tenantId:guid}/lifecycle-jobs")]
    public async Task<IActionResult> LifecycleJobs(Guid tenantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: inspect tenant lifecycle jobs");
        if (!await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct)) return NotFound();
        return Ok(await _db.TenantLifecycleJobs.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct));
    }


    [HttpPost("lifecycle-jobs/{jobId:guid}/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> LifecycleJobCallback(Guid jobId, CancellationToken ct)
    {
        var secret = _configuration["Operations:WorkerSecret"];
        if (string.IsNullOrWhiteSpace(secret)) return StatusCode(503, new { error = "Operations callback authentication is not configured." });
        var timestamp = Request.Headers["X-Apex-Timestamp"].ToString();
        var signature = Request.Headers["X-Apex-Signature"].ToString();
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - epoch) > 300 || string.IsNullOrWhiteSpace(signature))
            return Unauthorized();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        var expected = TenantLifecycleDispatcher.Sign(secret, timestamp + "\n" + body);
        byte[] suppliedBytes;
        try { suppliedBytes = Convert.FromHexString(signature); } catch { return Unauthorized(); }
        var expectedBytes = Convert.FromHexString(expected);
        if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
            return Unauthorized();
        JsonDocument payload;
        try { payload = JsonDocument.Parse(body); } catch { return BadRequest(new { error = "Callback body must be valid JSON." }); }
        using (payload)
        {
            var root = payload.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status is not ("Running" or "Succeeded" or "Failed")) return BadRequest(new { error = "Callback status must be Running, Succeeded or Failed." });
            using var scope = _db.SystemAccessScope("operations worker: authenticated lifecycle callback");
            var job = await _db.TenantLifecycleJobs.SingleOrDefaultAsync(x => x.Id == jobId, ct);
            if (job is null) return NotFound();
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return Conflict(new { error = "Lifecycle job is already terminal." });
            job.Status = status;
            if (status == "Running") job.StartedAt ??= DateTime.UtcNow;
            if (status is "Succeeded" or "Failed") job.CompletedAt = DateTime.UtcNow;
            if (root.TryGetProperty("externalJobReference", out var reference)) job.ExternalJobReference = reference.GetString();
            if (root.TryGetProperty("resultSummary", out var summary)) job.ResultSummary = summary.GetString();
            await _db.SaveChangesAsync(ct);
            await AuditAsync("tenant.lifecycle-job.worker-callback", "TenantLifecycleJob", job.Id.ToString(), new { status }, ct);
            return Ok(new { job.Id, job.Status, job.CompletedAt });
        }
    }

    [HttpPut("lifecycle-jobs/{jobId:guid}")]
    public async Task<IActionResult> UpdateLifecycleJob(Guid jobId, [FromBody] LifecycleJobStatusRequest request, CancellationToken ct)
    {
        var transitions = new[] { "Dispatched", "Running", "Succeeded", "Failed", "Cancelled" };
        if (!transitions.Contains(request.Status)) return BadRequest(new { error = "Unsupported lifecycle job status." });
        using var scope = _db.SystemAccessScope("platform admin: update tenant lifecycle job");
        var job = await _db.TenantLifecycleJobs.SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null) return NotFound();
        if (job.Status is "Succeeded" or "Failed" or "Cancelled") return Conflict(new { error = "Terminal lifecycle jobs cannot be changed." });
        if (request.Status == "Running") { job.StartedAt ??= DateTime.UtcNow; }
        if (request.Status is "Succeeded" or "Failed" or "Cancelled") job.CompletedAt = DateTime.UtcNow;
        job.Status = request.Status; job.ExternalJobReference = request.ExternalJobReference ?? job.ExternalJobReference;
        job.ResultSummary = request.ResultSummary;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.lifecycle-job.status-changed", "TenantLifecycleJob", job.Id.ToString(), new { request.Status }, ct);
        return Ok(new { job.Id, job.Status, job.StartedAt, job.CompletedAt });
    }

    [HttpGet("support-grants/{grantId:guid}/diagnostics")]
    public async Task<IActionResult> SupportDiagnostics(Guid grantId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("platform admin: access approved time-limited support diagnostics");
        var grant = await _db.TenantSupportGrants.SingleOrDefaultAsync(x => x.Id == grantId, ct);
        if (grant is null) return NotFound();
        if (grant.Status != "Approved" || grant.RevokedAt.HasValue || grant.ExpiresAt <= DateTime.UtcNow)
            return Forbid();
        var checks = await _db.TenantOperationalChecks.AsNoTracking().Where(x => x.TenantId == grant.TenantId)
            .OrderByDescending(x => x.CheckedAt).Take(100).ToListAsync(ct);
        var jobs = await _db.TenantLifecycleJobs.AsNoTracking().Where(x => x.TenantId == grant.TenantId)
            .OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct);
        await AuditAsync("support-grant.diagnostics-accessed", "TenantSupportGrant", grant.Id.ToString(), null, ct);
        return Ok(new { grant.TenantId, grant.ExpiresAt, checks, jobs });
    }

    private Guid CurrentActorId() => Guid.TryParse(User.FindFirst("sub")?.Value, out var id)
        ? id : throw new UnauthorizedAccessException("Platform actor identity is missing.");

    private async Task AuditAsync(string action, string targetType, string targetId, object? details, CancellationToken ct)
    {
        _db.PlatformAuditEvents.Add(new PlatformAuditEvent
        {
            ActorUserId = Guid.TryParse(User.FindFirst("sub")?.Value, out var actor) ? actor : null,
            Action = action, TargetType = targetType, TargetId = targetId,
            CorrelationId = HttpContext.TraceIdentifier,
            DetailsJson = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details)
        });
        await _db.SaveChangesAsync(ct);
    }
}

public sealed record CreatePlanRequest(string Code, string Name, string? Description, int MaxSeats,
    int MaxCompanies, string[]? ModuleCodes, decimal? MonthlyPrice, string? Currency, bool IsPubliclyAvailable);
public sealed record ProvisionTenantRequest(string OrganisationName, string? LegalName, string InitialCompanyName,
    string AdministratorEmail, Guid PlanId, int TrialDays = 30, int FiscalYearStartMonth = 1,
    string? Currency = "USD", string? TimeZone = "UTC", string? Sector = null);
public sealed record SubscriptionLifecycleRequest(string Status, DateTime? PeriodEndsAt = null, Guid? PlanId = null, int? SeatOverride = null, int? CompanyOverride = null);
public sealed record DeploymentProfileRequest(string Code, string Name, string Sector, string? ConfigurationJson = "{}");
public sealed record JurisdictionProfileRequest(string CountryCode, string CountryName, string DefaultCurrency, string? DefaultTimeZone = "UTC", string? DefaultReportingFramework = "IFRS");
public sealed record SupportGrantRequest(string Reason, int DurationMinutes = 60);
public sealed record OperationalCheckRequest(string CheckCode, string Status, long? DurationMilliseconds = null, string? DetailsJson = null);
public sealed record LifecycleJobRequest(string JobType, string RequestedBy);
public sealed record LifecycleJobStatusRequest(string Status, string? ExternalJobReference = null, string? ResultSummary = null);
public sealed record SlaPolicyRequest(string? SupportTier, decimal AvailabilityTargetPercent, int ResponseTargetMinutes, int ResolutionTargetMinutes, int MonitoringWindowDays = 30, string? BusinessTimeZone = "UTC", string? BusinessHoursJson = "{}");
public sealed record CreateSlaIncidentRequest(string Severity, string Summary, Guid? AssignedTo = null);
public sealed record UpdateSlaIncidentRequest(string Status, string? ResolutionNotes = null, Guid? AssignedTo = null);
public sealed record AcceptInvitationRequest(string Token, string Email, string Password, string FirstName, string LastName);
