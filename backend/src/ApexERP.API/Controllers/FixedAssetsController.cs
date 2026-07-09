using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record RegisterAssetRequest(string AssetNo, string Name, Guid CategoryId, DateTime AcquisitionDate,
        decimal AcquisitionCost, Domain.Common.DepreciationMethod Method, int UsefulLifeMonths, decimal ResidualValue, Guid? CostCentreId);
    public record DisposeAssetRequest(DateTime DisposalDate, decimal Proceeds, Guid BankOrReceivableAccountId, Guid GainLossAccountId);
    public record RunDepreciationRequest(DateTime PeriodDate);

    [Route("api/v1/fixed-assets")]
    public class FixedAssetsController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly FixedAssetService _assets;

        public FixedAssetsController(ApexErpDbContext db, FixedAssetService assets)
        {
            _db = db;
            _assets = assets;
        }

        [HttpGet("categories")]
        public async Task<IActionResult> GetCategories() => Ok(await _db.AssetCategories.Where(c => c.CompanyId == CompanyId).ToListAsync());

        [HttpPost("categories")]
        public async Task<IActionResult> CreateCategory([FromBody] AssetCategory c)
        {
            c.CompanyId = CompanyId;
            _db.AssetCategories.Add(c);
            await _db.SaveChangesAsync();
            return Ok(c);
        }

        [HttpGet("assets")]
        public async Task<IActionResult> GetAssets() => Ok(await _db.FixedAssets.Where(a => a.CompanyId == CompanyId).ToListAsync());

        [HttpPost("assets")]
        public async Task<IActionResult> RegisterAsset(RegisterAssetRequest req)
        {
            var asset = await _assets.RegisterAssetAsync(CompanyId, req.AssetNo, req.Name, req.CategoryId, req.AcquisitionDate,
                req.AcquisitionCost, req.Method, req.UsefulLifeMonths, req.ResidualValue, req.CostCentreId);
            return Ok(asset);
        }

        [HttpGet("assets/{id}/depreciation-schedule")]
        public async Task<IActionResult> GetDepreciationSchedule(Guid id) =>
            Ok(await _db.DepreciationEntries.Where(d => d.CompanyId == CompanyId && d.FixedAssetId == id)
                .OrderBy(d => d.PeriodDate).ToListAsync());

        /// <summary>Batch job: runs monthly depreciation for every active asset, grouped and posted
        /// per category as a single journal each (Hangfire scheduled job in production).</summary>
        [HttpPost("run-depreciation")]
        public async Task<IActionResult> RunDepreciation(RunDepreciationRequest req)
        {
            var entries = await _assets.RunMonthlyDepreciationAsync(CompanyId, req.PeriodDate);
            return Ok(new { count = entries.Count, totalAmount = entries.Sum(e => e.Amount) });
        }

        [HttpPost("assets/{id}/dispose")]
        public async Task<IActionResult> Dispose(Guid id, DisposeAssetRequest req)
        {
            try
            {
                var asset = await _assets.DisposeAsync(CompanyId, id, req.DisposalDate, req.Proceeds, req.BankOrReceivableAccountId, req.GainLossAccountId);
                return Ok(asset);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
