using System.Text.Json;
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
        string tokenRoleName,
        string tokenPermissionsJson,
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
        if (user is null || (user.LockedUntil.HasValue && user.LockedUntil.Value > now)) return false;

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
        var role = await db.Roles.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == tokenRoleId && x.CompanyId == companyId, cancellationToken);
        if (role is null || !string.Equals(role.Name, tokenRoleName, StringComparison.Ordinal)) return false;
        return PermissionsMatch(role.PermissionsJson, tokenPermissionsJson);
    }

    private static bool PermissionsMatch(string? currentJson, string? tokenJson)
    {
        try
        {
            var current = JsonSerializer.Deserialize<string[]>(currentJson ?? "[]");
            var token = JsonSerializer.Deserialize<string[]>(tokenJson ?? "");
            if (current is null || token is null) return false;
            return new HashSet<string>(current, StringComparer.OrdinalIgnoreCase)
                .SetEquals(token);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
