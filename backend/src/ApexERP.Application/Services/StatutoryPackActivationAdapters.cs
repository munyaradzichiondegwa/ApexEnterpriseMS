using System.Globalization;
using System.Text.Json;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services;

/// <summary>
/// Country-specific statutory activation boundary. Implementations must validate their own
/// legal schedules and apply only their jurisdiction's rules; no country mapper is a fallback.
/// </summary>
public interface IStatutoryPackActivationAdapter
{
    string JurisdictionCode { get; }
    void Validate(StatutoryPackDocument document);
    Task ActivateAsync(StatutoryPackDocument document, CancellationToken ct);
}

/// <summary>Zimbabwe tax/payroll activation. This is deliberately not a generic tax mapper.</summary>
public sealed class ZimbabweStatutoryPackActivationAdapter : IStatutoryPackActivationAdapter
{
    private readonly ApexErpDbContext _db;
    private readonly AuditService _audit;

    public ZimbabweStatutoryPackActivationAdapter(ApexErpDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public string JurisdictionCode => "ZW";

    public void Validate(StatutoryPackDocument document)
    {
        if (!string.Equals(document.JurisdictionCode, JurisdictionCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Zimbabwe activation adapter cannot validate another jurisdiction.");
        ValidateJurisdictionRules(document);
    }

    public Task ActivateAsync(StatutoryPackDocument document, CancellationToken ct) =>
        ApplyApprovedRulesAsync(document, ct);

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
}
