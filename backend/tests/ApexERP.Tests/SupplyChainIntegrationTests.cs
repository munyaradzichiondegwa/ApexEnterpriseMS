using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
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

    private static async Task<(Guid TenantId, Guid VendorId, Guid ItemId, Guid OtherItemId)> SeedCompanyAsync(
        TestDb db, Guid companyId)
    {
        var tenantId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var assetAccountId = Guid.NewGuid();
        var cogsAccountId = Guid.NewGuid();

        await using var seed = db.NewContext(companyId, tenantId: tenantId);
        seed.CustomerOrganisations.Add(new CustomerOrganisation
        {
            Id = tenantId, Name = "GRN test tenant", IsActive = true
        });
        seed.Companies.Add(new Company
        {
            Id = companyId, TenantId = tenantId, Name = "GRN test company",
            BaseCurrency = "USD", FunctionalCurrency = "USD"
        });
        seed.Vendors.Add(new Vendor
        {
            Id = vendorId, CompanyId = companyId,
            Code = "V-GRN", Name = "GRN Supplier", IsApproved = true, Currency = "USD"
        });
        seed.GlAccounts.AddRange(
            new GlAccount
            {
                Id = assetAccountId, CompanyId = companyId,
                Code = "1400", Name = "Inventory Asset", Type = GlAccountType.Asset, Currency = "USD"
            },
            new GlAccount
            {
                Id = cogsAccountId, CompanyId = companyId,
                Code = "5000", Name = "Inventory Clearing", Type = GlAccountType.Expense, Currency = "USD"
            });
        seed.InventoryItems.AddRange(
            new InventoryItem
            {
                Id = itemId, CompanyId = companyId,
                Sku = "GRN-001", Name = "Primary item", UnitOfMeasure = "EA",
                AssetAccountId = assetAccountId, CogsAccountId = cogsAccountId,
                ValuationMethod = ValuationMethod.FIFO, QuantityOnHand = 0m
            },
            new InventoryItem
            {
                Id = otherItemId, CompanyId = companyId,
                Sku = "GRN-002", Name = "Different item", UnitOfMeasure = "EA",
                AssetAccountId = assetAccountId, CogsAccountId = cogsAccountId,
                ValuationMethod = ValuationMethod.FIFO, QuantityOnHand = 0m
            });
        await seed.SaveChangesAsync();
        return (tenantId, vendorId, itemId, otherItemId);
    }

    private static SupplyChainService CreateSupplyChainService(ApexErpDbContext db)
    {
        var audit = new AuditService(db);
        var gl = new GlPostingService(db, audit, new JournalControlOptions());
        return new SupplyChainService(db, new InventoryService(db, gl));
    }

    private static async Task<PurchaseOrder> CreateApprovedPoAsync(
        TestDb db, Guid companyId, Guid tenantId, Guid vendorId, Guid itemId, decimal quantity)
    {
        await using var ctx = db.NewContext(companyId, tenantId: tenantId);
        var service = CreateSupplyChainService(ctx);
        var po = await service.CreatePurchaseOrderAsync(companyId, vendorId,
            new DateTime(2026, 11, 27), null,
            new[] { new PoLineRequest(itemId, quantity, 5m) });
        return await service.ApproveAsync(companyId, po.Id, Guid.NewGuid());
    }

    [PostgresFact]
    public async Task Invalid_later_GRN_line_rolls_back_earlier_inventory_and_GL_postings()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        var seeded = await SeedCompanyAsync(db, companyId);
        var po = await CreateApprovedPoAsync(db, companyId, seeded.TenantId,
            seeded.VendorId, seeded.ItemId, 10m);
        var poLineId = po.Lines.Single().Id;

        await using (var ctx = db.NewContext(companyId, tenantId: seeded.TenantId))
        {
            var service = CreateSupplyChainService(ctx);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveGoodsAsync(
                companyId, po.Id, new DateTime(2026, 11, 27), null,
                new[]
                {
                    new GrnLineRequest(poLineId, seeded.ItemId, 5m, 5m),
                    new GrnLineRequest(poLineId, seeded.OtherItemId, 1m, 5m)
                }));
        }

        await using var verify = db.NewContext(companyId, tenantId: seeded.TenantId);
        var persistedPo = await verify.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == po.Id);
        var item = await verify.InventoryItems.SingleAsync(x => x.Id == seeded.ItemId);
        Assert.Equal(PurchaseOrderStatus.Approved, persistedPo.Status);
        Assert.Equal(0m, persistedPo.Lines.Single().QuantityReceived);
        Assert.Equal(0m, item.QuantityOnHand);
        Assert.Empty(await verify.GoodsReceipts.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    [PostgresFact]
    public async Task Concurrent_GRNs_are_serialised_and_cannot_over_receive_a_PO_line()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        var seeded = await SeedCompanyAsync(db, companyId);
        var po = await CreateApprovedPoAsync(db, companyId, seeded.TenantId,
            seeded.VendorId, seeded.ItemId, 10m);
        var poLineId = po.Lines.Single().Id;

        async Task<bool> ReceiveSixAsync()
        {
            await using var ctx = db.NewContext(companyId, tenantId: seeded.TenantId);
            var service = CreateSupplyChainService(ctx);
            try
            {
                await service.ReceiveGoodsAsync(companyId, po.Id, new DateTime(2026, 11, 27), null,
                    new[] { new GrnLineRequest(poLineId, seeded.ItemId, 6m, 5m) });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(ReceiveSixAsync(), ReceiveSixAsync());
        Assert.Single(outcomes, x => x);

        await using var verify = db.NewContext(companyId, tenantId: seeded.TenantId);
        var persistedPo = await verify.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == po.Id);
        var item = await verify.InventoryItems.SingleAsync(x => x.Id == seeded.ItemId);
        Assert.Equal(6m, persistedPo.Lines.Single().QuantityReceived);
        Assert.Equal(PurchaseOrderStatus.Partial, persistedPo.Status);
        Assert.Equal(6m, item.QuantityOnHand);
        Assert.Single(await verify.GoodsReceipts.ToListAsync());
        Assert.Single(await verify.InventoryMovements.ToListAsync());
        Assert.Single(await verify.JournalEntries.ToListAsync());
    }

    [PostgresFact]
    public async Task AP_invoice_inherits_fund_centre_and_active_commitment_from_its_po()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var fundCentreId = Guid.NewGuid();
        var poId = Guid.NewGuid();
        var commitmentId = Guid.NewGuid();
        var expenseAccountId = Guid.NewGuid();

        await using (var seed = db.NewContext(companyId))
        {
            seed.Companies.Add(new Company
            {
                Id = companyId, Name = "AP commitment linkage test",
                BaseCurrency = "USD", FunctionalCurrency = "USD"
            });
            seed.Suppliers.Add(new Supplier
            {
                Id = supplierId, CompanyId = companyId, Code = "SUP-AP-01",
                Name = "AP commitment supplier", Currency = "USD"
            });
            seed.Vendors.Add(new Vendor
            {
                Id = vendorId, CompanyId = companyId, Code = "VEN-AP-01",
                Name = "AP commitment vendor"
            });
            seed.GlAccounts.Add(new GlAccount
            {
                Id = expenseAccountId, CompanyId = companyId, Code = "5200",
                Name = "Programme Expense", Type = GlAccountType.Expense, Currency = "USD"
            });
            seed.FundCentres.Add(new FundCentre
            {
                Id = fundCentreId, CompanyId = companyId, Code = "VOTE-AP",
                Name = "AP Vote", Level = FundCentreLevel.Vote,
                FinancialYear = 2026, AppropriationAmount = 1000m
            });
            seed.PurchaseOrders.Add(new PurchaseOrder
            {
                Id = poId, CompanyId = companyId, VendorId = vendorId,
                PoNo = "PO-AP-LINK", IssueDate = new DateTime(2026, 3, 1),
                Status = PurchaseOrderStatus.Approved, Total = 100m,
                FundCentreId = fundCentreId
            });
            seed.Commitments.Add(new Commitment
            {
                Id = commitmentId, CompanyId = companyId, FundCentreId = fundCentreId,
                PurchaseOrderId = poId, Amount = 100m, RealisedAmount = 0m,
                Status = CommitmentStatus.Active, CommittedDate = new DateTime(2026, 3, 1)
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(companyId);
        var audit = new AuditService(ctx);
        var gl = new GlPostingService(ctx, audit, new JournalControlOptions());
        var publicSector = new PublicSectorService(ctx, audit);
        var ap = new ApService(ctx, gl, publicSector: publicSector);

        var invoice = await ap.CreateInvoiceAsync(companyId, supplierId, new DateTime(2026, 3, 10),
            "USD", 1m,
            new[] { new ApInvoiceLineRequest("Programme delivery", 1m, 75m, null, expenseAccountId) },
            purchaseOrderId: poId);

        Assert.Equal(fundCentreId, invoice.FundCentreId);
        Assert.Equal(commitmentId, invoice.CommitmentId);
        Assert.Equal(poId, invoice.PurchaseOrderId);
    }

}
