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
    /// changes), NSSA (4.5% employee / 4.5% employer, subject to the currency-specific insurable ceiling), and the
    /// AIDS Levy (3% of PAYE). Auto-posts the payroll run to the GL on approval.
    /// </summary>
    public class PayrollService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        // Official NSSA POBS USD baseline: 4.5% employee + 4.5% employer, capped at USD 700.
        // Other currencies require an approved, effective-dated StatutoryRate; never reuse a USD ceiling.
        private const decimal NssaUsdInsurableCeiling = 700m;
        private const decimal NssaRate = 0.045m;
        private const decimal AidsLevyRate = 0.03m;

        public PayrollService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        /// <summary>Calculates PAYE for a given monthly gross using the effective ZIMRA tax bands
        /// (cumulative "deduct" tax-table technique: tax = gross * marginalRate - deduct).</summary>
        public async Task<decimal> CalculatePayeAsync(Guid companyId, decimal monthlyGross, DateTime asOf, string currency = "USD", CancellationToken ct = default)
        {
            currency = (currency ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException("A PAYE currency is required.");
            var bands = await _db.PayeTaxBands
                .Where(b => b.CompanyId == companyId && b.Currency == currency && b.EffectiveFrom <= asOf && (b.EffectiveTo == null || b.EffectiveTo >= asOf))
                .OrderBy(b => b.LowerBound)
                .ToListAsync(ct);

            if (!bands.Any())
                throw new InvalidOperationException("No PAYE tax bands configured for this company/period. Configure ZW Tax Compliance > PAYE Tables first.");

            var band = bands.LastOrDefault(b => monthlyGross >= b.LowerBound) ?? bands.First();
            var paye = (monthlyGross * band.Rate) - band.Deduct;
            return Math.Max(0m, Math.Round(paye, 6));
        }

        // Statutory rates are effective-dated configuration (StatutoryRate rows). The constants above are only the
        // fallback when a company has not configured a code yet, so existing tenants keep working (spec 25.2).
        private static StatutoryRate? Pick(IReadOnlyList<StatutoryRate>? rates, string code, string currency) =>
            rates?.Where(r => r.Code == code && string.Equals(r.Currency, currency, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();

        public decimal CalculateNssaEmployee(decimal monthlyGross, IReadOnlyList<StatutoryRate>? rates = null, string currency = "USD")
        {
            currency = (currency ?? string.Empty).Trim().ToUpperInvariant();
            var r = Pick(rates, "NSSA_EMPLOYEE", currency);
            if (r == null && currency != "USD")
                throw new InvalidOperationException($"No approved NSSA employee rate and insurable ceiling are configured for {currency}.");
            var insurable = r != null
                ? (r.Ceiling.HasValue ? Math.Min(monthlyGross, r.Ceiling.Value) : monthlyGross)
                : Math.Min(monthlyGross, NssaUsdInsurableCeiling);
            return Math.Round(insurable * (r != null ? r.RatePct / 100m : NssaRate), 6);
        }

        public decimal CalculateNssaEmployer(decimal monthlyGross, IReadOnlyList<StatutoryRate>? rates = null, string currency = "USD")
        {
            currency = (currency ?? string.Empty).Trim().ToUpperInvariant();
            var r = Pick(rates, "NSSA_EMPLOYER", currency);
            if (r == null && currency != "USD")
                throw new InvalidOperationException($"No approved NSSA employer rate and insurable ceiling are configured for {currency}.");
            var insurable = r != null
                ? (r.Ceiling.HasValue ? Math.Min(monthlyGross, r.Ceiling.Value) : monthlyGross)
                : Math.Min(monthlyGross, NssaUsdInsurableCeiling);
            return Math.Round(insurable * (r != null ? r.RatePct / 100m : NssaRate), 6);
        }

        public decimal CalculateAidsLevy(decimal paye, IReadOnlyList<StatutoryRate>? rates = null, string currency = "USD")
        {
            var r = Pick(rates, "AIDS_LEVY", currency);
            return Math.Round(paye * (r != null ? r.RatePct / 100m : AidsLevyRate), 6);
        }

        /// <summary>
        /// Runs payroll for every active employee for the given month/year, computing gross-to-net
        /// per employee. Does NOT post to GL (draft state) -- posting happens on ApproveAndPostAsync,
        /// matching the PRD's draft -> approved -> posted workflow.
        /// </summary>
        public async Task<RunPayrollResult> RunAsync(Guid companyId, int month, int year,
            Dictionary<Guid, decimal>? allowancesByEmployee = null, string currency = "USD", CancellationToken ct = default)
        {
            currency = (currency ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException("A payroll currency is required.");
            var already = await _db.PayrollRuns.AnyAsync(
                p => p.CompanyId == companyId && p.PeriodMonth == month && p.PeriodYear == year && p.Currency == currency, ct);
            if (already)
                throw new InvalidOperationException($"A {currency} payroll run for {month}/{year} already exists.");

            var employees = await _db.Employees.Where(e => e.CompanyId == companyId && e.IsActive && e.Currency.ToUpper() == currency).ToListAsync(ct);
            if (employees.Count == 0)
                throw new InvalidOperationException($"No active employees are configured for payroll currency {currency}.");
            var asOf = new DateTime(year, month, DateTime.DaysInMonth(year, month));

            var rates = await _db.StatutoryRates
                .Where(r => r.CompanyId == companyId && r.Currency == currency && r.ApprovedBy != null &&
                    r.EffectiveFrom <= asOf && (r.EffectiveTo == null || r.EffectiveTo >= asOf))
                .ToListAsync(ct);

            var run = new PayrollRun { CompanyId = companyId, PeriodMonth = month, PeriodYear = year, Currency = currency, Status = PayrollRunStatus.Draft };
            var lines = new List<PayrollLine>();

            foreach (var emp in employees)
            {
                var allowances = allowancesByEmployee != null && allowancesByEmployee.TryGetValue(emp.Id, out var a) ? a : 0m;
                var gross = emp.BasicSalary + allowances;
                var paye = await CalculatePayeAsync(companyId, gross, asOf, currency, ct);
                var nssaEmp = CalculateNssaEmployee(gross, rates, currency);
                var nssaEmployer = CalculateNssaEmployer(gross, rates, currency);
                var aidsLevy = CalculateAidsLevy(paye, rates, currency);
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

            var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                ?? throw new InvalidOperationException("Company not found.");
            var functionalCurrency = company.FunctionalCurrency.Trim().ToUpperInvariant();
            var fxRate = 1m;
            if (!string.Equals(run.Currency, functionalCurrency, StringComparison.OrdinalIgnoreCase))
            {
                var direct = await _db.ExchangeRates.Where(r => r.CompanyId == companyId &&
                    r.FromCurrency == run.Currency && r.ToCurrency == functionalCurrency &&
                    r.RateDate <= entryDate && r.Rate > 0).OrderByDescending(r => r.RateDate).FirstOrDefaultAsync(ct);
                if (direct != null) fxRate = direct.Rate;
                else
                {
                    var inverse = await _db.ExchangeRates.Where(r => r.CompanyId == companyId &&
                        r.FromCurrency == functionalCurrency && r.ToCurrency == run.Currency &&
                        r.RateDate <= entryDate && r.Rate > 0).OrderByDescending(r => r.RateDate).FirstOrDefaultAsync(ct);
                    if (inverse == null) throw new InvalidOperationException($"No payroll exchange rate is configured from {run.Currency} to {functionalCurrency} for {entryDate:yyyy-MM-dd}.");
                    fxRate = 1m / inverse.Rate;
                }
            }

            var glLines = new List<GlLineRequest>
            {
                new(salariesExpenseAccountId, totalGross, 0, run.Currency, fxRate, null, "Gross salaries expense"),
                new(nssaEmployerExpenseAccountId, totalNssaEmployer, 0, run.Currency, fxRate, null, "NSSA employer contribution expense"),
                new(payeControlAccountId, 0, totalPaye, run.Currency, fxRate, null, "PAYE payable to ZIMRA"),
                new(nssaControlAccountId, 0, totalNssaEmp + totalNssaEmployer, run.Currency, fxRate, null, "NSSA payable (employee + employer)"),
                new(aidsLevyControlAccountId, 0, totalAidsLevy, run.Currency, fxRate, null, "AIDS Levy payable"),
                new(netPayControlAccountId, 0, totalNet, run.Currency, fxRate, null, "Net salaries payable to employees")
            };

            var je = await _gl.PostAsync(companyId, entryDate, JournalSource.Payroll,
                $"Payroll {run.PeriodMonth}/{run.PeriodYear} {run.Currency}", $"PAYROLL-{run.PeriodYear}{run.PeriodMonth:D2}-{run.Currency}", glLines, ct);

            run.JournalEntryId = je.Id;
            run.Status = PayrollRunStatus.Posted;
            run.ApprovedBy = approvedByUserId;
            run.ApprovedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return run;
        }
    }
}
