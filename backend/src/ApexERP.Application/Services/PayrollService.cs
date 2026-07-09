using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record RunPayrollResult(PayrollRun Run, List<PayrollLine> Lines);

    /// <summary>
    /// Implements PRD Module 8 (Payroll & HR): per-employee gross-to-net calculation using ZIMRA
    /// PAYE tax bands stored in the database (config-driven, no redeploy needed for annual budget
    /// changes), NSSA (3% employee / 3% employer, capped at the NSSA insurable ceiling), and the
    /// AIDS Levy (3% of PAYE). Auto-posts the payroll run to the GL on approval.
    /// </summary>
    public class PayrollService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        // NSSA insurable earnings ceiling - configurable in a real deployment via a settings table;
        // hard capped here at a representative figure to keep the calculation realistic.
        private const decimal NssaInsurableCeiling = 700m;
        private const decimal NssaRate = 0.03m;
        private const decimal AidsLevyRate = 0.03m;

        public PayrollService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        /// <summary>Calculates PAYE for a given monthly gross using the effective ZIMRA tax bands
        /// (cumulative "deduct" tax-table technique: tax = gross * marginalRate - deduct).</summary>
        public async Task<decimal> CalculatePayeAsync(Guid companyId, decimal monthlyGross, DateTime asOf, CancellationToken ct = default)
        {
            var bands = await _db.PayeTaxBands
                .Where(b => b.CompanyId == companyId && b.EffectiveFrom <= asOf && (b.EffectiveTo == null || b.EffectiveTo >= asOf))
                .OrderBy(b => b.LowerBound)
                .ToListAsync(ct);

            if (!bands.Any())
                throw new InvalidOperationException("No PAYE tax bands configured for this company/period. Configure ZW Tax Compliance > PAYE Tables first.");

            var band = bands.LastOrDefault(b => monthlyGross >= b.LowerBound) ?? bands.First();
            var paye = (monthlyGross * band.Rate) - band.Deduct;
            return Math.Max(0m, Math.Round(paye, 6));
        }

        public decimal CalculateNssaEmployee(decimal monthlyGross)
        {
            var insurable = Math.Min(monthlyGross, NssaInsurableCeiling);
            return Math.Round(insurable * NssaRate, 6);
        }

        public decimal CalculateNssaEmployer(decimal monthlyGross)
        {
            var insurable = Math.Min(monthlyGross, NssaInsurableCeiling);
            return Math.Round(insurable * NssaRate, 6);
        }

        public decimal CalculateAidsLevy(decimal paye) => Math.Round(paye * AidsLevyRate, 6);

        /// <summary>
        /// Runs payroll for every active employee for the given month/year, computing gross-to-net
        /// per employee. Does NOT post to GL (draft state) -- posting happens on ApproveAndPostAsync,
        /// matching the PRD's draft -> approved -> posted workflow.
        /// </summary>
        public async Task<RunPayrollResult> RunAsync(Guid companyId, int month, int year,
            Dictionary<Guid, decimal>? allowancesByEmployee = null, CancellationToken ct = default)
        {
            var already = await _db.PayrollRuns.AnyAsync(
                p => p.CompanyId == companyId && p.PeriodMonth == month && p.PeriodYear == year, ct);
            if (already)
                throw new InvalidOperationException($"A payroll run for {month}/{year} already exists.");

            var employees = await _db.Employees.Where(e => e.CompanyId == companyId && e.IsActive).ToListAsync(ct);
            var asOf = new DateTime(year, month, DateTime.DaysInMonth(year, month));

            var run = new PayrollRun { CompanyId = companyId, PeriodMonth = month, PeriodYear = year, Status = PayrollRunStatus.Draft };
            var lines = new List<PayrollLine>();

            foreach (var emp in employees)
            {
                var allowances = allowancesByEmployee != null && allowancesByEmployee.TryGetValue(emp.Id, out var a) ? a : 0m;
                var gross = emp.BasicSalary + allowances;
                var paye = await CalculatePayeAsync(companyId, gross, asOf, ct);
                var nssaEmp = CalculateNssaEmployee(gross);
                var nssaEmployer = CalculateNssaEmployer(gross);
                var aidsLevy = CalculateAidsLevy(paye);
                var net = gross - paye - nssaEmp - aidsLevy;

                lines.Add(new PayrollLine
                {
                    PayrollRunId = run.Id,
                    EmployeeId = emp.Id,
                    BasicPay = emp.BasicSalary,
                    Allowances = allowances,
                    GrossPay = gross,
                    Paye = paye,
                    NssaEmployee = nssaEmp,
                    NssaEmployer = nssaEmployer,
                    AidsLevy = aidsLevy,
                    NetPay = Math.Round(net, 6)
                });
            }

            run.Lines = lines;
            _db.PayrollRuns.Add(run);
            await _db.SaveChangesAsync(ct);
            return new RunPayrollResult(run, lines);
        }

        /// <summary>Approves the run and posts a single consolidated journal entry:
        /// Dr Salaries Expense (gross); Cr PAYE Payable, Cr NSSA Payable (employee+employer),
        /// Cr AIDS Levy Payable, Cr Net Salaries Payable. Employer NSSA is also expensed.</summary>
        public async Task<PayrollRun> ApproveAndPostAsync(Guid companyId, Guid payrollRunId, Guid approvedByUserId,
            Guid salariesExpenseAccountId, Guid nssaEmployerExpenseAccountId, Guid payeControlAccountId,
            Guid nssaControlAccountId, Guid aidsLevyControlAccountId, Guid netPayControlAccountId,
            CancellationToken ct = default)
        {
            var run = await _db.PayrollRuns.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Payroll run not found.");

            if (run.Status != PayrollRunStatus.Draft)
                throw new InvalidOperationException("Only draft payroll runs can be approved and posted.");

            var totalGross = run.Lines.Sum(l => l.GrossPay);
            var totalPaye = run.Lines.Sum(l => l.Paye);
            var totalNssaEmp = run.Lines.Sum(l => l.NssaEmployee);
            var totalNssaEmployer = run.Lines.Sum(l => l.NssaEmployer);
            var totalAidsLevy = run.Lines.Sum(l => l.AidsLevy);
            var totalNet = run.Lines.Sum(l => l.NetPay);

            var entryDate = new DateTime(run.PeriodYear, run.PeriodMonth, DateTime.DaysInMonth(run.PeriodYear, run.PeriodMonth));

            var glLines = new List<GlLineRequest>
            {
                new(salariesExpenseAccountId, totalGross, 0, "USD", 1m, null, "Gross salaries expense"),
                new(nssaEmployerExpenseAccountId, totalNssaEmployer, 0, "USD", 1m, null, "NSSA employer contribution expense"),
                new(payeControlAccountId, 0, totalPaye, "USD", 1m, null, "PAYE payable to ZIMRA"),
                new(nssaControlAccountId, 0, totalNssaEmp + totalNssaEmployer, "USD", 1m, null, "NSSA payable (employee + employer)"),
                new(aidsLevyControlAccountId, 0, totalAidsLevy, "USD", 1m, null, "AIDS Levy payable"),
                new(netPayControlAccountId, 0, totalNet, "USD", 1m, null, "Net salaries payable to employees")
            };

            var je = await _gl.PostAsync(companyId, entryDate, JournalSource.Payroll,
                $"Payroll {run.PeriodMonth}/{run.PeriodYear}", $"PAYROLL-{run.PeriodYear}{run.PeriodMonth:D2}", glLines, ct);

            run.JournalEntryId = je.Id;
            run.Status = PayrollRunStatus.Posted;
            run.ApprovedBy = approvedByUserId;
            run.ApprovedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return run;
        }
    }
}
