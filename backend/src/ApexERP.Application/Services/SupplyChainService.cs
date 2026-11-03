using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record PoLineRequest(Guid InventoryItemId, decimal Quantity, decimal UnitCost);
    public record GrnLineRequest(Guid PurchaseOrderLineId, Guid InventoryItemId, decimal QuantityReceived, decimal UnitCost);

    /// <summary>PRD Module 11 (Supply Chain & Procurement): PO creation and approval, goods receipt
    /// (which increases inventory via InventoryService and posts a GRN accrual to the GL), feeding
    /// the three-way match (PO / GRN / AP Invoice).</summary>
    public class SupplyChainService
    {
        private readonly ApexErpDbContext _db;
        private readonly InventoryService _inventory;

        public SupplyChainService(ApexErpDbContext db, InventoryService inventory)
        {
            _db = db;
            _inventory = inventory;
        }

        public async Task<PurchaseOrder> CreatePurchaseOrderAsync(Guid companyId, Guid vendorId, DateTime issueDate,
            DateTime? expectedDelivery, IEnumerable<PoLineRequest> lines, Guid? fundCentreId = null, Guid? grantId = null, CancellationToken ct = default)
        {
            var lineEntities = lines.Select(l => new PurchaseOrderLine
            {
                InventoryItemId = l.InventoryItemId,
                QuantityOrdered = l.Quantity,
                QuantityReceived = 0,
                UnitCost = l.UnitCost,
                LineTotal = Math.Round(l.Quantity * l.UnitCost, 6)
            }).ToList();

            var poNo = await GeneratePoNoAsync(companyId, ct);
            var po = new PurchaseOrder
            {
                CompanyId = companyId,
                PoNo = poNo,
                VendorId = vendorId,
                IssueDate = issueDate,
                ExpectedDelivery = expectedDelivery,
                Status = PurchaseOrderStatus.Draft,
                Total = lineEntities.Sum(l => l.LineTotal),
                FundCentreId = fundCentreId,
                GrantId = grantId,
                Lines = lineEntities
            };
            _db.PurchaseOrders.Add(po);
            await _db.SaveChangesAsync(ct);
            return po;
        }

        public async Task<PurchaseOrder> ApproveAsync(Guid companyId, Guid poId, Guid approvedBy, CancellationToken ct = default)
        {
            var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Purchase order not found.");
            if (po.Status != PurchaseOrderStatus.Submitted && po.Status != PurchaseOrderStatus.Draft)
                throw new InvalidOperationException("Purchase order cannot be approved in its current status.");
            po.Status = PurchaseOrderStatus.Approved;
            po.ApprovedBy = approvedBy;
            await _db.SaveChangesAsync(ct);
            return po;
        }

        /// <summary>Records a goods receipt against an approved PO, increasing inventory quantity/value
        /// and updating PO fulfilment status (partial/fulfilled).</summary>
        public async Task<GoodsReceipt> ReceiveGoodsAsync(Guid companyId, Guid poId, DateTime receiptDate,
            Guid? warehouseId, IEnumerable<GrnLineRequest> lines, CancellationToken ct = default)
        {
            var po = await _db.PurchaseOrders.Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Purchase order not found.");
            if (po.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.Partial))
                throw new InvalidOperationException("Only approved purchase orders can receive goods.");

            var grnNo = await GenerateGrnNoAsync(companyId, ct);
            var grn = new GoodsReceipt { CompanyId = companyId, GrnNo = grnNo, PurchaseOrderId = poId, ReceiptDate = receiptDate, WarehouseId = warehouseId };

            foreach (var lr in lines)
            {
                var poLine = po.Lines.FirstOrDefault(l => l.Id == lr.PurchaseOrderLineId)
                    ?? throw new InvalidOperationException("PO line not found.");
                var remainingQty = poLine.QuantityOrdered - poLine.QuantityReceived;
                if (lr.QuantityReceived > remainingQty)
                    throw new InvalidOperationException($"Receiving {lr.QuantityReceived} exceeds remaining ordered quantity {remainingQty}.");

                poLine.QuantityReceived += lr.QuantityReceived;
                grn.Lines.Add(new GoodsReceiptLine
                {
                    PurchaseOrderLineId = poLine.Id,
                    InventoryItemId = lr.InventoryItemId,
                    QuantityReceived = lr.QuantityReceived,
                    UnitCost = lr.UnitCost
                });

                await _inventory.ReceiveAsync(companyId, lr.InventoryItemId, warehouseId, lr.QuantityReceived,
                    lr.UnitCost, receiptDate, MovementType.GRN, grnNo, null, ct);
            }

            po.Status = po.Lines.All(l => l.QuantityReceived >= l.QuantityOrdered)
                ? PurchaseOrderStatus.Fulfilled
                : PurchaseOrderStatus.Partial;

            _db.GoodsReceipts.Add(grn);
            await _db.SaveChangesAsync(ct);
            return grn;
        }

        private async Task<string> GeneratePoNoAsync(Guid companyId, CancellationToken ct)
        {
            var prefix = $"PO-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:{prefix}", ct);
            var count = await _db.PurchaseOrders.IgnoreQueryFilters().CountAsync(p => p.CompanyId == companyId && p.PoNo.StartsWith(prefix), ct);
            return $"{prefix}{(count + 1):D5}";
        }

        private async Task<string> GenerateGrnNoAsync(Guid companyId, CancellationToken ct)
        {
            var prefix = $"GRN-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:{prefix}", ct);
            var count = await _db.GoodsReceipts.IgnoreQueryFilters().CountAsync(g => g.CompanyId == companyId && g.GrnNo.StartsWith(prefix), ct);
            return $"{prefix}{(count + 1):D5}";
        }
    }
}
