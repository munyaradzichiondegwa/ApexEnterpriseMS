using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ApexERP.Infrastructure.Data
{
    /// <summary>
    /// Optional PostgreSQL row-level security (APEXERP spec 10.3). When Security:RowLevelSecurity:Enabled is true, every database
    /// connection is told which customer organisation and company the request belongs to ("app.tenant_id" and
    /// "app.company_id") and whether it is a system context
    /// ("app.rls_bypass" = on only inside an explicit <see cref="SystemAccessScope"/> or <see cref="CrossTenantScope"/>). The
    /// policies in backend/database/hardening/002_row_level_security.sql then make the database itself refuse rows from other
    /// companies, even if application code forgot a filter. Off by default; the application-level query filters always apply.
    /// </summary>
    public static class TenantSession
    {
        public static bool Enabled { get; set; }

        internal static (string CompanyId, string TenantId, string Bypass) ValuesFor(ApexErpDbContext db) =>
            (db.CurrentCompanyId?.ToString() ?? string.Empty,
             db.CurrentTenantId?.ToString() ?? string.Empty,
             db.AllowSystemAccess || db.AllowCrossTenant ? "on" : "off");

        public const string Sql = "SELECT set_config('app.company_id', @c, false), set_config('app.tenant_id', @t, false), set_config('app.rls_bypass', @b, false)";

        // Settings are session-scoped because ordinary read requests are not wrapped in transactions.
        // The interceptor re-stamps every pooled connection checkout and clears the session before it
        // is returned to Npgsql's pool, so a previous request's identity or bypass cannot survive reuse.
        public const string ResetSql = "SELECT set_config('app.company_id', '', false), set_config('app.tenant_id', '', false), set_config('app.rls_bypass', 'off', false)";

        internal static void Clear(DbConnection connection)
        {
            if (connection.State != ConnectionState.Open) return;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = ResetSql;
            cmd.ExecuteNonQuery();
        }

        internal static async Task ClearAsync(DbConnection connection)
        {
            if (connection.State != ConnectionState.Open) return;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = ResetSql;
            await cmd.ExecuteNonQueryAsync();
        }

        internal static void Apply(DbConnection connection, ApexErpDbContext db)
        {
            var (company, tenant, bypass) = ValuesFor(db);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql;
            AddParam(cmd, "@c", company); AddParam(cmd, "@t", tenant); AddParam(cmd, "@b", bypass);
            cmd.ExecuteNonQuery();
        }

        internal static async Task ApplyAsync(DbConnection connection, ApexErpDbContext db, CancellationToken ct)
        {
            var (company, tenant, bypass) = ValuesFor(db);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql;
            AddParam(cmd, "@c", company); AddParam(cmd, "@t", tenant); AddParam(cmd, "@b", bypass);
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
            var (company, tenant, bypass) = ValuesFor(db);
            await db.Database.ExecuteSqlRawAsync(Sql.Replace("@c", "{0}").Replace("@t", "{1}").Replace("@b", "{2}"), new object[] { company, tenant, bypass }, ct);
        }

        public static void RefreshSync(ApexErpDbContext db)
        {
            if (!Enabled) return;
            var (company, tenant, bypass) = ValuesFor(db);
            db.Database.ExecuteSqlRaw(Sql.Replace("@c", "{0}").Replace("@t", "{1}").Replace("@b", "{2}"), company, tenant, bypass);
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

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            if (TenantSession.Enabled && eventData.Context is ApexErpDbContext) TenantSession.Clear(connection);
            return result;
        }

        public override async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            if (TenantSession.Enabled && eventData.Context is ApexErpDbContext) await TenantSession.ClearAsync(connection);
            return result;
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

    /// <summary>
    /// Grants a narrowly scoped, explicitly named platform operation access to rows across tenant boundaries.
    /// This replaces the unsafe assumption that a missing company identity automatically means system access.
    /// </summary>
    public sealed class SystemAccessScope : IDisposable
    {
        private readonly ApexErpDbContext _db;
        private readonly bool _previous;
        private readonly string? _previousReason;

        public SystemAccessScope(ApexErpDbContext db, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A system-access reason is required.", nameof(reason));

            _db = db;
            _previous = db.AllowSystemAccess;
            _previousReason = db.SystemAccessReason;
            db.AllowSystemAccess = true;
            db.SystemAccessReason = reason;
            TenantSession.RefreshSync(db);
        }

        public void Dispose()
        {
            _db.AllowSystemAccess = _previous;
            _db.SystemAccessReason = _previousReason;
            TenantSession.RefreshSync(_db);
        }
    }

    public static class CrossTenantExtensions
    {
        public static CrossTenantScope CrossTenantScope(this ApexErpDbContext db) => new(db);
        public static SystemAccessScope SystemAccessScope(this ApexErpDbContext db, string reason) => new(db, reason);
    }
}
