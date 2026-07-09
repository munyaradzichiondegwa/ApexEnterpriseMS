using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreatePoRequest(Guid VendorId, DateTime IssueDate, DateTime? ExpectedDelivery, List<PoLineRequest> Lines, Guid? FundCentreId, Guid? GrantId);
    public record ReceiveGoodsRequest(DateTime ReceiptDate, Guid? WarehouseId, List<GrnLineRequest> Lines);

    [Route("api/v1/supply-chain")]
    public class SupplyChainController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly SupplyChainService _sc;
        private readonly PublicSectorService _publicSector;

        public SupplyChainController(ApexErpDbContext db, SupplyChainService sc, PublicSectorService publicSector)
        {
            _db = db;
            _sc = sc;
            _publicSector = publicSector;
        }

        [HttpGet("vendors")]
        public async Task<IActionResult> GetVendors() => Ok(await _db.Vendors.Where(v => v.CompanyId == CompanyId).ToListAsync());

        [HttpPost("vendors")]
        public async Task<IActionResult> CreateVendor([FromBody] Vendor v)
        {
            v.CompanyId = CompanyId;
            _db.Vendors.Add(v);
            await _db.SaveChangesAsync();
            return Ok(v);
        }

        [HttpGet("purchase-orders")]
        public async Task<IActionResult> GetPurchaseOrders() =>
            Ok(await _db.PurchaseOrders.Include(p => p.Lines).Where(p => p.CompanyId == CompanyId).OrderByDescending(p => p.IssueDate).ToListAsync());

        [HttpPost("purchase-orders")]
        public async Task<IActionResult> CreatePurchaseOrder(CreatePoRequest req)
        {
            var po = await _sc.CreatePurchaseOrderAsync(CompanyId, req.VendorId, req.IssueDate, req.ExpectedDelivery, req.Lines, req.FundCentreId, req.GrantId);
            return Ok(po);
        }

        /// <summary>Approving a PO that carries a FundCentreId creates the budget commitment
        /// (encumbrance) immediately -- Available Budget drops right away, before any invoice exists
        /// (PRD Module 16).</summary>
        [HttpPost("purchase-orders/{id}/approve")]
        public async Task<IActionResult> ApprovePo(Guid id, [FromQuery] bool allowBudgetOverride = false)
        {
            try
            {
                var po = await _sc.ApproveAsync(CompanyId, id, UserId);

                if (po.FundCentreId.HasValue)
                    await _publicSector.CommitAsync(CompanyId, po.FundCentreId.Value, po.Id, po.Total, allowBudgetOverride);

                return Ok(po);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("purchase-orders/{id}/receive")]
        public async Task<IActionResult> ReceiveGoods(Guid id, ReceiveGoodsRequest req)
        {
            try
            {
                var grn = await _sc.ReceiveGoodsAsync(CompanyId, id, req.ReceiptDate, req.WarehouseId, req.Lines);

                var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == CompanyId);
                if (po?.FundCentreId.HasValue == true && po.Status == Domain.Common.PurchaseOrderStatus.Fulfilled)
                    await _publicSector.ReleaseCommitmentForPoAsync(CompanyId, id);

                return Ok(grn);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("purchase-orders/{id}/cancel")]
        public async Task<IActionResult> CancelPo(Guid id)
        {
            var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == CompanyId);
            if (po == null) return NotFound();
            po.Status = Domain.Common.PurchaseOrderStatus.Cancelled;
            await _publicSector.CancelCommitmentForPoAsync(CompanyId, id);
            await _db.SaveChangesAsync();
            return Ok(po);
        }
    }
}
