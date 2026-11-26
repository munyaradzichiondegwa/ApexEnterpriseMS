using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Jobs
{
    /// <summary>
    /// Ticks once a minute and runs due jobs for every company (spec 26). Safe with several API instances: each job run is claimed
    /// by inserting a unique JobExecution row. Disable with Jobs:Enabled=false (for example on a read-only replica).
    /// </summary>
    public sealed class ScheduledJobRunner : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<ScheduledJobRunner> _logger;
        private readonly bool _enabled;

        public ScheduledJobRunner(IServiceProvider services, ILogger<ScheduledJobRunner> logger, IConfiguration config)
        { _services = services; _logger = logger; _enabled = config.GetValue("Jobs:Enabled", true); }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled) { _logger.LogInformation("Scheduled jobs are disabled (Jobs:Enabled=false)."); return; }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);          // let migrations and warm-up finish
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    List<Guid> companies;
                    using (var scope = _services.CreateScope())
                        companies = await scope.ServiceProvider.GetRequiredService<ApexErpDbContext>().Companies.IgnoreQueryFilters().Select(c => c.Id).ToListAsync(stoppingToken);
                    foreach (var id in companies)
                    {
                        try { await ScheduledJobs.RunDueForCompanyAsync(_services, id, DateTime.UtcNow, null, stoppingToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Scheduled jobs failed for company {CompanyId}", id); }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Scheduled job tick failed"); }
            }
        }
    }
}
