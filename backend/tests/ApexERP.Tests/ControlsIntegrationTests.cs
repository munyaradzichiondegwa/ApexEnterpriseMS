
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests
{
    /// <summary>
    /// Integration tests for the controls and automation added from the APEXERP specification. Like the other integration tests they
    /// need APEX_TEST_DB and apply the repository's real migrations to a throw-away database.
    /// </summary>
    public class ControlsIntegrationTests
    {
        private const string Secret = "integration-test-secret-0123456789-0123456789";
        private static readonly Guid Preparer = Guid.NewGuid();
        private static readonly Guid Approver = Guid.NewGuid();

        private static GlPostingService Gl(ApexErpDbContext db) => new(db, new AuditService(db), new JournalControlOptions());
        private static GovernanceService Gov(ApexErpDbContext db) => new(db, new AuditService(db), new SettingsService(db));

        private static async Task<(Guid Company, Guid Asset, Guid Expense, Guid Liability, Guid Cash)> SeedAsync(TestDb db)
        {
            var company = Guid.NewGuid();
            await using var ctx = db.NewContext(company);
            ctx.Companies.Add(new Company { Id = company, Name = "Test Co", BaseCurrency = "USD", FunctionalCurrency = "USD" });
            var asset = new GlAccount { CompanyId = company, Code = "1300", Name = "Prepayments", Type = GlAccountType.Asset };
            var expense = new GlAccount { CompanyId = company, Code = "6000", Name = "Insurance expense", Type = GlAccountType.Expense };
            var liability = new GlAccount { CompanyId = company, Code = "2300", Name = "Accrued expenses", Type = GlAccountType.Liability };
            var cash = new GlAccount { CompanyId = company, Code = "1000", Name = "Cash", Type = GlAccountType.Asset };
            ctx.GlAccounts.AddRange(asset, expense, liability, cash);
            await ctx.SaveChangesAsync();
            return (company, asset.Id, expense.Id, liability.Id, cash.Id);
        }

        // ---- prepayments: amortise once, however often the job runs ----------------------------------------------------

        [PostgresFact]
        public async Task Prepayment_amortisation_posts_each_month_exactly_once()
        {
            await using var db = await TestDb.CreateAsync();
            var (company, asset, expense, _, _) = await SeedAsync(db);
            Guid scheduleId;
            await using (var ctx = db.NewContext(company))
            {
                var svc = new PrepaymentService(ctx, Gl(ctx), Gov(ctx), new AuditService(ctx));
                var p = await svc.CreateAsync(company, "Annual insurance", 1200m, "USD", new DateTime(2026, 1, 1), 12, asset, expense, null, null, Preparer);
                await svc.ApproveAsync(company, p.Id, Approver);
                scheduleId = p.Id;
            }
            await using (var ctx = db.NewContext(company))
            {
                var svc = new PrepaymentService(ctx, Gl(ctx), Gov(ctx), new AuditService(ctx));
                var first = await svc.RunDueAsync(company, new DateTime(2026, 3, 15), null);
                Assert.Equal(3, first.Posted);                       // January, February, March
                var again = await svc.RunDueAsync(company, new DateTime(2026, 3, 15), null);
                Assert.Equal(0, again.Posted);                       // a re-run changes nothing
            }
            await using (var ctx = db.NewContext(company))
            {
                var balance = await new PrepaymentService(ctx, Gl(ctx), Gov(ctx), new AuditService(ctx)).BalanceAsync(company, scheduleId);
                Assert.Equal(900m, (decimal)balance.GetType().GetProperty("Remaining")!.GetValue(balance)!);
                var journals = await ctx.JournalEntries.CountAsync(j => j.CompanyId == company && j.Source == JournalSource.Prepayment);
                Assert.Equal(3, journals);
            }
        }

        [PostgresFact]
        public async Task A_preparer_cannot_approve_their_own_prepayment()
        {
            await using var db = await TestDb.CreateAsync();
            var (company, asset, expense, _, _) = await SeedAsync(db);
            await using var ctx = db.NewContext(company);
            var svc = new PrepaymentService(ctx, Gl(ctx), Gov(ctx), new AuditService(ctx));
            var p = await svc.CreateAsync(company, "Rent", 600m, "USD", new DateTime(2026, 1, 1), 6, asset, expense, null, null, Preparer);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApproveAsync(company, p.Id, Preparer));
        }

        // ---- accruals: post, auto-reverse once -------------------------------------------------------------------------

        [PostgresFact]
        public async Task Accrual_is_posted_then_reversed_in_the_next_period_only_once()
        {
            await using var db = await TestDb.CreateAsync();
            var (company, _, expense, liability, _) = await SeedAsync(db);
            Guid id;
            await using (var ctx = db.NewContext(company))
            {
                var svc = new AccrualService(ctx, Gl(ctx), Gov(ctx), new AuditService(ctx));
                var a = await svc.CreateAsync(company, "October electricity", 450m, "USD", new DateTime(2026, 10, 31), expense, liability, null, true, null, Preparer);
                await svc.ApproveAsync(company, a.Id, Approver);
                await svc.PostAsync(company, a.Id, Approver);
                id = a.Id;
            }
            await using (var ctx = db.NewContext(company))
            {
                var svc = new AccrualService(ctx, Gl(ctx), Gov(ctx), new AuditService(ctx));
                Assert.Equal(0, (await svc.ReverseDueAsync(company, new DateTime(2026, 10, 31), null)).Posted);   // not yet due
                Assert.Equal(1, (await svc.ReverseDueAsync(company, new DateTime(2026, 11, 2), null)).Posted);
                Assert.Equal(0, (await svc.ReverseDueAsync(company, new DateTime(2026, 11, 2), null)).Posted);   // idempotent
            }
            await using (var ctx = db.NewContext(company))
            {
                var a = await ctx.AccrualSchedules.FirstAsync(x => x.Id == id);
                Assert.Equal("Reversed", a.Status);
                Assert.NotNull(a.ReversalJournalEntryId);
            }
        }

        // ---- sign-in security --------------------------------------------------------------------------------------------

        [PostgresFact]
        public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
        {
            await using var db = await TestDb.CreateAsync();
            await using var ctx = db.NewContext(null);
            var auth = new AuthService(ctx, Secret, "apex");
            await auth.RegisterCompanyAndAdminAsync("Lock Co", "lock@example.com", "Correct-Horse-9!", "A", "B");
            for (var i = 0; i < 5; i++)
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync("lock@example.com", "wrong-password-" + i));
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync("lock@example.com", "Correct-Horse-9!"));
            Assert.Contains("Too many", ex.Message);
        }

        [PostgresFact]
        public async Task Refresh_tokens_rotate_and_reuse_revokes_the_whole_session()
        {
            await using var db = await TestDb.CreateAsync();
            await using var ctx = db.NewContext(null);
            var auth = new AuthService(ctx, Secret, "apex");
            await auth.RegisterCompanyAndAdminAsync("Rotate Co", "rot@example.com", "Correct-Horse-9!", "A", "B");
            var first = await auth.LoginAsync("rot@example.com", "Correct-Horse-9!");
            var second = await auth.RefreshAsync(first.RefreshToken, "127.0.0.1");
            Assert.True(second.RefreshToken != first.RefreshToken);
            // The first token was already used: presenting it again is treated as theft and kills the family.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.RefreshAsync(first.RefreshToken, "203.0.113.9"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.RefreshAsync(second.RefreshToken, "127.0.0.1"));
        }

        [PostgresFact]
        public async Task Changing_the_password_ends_every_existing_session()
        {
            await using var db = await TestDb.CreateAsync();
            await using var ctx = db.NewContext(null);
            var auth = new AuthService(ctx, Secret, "apex");
            var user = await auth.RegisterCompanyAndAdminAsync("Pwd Co", "pwd@example.com", "Correct-Horse-9!", "A", "B");
            var session = await auth.LoginAsync("pwd@example.com", "Correct-Horse-9!");
            await using var scoped = db.NewContext(user.CompanyId, user.Id);
            await new AuthService(scoped, Secret, "apex").ChangePasswordAsync(user.Id, "Correct-Horse-9!", "Another-Strong-Pass-7!");
            await using var refreshCtx = db.NewContext(null);
            var refreshAuth = new AuthService(refreshCtx, Secret, "apex");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => refreshAuth.RefreshAsync(session.RefreshToken, null));
        }

        // ---- database-level guards (AppendOnlyDatabaseGuards migration) ---------------------------------------------------

        [PostgresFact]
        public async Task The_database_itself_refuses_to_change_the_audit_log_or_a_posted_journal()
        {
            await using var db = await TestDb.CreateAsync();
            var (company, _, expense, liability, _) = await SeedAsync(db);
            await using var ctx = db.NewContext(company, Approver);
            var je = await Gl(ctx).PostAsync(company, new DateTime(2026, 10, 15), JournalSource.Manual, "guard test", "G-1",
                new[] { new GlLineRequest(expense, 10m, 0m, "USD", 1m, null, "t"), new GlLineRequest(liability, 0m, 10m, "USD", 1m, null, "t") });

            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync("UPDATE \"AuditLogs\" SET \"Action\" = 'tampered'"));
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLogs\""));
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync("UPDATE \"JournalLines\" SET \"BaseDebit\" = 1"));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"JournalEntries\" SET \"Description\" = 'edited' WHERE \"Id\" = {je.Id}"));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"JournalEntries\" WHERE \"Id\" = {je.Id}"));
        }

        // ---- control self-tests (A-H) on clean data ---------------------------------------------------------------------

        [PostgresFact]
        public async Task Self_tests_pass_on_clean_ledger_data()
        {
            await using var db = await TestDb.CreateAsync();
            var (company, _, expense, liability, _) = await SeedAsync(db);
            await using (var ctx = db.NewContext(company, Approver))
                await Gl(ctx).PostAsync(company, new DateTime(2026, 10, 15), JournalSource.Manual, "clean", "C-1",
                    new[] { new GlLineRequest(expense, 25m, 0m, "USD", 1m, null, "t"), new GlLineRequest(liability, 0m, 25m, "USD", 1m, null, "t") });
            await using (var ctx = db.NewContext(company, Approver))
            {
                var gov = Gov(ctx);
                var results = await new ControlMonitoringService(ctx, gov, new SettingsService(ctx), new AuditService(ctx)).RunSelfTestsAsync(company);
                Assert.Equal(8, results.Count);
                Assert.True(results.All(r => r.Passed), string.Join("; ", results.Where(r => !r.Passed).Select(r => r.Code + ": " + r.Details)));
            }
        }

        // ---- idempotency table ---------------------------------------------------------------------------------------------

        [PostgresFact]
        public async Task The_same_idempotency_key_cannot_be_stored_twice_for_one_request_scope()
        {
            await using var db = await TestDb.CreateAsync();
            var company = Guid.NewGuid();
            await using var ctx = db.NewContext(company);
            ctx.IdempotencyRecords.Add(new IdempotencyRecord { CompanyId = company, Scope = "post /api/v1/ap/payments", Key = "key-12345678", RequestHash = "h", ResponseStatus = 200 });
            await ctx.SaveChangesAsync();
            await using var ctx2 = db.NewContext(company);
            ctx2.IdempotencyRecords.Add(new IdempotencyRecord { CompanyId = company, Scope = "post /api/v1/ap/payments", Key = "key-12345678", RequestHash = "h", ResponseStatus = 200 });
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        }
    }
}
