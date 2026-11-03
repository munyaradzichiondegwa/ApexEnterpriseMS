using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ApexERP.Tests
{
    /// <summary>
    /// Integration tests against a REAL PostgreSQL database. They run only when APEX_TEST_DB is set to a connection string
    /// whose user may create databases (for example "Host=localhost;Username=postgres;Password=...;Database=postgres");
    /// otherwise they are skipped. Each test creates a throw-away database, applies the repository's real EF migrations to it
    /// (so these tests also prove the migrations apply), and drops it afterwards.
    /// </summary>
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public const string EnvironmentVariable = "APEX_TEST_DB";

        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
                Skip = $"Set {EnvironmentVariable} to a PostgreSQL connection string to run the integration tests.";
        }
    }

    internal sealed class TestDb : IAsyncDisposable
    {
        private readonly string _serverConnection;
        private readonly string _name;
        public string ConnectionString { get; }

        private TestDb(string serverConnection, string name)
        {
            _serverConnection = serverConnection;
            _name = name;
            ConnectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = name }.ConnectionString;
        }

        public static async Task<TestDb> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvironmentVariable)!;
            var name = "apex_it_" + Guid.NewGuid().ToString("N");
            await Execute(server, $"CREATE DATABASE \"{name}\"");

            var db = new TestDb(server, name);
            await using var ctx = db.NewContext(null);
            await ctx.Database.MigrateAsync();      // the real migrations, exactly as production applies them
            return db;
        }

        /// <summary>A context scoped to one company (null = no tenant, which must see nothing).</summary>
        public ApexErpDbContext NewContext(Guid? companyId, Guid? userId = null)
        {
            var options = new DbContextOptionsBuilder<ApexErpDbContext>().UseNpgsql(ConnectionString).Options;
            return new ApexErpDbContext(options) { CurrentCompanyId = companyId, CurrentUserId = userId };
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(_serverConnection, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
        }

        private static async Task Execute(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    public class PostgresIntegrationTests
    {
        private static readonly DateTime Oct15 = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

        private static GlPostingService Gl(ApexErpDbContext db, bool allowSelfApproval = false) =>
            new(db, new AuditService(db), new JournalControlOptions { AllowSelfApproval = allowSelfApproval });

        private static GlLineRequest Dr(Guid account, decimal amount, Guid? fund = null, Guid? grant = null, Guid? costCentre = null) =>
            new(account, amount, 0m, "USD", 1m, costCentre, "test", fund, grant);

        private static GlLineRequest Cr(Guid account, decimal amount, Guid? fund = null, Guid? grant = null, Guid? costCentre = null) =>
            new(account, 0m, amount, "USD", 1m, costCentre, "test", fund, grant);

        private static async Task<(Guid Cash, Guid Revenue)> SeedAccountsAsync(TestDb db, Guid company)
        {
            await using var ctx = db.NewContext(company);
            var cash = new GlAccount { CompanyId = company, Code = "1000", Name = "Cash", Type = GlAccountType.Asset };
            var revenue = new GlAccount { CompanyId = company, Code = "4000", Name = "Revenue", Type = GlAccountType.Income };
            ctx.GlAccounts.AddRange(cash, revenue);
            await ctx.SaveChangesAsync();
            return (cash.Id, revenue.Id);
        }

        private static async Task<JournalEntry> PostAsync(TestDb db, Guid company, DateTime date, params GlLineRequest[] lines)
        {
            await using var ctx = db.NewContext(company);
            return await Gl(ctx).PostAsync(company, date, JournalSource.Manual, "test", null, lines);
        }

        // ---- audit finding 9: tenant isolation fails closed ------------------------------------------------------------

        [PostgresFact]
        public async Task Tenant_filter_fails_closed_and_isolates_companies()
        {
            await using var db = await TestDb.CreateAsync();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await SeedAccountsAsync(db, a);
            await SeedAccountsAsync(db, b);

            await using (var noTenant = db.NewContext(null))
                Assert.Empty(await noTenant.GlAccounts.ToListAsync());

            await using (var asA = db.NewContext(a))
            {
                var visible = await asA.GlAccounts.ToListAsync();
                Assert.Equal(2, visible.Count);
                Assert.All(visible, x => Assert.Equal(a, x.CompanyId));
            }

            await using (var everything = db.NewContext(null))
                Assert.Equal(4, await everything.GlAccounts.IgnoreQueryFilters().CountAsync());   // only an explicit opt-in sees all tenants
        }

        // ---- audit finding 5: concurrency-safe numbering ---------------------------------------------------------------

        [PostgresFact]
        public async Task Concurrent_postings_receive_unique_gapless_journal_numbers()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);

            // 8 requests at once, 5 postings each, none of them pre-creating the period or number.
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await using var ctx = db.NewContext(company);
                    await Gl(ctx).PostAsync(company, Oct15, JournalSource.Manual, "load", null, new[] { Dr(cash, 10m), Cr(revenue, 10m) });
                }
            }));

            await using var check = db.NewContext(company);
            var numbers = (await check.JournalEntries.Select(j => j.EntryNo).ToListAsync()).OrderBy(n => n).ToList();
            Assert.Equal(Enumerable.Range(1, 40).Select(n => $"JE-202610-{n:D5}").ToList(), numbers);
            Assert.Equal(1, await check.AccountingPeriods.CountAsync());
            Assert.Equal(400m, (await check.GlAccounts.SingleAsync(a => a.Id == cash)).CurrentBalance);   // atomic balance increments: none lost
        }

        // ---- audit findings 4 and 6: atomic, single, tag-preserving reversal --------------------------------------------

        [PostgresFact]
        public async Task Reversal_keeps_dimension_tags_and_can_happen_only_once()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);
            var fund = Guid.NewGuid();
            var grant = Guid.NewGuid();
            var costCentre = Guid.NewGuid();

            var original = await PostAsync(db, company, Oct15, Dr(cash, 50m, fund, grant, costCentre), Cr(revenue, 50m, fund, grant, costCentre));

            JournalEntry reversal;
            await using (var ctx = db.NewContext(company))
                reversal = await Gl(ctx).ReverseAsync(company, original.Id, Oct15.AddDays(1), "entered in error");

            await using var check = db.NewContext(company);
            var reloaded = await check.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.Id == reversal.Id);
            Assert.Equal(original.Id, reloaded.ReversalOfId);
            Assert.All(reloaded.Lines, l =>
            {
                Assert.Equal(fund, l.FundCentreId);
                Assert.Equal(grant, l.GrantId);
                Assert.Equal(costCentre, l.CostCentreId);
            });
            var cashLine = reloaded.Lines.Single(l => l.GlAccountId == cash);
            Assert.Equal(0m, cashLine.Debit);
            Assert.Equal(50m, cashLine.Credit);
            Assert.Equal(JournalStatus.Reversed, (await check.JournalEntries.SingleAsync(j => j.Id == original.Id)).Status);
            Assert.Equal(0m, (await check.GlAccounts.SingleAsync(a => a.Id == cash)).CurrentBalance);

            await using (var again = db.NewContext(company))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Gl(again).ReverseAsync(company, original.Id, Oct15.AddDays(2), "twice"));
            Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.ReversalOfId == original.Id));
        }

        [PostgresFact]
        public async Task A_failed_reversal_leaves_nothing_behind()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);

            var original = await PostAsync(db, company, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), Dr(cash, 50m), Cr(revenue, 50m));
            await PostAsync(db, company, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), Dr(cash, 1m), Cr(revenue, 1m));   // creates the October period

            await using (var ctx = db.NewContext(company))
            {
                var october = await ctx.AccountingPeriods.SingleAsync(p => p.Year == 2026 && p.PeriodNo == 10);
                await Gl(ctx).ClosePeriodAsync(company, october.Id, Guid.NewGuid());
            }

            await using (var ctx = db.NewContext(company))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Gl(ctx).ReverseAsync(company, original.Id, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), "too late"));

            await using var check = db.NewContext(company);
            Assert.Equal(JournalStatus.Posted, (await check.JournalEntries.SingleAsync(j => j.Id == original.Id)).Status);
            Assert.Equal(0, await check.JournalEntries.CountAsync(j => j.ReversalOfId != null));
            Assert.Equal(51m, (await check.GlAccounts.SingleAsync(a => a.Id == cash)).CurrentBalance);   // unchanged by the failed attempt
        }

        // ---- audit finding 3 (and the reversed-original / fiscal-year follow-ups): reports --------------------------------

        [PostgresFact]
        public async Task Reports_include_inactive_accounts_and_reversed_originals_and_always_balance()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);

            await PostAsync(db, company, new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc), Dr(cash, 300m), Cr(revenue, 300m));   // prior year, never closed
            var reversed = await PostAsync(db, company, new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc), Dr(cash, 100m), Cr(revenue, 100m));
            await using (var ctx = db.NewContext(company))
                await Gl(ctx).ReverseAsync(company, reversed.Id, new DateTime(2026, 4, 11, 0, 0, 0, DateTimeKind.Utc), "customer cancelled");
            await PostAsync(db, company, new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), Dr(cash, 40m), Cr(revenue, 40m));

            await using (var ctx = db.NewContext(company))                      // deactivate AFTER it has history
            {
                var account = await ctx.GlAccounts.SingleAsync(a => a.Id == revenue);
                account.IsActive = false;
                await ctx.SaveChangesAsync();
            }

            await using var reporting = db.NewContext(company);
            var asOf = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
            var reports = new ReportingService(reporting);

            var trialBalance = await reports.GetTrialBalanceAsync(company, asOf);
            Assert.Equal(340m, trialBalance.Single(l => l.Code == "1000").Debit);          // 300 + (100 - 100) + 40
            Assert.Equal(340m, trialBalance.Single(l => l.Code == "4000").Credit);         // inactive account still reported
            Assert.Equal(trialBalance.Sum(l => l.Debit), trialBalance.Sum(l => l.Credit));

            var incomeStatement = await reports.GetIncomeStatementAsync(company, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), asOf);
            Assert.Equal(40m, incomeStatement.TotalIncome);                                // the reversed 100 nets to zero

            var balanceSheet = await reports.GetBalanceSheetAsync(company, asOf);
            Assert.True(balanceSheet.IsBalanced, $"Balance sheet difference {balanceSheet.Difference}");
            Assert.Equal(300m, balanceSheet.Equity.Single(l => l.Code == "RE-PY").Credit);
            Assert.Equal(40m, balanceSheet.Equity.Single(l => l.Code == "RE-YTD").Credit);

            var integrity = await reports.GetIntegrityCheckAsync(company, asOf);
            Assert.True(integrity.TrialBalanceBalanced);
            Assert.Equal(0, integrity.UnbalancedPostedJournals);

            await using var posting = db.NewContext(company);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Gl(posting).PostAsync(company, asOf, JournalSource.Manual, "to inactive", null, new[] { Dr(cash, 1m), Cr(revenue, 1m) }));
        }

        // ---- audit finding 2 / phase 2: approval workflow ----------------------------------------------------------------

        [PostgresFact]
        public async Task Manual_journals_need_a_different_approver_and_follow_the_lifecycle()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);
            var preparer = Guid.NewGuid();
            var approver = Guid.NewGuid();

            JournalEntry draft;
            await using (var ctx = db.NewContext(company, preparer))
                draft = await Gl(ctx).CreateDraftAsync(company, preparer, Oct15, "sale", "REF-1", new[] { Dr(cash, 100m), Cr(revenue, 100m) });
            Assert.Equal(JournalStatus.Draft, draft.Status);
            Assert.True(draft.EntryNo.StartsWith("DR-"));

            await using (var ctx = db.NewContext(company, preparer)) await Gl(ctx).SubmitAsync(company, preparer, draft.Id);

            await using (var ctx = db.NewContext(company, preparer))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Gl(ctx).ApproveAsync(company, preparer, draft.Id, null));   // segregation of duties
            await using (var ctx = db.NewContext(company, approver))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Gl(ctx).PostApprovedAsync(company, approver, draft.Id));    // not approved yet

            await using (var ctx = db.NewContext(company, approver)) await Gl(ctx).ApproveAsync(company, approver, draft.Id, "ok");

            JournalEntry posted;
            await using (var ctx = db.NewContext(company, approver)) posted = await Gl(ctx).PostApprovedAsync(company, approver, draft.Id);
            Assert.Equal(JournalStatus.Posted, posted.Status);
            Assert.True(posted.EntryNo.StartsWith("JE-"));

            await using (var ctx = db.NewContext(company, approver))
                Assert.Equal(posted.EntryNo, (await Gl(ctx).PostApprovedAsync(company, approver, draft.Id)).EntryNo);   // repeat is a no-op

            await using var check = db.NewContext(company);
            Assert.Equal(100m, (await check.GlAccounts.SingleAsync(a => a.Id == cash)).CurrentBalance);                   // posted exactly once
            var history = await Gl(check).GetJournalHistoryAsync(company, draft.Id);
            Assert.Equal(new[] { "Journal.DraftCreated", "Journal.Submitted", "Journal.Approved", "Journal.Posted" }, history.Select(h => h.Action).ToArray());
        }

        [PostgresFact]
        public async Task A_period_with_unposted_journals_cannot_close_and_a_closed_period_accepts_no_postings()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);
            var user = Guid.NewGuid();

            JournalEntry draft;
            await using (var ctx = db.NewContext(company, user))
                draft = await Gl(ctx).CreateDraftAsync(company, user, Oct15, "pending", null, new[] { Dr(cash, 5m), Cr(revenue, 5m) });

            Guid periodId;
            await using (var ctx = db.NewContext(company)) periodId = (await ctx.AccountingPeriods.SingleAsync(p => p.Year == 2026 && p.PeriodNo == 10)).Id;

            await using (var ctx = db.NewContext(company, user))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Gl(ctx).ClosePeriodAsync(company, periodId, user));
                Assert.Contains("unposted", ex.Message);
            }

            await using (var ctx = db.NewContext(company, user)) await Gl(ctx).CancelAsync(company, user, draft.Id, "not needed");
            await using (var ctx = db.NewContext(company, user)) await Gl(ctx).ClosePeriodAsync(company, periodId, user);

            await using (var ctx = db.NewContext(company))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Gl(ctx).PostAsync(company, Oct15, JournalSource.Manual, "late", null, new[] { Dr(cash, 1m), Cr(revenue, 1m) }));
                Assert.Contains("cannot accept new postings", ex.Message);
            }

            await using (var ctx = db.NewContext(company, user)) await Gl(ctx).ReopenPeriodAsync(company, periodId, user, "correction needed");
            await PostAsync(db, company, Oct15, Dr(cash, 1m), Cr(revenue, 1m));   // accepted again
        }

        // ---- immutability and database safeguards -------------------------------------------------------------------------

        [PostgresFact]
        public async Task Posted_history_cannot_be_edited_and_the_database_rejects_bad_lines()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            var (cash, revenue) = await SeedAccountsAsync(db, company);
            var posted = await PostAsync(db, company, Oct15, Dr(cash, 25m), Cr(revenue, 25m));

            await using (var ctx = db.NewContext(company))
            {
                var journal = await ctx.JournalEntries.SingleAsync(j => j.Id == posted.Id);
                journal.Description = "tampered";
                await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
            }
            await using (var ctx = db.NewContext(company))
            {
                var line = await ctx.JournalLines.FirstAsync(l => l.JournalEntryId == posted.Id);
                line.Debit = 1m;
                await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
            }
            await using (var ctx = db.NewContext(company))
            {
                ctx.JournalEntries.Remove(await ctx.JournalEntries.SingleAsync(j => j.Id == posted.Id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
            }
            await using (var ctx = db.NewContext(company))
            {
                var audit = await ctx.AuditLogs.FirstAsync();
                audit.Action = "rewritten";
                await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
            }

            // Bypass the application entirely: the database itself refuses a negative line and a second reversal.
            await using (var ctx = db.NewContext(company))
            {
                ctx.JournalLines.Add(new JournalLine { JournalEntryId = posted.Id, GlAccountId = cash, Debit = -5m, Credit = 0m, Currency = "USD", ExchangeRate = 1m });
                await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            }
        }
    }
}
