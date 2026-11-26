using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers
{
    public record SetSettingRequest(string Value);
    public record AddSoDRuleRequest(string PermissionA, string PermissionB, string Reason);
    public record CheckPermissionsRequest(List<string> Permissions);
    public record CreateDelegationRequest(Guid DelegateUserId, string? EntityType, decimal? MaxAmount, DateTime ValidFrom, DateTime ValidTo, string Reason, bool IsEmergency = false, Guid? DelegatorUserId = null);
    public record TierDto(decimal UpToAmount, string RequiredRoleName, int SlaHours = 24, string? EscalateToRoleName = null);
    public record ReplaceTiersRequest(List<TierDto> Tiers);
    public record ProposeMaterialityRequest(decimal Medium, decimal High, decimal Critical, string Currency = "USD");
    public record ProposePolicyRequest(string Area, string Description, DateTime EffectiveFrom);
    public record ProposeStatutoryRateRequest(string Code, decimal RatePct, decimal? Ceiling, string Currency, DateTime EffectiveFrom, string SourceReference);
    public record AssignControlExceptionRequest(Guid OwnerUserId, DateTime? DueDate);
    public record NoteRequest(string Note);

    /// <summary>Governance: settings, segregation of duties, delegation, approval matrix, materiality, policy versions, exceptions and self-tests (spec 7-9, 12, 15).</summary>
    [Route("api/v1/controls")]
    public class ControlsController : ControlsControllerBase
    {
        private readonly SettingsService _settings; private readonly GovernanceService _gov; private readonly ControlMonitoringService _monitor;
        private readonly StatutoryRateService _rates; private readonly AuthService _auth;

        public ControlsController(SettingsService settings, GovernanceService gov, ControlMonitoringService monitor, StatutoryRateService rates, AuthService auth)
        { _settings = settings; _gov = gov; _monitor = monitor; _rates = rates; _auth = auth; }

        // ---- settings ----
        [HttpGet("settings")] public async Task<IActionResult> Settings() => Ok(await _settings.ListAsync(CompanyId));
        [HttpPut("settings/{key}")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> SetSetting(string key, SetSettingRequest r) => Run(async () => { await _settings.SetAsync(CompanyId, key, r.Value, UserId); return Ok(new { key, r.Value }); });

        // ---- segregation of duties ----
        [HttpGet("sod/report")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public async Task<IActionResult> SodReport() => Ok(await _gov.ConflictReportAsync(CompanyId));
        [HttpPost("sod/check")] public async Task<IActionResult> SodCheck(CheckPermissionsRequest r) => Ok(await _gov.DetectConflictsAsync(CompanyId, r.Permissions ?? new List<string>()));
        [HttpPost("sod/rules")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> AddRule(AddSoDRuleRequest r) => Run(async () => Ok(await _gov.AddRuleAsync(CompanyId, r.PermissionA, r.PermissionB, r.Reason, UserId)));
        [HttpPost("users/{userId:guid}/reset-mfa")] [HasPermission(Permissions.AdminUsersManage)]
        public Task<IActionResult> ResetMfa(Guid userId) => Run(async () => { await _auth.AdminResetMfaAsync(CompanyId, userId, UserId); return Ok(new { reset = true }); });

        // ---- delegation and emergency access ----
        [HttpGet("delegations")] public async Task<IActionResult> Delegations([FromQuery] bool activeOnly = false) => Ok(await _gov.ListDelegationsAsync(CompanyId, activeOnly));
        [HttpPost("delegations")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> CreateDelegation(CreateDelegationRequest r) => Run(async () =>
            Ok(await _gov.CreateDelegationAsync(CompanyId, r.DelegatorUserId ?? UserId, r.DelegateUserId, r.EntityType, r.MaxAmount, r.ValidFrom, r.ValidTo, r.Reason, r.IsEmergency, UserId)));
        [HttpPost("delegations/{id:guid}/revoke")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> Revoke(Guid id) => Run(async () => { await _gov.RevokeDelegationAsync(CompanyId, id, UserId); return Ok(new { revoked = true }); });
        [HttpPost("delegations/{id:guid}/review")] [HasPermission(Permissions.ControlsGovernanceApprove)]
        public Task<IActionResult> ReviewEmergency(Guid id) => Run(async () => { await _gov.ReviewEmergencyAsync(CompanyId, id, UserId); return Ok(new { reviewed = true }); });

        // ---- approval matrix ----
        [HttpGet("approval-matrix/{entityType}")] public async Task<IActionResult> Tiers(string entityType) => Ok(await _gov.GetTiersAsync(CompanyId, entityType));
        [HttpPut("approval-matrix/{entityType}")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> ReplaceTiers(string entityType, ReplaceTiersRequest r) => Run(async () =>
            Ok(await _gov.ReplaceTiersAsync(CompanyId, entityType, r.Tiers.Select(t => new ApprovalMatrixTier { UpToAmount = t.UpToAmount, RequiredRoleName = t.RequiredRoleName, SlaHours = t.SlaHours, EscalateToRoleName = t.EscalateToRoleName }), UserId)));

        // ---- materiality and policy ----
        [HttpGet("materiality")] public async Task<IActionResult> Materiality() => Ok(await _gov.ListMaterialityAsync(CompanyId));
        [HttpPost("materiality")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> ProposeMateriality(ProposeMaterialityRequest r) => Run(async () => Ok(await _gov.ProposeMaterialityAsync(CompanyId, r.Medium, r.High, r.Critical, r.Currency, UserId)));
        [HttpPost("materiality/{id:guid}/approve")] [HasPermission(Permissions.ControlsGovernanceApprove)]
        public Task<IActionResult> ApproveMateriality(Guid id) => Run(async () => Ok(await _gov.ApproveMaterialityAsync(CompanyId, id, UserId)));
        [HttpPost("policies")] [HasPermission(Permissions.ControlsGovernanceManage)]
        public Task<IActionResult> ProposePolicy(ProposePolicyRequest r) => Run(async () => Ok(await _gov.ProposePolicyAsync(CompanyId, r.Area, r.Description, r.EffectiveFrom, UserId)));
        [HttpPost("policies/{id:guid}/approve")] [HasPermission(Permissions.ControlsGovernanceApprove)]
        public Task<IActionResult> ApprovePolicy(Guid id) => Run(async () => Ok(await _gov.ApprovePolicyAsync(CompanyId, id, UserId)));
        [HttpGet("policies/stamp")] public async Task<IActionResult> PolicyStamp([FromQuery] DateTime? asOf) => Ok(await _gov.PolicyStampAsync(CompanyId, DateOrToday(asOf)));

        // ---- statutory payroll rates ----
        [HttpGet("statutory-rates")] public async Task<IActionResult> StatutoryRates() => Ok(await _rates.ListAsync(CompanyId));
        [HttpPost("statutory-rates")] [HasPermission(Permissions.PayrollConfigManage)]
        public Task<IActionResult> ProposeRate(ProposeStatutoryRateRequest r) => Run(async () => Ok(await _rates.ProposeAsync(CompanyId, r.Code, r.RatePct, r.Ceiling, r.Currency, r.EffectiveFrom, r.SourceReference, UserId)));
        [HttpPost("statutory-rates/{id:guid}/approve")] [HasPermission(Permissions.ControlsGovernanceApprove)]
        public Task<IActionResult> ApproveRate(Guid id) => Run(async () => Ok(await _rates.ApproveAsync(CompanyId, id, UserId)));

        // ---- exceptions: detection and life-cycle ----
        [HttpPost("monitoring/run")] public Task<IActionResult> RunDetectors([FromQuery] int lookbackDays = 35) => Run(async () => Ok(await _monitor.RunDetectorsAsync(CompanyId, Math.Clamp(lookbackDays, 1, 400))));
        [HttpGet("exceptions")] public async Task<IActionResult> Exceptions([FromQuery] string? status, [FromQuery] string? severity) => Ok(await _monitor.ListAsync(CompanyId, status, severity));
        [HttpPost("exceptions/{id:guid}/assign")] public Task<IActionResult> Assign(Guid id, AssignControlExceptionRequest r) => Run(async () => Ok(await _monitor.AssignAsync(CompanyId, id, r.OwnerUserId, r.DueDate, UserId)));
        [HttpPost("exceptions/{id:guid}/investigate")] public Task<IActionResult> Investigate(Guid id, NoteRequest r) => Run(async () => Ok(await _monitor.InvestigateAsync(CompanyId, id, r.Note, UserId)));
        [HttpPost("exceptions/{id:guid}/resolve")] public Task<IActionResult> Resolve(Guid id, NoteRequest r) => Run(async () => Ok(await _monitor.ResolveAsync(CompanyId, id, r.Note, UserId)));
        [HttpPost("exceptions/{id:guid}/review")] [HasPermission(Permissions.ControlsExceptionsReview)]
        public Task<IActionResult> Review(Guid id) => Run(async () => Ok(await _monitor.ReviewAsync(CompanyId, id, UserId)));
        [HttpPost("exceptions/{id:guid}/close")] [HasPermission(Permissions.ControlsExceptionsReview)]
        public Task<IActionResult> CloseException(Guid id) => Run(async () => Ok(await _monitor.CloseAsync(CompanyId, id, UserId)));

        // ---- control self-tests (tests A-H) ----
        [HttpPost("self-tests/run")] public Task<IActionResult> SelfTests() => Run(async () => Ok(await _monitor.RunSelfTestsAsync(CompanyId)));
    }

    public record RunReconciliationRequest(string? Area, DateTime? AsOf);
    public record ExplainRequest(string Explanation);
    public record PrepareBankRecRequest(Guid BankAccountId, DateTime StatementDate, decimal StatementBalance, string? Notes);
    public record CertifyBankRecRequest(string? ReviewerNotes);
    public record CreateFundRequest(string Name, Guid GlAccountId, decimal ImprestAmount, Guid? CustodianUserId);
    public record CreateVoucherRequest(Guid PettyCashFundId, DateTime VoucherDate, decimal Amount, Guid ExpenseAccountId, string Description);
    public record ReplenishRequest(Guid BankGlAccountId, DateTime? Date);
    public record CompleteTaskRequest(string Note);

    /// <summary>Reconciliation, bank certification, petty cash, forecasting, period-close checklist, evidence and drill-down (spec 12, 13, 18, 24).</summary>
    [Route("api/v1/controls/ops")]
    public class ControlsOpsController : ControlsControllerBase
    {
        private readonly ReconciliationService _recon; private readonly PeriodCloseService _close; private readonly EvidenceService _evidence;

        public ControlsOpsController(ReconciliationService recon, PeriodCloseService close, EvidenceService evidence) { _recon = recon; _close = close; _evidence = evidence; }

        // ---- sub-ledger reconciliation ----
        [HttpGet("reconciliations")] public async Task<IActionResult> Recs([FromQuery] string? area) => Ok(await _recon.ListAsync(CompanyId, area));
        [HttpPost("reconciliations/run")] public Task<IActionResult> RunRec(RunReconciliationRequest r) => Run(async () =>
            r.Area == null ? Ok(await _recon.RunAllAsync(CompanyId, DateOrToday(r.AsOf), UserId)) : Ok(await _recon.RunAsync(CompanyId, r.Area, DateOrToday(r.AsOf), UserId)));
        [HttpPost("reconciliations/{id:guid}/explain")] public Task<IActionResult> Explain(Guid id, ExplainRequest r) => Run(async () => Ok(await _recon.ExplainAsync(CompanyId, id, r.Explanation, UserId)));
        [HttpPost("reconciliations/{id:guid}/sign-off")] [HasPermission(Permissions.ControlsReconciliationSignoff)]
        public Task<IActionResult> SignOff(Guid id) => Run(async () => Ok(await _recon.SignOffAsync(CompanyId, id, UserId)));

        // ---- bank reconciliation certification ----
        [HttpGet("bank-reconciliations")] public async Task<IActionResult> BankRecs() => Ok(await _recon.ListBankReconciliationsAsync(CompanyId));
        [HttpPost("bank-reconciliations")] public Task<IActionResult> PrepareBankRec(PrepareBankRecRequest r) => Run(async () => Ok(await _recon.PrepareBankReconciliationAsync(CompanyId, r.BankAccountId, r.StatementDate, r.StatementBalance, r.Notes, UserId)));
        [HttpPost("bank-reconciliations/{id:guid}/certify")] [HasPermission(Permissions.ControlsBankRecCertify)]
        public Task<IActionResult> Certify(Guid id, CertifyBankRecRequest r) => Run(async () => Ok(await _recon.CertifyBankReconciliationAsync(CompanyId, id, UserId, r.ReviewerNotes)));

        // ---- petty cash and forecast ----
        [HttpGet("petty-cash/funds")] public async Task<IActionResult> Funds() => Ok(await _recon.ListFundsAsync(CompanyId));
        [HttpPost("petty-cash/funds")] public Task<IActionResult> CreateFund(CreateFundRequest r) => Run(async () => Ok(await _recon.CreateFundAsync(CompanyId, r.Name, r.GlAccountId, r.ImprestAmount, r.CustodianUserId, UserId)));
        [HttpGet("petty-cash/vouchers")] public async Task<IActionResult> Vouchers() => Ok(await _recon.ListVouchersAsync(CompanyId));
        [HttpPost("petty-cash/vouchers")] public Task<IActionResult> CreateVoucher(CreateVoucherRequest r) => Run(async () => Ok(await _recon.CreateVoucherAsync(CompanyId, r.PettyCashFundId, r.VoucherDate, r.Amount, r.ExpenseAccountId, r.Description, UserId)));
        [HttpPost("petty-cash/vouchers/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveVoucher(Guid id) => Run(async () => Ok(await _recon.ApproveAndPostVoucherAsync(CompanyId, id, UserId)));
        [HttpPost("petty-cash/funds/{id:guid}/replenish")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> Replenish(Guid id, ReplenishRequest r) => Run(async () => Ok(await _recon.ReplenishFundAsync(CompanyId, id, r.BankGlAccountId, DateOrToday(r.Date), UserId)));
        [HttpGet("cash-forecast")] public async Task<IActionResult> Forecast([FromQuery] DateTime? asOf, [FromQuery] int weeks = 13) => Ok(await _recon.ForecastAsync(CompanyId, DateOrToday(asOf), weeks));

        // ---- period-close checklist ----
        [HttpGet("period-close/{periodId:guid}")] public Task<IActionResult> Checklist(Guid periodId) => Run(async () => Ok(await _close.EnsureChecklistAsync(CompanyId, periodId)));
        [HttpPost("period-close/tasks/{taskId:guid}/complete")] public Task<IActionResult> CompleteTask(Guid taskId, CompleteTaskRequest r) => Run(async () => Ok(await _close.CompleteManualAsync(CompanyId, taskId, UserId, r.Note, HasPermission(Permissions.ControlsPeriodCertify))));
        [HttpPost("period-close/tasks/{taskId:guid}/certify")] [HasPermission(Permissions.ControlsPeriodCertify)]
        public Task<IActionResult> Certify2(Guid taskId, CompleteTaskRequest r) => Run(async () => Ok(await _close.CompleteManualAsync(CompanyId, taskId, UserId, r.Note, true)));
        [HttpPost("period-close/tasks/{taskId:guid}/waive")] [HasPermission(Permissions.ControlsPeriodCertify)]
        public Task<IActionResult> Waive(Guid taskId, CompleteTaskRequest r) => Run(async () => Ok(await _close.WaiveAsync(CompanyId, taskId, UserId, r.Note)));

        // ---- evidence and drill-down ----
        [HttpGet("evidence/{entityType}/{entityId:guid}")] public async Task<IActionResult> Evidence(string entityType, Guid entityId) => Ok(await _evidence.ListAsync(CompanyId, entityType, entityId));
        [HttpPost("evidence/{entityType}/{entityId:guid}")] [HasPermission(Permissions.ControlsEvidenceManage)]
        [RequestSizeLimit(EvidenceService.MaxBytes + 4096)]
        public Task<IActionResult> Attach(string entityType, Guid entityId, IFormFile file, [FromForm] string? description) => Run(async () =>
        {
            if (file == null || file.Length == 0) return BadRequest(new { error = "Choose a file to attach." });
            await using var ms = new MemoryStream(); await file.CopyToAsync(ms);
            var e = await _evidence.AttachAsync(CompanyId, entityType, entityId, file.FileName, file.ContentType, ms.ToArray(), description, UserId);
            return Ok(new { e.Id, e.FileName, e.Sha256, e.SizeBytes });
        });
        [HttpGet("evidence/file/{id:guid}")] public Task<IActionResult> Download(Guid id) => Run(async () =>
        {
            var (f, ok) = await _evidence.DownloadAsync(CompanyId, id, UserId);
            Response.Headers["X-Content-Integrity"] = ok ? "verified" : "MISMATCH";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(f.Content, f.ContentType, f.FileName);
        });
        [HttpGet("trace/account/{accountId:guid}")] public Task<IActionResult> TraceAccount(Guid accountId, [FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
            Run(async () => Ok(await _evidence.AccountActivityAsync(CompanyId, accountId, from, to, page, pageSize)));
        [HttpGet("trace/journal/{journalId:guid}")] public Task<IActionResult> TraceJournal(Guid journalId) => Run(async () => Ok(await _evidence.TraceJournalAsync(CompanyId, journalId)));
    }

    /// <summary>Executive dashboard (spec 14).</summary>
    [Route("api/v1/reports/dashboard")]
    public class DashboardController : ControlsControllerBase
    {
        private readonly DashboardService _dash;
        public DashboardController(DashboardService dash) { _dash = dash; }
        [HttpGet] public async Task<IActionResult> Get([FromQuery] DateTime? asOf) => Ok(await _dash.GetAsync(CompanyId, DateOrToday(asOf)));
    }
}
