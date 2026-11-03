using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateGlAccountRequest(string Code, string Name, GlAccountType Type, string? SubType, Guid? ParentId, string Currency, bool IsBankAccount, bool IsControlAccount);
    public record SetAccountActiveRequest(bool IsActive);
    public record CreateJournalLineRequest(Guid GlAccountId, decimal Debit, decimal Credit, string Currency, decimal ExchangeRate, Guid? CostCentreId, string? Narrative,
        Guid? FundCentreId = null, Guid? GrantId = null);
    public record CreateJournalRequest(DateTime EntryDate, string? Description, string? Reference, List<CreateJournalLineRequest> Lines);
    public record JournalCommentRequest(string? Comment);
    public record JournalReasonRequest(string? Reason);

    [Route("api/v1/gl")]
    public class GlController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        public GlController(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        // ---------------------------------------------------------------------------------------------
        // Chart of accounts
        // ---------------------------------------------------------------------------------------------

        [HttpGet("accounts")]
        [HasPermission(Permissions.GlAccountsRead)]
        public async Task<IActionResult> GetAccounts() =>
            Ok(await _db.GlAccounts.Where(a => a.CompanyId == CompanyId).OrderBy(a => a.Code).ToListAsync());

        [HttpPost("accounts")]
        [HasPermission(Permissions.GlAccountsManage)]
        public async Task<IActionResult> CreateAccount(CreateGlAccountRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { error = "An account code and name are required." });

            var code = req.Code.Trim();
            if (await _db.GlAccounts.AnyAsync(a => a.CompanyId == CompanyId && a.Code == code))
                return Conflict(new { error = $"Account code {code} already exists." });

            var account = new GlAccount
            {
                CompanyId = CompanyId,
                Code = code,
                Name = req.Name.Trim(),
                Type = req.Type,
                SubType = req.SubType,
                ParentId = req.ParentId,
                Currency = req.Currency,
                IsBankAccount = req.IsBankAccount,
                IsControlAccount = req.IsControlAccount
            };
            _db.GlAccounts.Add(account);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetAccounts), new { id = account.Id }, account);
        }

        /// <summary>Deactivate (or reactivate) an account. A deactivated account accepts no NEW postings, but its
        /// historical activity still appears in the trial balance and statements.</summary>
        [HttpPut("accounts/{id}/active")]
        [HasPermission(Permissions.GlAccountsManage)]
        public async Task<IActionResult> SetAccountActive(Guid id, SetAccountActiveRequest req)
        {
            var account = await _db.GlAccounts.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == CompanyId);
            if (account == null) return NotFound();
            account.IsActive = req.IsActive;
            await _db.SaveChangesAsync();
            return Ok(account);
        }

        // ---------------------------------------------------------------------------------------------
        // Dimensions
        // ---------------------------------------------------------------------------------------------

        [HttpGet("cost-centres")]
        public async Task<IActionResult> GetCostCentres() =>
            Ok(await _db.CostCentres.Where(c => c.CompanyId == CompanyId).ToListAsync());

        [HttpPost("cost-centres")]
        [HasPermission(Permissions.GlStructureManage)]
        public async Task<IActionResult> CreateCostCentre([FromBody] CostCentre req)
        {
            req.Id = Guid.NewGuid();            // never accept a client-supplied key
            req.CompanyId = CompanyId;
            _db.CostCentres.Add(req);
            await _db.SaveChangesAsync();
            return Ok(req);
        }

        /// <summary>IFRS 8 operating segments -- assign a cost centre's SegmentId to roll its
        /// transactions into segment reporting (see Notes to the Financial Statements).</summary>
        [HttpGet("segments")]
        public async Task<IActionResult> GetSegments() => Ok(await _db.Segments.Where(s => s.CompanyId == CompanyId).ToListAsync());

        [HttpPost("segments")]
        [HasPermission(Permissions.GlStructureManage)]
        public async Task<IActionResult> CreateSegment([FromBody] Segment req)
        {
            req.Id = Guid.NewGuid();
            req.CompanyId = CompanyId;
            _db.Segments.Add(req);
            await _db.SaveChangesAsync();
            return Ok(req);
        }

        // ---------------------------------------------------------------------------------------------
        // Accounting periods
        // ---------------------------------------------------------------------------------------------

        [HttpGet("periods")]
        public async Task<IActionResult> GetPeriods() =>
            Ok(await _db.AccountingPeriods.Where(p => p.CompanyId == CompanyId).OrderByDescending(p => p.Year).ThenBy(p => p.PeriodNo).ToListAsync());

        /// <summary>Closes a period. Refused while the period still holds unposted (draft / submitted / approved)
        /// journals; serialised against posting so nothing can slip in while it closes.</summary>
        [HttpPost("periods/{id}/close")]
        [HasPermission(Permissions.AccountingPeriodClose)]
        public Task<IActionResult> ClosePeriod(Guid id) =>
            Run(async () => Ok(await _gl.ClosePeriodAsync(CompanyId, id, UserId)));

        /// <summary>Controlled re-opening of a closed period. A reason is mandatory and is audited.</summary>
        [HttpPost("periods/{id}/reopen")]
        [HasPermission(Permissions.AccountingPeriodReopen)]
        public Task<IActionResult> ReopenPeriod(Guid id, JournalReasonRequest req) =>
            Run(async () => Ok(await _gl.ReopenPeriodAsync(CompanyId, id, UserId, req.Reason ?? string.Empty)));

        // ---------------------------------------------------------------------------------------------
        // Journals
        // ---------------------------------------------------------------------------------------------

        [HttpGet("journal-entries")]
        [HasPermission(Permissions.GlJournalsRead)]
        public async Task<IActionResult> GetJournalEntries([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] JournalStatus? status)
        {
            var q = _db.JournalEntries.Include(j => j.Lines).Where(j => j.CompanyId == CompanyId);
            if (from.HasValue) q = q.Where(j => j.EntryDate >= from.Value);
            if (to.HasValue) q = q.Where(j => j.EntryDate <= to.Value);
            if (status.HasValue) q = q.Where(j => j.Status == status.Value);
            return Ok(await q.OrderByDescending(j => j.EntryDate).Take(200).ToListAsync());
        }

        [HttpGet("journal-entries/{id}")]
        [HasPermission(Permissions.GlJournalsRead)]
        public async Task<IActionResult> GetJournalEntry(Guid id)
        {
            var entry = await _db.JournalEntries.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == id && j.CompanyId == CompanyId);
            return entry == null ? NotFound() : Ok(entry);
        }

        /// <summary>Who did what to this journal and when (immutable audit trail), oldest first.</summary>
        [HttpGet("journal-entries/{id}/history")]
        [HasPermission(Permissions.GlJournalsRead)]
        public async Task<IActionResult> GetJournalHistory(Guid id) =>
            Ok(await _gl.GetJournalHistoryAsync(CompanyId, id));

        /// <summary>
        /// Creates a manual journal as a DRAFT. Nothing reaches the ledger until it has been submitted, approved by
        /// someone other than its preparer, and posted (Draft -> Submitted -> Approved -> Posted). Balance, exchange
        /// rate, currency and period are validated now so mistakes are caught early.
        /// </summary>
        [HttpPost("journal-entries")]
        [HasPermission(Permissions.GlJournalsCreate)]
        public Task<IActionResult> CreateJournalEntry(CreateJournalRequest req) =>
            Run(async () =>
            {
                var lines = (req.Lines ?? new()).Select(l => new GlLineRequest(l.GlAccountId, l.Debit, l.Credit, l.Currency,
                    l.ExchangeRate, l.CostCentreId, l.Narrative, l.FundCentreId, l.GrantId));
                var draft = await _gl.CreateDraftAsync(CompanyId, UserId, req.EntryDate, req.Description, req.Reference, lines);
                return Ok(draft);
            });

        [HttpPost("journal-entries/{id}/submit")]
        [HasPermission(Permissions.GlJournalsCreate)]
        public Task<IActionResult> SubmitJournalEntry(Guid id) =>
            Run(async () => Ok(await _gl.SubmitAsync(CompanyId, UserId, id)));

        [HttpPost("journal-entries/{id}/approve")]
        [HasPermission(Permissions.GlJournalsApprove)]
        public Task<IActionResult> ApproveJournalEntry(Guid id, JournalCommentRequest? req) =>
            Run(async () => Ok(await _gl.ApproveAsync(CompanyId, UserId, id, req?.Comment)));

        [HttpPost("journal-entries/{id}/reject")]
        [HasPermission(Permissions.GlJournalsApprove)]
        public Task<IActionResult> RejectJournalEntry(Guid id, JournalReasonRequest req) =>
            Run(async () => Ok(await _gl.RejectAsync(CompanyId, UserId, id, req.Reason ?? string.Empty)));

        /// <summary>Posts an approved journal to the ledger (atomic; allocates the final JE number).</summary>
        [HttpPost("journal-entries/{id}/post")]
        [HasPermission(Permissions.GlJournalsPost)]
        public Task<IActionResult> PostJournalEntry(Guid id) =>
            Run(async () => Ok(await _gl.PostApprovedAsync(CompanyId, UserId, id)));

        [HttpPost("journal-entries/{id}/cancel")]
        [HasPermission(Permissions.GlJournalsCreate)]
        public Task<IActionResult> CancelJournalEntry(Guid id, JournalReasonRequest? req) =>
            Run(async () => Ok(await _gl.CancelAsync(CompanyId, UserId, id, req?.Reason)));

        /// <summary>Reverses a posted journal with an equal-and-opposite entry (once only). Body: the reason as a JSON string.</summary>
        [HttpPost("journal-entries/{id}/reverse")]
        [HasPermission(Permissions.GlJournalsReverse)]
        public Task<IActionResult> ReverseJournalEntry(Guid id, [FromBody] string reason) =>
            Run(async () => Ok(await _gl.ReverseAsync(CompanyId, id, DateTime.UtcNow, reason, userId: UserId)));

        /// <summary>Business-rule violations come back as 400 with the message; the request transaction rolls back.</summary>
        private static async Task<IActionResult> Run(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (InvalidOperationException ex)
            {
                return new BadRequestObjectResult(new { error = ex.Message });
            }
        }
    }
}
