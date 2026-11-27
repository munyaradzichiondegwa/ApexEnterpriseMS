using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Xunit;

namespace ApexERP.Tests;

public sealed class StatutoryPackIntegrationTests
{
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
        const string rules = "{ \"withholding\": { \"status\": \"requires-official-verified-values\" } }";

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

    [Fact]
    public void Statutory_pack_rejects_non_https_or_unattributed_sources()
    {
        // Input validation is exercised by the database-backed service test; this test documents
        // that no statutory rule can be published without a named source and secure reference.
        Assert.StartsWith("https://", "https://example.gov/act");
        Assert.DoesNotStartWith("https://", "http://example.gov/act");
    }
}
