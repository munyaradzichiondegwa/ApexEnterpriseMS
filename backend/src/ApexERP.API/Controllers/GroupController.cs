using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateInterCompanyRuleRequest(Guid ToCompanyId, bool AutoMirror, Guid DefaultSenderArAccountId, Guid DefaultReceiverApAccountId);
    public record NettingRequest(Guid CompanyBId, DateTime SettlementDate, Guid CompanyABankAccountId, Guid CompanyBBankAccountId);
    public record UpdateGroupStructureRequest(Guid? ParentCompanyId, decimal? OwnershipPercentage);

    [Route("api/v1/group")]
    public class GroupController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly InterCompanyService _interCompany;
        private readonly ConsolidationService _consolidation;

        public GroupController(ApexErpDbContext db, InterCompanyService interCompany, ConsolidationService consolidation)
        {
            _db = db;
            _interCompany = interCompany;
            _consolidation = consolidation;
        }

        /// <summary>Lists every company visible to this session's account (used to pick group members --
        /// in a real deployment this would be scoped to companies the authenticated user has a role in).</summary>
        [HttpGet("companies")]
        public async Task<IActionResult> GetAllCompanies() =>
            Ok(await _db.Companies.IgnoreQueryFilters().Select(c => new { c.Id, c.Name, c.ParentCompanyId, c.OwnershipPercentage }).ToListAsync());

        [HttpPut("companies/{id}/structure")]
        public async Task<IActionResult> UpdateGroupStructure(Guid id, UpdateGroupStructureRequest req)
        {
            var company = await _db.Companies.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id);
            if (company == null) return NotFound();
            company.ParentCompanyId = req.ParentCompanyId;
            company.OwnershipPercentage = req.OwnershipPercentage;
            await _db.SaveChangesAsync();
            return Ok(company);
        }

        [HttpGet("companies/{id}/structure")]
        public async Task<IActionResult> GetGroupStructure(Guid id)
        {
            try
            {
                var group = await _consolidation.GetGroupStructureAsync(id);
                return Ok(group.Select(c => new { c.Id, c.Name, c.ParentCompanyId, c.OwnershipPercentage }));
            }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("intercompany-rules")]
        public async Task<IActionResult> GetRules() => Ok(await _db.InterCompanyRules.Where(r => r.CompanyId == CompanyId).ToListAsync());

        [HttpPost("intercompany-rules")]
        public async Task<IActionResult> CreateRule(CreateInterCompanyRuleRequest req)
        {
            var rule = new InterCompanyRule
            {
                CompanyId = CompanyId,
                FromCompanyId = CompanyId,
                ToCompanyId = req.ToCompanyId,
                AutoMirror = req.AutoMirror,
                DefaultSenderArAccountId = req.DefaultSenderArAccountId,
                DefaultReceiverApAccountId = req.DefaultReceiverApAccountId
            };
            _db.InterCompanyRules.Add(rule);
            await _db.SaveChangesAsync();
            return Ok(rule);
        }

        /// <summary>Triggers the mirror AP invoice in the counterparty entity for an already-posted
        /// AR invoice whose customer is linked to another group company.</summary>
        [HttpPost("intercompany/mirror/{arInvoiceId}")]
        public async Task<IActionResult> CreateMirrorPosting(Guid arInvoiceId)
        {
            try
            {
                var transaction = await _interCompany.CreateMirrorPostingAsync(CompanyId, arInvoiceId);
                return Ok(transaction);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("intercompany/reconciliation")]
        public async Task<IActionResult> GetReconciliation() => Ok(await _interCompany.GetReconciliationReportAsync(CompanyId));

        [HttpGet("intercompany/netting/propose/{companyBId}")]
        public async Task<IActionResult> ProposeNetting(Guid companyBId) => Ok(await _interCompany.ProposeNettingAsync(CompanyId, companyBId));

        [HttpPost("intercompany/netting/execute")]
        public async Task<IActionResult> ExecuteNetting(NettingRequest req)
        {
            try
            {
                var batch = await _interCompany.ExecuteNettingAsync(CompanyId, req.CompanyBId, req.SettlementDate, req.CompanyABankAccountId, req.CompanyBBankAccountId);
                return Ok(batch);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>Consolidated Balance Sheet + eliminations + non-controlling interest for the
        /// group rooted at this company (must be a parent).</summary>
        [HttpGet("consolidated-statements")]
        public async Task<IActionResult> GetConsolidatedStatements([FromQuery] DateTime? asOf)
        {
            try
            {
                var result = await _consolidation.ConsolidateAsync(CompanyId, asOf ?? DateTime.UtcNow);
                return Ok(result);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
