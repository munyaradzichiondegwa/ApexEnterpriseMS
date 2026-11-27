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

                // Partial invoice realisation releases only the fulfilled portion of the encumbrance.
                var publicSector = new PublicSectorService(verify, new AuditService(verify));
                await publicSector.RealiseCommitmentAsync(companyId, commitment.Id, 30m);
                var partiallyRealised = await verify.Commitments.SingleAsync(x => x.Id == commitment.Id);
                Assert.Equal(30m, partiallyRealised.RealisedAmount);
                Assert.Equal(CommitmentStatus.Active, partiallyRealised.Status);
                var afterPartial = await publicSector.GetBudgetUtilizationAsync(companyId, fundCentreId);
                Assert.Equal(50m, afterPartial.Commitments);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    publicSector.RealiseCommitmentAsync(companyId, commitment.Id, 51m));

                await publicSector.RealiseCommitmentAsync(companyId, commitment.Id, 50m);
                var released = await verify.Commitments.SingleAsync(x => x.Id == commitment.Id);
                Assert.Equal(80m, released.RealisedAmount);
                Assert.Equal(CommitmentStatus.Released, released.Status);
                Assert.NotNull(released.ReleasedDate);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new PublicSectorService(verify, new AuditService(verify)).CommitAsync(
                        companyId, fundCentreId, validPoId, 80m, false, null, Guid.NewGuid()));
            }
        }

        [PostgresFact]
        public async Task Year_end_carry_forward_preserves_the_next_year_fund_hierarchy()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Year End MDA",
                    BaseCurrency = "USD",
                    FunctionalCurrency = "USD"
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var vote = await service.CreateFundCentreAsync(companyId, "VOTE-YE", "Vote",
                FundCentreLevel.Vote, null, 2026, 1000m, 0m);
            var program = await service.CreateFundCentreAsync(companyId, "PROG-YE", "Programme",
                FundCentreLevel.Program, vote.Id, 2026, 100m, 0m);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CloseYearEndAsync(companyId, program.Id, carryForward: true));

            var voteClose = await service.CloseYearEndAsync(companyId, vote.Id, carryForward: true);
            Assert.NotNull(voteClose.NewFundCentreId);

            var programClose = await service.CloseYearEndAsync(companyId, program.Id, carryForward: true);
            Assert.NotNull(programClose.NewFundCentreId);

            var nextVote = await ctx.FundCentres.SingleAsync(x => x.Id == voteClose.NewFundCentreId.Value);
            var nextProgram = await ctx.FundCentres.SingleAsync(x => x.Id == programClose.NewFundCentreId!.Value);
            Assert.Equal(2027, nextVote.FinancialYear);
            Assert.Equal(2027, nextProgram.FinancialYear);
            Assert.Equal(nextVote.Id, nextProgram.ParentId);
            Assert.Equal(100m, nextProgram.CarriedForwardAmount);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CloseYearEndAsync(companyId, program.Id, carryForward: true));
        }

        [PostgresFact]
        public async Task Tender_evaluation_requires_approved_vendor_valid_scoring_and_eligible_award()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var secondVendorId = Guid.NewGuid();
            var tenderId = Guid.NewGuid();
            var bidId = Guid.NewGuid();
            var secondBidId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Tender Evaluation MDA",
                    BaseCurrency = "USD",
                    FunctionalCurrency = "USD"
                });
                seed.Vendors.AddRange(
                    new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-003", Name = "Approved Supplier", IsApproved = true },
                    new Vendor { Id = secondVendorId, CompanyId = companyId, Code = "V-004", Name = "Second Approved Supplier", IsApproved = true });
                seed.TenderNotices.Add(new TenderNotice
                {
                    Id = tenderId, CompanyId = companyId, TenderNo = "TND-2026-TEST",
                    Title = "Network equipment", SubmissionDeadline = DateTime.UtcNow.AddMinutes(-5),
                    Status = TenderStatus.Open
                });
                seed.TenderBids.AddRange(
                    new TenderBid
                    {
                        Id = bidId, CompanyId = companyId, TenderNoticeId = tenderId, VendorId = vendorId,
                        PriceAmount = 1000m, SubmittedAt = DateTime.UtcNow.AddMinutes(-10), IsLateRejected = false
                    },
                    new TenderBid
                    {
                        Id = secondBidId, CompanyId = companyId, TenderNoticeId = tenderId, VendorId = secondVendorId,
                        PriceAmount = 950m, SubmittedAt = DateTime.UtcNow.AddMinutes(-9), IsLateRejected = false
                    });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new TenderService(ctx);
            var evaluation = await service.BeginEvaluationAsync(companyId, tenderId);
            Assert.Equal(TenderStatus.Evaluation, evaluation.Status);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ScoreBidAsync(companyId, bidId, 80m, 90m, 70m, 0.5m, 0.5m, 0.5m));
            var scored = await service.ScoreBidAsync(companyId, bidId, 80m, 90m, 70m, 0.4m, 0.4m, 0.2m);
            Assert.Equal(82m, scored.TotalScore);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ScoreBidAsync(companyId, secondBidId, 70m, 80m, 70m, 0.5m, 0.3m, 0.2m));
            var secondScored = await service.ScoreBidAsync(companyId, secondBidId, 70m, 80m, 70m, 0.4m, 0.4m, 0.2m);
            Assert.Equal(74m, secondScored.TotalScore);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AwardAsync(companyId, tenderId, Guid.NewGuid(), "Unsupported supplier"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AwardAsync(companyId, tenderId, vendorId, " "));

            var winningVendor = await ctx.Vendors.SingleAsync(v => v.Id == vendorId);
            winningVendor.IsApproved = false;
            await ctx.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AwardAsync(companyId, tenderId, vendorId, "Approval was revoked after bid submission."));

            winningVendor.IsApproved = true;
            await ctx.SaveChangesAsync();
            var awarded = await service.AwardAsync(companyId, tenderId, vendorId, "Highest evaluated compliant bid.");
            Assert.Equal(TenderStatus.Awarded, awarded.Status);
            Assert.Equal(vendorId, awarded.AwardedVendorId);
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
                seed.Vendors.Add(new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-002", Name = "Late Supplier", IsApproved = true });
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
