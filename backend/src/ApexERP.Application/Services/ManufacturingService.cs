using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>PRD Module 14 (Manufacturing & Production): Bill of Materials costing, work-order
    /// release (issues components to production, consuming inventory via FIFO/AVCO), and completion
    /// (receives finished goods into inventory at accumulated actual cost).</summary>
    public class ManufacturingService
    {
        private readonly ApexErpDbContext _db;
        private readonly InventoryService _inventory;

        public ManufacturingService(ApexErpDbContext db, InventoryService inventory)
        {
            _db = db;
            _inventory = inventory;
        }

        /// <summary>Standard cost of one unit of the finished good, computed by summing
        /// componentQty * componentAverageCost across all BOM lines (scrap factor included).</summary>
        public async Task<decimal> CalculateStandardUnitCostAsync(Guid companyId, Guid bomHeaderId, CancellationToken ct = default)
        {
            var bom = await _db.BomHeaders.Include(b => b.Lines)
                .FirstOrDefaultAsync(b => b.Id == bomHeaderId && b.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("BOM not found.");

            var componentIds = bom.Lines.Select(l => l.ComponentItemId).ToList();
            var components = await _db.InventoryItems.Where(i => componentIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

            decimal totalCost = 0;
            foreach (var line in bom.Lines)
            {
                if (!components.TryGetValue(line.ComponentItemId, out var comp)) continue;
                var qtyWithScrap = line.QtyPerUnit * (1 + line.ScrapFactor);
                totalCost += qtyWithScrap * comp.AverageCost;
            }
            return Math.Round(totalCost / Math.Max(bom.StandardBatchQty, 1), 6);
        }

        /// <summary>Releases a work order: issues all BOM component quantities (scaled to QtyToProduce)
        /// from inventory, accumulating actual material cost on the work order.</summary>
        public async Task<WorkOrder> ReleaseAsync(Guid companyId, Guid workOrderId, DateTime date, CancellationToken ct = default)
        {
            var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.Id == workOrderId && w.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Work order not found.");
            if (wo.Status != WorkOrderStatus.Draft) throw new InvalidOperationException("Only draft work orders can be released.");

            var bom = await _db.BomHeaders.Include(b => b.Lines)
                .FirstOrDefaultAsync(b => b.Id == wo.BomHeaderId, ct)
                ?? throw new InvalidOperationException("BOM not found.");

            decimal totalMaterialCost = 0;
            var scaleFactor = wo.QtyToProduce / Math.Max(bom.StandardBatchQty, 1);

            foreach (var line in bom.Lines)
            {
                var requiredQty = Math.Round(line.QtyPerUnit * (1 + line.ScrapFactor) * scaleFactor, 6);
                var movement = await _inventory.IssueAsync(companyId, line.ComponentItemId, null, requiredQty, date,
                    MovementType.ProductionIssue, wo.WoNo, wo.CostCentreId, ct);
                totalMaterialCost += movement.Quantity == 0 ? 0 : Math.Abs(movement.Quantity) * movement.UnitCost;
            }

            wo.TotalMaterialCost = totalMaterialCost;
            wo.Status = WorkOrderStatus.InProgress;
            wo.ActualStart = date;
            await _db.SaveChangesAsync(ct);
            return wo;
        }

        /// <summary>Completes a work order: receives the produced finished-good quantity into
        /// inventory at total actual cost (material + labour + overhead) / qty produced.</summary>
        public async Task<WorkOrder> CompleteAsync(Guid companyId, Guid workOrderId, decimal qtyProduced,
            decimal labourCost, decimal overheadCost, DateTime date, CancellationToken ct = default)
        {
            var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.Id == workOrderId && w.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Work order not found.");
            if (wo.Status != WorkOrderStatus.InProgress) throw new InvalidOperationException("Only in-progress work orders can be completed.");

            var bom = await _db.BomHeaders.FirstOrDefaultAsync(b => b.Id == wo.BomHeaderId, ct)
                ?? throw new InvalidOperationException("BOM not found.");

            wo.TotalLabourCost = labourCost;
            wo.TotalOverheadCost = overheadCost;
            var totalCost = wo.TotalMaterialCost + labourCost + overheadCost;
            var unitCost = qtyProduced == 0 ? 0 : Math.Round(totalCost / qtyProduced, 6);

            await _inventory.ReceiveAsync(companyId, bom.FinishedItemId, null, qtyProduced, unitCost, date,
                MovementType.ProductionReceipt, wo.WoNo, wo.CostCentreId, ct);

            wo.QtyProduced = qtyProduced;
            wo.Status = WorkOrderStatus.Completed;
            wo.ActualEnd = date;
            await _db.SaveChangesAsync(ct);
            return wo;
        }
    }
}
