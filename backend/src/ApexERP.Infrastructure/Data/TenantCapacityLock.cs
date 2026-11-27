using Microsoft.EntityFrameworkCore;

namespace ApexERP.Infrastructure.Data;

/// <summary>
/// Serialises tenant seat/company capacity decisions across API instances. The lock is
/// transaction-scoped, so it is released automatically on commit or rollback.
/// </summary>
public static class TenantCapacityLock
{
    public static async Task AcquireTenantCapacityLockAsync(
        this ApexErpDbContext db, Guid tenantId, string purpose, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return;

        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Tenant capacity checks must run inside the request transaction.");

        var lockKey = $"{tenantId:N}:{purpose}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
    }
}
