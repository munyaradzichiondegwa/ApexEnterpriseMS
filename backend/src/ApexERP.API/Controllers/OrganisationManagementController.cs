using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ApexERP.Application.Security;
using ApexERP.API.Authorization;
using ApexERP.API.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers;

[Route("api/v1/organisation")]
[Authorize]
[AuthenticatedOnly]
public sealed class OrganisationManagementController : ApiControllerBase
{
    private readonly ApexErpDbContext _db;
    private readonly IEmailDeliveryService _email;
    public OrganisationManagementController(ApexErpDbContext db, IEmailDeliveryService email) { _db = db; _email = email; }

    private Guid TenantId => Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id)
        ? id : throw new UnauthorizedAccessException("Missing tenant context.");

    private bool CanManage => GrantedPermissions.Contains("*") ||
        GrantedPermissions.Contains("settings.write") ||
        string.Equals(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, "Administrator", StringComparison.OrdinalIgnoreCase);

    private IActionResult? DenyManagement() => CanManage ? null : Forbid();


    [HttpGet("subscription")]
    [AuthenticatedOnly]
    public async Task<IActionResult> Subscription(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant self-service: view subscription and usage");
        var sub = await _db.TenantSubscriptions.Where(x => x.TenantId == TenantId)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (sub is null) return NotFound();
        var plan = await _db.SubscriptionPlans.SingleOrDefaultAsync(x => x.Id == sub.PlanId, ct);
        var companies = await _db.Companies.IgnoreQueryFilters().CountAsync(x => x.TenantId == TenantId && x.IsActive, ct);
        var seats = await _db.CompanyMemberships.IgnoreQueryFilters()
            .Join(_db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == TenantId), m => m.CompanyId, x => x.Id, (m,x) => new { m.UserId, m.IsActive })
            .Where(x => x.IsActive).Select(x => x.UserId).Distinct().CountAsync(ct);
        return Ok(new { subscriptionId = sub.Id, status = sub.Status, plan = plan == null ? null : new {
            plan.Code, plan.Name, plan.MaxSeats, plan.MaxCompanies, plan.ModuleCodesJson, plan.MonthlyPrice, plan.Currency },
            trialEndsAt = sub.TrialEndsAt, currentPeriodEndsAt = sub.CurrentPeriodEndsAt,
            suspendedAt = sub.SuspendedAt, cancelledAt = sub.CancelledAt,
            usage = new { seats, companyCount = companies },
            limits = new { seats = sub.SeatOverride > 0 ? sub.SeatOverride : plan?.MaxSeats,
                companies = sub.CompanyOverride > 0 ? sub.CompanyOverride : plan?.MaxCompanies } });
    }

    [HttpGet("subscription/plans")]
    [AuthenticatedOnly]
    public async Task<IActionResult> AvailablePlans(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant self-service: list available plans");
        return Ok(await _db.SubscriptionPlans.AsNoTracking().Where(x => x.IsActive && x.IsPubliclyAvailable)
            .OrderBy(x => x.MonthlyPrice).Select(x => new { x.Id, x.Code, x.Name, x.Description,
                x.MaxSeats, x.MaxCompanies, x.ModuleCodesJson, x.MonthlyPrice, x.Currency }).ToListAsync(ct));
    }

    [HttpPost("subscription/renewal-request")]
    public async Task<IActionResult> RequestSubscriptionRenewal(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant self-service: request subscription renewal");
        var sub = await _db.TenantSubscriptions.Where(x => x.TenantId == TenantId)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (sub is null) return NotFound();
        if (sub.Status == "Cancelled") return Conflict(new { error = "A cancelled subscription must be reactivated by the platform administrator." });
        var pending = await _db.TenantLifecycleJobs.AnyAsync(x => x.TenantId == TenantId &&
            x.JobType == "Renewal" && (x.Status == "Queued" || x.Status == "AwaitingProvider" || x.Status == "Dispatching" || x.Status == "Dispatched" || x.Status == "Running"), ct);
        if (pending) return Conflict(new { error = "A renewal request is already pending." });
        var job = new TenantLifecycleJob { TenantId = TenantId, JobType = "Renewal", RequestedBy = UserId.ToString(), Status = "AwaitingProvider" };
        _db.TenantLifecycleJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return Accepted(new { requestId = job.Id, status = job.Status });
    }

    [HttpGet("companies")]
    public async Task<IActionResult> Companies(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant administration: list legal entities");
        var rows = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.RegistrationNo, x.BaseCurrency, x.FunctionalCurrency,
                x.FiscalYearStartMonth, x.Timezone, x.Sector, x.ParentCompanyId, x.OwnershipPercentage,
                x.IsPublicSectorEntity, x.AccountingBasis, x.IsActive })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("companies")]
    public async Task<IActionResult> CreateCompany([FromBody] CreateLegalCompanyRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(request.Name) || request.FiscalYearStartMonth is < 1 or > 12)
            return BadRequest(new { error = "A company name and valid fiscal year start month (1-12) are required." });

        using var scope = _db.SystemAccessScope("tenant administration: create legal entity");
        var sub = await _db.TenantSubscriptions.Where(x => x.TenantId == TenantId)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (sub is null || sub.Status is not ("Active" or "Trial") ||
            (sub.TrialEndsAt.HasValue && sub.TrialEndsAt.Value <= DateTime.UtcNow))
            return StatusCode(402, new { error = "The tenant subscription is not active." });
        var plan = await _db.SubscriptionPlans.SingleAsync(x => x.Id == sub.PlanId, ct);
        var limit = sub.CompanyOverride > 0 ? sub.CompanyOverride : plan.MaxCompanies;
        var count = await _db.Companies.IgnoreQueryFilters().CountAsync(x => x.TenantId == TenantId && x.IsActive, ct);
        if (count >= limit) return Conflict(new { error = "The subscription company limit has been reached.", limit });

        if (request.OwnershipPercentage.HasValue && (request.OwnershipPercentage < 0 || request.OwnershipPercentage > 100))
            return BadRequest(new { error = "Ownership percentage must be between 0 and 100." });
        if (request.ParentCompanyId.HasValue &&
            !await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == request.ParentCompanyId && x.TenantId == TenantId && x.IsActive, ct))
            return BadRequest(new { error = "The parent company must be an active legal entity in this tenant." });

        var sourceRoles = await _db.Roles.IgnoreQueryFilters().Where(x => x.CompanyId == CompanyId).AsNoTracking().ToListAsync(ct);
        if (sourceRoles.Count == 0) sourceRoles.Add(new Role { CompanyId = CompanyId, Name = "Administrator", IsSystemRole = true, PermissionsJson = "[\\"*\\"]" });
        var company = new Company
        {
            TenantId = TenantId, Name = request.Name.Trim(), RegistrationNo = request.RegistrationNo,
            BaseCurrency = (request.Currency ?? "USD").ToUpperInvariant(),
            FunctionalCurrency = (request.FunctionalCurrency ?? request.Currency ?? "USD").ToUpperInvariant(),
            FiscalYearStartMonth = request.FiscalYearStartMonth, Timezone = request.Timezone ?? "UTC",
            Sector = request.Sector, ParentCompanyId = request.ParentCompanyId,
            OwnershipPercentage = request.OwnershipPercentage, IsPublicSectorEntity = request.IsPublicSectorEntity
        };
        _db.Companies.Add(company);
        var roleMap = new Dictionary<Guid, Guid>();
        foreach (var oldRole in sourceRoles)
        {
            var newRole = new Role { CompanyId = company.Id, Name = oldRole.Name,
                IsSystemRole = oldRole.IsSystemRole, PermissionsJson = oldRole.PermissionsJson };
            _db.Roles.Add(newRole);
            roleMap[oldRole.Id] = newRole.Id;
        }
        var currentRoleId = Guid.TryParse(User.FindFirst("role_id")?.Value, out var rid) ? rid : sourceRoles.FirstOrDefault(x => x.Name == User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value)?.Id;
        Guid? targetRole = currentRoleId.HasValue && roleMap.TryGetValue(currentRoleId.Value, out var mapped)
            ? mapped : roleMap.Values.Cast<Guid?>().FirstOrDefault();
        if (targetRole == Guid.Empty) targetRole = null;
        _db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = company.Id, UserId = UserId, MembershipRole = "Administrator",
            RoleId = targetRole, IsDefault = false, IsActive = true, ValidFrom = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Companies), new { id = company.Id }, new { company.Id, company.Name });
    }

    [HttpGet("companies/{companyId:guid}/branches")]
    public async Task<IActionResult> Branches(Guid companyId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant administration: list branches");
        if (!await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == companyId && x.TenantId == TenantId, ct)) return NotFound();
        var rows = await _db.OrganisationalBranches.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.CompanyId == companyId).OrderBy(x => x.Code).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("companies/{companyId:guid}/branches")]
    public async Task<IActionResult> CreateBranch(Guid companyId, [FromBody] CreateBranchRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name)) return BadRequest();
        using var scope = _db.SystemAccessScope("tenant administration: create branch");
        if (!await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == companyId && x.TenantId == TenantId && x.IsActive, ct)) return NotFound();
        if (request.ParentBranchId.HasValue &&
            !await _db.OrganisationalBranches.IgnoreQueryFilters().AnyAsync(x => x.Id == request.ParentBranchId && x.CompanyId == companyId && x.IsActive, ct))
            return BadRequest(new { error = "Parent branch must belong to the same company." });
        var branch = new OrganisationalBranch { CompanyId = companyId, Code = request.Code.Trim().ToUpperInvariant(), Name = request.Name.Trim(), ParentBranchId = request.ParentBranchId };
        _db.OrganisationalBranches.Add(branch);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Branches), new { companyId }, new { branch.Id, branch.Code, branch.Name });
    }

    [HttpGet("companies/{companyId:guid}/members")]
    public async Task<IActionResult> Members(Guid companyId, CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant administration: list company memberships");
        if (!await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == companyId && x.TenantId == TenantId, ct)) return NotFound();
        var rows = await _db.CompanyMemberships.IgnoreQueryFilters().Where(x => x.CompanyId == companyId)
            .Join(_db.Users.IgnoreQueryFilters(), m => m.UserId, u => u.Id,
                (m,u) => new { m.Id, m.UserId, u.Email, u.FirstName, u.LastName, m.MembershipRole, m.RoleId, m.IsDefault, m.IsActive, m.ValidFrom, m.ValidTo })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("companies/{companyId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid companyId, [FromBody] AddCompanyMemberRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        using var scope = _db.SystemAccessScope("tenant administration: add company membership");
        if (!await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == companyId && x.TenantId == TenantId && x.IsActive, ct)) return NotFound();
        var user = await _db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == request.UserId && x.IsActive, ct);
        if (user is null) return NotFound(new { error = "User not found." });
        var userTenant = await _db.Companies.IgnoreQueryFilters().Where(x => x.Id == user.CompanyId).Select(x => x.TenantId).SingleOrDefaultAsync(ct);
        if (userTenant != TenantId) return BadRequest(new { error = "Membership cannot cross tenant boundaries." });
        var role = await _db.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == request.RoleId && x.CompanyId == companyId, ct);
        if (role is null) return BadRequest(new { error = "Role must belong to the target company." });
        var existingMembership = await _db.CompanyMemberships.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == user.Id, ct);
        if (existingMembership?.IsActive == true)
            return Conflict(new { error = "User is already an active member of this company." });

        var sub = await _db.TenantSubscriptions.Where(x => x.TenantId == TenantId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (sub is null || sub.Status is not ("Active" or "Trial")) return StatusCode(402, new { error = "Subscription is not active." });
        var plan = await _db.SubscriptionPlans.SingleAsync(x => x.Id == sub.PlanId, ct);
        var seatLimit = sub.SeatOverride > 0 ? sub.SeatOverride : plan.MaxSeats;
        var seats = await _db.CompanyMemberships.IgnoreQueryFilters().Where(x => x.IsActive)
            .Join(_db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == TenantId), m => m.CompanyId, c => c.Id, (m,c) => m.UserId)
            .Distinct().CountAsync(ct);
        if (seats >= seatLimit && !await _db.CompanyMemberships.IgnoreQueryFilters().AnyAsync(x => x.UserId == user.Id && x.IsActive, ct))
            return Conflict(new { error = "The subscription seat limit has been reached.", seatLimit });

        if (existingMembership is null)
            _db.CompanyMemberships.Add(new CompanyMembership { CompanyId = companyId, UserId = user.Id,
                MembershipRole = role.Name, RoleId = role.Id, IsActive = true, IsDefault = false, ValidFrom = DateTime.UtcNow });
        else { existingMembership.MembershipRole = role.Name; existingMembership.RoleId = role.Id;
            existingMembership.IsActive = true; existingMembership.ValidTo = null; existingMembership.ValidFrom = DateTime.UtcNow; }
        await _db.SaveChangesAsync(ct);
        return Ok(new { added = true, userId = user.Id, companyId, role = role.Name });
    }


    [HttpPost("companies/{companyId:guid}/invitations")]
    public async Task<IActionResult> InviteCompanyUser(Guid companyId, [FromBody] InviteCompanyUserRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(request.Email)) return BadRequest(new { error = "Email is required." });
        using var scope = _db.SystemAccessScope("tenant administration: invite company user");
        var company = await _db.Companies.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == TenantId && x.IsActive, ct);
        if (company is null) return NotFound();
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == email, ct))
            return Conflict(new { error = "This email already has an ApexERP account. Add the existing user through company membership." });
        var role = await _db.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == request.RoleId && x.CompanyId == companyId, ct);
        if (role is null) return BadRequest(new { error = "Role must belong to the target company." });
        if (await _db.CompanyUserInvitations.AnyAsync(x => x.TenantId == TenantId && x.Email == email && x.AcceptedAt == null && x.ExpiresAt > DateTime.UtcNow, ct))
            return Conflict(new { error = "A pending invitation already exists for this email in the tenant." });

        var subscription = await _db.TenantSubscriptions.Where(x => x.TenantId == TenantId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (subscription is null || subscription.Status is not ("Active" or "Trial"))
            return StatusCode(402, new { error = "The tenant subscription is inactive." });
        var plan = await _db.SubscriptionPlans.SingleAsync(x => x.Id == subscription.PlanId, ct);
        var seatLimit = subscription.SeatOverride > 0 ? subscription.SeatOverride : plan.MaxSeats;
        var activeSeats = await _db.CompanyMemberships.IgnoreQueryFilters()
            .Join(_db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == TenantId), m => m.CompanyId, x => x.Id, (m,x) => new { m.UserId, m.IsActive })
            .Where(x => x.IsActive).Select(x => x.UserId).Distinct().CountAsync(ct);
        var pendingSeats = await _db.CompanyUserInvitations.CountAsync(x => x.TenantId == TenantId && x.AcceptedAt == null && x.ExpiresAt > DateTime.UtcNow, ct);
        if (activeSeats + pendingSeats >= seatLimit) return Conflict(new { error = "The subscription seat limit has been reached.", seatLimit });

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        var invitation = new CompanyUserInvitation { TenantId = TenantId, CompanyId = companyId, RoleId = role.Id,
            Email = email, TokenHash = tokenHash, ExpiresAt = DateTime.UtcNow.AddHours(48), InvitedBy = UserId };
        _db.CompanyUserInvitations.Add(invitation);
        await _db.SaveChangesAsync(ct);
        var emailSent = false;
        try
        {
            emailSent = await _email.SendAsync(email, "ApexERP company access invitation",
                $"You have been invited to join {company.Name}.\n\nInvitation token: {rawToken}\n\nAccept it through the ApexERP onboarding page within 48 hours.");
        }
        catch { emailSent = false; }
        return Accepted(new { invitationId = invitation.Id, invitationToken = emailSent ? null : rawToken,
            invitationEmailSent = emailSent, invitation.ExpiresAt });
    }

    [HttpPut("members/{membershipId:guid}/deactivate")]
    public async Task<IActionResult> DeactivateMember(Guid membershipId, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        using var scope = _db.SystemAccessScope("tenant administration: deactivate company membership");
        var membership = await _db.CompanyMemberships.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == membershipId, ct);
        if (membership is null || !await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == membership.CompanyId && x.TenantId == TenantId, ct)) return NotFound();
        if (membership.UserId == UserId) return BadRequest(new { error = "Administrators cannot remove their own membership through this endpoint." });
        if (string.Equals(membership.MembershipRole, "Administrator", StringComparison.OrdinalIgnoreCase))
        {
            var otherAdmins = await _db.CompanyMemberships.IgnoreQueryFilters().CountAsync(x => x.CompanyId == membership.CompanyId && x.IsActive && x.Id != membership.Id && x.MembershipRole == "Administrator", ct);
            if (otherAdmins == 0) return Conflict(new { error = "The last active company administrator cannot be removed." });
        }
        membership.IsActive = false; membership.ValidTo = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { deactivated = true });
    }


    [HttpPut("companies/{companyId:guid}/departments/{departmentId:guid}/branch")]
    public async Task<IActionResult> AssignDepartmentBranch(Guid companyId, Guid departmentId, [FromBody] AssignBranchRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        using var scope = _db.SystemAccessScope("tenant administration: assign department to branch");
        if (!await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == companyId && x.TenantId == TenantId, ct)) return NotFound();
        var department = await _db.Departments.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == departmentId && x.CompanyId == companyId, ct);
        if (department is null) return NotFound();
        if (request.BranchId.HasValue && !await _db.OrganisationalBranches.IgnoreQueryFilters().AnyAsync(x => x.Id == request.BranchId && x.CompanyId == companyId && x.IsActive, ct))
            return BadRequest(new { error = "Branch must belong to the same company." });
        department.BranchId = request.BranchId;
        await _db.SaveChangesAsync(ct);
        return Ok(new { department.Id, department.BranchId });
    }

    [HttpPut("companies/{companyId:guid}/cost-centres/{costCentreId:guid}/branch")]
    public async Task<IActionResult> AssignCostCentreBranch(Guid companyId, Guid costCentreId, [FromBody] AssignBranchRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        using var scope = _db.SystemAccessScope("tenant administration: assign cost centre to branch");
        if (!await _db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Id == companyId && x.TenantId == TenantId, ct)) return NotFound();
        var costCentre = await _db.CostCentres.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == costCentreId && x.CompanyId == companyId, ct);
        if (costCentre is null) return NotFound();
        if (request.BranchId.HasValue && !await _db.OrganisationalBranches.IgnoreQueryFilters().AnyAsync(x => x.Id == request.BranchId && x.CompanyId == companyId && x.IsActive, ct))
            return BadRequest(new { error = "Branch must belong to the same company." });
        costCentre.BranchId = request.BranchId;
        await _db.SaveChangesAsync(ct);
        return Ok(new { costCentre.Id, costCentre.BranchId });
    }

    [HttpGet("jurisdictions")]
    public async Task<IActionResult> Jurisdictions(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant configuration: read jurisdictions");
        return Ok(await _db.TenantJurisdictions.AsNoTracking().Where(x => x.TenantId == TenantId).ToListAsync(ct));
    }

    [HttpPut("jurisdictions")]
    public async Task<IActionResult> ConfigureJurisdiction([FromBody] ConfigureJurisdictionRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(request.CountryCode) || request.CountryCode.Length != 2 || request.FiscalYearStartMonth is < 1 or > 12 ||
            string.IsNullOrWhiteSpace(request.FunctionalCurrency) || request.FunctionalCurrency.Length != 3) return BadRequest(new { error = "Country code, ISO currency and fiscal year are invalid." });
        using var scope = _db.SystemAccessScope("tenant configuration: set jurisdiction");
        var profile = await _db.JurisdictionProfiles.SingleOrDefaultAsync(x => x.CountryCode == request.CountryCode.ToUpperInvariant() && x.IsActive, ct);
        if (profile is null) return BadRequest(new { error = "The jurisdiction is not in the configured country catalogue." });
        var targetCompanyId = request.CompanyId ?? CompanyId;
        var targetCompany = await _db.Companies.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == targetCompanyId && x.TenantId == TenantId && x.IsActive, ct);
        if (targetCompany is null) return BadRequest(new { error = "The jurisdiction must be assigned to an active company in this tenant." });
        var row = await _db.TenantJurisdictions.SingleOrDefaultAsync(x => x.TenantId == TenantId && x.CompanyId == targetCompanyId && x.CountryCode == profile.CountryCode, ct);
        if (row is null) { row = new TenantJurisdiction { TenantId = TenantId, CompanyId = targetCompanyId, CountryCode = profile.CountryCode }; _db.TenantJurisdictions.Add(row); }
        row.FunctionalCurrency = request.FunctionalCurrency.ToUpperInvariant();
        row.FiscalYearStartMonth = request.FiscalYearStartMonth;
        row.ReportingFramework = request.ReportingFramework;
        row.TaxConfigurationJson = "{}"; // Tax packs require verified, effective-dated legal configuration.
        row.IsPrimary = request.IsPrimary;
        row.ConfigurationStatus = "PendingStatutoryReview";
        targetCompany.FunctionalCurrency = row.FunctionalCurrency;
        targetCompany.FiscalYearStartMonth = row.FiscalYearStartMonth;
        if (!string.IsNullOrWhiteSpace(request.Timezone)) targetCompany.Timezone = request.Timezone;
        if (request.IsPrimary)
        {
            var others = await _db.TenantJurisdictions.Where(x => x.TenantId == TenantId && x.CompanyId == targetCompanyId && x.Id != row.Id).ToListAsync(ct);
            foreach (var other in others) other.IsPrimary = false;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(row);
    }

    [HttpGet("deployment-profiles")]
    public async Task<IActionResult> DeploymentProfiles(CancellationToken ct)
    {
        using var scope = _db.SystemAccessScope("tenant configuration: list deployment profiles");
        return Ok(await _db.DeploymentProfiles.Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Code, x.Name, x.Sector, x.ConfigurationJson }).ToListAsync(ct));
    }

    [HttpPut("onboarding")]
    public async Task<IActionResult> UpdateOnboarding([FromBody] UpdateOnboardingRequest request, CancellationToken ct)
    {
        if (DenyManagement() is { } denied) return denied;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "organisation", "companies", "branches", "members", "jurisdiction", "modules", "workflow", "opening-balances", "review" };
        if (request.CompletedSteps is null || request.CompletedSteps.Any(x => !allowed.Contains(x))) return BadRequest(new { error = "Unknown or missing onboarding steps." });
        using var scope = _db.SystemAccessScope("tenant onboarding: update wizard progress");
        var row = await _db.TenantOnboardings.SingleOrDefaultAsync(x => x.TenantId == TenantId, ct);
        if (row is null) return NotFound();
        DeploymentProfile? selectedProfile = null;
        if (request.DeploymentProfileId.HasValue)
        {
            selectedProfile = await _db.DeploymentProfiles.SingleOrDefaultAsync(x => x.Id == request.DeploymentProfileId && x.IsActive, ct);
            if (selectedProfile is null) return BadRequest(new { error = "The selected deployment profile is unavailable." });
        }
        if (request.Completed)
        {
            var required = new[] { "organisation", "companies", "jurisdiction", "review" };
            if (required.Any(x => !request.CompletedSteps.Contains(x)))
                return BadRequest(new { error = "Organisation, companies, jurisdiction and review steps must be completed." });
            if (!request.DeploymentProfileId.HasValue)
                return BadRequest(new { error = "Select a deployment profile before completing onboarding." });
            var companyIds = await _db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == TenantId && x.IsActive).Select(x => x.Id).ToListAsync(ct);
            if (companyIds.Count == 0) return BadRequest(new { error = "At least one active legal company is required." });
            var configuredCompanyIds = await _db.TenantJurisdictions.Where(x => x.TenantId == TenantId && x.IsPrimary && x.CompanyId.HasValue)
                .Select(x => x.CompanyId!.Value).ToListAsync(ct);
            if (companyIds.Any(x => !configuredCompanyIds.Contains(x)))
                return BadRequest(new { error = "Every active legal company must have a primary jurisdiction configuration." });
            var hasAdminMembership = await _db.CompanyMemberships.IgnoreQueryFilters().AnyAsync(x => x.UserId == UserId && x.IsActive &&
                x.MembershipRole == "Administrator" && companyIds.Contains(x.CompanyId), ct);
            if (!hasAdminMembership) return BadRequest(new { error = "An active company administrator membership is required." });
        }
        row.CompletedStepsJson = JsonSerializer.Serialize(request.CompletedSteps.Distinct());
        row.DeploymentProfileId = request.DeploymentProfileId;
        if (selectedProfile is not null)
        {
            var tenantCompanies = await _db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == TenantId && x.IsActive).ToListAsync(ct);
            foreach (var company in tenantCompanies)
            {
                company.Sector = selectedProfile.Sector;
                company.IsPublicSectorEntity = selectedProfile.Sector is "PUBLIC_SECTOR" or "STATE_OWNED";
            }
        }
        row.Status = request.Completed ? "Completed" : "InProgress";
        row.CompletedAt = request.Completed ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Status, row.CompletedStepsJson, row.CompletedAt });
    }
}

public sealed record CreateLegalCompanyRequest(string Name, string? RegistrationNo, string? Currency = "USD",
    string? FunctionalCurrency = null, int FiscalYearStartMonth = 1, string? Timezone = "UTC",
    string? Sector = null, Guid? ParentCompanyId = null, decimal? OwnershipPercentage = null, bool IsPublicSectorEntity = false);
public sealed record CreateBranchRequest(string Code, string Name, Guid? ParentBranchId = null);
public sealed record AssignBranchRequest(Guid? BranchId);
public sealed record AddCompanyMemberRequest(Guid UserId, Guid RoleId);
public sealed record InviteCompanyUserRequest(string Email, Guid RoleId);
public sealed record ConfigureJurisdictionRequest(string CountryCode, string FunctionalCurrency, int FiscalYearStartMonth,
    string ReportingFramework, bool IsPrimary = false, Guid? CompanyId = null, string? Timezone = null);
public sealed record UpdateOnboardingRequest(string[] CompletedSteps, Guid? DeploymentProfileId = null, bool Completed = false);
