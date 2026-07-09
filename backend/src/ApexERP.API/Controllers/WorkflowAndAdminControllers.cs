using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateWorkflowRequest(string RequestType, Guid ReferenceId, string ReferenceTable, Guid CurrentApproverId, string? Notes);
    public record CreateApprovalChainRequest(string Name, string EntityType, decimal AmountThreshold, Guid? FundCentreId, List<ChainTierRequest> Tiers);
    public record ChainTierRequest(int TierOrder, Guid ApproverRoleId, int SlaHours);
    public record SubmitForApprovalRequest(string EntityType, Guid ReferenceId, string ReferenceTable, decimal Amount, Guid? FundCentreId);
    public record DecideWithChainRequest(bool Approved, string? Comments);

    [Route("api/v1/workflow")]
    public class WorkflowController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly ApprovalChainService _chains;

        public WorkflowController(ApexErpDbContext db, ApprovalChainService chains)
        {
            _db = db;
            _chains = chains;
        }

        [HttpGet("pending")]
        public async Task<IActionResult> GetPending() =>
            Ok(await _db.WorkflowRequests.Where(w => w.CompanyId == CompanyId && w.CurrentApproverId == UserId && w.Status == WorkflowStatus.Pending).ToListAsync());

        [HttpPost]
        public async Task<IActionResult> Create(CreateWorkflowRequest req)
        {
            var wf = new WorkflowRequest
            {
                CompanyId = CompanyId,
                RequestType = req.RequestType,
                ReferenceId = req.ReferenceId,
                ReferenceTable = req.ReferenceTable,
                SubmittedBy = UserId,
                CurrentApproverId = req.CurrentApproverId,
                Notes = req.Notes,
                Status = WorkflowStatus.Pending
            };
            _db.WorkflowRequests.Add(wf);
            await _db.SaveChangesAsync();
            return Ok(wf);
        }

        [HttpPost("{id}/decide")]
        public async Task<IActionResult> Decide(Guid id, [FromBody] bool approve)
        {
            var wf = await _db.WorkflowRequests.FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == CompanyId);
            if (wf == null) return NotFound();
            wf.Status = approve ? WorkflowStatus.Approved : WorkflowStatus.Rejected;
            await _db.SaveChangesAsync();
            return Ok(wf);
        }

        // ---- Multi-tier approval chains (PRD Module 16, applied generically) ----

        [HttpGet("chains")]
        public async Task<IActionResult> GetChains() => Ok(await _db.ApprovalChains.Include(c => c.Tiers).Where(c => c.CompanyId == CompanyId).ToListAsync());

        [HttpPost("chains")]
        public async Task<IActionResult> CreateChain(CreateApprovalChainRequest req)
        {
            try
            {
                var chain = await _chains.CreateChainAsync(CompanyId, req.Name, req.EntityType, req.AmountThreshold, req.FundCentreId,
                    req.Tiers.Select(t => (t.TierOrder, t.ApproverRoleId, t.SlaHours)).ToList());
                return Ok(chain);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("submit-for-approval")]
        public async Task<IActionResult> SubmitForApproval(SubmitForApprovalRequest req) =>
            Ok(await _chains.SubmitForApprovalAsync(CompanyId, req.EntityType, req.ReferenceId, req.ReferenceTable, UserId, req.Amount, req.FundCentreId));

        [HttpPost("{id}/decide-tier")]
        public async Task<IActionResult> DecideTier(Guid id, DecideWithChainRequest req)
        {
            try
            {
                var wf = await _chains.DecideAsync(CompanyId, id, UserId, req.Approved, req.Comments);
                return Ok(wf);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("overdue")]
        public async Task<IActionResult> GetOverdue() => Ok(await _chains.GetOverdueRequestsAsync(CompanyId));
    }

    [Route("api/v1/admin")]
    public class AdminController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;

        public AdminController(ApexErpDbContext db)
        {
            _db = db;
        }

        [HttpGet("roles")]
        public async Task<IActionResult> GetRoles() => Ok(await _db.Roles.Where(r => r.CompanyId == CompanyId).ToListAsync());

        [HttpPost("roles")]
        public async Task<IActionResult> CreateRole([FromBody] Role role)
        {
            role.CompanyId = CompanyId;
            _db.Roles.Add(role);
            await _db.SaveChangesAsync();
            return Ok(role);
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers() =>
            Ok(await _db.Users.Where(u => u.CompanyId == CompanyId)
                .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, u.RoleId, u.IsActive, u.LastLogin })
                .ToListAsync());

        [HttpPost("users")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
        {
            var user = new User
            {
                CompanyId = CompanyId,
                Email = req.Email.ToLowerInvariant(),
                PasswordHash = Application.Security.AuthService.HashPassword(req.Password),
                RoleId = req.RoleId,
                FirstName = req.FirstName,
                LastName = req.LastName,
                IsActive = true
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return Ok(new { user.Id, user.Email, user.FirstName, user.LastName });
        }

        [HttpGet("company")]
        public async Task<IActionResult> GetCompany() => Ok(await _db.Companies.FirstOrDefaultAsync(c => c.Id == CompanyId));

        [HttpPut("company")]
        public async Task<IActionResult> UpdateCompany([FromBody] Company update)
        {
            var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == CompanyId);
            if (company == null) return NotFound();
            company.Name = update.Name;
            company.RegistrationNo = update.RegistrationNo;
            company.VatNo = update.VatNo;
            company.Address = update.Address;
            company.Sector = update.Sector;
            company.LogoUrl = update.LogoUrl;
            company.BaseCurrency = update.BaseCurrency;
            company.FunctionalCurrency = update.FunctionalCurrency;
            await _db.SaveChangesAsync();
            return Ok(company);
        }
    }

    public record CreateUserRequest(string Email, string Password, string FirstName, string LastName, Guid RoleId);
}
