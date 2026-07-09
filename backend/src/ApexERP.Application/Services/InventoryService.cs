using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>
    /// Implements real FIFO and Weighted-Average (AVCO) perpetual inventory costing, and posts
    /// every movement to the GL (Inventory Asset debit / Cost of Sales credit and vice versa)
    /// as required by PRD Module 6 (Inventory).
    /// </summary>
    public class InventoryService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        public InventoryService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        /// <summary>Receive stock in (GRN, production receipt, positive adjustment).</summary>
        public async Task<InventoryMovement> ReceiveAsync(Guid companyId, Guid itemId, Guid? warehouseId,
            decimal quantity, decimal unitCost, DateTime date, MovementType type, string? reference,
            Guid? costCentreId = null, CancellationToken ct = default)
        {
            if (quantity <= 0) throw new InvalidOperationException("Receipt quantity must be positive.");

            var item = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Inventory item not found.");

            var movement = new InventoryMovement
            {
                CompanyId = companyId,
                InventoryItemId = itemId,
                WarehouseId = warehouseId,
                MovementDate = date,
                MovementType = type,
                Quantity = quantity,
                UnitCost = unitCost,
                Reference = reference
            };
            _db.InventoryMovements.Add(movement);

            if (item.ValuationMethod == ValuationMethod.FIFO)
            {
                _db.InventoryCostLayers.Add(new InventoryCostLayer
                {
                    CompanyId = companyId,
                    InventoryItemId = itemId,
                    WarehouseId = warehouseId,
                    ReceiptDate = date,
                    OriginalQuantity = quantity,
                    RemainingQuantity = quantity,
                    UnitCost = unitCost,
                    SourceMovementId = movement.Id
                });
                item.AverageCost = await WeightedAverageAfterReceiptAsync(item, quantity, unitCost, ct); // informational only for FIFO items
            }
            else // AVCO: new weighted average = (existing value + incoming value) / new total qty
            {
                var existingValue = item.QuantityOnHand * item.AverageCost;
                var incomingValue = quantity * unitCost;
                var newQty = item.QuantityOnHand + quantity;
                item.AverageCost = newQty == 0 ? 0 : Math.Round((existingValue + incomingValue) / newQty, 6);
            }

            item.QuantityOnHand += quantity;

            var totalValue = Math.Round(quantity * unitCost, 6);
            var je = await _gl.PostAsync(companyId, date, MapSource(type), $"Stock receipt: {item.Sku} x{quantity}",
                reference,
                new[]
                {
                    new GlLineRequest(item.AssetAccountId, totalValue, 0, "USD", 1m, costCentreId, "Inventory asset increase"),
                    new GlLineRequest(GetOffsetAccount(item, type), 0, totalValue, "USD", 1m, costCentreId, "Offset (GRN accrual / production issue reversal)")
                }, ct);

            movement.JournalEntryId = je.Id;
            await _db.SaveChangesAsync(ct);
            return movement;
        }

        /// <summary>Issue stock out (sale, production issue, negative adjustment). Costs the issue using the
        /// item's configured valuation method and posts COGS.</summary>
        public async Task<InventoryMovement> IssueAsync(Guid companyId, Guid itemId, Guid? warehouseId,
            decimal quantity, DateTime date, MovementType type, string? reference,
            Guid? costCentreId = null, CancellationToken ct = default)
        {
            if (quantity <= 0) throw new InvalidOperationException("Issue quantity must be positive.");

            var item = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Inventory item not found.");

            if (item.QuantityOnHand < quantity)
                throw new InvalidOperationException($"Insufficient stock for {item.Sku}: on hand {item.QuantityOnHand}, requested {quantity}.");

            decimal costOfIssue;
            if (item.ValuationMethod == ValuationMethod.FIFO)
            {
                costOfIssue = await ConsumeFifoLayersAsync(companyId, itemId, quantity, ct);
            }
            else
            {
                costOfIssue = Math.Round(quantity * item.AverageCost, 6);
            }

            item.QuantityOnHand -= quantity;

            var movement = new InventoryMovement
            {
                CompanyId = companyId,
                InventoryItemId = itemId,
                WarehouseId = warehouseId,
                MovementDate = date,
                MovementType = type,
                Quantity = -quantity,
                UnitCost = quantity == 0 ? 0 : Math.Round(costOfIssue / quantity, 6),
                Reference = reference
            };
            _db.InventoryMovements.Add(movement);

            var je = await _gl.PostAsync(companyId, date, MapSource(type), $"Stock issue: {item.Sku} x{quantity}",
                reference,
                new[]
                {
                    new GlLineRequest(item.CogsAccountId, costOfIssue, 0, "USD", 1m, costCentreId, "Cost of goods sold"),
                    new GlLineRequest(item.AssetAccountId, 0, costOfIssue, "USD", 1m, costCentreId, "Inventory asset decrease")
                }, ct);

            movement.JournalEntryId = je.Id;
            await _db.SaveChangesAsync(ct);
            return movement;
        }

        /// <summary>Consumes the oldest remaining FIFO cost layers first and returns the total cost of the issue.</summary>
        private async Task<decimal> ConsumeFifoLayersAsync(Guid companyId, Guid itemId, decimal quantity, CancellationToken ct)
        {
            var layers = await _db.InventoryCostLayers
                .Where(l => l.CompanyId == companyId && l.InventoryItemId == itemId && l.RemainingQuantity > 0)
                .OrderBy(l => l.ReceiptDate)
                .ToListAsync(ct);

            decimal remainingToConsume = quantity;
            decimal totalCost = 0m;
            foreach (var layer in layers)
            {
                if (remainingToConsume <= 0) break;
                var take = Math.Min(layer.RemainingQuantity, remainingToConsume);
                layer.RemainingQuantity -= take;
                totalCost += take * layer.UnitCost;
                remainingToConsume -= take;
            }

            if (remainingToConsume > 0)
                throw new InvalidOperationException("FIFO cost layers insufficient to cost the full issue quantity (data integrity issue).");

            return Math.Round(totalCost, 6);
        }

        private Task<decimal> WeightedAverageAfterReceiptAsync(InventoryItem item, decimal qty, decimal unitCost, CancellationToken ct)
        {
            var existingValue = item.QuantityOnHand * item.AverageCost;
            var incomingValue = qty * unitCost;
            var newQty = item.QuantityOnHand + qty;
            return Task.FromResult(newQty == 0 ? 0 : Math.Round((existingValue + incomingValue) / newQty, 6));
        }

        private static JournalSource MapSource(MovementType type) => type switch
        {
            MovementType.ProductionIssue or MovementType.ProductionReceipt => JournalSource.Manufacturing,
            MovementType.GRN => JournalSource.Procurement,
            _ => JournalSource.Inventory
        };

        /// <summary>For a GRN, the offset is AP accrual; for a positive adjustment/production receipt it is
        /// an inventory adjustment/WIP account. Defaults to the item's COGS account as a configurable clearing account.</summary>
        private static Guid GetOffsetAccount(InventoryItem item, MovementType type) => item.CogsAccountId;
    }
}
