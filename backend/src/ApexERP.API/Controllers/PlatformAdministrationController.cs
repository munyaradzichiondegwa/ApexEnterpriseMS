using System.Security.Cryptography;
using System.Text;
using ApexERP.Domain.Entities;
using ApexERP.Application.Security;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers;

/// <summary>
/// Separate control-plane API. Tenant SUPER_ADMIN is not sufficient: callers must receive
/// the platform_admin=true claim from a separately controlled platform identity provider.
/// </summary>
[ApiController]
[Route("api/platform")]
[Authorize(Policy = "PlatformAdmin")]
public sealed class PlatformAdministrationController : ControllerBase
{
    private readonly ApexErpDbContext _db;
    private readonly AuthService _auth;
    public PlatformAdministrationController(ApexErpDbContext db, AuthService auth) { _db = db; _auth = auth; }

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
        _db.TenantAdministratorInvitations.Add(new TenantAdministratorInvitation
        {
            TenantId = tenant.Id, CompanyId = company.Id, Email = request.AdministratorEmail.Trim().ToLowerInvariant(),
            TokenHash = tokenHash, ExpiresAt = DateTime.UtcNow.AddHours(48)
        });
        await _db.SaveChangesAsync(ct);
        await AuditAsync("tenant.provisioned", "CustomerOrganisation", tenant.Id.ToString(),
            new { companyId = company.Id, planId = plan.Id }, ct);
        await transaction.CommitAsync(ct);

        // Returned once to the authorised platform operator. Store only the hash; deliver through
        // a configured mail provider out-of-band. This endpoint never creates a password or user.
        return Created($"/api/platform/tenants/{tenant.Id}", new
        {
            tenantId = tenant.Id, companyId = company.Id, invitation = rawToken,
            invitationExpiresAt = DateTime.UtcNow.AddHours(48), onboardingStatus = "Provisioned"
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
        var allowed = new[] { "Active", "Suspended", "Cancelled" };
        if (!allowed.Contains(request.Status, StringComparer.Ordinal))
            return BadRequest(new { error = "Status must be Active, Suspended or Cancelled." });
        using var scope = _db.SystemAccessScope("platform admin: change subscription lifecycle");
        var sub = await _db.TenantSubscriptions.SingleOrDefaultAsync(
            x => x.Id == subscriptionId && x.TenantId == tenantId, ct);
        if (sub is null) return NotFound();
        sub.Status = request.Status;
        sub.SuspendedAt = request.Status == "Suspended" ? DateTime.UtcNow : null;
        sub.CancelledAt = request.Status == "Cancelled" ? DateTime.UtcNow : null;
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
        return Ok(new { accessToken = login.AccessToken, expiresAt = login.ExpiresAt, userId = login.UserId,
            tenantId = invitation.TenantId, companyId = invitation.CompanyId, role = login.Role, permissions = login.Permissions });
    }

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
public sealed record SubscriptionLifecycleRequest(string Status);
public sealed record AcceptInvitationRequest(string Token, string Email, string Password, string FirstName, string LastName);
