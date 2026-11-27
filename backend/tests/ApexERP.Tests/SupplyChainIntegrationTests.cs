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
}
