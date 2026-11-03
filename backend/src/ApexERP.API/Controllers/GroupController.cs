using ApexERP.API.Authorization;
using ApexERP.Application.Security;
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

    /// <summary>
    /// Group / inter-company / consolidation. Everything here is scoped to the caller's OWN group (itself, its parents and its
    /// subsidiaries): the earlier version listed every company in the system and let any user re-parent any company, after which
    /// "consolidated statements" would read that company's books -- a cross-tenant data-exposure path closed by this scoping.
    /// </summary>
    [Route("api/v1/group")]
    public class GroupController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly InterCompanyService _interCompany;
        private readonly ConsolidationService _consolidation;
        private readonly GroupScopeService _scope;
        private readonly AuditService _audit;

        public GroupController(ApexErpDbContext db, InterCompanyService interCompany, ConsolidationService consolidation,
            GroupScopeService scope, AuditService audit)
        {
            _db = db;
            _interCompany = interCompany;
            _consolidation = consolidation;
            _scope = scope;
            _audit = audit;
        }

        /// <summary>Lists the companies in this company's own group (itself, its parents and subsidiaries) -- never other tenants.</summary>
        [HttpGet("companies")]
        public async Task<IActionResult> GetGroupCompanies()
        {
            var visible = (await _scope.GetVisibleCompanyIdsAsync(CompanyId)).ToList();
            return Ok(await _db.Companies.Where(c => visible.Contains(c.Id))
                .Select(c => new { c.Id, c.Name, c.ParentCompanyId, c.OwnershipPercentage }).ToListAsync());
        }

        /// <summary>
        /// Changes where a company sits in the group. Allowed only for your own company and its subsidiaries; placing your own
        /// company under an outside parent additionally requires that parent to have registered you as a linked customer or
        /// supplier (consent from both sides).
        /// </summary>
        [HttpPut("companies/{id}/structure")]
        [HasPermission(Permissions.GroupStructureManage)]
        public async Task<IActionResult> UpdateGroupStructure(Guid id, UpdateGroupStructureRequest req)
        {
            if (req.OwnershipPercentage is < 0 or > 100)
                return BadRequest(new { error = "Ownership percentage must be between 0 and 100." });

            var problem = await _scope.ValidateStructureChangeAsync(CompanyId, id, req.ParentCompanyId);
            if (problem != null)
                return problem == "Company not found." ? NotFound(new { error = problem }) : BadRequest(new { error = problem });

            var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == id);
            if (company == null) return NotFound();

            var before = new { company.ParentCompanyId, company.OwnershipPercentage };
            company.ParentCompanyId = req.ParentCompanyId;
            company.OwnershipPercentage = req.OwnershipPercentage;
            _audit.Record(CompanyId, UserId, "Group.StructureChanged", "Group", nameof(Company), company.Id,
                before, new { company.ParentCompanyId, company.OwnershipPercentage });
            await _db.SaveChangesAsync();
            return Ok(new { company.Id, company.Name, company.ParentCompanyId, company.OwnershipPercentage });
        }

        [HttpGet("companies/{id}/structure")]
        public async Task<IActionResult> GetGroupStructure(Guid id)
        {
            if (!await _scope.IsVisibleAsync(CompanyId, id))
                return NotFound(new { error = "Company not found." });

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
            if (req.ToCompanyId == CompanyId)
                return BadRequest(new { error = "An inter-company rule must point at a different company." });

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

        /// <summary>(Re)triggers the mirror AP invoice in the counterparty entity for an already-posted AR invoice whose
        /// customer is linked to another group company. Idempotent; a failure is queued as an exception and reported
        /// in the outcome rather than thrown.</summary>
        [HttpPost("intercompany/mirror/{arInvoiceId}")]
        public async Task<IActionResult> CreateMirrorPosting(Guid arInvoiceId)
        {
            try
            {
                return Ok(await _interCompany.TryMirrorAsync(CompanyId, arInvoiceId, UserId));
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("intercompany/reconciliation")]
        public async Task<IActionResult> GetReconciliation() => Ok(await _interCompany.GetReconciliationReportAsync(CompanyId));

        [HttpGet("intercompany/netting/propose/{companyBId}")]
        public async Task<IActionResult> ProposeNetting(Guid companyBId)
        {
            if (!await _scope.IsVisibleAsync(CompanyId, companyBId))
                return NotFound(new { error = "Company not found in your group." });
            return Ok(await _interCompany.ProposeNettingAsync(CompanyId, companyBId));
        }

        [HttpPost("intercompany/netting/execute")]
        [HasPermission(Permissions.GroupNettingExecute)]
        public async Task<IActionResult> ExecuteNetting(NettingRequest req)
        {
            if (!await _scope.IsVisibleAsync(CompanyId, req.CompanyBId))
                return NotFound(new { error = "Company not found in your group." });

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
        [HasPermission(Permissions.GroupConsolidationRead)]
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
