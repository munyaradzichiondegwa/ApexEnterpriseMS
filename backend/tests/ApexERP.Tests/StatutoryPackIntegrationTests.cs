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
        Assert.Equal(5, draft.Sources.Count);
        using var rules = JsonDocument.Parse(draft.RulesJson);
        Assert.Equal(0.155m, rules.RootElement.GetProperty("vat").GetProperty("standardRate").GetDecimal());
        Assert.Equal(0.03m, rules.RootElement.GetProperty("paye").GetProperty("aidsLevyRate").GetDecimal());
        Assert.Equal("baseline-only", rules.RootElement.GetProperty("coverage").GetString());
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
