using System.Text.Json;
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

        /// <summary>The caller's own pending approvals -- available to every signed-in user.</summary>
        [HttpGet("pending")]
        [AuthenticatedOnly]
        public async Task<IActionResult> GetPending() =>
            Ok(await _db.WorkflowRequests.Where(w => w.CompanyId == CompanyId && w.CurrentApproverId == UserId && w.Status == WorkflowStatus.Pending).ToListAsync());

        [HttpPost]
        public async Task<IActionResult> Create(CreateWorkflowRequest req)
        {
            if (req.CurrentApproverId == UserId)
                return BadRequest(new { error = "You cannot assign an approval request to yourself." });
            // The tenant filter scopes this lookup to the caller's company, so only a colleague can be named.
            if (!await _db.Users.AnyAsync(u => u.Id == req.CurrentApproverId && u.IsActive))
                return BadRequest(new { error = "The approver was not found in your company." });

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

        /// <summary>Decides a simple (single-approver) request. Only the assigned approver may decide it, never its
        /// submitter, and only while it is still pending.</summary>
        [HttpPost("{id}/decide")]
        [HasPermission(Permissions.WorkflowDecide)]
        public async Task<IActionResult> Decide(Guid id, [FromBody] bool approve)
        {
            var wf = await _db.WorkflowRequests.FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == CompanyId);
            if (wf == null) return NotFound();
            if (wf.Status != WorkflowStatus.Pending)
                return BadRequest(new { error = "This request has already been decided." });
            if (wf.SubmittedBy == UserId)
                return BadRequest(new { error = "You submitted this request, so someone else must decide it." });
            if (wf.CurrentApproverId != UserId)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "This request is assigned to a different approver." });
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
        [HasPermission(Permissions.WorkflowDecide)]
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

        /// <summary>Creates a role. Roles are the unit of access control, so this is itself a protected action and a
        /// caller can never hand out a permission they do not hold themselves (no privilege escalation).</summary>
        [HttpPost("roles")]
        [HasPermission(Permissions.AdminRolesManage)]
        public async Task<IActionResult> CreateRole([FromBody] Role role)
        {
            if (string.IsNullOrWhiteSpace(role.Name))
                return BadRequest(new { error = "A role name is required." });

            var requested = PermissionEvaluator.Parse(role.PermissionsJson).ToList();
            if (requested.Count == 0)
                return BadRequest(new { error = "Choose at least one permission for the role." });

            var unknown = requested.Where(r => !Permissions.IsKnown(r)).ToList();
            if (unknown.Count > 0)
                return BadRequest(new { error = $"Unknown permission(s): {string.Join(", ", unknown)}." });

            var notGrantable = PermissionEvaluator.NotGrantable(GrantedPermissions, requested);
            if (notGrantable.Count > 0)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = $"You cannot grant permissions you do not hold yourself: {string.Join(", ", notGrantable)}." });

            var name = role.Name.Trim();
            if (await _db.Roles.AnyAsync(r => r.CompanyId == CompanyId && r.Name == name))
                return Conflict(new { error = $"A role named '{name}' already exists." });

            var created = new Role
            {
                CompanyId = CompanyId,
                Name = name,
                IsSystemRole = false,
                PermissionsJson = JsonSerializer.Serialize(requested.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            };
            _db.Roles.Add(created);
            await _db.SaveChangesAsync();
            return Ok(created);
        }

        /// <summary>The permission keys that can be put in a role (for the role editor).</summary>
        [HttpGet("permissions")]
        public IActionResult GetPermissionCatalogue() => Ok(new
        {
            all = Permissions.All,
            explicitPermissions = Permissions.Catalogue,
            moduleDefaults = Permissions.Modules.SelectMany(m => new[] { m + ".read", m + ".write" })
        });

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers() =>
            Ok(await _db.Users.Where(u => u.CompanyId == CompanyId)
                .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, u.RoleId, u.IsActive, u.LastLogin })
                .ToListAsync());

        [HttpPost("users")]
        [HasPermission(Permissions.AdminUsersManage)]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
        {
            var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return BadRequest(new { error = "A valid e-mail address is required." });

            var passwordProblem = PasswordPolicy.Validate(req.Password, email);
            if (passwordProblem != null) return BadRequest(new { error = passwordProblem });

            // The role must belong to THIS company (the tenant filter guarantees it) ...
            var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == req.RoleId && r.CompanyId == CompanyId);
            if (role == null) return BadRequest(new { error = "The selected role was not found in your company." });

            // ... and may not carry more power than the person creating the user.
            var notGrantable = PermissionEvaluator.NotGrantable(GrantedPermissions, PermissionEvaluator.Parse(role.PermissionsJson));
            if (notGrantable.Count > 0)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "You cannot assign a role that carries permissions you do not hold yourself." });

            // E-mail addresses are unique across the whole system (sign-in looks users up by e-mail alone).
            if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
                return Conflict(new { error = "A user with this e-mail address already exists." });

            var user = new User
            {
                CompanyId = CompanyId,
                Email = email,
                PasswordHash = AuthService.HashPassword(req.Password),
                RoleId = role.Id,
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
        [HasPermission(Permissions.AdminCompanyManage)]
        public async Task<IActionResult> UpdateCompany([FromBody] Company update)
        {
            var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == CompanyId);
            if (company == null) return NotFound();
            if (string.IsNullOrWhiteSpace(update.Name))
                return BadRequest(new { error = "A company name is required." });

            // Changing the reporting currency after postings exist would silently re-state every base amount.
            var currencyChanged = !string.Equals(company.BaseCurrency, update.BaseCurrency, StringComparison.OrdinalIgnoreCase)
                                  || !string.Equals(company.FunctionalCurrency, update.FunctionalCurrency, StringComparison.OrdinalIgnoreCase);
            if (currencyChanged && await _db.JournalEntries.AnyAsync(j => j.CompanyId == CompanyId))
                return BadRequest(new { error = "The base and functional currency cannot be changed once journal entries exist." });

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
