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

    public Task<StatutoryPackDocument> CreateDraftAsync(Guid companyId, string jurisdictionCode,
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

        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"statutory-pack:{companyId:N}:{code}", token);
            var key = Key(code, version);
            if (await _db.CompanySettings.AnyAsync(s => s.CompanyId == companyId && s.Key == key, token))
                throw new InvalidOperationException($"Statutory pack {code} version {version} already exists.");

            var document = new StatutoryPackDocument(code, jurisdictionName.Trim(), version, effectiveFrom.Date,
                effectiveTo?.Date, rulesJson, sources, "Draft", actorId, DateTime.UtcNow, null, null, null, null, null);
            _db.CompanySettings.Add(new CompanySetting { CompanyId = companyId, Key = key, Value = JsonSerializer.Serialize(document) });
            _audit.Record(companyId, actorId, "StatutoryPack.DraftCreated", "Statutory", nameof(StatutoryPackDocument),
                Guid.Empty, null, new { code, version, effectiveFrom, effectiveTo, sourceCount = sources.Count });
            await _db.SaveChangesAsync(token);
            return document;
        }, ct);
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
            // A later effective-dated version supersedes an earlier open-ended version without
            // rewriting the already-published legal record. A new pack cannot be backdated over a
            // later publication or share its effective-from date.
            var conflicts = existing.Where(x => x.JurisdictionCode == code && x.Status == "Published" &&
                x.Version != version && RangesOverlap(document.EffectiveFrom, document.EffectiveTo, x.EffectiveFrom, x.EffectiveTo));
            if (conflicts.Any(x => x.EffectiveFrom.Date >= document.EffectiveFrom.Date))
                throw new InvalidOperationException("A published pack already takes effect on or after this date. New versions must advance the jurisdiction's effective timeline.");

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

    /// <summary>
    /// Creates a reviewable Zimbabwe 2026 baseline from ZIMRA's published 2026 PAYE tables,
    /// corporate rates and VAT guidance. This is deliberately a Draft: specialist schedules,
    /// exemptions, withholding rules and filing validations still require legal review.
    /// </summary>
    public Task<StatutoryPackDocument> CreateZimbabwe2026BaselineDraftAsync(Guid companyId, Guid preparedBy,
        CancellationToken ct = default)
    {
        const string rules = """
        {
          "packKind": "tax-baseline",
          "coverage": "baseline-only",
          "approvalNotice": "DRAFT ONLY. Do not activate for statutory calculations or filing until reviewed and approved. VAT exemptions, zero-rated schedules, withholding taxes, customs, capital allowances, tax credits, special regimes and filing-specific edge cases are not fully represented.",
          "effectiveCurrencySet": ["USD", "ZWG"],
          "paye": {
            "aidsLevyRate": 0.03,
            "USD": {
              "effectiveFrom": "2026-01-01",
              "effectiveTo": "2026-12-31",
              "monthly": [
                { "from": 0, "to": 100, "rate": 0, "deduction": 0 },
                { "from": 100.01, "to": 300, "rate": 0.20, "deduction": 20 },
                { "from": 300.01, "to": 1000, "rate": 0.25, "deduction": 35 },
                { "from": 1000.01, "to": 2000, "rate": 0.30, "deduction": 85 },
                { "from": 2000.01, "to": 3000, "rate": 0.35, "deduction": 185 },
                { "from": 3000.01, "to": null, "rate": 0.40, "deduction": 335 }
              ],
              "annual": [
                { "from": 0, "to": 1200, "rate": 0, "deduction": 0 },
                { "from": 1201, "to": 3600, "rate": 0.20, "deduction": 240 },
                { "from": 3601, "to": 12000, "rate": 0.25, "deduction": 420 },
                { "from": 12001, "to": 24000, "rate": 0.30, "deduction": 1020 },
                { "from": 24001, "to": 36000, "rate": 0.35, "deduction": 2220 },
                { "from": 36001, "to": null, "rate": 0.40, "deduction": 4020 }
              ]
            },
            "ZWG": {
              "effectiveFrom": "2026-01-01",
              "effectiveTo": "2026-12-31",
              "monthly": [
                { "from": 0, "to": 2800, "rate": 0, "deduction": 0 },
                { "from": 2800.01, "to": 8400, "rate": 0.20, "deduction": 560 },
                { "from": 8400.01, "to": 28000, "rate": 0.25, "deduction": 980 },
                { "from": 28000.01, "to": 56000, "rate": 0.30, "deduction": 2380 },
                { "from": 56000.01, "to": 84000, "rate": 0.35, "deduction": 5180 },
                { "from": 84000.01, "to": null, "rate": 0.40, "deduction": 9380 }
              ],
              "annual": [
                { "from": 0, "to": 33600, "rate": 0, "deduction": 0 },
                { "from": 33601, "to": 100800, "rate": 0.20, "deduction": 6720 },
                { "from": 100801, "to": 336000, "rate": 0.25, "deduction": 11760 },
                { "from": 336001, "to": 672000, "rate": 0.30, "deduction": 28560 },
                { "from": 672001, "to": 1008000, "rate": 0.35, "deduction": 62160 },
                { "from": 1008001, "to": null, "rate": 0.40, "deduction": 112560 }
              ]
            }
          },
          "corporateIncomeTax": {
            "standardCompanyOrTrustRate": 0.25,
            "pensionFundTradeOrInvestmentRate": 0.15,
            "specialMiningLeaseRate": 0.15,
            "licensedInvestorFirstFiveYearsRate": 0,
            "licensedInvestorAfterFiveYearsRate": 0.25,
            "aidsLevyRate": 0.03,
            "note": "Special regimes and eligibility conditions must be checked against the applicable legislation."
          },
          "vat": {
            "standardRate": 0.155,
            "effectiveFrom": "2026-01-01",
            "note": "Standard rate only. Zero-rated and exempt supplies are governed by specific statutory schedules and are not exhaustively encoded in this baseline."
          }
        }
        """;

        var sources = new List<StatutoryPackSource>
        {
            new("Zimbabwe Revenue Authority (ZIMRA)", "PAYE USD tax tables, 1 January–31 December 2026",
                "https://www.zimra.co.zw/domestic-taxes/tax-tables?download=4523%3Ausd-paye-tax-tables-2026",
                null, new DateTime(2026, 1, 1)),
            new("Zimbabwe Revenue Authority (ZIMRA)", "PAYE ZWG tax tables, 1 January–31 December 2026",
                "https://www.zimra.co.zw/domestic-taxes/tax-tables?download=4522%3Azwg-paye-tax-tables-2026",
                null, new DateTime(2026, 1, 1)),
            new("Zimbabwe Revenue Authority (ZIMRA)", "Corporate income tax rates",
                "https://www.zimra.co.zw/domestic-taxes/corporate/tax-rates", null, new DateTime(2026, 1, 1)),
            new("Zimbabwe Revenue Authority (ZIMRA)", "VAT mechanics and standard rate",
                "https://www.zimra.co.zw/domestic-taxes/vat/mechanics-of-vat", null, new DateTime(2026, 1, 1)),
            new("Zimbabwe Revenue Authority (ZIMRA)", "Public Notice 7 of 2026: VAT rate change effective 1 January 2026",
                "https://www.zimra.co.zw/public-notices?download=4441%3Apublic-notice-07-of-2026-change-of-vat-rate-on-submission-of-return-category-a",
                new DateTime(2026, 1, 7), new DateTime(2026, 1, 1))
        };

        return CreateDraftAsync(companyId, "ZW", "Zimbabwe", 1, new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31), rules, sources, preparedBy, ct);
    }

    public async Task<StatutoryPackDocument> GetApplicableAsync(Guid companyId, string jurisdictionCode,
        DateTime onDate, CancellationToken ct = default)
    {
        var code = NormalizeCode(jurisdictionCode);
        var matches = (await ListCoreAsync(companyId, ct)).Where(x => x.JurisdictionCode == code &&
            x.Status == "Published" && x.EffectiveFrom.Date <= onDate.Date &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value.Date >= onDate.Date))
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Version).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"No approved and published statutory pack applies to {code} on {onDate:yyyy-MM-dd}.");
        if (matches.Count > 1 && matches[0].EffectiveFrom.Date == matches[1].EffectiveFrom.Date)
            throw new InvalidOperationException($"Conflicting statutory packs share the same effective date for {code}; administrator review is required.");
        return matches[0];
    }

    private Task<StatutoryPackDocument> ChangeAsync(Guid companyId, string jurisdictionCode, int version,
        Guid actorId, string expectedStatus, string nextStatus,
        Func<StatutoryPackDocument, Guid, StatutoryPackDocument> update, string auditAction, CancellationToken ct)
    {
        var code = NormalizeCode(jurisdictionCode);
        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"statutory-pack:{companyId:N}:{code}", token);
            var setting = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == Key(code, version), token)
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
            await _db.SaveChangesAsync(token);
            return changed;
        }, ct);
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
