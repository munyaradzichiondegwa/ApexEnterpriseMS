using Microsoft.EntityFrameworkCore;

namespace ApexERP.Infrastructure.Data;

/// <summary>
/// Revalidates the signed tenant/company/role claims against current database state on every request.
/// This makes membership revocation, role changes, account deactivation and tenant suspension effective
/// immediately rather than waiting for an access token to expire.
/// </summary>
public static class TenantMembershipValidator
{
    public static async Task<bool> IsValidAsync(
        ApexErpDbContext db,
        Guid userId,
        Guid tenantId,
        Guid companyId,
        Guid tokenRoleId,
        CancellationToken cancellationToken = default)
    {
        using var systemAccess = db.SystemAccessScope("tenant resolution: revalidate signed user, company and role claims");
        var now = DateTime.UtcNow;

        var tenantIsActive = await db.CustomerOrganisations.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == tenantId && x.IsActive, cancellationToken);
        if (!tenantIsActive) return false;

        var targetCompanyIsActive = await db.Companies.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == companyId && x.TenantId == tenantId && x.IsActive, cancellationToken);
        if (!targetCompanyIsActive) return false;

        var user = await db.Users.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (user is null) return false;

        var homeTenantId = await db.Companies.IgnoreQueryFilters()
            .Where(x => x.Id == user.CompanyId)
            .Select(x => x.TenantId)
            .SingleOrDefaultAsync(cancellationToken);
        if (homeTenantId != tenantId) return false;

        Guid? currentRoleId;
        if (user.CompanyId == companyId)
        {
            currentRoleId = user.RoleId;
        }
        else
        {
            currentRoleId = await db.CompanyMemberships.IgnoreQueryFilters()
                .Where(x => x.UserId == userId && x.CompanyId == companyId && x.IsActive &&
                    (x.ValidFrom == null || x.ValidFrom <= now) &&
                    (x.ValidTo == null || x.ValidTo > now))
                .Select(x => x.RoleId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (!currentRoleId.HasValue || currentRoleId.Value != tokenRoleId) return false;
        return await db.Roles.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == tokenRoleId && x.CompanyId == companyId, cancellationToken);
    }
}
