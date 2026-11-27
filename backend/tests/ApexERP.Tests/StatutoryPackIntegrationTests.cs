using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using System.Text.Json;
using Xunit;

namespace ApexERP.Tests;

public sealed class StatutoryPackIntegrationTests
{
    [PostgresFact]
    public async Task Zimbabwe_2026_baseline_is_imported_as_unapproved_draft_with_official_sources()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId, Name = "Zimbabwe Statutory Baseline",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var service = new StatutoryPackService(ctx, new AuditService(ctx));
        var draft = await service.CreateZimbabwe2026BaselineDraftAsync(companyId, Guid.NewGuid());
        Assert.Equal("ZW", draft.JurisdictionCode);
        Assert.Equal(1, draft.Version);
        Assert.Equal("Draft", draft.Status);
        Assert.Equal(new DateTime(2026, 1, 1), draft.EffectiveFrom);
        Assert.Equal(7, draft.Sources.Count);
        using var rules = JsonDocument.Parse(draft.RulesJson);
        Assert.Equal(0.155m, rules.RootElement.GetProperty("vat").GetProperty("standardRate").GetDecimal());
        Assert.Equal(0.03m, rules.RootElement.GetProperty("paye").GetProperty("aidsLevyRate").GetDecimal());
        Assert.Equal("baseline-only", rules.RootElement.GetProperty("coverage").GetString());
    }

    [PostgresFact]
    public async Task Published_statutory_pack_activates_currency_specific_PAYE_bands_and_levy_rates()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId, Name = "Payroll Statutory Integration",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var packs = new StatutoryPackService(ctx, new AuditService(ctx));
        var source = new List<StatutoryPackSource>
        {
            new("Test Revenue Authority", "Verified PAYE tables", "https://example.gov/paye", null, new DateTime(2026, 1, 1)),
            new("Test NSSA", "NSSA contribution rates", "https://example.gov/nssa", null, new DateTime(2026, 1, 1))
        };
        const string rules = """
        {
          "coverage": "complete",
          "paye": {
            "aidsLevyRate": 0.03,
            "USD": { "monthly": [ { "from": 0, "to": null, "rate": 0.20, "deduction": 0 } ] },
            "ZWG": { "monthly": [ { "from": 0, "to": null, "rate": 0.10, "deduction": 0 } ] }
          },
          "vat": { "standardRate": 0.155, "zeroRatedSupplies": [], "exemptSupplies": [] },
          "corporateIncomeTax": { "standardCompanyOrTrustRate": 0.25 },
          "nssa": {
            "USD": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 700 },
            "ZWG": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 7000 }
          }
        }
        """;

        var preparer = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var publisher = Guid.NewGuid();
        await packs.CreateDraftAsync(companyId, "ZW", "Zimbabwe", 2, new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31), rules, source, preparer);
        await packs.ReviewAsync(companyId, "ZW", 2, reviewer);
        await packs.ApproveAsync(companyId, "ZW", 2, approver);
        await packs.PublishAsync(companyId, "ZW", 2, publisher);

        var payroll = new PayrollService(ctx, null!);
        Assert.Equal(200m, await payroll.CalculatePayeAsync(companyId, 1000m, new DateTime(2026, 6, 1), "USD"));
        Assert.Equal(100m, await payroll.CalculatePayeAsync(companyId, 1000m, new DateTime(2026, 6, 1), "ZWG"));
        var levyRates = await ctx.StatutoryRates.Where(r => r.CompanyId == companyId && r.Code == "AIDS_LEVY").ToListAsync();
        Assert.Equal(2, levyRates.Count);
        Assert.All(levyRates, rate => Assert.Equal(3m, rate.RatePct));
        Assert.Contains(levyRates, rate => rate.Currency == "USD" && rate.ApprovedBy == approver);
        Assert.Contains(levyRates, rate => rate.Currency == "ZWG" && rate.ApprovedBy == approver);
    }

    [PostgresFact]
    public async Task Statutory_pack_requires_independent_review_approval_and_publication()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId,
                Name = "Statutory Pack Test",
                BaseCurrency = "USD",
                FunctionalCurrency = "USD"
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var service = new StatutoryPackService(ctx, new AuditService(ctx));
        var sources = new List<StatutoryPackSource>
        {
            new("Test Revenue Authority", "Income Tax Act", "https://example.gov/legislation/income-tax", null, new DateTime(2026, 1, 1))
        };
        const string rules = "{ \"coverage\": \"complete\", \"withholding\": { \"status\": \"reviewed\" } }";

        var invalidSources = new List<StatutoryPackSource>
        {
            new("Unknown", "Unverified source", "http://example.gov/act", null, new DateTime(2026, 1, 1))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateDraftAsync(companyId, "ZW", "Zimbabwe", 99, new DateTime(2026, 1, 1),
                null, rules, invalidSources, Guid.NewGuid()));

        var draft = await service.CreateDraftAsync(companyId, "ZW", "Zimbabwe", 1,
            new DateTime(2026, 1, 1), null, rules, sources, Guid.NewGuid());
        Assert.Equal("Draft", draft.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReviewAsync(companyId, "ZW", 1, draft.CreatedBy));
        
        var reviewer = Guid.NewGuid();
        await service.ReviewAsync(companyId, "ZW", 1, reviewer);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApproveAsync(companyId, "ZW", 1, reviewer));

        var approver = Guid.NewGuid();
        await service.ApproveAsync(companyId, "ZW", 1, approver);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(companyId, "ZW", 1, approver));

        var publisher = Guid.NewGuid();
        var published = await service.PublishAsync(companyId, "ZW", 1, publisher);
        Assert.Equal("Published", published.Status);
        Assert.Equal(1, (await service.GetApplicableAsync(companyId, "ZW", new DateTime(2026, 7, 1))).Version);

        var next = await service.CreateDraftAsync(companyId, "ZW", "Zimbabwe", 2,
            new DateTime(2027, 1, 1), null, rules, sources, Guid.NewGuid());
        await service.ReviewAsync(companyId, "ZW", 2, Guid.NewGuid());
        await service.ApproveAsync(companyId, "ZW", 2, Guid.NewGuid());
        await service.PublishAsync(companyId, "ZW", 2, Guid.NewGuid());

        Assert.Equal(1, (await service.GetApplicableAsync(companyId, "ZW", new DateTime(2026, 12, 31))).Version);
        Assert.Equal(next.Version, (await service.GetApplicableAsync(companyId, "ZW", new DateTime(2027, 1, 1))).Version);
    }

}
