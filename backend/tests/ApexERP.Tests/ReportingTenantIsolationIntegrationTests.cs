using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Xunit;

namespace ApexERP.Tests;

public sealed class ReportingTenantIsolationIntegrationTests
{
    [PostgresFact]
    public async Task Financial_report_sources_are_confined_to_the_requested_company()
    {
        await using var db = await TestDb.CreateAsync();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var periodA = Guid.NewGuid();
        var periodB = Guid.NewGuid();
        var cashA = Guid.NewGuid();
        var incomeA = Guid.NewGuid();
        var cashB = Guid.NewGuid();
        var incomeB = Guid.NewGuid();

        await using (var seed = db.NewContext(null))
        {
            seed.Companies.AddRange(
                new Company { Id = companyA, Name = "Report Tenant A", BaseCurrency = "USD", FunctionalCurrency = "USD" },
                new Company { Id = companyB, Name = "Report Tenant B", BaseCurrency = "USD", FunctionalCurrency = "USD" });
            seed.AccountingPeriods.AddRange(
                new AccountingPeriod { Id = periodA, CompanyId = companyA, Year = 2026, PeriodNo = 3, StartDate = new DateTime(2026, 3, 1), EndDate = new DateTime(2026, 3, 31) },
                new AccountingPeriod { Id = periodB, CompanyId = companyB, Year = 2026, PeriodNo = 3, StartDate = new DateTime(2026, 3, 1), EndDate = new DateTime(2026, 3, 31) });
            seed.GlAccounts.AddRange(
                new GlAccount { Id = cashA, CompanyId = companyA, Code = "A-100", Name = "Tenant A Cash", Type = GlAccountType.Asset, Currency = "USD" },
                new GlAccount { Id = incomeA, CompanyId = companyA, Code = "A-400", Name = "Tenant A Revenue", Type = GlAccountType.Income, Currency = "USD" },
                new GlAccount { Id = cashB, CompanyId = companyB, Code = "B-100", Name = "Tenant B Cash", Type = GlAccountType.Asset, Currency = "USD" },
                new GlAccount { Id = incomeB, CompanyId = companyB, Code = "B-400", Name = "Tenant B Revenue", Type = GlAccountType.Income, Currency = "USD" });

            var journalA = new JournalEntry
            {
                CompanyId = companyA, PeriodId = periodA, EntryNo = "TENANT-A-001",
                EntryDate = new DateTime(2026, 3, 15), Source = JournalSource.Manual, Status = JournalStatus.Posted
            };
            var journalB = new JournalEntry
            {
                CompanyId = companyB, PeriodId = periodB, EntryNo = "TENANT-B-001",
                EntryDate = new DateTime(2026, 3, 15), Source = JournalSource.Manual, Status = JournalStatus.Posted
            };
            seed.JournalEntries.AddRange(journalA, journalB);
            seed.JournalLines.AddRange(
                new JournalLine { JournalEntryId = journalA.Id, GlAccountId = cashA, Debit = 100m, Currency = "USD", ExchangeRate = 1m, BaseDebit = 100m },
                new JournalLine { JournalEntryId = journalA.Id, GlAccountId = incomeA, Credit = 100m, Currency = "USD", ExchangeRate = 1m, BaseCredit = 100m },
                new JournalLine { JournalEntryId = journalB.Id, GlAccountId = cashB, Debit = 900m, Currency = "USD", ExchangeRate = 1m, BaseDebit = 900m },
                new JournalLine { JournalEntryId = journalB.Id, GlAccountId = incomeB, Credit = 900m, Currency = "USD", ExchangeRate = 1m, BaseCredit = 900m });
            await seed.SaveChangesAsync();
        }

        await using var tenantContext = db.NewContext(companyA);
        var reports = new ReportingService(tenantContext);
        var trialBalance = await reports.GetTrialBalanceAsync(companyA, new DateTime(2026, 3, 31));
        var income = await reports.GetIncomeStatementAsync(companyA, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));
        var trend = await reports.GetFinancialTrendAsync(companyA, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        Assert.Equal(new[] { "A-100", "A-400" }, trialBalance.Select(x => x.Code).OrderBy(x => x).ToArray());
        Assert.DoesNotContain(trialBalance, x => x.Code.StartsWith("B-", StringComparison.Ordinal));
        Assert.Equal(100m, income.TotalIncome);
        Assert.Equal(0m, income.TotalExpense);
        Assert.Equal(100m, income.NetProfit);
        var march = Assert.Single(trend);
        Assert.Equal(100m, march.Income);
        Assert.Equal(0m, march.Expense);
        Assert.Equal(100m, march.NetProfit);
    }
}
