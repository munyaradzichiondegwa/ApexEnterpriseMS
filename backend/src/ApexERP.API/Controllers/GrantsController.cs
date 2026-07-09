using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateGrantRequest(string GrantNo, string Title, Guid DonorCustomerId, decimal AwardAmount, string Currency,
        DateTime StartDate, DateTime EndDate, decimal IndirectCostRatePct, string RestrictionsJson, bool EnforceRestriction);
    public record SetBudgetLineRequest(Guid GlAccountId, int Year, int? Month, decimal Amount);
    public record IndirectCostRecoveryRequest(DateTime PeriodDate, Guid IndirectCostExpenseAccountId, Guid OverheadRecoveryIncomeAccountId);
    public record CreateSubAwardRequest(string SubAwardeeName, Guid? VendorId, decimal Amount, string Currency, DateTime StartDate, DateTime EndDate, string? ReportingRequirements);
    public record RecordDisbursementRequest(decimal Amount, DateTime Date, Guid? ApPaymentId);
    public record CreateDistributionRequest(Guid BeneficiaryId, DateTime DistributionDate, string ItemsOrServices, decimal Quantity);
    public record UpdateCloseoutRequest(bool AllExpendituresPosted, bool SubAwardsClosed, bool IndirectCostCalculated,
        bool FinalReportGenerated, decimal? SurplusAmount, string? SurplusDisposition);

    [Route("api/v1/grants")]
    public class GrantsController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly GrantService _grants;

        public GrantsController(ApexErpDbContext db, GrantService grants)
        {
            _db = db;
            _grants = grants;
        }

        [HttpGet]
        public async Task<IActionResult> GetGrants() => Ok(await _db.Grants.Where(g => g.CompanyId == CompanyId).ToListAsync());

        [HttpPost]
        public async Task<IActionResult> CreateGrant(CreateGrantRequest req) =>
            Ok(await _grants.CreateGrantAsync(CompanyId, req.GrantNo, req.Title, req.DonorCustomerId, req.AwardAmount, req.Currency,
                req.StartDate, req.EndDate, req.IndirectCostRatePct, req.RestrictionsJson, req.EnforceRestriction));

        [HttpPost("{id}/budget-lines")]
        public async Task<IActionResult> SetBudgetLine(Guid id, SetBudgetLineRequest req)
        {
            try { return Ok(await _grants.SetBudgetLineAsync(CompanyId, id, req.GlAccountId, req.Year, req.Month, req.Amount)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("{id}/budget-vs-actual")]
        public async Task<IActionResult> GetBudgetVsActual(Guid id) => Ok(await _grants.GetBudgetVsActualAsync(CompanyId, id));

        [HttpPost("{id}/indirect-cost-recovery")]
        public async Task<IActionResult> RecoverIndirectCost(Guid id, IndirectCostRecoveryRequest req)
        {
            try
            {
                var je = await _grants.CalculateAndPostIndirectCostRecoveryAsync(CompanyId, id, req.PeriodDate, req.IndirectCostExpenseAccountId, req.OverheadRecoveryIncomeAccountId);
                return Ok(je);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("{id}/donor-report")]
        public async Task<IActionResult> GetDonorReport(Guid id, [FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            try { return Ok(await _grants.GenerateDonorReportAsync(CompanyId, id, from, to)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        // ---- Sub-awards ----
        [HttpGet("{id}/sub-awards")]
        public async Task<IActionResult> GetSubAwards(Guid id) => Ok(await _db.SubAwards.Where(s => s.GrantId == id && s.CompanyId == CompanyId).ToListAsync());

        [HttpPost("{id}/sub-awards")]
        public async Task<IActionResult> CreateSubAward(Guid id, CreateSubAwardRequest req) =>
            Ok(await _grants.CreateSubAwardAsync(CompanyId, id, req.SubAwardeeName, req.VendorId, req.Amount, req.Currency, req.StartDate, req.EndDate, req.ReportingRequirements));

        [HttpPost("sub-awards/{subAwardId}/disbursements")]
        public async Task<IActionResult> RecordDisbursement(Guid subAwardId, RecordDisbursementRequest req)
        {
            try { return Ok(await _grants.RecordDisbursementAsync(CompanyId, subAwardId, req.Amount, req.Date, req.ApPaymentId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---- Beneficiaries & Distributions ----
        [HttpGet("beneficiaries")]
        public async Task<IActionResult> GetBeneficiaries() => Ok(await _db.Beneficiaries.Where(b => b.CompanyId == CompanyId).ToListAsync());

        [HttpPost("beneficiaries")]
        public async Task<IActionResult> CreateBeneficiary([FromBody] Beneficiary b)
        {
            b.CompanyId = CompanyId;
            _db.Beneficiaries.Add(b);
            await _db.SaveChangesAsync();
            return Ok(b);
        }

        [HttpPost("{id}/distributions")]
        public async Task<IActionResult> CreateDistribution(Guid id, CreateDistributionRequest req)
        {
            var dist = new Distribution
            {
                CompanyId = CompanyId,
                GrantId = id,
                BeneficiaryId = req.BeneficiaryId,
                DistributionDate = req.DistributionDate,
                ItemsOrServices = req.ItemsOrServices,
                Quantity = req.Quantity
            };
            _db.Distributions.Add(dist);
            await _db.SaveChangesAsync();
            return Ok(dist);
        }

        // ---- Closeout ----
        [HttpPost("{id}/closeout/initiate")]
        public async Task<IActionResult> InitiateCloseout(Guid id) => Ok(await _grants.InitiateCloseoutAsync(CompanyId, id));

        [HttpPut("closeout/{checklistId}")]
        public async Task<IActionResult> UpdateCloseout(Guid checklistId, UpdateCloseoutRequest req)
        {
            try
            {
                var checklist = await _grants.UpdateCloseoutChecklistAsync(CompanyId, checklistId, req.AllExpendituresPosted,
                    req.SubAwardsClosed, req.IndirectCostCalculated, req.FinalReportGenerated, req.SurplusAmount, req.SurplusDisposition, UserId);
                return Ok(checklist);
            }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }
    }
}
