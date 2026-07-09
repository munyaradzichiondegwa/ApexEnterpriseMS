using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record ReceiveStockRequest(Guid ItemId, Guid? WarehouseId, decimal Quantity, decimal UnitCost, DateTime Date, string? Reference, Guid? CostCentreId);
    public record IssueStockRequest(Guid ItemId, Guid? WarehouseId, decimal Quantity, DateTime Date, string? Reference, Guid? CostCentreId);

    [Route("api/v1/inventory")]
    public class InventoryController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly InventoryService _inventory;

        public InventoryController(ApexErpDbContext db, InventoryService inventory)
        {
            _db = db;
            _inventory = inventory;
        }

        [HttpGet("warehouses")]
        public async Task<IActionResult> GetWarehouses() => Ok(await _db.Warehouses.Where(w => w.CompanyId == CompanyId).ToListAsync());

        [HttpPost("warehouses")]
        public async Task<IActionResult> CreateWarehouse([FromBody] Warehouse w)
        {
            w.CompanyId = CompanyId;
            _db.Warehouses.Add(w);
            await _db.SaveChangesAsync();
            return Ok(w);
        }

        [HttpGet("items")]
        public async Task<IActionResult> GetItems() => Ok(await _db.InventoryItems.Where(i => i.CompanyId == CompanyId).ToListAsync());

        [HttpGet("items/low-stock")]
        public async Task<IActionResult> GetLowStock() =>
            Ok(await _db.InventoryItems.Where(i => i.CompanyId == CompanyId && i.IsActive && i.QuantityOnHand <= i.ReorderLevel).ToListAsync());

        [HttpPost("items")]
        public async Task<IActionResult> CreateItem([FromBody] InventoryItem item)
        {
            item.CompanyId = CompanyId;
            _db.InventoryItems.Add(item);
            await _db.SaveChangesAsync();
            return Ok(item);
        }

        [HttpGet("items/{itemId}/movements")]
        public async Task<IActionResult> GetMovements(Guid itemId) =>
            Ok(await _db.InventoryMovements.Where(m => m.CompanyId == CompanyId && m.InventoryItemId == itemId)
                .OrderByDescending(m => m.MovementDate).ToListAsync());

        [HttpPost("movements/receive")]
        public async Task<IActionResult> Receive(ReceiveStockRequest req)
        {
            try
            {
                var movement = await _inventory.ReceiveAsync(CompanyId, req.ItemId, req.WarehouseId, req.Quantity,
                    req.UnitCost, req.Date, MovementType.Adjustment, req.Reference, req.CostCentreId);
                return Ok(movement);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("movements/issue")]
        public async Task<IActionResult> Issue(IssueStockRequest req)
        {
            try
            {
                var movement = await _inventory.IssueAsync(CompanyId, req.ItemId, req.WarehouseId, req.Quantity,
                    req.Date, MovementType.Adjustment, req.Reference, req.CostCentreId);
                return Ok(movement);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
