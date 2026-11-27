using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexERP.Application.Services
{
    public sealed record JobDefinition(string Code, string Title, Func<DateTime, string> RunKey, Func<IServiceProvider, Guid, CancellationToken, Task<string>> Execute);

    /// <summary>
    /// Background jobs (spec 26). Each job runs once per company per run key; the unique JobExecution row is the lock, so several
    /// API instances can run the scheduler and a job still applies a period only once. Failed runs retry up to three times.
    /// </summary>
    public static class ScheduledJobs
    {
        public const int MaxAttempts = 3;
        private static string Day(DateTime t) => t.ToString("yyyy-MM-dd");
        private static string FiveMinutes(DateTime t) => $"{t:yyyy-MM-ddTHH}:{t.Minute / 5 * 5:D2}";

        public static readonly IReadOnlyList<JobDefinition> All = new JobDefinition[]
        {
            new("accruals.reverse", "Auto-reverse accruals", Day, async (sp, c, ct) =>
                Summ(await sp.GetRequiredService<AccrualService>().ReverseDueAsync(c, DateTime.UtcNow, null, ct))),
            new("prepayments.amortise", "Post prepayment amortisation", Day, async (sp, c, ct) =>
                Summ(await sp.GetRequiredService<PrepaymentService>().RunDueAsync(c, DateTime.UtcNow, null, ct))),
            new("revenue.recognise", "Recognise contract revenue", Day, async (sp, c, ct) =>
                Summ(await sp.GetRequiredService<RevenueService>().RecogniseDueAsync(c, DateTime.UtcNow, null, ct))),
            new("controls.detect", "Run control detectors", Day, async (sp, c, ct) =>
            { var r = await sp.GetRequiredService<ControlMonitoringService>().RunDetectorsAsync(c, 35, ct); return $"{r.Raised} new, {r.AlreadyOpen} already open"; }),
            new("controls.selftests", "Run control self-tests", Day, async (sp, c, ct) =>
            { var r = await sp.GetRequiredService<ControlMonitoringService>().RunSelfTestsAsync(c, ct); return $"{r.Count(x => x.Passed)}/{r.Count} passed"; }),
            new("reconciliation.nightly", "Reconcile sub-ledgers to the GL", Day, async (sp, c, ct) =>
            { var r = await sp.GetRequiredService<ReconciliationService>().RunAllAsync(c, DateTime.UtcNow, null, ct); return $"{r.Count(x => x.Status == "Reconciled")}/{r.Count} reconciled"; }),
            new("integrations.retry", "Retry failed integration messages", FiveMinutes, async (sp, c, ct) =>
                $"{await sp.GetRequiredService<IntegrationService>().RetryDueAsync(c, ct)} retried"),
        };

        private static string Summ(RunSummary r) => $"{r.Posted} posted, {r.Skipped} skipped" + (r.Problems.Count > 0 ? "; " + string.Join(" | ", r.Problems.Take(3)) : "");

        /// <summary>Runs every due job for one company. Returns the number of jobs executed.</summary>
        public static async Task<int> RunDueForCompanyAsync(IServiceProvider root, Guid companyId, DateTime nowUtc, IReadOnlyList<JobDefinition>? jobs = null, CancellationToken ct = default)
        {
            var ran = 0;
            foreach (var job in jobs ?? All)
            {
                using var scope = root.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<ApexErpDbContext>();

                // Resolve and revalidate the owning tenant for each isolated job context.
                // This also prevents a company deactivated after the scheduler's initial scan
                // from receiving work, and supplies both identities required by tenant RLS.
                var tenantId = await db.Companies.IgnoreQueryFilters()
                    .Where(c => c.Id == companyId && c.IsActive && c.TenantId.HasValue &&
                                c.Tenant != null && c.Tenant.IsActive)
                    .Select(c => c.TenantId)
                    .SingleOrDefaultAsync(ct);
                if (!tenantId.HasValue) continue;

                db.CurrentTenantId = tenantId.Value;
                db.CurrentCompanyId = companyId;
                var key = job.RunKey(nowUtc);

                var exec = await db.JobExecutions.FirstOrDefaultAsync(j => j.CompanyId == companyId && j.JobCode == job.Code && j.RunKey == key, ct);
                if (exec == null)
                {
                    exec = new JobExecution { CompanyId = companyId, JobCode = job.Code, RunKey = key };
                    db.JobExecutions.Add(exec);
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException) { continue; }                       // another instance claimed this run
                }
                else if (exec.Status != "Failed" || exec.Attempt >= MaxAttempts || exec.StartedAt > nowUtc.AddMinutes(-10)) continue;
                else { exec.Attempt++; exec.Status = "Running"; exec.StartedAt = nowUtc; exec.Message = null; await db.SaveChangesAsync(ct); }

                try
                {
                    exec.Message = await job.Execute(sp, companyId, ct);
                    exec.Status = "Succeeded";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();     // discard half-applied state before recording the failure
                    var failed = await db.JobExecutions.FirstAsync(j => j.CompanyId == companyId && j.JobCode == job.Code && j.RunKey == key, ct);
                    failed.Status = "Failed"; failed.Message = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message; failed.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    ran++; continue;
                }
                exec.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                ran++;
            }
            return ran;
        }
    }
}
