using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateBomLineRequest(Guid ComponentItemId, decimal QtyPerUnit, string UnitOfMeasure, decimal ScrapFactor);
    public record CreateBomRequest(Guid FinishedItemId, DateTime EffectiveDate, decimal StandardBatchQty, List<CreateBomLineRequest> Lines);
    public record CreateWorkOrderRequest(Guid BomHeaderId, decimal QtyToProduce, DateTime? ScheduledStart, DateTime? ScheduledEnd, Guid? CostCentreId);
    public record CompleteWorkOrderRequest(decimal QtyProduced, decimal LabourCost, decimal OverheadCost, DateTime Date);

    [Route("api/v1/manufacturing")]
    public class ManufacturingController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly ManufacturingService _mfg;

        public ManufacturingController(ApexErpDbContext db, ManufacturingService mfg)
        {
            _db = db;
            _mfg = mfg;
        }

        [HttpGet("boms")]
        public async Task<IActionResult> GetBoms() => Ok(await _db.BomHeaders.Include(b => b.Lines).Where(b => b.CompanyId == CompanyId).ToListAsync());

        [HttpPost("boms")]
        public async Task<IActionResult> CreateBom(CreateBomRequest req)
        {
            var bom = new BomHeader
            {
                CompanyId = CompanyId,
                FinishedItemId = req.FinishedItemId,
                EffectiveDate = req.EffectiveDate,
                StandardBatchQty = req.StandardBatchQty,
                Status = BomStatus.Draft,
                Lines = req.Lines.Select(l => new BomLine
                {
                    ComponentItemId = l.ComponentItemId,
                    QtyPerUnit = l.QtyPerUnit,
                    UnitOfMeasure = l.UnitOfMeasure,
                    ScrapFactor = l.ScrapFactor
                }).ToList()
            };
            _db.BomHeaders.Add(bom);
            await _db.SaveChangesAsync();
            return Ok(bom);
        }

        [HttpPost("boms/{id}/activate")]
        public async Task<IActionResult> ActivateBom(Guid id)
        {
            var bom = await _db.BomHeaders.FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == CompanyId);
            if (bom == null) return NotFound();
            bom.Status = BomStatus.Active;
            await _db.SaveChangesAsync();
            return Ok(bom);
        }

        [HttpGet("boms/{id}/standard-cost")]
        public async Task<IActionResult> GetStandardCost(Guid id) => Ok(new { unitCost = await _mfg.CalculateStandardUnitCostAsync(CompanyId, id) });

        [HttpGet("work-orders")]
        public async Task<IActionResult> GetWorkOrders() => Ok(await _db.WorkOrders.Where(w => w.CompanyId == CompanyId).OrderByDescending(w => w.CreatedAt).ToListAsync());

        [HttpPost("work-orders")]
        public async Task<IActionResult> CreateWorkOrder(CreateWorkOrderRequest req)
        {
            var prefix = $"WO-{DateTime.UtcNow:yyyy}-";
            var count = await _db.WorkOrders.IgnoreQueryFilters().CountAsync(w => w.CompanyId == CompanyId && w.WoNo.StartsWith(prefix));
            var wo = new WorkOrder
            {
                CompanyId = CompanyId,
                WoNo = $"{prefix}{(count + 1):D5}",
                BomHeaderId = req.BomHeaderId,
                QtyToProduce = req.QtyToProduce,
                ScheduledStart = req.ScheduledStart,
                ScheduledEnd = req.ScheduledEnd,
                CostCentreId = req.CostCentreId,
                Status = WorkOrderStatus.Draft
            };
            _db.WorkOrders.Add(wo);
            await _db.SaveChangesAsync();
            return Ok(wo);
        }

        [HttpPost("work-orders/{id}/release")]
        public async Task<IActionResult> ReleaseWorkOrder(Guid id)
        {
            try
            {
                var wo = await _mfg.ReleaseAsync(CompanyId, id, DateTime.UtcNow);
                return Ok(wo);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("work-orders/{id}/complete")]
        public async Task<IActionResult> CompleteWorkOrder(Guid id, CompleteWorkOrderRequest req)
        {
            try
            {
                var wo = await _mfg.CompleteAsync(CompanyId, id, req.QtyProduced, req.LabourCost, req.OverheadCost, req.Date);
                return Ok(wo);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
