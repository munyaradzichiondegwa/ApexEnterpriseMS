using System.Security.Cryptography;
using System.Text;

using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        public async Task Tenant_request_validation_rejects_revoked_membership_and_changed_company_role()
        {
            await using var db = await TestDb.CreateAsync();
            await using var context = db.NewContext(null);
            var auth = new AuthService(context, Secret, "apex");
            var user = await auth.RegisterCompanyAndAdminAsync("Membership Co", "membership@example.com", "Correct-Horse-9!", "A", "B");
            var tenantId = await context.Companies.IgnoreQueryFilters()
                .Where(x => x.Id == user.CompanyId).Select(x => x.TenantId).SingleAsync();
            var company = new Company { TenantId = tenantId, Name = "Membership Subsidiary", BaseCurrency = "USD", FunctionalCurrency = "USD" };
            var role = new Role { CompanyId = company.Id, Name = "Subsidiary User", PermissionsJson = "[\"reports.financial.read\"]" };
            var replacementRole = new Role { CompanyId = company.Id, Name = "Replacement User", PermissionsJson = "[\"reports.financial.read\"]" };
            context.Companies.Add(company);
            context.Roles.AddRange(role, replacementRole);
            var membership = new CompanyMembership { CompanyId = company.Id, UserId = user.Id, MembershipRole = role.Name, RoleId = role.Id, IsActive = true, ValidFrom = DateTime.UtcNow };
            context.CompanyMemberships.Add(membership);
            await context.SaveChangesAsync();

            var homeRole = await context.Roles.IgnoreQueryFilters().SingleAsync(x => x.Id == user.RoleId);
            Assert.True(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId!.Value, user.CompanyId,
                user.RoleId, homeRole.Name, homeRole.PermissionsJson));
            Assert.True(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId.Value, company.Id,
                role.Id, role.Name, role.PermissionsJson));

            membership.RoleId = replacementRole.Id;
            membership.MembershipRole = replacementRole.Name;
            await context.SaveChangesAsync();
            Assert.False(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId.Value, company.Id,
                role.Id, role.Name, role.PermissionsJson));
            Assert.True(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId.Value, company.Id,
                replacementRole.Id, replacementRole.Name, replacementRole.PermissionsJson));

            replacementRole.PermissionsJson = "[\"reports.financial.read\",\"ap.write\"]";
            await context.SaveChangesAsync();
            Assert.False(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId.Value, company.Id,
                                replacementRole.Id, replacementRole.Name, "[\"reports.financial.read\"]"));
            Assert.True(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId.Value, company.Id,
                replacementRole.Id, replacementRole.Name, replacementRole.PermissionsJson));

            membership.IsActive = false;
            membership.ValidTo = DateTime.UtcNow;
            await context.SaveChangesAsync();
            Assert.False(await TenantMembershipValidator.IsValidAsync(context, user.Id, tenantId.Value, company.Id,
                replacementRole.Id, replacementRole.Name, replacementRole.PermissionsJson));
        }

        [PostgresFact]
        public async Task Changing_the_password_revokes_sessions_across_all_companies_in_the_same_tenant()
        {
            await using var db = await TestDb.CreateAsync();
            await using var ctx = db.NewContext(null);
            var auth = new AuthService(ctx, Secret, "apex");
            var user = await auth.RegisterCompanyAndAdminAsync("Pwd Co", "pwd@example.com", "Correct-Horse-9!", "A", "B");
            var tenantId = await ctx.Companies.IgnoreQueryFilters()
                .Where(x => x.Id == user.CompanyId).Select(x => x.TenantId).SingleAsync();
            var subsidiary = new Company
            {
                TenantId = tenantId, Name = "Pwd Subsidiary", BaseCurrency = "USD", FunctionalCurrency = "USD"
            };
            var subsidiaryRole = new Role
            {
                CompanyId = subsidiary.Id, Name = "Subsidiary Accountant",
                PermissionsJson = "[\"reports.financial.read\"]"
            };
            ctx.Companies.Add(subsidiary);
            ctx.Roles.Add(subsidiaryRole);
            ctx.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = subsidiary.Id, UserId = user.Id, RoleId = subsidiaryRole.Id,
                MembershipRole = subsidiaryRole.Name, IsActive = true, ValidFrom = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var session = await auth.LoginAsync("pwd@example.com", "Correct-Horse-9!");
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.RefreshToken)));
            var stored = await ctx.RefreshTokens.IgnoreQueryFilters().SingleAsync(x => x.TokenHash == tokenHash);
            var subsidiarySession = await auth.IssueTokensForCompanyAsync(user.Id, subsidiary.Id, "127.0.0.1", stored.FamilyId);

            await using var scoped = db.NewContext(user.CompanyId, user.Id, tenantId);
            await new AuthService(scoped, Secret, "apex").ChangePasswordAsync(user.Id, "Correct-Horse-9!", "Another-Strong-Pass-7!");

            await using var refreshCtx = db.NewContext(null);
            var refreshAuth = new AuthService(refreshCtx, Secret, "apex");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => refreshAuth.RefreshAsync(session.RefreshToken, null));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => refreshAuth.RefreshAsync(subsidiarySession.RefreshToken, null));
        }

        [PostgresFact]
        public async Task Company_switch_uses_the_target_company_role_and_rejects_revoked_or_cross_tenant_membership()
        {
            await using var db = await TestDb.CreateAsync();
            await using var ctx = db.NewContext(null);
            var auth = new AuthService(ctx, Secret, "apex");
            var user = await auth.RegisterCompanyAndAdminAsync(
                "Multi-company tenant", "multi-company@example.com", "Correct-Horse-9!", "Multi", "Company");
            var tenantId = await ctx.Companies.IgnoreQueryFilters()
                .Where(x => x.Id == user.CompanyId).Select(x => x.TenantId).SingleAsync();

            var targetCompany = new Company
            {
                TenantId = tenantId, Name = "Subsidiary", BaseCurrency = "USD", FunctionalCurrency = "USD"
            };
            var targetRole = new Role
            {
                CompanyId = targetCompany.Id, Name = "Subsidiary Accountant",
                PermissionsJson = "[\"reports.financial.read\"]"
            };
            ctx.Companies.Add(targetCompany);
            ctx.Roles.Add(targetRole);
            var membership = new CompanyMembership
            {
                CompanyId = targetCompany.Id, UserId = user.Id, RoleId = targetRole.Id,
                MembershipRole = targetRole.Name, IsActive = true, ValidFrom = DateTime.UtcNow
            };
            ctx.CompanyMemberships.Add(membership);
            await ctx.SaveChangesAsync();

            var switched = await auth.IssueTokensForCompanyAsync(user.Id, targetCompany.Id, "127.0.0.1");
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(switched.AccessToken);
            Assert.Equal(targetCompany.Id.ToString(), jwt.Claims.Single(x => x.Type == "company_id").Value);
            Assert.Equal(targetRole.Id.ToString(), jwt.Claims.Single(x => x.Type == "role_id").Value);
            Assert.Equal("Subsidiary Accountant", jwt.Claims.Single(x => x.Type == System.Security.Claims.ClaimTypes.Role).Value);

            membership.IsActive = false;
            membership.ValidTo = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.IssueTokensForCompanyAsync(user.Id, targetCompany.Id, "127.0.0.1"));

            var foreignTenant = new CustomerOrganisation { Name = "Separate tenant" };
            var foreignCompany = new Company
            {
                Tenant = foreignTenant, TenantId = foreignTenant.Id, Name = "Foreign company",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            };
            ctx.CustomerOrganisations.Add(foreignTenant);
            ctx.Companies.Add(foreignCompany);
            await ctx.SaveChangesAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.IssueTokensForCompanyAsync(user.Id, foreignCompany.Id, "127.0.0.1"));
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

        [PostgresFact]
        public async Task Evidence_upload_and_download_are_bound_to_the_authenticated_company_and_target_record()
        {
            await using var db = await TestDb.CreateAsync();
            var companyA = Guid.NewGuid();
            var companyB = Guid.NewGuid();
            var ownTarget = new ControlException
            {
                CompanyId = companyA, DetectorCode = "TEST", Fingerprint = "own-evidence-target",
                Category = "Test", Severity = "Low", SourceType = "Test", Description = "Own company target"
            };
            var foreignTarget = new ControlException
            {
                CompanyId = companyB, DetectorCode = "TEST", Fingerprint = "foreign-evidence-target",
                Category = "Test", Severity = "Low", SourceType = "Test", Description = "Foreign company target"
            };
            var foreignContent = Encoding.UTF8.GetBytes("foreign evidence");
            var foreignEvidence = new AuditEvidenceReference
            {
                CompanyId = companyB, EntityType = nameof(ControlException), EntityId = foreignTarget.Id,
                FileName = "foreign.pdf", ContentType = "application/pdf", SizeBytes = foreignContent.Length,
                Content = foreignContent, Sha256 = Convert.ToHexString(SHA256.HashData(foreignContent))
            };
            await using (var seed = db.NewContext(null))
            {
                seed.Companies.AddRange(
                    new Company { Id = companyA, Name = "Evidence A", BaseCurrency = "USD", FunctionalCurrency = "USD" },
                    new Company { Id = companyB, Name = "Evidence B", BaseCurrency = "USD", FunctionalCurrency = "USD" });
                seed.ControlExceptions.AddRange(ownTarget, foreignTarget);
                seed.AuditEvidenceReferences.Add(foreignEvidence);
                await seed.SaveChangesAsync();
            }

            await using var tenantA = db.NewContext(companyA, Approver);
            var evidence = new EvidenceService(tenantA, new AuditService(tenantA));
            var bytes = Encoding.UTF8.GetBytes("company A evidence");
            var attached = await evidence.AttachAsync(companyA, nameof(ControlException), ownTarget.Id,
                "own.pdf", "application/pdf", bytes, null, Approver);
            Assert.Equal(companyA, attached.CompanyId);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                evidence.AttachAsync(companyA, nameof(ControlException), foreignTarget.Id,
                    "cross-tenant.pdf", "application/pdf", bytes, null, Approver));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                evidence.AttachAsync(companyB, nameof(ControlException), foreignTarget.Id,
                    "wrong-context.pdf", "application/pdf", bytes, null, Approver));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                evidence.DownloadAsync(companyA, foreignEvidence.Id, Approver));
            Assert.Single(await evidence.ListAsync(companyA, nameof(ControlException), ownTarget.Id));
            Assert.Empty(await evidence.ListAsync(companyA, nameof(ControlException), foreignTarget.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                evidence.ListAsync(companyB, nameof(ControlException), foreignTarget.Id));

            // Simulate storage corruption after upload. A fresh company-scoped read must
            // report the hash mismatch rather than silently treating the bytes as verified.
            await using (var corrupt = db.NewContext(null))
            {
                var stored = await corrupt.AuditEvidenceReferences.IgnoreQueryFilters().SingleAsync(x => x.Id == attached.Id);
                stored.Content = Encoding.UTF8.GetBytes("tampered evidence");
                await corrupt.SaveChangesAsync();
            }
            await using var reloadedCompanyA = db.NewContext(companyA, Approver);
            var reloadedEvidence = new EvidenceService(reloadedCompanyA, new AuditService(reloadedCompanyA));
            var (tamperedFile, integrityOk) = await reloadedEvidence.DownloadAsync(companyA, attached.Id, Approver);
            Assert.False(integrityOk);
            Assert.NotEqual(attached.Sha256, Convert.ToHexString(SHA256.HashData(tamperedFile.Content)));
        }

        [PostgresFact]
        public async Task Scheduled_jobs_execute_with_the_company_and_tenant_context_of_each_claim()
        {
            await using var db = await TestDb.CreateAsync();
            var tenantId = Guid.NewGuid();
            var companyA = Guid.NewGuid();
            var companyB = Guid.NewGuid();
            await using (var seed = db.NewContext(null))
            {
                seed.CustomerOrganisations.Add(new CustomerOrganisation
                {
                    Id = tenantId, Name = "Scheduled job tenant", IsActive = true
                });
                seed.Companies.AddRange(
                    new Company
                    {
                        Id = companyA, TenantId = tenantId, Name = "Scheduled company A",
                        BaseCurrency = "USD", FunctionalCurrency = "USD", IsActive = true
                    },
                    new Company
                    {
                        Id = companyB, TenantId = tenantId, Name = "Scheduled company B",
                        BaseCurrency = "USD", FunctionalCurrency = "USD", IsActive = true
                    });
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddScoped<ApexErpDbContext>(_ => db.NewContext(null));
            using var root = services.BuildServiceProvider();
            var job = new JobDefinition(
                "test.tenant-context", "Verify isolated tenant context", _ => "2026-11-28",
                async (sp, companyId, token) =>
                {
                    var context = sp.GetRequiredService<ApexErpDbContext>();
                    Assert.Equal(tenantId, context.CurrentTenantId);
                    Assert.Equal(companyId, context.CurrentCompanyId);
                    // Tenant scope deliberately exposes all companies in the tenant; company-scoped
                    // operational records must remain restricted to the claimed company.
                    var visibleCompanies = await context.Companies.Select(x => x.Id).OrderBy(x => x).ToListAsync(token);
                    Assert.Equal(new[] { companyA, companyB }.OrderBy(x => x), visibleCompanies);
                    var visibleExecutions = await context.JobExecutions.Select(x => x.CompanyId).Distinct().ToListAsync(token);
                    Assert.Equal(new[] { companyId }, visibleExecutions);
                    return "tenant and company context verified";
                });

            var now = new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(1, await ScheduledJobs.RunDueForCompanyAsync(root, companyA, now, new[] { job }));
            Assert.Equal(1, await ScheduledJobs.RunDueForCompanyAsync(root, companyB, now, new[] { job }));

            await using var verify = db.NewContext(null);
            var executions = await verify.JobExecutions.IgnoreQueryFilters()
                .Where(x => x.JobCode == "test.tenant-context").ToListAsync();
            Assert.Equal(2, executions.Count);
            Assert.Contains(executions, x => x.CompanyId == companyA && x.Status == "Succeeded" && x.Message == "tenant and company context verified");
            Assert.Contains(executions, x => x.CompanyId == companyB && x.Status == "Succeeded" && x.Message == "tenant and company context verified");
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
        [PostgresFact]
        public async Task Concurrent_retry_workers_claim_a_due_payment_webhook_only_once()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Webhook Retry Co",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.IntegrationEndpoints.Add(new IntegrationEndpoint
                {
                    CompanyId = companyId, Provider = "EcoCash", Name = "EcoCash test",
                    SecretName = "APEX_TEST_ECOCASH_SECRET", IsActive = true
                });
                seed.IntegrationMessages.Add(new IntegrationMessage
                {
                    Id = messageId, CompanyId = companyId, Provider = "EcoCash",
                    Direction = "Inbound", ExternalRef = "retry-race-001",
                    PayloadJson = """{"externalRef":"retry-race-001","status":"Paid","amount":10.00,"currency":"USD","reference":"MISSING-INVOICE"}""",
                    SignatureValid = true, Status = "Failed", Attempts = 0,
                    NextAttemptAt = DateTime.UtcNow.AddMinutes(-1)
                });
                await seed.SaveChangesAsync();
            }

            async Task<int> RunWorkerAsync()
            {
                await using var ctx = db.NewContext(companyId);
                var service = new IntegrationService(ctx, new ArService(ctx, Gl(ctx)),
                    new SettingsService(ctx), new AuditService(ctx));
                return await service.RetryDueAsync(companyId);
            }

            var results = await Task.WhenAll(RunWorkerAsync(), RunWorkerAsync());
            Assert.Equal(1, results.Sum());

            await using var verify = db.NewContext(companyId);
            var message = await verify.IntegrationMessages.SingleAsync(x => x.Id == messageId);
            Assert.Equal(1, message.Attempts);
            Assert.Equal("Failed", message.Status);
            Assert.NotNull(message.NextAttemptAt);
        }

        [Fact]
        public void Webhook_signature_rejects_out_of_range_and_non_numeric_timestamps_without_throwing()
        {
            var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            Assert.False(WebhookSignature.Verify(Secret, "999999999999999999999999999999",
                "{}", "00", now));
            Assert.False(WebhookSignature.Verify(Secret, "not-a-timestamp", "{}", "00", now));
        }

        [Fact]
        public void Webhook_signature_accepts_a_valid_signature_case_insensitively_within_the_replay_window()
        {
            var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            var timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var signature = WebhookSignature.Sign(Secret, timestamp, "{\"event\":\"paid\"}");

            Assert.True(WebhookSignature.Verify(Secret, timestamp, "{\"event\":\"paid\"}",
                signature.ToUpperInvariant(), now));
            Assert.False(WebhookSignature.Verify(Secret, timestamp, "{\"event\":\"paid\"}",
                signature, now.AddMinutes(6)));
        }

        [PostgresFact]
        public async Task Webhook_lookup_is_provider_case_insensitive_and_invalid_signatures_never_create_messages()
        {
            await using var db = await TestDb.CreateAsync();
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            await using (var seed = db.NewContext(null))
            {
                seed.CustomerOrganisations.Add(new CustomerOrganisation
                {
                    Id = tenantId, Name = "Webhook tenant", IsActive = true
                });
                seed.Companies.Add(new Company
                {
                    Id = companyId, TenantId = tenantId, Name = "Webhook company",
                    BaseCurrency = "USD", FunctionalCurrency = "USD", IsActive = true
                });
                seed.IntegrationEndpoints.Add(new IntegrationEndpoint
                {
                    CompanyId = companyId, Provider = "EcoCash", Name = "EcoCash",
                    SecretName = "APEX_TEST_ECOCASH_SECRET", IsActive = true
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(null);
            var service = new IntegrationService(ctx, new ArService(ctx, Gl(ctx)),
                new SettingsService(ctx), new AuditService(ctx), _ => Secret);
            var result = await service.ReceiveWebhookAsync(companyId, "ecocash",
                """{"externalRef":"invalid-signature-1","status":"Paid","amount":25.00,"currency":"USD","reference":"INV-1"}""",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture), "not-a-valid-signature");

            Assert.Equal(401, result.Status);
            await using var verify = db.NewContext(companyId);
            Assert.Empty(await verify.IntegrationMessages.ToListAsync());
        }

    }
}
