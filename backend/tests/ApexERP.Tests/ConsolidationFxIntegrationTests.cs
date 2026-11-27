using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Xunit;

namespace ApexERP.Tests;

public sealed class ConsolidationFxIntegrationTests
{
    [PostgresFact]
    public async Task Consolidation_returns_effective_date_rate_source_and_inverse_quote_evidence()
    {
        await using var db = await TestDb.CreateAsync();
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var subsidiaryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var equityId = Guid.NewGuid();
        var periodId = Guid.NewGuid();

        await using (var seed = db.NewContext(null))
        {
            seed.CustomerOrganisations.Add(new CustomerOrganisation
            {
                Id = tenantId, Name = "FX consolidation tenant", IsActive = true
            });
            seed.Companies.AddRange(
                new Company
                {
                    Id = parentId, TenantId = tenantId, Name = "Group Parent",
                    BaseCurrency = "USD", FunctionalCurrency = "USD", IsActive = true
                },
                new Company
                {
                    Id = subsidiaryId, TenantId = tenantId, Name = "ZWG Subsidiary",
                    BaseCurrency = "ZWG", FunctionalCurrency = "ZWG",
                    ParentCompanyId = parentId, OwnershipPercentage = 100m, IsActive = true
                });

            seed.GlAccounts.AddRange(
                new GlAccount
                {
                    Id = assetId, CompanyId = subsidiaryId, Code = "1000",
                    Name = "Cash", Type = GlAccountType.Asset, Currency = "ZWG"
                },
                new GlAccount
                {
                    Id = equityId, CompanyId = subsidiaryId, Code = "3000",
                    Name = "Share Capital", Type = GlAccountType.Equity, Currency = "ZWG"
                });
            seed.AccountingPeriods.Add(new AccountingPeriod
            {
                Id = periodId, CompanyId = subsidiaryId, Year = 2026, PeriodNo = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 2, 1),
                Status = PeriodStatus.Open
            });
            var journal = new JournalEntry
            {
                CompanyId = subsidiaryId, EntryNo = "JE-FX-001",
                Description = "Opening balances", EntryDate = new DateTime(2026, 1, 5),
                PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
            };
            seed.JournalEntries.Add(journal);
            seed.JournalLines.AddRange(
                new JournalLine
                {
                    JournalEntryId = journal.Id, GlAccountId = assetId,
                    Debit = 100m, Credit = 0m, Currency = "ZWG", ExchangeRate = 1m,
                    BaseDebit = 100m, BaseCredit = 0m
                },
                new JournalLine
                {
                    JournalEntryId = journal.Id, GlAccountId = equityId,
                    Debit = 0m, Credit = 100m, Currency = "ZWG", ExchangeRate = 1m,
                    BaseDebit = 0m, BaseCredit = 100m
                });
            seed.ExchangeRates.AddRange(
                new ExchangeRate
                {
                    CompanyId = subsidiaryId, FromCurrency = "ZWG", ToCurrency = "USD",
                    RateDate = new DateTime(2026, 1, 1), Rate = 0.50m,
                    RateSource = "RBZ daily bulletin", SourceReference = "RBZ/FX/2026-01-01"
                },
                new ExchangeRate
                {
                    CompanyId = subsidiaryId, FromCurrency = "USD", ToCurrency = "ZWG",
                    RateDate = new DateTime(2026, 1, 10), Rate = 1.80m,
                    RateSource = "Bank treasury quote", SourceReference = "TREASURY/2026-01-10",
                    IsManualOverride = true
                });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(parentId, tenantId: tenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(parentId, new DateTime(2026, 1, 31));

        Assert.Equal(2, result.RateEvidence.Count);
        var transactionDateRate = Assert.Single(result.RateEvidence, x =>
            x.EffectiveDate == new DateTime(2026, 1, 5));
        Assert.Equal("ZWG", transactionDateRate.FromCurrency);
        Assert.Equal("USD", transactionDateRate.ToCurrency);
        Assert.Equal(new DateTime(2026, 1, 1), transactionDateRate.RateDate);
        Assert.Equal(0.50m, transactionDateRate.QuotedRate);
        Assert.Equal(0.50m, transactionDateRate.AppliedRate);
        Assert.False(transactionDateRate.IsInverse);
        Assert.Equal("RBZ daily bulletin", transactionDateRate.RateSource);
        Assert.Equal("RBZ/FX/2026-01-01", transactionDateRate.SourceReference);

        var closingRate = Assert.Single(result.RateEvidence, x =>
            x.EffectiveDate == new DateTime(2026, 1, 31));
        Assert.Equal(new DateTime(2026, 1, 10), closingRate.RateDate);
        Assert.Equal(1.80m, closingRate.QuotedRate);
        Assert.Equal(1m / 1.80m, closingRate.AppliedRate);
        Assert.True(closingRate.IsInverse);
        Assert.True(closingRate.IsManualOverride);
        Assert.Equal("Bank treasury quote", closingRate.RateSource);
        Assert.Equal("TREASURY/2026-01-10", closingRate.SourceReference);

        var cta = Assert.Single(result.Lines, x => x.Code == "FX-CTA");
        Assert.InRange(Math.Abs(cta.ConsolidatedAmount - (50m - (100m / 1.80m))),
            0m, 0.000001m);
        Assert.Equal(0m, Math.Round(result.TotalAssets - result.TotalLiabilities - result.TotalGroupEquity, 6));
    }
    [PostgresFact]
    public async Task Consolidation_calculates_indirect_non_controlling_interest_across_the_full_ownership_chain()
    {
        await using var db = await TestDb.CreateAsync();
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var subsidiaryId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();
        var cashAccountId = Guid.NewGuid();
        var equityAccountId = Guid.NewGuid();
        var periodId = Guid.NewGuid();

        await using (var seed = db.NewContext(null))
        {
            seed.CustomerOrganisations.Add(new CustomerOrganisation
            {
                Id = tenantId, Name = "Indirect NCI test tenant", IsActive = true
            });
            seed.Companies.AddRange(
                new Company
                {
                    Id = parentId, TenantId = tenantId, Name = "NCI Parent",
                    BaseCurrency = "USD", FunctionalCurrency = "USD", IsActive = true
                },
                new Company
                {
                    Id = subsidiaryId, TenantId = tenantId, Name = "NCI Subsidiary",
                    BaseCurrency = "USD", FunctionalCurrency = "USD",
                    ParentCompanyId = parentId, OwnershipPercentage = 80m, IsActive = true
                },
                new Company
                {
                    Id = grandchildId, TenantId = tenantId, Name = "NCI Grandchild",
                    BaseCurrency = "USD", FunctionalCurrency = "USD",
                    ParentCompanyId = subsidiaryId, OwnershipPercentage = 70m, IsActive = true
                });
            seed.GlAccounts.AddRange(
                new GlAccount
                {
                    Id = cashAccountId, CompanyId = grandchildId, Code = "1000",
                    Name = "Cash", Type = GlAccountType.Asset, Currency = "USD"
                },
                new GlAccount
                {
                    Id = equityAccountId, CompanyId = grandchildId, Code = "3000",
                    Name = "Share Capital", Type = GlAccountType.Equity, Currency = "USD"
                });
            seed.AccountingPeriods.Add(new AccountingPeriod
            {
                Id = periodId, CompanyId = grandchildId, Year = 2026, PeriodNo = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 2, 1),
                Status = PeriodStatus.Open
            });
            var journal = new JournalEntry
            {
                CompanyId = grandchildId, EntryNo = "JE-NCI-001",
                Description = "Grandchild opening net assets", EntryDate = new DateTime(2026, 1, 5),
                PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
            };
            seed.JournalEntries.Add(journal);
            seed.JournalLines.AddRange(
                new JournalLine
                {
                    JournalEntryId = journal.Id, GlAccountId = cashAccountId,
                    Debit = 100m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                    BaseDebit = 100m, BaseCredit = 0m
                },
                new JournalLine
                {
                    JournalEntryId = journal.Id, GlAccountId = equityAccountId,
                    Debit = 0m, Credit = 100m, Currency = "USD", ExchangeRate = 1m,
                    BaseDebit = 0m, BaseCredit = 100m
                });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(parentId, tenantId: tenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(parentId, new DateTime(2026, 1, 31));

        Assert.Equal(44m, result.TotalNonControllingInterest);
        Assert.Equal(56m, result.TotalGroupEquity);
        Assert.Equal(100m, result.TotalAssets);
        Assert.Equal(0m, result.TotalLiabilities);
        Assert.Equal(0m, Math.Round(
            result.TotalAssets - result.TotalLiabilities - result.TotalGroupEquity -
            result.TotalNonControllingInterest, 6));
    }

}
