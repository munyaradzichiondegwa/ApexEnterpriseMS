using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record RunPayrollRequest(int Month, int Year, Dictionary<Guid, decimal>? Allowances);
    public record ApprovePayrollRequest(Guid SalariesExpenseAccountId, Guid NssaEmployerExpenseAccountId,
        Guid PayeControlAccountId, Guid NssaControlAccountId, Guid AidsLevyControlAccountId, Guid NetPayControlAccountId);

    [Route("api/v1/payroll")]
    public class PayrollController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly PayrollService _payroll;

        public PayrollController(ApexErpDbContext db, PayrollService payroll)
        {
            _db = db;
            _payroll = payroll;
        }

        [HttpGet("departments")]
        public async Task<IActionResult> GetDepartments() => Ok(await _db.Departments.Where(d => d.CompanyId == CompanyId).ToListAsync());

        [HttpPost("departments")]
        public async Task<IActionResult> CreateDepartment([FromBody] Department d)
        {
            d.CompanyId = CompanyId;
            _db.Departments.Add(d);
            await _db.SaveChangesAsync();
            return Ok(d);
        }

        [HttpGet("employees")]
        public async Task<IActionResult> GetEmployees() => Ok(await _db.Employees.Where(e => e.CompanyId == CompanyId).ToListAsync());

        [HttpPost("employees")]
        public async Task<IActionResult> CreateEmployee([FromBody] Employee e)
        {
            e.CompanyId = CompanyId;
            _db.Employees.Add(e);
            await _db.SaveChangesAsync();
            return Ok(e);
        }

        [HttpGet("paye-bands")]
        public async Task<IActionResult> GetPayeBands() =>
            Ok(await _db.PayeTaxBands.Where(b => b.CompanyId == CompanyId).OrderBy(b => b.LowerBound).ToListAsync());

        [HttpPost("paye-bands")]
        public async Task<IActionResult> CreatePayeBand([FromBody] PayeTaxBand b)
        {
            b.CompanyId = CompanyId;
            _db.PayeTaxBands.Add(b);
            await _db.SaveChangesAsync();
            return Ok(b);
        }

        [HttpGet("runs")]
        public async Task<IActionResult> GetRuns() =>
            Ok(await _db.PayrollRuns.Where(r => r.CompanyId == CompanyId).OrderByDescending(r => r.PeriodYear).ThenByDescending(r => r.PeriodMonth).ToListAsync());

        [HttpGet("runs/{id}")]
        public async Task<IActionResult> GetRun(Guid id)
        {
            var run = await _db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == CompanyId);
            return run == null ? NotFound() : Ok(run);
        }

        /// <summary>Runs payroll for every active employee: PAYE via ZIMRA tax bands, NSSA 3%/3%,
        /// AIDS Levy 3% of PAYE. Target: completes well within the PRD's 60-second/500-employee SLA.</summary>
        [HttpPost("run")]
        public async Task<IActionResult> RunPayroll(RunPayrollRequest req)
        {
            try
            {
                var result = await _payroll.RunAsync(CompanyId, req.Month, req.Year, req.Allowances);
                return Ok(new { run = result.Run, lines = result.Lines });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("runs/{id}/approve-and-post")]
        public async Task<IActionResult> ApproveAndPost(Guid id, ApprovePayrollRequest req)
        {
            try
            {
                var run = await _payroll.ApproveAndPostAsync(CompanyId, id, UserId, req.SalariesExpenseAccountId,
                    req.NssaEmployerExpenseAccountId, req.PayeControlAccountId, req.NssaControlAccountId,
                    req.AidsLevyControlAccountId, req.NetPayControlAccountId);
                return Ok(run);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>Payslip for a single employee within a run -- gross, PAYE, NSSA, AIDS Levy, net.</summary>
        [HttpGet("runs/{runId}/payslip/{employeeId}")]
        public async Task<IActionResult> GetPayslip(Guid runId, Guid employeeId)
        {
            var line = await _db.PayrollLines.FirstOrDefaultAsync(l => l.PayrollRunId == runId && l.EmployeeId == employeeId);
            if (line == null) return NotFound();
            var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == CompanyId);
            return Ok(new { employee, payslip = line });
        }
    }
}
