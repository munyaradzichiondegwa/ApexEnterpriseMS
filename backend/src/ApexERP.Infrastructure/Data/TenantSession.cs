using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ApexERP.Infrastructure.Data
{
    /// <summary>
    /// Optional PostgreSQL row-level security (APEXERP spec 10.3). When Security:RowLevelSecurity:Enabled is true, every database
    /// connection is told which company the request belongs to ("app.company_id") and whether it is a system context
    /// ("app.rls_bypass" = on: sign-in, registration, background scheduling, or an explicit <see cref="CrossTenantScope"/>). The
    /// policies in backend/database/hardening/002_row_level_security.sql then make the database itself refuse rows from other
    /// companies, even if application code forgot a filter. Off by default; the application-level query filters always apply.
    /// </summary>
    public static class TenantSession
    {
        public static bool Enabled { get; set; }

        internal static (string CompanyId, string Bypass) ValuesFor(ApexErpDbContext db) =>
            (db.CurrentCompanyId?.ToString() ?? string.Empty, db.CurrentCompanyId == null || db.AllowCrossTenant ? "on" : "off");

        public const string Sql = "SELECT set_config('app.company_id', @c, false), set_config('app.rls_bypass', @b, false)";

        internal static void Apply(DbConnection connection, ApexErpDbContext db)
        {
            var (company, bypass) = ValuesFor(db);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql;
            AddParam(cmd, "@c", company); AddParam(cmd, "@b", bypass);
            cmd.ExecuteNonQuery();
        }

        internal static async Task ApplyAsync(DbConnection connection, ApexErpDbContext db, CancellationToken ct)
        {
            var (company, bypass) = ValuesFor(db);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql;
            AddParam(cmd, "@c", company); AddParam(cmd, "@b", bypass);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static void AddParam(DbCommand cmd, string name, string value)
        {
            var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
        }

        /// <summary>Re-sends the settings on the connection already in use (after the company or cross-tenant flag changed mid-request).</summary>
        public static async Task RefreshAsync(ApexErpDbContext db, CancellationToken ct = default)
        {
            if (!Enabled) return;
            var (company, bypass) = ValuesFor(db);
            await db.Database.ExecuteSqlRawAsync(Sql.Replace("@c", "{0}").Replace("@b", "{1}"), new object[] { company, bypass }, ct);
        }

        public static void RefreshSync(ApexErpDbContext db)
        {
            if (!Enabled) return;
            var (company, bypass) = ValuesFor(db);
            db.Database.ExecuteSqlRaw(Sql.Replace("@c", "{0}").Replace("@b", "{1}"), company, bypass);
        }
    }

    /// <summary>Runs on every connection open and stamps the tenant settings before any query.</summary>
    public sealed class TenantSessionInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (TenantSession.Enabled && eventData.Context is ApexErpDbContext db) TenantSession.Apply(connection, db);
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (TenantSession.Enabled && eventData.Context is ApexErpDbContext db) await TenantSession.ApplyAsync(connection, db, cancellationToken);
        }
    }

    /// <summary>Marks a block of code that must read across companies. Disposing restores the caller's own company scope.</summary>
    public sealed class CrossTenantScope : IDisposable
    {
        private readonly ApexErpDbContext _db;
        private readonly bool _previous;

        public CrossTenantScope(ApexErpDbContext db)
        {
            _db = db; _previous = db.AllowCrossTenant;
            if (!_previous) { db.AllowCrossTenant = true; TenantSession.RefreshSync(db); }
        }

        public void Dispose()
        {
            if (_previous) return;
            _db.AllowCrossTenant = false;
            TenantSession.RefreshSync(_db);
        }
    }

    public static class CrossTenantExtensions
    {
        public static CrossTenantScope CrossTenantScope(this ApexErpDbContext db) => new(db);
    }
}
