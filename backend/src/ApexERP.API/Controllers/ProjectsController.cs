using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record SubmitTimesheetRequest(Guid EmployeeId, Guid ProjectId, Guid? TaskId, DateTime Date, decimal Hours, string? Description);

    [Route("api/v1/projects")]
    public class ProjectsController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly ProjectService _projects;

        public ProjectsController(ApexErpDbContext db, ProjectService projects)
        {
            _db = db;
            _projects = projects;
        }

        [HttpGet]
        public async Task<IActionResult> GetProjects() => Ok(await _db.Projects.Where(p => p.CompanyId == CompanyId).ToListAsync());

        [HttpPost]
        public async Task<IActionResult> CreateProject([FromBody] Project p)
        {
            p.CompanyId = CompanyId;
            _db.Projects.Add(p);
            await _db.SaveChangesAsync();
            return Ok(p);
        }

        [HttpGet("{id}/tasks")]
        public async Task<IActionResult> GetTasks(Guid id) => Ok(await _db.ProjectTasks.Where(t => t.CompanyId == CompanyId && t.ProjectId == id).ToListAsync());

        [HttpPost("{id}/tasks")]
        public async Task<IActionResult> CreateTask(Guid id, [FromBody] ProjectTask task)
        {
            task.CompanyId = CompanyId;
            task.ProjectId = id;
            _db.ProjectTasks.Add(task);
            await _db.SaveChangesAsync();
            return Ok(task);
        }

        [HttpGet("{id}/budget-vs-actual")]
        public async Task<IActionResult> GetBudgetVsActual(Guid id)
        {
            try
            {
                var (budget, actual, variance) = await _projects.GetBudgetVsActualAsync(CompanyId, id);
                return Ok(new { budget, actual, variancePct = variance });
            }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpPost("timesheets")]
        public async Task<IActionResult> SubmitTimesheet(SubmitTimesheetRequest req)
        {
            try
            {
                var ts = await _projects.SubmitTimesheetAsync(CompanyId, req.EmployeeId, req.ProjectId, req.TaskId, req.Date, req.Hours, req.Description);
                return Ok(ts);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("timesheets")]
        public async Task<IActionResult> GetTimesheets([FromQuery] Guid? projectId, [FromQuery] Guid? employeeId)
        {
            var q = _db.Timesheets.Where(t => t.CompanyId == CompanyId);
            if (projectId.HasValue) q = q.Where(t => t.ProjectId == projectId);
            if (employeeId.HasValue) q = q.Where(t => t.EmployeeId == employeeId);
            return Ok(await q.OrderByDescending(t => t.Date).ToListAsync());
        }

        [HttpPost("timesheets/{id}/approve")]
        public async Task<IActionResult> ApproveTimesheet(Guid id)
        {
            try
            {
                var ts = await _projects.ApproveTimesheetAsync(CompanyId, id, UserId);
                return Ok(ts);
            }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }
    }
}
