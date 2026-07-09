using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateGlAccountRequest(string Code, string Name, GlAccountType Type, string? SubType, Guid? ParentId, string Currency, bool IsBankAccount, bool IsControlAccount);
    public record CreateJournalLineRequest(Guid GlAccountId, decimal Debit, decimal Credit, string Currency, decimal ExchangeRate, Guid? CostCentreId, string? Narrative);
    public record CreateJournalRequest(DateTime EntryDate, string? Description, string? Reference, List<CreateJournalLineRequest> Lines);

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

        [HttpGet("accounts")]
        public async Task<IActionResult> GetAccounts() =>
            Ok(await _db.GlAccounts.Where(a => a.CompanyId == CompanyId).OrderBy(a => a.Code).ToListAsync());

        [HttpPost("accounts")]
        public async Task<IActionResult> CreateAccount(CreateGlAccountRequest req)
        {
            var account = new GlAccount
            {
                CompanyId = CompanyId,
                Code = req.Code,
                Name = req.Name,
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

        [HttpGet("cost-centres")]
        public async Task<IActionResult> GetCostCentres() =>
            Ok(await _db.CostCentres.Where(c => c.CompanyId == CompanyId).ToListAsync());

        [HttpPost("cost-centres")]
        public async Task<IActionResult> CreateCostCentre([FromBody] CostCentre req)
        {
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
        public async Task<IActionResult> CreateSegment([FromBody] Segment req)
        {
            req.CompanyId = CompanyId;
            _db.Segments.Add(req);
            await _db.SaveChangesAsync();
            return Ok(req);
        }

        [HttpGet("periods")]
        public async Task<IActionResult> GetPeriods() =>
            Ok(await _db.AccountingPeriods.Where(p => p.CompanyId == CompanyId).OrderByDescending(p => p.Year).ThenBy(p => p.PeriodNo).ToListAsync());

        [HttpPost("periods/{id}/close")]
        public async Task<IActionResult> ClosePeriod(Guid id)
        {
            var period = await _db.AccountingPeriods.FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == CompanyId);
            if (period == null) return NotFound();
            period.Status = PeriodStatus.Closed;
            period.ClosedBy = UserId;
            period.ClosedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(period);
        }

        [HttpGet("journal-entries")]
        public async Task<IActionResult> GetJournalEntries([FromQuery] DateTime? from, [FromQuery] DateTime? to)
        {
            var q = _db.JournalEntries.Include(j => j.Lines).Where(j => j.CompanyId == CompanyId);
            if (from.HasValue) q = q.Where(j => j.EntryDate >= from.Value);
            if (to.HasValue) q = q.Where(j => j.EntryDate <= to.Value);
            return Ok(await q.OrderByDescending(j => j.EntryDate).Take(200).ToListAsync());
        }

        [HttpGet("journal-entries/{id}")]
        public async Task<IActionResult> GetJournalEntry(Guid id)
        {
            var entry = await _db.JournalEntries.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == id && j.CompanyId == CompanyId);
            return entry == null ? NotFound() : Ok(entry);
        }

        /// <summary>Manual journal entry -- immediately posted via the shared GlPostingService, enforcing
        /// the double-entry balance check and open-period validation.</summary>
        [HttpPost("journal-entries")]
        public async Task<IActionResult> CreateJournalEntry(CreateJournalRequest req)
        {
            try
            {
                var lines = req.Lines.Select(l => new GlLineRequest(l.GlAccountId, l.Debit, l.Credit, l.Currency, l.ExchangeRate, l.CostCentreId, l.Narrative));
                var je = await _gl.PostAsync(CompanyId, req.EntryDate, JournalSource.Manual, req.Description, req.Reference, lines);
                return Ok(je);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("journal-entries/{id}/reverse")]
        public async Task<IActionResult> ReverseJournalEntry(Guid id, [FromBody] string reason)
        {
            try
            {
                var reversal = await _gl.ReverseAsync(CompanyId, id, DateTime.UtcNow, reason);
                return Ok(reversal);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
