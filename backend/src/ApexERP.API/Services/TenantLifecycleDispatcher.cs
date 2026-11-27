using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Services;

/// <summary>
/// Dispatches tenant backup/restore/provisioning/diagnostic jobs to a separately operated
/// worker. The API never receives database credentials or backup payloads from the worker.
/// </summary>
public sealed class TenantLifecycleDispatcher : BackgroundService
{
    private const int DispatchLeaseMinutes = 10;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantLifecycleDispatcher> _logger;

    public TenantLifecycleDispatcher(IServiceScopeFactory scopeFactory, IHttpClientFactory clients,
        IConfiguration configuration, ILogger<TenantLifecycleDispatcher> logger)
    {
        _scopeFactory = scopeFactory; _clients = clients; _configuration = configuration; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var endpoint = _configuration["Operations:WorkerUrl"];
            var secret = _configuration["Operations:WorkerSecret"];
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(secret))
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                continue;
            }

            try { await DispatchOneAsync(endpoint, secret, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Tenant lifecycle dispatch cycle failed."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task DispatchOneAsync(string endpoint, string secret, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApexErpDbContext>();
        TenantLifecycleJob? job;
        using (db.SystemAccessScope("operations worker: claim tenant lifecycle job"))
        {
            var retryBefore = DateTime.UtcNow.AddMinutes(-DispatchLeaseMinutes);
            job = await db.TenantLifecycleJobs.AsNoTracking()
                .Where(x =>
                    ((x.Status == "Queued" || x.Status == "AwaitingProvider") &&
                        (x.StartedAt == null || x.StartedAt < retryBefore)) ||
                    (x.Status == "Dispatching" && x.StartedAt != null && x.StartedAt < retryBefore))
                .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (job is null) return;
            var claimedAt = DateTime.UtcNow;
            var claimed = await db.TenantLifecycleJobs
                .Where(x => x.Id == job.Id &&
                    (((x.Status == "Queued" || x.Status == "AwaitingProvider") &&
                        (x.StartedAt == null || x.StartedAt < retryBefore)) ||
                     (x.Status == "Dispatching" && x.StartedAt != null && x.StartedAt < retryBefore)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Dispatching")
                    .SetProperty(x => x.StartedAt, claimedAt), ct);
            if (claimed != 1) return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            jobId = job.Id, tenantId = job.TenantId, jobType = job.JobType,
            requestedBy = job.RequestedBy, requestedAt = job.CreatedAt
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Sign(secret, timestamp + "\n" + payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Apex-Timestamp", timestamp);
        request.Headers.Add("X-Apex-Signature", signature);
        try
        {
            var response = await _clients.CreateClient(nameof(TenantLifecycleDispatcher)).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var inner = _scopeFactory.CreateScope();
            var updateDb = inner.ServiceProvider.GetRequiredService<ApexErpDbContext>();
            using var system = updateDb.SystemAccessScope("operations worker: record lifecycle dispatch result");
            var current = await updateDb.TenantLifecycleJobs.SingleOrDefaultAsync(x => x.Id == job.Id, ct);
            if (current is null) return;
            if (response.IsSuccessStatusCode)
            {
                current.Status = "Dispatched";
                current.ExternalJobReference = response.Headers.TryGetValues("X-Apex-Job-Reference", out var refs) ? refs.FirstOrDefault() : null;
                current.ResultSummary = string.IsNullOrWhiteSpace(body) ? "Accepted by configured operations worker." : body[..Math.Min(body.Length, 2000)];
            }
            else
            {
                var transientFailure = (int)response.StatusCode >= 500 ||
                    response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                current.Status = transientFailure ? "Queued" : "Failed";
                current.CompletedAt = transientFailure ? null : DateTime.UtcNow;
                current.StartedAt = DateTime.UtcNow;
                current.ResultSummary = $"Worker returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}" +
                    (transientFailure ? $" Retry will be attempted after {DispatchLeaseMinutes} minutes." : "");
            }
            await updateDb.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using var inner = _scopeFactory.CreateScope();
            var updateDb = inner.ServiceProvider.GetRequiredService<ApexErpDbContext>();
            using var system = updateDb.SystemAccessScope("operations worker: record dispatch failure");
            var current = await updateDb.TenantLifecycleJobs.SingleOrDefaultAsync(x => x.Id == job.Id, ct);
            if (current != null)
            {
                // Dispatch is at-least-once: the worker must deduplicate on the stable jobId in
                // the signed payload because a timeout can occur after the worker accepted it.
                current.Status = "Queued";
                current.StartedAt = DateTime.UtcNow;
                current.CompletedAt = null;
                current.ResultSummary = $"Dispatch attempt failed; retry will be attempted after {DispatchLeaseMinutes} minutes: " +
                    ex.Message[..Math.Min(ex.Message.Length, 900)];
                await updateDb.SaveChangesAsync(ct);
            }
            _logger.LogError(ex, "Unable to dispatch tenant lifecycle job {JobId}.", job.Id);
        }
    }

    public static string Sign(string secret, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(value)));
}
