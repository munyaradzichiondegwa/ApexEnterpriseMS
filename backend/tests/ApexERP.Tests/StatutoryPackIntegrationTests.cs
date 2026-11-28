using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace ApexERP.Tests;

public sealed class StatutoryPackIntegrationTests
{
    [PostgresFact]
    public async Task Zimbabwe_2026_baseline_is_imported_as_unapproved_draft_and_cannot_be_published()
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
        // ZIMRA's official 2026 ZWG monthly PAYE table: preserve each published threshold,
        // marginal rate and cumulative deduction as a regression against statutory-source drift.
        var zwgMonthly = rules.RootElement.GetProperty("paye").GetProperty("ZWG").GetProperty("monthly");
        Assert.Equal(6, zwgMonthly.GetArrayLength());
        var expectedZwgMonthly = new (decimal From, decimal? To, decimal Rate, decimal Deduction)[]
        {
            (0m, 2800m, 0m, 0m),
            (2800.01m, 8400m, 0.20m, 560m),
            (8400.01m, 28000m, 0.25m, 980m),
            (28000.01m, 56000m, 0.30m, 2380m),
            (56000.01m, 84000m, 0.35m, 5180m),
            (84000.01m, null, 0.40m, 9380m)
        };
        for (var i = 0; i < expectedZwgMonthly.Length; i++)
        {
            var band = zwgMonthly[i];
            Assert.Equal(expectedZwgMonthly[i].From, band.GetProperty("from").GetDecimal());
            Assert.Equal(expectedZwgMonthly[i].To.HasValue ? expectedZwgMonthly[i].To.Value : (decimal?)null,
                band.GetProperty("to").ValueKind == JsonValueKind.Null ? null : band.GetProperty("to").GetDecimal());
            Assert.Equal(expectedZwgMonthly[i].Rate, band.GetProperty("rate").GetDecimal());
            Assert.Equal(expectedZwgMonthly[i].Deduction, band.GetProperty("deduction").GetDecimal());
        }
        Assert.Contains(draft.Sources, source =>
            source.Url == "https://www.zimra.co.zw/domestic-taxes/tax-tables?download=4522%3Azwg-paye-tax-tables-2026");


        var reviewer = Guid.NewGuid();
        var approver = Guid.NewGuid();
        await service.ReviewAsync(companyId, "ZW", 1, reviewer);
        await service.ApproveAsync(companyId, "ZW", 1, approver);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(companyId, "ZW", 1, Guid.NewGuid()));

        var stillApproved = Assert.Single(await service.ListAsync(companyId, "ZW"));
        Assert.Equal("Approved", stillApproved.Status);
        Assert.Empty(await ctx.PayeTaxBands.Where(x => x.CompanyId == companyId).ToListAsync());
        Assert.Empty(await ctx.StatutoryRates.Where(x => x.CompanyId == companyId).ToListAsync());
        Assert.Empty(await ctx.TaxCodes.Where(x => x.CompanyId == companyId).ToListAsync());
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
            new("Test ZIMRA", "Verified PAYE tables", "https://example.gov/paye", null, new DateTime(2026, 1, 1)),
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
        var allRates = await ctx.StatutoryRates.Where(r => r.CompanyId == companyId).ToListAsync();
        Assert.Equal(31.5m, payroll.CalculateNssaEmployee(1000m, allRates, "USD"));
        Assert.Equal(45m, payroll.CalculateNssaEmployer(1000m, allRates, "ZWG"));
        Assert.Equal(6m, payroll.CalculateAidsLevy(200m, allRates, "USD"));
        var standardVat = await ctx.TaxCodes.SingleAsync(t => t.CompanyId == companyId && t.Code == "VAT_STANDARD");
        Assert.Equal(0.155m, standardVat.Rate);
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
            new("ZIMRA", "Verified Zimbabwe tax schedules", "https://example.gov/zimra/tax", null, new DateTime(2026, 1, 1)),
            new("NSSA", "Verified social security contribution schedules", "https://example.gov/nssa/contributions", null, new DateTime(2026, 1, 1))
        };
        const string rules = """
        {
          "coverage": "complete",
          "paye": {
            "aidsLevyRate": 0.03,
            "USD": { "monthly": [ { "from": 0, "to": null, "rate": 0.20, "deduction": 0 } ] },
            "ZWG": { "monthly": [ { "from": 0, "to": null, "rate": 0.10, "deduction": 0 } ] }
          },
          "nssa": {
            "USD": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 700 },
            "ZWG": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 7000 }
          },
          "corporateIncomeTax": { "standardCompanyOrTrustRate": 0.25 },
          "vat": { "standardRate": 0.155, "zeroRatedSupplies": [], "exemptSupplies": [] },
          "withholding": { "status": "reviewed" }
        }
        """;

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

    [PostgresFact]
    public async Task Unsupported_jurisdiction_cannot_publish_through_the_Zimbabwe_tax_activation_mapper()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId, Name = "Unsupported Jurisdiction Activation",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var packs = new StatutoryPackService(ctx, new AuditService(ctx));
        var sources = new List<StatutoryPackSource>
        {
            new("ZIMRA", "Test tax source", "https://example.gov/tax", null, new DateTime(2026, 1, 1)),
            new("NSSA", "Test social security source", "https://example.gov/social-security", null, new DateTime(2026, 1, 1))
        };
        const string rules = """
        {
          "coverage": "complete",
          "paye": {
            "aidsLevyRate": 0.03,
            "USD": { "monthly": [ { "from": 0, "to": null, "rate": 0.20, "deduction": 0 } ] },
            "ZWG": { "monthly": [ { "from": 0, "to": null, "rate": 0.10, "deduction": 0 } ] }
          },
          "nssa": {
            "USD": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 700 },
            "ZWG": { "employeeRate": 0.045, "employerRate": 0.045, "ceiling": 7000 }
          },
          "corporateIncomeTax": { "standardCompanyOrTrustRate": 0.25 },
          "vat": { "standardRate": 0.155, "zeroRatedSupplies": [], "exemptSupplies": [] }
        }
        """;

        await packs.CreateDraftAsync(companyId, "US", "United States", 1,
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), rules, sources, Guid.NewGuid());
        await packs.ReviewAsync(companyId, "US", 1, Guid.NewGuid());
        await packs.ApproveAsync(companyId, "US", 1, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            packs.PublishAsync(companyId, "US", 1, Guid.NewGuid()));

        var stored = Assert.Single(await packs.ListAsync(companyId, "US"));
        Assert.Equal("Approved", stored.Status);
        Assert.Empty(await ctx.PayeTaxBands.Where(x => x.CompanyId == companyId).ToListAsync());
        Assert.Empty(await ctx.StatutoryRates.Where(x => x.CompanyId == companyId).ToListAsync());
        Assert.Empty(await ctx.TaxCodes.Where(x => x.CompanyId == companyId).ToListAsync());
    }


    [PostgresFact]
    public async Task Registered_jurisdiction_adapter_owns_validation_and_activation_dispatch()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId, Name = "Jurisdiction Adapter Dispatch",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var adapter = new TestJurisdictionAdapter("US");
        var service = new StatutoryPackService(ctx, new AuditService(ctx), new[] { adapter });
        var sources = new List<StatutoryPackSource>
        {
            new("Test authority", "Test statutory source", "https://example.gov/statutes", null, new DateTime(2026, 1, 1))
        };
        const string rules = """{ "coverage": "complete", "countrySpecificSchedule": { "version": 1 } }""";
        await service.CreateDraftAsync(companyId, "US", "United States", 1,
            new DateTime(2026, 1, 1), null, rules, sources, Guid.NewGuid());
        await service.ReviewAsync(companyId, "US", 1, Guid.NewGuid());
        await service.ApproveAsync(companyId, "US", 1, Guid.NewGuid());

        var published = await service.PublishAsync(companyId, "US", 1, Guid.NewGuid());

        Assert.Equal("Published", published.Status);
        Assert.True(adapter.ValidationCalled);
        Assert.True(adapter.ActivationCalled);
        Assert.Empty(await ctx.PayeTaxBands.Where(x => x.CompanyId == companyId).ToListAsync());
        Assert.Empty(await ctx.StatutoryRates.Where(x => x.CompanyId == companyId).ToListAsync());
        Assert.Empty(await ctx.TaxCodes.Where(x => x.CompanyId == companyId).ToListAsync());
    }

    private sealed class TestJurisdictionAdapter : IStatutoryPackActivationAdapter
    {
        public TestJurisdictionAdapter(string jurisdictionCode) => JurisdictionCode = jurisdictionCode;
        public string JurisdictionCode { get; }
        public bool ValidationCalled { get; private set; }
        public bool ActivationCalled { get; private set; }

        public void Validate(StatutoryPackDocument document)
        {
            Assert.Equal(JurisdictionCode, document.JurisdictionCode);
            using var rules = JsonDocument.Parse(document.RulesJson);
            Assert.Equal("complete", rules.RootElement.GetProperty("coverage").GetString());
            ValidationCalled = true;
        }

        public Task ActivateAsync(Guid companyId, StatutoryPackDocument document, CancellationToken ct)
        {
            Assert.NotEqual(Guid.Empty, companyId);
            Assert.Equal(JurisdictionCode, document.JurisdictionCode);
            ActivationCalled = true;
            return Task.CompletedTask;
        }
    }

}
