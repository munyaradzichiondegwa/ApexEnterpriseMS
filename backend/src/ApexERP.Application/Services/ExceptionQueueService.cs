using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>
    /// The visible exception queue (design document: "exception management"; audit finding 8). A failed
    /// inter-company mirror, for example, becomes an owned, auditable item that can be retried or resolved,
    /// instead of an exception that is swallowed while the caller is told everything succeeded.
    /// Methods that raise or update items do NOT save: they join the caller's unit of work.
    /// </summary>
    public class ExceptionQueueService
    {
        private const int MaxMessageLength = 2000;
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;

        public ExceptionQueueService(ApexErpDbContext db, AuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        /// <summary>Raises an exception item, or refreshes the open one for the same source (no duplicates).</summary>
        public async Task<ExceptionItem> RaiseAsync(Guid companyId, ExceptionCategory category, string sourceType, Guid sourceId,
            string message, Guid? userId, CancellationToken ct = default)
        {
            var text = Truncate(message);
            var existing = await _db.ExceptionItems.IgnoreQueryFilters().FirstOrDefaultAsync(e =>
                e.CompanyId == companyId && e.Category == category && e.SourceType == sourceType &&
                e.SourceId == sourceId && e.Status == ExceptionStatus.Open, ct);

            if (existing != null)
            {
                existing.Message = text;
                existing.RetryCount++;
                existing.LastAttemptAt = DateTime.UtcNow;
                _audit.Record(companyId, userId, "Exception.RetryFailed", "Controls", nameof(ExceptionItem), existing.Id,
                    before: null, after: new { existing.RetryCount, message = text });
                return existing;
            }

            var item = new ExceptionItem
            {
                CompanyId = companyId,
                Category = category,
                SourceType = sourceType,
                SourceId = sourceId,
                Message = text,
                Status = ExceptionStatus.Open,
                OwnerUserId = userId,
                LastAttemptAt = DateTime.UtcNow
            };
            _db.ExceptionItems.Add(item);
            _audit.Record(companyId, userId, "Exception.Raised", "Controls", nameof(ExceptionItem), item.Id,
                before: null, after: new { category = category.ToString(), sourceType, sourceId, message = text });
            return item;
        }

        /// <summary>Closes any open item for this source once the underlying work has succeeded.</summary>
        public async Task ResolveForSourceAsync(Guid companyId, ExceptionCategory category, string sourceType, Guid sourceId,
            Guid? userId, string note, CancellationToken ct = default)
        {
            var open = await _db.ExceptionItems.IgnoreQueryFilters().Where(e =>
                e.CompanyId == companyId && e.Category == category && e.SourceType == sourceType &&
                e.SourceId == sourceId && e.Status == ExceptionStatus.Open).ToListAsync(ct);

            foreach (var item in open) Close(item, ExceptionStatus.Resolved, userId, note);
        }

        public async Task<List<ExceptionItem>> ListAsync(Guid companyId, ExceptionStatus? status, ExceptionCategory? category, CancellationToken ct = default)
        {
            var query = _db.ExceptionItems.IgnoreQueryFilters().Where(e => e.CompanyId == companyId);
            if (status.HasValue) query = query.Where(e => e.Status == status.Value);
            if (category.HasValue) query = query.Where(e => e.Category == category.Value);
            return await query.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        }

        public async Task<ExceptionItem> GetAsync(Guid companyId, Guid id, CancellationToken ct = default) =>
            await _db.ExceptionItems.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.CompanyId == companyId, ct)
            ?? throw new InvalidOperationException("Exception item not found.");

        public async Task<ExceptionItem> CloseAsync(Guid companyId, Guid id, Guid userId, ExceptionStatus outcome, string note, CancellationToken ct = default)
        {
            if (outcome == ExceptionStatus.Open) throw new InvalidOperationException("Choose Resolved or Dismissed.");
            if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("A resolution note is required.");

            var item = await GetAsync(companyId, id, ct);
            if (item.Status != ExceptionStatus.Open) throw new InvalidOperationException("This exception has already been closed.");
            Close(item, outcome, userId, note);
            await _db.SaveChangesAsync(ct);
            return item;
        }

        public async Task<ExceptionItem> AssignAsync(Guid companyId, Guid id, Guid ownerUserId, Guid actingUserId, CancellationToken ct = default)
        {
            var item = await GetAsync(companyId, id, ct);
            if (item.Status != ExceptionStatus.Open) throw new InvalidOperationException("A closed exception cannot be reassigned.");
            var before = item.OwnerUserId;
            item.OwnerUserId = ownerUserId;
            _audit.Record(companyId, actingUserId, "Exception.Assigned", "Controls", nameof(ExceptionItem), item.Id,
                before: new { owner = before }, after: new { owner = ownerUserId });
            await _db.SaveChangesAsync(ct);
            return item;
        }

        private void Close(ExceptionItem item, ExceptionStatus outcome, Guid? userId, string note)
        {
            item.Status = outcome;
            item.ResolvedAt = DateTime.UtcNow;
            item.ResolvedBy = userId;
            item.ResolutionNote = Truncate(note);
            _audit.Record(item.CompanyId, userId, "Exception." + outcome, "Controls", nameof(ExceptionItem), item.Id,
                before: new { status = ExceptionStatus.Open.ToString() }, after: new { status = outcome.ToString(), note = item.ResolutionNote });
        }

        private static string Truncate(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty : text.Length <= MaxMessageLength ? text : text[..MaxMessageLength];
    }
}
