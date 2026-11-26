using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record SaveEndpointRequest(string Provider, string Name, string? BaseUrl, string SecretName, Guid? SettlementBankGlAccountId, bool IsActive = true);

    /// <summary>Payment integrations (spec 25.3, 25.5): provider webhooks with signature verification, retry, dead-letter queue and reconciliation.</summary>
    [Route("api/v1/integrations")]
    public class IntegrationsController : ControlsControllerBase
    {
        private readonly IntegrationService _svc;
        private readonly ILogger<IntegrationsController> _logger;
        public IntegrationsController(IntegrationService svc, ILogger<IntegrationsController> logger) { _svc = svc; _logger = logger; }

        [HttpGet("endpoints")] public async Task<IActionResult> Endpoints() => Ok(await _svc.ListEndpointsAsync(CompanyId));
        [HttpPut("endpoints")] [HasPermission(Permissions.IntegrationsManage)]
        public Task<IActionResult> Save(SaveEndpointRequest r) => Run(async () => Ok(await _svc.UpsertEndpointAsync(CompanyId, r.Provider, r.Name, r.BaseUrl, r.SecretName, r.SettlementBankGlAccountId, r.IsActive, UserId)));
        [HttpGet("messages")] public async Task<IActionResult> Messages([FromQuery] string? status) => Ok(await _svc.ListMessagesAsync(CompanyId, status));
        [HttpPost("messages/{id:guid}/requeue")] [HasPermission(Permissions.IntegrationsManage)]
        public Task<IActionResult> Requeue(Guid id) => Run(async () => Ok(await _svc.RequeueAsync(CompanyId, id, UserId)));
        [HttpGet("reconciliation")] public async Task<IActionResult> Reconcile([FromQuery] DateTime from, [FromQuery] DateTime to) => Ok(await _svc.ReconcileAsync(CompanyId, from, to));

        /// <summary>
        /// Called by the provider, so it carries no user token: the HMAC signature (X-Apex-Timestamp + X-Apex-Signature, shared secret held
        /// in an environment variable) is the authentication. Duplicate deliveries are acknowledged and ignored.
        /// </summary>
        [HttpPost("webhooks/{companyId:guid}/{provider}")]
        [AllowAnonymous]
        [EnableRateLimiting("webhook")]
        [RequestSizeLimit(70 * 1024)]
        public async Task<IActionResult> Webhook(Guid companyId, string provider)
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            var (status, message) = await _svc.ReceiveWebhookAsync(companyId, provider, body, Request.Headers["X-Apex-Timestamp"], Request.Headers["X-Apex-Signature"], HttpContext.RequestAborted);
            if (status == 401) _logger.LogWarning("Rejected webhook with an invalid signature for company {CompanyId}, provider {Provider} from {Ip}", companyId, provider, HttpContext.Connection.RemoteIpAddress);
            return StatusCode(status, new { message });
        }
    }

    /// <summary>Operational view of background jobs (spec 26).</summary>
    [Route("api/v1/admin/jobs")]
    public class JobsController : ControlsControllerBase
    {
        private readonly ApexErpDbContext _db; private readonly IServiceProvider _sp;
        public JobsController(ApexErpDbContext db, IServiceProvider sp) { _db = db; _sp = sp; }

        [HttpGet] [HasPermission(Permissions.AdminAuditRead)]
        public async Task<IActionResult> List() => Ok(new
        {
            Definitions = ScheduledJobs.All.Select(j => new { j.Code, j.Title }),
            Recent = await _db.JobExecutions.Where(j => j.CompanyId == CompanyId).OrderByDescending(j => j.StartedAt).Take(100).ToListAsync()
        });

        /// <summary>Runs every job that is due for this company right now (same once-per-key guard as the scheduler).</summary>
        [HttpPost("run-due")] [HasPermission(Permissions.AdminCompanyManage)]
        public async Task<IActionResult> RunDue() => Ok(new { executed = await ScheduledJobs.RunDueForCompanyAsync(_sp, CompanyId, DateTime.UtcNow) });
    }
}
