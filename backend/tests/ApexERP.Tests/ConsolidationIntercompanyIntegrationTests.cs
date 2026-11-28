using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests;

public sealed class ConsolidationIntercompanyIntegrationTests
{
    private sealed record SeededGroup(
        Guid TenantId, Guid ParentId, Guid CompanyAId, Guid CompanyBId,
        Guid IntercompanyAccountId);

    private static async Task<SeededGroup> SeedGroupAsync(TestDb db, decimal payableAmount,
        string buyerCurrency = "USD", decimal? buyerToGroupRate = null, bool addMatchedTransaction = false)
    {
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var companyAId = Guid.NewGuid();
        var companyBId = Guid.NewGuid();
        var intercompanyAccountId = Guid.NewGuid();
        var revenueAccountId = Guid.NewGuid();
        var payableAccountId = Guid.NewGuid();
        var expenseAccountId = Guid.NewGuid();
        var parentPeriodId = Guid.NewGuid();
        var periodAId = Guid.NewGuid();
        var periodBId = Guid.NewGuid();

        await using var seed = db.NewContext(null);
        seed.CustomerOrganisations.Add(new CustomerOrganisation
        {
            Id = tenantId, Name = "Intercompany consolidation tenant", IsActive = true
        });
        seed.Companies.AddRange(
            new Company
            {
                Id = parentId, TenantId = tenantId, Name = "Intercompany Parent",
                BaseCurrency = "USD", FunctionalCurrency = "USD", IsActive = true
            },
            new Company
            {
                Id = companyAId, TenantId = tenantId, Name = "Intercompany Seller",
                BaseCurrency = "USD", FunctionalCurrency = "USD",
                ParentCompanyId = parentId, OwnershipPercentage = 100m, IsActive = true
            },
            new Company
            {
                Id = companyBId, TenantId = tenantId, Name = "Intercompany Buyer",
                BaseCurrency = buyerCurrency, FunctionalCurrency = buyerCurrency,
                ParentCompanyId = parentId, OwnershipPercentage = 100m, IsActive = true
            });
        seed.GlAccounts.AddRange(
            new GlAccount
            {
                Id = intercompanyAccountId, CompanyId = companyAId, Code = "IC-100",
                Name = "Due from group company", Type = GlAccountType.Asset, Currency = "USD",
                IsIntercompanyAccount = true, IntercompanyCounterpartyCompanyId = companyBId
            },
            new GlAccount
            {
                Id = revenueAccountId, CompanyId = companyAId, Code = "4000",
                Name = "Intercompany revenue", Type = GlAccountType.Income, Currency = "USD"
            },
            new GlAccount
            {
                Id = payableAccountId, CompanyId = companyBId, Code = "IC-100",
                Name = "Due to group company", Type = GlAccountType.Liability, Currency = buyerCurrency,
                IsIntercompanyAccount = true, IntercompanyCounterpartyCompanyId = companyAId
            },
            new GlAccount
            {
                Id = expenseAccountId, CompanyId = companyBId, Code = "5000",
                Name = "Intercompany expense", Type = GlAccountType.Expense, Currency = buyerCurrency
            });
        seed.AccountingPeriods.AddRange(
            new AccountingPeriod
            {
                Id = parentPeriodId, CompanyId = parentId, Year = 2026, PeriodNo = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 31),
                Status = PeriodStatus.Closed
            },
            new AccountingPeriod
            {
                Id = periodAId, CompanyId = companyAId, Year = 2026, PeriodNo = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 31),
                Status = PeriodStatus.Closed
            },
            new AccountingPeriod
            {
                Id = periodBId, CompanyId = companyBId, Year = 2026, PeriodNo = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 31),
                Status = PeriodStatus.Closed
            });

        var sellerJournal = new JournalEntry
        {
            CompanyId = companyAId, EntryNo = "JE-IC-SELL",
            Description = "Intercompany sale", EntryDate = new DateTime(2026, 1, 15),
            PeriodId = periodAId, Source = JournalSource.Manual, Status = JournalStatus.Posted
        };
        var buyerJournal = new JournalEntry
        {
            CompanyId = companyBId, EntryNo = "JE-IC-BUY",
            Description = "Intercompany purchase", EntryDate = new DateTime(2026, 1, 15),
            PeriodId = periodBId, Source = JournalSource.Manual, Status = JournalStatus.Posted
        };
        seed.JournalEntries.AddRange(sellerJournal, buyerJournal);
        seed.JournalLines.AddRange(
            new JournalLine
            {
                JournalEntryId = sellerJournal.Id, GlAccountId = intercompanyAccountId,
                Debit = 100m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                BaseDebit = 100m, BaseCredit = 0m
            },
            new JournalLine
            {
                JournalEntryId = sellerJournal.Id, GlAccountId = revenueAccountId,
                Debit = 0m, Credit = 100m, Currency = "USD", ExchangeRate = 1m,
                BaseDebit = 0m, BaseCredit = 100m
            },
            new JournalLine
            {
                JournalEntryId = buyerJournal.Id, GlAccountId = expenseAccountId,
                Debit = payableAmount, Credit = 0m, Currency = buyerCurrency, ExchangeRate = 1m,
                BaseDebit = payableAmount, BaseCredit = 0m
            },
            new JournalLine
            {
                JournalEntryId = buyerJournal.Id, GlAccountId = payableAccountId,
                Debit = 0m, Credit = payableAmount, Currency = buyerCurrency, ExchangeRate = 1m,
                BaseDebit = 0m, BaseCredit = payableAmount
            });
        if (buyerToGroupRate.HasValue)
        {
            seed.ExchangeRates.Add(new ExchangeRate
            {
                CompanyId = companyBId, FromCurrency = buyerCurrency, ToCurrency = "USD",
                RateDate = new DateTime(2026, 1, 1), Rate = buyerToGroupRate.Value,
                RateSource = "Test treasury rate", SourceReference = "TEST/IC/2026-01-01"
            });
        }
        if (addMatchedTransaction)
        {
            seed.IntercompanyTransactions.Add(new IntercompanyTransaction
            {
                CompanyId = companyAId, FromCompanyId = companyAId, ToCompanyId = companyBId,
                TransactionDate = new DateTime(2026, 1, 15),
                Description = "Linked source and mirror journals",
                Amount = payableAmount, Currency = buyerCurrency,
                Status = IntercompanyStatus.Matched,
                SourceJournalEntryId = sellerJournal.Id,
                MirrorJournalEntryId = buyerJournal.Id
            });
        }
        await seed.SaveChangesAsync();
        return new SeededGroup(tenantId, parentId, companyAId, companyBId, intercompanyAccountId);
    }

    [PostgresFact]
    public async Task Reciprocal_intercompany_balances_are_eliminated_when_equal_and_opposite()
    {
        await using var db = await TestDb.CreateAsync();
        var group = await SeedGroupAsync(db, 100m);

        await using var ctx = db.NewContext(group.ParentId, tenantId: group.TenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(group.ParentId, new DateTime(2026, 1, 31));

        Assert.Empty(result.IntercompanyExceptions);
        Assert.Equal(100m, result.TotalEliminations);
        var intercompanyLine = Assert.Single(result.Lines, x => x.Code == "IC-100");
        Assert.Equal(0m, intercompanyLine.ConsolidatedAmount);
        Assert.All(result.PeriodEvidence, x => Assert.True(x.IsGroupPeriodAligned));
    }

    [PostgresFact]
    public async Task Reciprocal_intercompany_balances_are_eliminated_after_functional_currency_translation()
    {
        await using var db = await TestDb.CreateAsync();
        // Seller records USD 100; buyer records EUR 50. At USD 2 per EUR,
        // the reciprocal balances are equal in the group's reporting currency.
        var group = await SeedGroupAsync(db, 50m, buyerCurrency: "EUR", buyerToGroupRate: 2m, addMatchedTransaction: true);

        await using var ctx = db.NewContext(group.ParentId, tenantId: group.TenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(group.ParentId, new DateTime(2026, 1, 31));

        Assert.Empty(result.IntercompanyExceptions);
        Assert.Equal(200m, result.TotalEliminations);
        var intercompanyLine = Assert.Single(result.Lines, x => x.Code == "IC-100");
        Assert.Equal(0m, intercompanyLine.ConsolidatedAmount);
        var revenueLine = Assert.Single(result.Lines, x => x.Code == "4000");
        Assert.Equal(-100m, revenueLine.GroupAmount);
        Assert.Equal(0m, revenueLine.ConsolidatedAmount);
        Assert.Equal(100m, revenueLine.EliminatedAmount);
        var expenseLine = Assert.Single(result.Lines, x => x.Code == "5000");
        Assert.Equal(100m, expenseLine.GroupAmount);
        Assert.Equal(0m, expenseLine.ConsolidatedAmount);
        Assert.Equal(-100m, expenseLine.EliminatedAmount);
        var buyerRateEvidence = Assert.Single(result.RateEvidence, x =>
            x.CompanyId == group.CompanyBId && x.EffectiveDate == new DateTime(2026, 1, 31));
        Assert.Equal("EUR", buyerRateEvidence.FromCurrency);
        Assert.Equal("USD", buyerRateEvidence.ToCurrency);
        Assert.Equal(2m, buyerRateEvidence.AppliedRate);
        Assert.Equal("TEST/IC/2026-01-01", buyerRateEvidence.SourceReference);
    }

    [PostgresFact]
    public async Task Cross_currency_intercompany_difference_is_not_hidden_by_elimination()
    {
        await using var db = await TestDb.CreateAsync();
        // USD 100 receivable versus EUR 49 payable translated at USD 2/EUR = USD 98.
        var group = await SeedGroupAsync(db, 49m, buyerCurrency: "EUR", buyerToGroupRate: 2m, addMatchedTransaction: true);

        await using var ctx = db.NewContext(group.ParentId, tenantId: group.TenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(group.ParentId, new DateTime(2026, 1, 31));

        Assert.Equal(2, result.IntercompanyExceptions.Count);
        var exception = Assert.Single(result.IntercompanyExceptions, x => x.AccountCode == "IC-100");
        Assert.Equal(2m, exception.Difference);
        var pnlException = Assert.Single(result.IntercompanyExceptions, x => x.AccountCode.StartsWith("IC-TXN-"));
        Assert.Equal(-2m, pnlException.Difference);
        Assert.Equal(0m, result.TotalEliminations);
        var intercompanyLine = Assert.Single(result.Lines, x => x.Code == "IC-100");
        Assert.Equal(2m, intercompanyLine.ConsolidatedAmount);
        var revenueLine = Assert.Single(result.Lines, x => x.Code == "4000");
        Assert.Equal(-100m, revenueLine.ConsolidatedAmount);
        var expenseLine = Assert.Single(result.Lines, x => x.Code == "5000");
        Assert.Equal(98m, expenseLine.ConsolidatedAmount);
        Assert.Contains("do not agree", exception.Reason);
        Assert.Contains("do not agree", pnlException.Reason);
    }

    [PostgresFact]
    public async Task Unmatched_intercompany_balances_are_reported_and_neither_side_is_eliminated()
    {
        await using var db = await TestDb.CreateAsync();
        var group = await SeedGroupAsync(db, 95m);

        await using var ctx = db.NewContext(group.ParentId, tenantId: group.TenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(group.ParentId, new DateTime(2026, 1, 31));

        var exception = Assert.Single(result.IntercompanyExceptions);
        Assert.Equal("IC-100", exception.AccountCode);
        if (exception.CompanyAId == group.CompanyAId)
        {
            Assert.Equal(100m, exception.CompanyABalance);
            Assert.Equal(-95m, exception.CompanyBBalance);
        }
        else
        {
            Assert.Equal(-95m, exception.CompanyABalance);
            Assert.Equal(100m, exception.CompanyBBalance);
        }
        Assert.Equal(5m, exception.Difference);
        Assert.Equal(0m, result.TotalEliminations);
        var intercompanyLine = Assert.Single(result.Lines, x => x.Code == "IC-100");
        Assert.Equal(5m, intercompanyLine.ConsolidatedAmount);
        Assert.Contains("do not agree", exception.Reason);
    }
    [PostgresFact]
    public async Task Consolidation_is_blocked_when_a_group_entity_has_a_different_reporting_window()
    {
        await using var db = await TestDb.CreateAsync();
        var group = await SeedGroupAsync(db, 100m);

        await using (var subsidiary = db.NewContext(group.CompanyBId, tenantId: group.TenantId))
        {
            var period = await subsidiary.AccountingPeriods.SingleAsync();
            period.EndDate = new DateTime(2026, 2, 28);
            await subsidiary.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(group.ParentId, tenantId: group.TenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConsolidateAsync(group.ParentId, new DateTime(2026, 1, 31)));

        Assert.Contains("group reporting-period policy", error.Message);
        Assert.Contains("Intercompany Buyer", error.Message);
    }

    [PostgresFact]
    public async Task Unmatched_and_unlinked_intercompany_transactions_are_reported_without_pnl_elimination()
    {
        await using var db = await TestDb.CreateAsync();
        var group = await SeedGroupAsync(db, 100m);

        await using (var source = db.NewContext(group.CompanyAId, tenantId: group.TenantId))
        {
            source.IntercompanyTransactions.AddRange(
                new IntercompanyTransaction
                {
                    CompanyId = group.CompanyAId,
                    FromCompanyId = group.CompanyAId,
                    ToCompanyId = group.CompanyBId,
                    TransactionDate = new DateTime(2026, 1, 15),
                    Description = "Unmatched intercompany transaction",
                    Amount = 100m,
                    Currency = "USD",
                    Status = IntercompanyStatus.Unmatched
                },
                new IntercompanyTransaction
                {
                    CompanyId = group.CompanyAId,
                    FromCompanyId = group.CompanyAId,
                    ToCompanyId = group.CompanyBId,
                    TransactionDate = new DateTime(2026, 1, 16),
                    Description = "Matched status without journal links",
                    Amount = 75m,
                    Currency = "USD",
                    Status = IntercompanyStatus.Matched,
                    SourceJournalEntryId = Guid.NewGuid()
                });
            await source.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(group.ParentId, tenantId: group.TenantId);
        var service = new ConsolidationService(ctx, new ReportingService(ctx), new GroupScopeService(ctx));
        var result = await service.ConsolidateAsync(group.ParentId, new DateTime(2026, 1, 31));

        var transactionExceptions = result.IntercompanyExceptions
            .Where(x => x.AccountCode.StartsWith("IC-TXN-")).ToList();
        Assert.Equal(2, transactionExceptions.Count);
        Assert.Contains(transactionExceptions, x => x.Reason.Contains("unmatched"));
        Assert.Contains(transactionExceptions, x => x.Reason.Contains("missing its source/mirror"));
        Assert.Equal(100m, result.TotalEliminations);
    }

}
