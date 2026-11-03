using System.Text.Json;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;

namespace ApexERP.Application.Services
{
    /// <summary>Writes immutable audit trail rows (never updated/deleted) capturing before/after
    /// JSON diffs for every sensitive action, as required by PRD Section 5 (audit_log) and Section 7
    /// (tamper-evident ZIMRA audit exports).</summary>
    public class AuditService
    {
        private readonly ApexErpDbContext _db;

        public AuditService(ApexErpDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Adds an audit row to the current unit of work WITHOUT saving, so it commits (or rolls back)
        /// together with the business change it describes (design document: audit metadata in the same
        /// transaction as the posting).
        /// </summary>
        public AuditLogEntry Record(Guid companyId, Guid? userId, string action, string module,
            string entityType, Guid entityId, object? before, object? after, string? ipAddress = null)
        {
            var entry = new AuditLogEntry
            {
                CompanyId = companyId,
                UserId = userId,
                IpAddress = ipAddress,
                Action = action,
                Module = module,
                EntityType = entityType,
                EntityId = entityId,
                BeforeValueJson = before == null ? null : JsonSerializer.Serialize(before),
                AfterValueJson = after == null ? null : JsonSerializer.Serialize(after),
                Timestamp = DateTime.UtcNow
            };
            _db.AuditLogs.Add(entry);
            return entry;
        }

        public async Task LogAsync(Guid companyId, Guid? userId, string? ipAddress, string action, string module,
            string entityType, Guid entityId, object? before, object? after, CancellationToken ct = default)
        {
            var entry = new AuditLogEntry
            {
                CompanyId = companyId,
                UserId = userId,
                IpAddress = ipAddress,
                Action = action,
                Module = module,
                EntityType = entityType,
                EntityId = entityId,
                BeforeValueJson = before == null ? null : JsonSerializer.Serialize(before),
                AfterValueJson = after == null ? null : JsonSerializer.Serialize(after),
                Timestamp = DateTime.UtcNow
            };
            _db.AuditLogs.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
    }
}
