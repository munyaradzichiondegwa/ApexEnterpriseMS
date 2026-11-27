using ApexERP.API.Authorization;
using ApexERP.API.Common;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateGlAccountRequest(string Code, string Name, GlAccountType Type, string? SubType, Guid? ParentId, string Currency, bool IsBankAccount, bool IsControlAccount, bool IsMonetary = false);
    public record SetAccountMonetaryRequest(bool IsMonetary);
    public record SetAccountActiveRequest(bool IsActive);
    public record SetPostingRulesRequest(bool IsPostable, string? AllowedSources);
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
        private readonly PeriodCloseService _close;
        private readonly GovernanceService _gov;
        private readonly AuditService _audit;

        public GlController(ApexErpDbContext db, GlPostingService gl, PeriodCloseService close,
            GovernanceService gov, AuditService audit)
        {
            _db = db;
            _gl = gl;
            _close = close;
            _gov = gov;
            _audit = audit;
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
                IsControlAccount = req.IsControlAccount,
                IsMonetary = req.IsMonetary
            };
            _db.GlAccounts.Add(account);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetAccounts), new { id = account.Id }, account);
        }

        /// <summary>Deactivate (or reactivate) an account. A deactivated account accepts no NEW postings, but its
        /// historical activity still appears in the trial balance and statements.</summary>
        [HttpPut("accounts/{id}/monetary")]
        [HasPermission(Permissions.GlAccountsManage)]
        public async Task<IActionResult> SetAccountMonetary(Guid id, SetAccountMonetaryRequest req)
        {
            var account = await _db.GlAccounts.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == CompanyId);
            if (account == null) return NotFound();
            if (req.IsMonetary && account.Type is not (GlAccountType.Asset or GlAccountType.Liability))
                return BadRequest(new { error = "Only balance-sheet asset or liability accounts can be classified as monetary items." });
            account.IsMonetary = req.IsMonetary;
            await _db.SaveChangesAsync();
            return Ok(account);
        }

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
            Run(async () =>
            {
                // Once a close checklist exists (or the company requires one) every task must be done or waived with a reason.
                await _close.AssertReadyAsync(CompanyId, id);
                return Ok(await _gl.ClosePeriodAsync(CompanyId, id, UserId));
            });

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
        public async Task<IActionResult> GetJournalEntries([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] JournalStatus? status,
            [FromQuery] int? page = null, [FromQuery] int pageSize = 50)
        {
            var q = _db.JournalEntries.Include(j => j.Lines).Where(j => j.CompanyId == CompanyId);
            if (from.HasValue) q = q.Where(j => j.EntryDate >= from.Value);
            if (to.HasValue) q = q.Where(j => j.EntryDate <= to.Value);
            if (status.HasValue) q = q.Where(j => j.Status == status.Value);
            var ordered = q.OrderByDescending(j => j.EntryDate).ThenBy(j => j.EntryNo);
            return page == null ? Ok(await ordered.Take(200).ToListAsync()) : Ok(await ordered.ToPagedAsync(page, pageSize));
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
            Run(async () =>
            {
                // Delegation of authority (spec 8): the approver's role (or a delegation to them) must cover the journal's value.
                var entry = await _db.JournalEntries.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == id && j.CompanyId == CompanyId);
                if (entry != null)
                {
                    var decision = await _gov.CheckApprovalAuthorityAsync(CompanyId, UserId, "JournalEntry", entry.Lines.Sum(l => l.BaseDebit), entry.CreatedBy);
                    if (!decision.Allowed) return StatusCode(StatusCodes.Status403Forbidden, new { error = decision.Reason });
                }
                return Ok(await _gl.ApproveAsync(CompanyId, UserId, id, req?.Comment));
            });

        /// <summary>Sets whether an account accepts postings and which kinds of transaction may post to it (spec GL-002). Body: IsPostable, AllowedSources e.g. "AR,Manual" (empty = all).</summary>
        [HttpPut("accounts/{id}/posting-rules")]
        [HasPermission(Permissions.GlAccountsManage)]
        public Task<IActionResult> SetPostingRules(Guid id, SetPostingRulesRequest req) =>
            Run(async () =>
            {
                var account = await _db.GlAccounts.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == CompanyId);
                if (account == null) return NotFound();
                var sources = (req.AllowedSources ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var bad = sources.Where(x => !Enum.TryParse<JournalSource>(x, true, out _)).ToList();
                if (bad.Count > 0) return BadRequest(new { error = "Unknown source(s): " + string.Join(", ", bad) + ". Valid: " + string.Join(", ", Enum.GetNames<JournalSource>()) });
                account.IsPostable = req.IsPostable;
                account.AllowedSourcesCsv = sources.Count == 0 ? null : string.Join(",", sources.Select(x => Enum.Parse<JournalSource>(x, true).ToString()));
                await _db.SaveChangesAsync();
                return Ok(account);
            });

    public record SetExchangeRateRequest(string FromCurrency, string ToCurrency, DateTime RateDate,
        decimal Rate, bool IsManualOverride = false);

        [HttpGet("fx-rates")]
        [HasPermission(Permissions.GlAccountsRead)]
        public async Task<IActionResult> GetExchangeRates([FromQuery] string? fromCurrency = null,
            [FromQuery] string? toCurrency = null, [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null)
        {
            var query = _db.ExchangeRates.Where(r => r.CompanyId == CompanyId);
            if (!string.IsNullOrWhiteSpace(fromCurrency))
                query = query.Where(r => r.FromCurrency == fromCurrency.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(toCurrency))
                query = query.Where(r => r.ToCurrency == toCurrency.Trim().ToUpperInvariant());
            if (fromDate.HasValue)
            {
                var fromUtc = DateTime.SpecifyKind(fromDate.Value.Date, DateTimeKind.Utc);
                query = query.Where(r => r.RateDate >= fromUtc);
            }
            if (toDate.HasValue)
            {
                var toUtcExclusive = DateTime.SpecifyKind(toDate.Value.Date.AddDays(1), DateTimeKind.Utc);
                query = query.Where(r => r.RateDate < toUtcExclusive);
            }
            return Ok(await query.OrderByDescending(r => r.RateDate)
                .ThenBy(r => r.FromCurrency).ThenBy(r => r.ToCurrency).ToListAsync());
        }

        [HttpPost("fx-rates")]
        [HasPermission(Permissions.GlAccountsManage)]
        public Task<IActionResult> SetExchangeRate([FromBody] SetExchangeRateRequest request) =>
            Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(request.FromCurrency) || string.IsNullOrWhiteSpace(request.ToCurrency) ||
                    request.RateDate == default || request.Rate <= 0m)
                    return BadRequest(new { error = "Currency codes, a rate date and a positive exchange rate are required." });
                var from = request.FromCurrency.Trim().ToUpperInvariant();
                var to = request.ToCurrency.Trim().ToUpperInvariant();
                if (from.Length != 3 || to.Length != 3 || from == to)
                    return BadRequest(new { error = "Provide two different three-letter currency codes." });
                var rateDate = DateTime.SpecifyKind(request.RateDate.Date, DateTimeKind.Utc);
                await _db.InTransactionAsync(async token =>
                {
                    await _db.AcquireTransactionLockAsync($"fx-rate:{CompanyId:N}:{from}:{to}:{rateDate:yyyyMMdd}", token);
                    var existing = await _db.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == CompanyId &&
                        r.FromCurrency == from && r.ToCurrency == to && r.RateDate == rateDate, token);
                    var before = existing == null ? null : new { existing.FromCurrency, existing.ToCurrency,
                        existing.RateDate, existing.Rate, existing.IsManualOverride };
                    if (existing == null)
                    {
                        existing = new ExchangeRate { CompanyId = CompanyId, FromCurrency = from,
                            ToCurrency = to, RateDate = rateDate, Rate = request.Rate,
                            IsManualOverride = request.IsManualOverride };
                        _db.ExchangeRates.Add(existing);
                    }
                    else
                    {
                        existing.Rate = request.Rate;
                        existing.IsManualOverride = request.IsManualOverride;
                    }
                    _audit.Record(CompanyId, UserId, "FxRate.Upserted", "GL", nameof(ExchangeRate), existing.Id,
                        before, new { existing.FromCurrency, existing.ToCurrency, existing.RateDate,
                            existing.Rate, existing.IsManualOverride });
                    await _db.SaveChangesAsync(token);
                });
                return Ok(await _db.ExchangeRates.FirstAsync(r => r.CompanyId == CompanyId &&
                    r.FromCurrency == from && r.ToCurrency == to && r.RateDate == rateDate));
            });

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
