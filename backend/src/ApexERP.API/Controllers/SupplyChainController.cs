using ApexERP.Application.Services;
using ApexERP.Application.Security;
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
        private readonly AuditService _audit;

        public SupplyChainController(ApexErpDbContext db, SupplyChainService sc,
            PublicSectorService publicSector, AuditService audit)
        {
            _db = db;
            _sc = sc;
            _publicSector = publicSector;
            _audit = audit;
        }

        [HttpGet("vendors")]
        public async Task<IActionResult> GetVendors() => Ok(await _db.Vendors.Where(v => v.CompanyId == CompanyId).ToListAsync());

        [HttpPost("vendors")]
        public async Task<IActionResult> CreateVendor([FromBody] Vendor v)
        {
            var today = DateTime.UtcNow.Date;
            if (v.IsBlacklisted && string.IsNullOrWhiteSpace(v.BlacklistedReason))
                return BadRequest(new { error = "A documented reason is required when blacklisting a vendor." });
            if (v.IsApproved && (!v.TaxClearanceValidTo.HasValue || v.TaxClearanceValidTo.Value.Date < today ||
                !v.RegistrationValidTo.HasValue || v.RegistrationValidTo.Value.Date < today))
                return BadRequest(new { error = "A vendor cannot be approved without current tax-clearance and company-registration documents." });
            v.CompanyId = CompanyId;
            _db.Vendors.Add(v);
            _audit.Record(CompanyId, UserId, "Vendor.Created", "Procurement", nameof(Vendor), v.Id,
                before: null, after: new { v.Code, v.Name, v.IsApproved, v.IsBlacklisted,
                    v.TaxClearanceValidTo, v.RegistrationValidTo });
            await _db.SaveChangesAsync();
            return Ok(v);
        }

        public record VendorEligibilityUpdateRequest(bool IsApproved, bool IsBlacklisted, string? BlacklistedReason,
            DateTime? TaxClearanceValidTo, DateTime? RegistrationValidTo);

        [HttpPut("vendors/{id}/eligibility")]
        public async Task<IActionResult> UpdateVendorEligibility(Guid id, VendorEligibilityUpdateRequest request)
        {
            var vendor = await _db.Vendors.FirstOrDefaultAsync(v => v.Id == id && v.CompanyId == CompanyId);
            if (vendor == null) return NotFound();
            var today = DateTime.UtcNow.Date;
            if (request.IsBlacklisted && string.IsNullOrWhiteSpace(request.BlacklistedReason))
                return BadRequest(new { error = "A documented reason is required when blacklisting a vendor." });
            if (request.IsApproved && (!request.TaxClearanceValidTo.HasValue || request.TaxClearanceValidTo.Value.Date < today ||
                !request.RegistrationValidTo.HasValue || request.RegistrationValidTo.Value.Date < today))
                return BadRequest(new { error = "A vendor cannot be approved without current tax-clearance and company-registration documents." });
            var before = new { vendor.IsApproved, vendor.IsBlacklisted, vendor.BlacklistedReason,
                vendor.TaxClearanceValidTo, vendor.RegistrationValidTo };
            vendor.IsApproved = request.IsApproved;
            vendor.IsBlacklisted = request.IsBlacklisted;
            vendor.BlacklistedReason = request.IsBlacklisted ? request.BlacklistedReason!.Trim() : null;
            vendor.TaxClearanceValidTo = request.TaxClearanceValidTo?.Date;
            vendor.RegistrationValidTo = request.RegistrationValidTo?.Date;
            _audit.Record(CompanyId, UserId, "Vendor.EligibilityUpdated", "Procurement", nameof(Vendor), vendor.Id,
                before, new { vendor.IsApproved, vendor.IsBlacklisted, vendor.BlacklistedReason,
                    vendor.TaxClearanceValidTo, vendor.RegistrationValidTo });
            await _db.SaveChangesAsync();
            return Ok(vendor);
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
        public async Task<IActionResult> ApprovePo(Guid id, [FromQuery] bool allowBudgetOverride = false,
            [FromQuery] string? overrideReason = null, CancellationToken ct = default)
        {
            if (allowBudgetOverride && !HasPermission(Permissions.PublicSectorBudgetOverride))
                return Forbid();
            if (allowBudgetOverride && string.IsNullOrWhiteSpace(overrideReason))
                return BadRequest(new { error = "An override justification is required." });

            try
            {
                var po = await _db.InTransactionAsync(async token =>
                {
                    var approved = await _sc.ApproveAsync(CompanyId, id, UserId, token);
                    if (approved.FundCentreId.HasValue)
                        await _publicSector.CommitAsync(CompanyId, approved.FundCentreId.Value, approved.Id, approved.Total,
                            allowBudgetOverride, overrideReason, UserId,
                            HttpContext.Connection.RemoteIpAddress?.ToString(), token);
                    return approved;
                }, ct);
                return Ok(po);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("purchase-orders/{id}/receive")]
        public async Task<IActionResult> ReceiveGoods(Guid id, ReceiveGoodsRequest req)
        {
            try
            {
                // A GRN confirms receipt, not the supplier's payable amount. Keep the budget
                // encumbrance active until the linked AP invoice posts and realises it against
                // the actual base-currency expense. Releasing it here would make AP realisation fail.
                var grn = await _sc.ReceiveGoodsAsync(CompanyId, id, req.ReceiptDate, req.WarehouseId, req.Lines);
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
