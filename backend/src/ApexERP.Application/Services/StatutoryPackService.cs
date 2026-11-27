using System.Text.Json;
using System.Text.RegularExpressions;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services;

public sealed record StatutoryPackSource(string Authority, string Title, string Url, DateTime? PublishedOn, DateTime EffectiveFrom);

public sealed record StatutoryPackDocument(
    string JurisdictionCode, string JurisdictionName, int Version,
    DateTime EffectiveFrom, DateTime? EffectiveTo, string RulesJson,
    IReadOnlyList<StatutoryPackSource> Sources, string Status,
    Guid CreatedBy, DateTime CreatedAt, Guid? ReviewedBy, DateTime? ReviewedAt,
    Guid? ApprovedBy, DateTime? ApprovedAt, DateTime? PublishedAt);

/// <summary>
/// Versioned, jurisdiction-scoped statutory rule packs. Rules are stored as supplied JSON and
/// cannot become active until independently reviewed, approved and published. Source references
/// are mandatory; this service deliberately does not invent tax rates or certify legal validity.
/// </summary>
public sealed class StatutoryPackService
{
    private const string KeyPrefix = "statutory.pack:";
    private readonly ApexErpDbContext _db;
    private readonly AuditService _audit;

    public StatutoryPackService(ApexErpDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<StatutoryPackDocument> CreateDraftAsync(Guid companyId, string jurisdictionCode,
        string jurisdictionName, int version, DateTime effectiveFrom, DateTime? effectiveTo,
        string rulesJson, IReadOnlyList<StatutoryPackSource> sources, Guid actorId, CancellationToken ct = default)
    {
        var code = NormalizeCode(jurisdictionCode);
        if (string.IsNullOrWhiteSpace(jurisdictionName)) throw new InvalidOperationException("A jurisdiction name is required.");
        if (version < 1) throw new InvalidOperationException("Pack version must be a positive integer.");
        if (effectiveTo.HasValue && effectiveTo.Value.Date < effectiveFrom.Date)
            throw new InvalidOperationException("Effective-to date cannot be earlier than effective-from date.");
        ValidateRules(rulesJson);
        ValidateSources(sources);

        var key = Key(code, version);
        if (await _db.CompanySettings.AnyAsync(s => s.CompanyId == companyId && s.Key == key, ct))
            throw new InvalidOperationException($"Statutory pack {code} version {version} already exists.");

        var document = new StatutoryPackDocument(code, jurisdictionName.Trim(), version, effectiveFrom.Date,
            effectiveTo?.Date, rulesJson, sources, "Draft", actorId, DateTime.UtcNow, null, null, null, null, null);
        _db.CompanySettings.Add(new CompanySetting { CompanyId = companyId, Key = key, Value = JsonSerializer.Serialize(document) });
        _audit.Record(companyId, actorId, "StatutoryPack.DraftCreated", "Statutory", nameof(StatutoryPackDocument),
            Guid.Empty, null, new { code, version, effectiveFrom, effectiveTo, sourceCount = sources.Count });
        await _db.SaveChangesAsync(ct);
        return document;
    }

    public Task<StatutoryPackDocument> ReviewAsync(Guid companyId, string jurisdictionCode, int version,
        Guid reviewerId, CancellationToken ct = default) =>
        ChangeAsync(companyId, jurisdictionCode, version, reviewerId, "Draft", "Reviewed",
            (d, actor) => d with { Status = "Reviewed", ReviewedBy = actor, ReviewedAt = DateTime.UtcNow },
            "StatutoryPack.Reviewed", ct);

    public Task<StatutoryPackDocument> ApproveAsync(Guid companyId, string jurisdictionCode, int version,
        Guid approverId, CancellationToken ct = default) =>
        ChangeAsync(companyId, jurisdictionCode, version, approverId, "Reviewed", "Approved",
            (d, actor) => d with { Status = "Approved", ApprovedBy = actor, ApprovedAt = DateTime.UtcNow },
            "StatutoryPack.Approved", ct);

    public async Task<StatutoryPackDocument> PublishAsync(Guid companyId, string jurisdictionCode, int version,
        Guid publisherId, CancellationToken ct = default)
    {
        var code = NormalizeCode(jurisdictionCode);
        return await _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"statutory-pack:{companyId:N}:{code}", token);
            var setting = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == Key(code, version), token)
                ?? throw new InvalidOperationException("Statutory pack not found.");
            var document = Deserialize(setting.Value);
            if (document.Status != "Approved" || !document.ApprovedBy.HasValue)
                throw new InvalidOperationException("Only an independently approved statutory pack can be published.");
            if (document.CreatedBy == publisherId || document.ReviewedBy == publisherId || document.ApprovedBy == publisherId)
                throw new InvalidOperationException("The pack preparer, reviewer and approver cannot publish the pack.");
            if (document.EffectiveTo.HasValue && document.EffectiveTo.Value.Date < document.EffectiveFrom.Date)
                throw new InvalidOperationException("The statutory pack effective date range is invalid.");

            var existing = await ListCoreAsync(companyId, token);
            var overlaps = existing.Where(x => x.JurisdictionCode == code && x.Status == "Published" && x.Version != version)
                .Any(x => RangesOverlap(document.EffectiveFrom, document.EffectiveTo, x.EffectiveFrom, x.EffectiveTo));
            if (overlaps)
                throw new InvalidOperationException("The effective period overlaps another published pack for this jurisdiction. Close the previous period before publishing.");

            var published = document with { Status = "Published", PublishedAt = DateTime.UtcNow };
            setting.Value = JsonSerializer.Serialize(published);
            _audit.Record(companyId, publisherId, "StatutoryPack.Published", "Statutory", nameof(StatutoryPackDocument),
                Guid.Empty, new { document.Status }, new { published.JurisdictionCode, published.Version, published.EffectiveFrom, published.EffectiveTo });
            await _db.SaveChangesAsync(token);
            return published;
        }, ct);
    }

    public async Task<IReadOnlyList<StatutoryPackDocument>> ListAsync(Guid companyId, string? jurisdictionCode = null,
        CancellationToken ct = default)
    {
        var items = await ListCoreAsync(companyId, ct);
        var code = string.IsNullOrWhiteSpace(jurisdictionCode) ? null : NormalizeCode(jurisdictionCode);
        return items.Where(x => code == null || x.JurisdictionCode == code)
            .OrderBy(x => x.JurisdictionCode).ThenByDescending(x => x.Version).ToList();
    }

    public async Task<StatutoryPackDocument> GetApplicableAsync(Guid companyId, string jurisdictionCode,
        DateTime onDate, CancellationToken ct = default)
    {
        var code = NormalizeCode(jurisdictionCode);
        var matches = (await ListCoreAsync(companyId, ct)).Where(x => x.JurisdictionCode == code &&
            x.Status == "Published" && x.EffectiveFrom.Date <= onDate.Date &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value.Date >= onDate.Date)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0
                ? $"No approved and published statutory pack applies to {code} on {onDate:yyyy-MM-dd}."
                : $"More than one published statutory pack applies to {code} on {onDate:yyyy-MM-dd}; publication integrity requires administrator review.");
        return matches[0];
    }

    private async Task<StatutoryPackDocument> ChangeAsync(Guid companyId, string jurisdictionCode, int version,
        Guid actorId, string expectedStatus, string nextStatus,
        Func<StatutoryPackDocument, Guid, StatutoryPackDocument> update, string auditAction, CancellationToken ct)
    {
        var code = NormalizeCode(jurisdictionCode);
        var setting = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == Key(code, version), ct)
            ?? throw new InvalidOperationException("Statutory pack not found.");
        var document = Deserialize(setting.Value);
        if (document.Status != expectedStatus)
            throw new InvalidOperationException($"Only a {expectedStatus} statutory pack can be moved to {nextStatus}.");
        if (document.CreatedBy == actorId || document.ReviewedBy == actorId || document.ApprovedBy == actorId)
            throw new InvalidOperationException("Preparation, review and approval must be performed by different users.");
        var changed = update(document, actorId);
        setting.Value = JsonSerializer.Serialize(changed);
        _audit.Record(companyId, actorId, auditAction, "Statutory", nameof(StatutoryPackDocument),
            Guid.Empty, new { document.Status }, new { changed.JurisdictionCode, changed.Version, changed.Status });
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    private async Task<List<StatutoryPackDocument>> ListCoreAsync(Guid companyId, CancellationToken ct)
    {
        var values = await _db.CompanySettings.Where(s => s.CompanyId == companyId && s.Key.StartsWith(KeyPrefix))
            .Select(s => s.Value).ToListAsync(ct);
        return values.Select(Deserialize).ToList();
    }

    private static string Key(string code, int version) => $"{KeyPrefix}{code}:{version:D4}";
    private static string NormalizeCode(string code)
    {
        var value = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!Regex.IsMatch(value, "^[A-Z]{2,3}(-[A-Z0-9]{1,6})?$"))
            throw new InvalidOperationException("Jurisdiction code must be an ISO-style country code, optionally followed by a sub-jurisdiction suffix.");
        return value;
    }

    private static void ValidateRules(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("A statutory rules JSON object is required.");
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Statutory rules must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Statutory rules must contain valid JSON.");
        }
    }

    private static void ValidateSources(IReadOnlyList<StatutoryPackSource> sources)
    {
        if (sources == null || sources.Count == 0)
            throw new InvalidOperationException("At least one official legislative or regulatory source reference is required.");
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Authority) || string.IsNullOrWhiteSpace(source.Title) ||
                !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Each source must include an issuing authority, title and HTTPS reference URL.");
        }
    }

    private static StatutoryPackDocument Deserialize(string value) =>
        JsonSerializer.Deserialize<StatutoryPackDocument>(value)
        ?? throw new InvalidOperationException("A stored statutory pack is invalid; administrator review is required.");

    private static bool RangesOverlap(DateTime startA, DateTime? endA, DateTime startB, DateTime? endB) =>
        startA.Date <= (endB?.Date ?? DateTime.MaxValue) && startB.Date <= (endA?.Date ?? DateTime.MaxValue);
}
