using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests
{
    /// <summary>
    /// Real-PostgreSQL acceptance tests for the MDA/SOE fund hierarchy and commitment-control
    /// workflows. These tests deliberately exercise the database-backed service and transaction
    /// boundaries rather than only the FinancialIdentities arithmetic helper.
    /// </summary>
    public sealed class PublicSectorIntegrationTests
    {
        [PostgresFact]
        public async Task Fund_hierarchy_enforces_vote_program_subprogram_and_financial_year()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "MDA Test",
                    BaseCurrency = "USD",
                    FunctionalCurrency = "USD"
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var vote = await service.CreateFundCentreAsync(companyId, "VOTE-01", "Ministry Vote",
                FundCentreLevel.Vote, null, 2026, 1000m, 0m);
            var program = await service.CreateFundCentreAsync(companyId, "PROG-01", "Primary Programme",
                FundCentreLevel.Program, vote.Id, 2026, 0m, 0m);
            var subProgram = await service.CreateFundCentreAsync(companyId, "SUB-01", "Primary Sub-Programme",
                FundCentreLevel.SubProgram, program.Id, 2026, 0m, 0m);

            Assert.Equal(FundCentreLevel.Vote, vote.Level);
            Assert.Equal(FundCentreLevel.Program, program.Level);
            Assert.Equal(FundCentreLevel.SubProgram, subProgram.Level);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateFundCentreAsync(companyId, "BAD-ROOT", "Invalid root",
                    FundCentreLevel.Program, null, 2026, 0m, 0m));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateFundCentreAsync(companyId, "BAD-SKIP", "Skipped level",
                    FundCentreLevel.SubProgram, vote.Id, 2026, 0m, 0m));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateFundCentreAsync(companyId, "BAD-YEAR", "Wrong financial year",
                    FundCentreLevel.Program, vote.Id, 2025, 0m, 0m));
        }

        [PostgresFact]
        public async Task Purchase_order_commitment_is_atomic_and_enforces_available_budget()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var overBudgetPoId = Guid.NewGuid();
            var validPoId = Guid.NewGuid();

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Public Entity",
                    BaseCurrency = "USD",
                    FunctionalCurrency = "USD"
                });
                seed.Vendors.Add(new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-001", Name = "Test Supplier" });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId,
                    CompanyId = companyId,
                    Code = "VOTE-100",
                    Name = "Operating Vote",
                    Level = FundCentreLevel.Vote,
                    FinancialYear = 2026,
                    AppropriationAmount = 100m
                });
                seed.PurchaseOrders.AddRange(
                    new PurchaseOrder
                    {
                        Id = overBudgetPoId,
                        CompanyId = companyId,
                        VendorId = vendorId,
                        PoNo = "PO-OVER",
                        IssueDate = new DateTime(2026, 1, 1),
                        Status = PurchaseOrderStatus.Draft,
                        Total = 125m,
                        FundCentreId = fundCentreId
                    },
                    new PurchaseOrder
                    {
                        Id = validPoId,
                        CompanyId = companyId,
                        VendorId = vendorId,
                        PoNo = "PO-VALID",
                        IssueDate = new DateTime(2026, 1, 2),
                        Status = PurchaseOrderStatus.Draft,
                        Total = 80m,
                        FundCentreId = fundCentreId
                    });
                await seed.SaveChangesAsync();
            }

            await using (var ctx = db.NewContext(companyId))
            {
                var service = new PublicSectorService(ctx, new AuditService(ctx));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ctx.InTransactionAsync(async token =>
                    {
                        var po = await ctx.PurchaseOrders.SingleAsync(x => x.Id == overBudgetPoId, token);
                        po.Status = PurchaseOrderStatus.Approved;
                        await ctx.SaveChangesAsync(token);
                        await service.CommitAsync(companyId, fundCentreId, overBudgetPoId, 125m,
                            false, null, Guid.NewGuid(), null, token);
                        return true;
                    }));
            }

            await using (var verifyRollback = db.NewContext(companyId))
            {
                Assert.Equal(PurchaseOrderStatus.Draft,
                    (await verifyRollback.PurchaseOrders.SingleAsync(x => x.Id == overBudgetPoId)).Status);
                Assert.Empty(await verifyRollback.Commitments.Where(x => x.PurchaseOrderId == overBudgetPoId).ToListAsync());
            }

            await using (var ctx = db.NewContext(companyId))
            {
                var service = new PublicSectorService(ctx, new AuditService(ctx));
                await ctx.InTransactionAsync(async token =>
                {
                    var po = await ctx.PurchaseOrders.SingleAsync(x => x.Id == validPoId, token);
                    po.Status = PurchaseOrderStatus.Approved;
                    await ctx.SaveChangesAsync(token);
                    await service.CommitAsync(companyId, fundCentreId, validPoId, 80m,
                        false, null, Guid.NewGuid(), null, token);
                    return true;
                });
            }

            await using (var verify = db.NewContext(companyId))
            {
                var commitment = await verify.Commitments.SingleAsync(x => x.PurchaseOrderId == validPoId);
                Assert.Equal(80m, commitment.Amount);
                Assert.Equal(CommitmentStatus.Active, commitment.Status);

                var utilisation = await new PublicSectorService(verify, new AuditService(verify))
                    .GetBudgetUtilizationAsync(companyId, fundCentreId);
                Assert.Equal(100m, utilisation.Appropriation);
                Assert.Equal(80m, utilisation.Commitments);
                Assert.Equal(0m, utilisation.Actuals);
                Assert.Equal(20m, utilisation.Available);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new PublicSectorService(verify, new AuditService(verify)).CommitAsync(
                        companyId, fundCentreId, validPoId, 80m, false, null, Guid.NewGuid()));
            }
        }

        [PostgresFact]
        public async Task Tender_service_rejects_late_bids_from_evaluation()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Tendering MDA",
                    BaseCurrency = "USD",
                    FunctionalCurrency = "USD"
                });
                seed.Vendors.Add(new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-002", Name = "Late Supplier" });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var tenderService = new TenderService(ctx);
            var tender = await tenderService.CreateTenderAsync(companyId, "Office supplies",
                "Stationery and office consumables", DateTime.UtcNow.AddMinutes(-1));
            var bid = await tenderService.SubmitBidAsync(companyId, tender.Id, vendorId, 500m);

            Assert.True(bid.IsLateRejected);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tenderService.ScoreBidAsync(companyId, bid.Id, 80m, 90m, 70m, 0.4m, 0.4m, 0.2m));
        }
    }
}
