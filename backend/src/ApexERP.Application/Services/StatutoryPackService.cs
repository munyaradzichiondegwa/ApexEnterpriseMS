using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using ApexERP.Domain.Common;
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

    public Task<StatutoryPackDocument> UpdateDraftAsync(Guid companyId, string jurisdictionCode, int version,
        string rulesJson, IReadOnlyList<StatutoryPackSource> sources, Guid editorId, CancellationToken ct = default)
    {
        var code = NormalizeCode(jurisdictionCode);
        ValidateRules(rulesJson);
        ValidateSources(sources);
        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"statutory-pack:{companyId:N}:{code}", token);
            var setting = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == Key(code, version), token)
                ?? throw new InvalidOperationException("Statutory pack not found.");
            var document = Deserialize(setting.Value);
            if (document.Status != "Draft")
                throw new InvalidOperationException("Only a Draft statutory pack can be edited; reviewed or approved packs require a new version.");
            if (document.CreatedBy != editorId)
                throw new InvalidOperationException("Only the original preparer may amend a draft statutory pack.");
            var changed = document with { RulesJson = rulesJson, Sources = sources };
            setting.Value = JsonSerializer.Serialize(changed);
            _audit.Record(companyId, editorId, "StatutoryPack.DraftUpdated", "Statutory", nameof(StatutoryPackDocument),
                Guid.Empty, new { document.Version, document.Status }, new { changed.Version, changed.Status, sourceCount = sources.Count });
            await _db.SaveChangesAsync(token);
            return changed;
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

            using (var rulesDocument = JsonDocument.Parse(document.RulesJson))
            {
                if (!rulesDocument.RootElement.TryGetProperty("coverage", out var coverage) ||
                    !string.Equals(coverage.GetString(), "complete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A statutory pack marked baseline-only or without complete coverage cannot be published. Complete the missing schedules and update the draft before approval.");
            }

            var existing = await ListCoreAsync(companyId, token);
            // A later effective-dated version supersedes an earlier open-ended version without
            // rewriting the already-published legal record. A new pack cannot be backdated over a
            // later publication or share its effective-from date.
            var conflicts = existing.Where(x => x.JurisdictionCode == code && x.Status == "Published" &&
                x.Version != version && RangesOverlap(document.EffectiveFrom, document.EffectiveTo, x.EffectiveFrom, x.EffectiveTo));
            if (conflicts.Any(x => x.EffectiveFrom.Date >= document.EffectiveFrom.Date))
                throw new InvalidOperationException("A published pack already takes effect on or after this date. New versions must advance the jurisdiction's effective timeline.");

            ValidateJurisdictionRules(document);
            await ApplyApprovedRulesAsync(companyId, document, token);
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
          "approvalNotice": "DRAFT ONLY. Do not activate for statutory calculations or filing until reviewed and approved. NSSA ZWG insurable ceiling and age-65 exemption handling, APWCS industry-specific premiums, VAT zero-rated/exempt schedules, withholding taxes, customs, capital allowances, tax credits, special regimes and filing-specific edge cases are not fully represented.",
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
          "nssa": {
            "USD": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 700, "effectiveFrom": "2026-11-27", "effectiveTo": "2026-12-31", "verifiedAsOf": "2026-11-27" }
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
                new DateTime(2026, 2, 9), new DateTime(2026, 1, 1)),
            new("National Social Security Authority (NSSA)", "Pension and Other Benefits Scheme contribution rates and USD ceiling",
                "https://www.nssa.org.zw/contributions/", null, new DateTime(2026, 11, 27)),
            new("National Social Security Authority (NSSA)", "Schedule of insurable earnings",
                "https://www.nssa.org.zw/scheduleofinsurableearnings/", null, new DateTime(2024, 6, 1))
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

    private static void ValidateJurisdictionRules(StatutoryPackDocument document)
    {
        if (!string.Equals(document.JurisdictionCode, "ZW", StringComparison.OrdinalIgnoreCase))
            return;
        using var parsed = JsonDocument.Parse(document.RulesJson);
        var root = parsed.RootElement;
        foreach (var section in new[] { "paye", "nssa", "corporateIncomeTax", "vat" })
            if (!root.TryGetProperty(section, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"A publishable Zimbabwe pack must include the {section} section.");

        foreach (var currency in new[] { "USD", "ZWG" })
        {
            if (!root.GetProperty("paye").TryGetProperty(currency, out var payeCurrency) ||
                !payeCurrency.TryGetProperty("monthly", out var bands) || bands.ValueKind != JsonValueKind.Array || bands.GetArrayLength() == 0)
                throw new InvalidOperationException($"A publishable Zimbabwe pack must include PAYE monthly bands for {currency}.");
            if (!root.GetProperty("nssa").TryGetProperty(currency, out var nssaCurrency) ||
                !nssaCurrency.TryGetProperty("employeeRate", out _) ||
                !nssaCurrency.TryGetProperty("employerRate", out _) ||
                !nssaCurrency.TryGetProperty("ceiling", out _))
                throw new InvalidOperationException($"A publishable Zimbabwe pack must include NSSA employee/employer rates and a ceiling for {currency}.");
        }

        var vat = root.GetProperty("vat");
        if (!vat.TryGetProperty("standardRate", out var vatRate) || vatRate.GetDecimal() is < 0m or > 1m ||
            !vat.TryGetProperty("zeroRatedSupplies", out var zeroRated) || zeroRated.ValueKind != JsonValueKind.Array ||
            !vat.TryGetProperty("exemptSupplies", out var exempt) || exempt.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("A publishable Zimbabwe pack must include the VAT standard rate, zero-rated supply schedule and exemption schedule.");

        var corporate = root.GetProperty("corporateIncomeTax");
        if (!corporate.TryGetProperty("standardCompanyOrTrustRate", out var corporateRate) ||
            corporateRate.GetDecimal() is < 0m or > 1m)
            throw new InvalidOperationException("A publishable Zimbabwe pack must include a valid standard corporate income-tax rate.");

        if (!document.Sources.Any(x => x.Authority.Contains("ZIMRA", StringComparison.OrdinalIgnoreCase)) ||
            !document.Sources.Any(x => x.Authority.Contains("NSSA", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A publishable Zimbabwe pack must cite official ZIMRA and NSSA sources.");
    }

    private async Task ApplyApprovedRulesAsync(Guid companyId, StatutoryPackDocument document, CancellationToken ct)
    {
        using var parsed = JsonDocument.Parse(document.RulesJson);
        if (!parsed.RootElement.TryGetProperty("paye", out var paye) || paye.ValueKind != JsonValueKind.Object)
            return;
        if (!paye.TryGetProperty("aidsLevyRate", out var levyElement))
            throw new InvalidOperationException("A complete PAYE pack must include the AIDS Levy rate.");
        var levyRate = levyElement.GetDecimal();
        if (levyRate is < 0m or > 1m)
            throw new InvalidOperationException("The AIDS Levy rate must be expressed as a fraction between zero and one.");

        if (!parsed.RootElement.TryGetProperty("nssa", out var nssa) || nssa.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("A complete statutory pack must include NSSA rates and ceilings for each payroll currency.");

        foreach (var currency in new[] { "USD", "ZWG" })
        {
            if (!paye.TryGetProperty(currency, out var currencyRules) ||
                !currencyRules.TryGetProperty("monthly", out var monthly) || monthly.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"The complete statutory pack is missing monthly PAYE bands for {currency}.");

            var bands = monthly.EnumerateArray().Select(item => new
            {
                Lower = item.GetProperty("from").GetDecimal(),
                Upper = item.GetProperty("to").ValueKind == JsonValueKind.Null ? (decimal?)null : item.GetProperty("to").GetDecimal(),
                Rate = item.GetProperty("rate").GetDecimal(),
                Deduct = item.GetProperty("deduction").GetDecimal()
            }).OrderBy(x => x.Lower).ToList();
            if (bands.Count == 0 || bands[0].Lower != 0m ||
                bands.Any(x => x.Lower < 0m || x.Rate is < 0m or > 1m || x.Deduct < 0m ||
                    (x.Upper.HasValue && x.Upper.Value < x.Lower)) ||
                bands.Take(bands.Count - 1).Any(x => !x.Upper.HasValue))
                throw new InvalidOperationException($"PAYE bands for {currency} are incomplete or invalid.");

            if (await _db.PayeTaxBands.AnyAsync(b => b.CompanyId == companyId && b.Currency == currency &&
                b.EffectiveFrom == document.EffectiveFrom, ct))
                throw new InvalidOperationException($"PAYE bands for {currency} already exist with this effective date.");

            var previousBands = await _db.PayeTaxBands.Where(b => b.CompanyId == companyId && b.Currency == currency &&
                b.EffectiveFrom < document.EffectiveFrom && (!b.EffectiveTo.HasValue || b.EffectiveTo >= document.EffectiveFrom)).ToListAsync(ct);
            foreach (var previous in previousBands)
                previous.EffectiveTo = document.EffectiveFrom.AddDays(-1);

            foreach (var band in bands)
            {
                _db.PayeTaxBands.Add(new PayeTaxBand
                {
                    CompanyId = companyId, Currency = currency, LowerBound = band.Lower, UpperBound = band.Upper,
                    Rate = band.Rate, Deduct = band.Deduct, EffectiveFrom = document.EffectiveFrom,
                    EffectiveTo = document.EffectiveTo, CreatedBy = document.CreatedBy
                });
            }

            if (await _db.StatutoryRates.AnyAsync(r => r.CompanyId == companyId && r.Code == "AIDS_LEVY" &&
                r.Currency == currency && r.EffectiveFrom == document.EffectiveFrom, ct))
                throw new InvalidOperationException($"An AIDS Levy rate for {currency} already exists with this effective date.");

            var previousLevyRates = await _db.StatutoryRates.Where(r => r.CompanyId == companyId &&
                r.Code == "AIDS_LEVY" && r.Currency == currency && r.EffectiveFrom < document.EffectiveFrom &&
                (!r.EffectiveTo.HasValue || r.EffectiveTo >= document.EffectiveFrom)).ToListAsync(ct);
            foreach (var previous in previousLevyRates)
                previous.EffectiveTo = document.EffectiveFrom.AddDays(-1);

            var source = document.Sources.FirstOrDefault(x => x.Authority.Contains("NSSA", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("An official NSSA source reference is required for a complete payroll statutory pack.");
            if (!nssa.TryGetProperty(currency, out var nssaCurrency))
                throw new InvalidOperationException($"A complete statutory pack must include NSSA employee and employer rates and ceiling for {currency}.");
            var employeeRate = nssaCurrency.GetProperty("employeeRate").GetDecimal();
            var employerRate = nssaCurrency.GetProperty("employerRate").GetDecimal();
            var ceilingElement = nssaCurrency.GetProperty("ceiling");
            decimal? nssaCeiling = ceilingElement.ValueKind == JsonValueKind.Null ? null : ceilingElement.GetDecimal();
            var nssaEffectiveFrom = nssaCurrency.TryGetProperty("effectiveFrom", out var nssaFrom)
                ? DateTime.Parse(nssaFrom.GetString()!, CultureInfo.InvariantCulture).Date
                : document.EffectiveFrom;
            DateTime? nssaEffectiveTo = nssaCurrency.TryGetProperty("effectiveTo", out var nssaTo)
                ? (nssaTo.ValueKind == JsonValueKind.Null ? null : DateTime.Parse(nssaTo.GetString()!, CultureInfo.InvariantCulture).Date)
                : document.EffectiveTo;
            if (employeeRate is < 0m or > 1m || employerRate is < 0m or > 1m ||
                (nssaCeiling.HasValue && nssaCeiling.Value <= 0m))
                throw new InvalidOperationException($"NSSA rates or ceiling for {currency} are invalid.");

            foreach (var code in new[] { "NSSA_EMPLOYEE", "NSSA_EMPLOYER" })
            {
                if (await _db.StatutoryRates.AnyAsync(r => r.CompanyId == companyId && r.Code == code &&
                    r.Currency == currency && r.EffectiveFrom == nssaEffectiveFrom, ct))
                    throw new InvalidOperationException($"A {code} rate for {currency} already exists with this effective date.");
                var previousRates = await _db.StatutoryRates.Where(r => r.CompanyId == companyId &&
                    r.Code == code && r.Currency == currency && r.EffectiveFrom < nssaEffectiveFrom &&
                    (!r.EffectiveTo.HasValue || r.EffectiveTo >= nssaEffectiveFrom)).ToListAsync(ct);
                foreach (var previous in previousRates)
                    previous.EffectiveTo = nssaEffectiveFrom.AddDays(-1);

                _db.StatutoryRates.Add(new StatutoryRate
                {
                    CompanyId = companyId, Code = code,
                    RatePct = (code == "NSSA_EMPLOYEE" ? employeeRate : employerRate) * 100m,
                    Ceiling = nssaCeiling, Currency = currency, EffectiveFrom = nssaEffectiveFrom,
                    EffectiveTo = nssaEffectiveTo, SourceReference = source.Title + " — " + source.Url,
                    CreatedBy = document.CreatedBy, ApprovedBy = document.ApprovedBy
                });
            }

            _db.StatutoryRates.Add(new StatutoryRate
            {
                CompanyId = companyId, Code = "AIDS_LEVY", RatePct = levyRate * 100m,
                Currency = currency, EffectiveFrom = document.EffectiveFrom, EffectiveTo = document.EffectiveTo,
                SourceReference = document.Sources.FirstOrDefault(x => x.Title.Contains(currency, StringComparison.OrdinalIgnoreCase))?.Url
                    ?? document.Sources.First().Url,
                CreatedBy = document.CreatedBy, ApprovedBy = document.ApprovedBy
            });
        }

        var vat = parsed.RootElement.GetProperty("vat");
        var vatRate = vat.GetProperty("standardRate").GetDecimal();
        var existingVatCodes = await _db.TaxCodes.Where(t => t.CompanyId == companyId &&
            t.Code == "VAT_STANDARD" && t.Type == TaxType.VAT && t.EffectiveFrom < document.EffectiveFrom &&
            (!t.EffectiveTo.HasValue || t.EffectiveTo >= document.EffectiveFrom)).ToListAsync(ct);
        if (await _db.TaxCodes.AnyAsync(t => t.CompanyId == companyId && t.Code == "VAT_STANDARD" &&
            t.Type == TaxType.VAT && t.EffectiveFrom == document.EffectiveFrom, ct))
            throw new InvalidOperationException("A standard VAT tax code already exists with this effective date.");
        foreach (var previous in existingVatCodes)
            previous.EffectiveTo = document.EffectiveFrom.AddDays(-1);
        _db.TaxCodes.Add(new TaxCode
        {
            CompanyId = companyId, Code = "VAT_STANDARD", Name = "Standard VAT",
            Rate = vatRate, Type = TaxType.VAT, IsActive = true,
            EffectiveFrom = document.EffectiveFrom, EffectiveTo = document.EffectiveTo, CreatedBy = document.CreatedBy
        });

        var corporateRate = parsed.RootElement.GetProperty("corporateIncomeTax")
            .GetProperty("standardCompanyOrTrustRate").GetDecimal();
        var corporateSource = document.Sources.FirstOrDefault(x => x.Authority.Contains("ZIMRA", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("An official ZIMRA source reference is required for corporate income tax.");
        foreach (var currency in new[] { "USD", "ZWG" })
        {
            if (await _db.StatutoryRates.AnyAsync(r => r.CompanyId == companyId && r.Code == "CORPORATE_INCOME_TAX" &&
                r.Currency == currency && r.EffectiveFrom == document.EffectiveFrom, ct))
                throw new InvalidOperationException($"A corporate income-tax rate for {currency} already exists with this effective date.");
            var previousRates = await _db.StatutoryRates.Where(r => r.CompanyId == companyId &&
                r.Code == "CORPORATE_INCOME_TAX" && r.Currency == currency && r.EffectiveFrom < document.EffectiveFrom &&
                (!r.EffectiveTo.HasValue || r.EffectiveTo >= document.EffectiveFrom)).ToListAsync(ct);
            foreach (var previous in previousRates)
                previous.EffectiveTo = document.EffectiveFrom.AddDays(-1);
            _db.StatutoryRates.Add(new StatutoryRate
            {
                CompanyId = companyId, Code = "CORPORATE_INCOME_TAX", RatePct = corporateRate * 100m,
                Currency = currency, EffectiveFrom = document.EffectiveFrom, EffectiveTo = document.EffectiveTo,
                SourceReference = corporateSource.Title + " — " + corporateSource.Url,
                CreatedBy = document.CreatedBy, ApprovedBy = document.ApprovedBy
            });
        }

        _audit.Record(companyId, document.ApprovedBy!.Value, "StatutoryPack.RulesActivated", "Tax",
            nameof(StatutoryPackDocument), Guid.Empty, null,
            new { document.JurisdictionCode, document.Version, currencies = new[] { "USD", "ZWG" } });
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
