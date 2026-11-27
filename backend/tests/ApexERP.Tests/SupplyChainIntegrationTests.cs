using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests;

public sealed class SupplyChainIntegrationTests
{
    [PostgresFact]
    public async Task Goods_receipt_rejects_an_empty_receipt_before_mutating_the_database()
    {
        await using var db = await TestDb.CreateAsync();
        await using var ctx = db.NewContext(Guid.NewGuid());
        var audit = new AuditService(ctx);
        var gl = new GlPostingService(ctx, audit, new JournalControlOptions());
        var service = new SupplyChainService(ctx, new InventoryService(ctx, gl));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ReceiveGoodsAsync(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 3, 1),
                null, Array.Empty<GrnLineRequest>()));

        Assert.Empty(await ctx.GoodsReceipts.ToListAsync());
        Assert.Empty(await ctx.InventoryMovements.ToListAsync());
    }
    [PostgresFact]
    public async Task Goods_receipt_rejects_an_inventory_item_that_differs_from_the_po_line()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var poId = Guid.NewGuid();
        var poLineId = Guid.NewGuid();
        var orderedItemId = Guid.NewGuid();
        var wrongItemId = Guid.NewGuid();
        var assetAccountId = Guid.NewGuid();
        var cogsAccountId = Guid.NewGuid();

        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId, Name = "GRN control test",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            });
            seed.Vendors.Add(new Vendor
            {
                Id = vendorId, CompanyId = companyId, Code = "GRN-V1", Name = "GRN Test Vendor"
            });
            seed.GlAccounts.AddRange(
                new GlAccount
                {
                    Id = assetAccountId, CompanyId = companyId, Code = "1300",
                    Name = "Inventory", Type = GlAccountType.Asset, Currency = "USD"
                },
                new GlAccount
                {
                    Id = cogsAccountId, CompanyId = companyId, Code = "5100",
                    Name = "Cost of Sales", Type = GlAccountType.Expense, Currency = "USD"
                });
            seed.InventoryItems.Add(new InventoryItem
            {
                Id = orderedItemId, CompanyId = companyId, Sku = "ORDERED-ITEM",
                Name = "Ordered item", AssetAccountId = assetAccountId, CogsAccountId = cogsAccountId
            });
            seed.PurchaseOrders.Add(new PurchaseOrder
            {
                Id = poId, CompanyId = companyId, VendorId = vendorId, PoNo = "PO-GRN-ITEM",
                IssueDate = new DateTime(2026, 3, 1), Status = PurchaseOrderStatus.Approved,
                Total = 100m,
                Lines = new List<PurchaseOrderLine>
                {
                    new()
                    {
                        Id = poLineId, InventoryItemId = orderedItemId,
                        QuantityOrdered = 10m, QuantityReceived = 0m, UnitCost = 10m, LineTotal = 100m
                    }
                }
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var audit = new AuditService(ctx);
        var service = new SupplyChainService(ctx,
            new InventoryService(ctx, new GlPostingService(ctx, audit, new JournalControlOptions())));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReceiveGoodsAsync(companyId, poId, new DateTime(2026, 3, 2), null,
                new[] { new GrnLineRequest(poLineId, wrongItemId, 1m, 10m) }));
        Assert.Contains("does not match the purchase order line", error.Message);

        var unchangedPo = await ctx.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == poId);
        Assert.Equal(0m, unchangedPo.Lines.Single().QuantityReceived);
        Assert.Empty(await ctx.GoodsReceipts.ToListAsync());
        Assert.Empty(await ctx.InventoryMovements.ToListAsync());
    }

}
