using ApexERP.API.Controllers;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

            var yearEndAudit = await ctx.AuditLogs.Where(x =>
                x.Action == "PublicSectorYearEnd.Closed" && x.EntityId == vote.Id).ToListAsync();
            Assert.Single(yearEndAudit);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CloseYearEndAsync(companyId, program.Id, carryForward: true));
        }

        [PostgresFact]
        public async Task Public_sector_budget_override_requires_an_independent_approval_chain()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var purchaseOrderId = Guid.NewGuid();
            var approverRoleId = Guid.NewGuid();
            var submitterId = Guid.NewGuid();
            var approverId = Guid.NewGuid();

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Budget Override MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD", IsPublicSectorEntity = true
                });
                seed.Vendors.Add(new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-OVR", Name = "Override Vendor", IsApproved = true });
                seed.Roles.Add(new Role { Id = approverRoleId, CompanyId = companyId, Name = "Budget Approver", PermissionsJson = "[]" });
                seed.Users.AddRange(
                    new User { Id = submitterId, CompanyId = companyId, Email = "budget.submitter@test.local", PasswordHash = "test-hash", RoleId = approverRoleId, IsActive = true },
                    new User { Id = approverId, CompanyId = companyId, Email = "budget.approver@test.local", PasswordHash = "test-hash", RoleId = approverRoleId, IsActive = true });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-OVR", Name = "Operating Vote",
                    Level = FundCentreLevel.Vote, FinancialYear = 2026, AppropriationAmount = 100m
                });
                seed.PurchaseOrders.Add(new PurchaseOrder
                {
                    Id = purchaseOrderId, CompanyId = companyId, VendorId = vendorId, PoNo = "PO-OVERRIDE",
                    IssueDate = new DateTime(2026, 1, 1), Status = PurchaseOrderStatus.Approved,
                    Total = 125m, FundCentreId = fundCentreId
                });
                seed.ApprovalChains.Add(new ApprovalChain
                {
                    CompanyId = companyId, Name = "Budget override approval",
                    EntityType = "PublicSectorBudgetOverride", AmountThreshold = 0m, FundCentreId = fundCentreId,
                    Tiers = new List<ApprovalChainTier> { new() { TierOrder = 1, ApproverRoleId = approverRoleId, SlaHours = 24 } }
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CommitAsync(companyId, fundCentreId, purchaseOrderId, 125m, true,
                    "Emergency appropriation", submitterId));

            var workflows = new ApprovalChainService(ctx);
            var request = await workflows.SubmitForApprovalAsync(companyId, "PublicSectorBudgetOverride",
                purchaseOrderId, "PurchaseOrder", submitterId, 125m, fundCentreId, "Emergency appropriation");
            await workflows.DecideAsync(companyId, request.Id, approverId, true, "Reviewed and approved.");
            var persistedRequest = await ctx.WorkflowRequests.SingleAsync(w => w.Id == request.Id);
            Assert.Equal(125m, persistedRequest.Amount);
            Assert.Equal(fundCentreId, persistedRequest.FundCentreId);
            Assert.Equal(WorkflowStatus.Approved, persistedRequest.Status);
            Assert.Single(await ctx.WorkflowApprovals.Where(a => a.WorkflowRequestId == request.Id).ToListAsync());

            var commitment = await service.CommitAsync(companyId, fundCentreId, purchaseOrderId, 125m,
                true, "Emergency appropriation", submitterId);
            Assert.Equal(125m, commitment.Amount);
            Assert.Equal(CommitmentStatus.Active, commitment.Status);
        }

        [PostgresFact]
        public async Task Tender_evaluation_requires_approved_vendor_valid_scoring_and_eligible_award()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var secondVendorId = Guid.NewGuid();
            var blacklistedVendorId = Guid.NewGuid();
            var expiredVendorId = Guid.NewGuid();
            var tenderId = Guid.NewGuid();
            var bidId = Guid.NewGuid();
            var secondBidId = Guid.NewGuid();
            var approverRoleId = Guid.NewGuid();
            var submitterId = Guid.NewGuid();
            var approverId = Guid.NewGuid();
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
                    new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-003", Name = "Approved Supplier", IsApproved = true, TaxClearanceValidTo = DateTime.UtcNow.AddDays(30), RegistrationValidTo = DateTime.UtcNow.AddDays(30) },
                    new Vendor { Id = secondVendorId, CompanyId = companyId, Code = "V-004", Name = "Second Approved Supplier", IsApproved = true, TaxClearanceValidTo = DateTime.UtcNow.AddDays(30), RegistrationValidTo = DateTime.UtcNow.AddDays(30) },
                    new Vendor { Id = blacklistedVendorId, CompanyId = companyId, Code = "V-005", Name = "Blacklisted Supplier", IsApproved = true, IsBlacklisted = true, BlacklistedReason = "Test blacklist", TaxClearanceValidTo = DateTime.UtcNow.AddDays(30), RegistrationValidTo = DateTime.UtcNow.AddDays(30) },
                    new Vendor { Id = expiredVendorId, CompanyId = companyId, Code = "V-006", Name = "Expired Supplier", IsApproved = true, TaxClearanceValidTo = DateTime.UtcNow.AddDays(-1), RegistrationValidTo = DateTime.UtcNow.AddDays(30) });
                seed.Roles.Add(new Role { Id = approverRoleId, CompanyId = companyId, Name = "Tender Approver", PermissionsJson = "[]" });
                seed.Users.AddRange(
                    new User { Id = submitterId, CompanyId = companyId, Email = "submitter@test.local", PasswordHash = "test-hash", RoleId = approverRoleId, IsActive = true },
                    new User { Id = approverId, CompanyId = companyId, Email = "approver@test.local", PasswordHash = "test-hash", RoleId = approverRoleId, IsActive = true });
                seed.ApprovalChains.Add(new ApprovalChain
                {
                    CompanyId = companyId, Name = "Tender award approval", EntityType = "TenderAward", AmountThreshold = 0m,
                    Tiers = new List<ApprovalChainTier> { new() { TierOrder = 1, ApproverRoleId = approverRoleId, SlaHours = 24 } }
                });
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
            var service = new TenderService(ctx, new ApprovalChainService(ctx));
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
            var approvalService = new ApprovalChainService(ctx);
            var approvalRequest = await service.SubmitAwardForApprovalAsync(companyId, tenderId, vendorId, submitterId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AwardAsync(companyId, tenderId, vendorId, "Award must not bypass approval."));
            await approvalService.DecideAsync(companyId, approvalRequest.Id, approverId, true, "Reviewed evaluation matrix and eligibility.");
            var awarded = await service.AwardAsync(companyId, tenderId, vendorId, "Highest evaluated compliant bid.");
            Assert.Equal(TenderStatus.Awarded, awarded.Status);
            Assert.Equal(vendorId, awarded.AwardedVendorId);

            var tenderAudit = await ctx.AuditLogs.Where(x => x.CompanyId == companyId &&
                x.EntityId == tenderId && x.Action.StartsWith("Tender.")).Select(x => x.Action).ToListAsync();
            Assert.Contains("Tender.EvaluationStarted", tenderAudit);
            Assert.Contains("Tender.Awarded", tenderAudit);
            var bidAudit = await ctx.AuditLogs.Where(x => x.CompanyId == companyId &&
                (x.EntityId == bidId || x.EntityId == secondBidId) && x.Action == "Tender.BidScored")
                .ToListAsync();
            Assert.Equal(2, bidAudit.Count);
            Assert.All(bidAudit, entry =>
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.BeforeValueJson));
                Assert.False(string.IsNullOrWhiteSpace(entry.AfterValueJson));
            });
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
                seed.Vendors.Add(new Vendor { Id = vendorId, CompanyId = companyId, Code = "V-002", Name = "Late Supplier", IsApproved = true, TaxClearanceValidTo = DateTime.UtcNow.AddDays(30), RegistrationValidTo = DateTime.UtcNow.AddDays(30) });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var tenderService = new TenderService(ctx, new ApprovalChainService(ctx));
            var tender = await tenderService.CreateTenderAsync(companyId, "Office supplies",
                "Stationery and office consumables", DateTime.UtcNow.AddMinutes(-1));
            var bid = await tenderService.SubmitBidAsync(companyId, tender.Id, vendorId, 500m);

            Assert.True(bid.IsLateRejected);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tenderService.ScoreBidAsync(companyId, bid.Id, 80m, 90m, 70m, 0.4m, 0.4m, 0.2m));
            var bidSubmissionAudit = await ctx.AuditLogs.Where(x => x.CompanyId == companyId &&
                x.EntityId == bid.Id && x.Action == "Tender.BidSubmitted").SingleAsync();
            Assert.Contains("true", bidSubmissionAudit.AfterValueJson!, StringComparison.OrdinalIgnoreCase);
        }

        [PostgresFact]
        public async Task Expenditure_report_separates_encumbrances_from_realised_commitments_and_resolves_hierarchy()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var voteId = Guid.NewGuid();
            var programId = Guid.NewGuid();
            var purchaseOrderId = Guid.NewGuid();
            var expenseAccountId = Guid.NewGuid();
            var incomeAccountId = Guid.NewGuid();
            var cashAccountId = Guid.NewGuid();
            var periodId = Guid.NewGuid();

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Expenditure Report MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.Vendors.Add(new Vendor
                {
                    Id = vendorId, CompanyId = companyId, Code = "V-REPORT", Name = "Report Supplier"
                });
                seed.FundCentres.AddRange(
                    new FundCentre
                    {
                        Id = voteId, CompanyId = companyId, Code = "VOTE-R", Name = "Education Vote",
                        Level = FundCentreLevel.Vote, FinancialYear = 2026, AppropriationAmount = 500m
                    },
                    new FundCentre
                    {
                        Id = programId, CompanyId = companyId, Code = "PROG-R", Name = "School Programme",
                        Level = FundCentreLevel.Program, ParentId = voteId, FinancialYear = 2026,
                        AppropriationAmount = 100m
                    });
                seed.PurchaseOrders.Add(new PurchaseOrder
                {
                    Id = purchaseOrderId, CompanyId = companyId, VendorId = vendorId,
                    PoNo = "PO-REPORT", IssueDate = new DateTime(2026, 2, 1),
                    Status = PurchaseOrderStatus.Approved, Total = 60m, FundCentreId = programId
                });
                seed.GlAccounts.AddRange(
                    new GlAccount
                    {
                        Id = expenseAccountId, CompanyId = companyId, Code = "5000",
                        Name = "School Supplies", Type = GlAccountType.Expense, Currency = "USD"
                    },
                    new GlAccount
                    {
                        Id = incomeAccountId, CompanyId = companyId, Code = "4000",
                        Name = "Programme Income", Type = GlAccountType.Income, Currency = "USD"
                    },
                    new GlAccount
                    {
                        Id = cashAccountId, CompanyId = companyId, Code = "1000",
                        Name = "Cash", Type = GlAccountType.Asset, Currency = "USD"
                    });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 3,
                    StartDate = new DateTime(2026, 3, 1), EndDate = new DateTime(2026, 4, 1),
                    Status = PeriodStatus.Open
                });
                var actualJournal = new JournalEntry
                {
                    CompanyId = companyId, EntryNo = "JE-REPORT-ACTUAL",
                    Description = "Posted school supplies expense", EntryDate = new DateTime(2026, 3, 15),
                    PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                };
                seed.JournalEntries.Add(actualJournal);
                seed.JournalLines.AddRange(
                    new JournalLine
                    {
                        JournalEntryId = actualJournal.Id, GlAccountId = expenseAccountId,
                        Debit = 15m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                        BaseDebit = 15m, BaseCredit = 0m, FundCentreId = programId
                    },
                    // A tagged income line must not reduce the Vote/Programme expenditure actual.
                    new JournalLine
                    {
                        JournalEntryId = actualJournal.Id, GlAccountId = incomeAccountId,
                        Debit = 0m, Credit = 25m, Currency = "USD", ExchangeRate = 1m,
                        BaseDebit = 0m, BaseCredit = 25m, FundCentreId = programId
                    },
                    new JournalLine
                    {
                        JournalEntryId = actualJournal.Id, GlAccountId = cashAccountId,
                        Debit = 10m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                        BaseDebit = 10m, BaseCredit = 0m
                    });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var commitment = await service.CommitAsync(companyId, programId, purchaseOrderId,
                60m, false, null, Guid.NewGuid());
            await service.RealiseCommitmentAsync(companyId, commitment.Id, 20m);

            var report = await service.GetExpenditureReportAsync(companyId, 2026);
            var vote = Assert.Single(report, x => x.FundCentreId == voteId);
            var program = Assert.Single(report, x => x.FundCentreId == programId);

            Assert.Equal(FundCentreLevel.Vote, vote.Level);
            Assert.Null(vote.ParentCode);
            Assert.Equal("VOTE-R", program.ParentCode);
            Assert.Equal("Education Vote", program.ParentName);
            Assert.Equal(100m, program.TotalBudget);
            Assert.Equal(60m, program.CommittedToDate);
            Assert.Equal(20m, program.RealisedAgainstCommitments);
            Assert.Equal(40m, program.OutstandingCommitments);
            Assert.Equal(15m, program.PostedActuals);
            Assert.Equal(45m, program.AvailableBudget);
            Assert.Equal(85m, program.ActualVariance);
            Assert.Equal(85m, program.ActualVariancePercent);
            var utilisation = await service.GetBudgetUtilizationAsync(companyId, programId);
            Assert.Equal(15m, utilisation.Actuals);
            Assert.Equal(45m, utilisation.Available);
        }

        [PostgresFact]
        public async Task Ipsas24_budget_versions_are_isolated_by_company()
        {
            await using var db = await TestDb.CreateAsync();
            var companyA = Guid.NewGuid();
            var companyB = Guid.NewGuid();
            await using (var seed = db.NewContext(companyA))
            {
                seed.Companies.AddRange(
                    new Company { Id = companyA, Name = "MDA A", BaseCurrency = "USD", FunctionalCurrency = "USD" },
                    new Company { Id = companyB, Name = "MDA B", BaseCurrency = "USD", FunctionalCurrency = "USD" });
                seed.PublicSectorBudgetVersions.AddRange(
                    new PublicSectorBudgetVersion
                    {
                        CompanyId = companyA, FinancialYear = 2026, VersionNumber = 1,
                        Kind = PublicSectorBudgetVersionKind.Original, Status = PublicSectorBudgetVersionStatus.Approved,
                        Currency = "USD", AccountingBasis = AccountingBasis.Accrual,
                        ClassificationBasis = "A", EntityScope = "MDA A",
                        PeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        PeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        AuthorityReference = "A-APPROPRIATION"
                    },
                    new PublicSectorBudgetVersion
                    {
                        CompanyId = companyB, FinancialYear = 2026, VersionNumber = 1,
                        Kind = PublicSectorBudgetVersionKind.Original, Status = PublicSectorBudgetVersionStatus.Approved,
                        Currency = "USD", AccountingBasis = AccountingBasis.Accrual,
                        ClassificationBasis = "B", EntityScope = "MDA B",
                        PeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        PeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        AuthorityReference = "B-APPROPRIATION"
                    });
                await seed.SaveChangesAsync();
            }

            await using var companyAContext = db.NewContext(companyA);
            var visible = await companyAContext.PublicSectorBudgetVersions.ToListAsync();
            var onlyVersion = Assert.Single(visible);
            Assert.Equal(companyA, onlyVersion.CompanyId);
            Assert.Equal("A-APPROPRIATION", onlyVersion.AuthorityReference);
        }

        [PostgresFact]
        public async Task Ipsas24_comparison_preserves_original_final_and_comparable_actuals_including_added_lines()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var expenseId = Guid.NewGuid();
            var incomeId = Guid.NewGuid();
            var addedExpenseId = Guid.NewGuid();
            var removedExpenseId = Guid.NewGuid();
            var periodId = Guid.NewGuid();

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "IPSAS 24 MDA", BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-IPSAS",
                    Name = "IPSAS Vote", Level = FundCentreLevel.Vote, FinancialYear = 2026,
                    AppropriationAmount = 1000m
                });
                seed.GlAccounts.AddRange(
                    new GlAccount { Id = expenseId, CompanyId = companyId, Code = "5100", Name = "Operating Expense", SubType = "EC-01", Type = GlAccountType.Expense, Currency = "USD" },
                    new GlAccount { Id = incomeId, CompanyId = companyId, Code = "4100", Name = "Service Revenue", SubType = "REV-01", Type = GlAccountType.Income, Currency = "USD" },
                    new GlAccount { Id = addedExpenseId, CompanyId = companyId, Code = "5200", Name = "New Programme Expense", SubType = "EC-02", Type = GlAccountType.Expense, Currency = "USD" },
                    new GlAccount { Id = removedExpenseId, CompanyId = companyId, Code = "5300", Name = "Removed Programme Expense", SubType = "EC-03", Type = GlAccountType.Expense, Currency = "USD" });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 1,
                    StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 2, 1),
                    Status = PeriodStatus.Open
                });
                var journal = new JournalEntry
                {
                    CompanyId = companyId, EntryNo = "JE-IPSAS24", Description = "Budget comparison actuals",
                    EntryDate = new DateTime(2026, 6, 30), PeriodId = periodId,
                    Source = JournalSource.Manual, Status = JournalStatus.Posted
                };
                seed.JournalEntries.Add(journal);
                seed.JournalLines.AddRange(
                    new JournalLine { JournalEntryId = journal.Id, GlAccountId = expenseId, Debit = 60m, Credit = 0m, BaseDebit = 60m, BaseCredit = 0m, FundCentreId = fundCentreId },
                    new JournalLine { JournalEntryId = journal.Id, GlAccountId = incomeId, Debit = 0m, Credit = 20m, BaseDebit = 0m, BaseCredit = 20m, FundCentreId = fundCentreId },
                    new JournalLine { JournalEntryId = journal.Id, GlAccountId = addedExpenseId, Debit = 10m, Credit = 0m, BaseDebit = 10m, BaseCredit = 0m, FundCentreId = fundCentreId });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Original, "USD", AccountingBasis.Accrual,
                "ZFRM Appendix 5", "IPSAS 24 MDA", periodStart, periodEnd, "Appropriation Act 2026", null,
                new[] { new PublicSectorService.BudgetVersionLineInput(fundCentreId, expenseId, null,
                    "Operating Expense", "EC-01", 100m) }, Guid.NewGuid()));

            var original = await service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Original, "USD", AccountingBasis.Accrual,
                "ZFRM Appendix 5", "IPSAS 24 MDA",
                periodStart, periodEnd, "Appropriation Act 2026", null,
                new[]
                {
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, expenseId, null, "Operating Expense", "EC-01", 100m, null, "Use of Goods and services"),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, incomeId, null, "Service Revenue", "REV-01", 50m, null, "Sales of goods and services"),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, removedExpenseId, null, "Removed Programme Expense", "EC-03", 20m, null, "Compensation of Employees")
                }, Guid.NewGuid());
            original.Status = PublicSectorBudgetVersionStatus.Approved;
            original.ApprovedBy = Guid.NewGuid();
            original.ApprovedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            var revised = await service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Revised, "USD", AccountingBasis.Accrual,
                "ZFRM Appendix 5", "IPSAS 24 MDA",
                periodStart, periodEnd, "Supplementary Appropriation 2026",
                "Supplementary funding and reprioritisation approved.",
                new[]
                {
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, expenseId, null, "Operating Expense", "EC-01", 120m, "Note 12", "Use of Goods and services"),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, incomeId, null, "Service Revenue", "REV-01", 70m, "Note 13", "Sales of goods and services"),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, addedExpenseId, null, "New Programme Expense", "EC-02", 30m, "Note 14", "Grants")
                }, Guid.NewGuid(), default, PublicSectorBudgetAmendmentNature.Mixed);
            revised.Status = PublicSectorBudgetVersionStatus.Approved;
            revised.ApprovedBy = Guid.NewGuid();
            revised.ApprovedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            var preparerId = Guid.NewGuid();
            var approverId = Guid.NewGuid();
            var reconciliation = await service.CreateBudgetReconciliationAsync(companyId, 2026,
                PublicSectorBudgetReconciliationType.Basis, "Manual cash-flow adjustment without a calculated source fingerprint",
                "Operating", 5m, -2m, 0m, "Reconciliation workpaper WP-IPSAS24-01",
                preparerId, default, 10m, -15m, budgetVersionId: revised.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.MarkBudgetReconciliationPendingAsync(companyId, reconciliation.Id, Guid.NewGuid()));

            var report = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.Equal(original.Id, report.OriginalBudgetVersionId);
            Assert.Equal(revised.Id, report.FinalBudgetVersionId);
            Assert.True(report.FinalBudgetAvailable);
            Assert.True(report.ComparableActualsAvailable, report.ComparabilityMessage);
            Assert.True(report.OriginalToFinalExplanationsComplete);
            Assert.Equal(4, report.Lines.Count);
            Assert.Equal("Vote", report.Lines[0].FundCentreLevel);
            Assert.Equal(string.Empty, report.Lines[0].CostCentreCode);
            var expense = Assert.Single(report.Lines, x => x.AccountCode == "5100");
            Assert.Equal(100m, expense.OriginalBudget);
            Assert.Equal(120m, expense.FinalBudget);
            Assert.Equal(60m, expense.ActualOnComparableBasis);
            Assert.Equal(60m, expense.Variance);
            Assert.Equal("Note 12", expense.NoteReference);
            Assert.Equal("Use of Goods and services", expense.TreasuryClassificationCode);
            Assert.Equal("Expense", expense.AccountType);
            var income = Assert.Single(report.Lines, x => x.AccountCode == "4100");
            Assert.Equal(20m, income.ActualOnComparableBasis);
            var added = Assert.Single(report.Lines, x => x.AccountCode == "5200");
            Assert.Equal(0m, added.OriginalBudget);
            Assert.Equal(30m, added.FinalBudget);
            Assert.Equal(10m, added.ActualOnComparableBasis);
            var removed = Assert.Single(report.Lines, x => x.AccountCode == "5300");
            Assert.Equal(20m, removed.OriginalBudget);
            Assert.Equal(0m, removed.FinalBudget);
            Assert.Equal(0m, removed.ActualOnComparableBasis);
            Assert.Equal(0m, removed.Variance);
            // The manually entered cash-flow row is not approved or included in the
            // statement because it lacks a system-calculated source fingerprint.
            Assert.Empty(report.Reconciliations);

            Assert.False(report.VarianceExplanationsComplete);
            var varianceExplanation = await service.CreateBudgetVarianceExplanationAsync(
                companyId, revised.Id, expense.BudgetVersionLineId,
                "Actual expenditure was below the final appropriation because two planned service contracts commenced in the next quarter.",
                "WP-IPSAS24-VARIANCE-5100", preparerId, default, "Comment 12");
            await service.SubmitBudgetVarianceExplanationAsync(companyId, varianceExplanation.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReviewBudgetVarianceExplanationAsync(companyId, varianceExplanation.Id,
                    preparerId, true, null));
            var approvedVariance = await service.ReviewBudgetVarianceExplanationAsync(
                companyId, varianceExplanation.Id, approverId, true,
                "Explanation and supporting workpaper reviewed.");
            Assert.Equal("Approved", approvedVariance.Status);
            Assert.Equal("Comment 12", approvedVariance.CommentReference);
            foreach (var remainingVariance in report.Lines.Where(x =>
                x.Variance.HasValue && x.Variance.Value != 0m &&
                x.BudgetVersionLineId != expense.BudgetVersionLineId))
            {
                var additionalExplanation = await service.CreateBudgetVarianceExplanationAsync(
                    companyId, revised.Id, remainingVariance.BudgetVersionLineId,
                    $"Actual activity differed from the final approved budget for {remainingVariance.AccountName}; the difference is supported by the period variance workpaper.",
                    $"WP-IPSAS24-VARIANCE-{remainingVariance.AccountCode}", preparerId);
                await service.SubmitBudgetVarianceExplanationAsync(companyId, additionalExplanation.Id, preparerId);
                await service.ReviewBudgetVarianceExplanationAsync(companyId, additionalExplanation.Id,
                    approverId, true, "Explanation and supporting workpaper reviewed.");
            }
            var explainedReport = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(explainedReport.VarianceExplanationsComplete);
            Assert.Contains(explainedReport.VarianceExplanations, x => x.Id == varianceExplanation.Id);

            // A later posted journal entry changes the actual variance. The earlier approved
            // explanation remains immutable but no longer satisfies the current variance.
            var laterJournal = new JournalEntry
            {
                CompanyId = companyId, EntryNo = "JE-IPSAS24-LATE-ACTUAL",
                Description = "Subsequent posted actual", EntryDate = new DateTime(2026, 7, 1),
                PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
            };
            ctx.JournalEntries.Add(laterJournal);
            ctx.JournalLines.Add(new JournalLine
            {
                JournalEntryId = laterJournal.Id, GlAccountId = expenseId, FundCentreId = fundCentreId,
                Debit = 10m, Credit = 0m, BaseDebit = 10m, BaseCredit = 0m
            });
            await ctx.SaveChangesAsync();
            var changedVarianceReport = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.Equal(50m, changedVarianceReport.Lines.Single(x => x.AccountCode == "5100").Variance);
            Assert.False(changedVarianceReport.VarianceExplanationsComplete);
        }


        [PostgresFact]
        public async Task Ipsas24_original_budget_is_final_budget_when_no_revision_is_approved()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "No Revision MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-NR",
                    Name = "No Revision Vote", Level = FundCentreLevel.Vote,
                    FinancialYear = 2026, AppropriationAmount = 100m
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = accountId, CompanyId = companyId, Code = "5100",
                    Name = "Operating Expense", SubType = "EC-01",
                    Type = GlAccountType.Expense, Currency = "USD", IsActive = true
                });
                seed.PublicSectorBudgetVersions.Add(new PublicSectorBudgetVersion
                {
                    Id = versionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Accrual, ClassificationBasis = "EC-01",
                    EntityScope = "No Revision MDA",
                    PeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    PeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AuthorityReference = "Appropriation Act 2026",
                    ApprovedBy = Guid.NewGuid(), ApprovedAt = DateTime.UtcNow
                });
                seed.PublicSectorBudgetVersionLines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = lineId, CompanyId = companyId, BudgetVersionId = versionId,
                    FundCentreId = fundCentreId, GlAccountId = accountId, LineNumber = 1,
                    Description = "Operating expense", ClassificationCode = "EC-01", Amount = 100m
                });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 1,
                    StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 2, 1),
                    Status = PeriodStatus.Open
                });
                var journal = new JournalEntry
                {
                    CompanyId = companyId, EntryNo = "JE-NO-REVISION",
                    Description = "Actual expenditure", EntryDate = new DateTime(2026, 6, 30),
                    PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                };
                seed.JournalEntries.Add(journal);
                seed.JournalLines.Add(new JournalLine
                {
                    JournalEntryId = journal.Id, GlAccountId = accountId,
                    FundCentreId = fundCentreId, Debit = 40m, Credit = 0m,
                    BaseDebit = 40m, BaseCredit = 0m
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var report = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(report.FinalBudgetAvailable);
            Assert.Equal(versionId, report.FinalBudgetVersionId);
            var line = Assert.Single(report.Lines);
            Assert.Equal(100m, line.OriginalBudget);
            Assert.Equal(100m, line.FinalBudget);
            Assert.Equal(40m, line.ActualOnComparableBasis);
            Assert.Equal(60m, line.Variance);

            // Posted journal lines are immutable. Add a separate posted transaction with
            // an intentionally missing Fund Centre to verify that the comparison detects it.
            await using (var untaggedContext = db.NewContext(companyId))
            {
                var untaggedJournal = new JournalEntry
                {
                    CompanyId = companyId, EntryNo = "JE-NO-REVISION-UNTAGGED",
                    Description = "Untagged budget-scope actual", EntryDate = new DateTime(2026, 7, 1),
                    PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                };
                untaggedContext.JournalEntries.Add(untaggedJournal);
                untaggedContext.JournalLines.Add(new JournalLine
                {
                    JournalEntryId = untaggedJournal.Id, GlAccountId = accountId,
                    FundCentreId = null, Debit = 5m, Credit = 0m,
                    BaseDebit = 5m, BaseCredit = 0m
                });
                await untaggedContext.SaveChangesAsync();
            }
            var blocked = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(blocked.ComparableActualsAvailable);
            Assert.Contains("missing or unmatched Fund Centre/cost-centre dimensions", blocked.ComparabilityMessage,
                StringComparison.OrdinalIgnoreCase);
        }

        [PostgresFact]
        public async Task Ipsas24_cash_basis_budget_does_not_mislabel_accrual_gl_as_comparable_actuals()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var expenseId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company { Id = companyId, Name = "Cash Basis MDA", BaseCurrency = "USD", FunctionalCurrency = "USD" });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-CASH",
                    Name = "Cash Vote", Level = FundCentreLevel.Vote, FinancialYear = 2026
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = expenseId, CompanyId = companyId, Code = "5300", Name = "Cash Expense", SubType = "EC-CASH",
                    Type = GlAccountType.Expense, Currency = "USD"
                });
                await seed.SaveChangesAsync();
            }
            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var version = await service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Original, "USD", AccountingBasis.Cash,
                "Cash economic classification", "Whole MDA",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                "Appropriation Act 2026", null,
                new[] { new PublicSectorService.BudgetVersionLineInput(fundCentreId, expenseId, null, "Cash Expense", "EC-CASH", 100m) },
                Guid.NewGuid());
            version.Status = PublicSectorBudgetVersionStatus.Approved;
            version.ApprovedBy = Guid.NewGuid();
            version.ApprovedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            var report = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(report.ComparableActualsAvailable);
            Assert.All(report.Lines, line => Assert.Null(line.ActualOnComparableBasis));
            Assert.Contains("zero-activity attestation", report.ComparabilityMessage, StringComparison.OrdinalIgnoreCase);
        }

        [PostgresFact]
        public async Task Dimensional_budget_variance_uses_only_approved_account_and_cost_centre_allocations()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var expenseAccountId = Guid.NewGuid();
            var secondExpenseAccountId = Guid.NewGuid();
            var costCentreId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var actorId = Guid.NewGuid();
            var submitterId = Guid.NewGuid();
            var approverId = Guid.NewGuid();
            var pendingAllocationId = Guid.NewGuid();
            var workflowId = Guid.NewGuid();

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Dimensional Budget MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-DIM",
                    Name = "Dimensional Vote", Level = FundCentreLevel.Vote,
                    FinancialYear = 2026, AppropriationAmount = 1000m
                });
                seed.GlAccounts.AddRange(
                    new GlAccount { Id = expenseAccountId, CompanyId = companyId, Code = "5100", Name = "Travel", SubType = "EC-TRAVEL", Type = GlAccountType.Expense, Currency = "USD" },
                    new GlAccount { Id = secondExpenseAccountId, CompanyId = companyId, Code = "5200", Name = "Training", SubType = "EC-TRAINING", Type = GlAccountType.Expense, Currency = "USD" });
                seed.CostCentres.Add(new CostCentre
                {
                    Id = costCentreId, CompanyId = companyId, Code = "CC-OPS", Name = "Operations", IsActive = true
                });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 3,
                    StartDate = new DateTime(2026, 3, 1), EndDate = new DateTime(2026, 4, 1),
                    Status = PeriodStatus.Open
                });
                var journal = new JournalEntry
                {
                    CompanyId = companyId, EntryNo = "JE-DIMENSIONAL-BUDGET",
                    Description = "Operations travel expense", EntryDate = new DateTime(2026, 3, 15),
                    PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                };
                seed.JournalEntries.Add(journal);
                seed.JournalLines.AddRange(
                    new JournalLine
                    {
                        JournalEntryId = journal.Id, GlAccountId = expenseAccountId,
                        Debit = 25m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                        BaseDebit = 25m, BaseCredit = 0m, FundCentreId = fundCentreId, CostCentreId = costCentreId
                    },
                    new JournalLine
                    {
                        JournalEntryId = journal.Id, GlAccountId = expenseAccountId,
                        Debit = 10m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                        BaseDebit = 10m, BaseCredit = 0m, FundCentreId = fundCentreId
                    });
                seed.PublicSectorBudgetAllocations.AddRange(
                    new PublicSectorBudgetAllocation
                    {
                        CompanyId = companyId, FundCentreId = fundCentreId, GlAccountId = expenseAccountId,
                        CostCentreId = costCentreId, FinancialYear = 2026, Amount = 80m,
                        Status = PublicSectorBudgetAllocationStatus.Approved,
                        ApprovedBy = actorId, ApprovedAt = DateTime.UtcNow
                    },
                    new PublicSectorBudgetAllocation
                    {
                        Id = pendingAllocationId, CompanyId = companyId, FundCentreId = fundCentreId,
                        GlAccountId = secondExpenseAccountId, FinancialYear = 2026, Amount = 120m,
                        Status = PublicSectorBudgetAllocationStatus.PendingApproval, ApprovalWorkflowId = workflowId
                    });
                seed.WorkflowRequests.Add(new WorkflowRequest
                {
                    Id = workflowId, CompanyId = companyId, RequestType = "PublicSectorBudgetAllocation",
                    ReferenceId = pendingAllocationId, ReferenceTable = "PublicSectorBudgetAllocation",
                    SubmittedBy = submitterId, Amount = 120m, FundCentreId = fundCentreId,
                    Status = WorkflowStatus.Approved
                });
                seed.WorkflowApprovals.Add(new WorkflowApproval
                {
                    CompanyId = companyId, WorkflowRequestId = workflowId, Tier = 1,
                    ApproverId = approverId, Approved = true
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var report = await service.GetBudgetVarianceReportAsync(companyId, 2026, fundCentreId);
            var line = Assert.Single(report);
            Assert.Equal("5100", line.AccountCode);
            Assert.Equal("CC-OPS", line.CostCentreCode);
            Assert.Equal(80m, line.ApprovedBudget);
            Assert.Equal(25m, line.PostedActuals);
            Assert.Equal(55m, line.Variance);
            Assert.Equal(68.75m, line.VariancePercent);

            var classification = await service.GetClassificationScheduleAsync(companyId, 2026, fundCentreId);
            var classifiedTravel = Assert.Single(classification);
            Assert.Equal("VOTE-DIM", classifiedTravel.ProgrammeCode);
            Assert.Equal("EC-TRAVEL", classifiedTravel.EconomicClassificationCode);
            Assert.Equal("CC-OPS", classifiedTravel.ObjectiveCode);
            Assert.Equal(80m, classifiedTravel.ApprovedBudget);
            Assert.Equal(25m, classifiedTravel.PostedActuals);
            Assert.Equal(55m, classifiedTravel.Variance);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.FinaliseBudgetAllocationApprovalAsync(companyId, pendingAllocationId, Guid.NewGuid()));
            var finalised = await service.FinaliseBudgetAllocationApprovalAsync(companyId, pendingAllocationId, approverId);
            Assert.Equal(PublicSectorBudgetAllocationStatus.Approved, finalised.Status);
            var finalReport = await service.GetBudgetVarianceReportAsync(companyId, 2026, fundCentreId);
            Assert.Equal(2, finalReport.Count);
            Assert.Equal(120m, finalReport.Single(x => x.AccountCode == "5200").ApprovedBudget);
            Assert.Equal(0m, finalReport.Single(x => x.AccountCode == "5200").PostedActuals);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateBudgetAllocationAsync(companyId, fundCentreId, Guid.NewGuid(),
                    null, 2026, 10m, actorId));
        }

        [PostgresFact]
        public async Task Year_end_lapse_records_unexpended_appropriation_and_prevents_a_second_close()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var voteId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Lapse Reporting MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = voteId, CompanyId = companyId, Code = "VOTE-LAPSE",
                    Name = "Health Vote", Level = FundCentreLevel.Vote,
                    FinancialYear = 2026, AppropriationAmount = 100m
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var result = await service.CloseYearEndAsync(companyId, voteId, carryForward: false);

            Assert.Equal(100m, result.UnexpendedBalance);
            Assert.Equal(0m, result.CarriedForward);
            Assert.Equal(100m, result.Lapsed);
            Assert.Null(result.NewFundCentreId);

            var closed = await ctx.FundCentres.SingleAsync(x => x.Id == voteId);
            Assert.True(closed.IsClosed);
            Assert.Equal(100m, closed.LapsedAmount);
            Assert.Equal(0m, (await service.GetBudgetUtilizationAsync(companyId, voteId)).Available);
            var closedSchedule = Assert.Single(await service.GetExpenditureReportAsync(companyId, 2026),
                x => x.FundCentreId == voteId);
            Assert.Equal(100m, closedSchedule.LapsedAmount);
            Assert.Equal(0m, closedSchedule.AvailableBudget);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CloseYearEndAsync(companyId, voteId, carryForward: false));
        }

        [PostgresFact]
        public async Task Repeated_commitment_release_is_idempotent_and_audits_the_no_op()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var commitmentId = Guid.NewGuid();

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Commitment release audit test",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-REL",
                    Name = "Release Vote", Level = FundCentreLevel.Vote,
                    FinancialYear = 2026, AppropriationAmount = 500m
                });
                seed.Commitments.Add(new Commitment
                {
                    Id = commitmentId, CompanyId = companyId, FundCentreId = fundCentreId,
                    Amount = 100m, RealisedAmount = 25m,
                    Status = CommitmentStatus.Active, CommittedDate = new DateTime(2026, 3, 1)
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            await service.ReleaseCommitmentAsync(companyId, commitmentId);
            await service.ReleaseCommitmentAsync(companyId, commitmentId);

            var commitment = await ctx.Commitments.SingleAsync(x => x.Id == commitmentId);
            Assert.Equal(CommitmentStatus.Released, commitment.Status);
            Assert.NotNull(commitment.ReleasedDate);
            Assert.Equal(25m, commitment.RealisedAmount);
            Assert.Equal(1, await ctx.AuditLogs.CountAsync(x =>
                x.EntityId == commitmentId && x.Action == "BudgetCommitment.Released"));
            Assert.Equal(1, await ctx.AuditLogs.CountAsync(x =>
                x.EntityId == commitmentId && x.Action == "BudgetCommitment.ReleaseNoOp"));
        }


        [PostgresFact]
        public async Task Ipsas24_cash_actual_requires_reconciled_posted_source_and_independent_certification()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var bankAccountId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var journalId = Guid.NewGuid();
            var excludedJournalId = Guid.NewGuid();
            var bankTransactionId = Guid.NewGuid();
            var unmappedBankTransactionId = Guid.NewGuid();
            var lateBankTransactionId = Guid.NewGuid();
            var statementBatchId = Guid.NewGuid();
            var statementLineId = Guid.NewGuid();
            var excludedStatementLineId = Guid.NewGuid();
            var bankUploaderId = Guid.NewGuid();
            var bankAuthenticatorId = Guid.NewGuid();
            var bankCompletenessCertifierId = Guid.NewGuid();
            var statementPeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var statementPeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var statementBytes = Encoding.UTF8.GetBytes("authenticated-test-statement");
            var statementSha256 = Convert.ToHexString(SHA256.HashData(statementBytes)).ToLowerInvariant();
            var budgetVersionId = Guid.NewGuid();
            var budgetLineId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var certifierId = Guid.NewGuid();
            var effectiveDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "IPSAS Cash Actual MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-CASH-ACT",
                    Name = "Cash Actual Vote", Level = FundCentreLevel.Vote, FinancialYear = 2026
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = accountId, CompanyId = companyId, Code = "5101", Name = "Operating Cash Expenditure",
                    Type = GlAccountType.Expense, SubType = "EC-CASH-ACT", Currency = "USD",
                    CashFlowCategory = CashFlowCategory.Operating
                });
                seed.BankAccounts.Add(new BankAccount
                {
                    Id = bankAccountId, CompanyId = companyId, Name = "Operating Bank",
                    AccountNo = "TEST-001", BankName = "Test Bank", Currency = "USD", GlAccountId = accountId
                });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 6,
                    StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                    Status = PeriodStatus.Open
                });
                seed.JournalEntries.AddRange(
                    new JournalEntry
                    {
                        Id = journalId, CompanyId = companyId, EntryNo = "JE-CASH-ACT",
                        Description = "Reconciled cash execution", EntryDate = effectiveDate,
                        PeriodId = periodId, Source = JournalSource.Bank, Status = JournalStatus.Posted
                    },
                    new JournalEntry
                    {
                        Id = excludedJournalId, CompanyId = companyId, EntryNo = "JE-CASH-EXCLUDED",
                        Description = "Separately identified bank charge", EntryDate = effectiveDate,
                        PeriodId = periodId, Source = JournalSource.Bank, Status = JournalStatus.Posted
                    });
                seed.BankTransactions.Add(new BankTransaction
                {
                    Id = bankTransactionId, CompanyId = companyId, BankAccountId = bankAccountId,
                    TransactionDate = effectiveDate, Description = "Verified supplier payment",
                    Debit = 75m, Credit = 0m, Reference = "BANK-REF-001",
                    IsReconciled = true, JournalEntryId = journalId,
                    MatchedStatementLineHash = statementLineId
                });
                seed.BankTransactions.Add(new BankTransaction
                {
                    Id = unmappedBankTransactionId, CompanyId = companyId, BankAccountId = bankAccountId,
                    TransactionDate = effectiveDate,
                    Description = "Bank charge excluded from budget actual allocation",
                    Debit = 25m, Credit = 0m, Reference = "BANK-REF-002",
                    IsReconciled = true, JournalEntryId = excludedJournalId,
                    MatchedStatementLineHash = excludedStatementLineId
                });
                seed.JournalLines.Add(new JournalLine
                {
                    JournalEntryId = journalId, GlAccountId = accountId, FundCentreId = fundCentreId,
                    Debit = 75m, Credit = 0m, BaseDebit = 75m, BaseCredit = 0m
                });
                // The bank population is now backed by a retained, independently authenticated
                // statement. Both statement rows are matched to the bank ledger; the separate
                // budget-source reconciliation below excludes the bank charge from budget actuals.
                seed.BankStatementImportBatches.Add(new BankStatementImportBatch
                {
                    Id = statementBatchId, CompanyId = companyId, BankAccountId = bankAccountId,
                    SourceInstitution = "Test Bank", SourceAccountReference = "TEST-001",
                    StatementReference = "TEST-STATEMENT-2026",
                    PeriodStart = statementPeriodStart, PeriodEnd = statementPeriodEnd,
                    OpeningBalance = 1000m, ClosingBalance = 900m,
                    TotalDebits = 100m, TotalCredits = 0m, TransactionCount = 2,
                    OriginalFileName = "statement-2026.csv", MediaType = "text/csv",
                    ParserVersion = BankStatementCsvParser.ParserVersion,
                    Sha256 = statementSha256, ByteLength = statementBytes.Length,
                    OriginalBytes = statementBytes,
                    UploadedBy = bankUploaderId, UploadedAt = DateTime.UtcNow,
                    AuthenticationMethod = BankStatementOriginAuthenticationMethod.IndependentBankConfirmation,
                    AuthenticationEvidenceReference = "BANK-CONFIRM-TEST-2026",
                    AuthenticatedBy = bankAuthenticatorId, AuthenticatedAt = DateTime.UtcNow,
                    Status = BankStatementImportStatus.Certified,
                    CompletenessCertifiedBy = bankCompletenessCertifierId,
                    CompletenessCertifiedAt = DateTime.UtcNow,
                    CreatedBy = bankUploaderId
                });
                seed.BankStatementImportedTransactions.AddRange(
                    new BankStatementImportedTransaction
                    {
                        Id = statementLineId, CompanyId = companyId, ImportBatchId = statementBatchId,
                        BankAccountId = bankAccountId, ExternalTransactionId = "EXT-TXN-001",
                        TransactionDate = effectiveDate, Description = "Verified supplier payment",
                        Reference = "BANK-REF-001", Debit = 75m, Credit = 0m, Currency = "USD",
                        SourceLineNumber = 2, TransactionFingerprint = new string('b', 64),
                        Status = BankStatementImportedTransactionStatus.Matched,
                        MatchedBankTransactionId = bankTransactionId, MatchedBy = bankAuthenticatorId,
                        MatchedAt = DateTime.UtcNow, CreatedBy = bankUploaderId
                    },
                    new BankStatementImportedTransaction
                    {
                        Id = excludedStatementLineId, CompanyId = companyId, ImportBatchId = statementBatchId,
                        BankAccountId = bankAccountId, ExternalTransactionId = "EXT-TXN-002",
                        TransactionDate = effectiveDate, Description = "Separately identified bank charge",
                        Reference = "BANK-REF-002", Debit = 25m, Credit = 0m, Currency = "USD",
                        SourceLineNumber = 3, TransactionFingerprint = new string('c', 64),
                        Status = BankStatementImportedTransactionStatus.Matched,
                        MatchedBankTransactionId = unmappedBankTransactionId, MatchedBy = bankAuthenticatorId,
                        MatchedAt = DateTime.UtcNow, CreatedBy = bankUploaderId
                    });
                var version = new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "MDA cash classification",
                    EntityScope = "IPSAS Cash Actual MDA",
                    PeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    PeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = certifierId,
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                };
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = budgetLineId, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = accountId, LineNumber = 1,
                    Description = "Operating cash expenditure", ClassificationCode = "EC-CASH-ACT",
                    Amount = 100m, CreatedBy = preparerId
                });
                seed.PublicSectorBudgetVersions.Add(version);
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            // A reconciliation must not be prepared before certified actuals and the
            // complete independently reviewed source register make the comparison current.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateBudgetReconciliationAsync(companyId, 2026,
                    PublicSectorBudgetReconciliationType.Basis, "Premature cash-to-accrual reconciliation",
                    "Revenue", 0m, 0m, 0m, "WP-IPSAS24-CASH-PREMATURE", preparerId,
                    default, 5m, 0m));
            var actual = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                budgetLineId, PublicSectorBudgetActualEventKind.CashPayment,
                PublicSectorBudgetActualSourceKind.BankTransaction, bankTransactionId,
                effectiveDate, CashFlowCategory.Operating, preparerId);

            Assert.Equal(75m, actual.Amount);
            Assert.Equal("BANK-REF-001", actual.SourceReference);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, actual.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CertifyBudgetExecutionActualAsync(companyId, actual.Id, preparerId));

            await service.SubmitBudgetExecutionActualAsync(companyId, actual.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CertifyBudgetExecutionActualAsync(companyId, actual.Id, preparerId));
            var certified = await service.CertifyBudgetExecutionActualAsync(companyId, actual.Id, certifierId);

            Assert.Equal(PublicSectorBudgetActualStatus.Certified, certified.Status);
            Assert.Equal(certifierId, certified.CertifiedBy);
            Assert.NotNull(certified.CertifiedAt);

            var sourceReviewerId = Guid.NewGuid();
            var reconciliationPreparerId = Guid.NewGuid();
            var reconciliationReviewerId = Guid.NewGuid();
            var expectedSource = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "BankAccount", "BANK-OPERATING-01", "Operating bank account",
                bankAccountId.ToString(), true,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), preparerId);
            await service.SubmitExpectedCashSourceAsync(companyId, expectedSource.Id, preparerId);
            await service.ApproveExpectedCashSourceAsync(companyId, expectedSource.Id, sourceReviewerId);
            var submission = await service.CreateCashSourceSubmissionAsync(companyId, expectedSource.Id,
                2, 100m, 1000m, 900m, "USD", "Operating bank statement and reconciliation", preparerId);
            await service.SubmitCashSourceSubmissionAsync(companyId, submission.Id, preparerId);
            await service.ReviewCashSourceSubmissionAsync(companyId, submission.Id, sourceReviewerId,
                true, "Statement control total independently confirmed.");
            await service.AddCashSourceReconciliationLineAsync(companyId, submission.Id,
                bankTransactionId, "BANK-REF-001", certified.Id, false, null, reconciliationPreparerId);
            await service.AddCashSourceReconciliationLineAsync(companyId, submission.Id,
                unmappedBankTransactionId, "BANK-REF-002", null, true,
                "Separately identified bank charge excluded from this budget line under the approved classification workpaper.",
                reconciliationPreparerId);
            await service.SubmitCashSourceRecordReconciliationAsync(companyId, submission.Id,
                reconciliationPreparerId);
            await service.ReviewCashSourceRecordReconciliationAsync(companyId, submission.Id,
                reconciliationReviewerId, true, "Bank statement transaction matched to certified budget actual.");
            var registerAttestation = await service.CreateExpectedCashSourceRegisterAttestationAsync(
                companyId, budgetVersionId, 1, "IPSAS Cash Actual MDA",
                "Signed bank account register for the reporting entity", preparerId);
            await service.SubmitExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id, preparerId);
            await service.ReviewExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id,
                certifierId, true, "All expected bank source populations independently confirmed.");

            // Cash-basis IPSAS 24 comparison must use certified source actuals, never
            // accrual GL totals. An unlinked bank movement is not treated as a posted
            // cash event and remains ineligible for capture.
            var comparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(comparison.ComparableActualsAvailable, comparison.ComparabilityMessage);
            var comparisonLine = Assert.Single(comparison.Lines);
            Assert.Equal(75m, comparisonLine.ActualOnComparableBasis);

            // A certified actual is corrected only through an independently certified
            // compensating event. The original source evidence remains in the audit trail.
            var reversalInitiatorId = Guid.NewGuid();
            var reversalCertifierId = Guid.NewGuid();
            var reversal = await service.CreateBudgetExecutionActualReversalAsync(companyId,
                certified.Id, "Correct the original classification after independent source review.",
                reversalInitiatorId);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, reversal.Status);
            Assert.Equal(certified.Id, reversal.ReversalOfId);
            Assert.Equal(PublicSectorBudgetActualEventKind.CashReceipt, reversal.EventKind);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateBudgetExecutionActualReversalAsync(companyId, certified.Id,
                    "Attempt a duplicate reversal of the same certified source allocation.", Guid.NewGuid()));
            await service.SubmitBudgetExecutionActualAsync(companyId, reversal.Id, reversalInitiatorId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CertifyBudgetExecutionActualAsync(companyId, reversal.Id, reversalInitiatorId));
            var certifiedReversal = await service.CertifyBudgetExecutionActualAsync(
                companyId, reversal.Id, reversalCertifierId);
            Assert.Equal(PublicSectorBudgetActualStatus.Certified, certifiedReversal.Status);
            Assert.Equal(PublicSectorBudgetActualStatus.Reversed,
                (await ctx.PublicSectorBudgetExecutionActuals.SingleAsync(x => x.Id == certified.Id)).Status);

            // The PostgreSQL trigger independently rejects a chained reversal even if
            // a caller bypasses the application service.
            await using (var chainContext = db.NewContext(companyId))
            {
                chainContext.PublicSectorBudgetExecutionActuals.Add(new PublicSectorBudgetExecutionActual
                {
                    CompanyId = companyId,
                    FinancialYear = certifiedReversal.FinancialYear,
                    BudgetVersionId = certifiedReversal.BudgetVersionId,
                    BudgetVersionLineId = certifiedReversal.BudgetVersionLineId,
                    FundCentreId = certifiedReversal.FundCentreId,
                    GlAccountId = certifiedReversal.GlAccountId,
                    CostCentreId = certifiedReversal.CostCentreId,
                    EventKind = PublicSectorBudgetActualEventKind.CashPayment,
                    SourceKind = certifiedReversal.SourceKind,
                    SourceRecordId = certifiedReversal.SourceRecordId,
                    SourceReference = certifiedReversal.SourceReference,
                    SourceDate = certifiedReversal.SourceDate,
                    EffectiveDate = certifiedReversal.EffectiveDate,
                    Currency = certifiedReversal.Currency,
                    SourceAmount = certifiedReversal.SourceAmount,
                    Amount = certifiedReversal.Amount,
                    ExchangeRateToBudgetCurrency = certifiedReversal.ExchangeRateToBudgetCurrency,
                    ExchangeRateDate = certifiedReversal.ExchangeRateDate,
                    ExchangeRateSource = certifiedReversal.ExchangeRateSource,
                    ExchangeRateSourceReference = certifiedReversal.ExchangeRateSourceReference,
                    ExchangeRateIsInverse = certifiedReversal.ExchangeRateIsInverse,
                    ExchangeRateApprovedBy = certifiedReversal.ExchangeRateApprovedBy,
                    ExchangeRateApprovedAt = certifiedReversal.ExchangeRateApprovedAt,
                    CashFlowSection = certifiedReversal.CashFlowSection,
                    ReversalOfId = certifiedReversal.Id,
                    ReversalReason = "Attempt to chain a second compensating reversal.",
                    Status = PublicSectorBudgetActualStatus.Draft,
                    CreatedBy = Guid.NewGuid()
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => chainContext.SaveChangesAsync());
            }

            // A source population can be corrected by a new numbered revision without
            // mutating the previously reviewed submission. Certified actuals are reused
            // only within the latest revision's record-level mapping.
            var revisedSubmission = await service.CreateCashSourceSubmissionAsync(companyId,
                expectedSource.Id, 2, 100m, 1000m, 900m, "USD",
                "Replacement bank statement after late statement delivery", preparerId,
                default, "The bank supplied a corrected statement export; reconcile the revised source population.");
            Assert.Equal(2, revisedSubmission.SubmissionRevision);
            Assert.Equal("Reviewed", submission.Status);
            Assert.Equal("Reviewed", submission.RecordLevelStatus);
            await service.SubmitCashSourceSubmissionAsync(companyId, revisedSubmission.Id, preparerId);
            await service.ReviewCashSourceSubmissionAsync(companyId, revisedSubmission.Id, sourceReviewerId,
                true, "Replacement statement control totals independently confirmed.");

            // Reuse the genuine bank transaction identifier only after the certified
            // reversal and in the new source-submission revision; no fabricated source ID.
            var replacementPreparerId = Guid.NewGuid();
            var replacementCertifierId = Guid.NewGuid();
            var replacementActual = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                budgetLineId, PublicSectorBudgetActualEventKind.CashPayment,
                PublicSectorBudgetActualSourceKind.BankTransaction, bankTransactionId,
                effectiveDate, CashFlowCategory.Operating, replacementPreparerId);
            await service.SubmitBudgetExecutionActualAsync(companyId, replacementActual.Id, replacementPreparerId);
            await service.CertifyBudgetExecutionActualAsync(companyId, replacementActual.Id, replacementCertifierId);

            await service.AddCashSourceReconciliationLineAsync(companyId, revisedSubmission.Id,
                bankTransactionId, "BANK-REF-001", replacementActual.Id, false, null, reconciliationPreparerId);
            await service.AddCashSourceReconciliationLineAsync(companyId, revisedSubmission.Id,
                unmappedBankTransactionId, "BANK-REF-002", null, true,
                "Separately identified bank charge excluded from this budget line under the approved classification workpaper.",
                reconciliationPreparerId);
            await service.SubmitCashSourceRecordReconciliationAsync(companyId, revisedSubmission.Id,
                reconciliationPreparerId);
            await service.ReviewCashSourceRecordReconciliationAsync(companyId, revisedSubmission.Id,
                reconciliationReviewerId, true, "Replacement source population reconciled and independently reviewed.");
            var revisedComparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(revisedComparison.ComparableActualsAvailable, revisedComparison.ComparabilityMessage);
            Assert.Equal(75m, Assert.Single(revisedComparison.Lines).ActualOnComparableBasis);
            Assert.True(revisedComparison.FinancialStatementReconciliation.Required);
            Assert.False(revisedComparison.FinancialStatementReconciliation.Complete);

            async Task ApproveFinancialStatementReconciliationAsync(PublicSectorBudgetReconciliation item, decimal workflowAmount)
            {
                var workflowId = Guid.NewGuid();
                ctx.WorkflowRequests.Add(new WorkflowRequest
                {
                    Id = workflowId, CompanyId = companyId, RequestType = "PublicSectorBudgetReconciliation",
                    ReferenceId = item.Id, ReferenceTable = "PublicSectorBudgetReconciliation",
                    SubmittedBy = reconciliationPreparerId, Amount = workflowAmount, Status = WorkflowStatus.Pending
                });
                await ctx.SaveChangesAsync();
                await service.MarkBudgetReconciliationPendingAsync(companyId, item.Id, workflowId);
                var workflow = await ctx.WorkflowRequests.SingleAsync(x => x.Id == workflowId);
                workflow.Status = WorkflowStatus.Approved;
                ctx.WorkflowApprovals.Add(new WorkflowApproval
                {
                    CompanyId = companyId, WorkflowRequestId = workflowId, Tier = 1,
                    ApproverId = reconciliationReviewerId, Approved = true, DecidedAt = DateTime.UtcNow
                });
                await ctx.SaveChangesAsync();
                await service.FinaliseBudgetReconciliationApprovalAsync(companyId, item.Id, reconciliationReviewerId);
            }

            foreach (var candidate in revisedComparison.AutomaticReconciliationCandidates)
            {
                var automaticRow = await service.CreateAutomaticBudgetReconciliationAsync(companyId, 2026,
                    revisedComparison.FinalBudgetVersionId!.Value, candidate.Type, candidate.StatementSection,
                    candidate.Explanation,
                    $"AUTO-IPSAS24-{candidate.Type}-{candidate.StatementSection}-{candidate.SourceFingerprint[..12]}",
                    revisedComparison.StatementPeriodStart, revisedComparison.StatementPeriodEnd,
                    revisedComparison.FinancialStatementEntityScope, reconciliationPreparerId);
                var workflowAmount = Math.Abs(automaticRow.RevenueAmount) + Math.Abs(automaticRow.ExpenseAmount);
                await ApproveFinancialStatementReconciliationAsync(automaticRow, workflowAmount);
            }

            var reconciledComparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(reconciledComparison.FinancialStatementReconciliation.Complete,
                reconciledComparison.FinancialStatementReconciliation.Message);
            var expectedRevenueAdjustment = revisedComparison.AutomaticReconciliationCandidates
                .Where(x => x.StatementSection == "Revenue").Sum(x => x.ExpectedAdjustment);
            var expectedExpenseAdjustment = revisedComparison.AutomaticReconciliationCandidates
                .Where(x => x.StatementSection == "Expense").Sum(x => x.ExpectedAdjustment);
            Assert.Equal(revisedComparison.FinancialStatementReconciliation.ComparableRevenueActual!.Value +
                expectedRevenueAdjustment, reconciledComparison.FinancialStatementReconciliation.FinancialStatementRevenueActual);
            Assert.Equal(revisedComparison.FinancialStatementReconciliation.ComparableExpenseActual!.Value +
                expectedExpenseAdjustment, reconciledComparison.FinancialStatementReconciliation.FinancialStatementExpenseActual);
            
            // Reconciliation must also revalidate each record, not just the aggregate
            // count and control total. A changed reference on an excluded record is stale.
            var changedExcludedSource = await ctx.BankTransactions.SingleAsync(x => x.Id == unmappedBankTransactionId);
            changedExcludedSource.Reference = "BANK-REF-CHANGED-AFTER-REVIEW";
            await ctx.SaveChangesAsync();
            var changedSourceComparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(changedSourceComparison.ComparableActualsAvailable);
            Assert.Contains("source record population", changedSourceComparison.ComparabilityMessage,
                StringComparison.OrdinalIgnoreCase);

            // A new, unlinked bank movement after certification must block comparability;
            // the earlier independently reviewed exclusion remains a valid covered source row.
            ctx.BankTransactions.Add(new BankTransaction
            {
                Id = lateBankTransactionId, CompanyId = companyId, BankAccountId = bankAccountId,
                TransactionDate = effectiveDate, Description = "Late unreconciled bank movement",
                Debit = 15m, Credit = 0m, Reference = "BANK-REF-003",
                IsReconciled = false, JournalEntryId = null
            });
            await ctx.SaveChangesAsync();
            var incompleteComparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(incompleteComparison.ComparableActualsAvailable);
            Assert.Contains("source coverage is incomplete", incompleteComparison.ComparabilityMessage);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId, budgetLineId,
                    PublicSectorBudgetActualEventKind.CashPayment,
                    PublicSectorBudgetActualSourceKind.BankTransaction, bankTransactionId,
                    effectiveDate, CashFlowCategory.Operating, preparerId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId, budgetLineId,
                    PublicSectorBudgetActualEventKind.CashPayment,
                    PublicSectorBudgetActualSourceKind.BankTransaction, unmappedBankTransactionId,
                    effectiveDate, CashFlowCategory.Operating, preparerId));
        }

        [PostgresFact]
        public async Task Ipsas24_cash_budget_allows_investing_asset_classification_lines()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var assetAccountId = Guid.NewGuid();
            var actorId = Guid.NewGuid();
            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "IPSAS Cash Capital MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-CAPEX",
                    Name = "Capital Programme", Level = FundCentreLevel.Vote, FinancialYear = 2026
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = assetAccountId, CompanyId = companyId, Code = "1500",
                    Name = "Property Plant and Equipment", Type = GlAccountType.Asset,
                    SubType = "CAPEX-01", Currency = "USD",
                    CashFlowCategory = CashFlowCategory.Investing
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var version = await service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Original, "USD", AccountingBasis.Cash,
                "Treasury cash economic classification", "IPSAS Cash Capital MDA",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                "Appropriation Act 2026", null,
                new[]
                {
                    new PublicSectorService.BudgetVersionLineInput(
                        fundCentreId, assetAccountId, null, "Capital asset acquisitions", "CAPEX-01", 250m)
                }, actorId);

            var line = Assert.Single(version.Lines);
            Assert.Equal(assetAccountId, line.GlAccountId);
            Assert.Equal(250m, line.Amount);
        }

        [PostgresFact]
        public async Task Ipsas24_cash_actuals_accept_fully_settled_AR_receipts_and_authorised_AP_payments()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var costCentreId = Guid.NewGuid();
            var bankGlId = Guid.NewGuid();
            var arControlGlId = Guid.NewGuid();
            var apControlGlId = Guid.NewGuid();
            var revenueGlId = Guid.NewGuid();
            var revenueGl2Id = Guid.NewGuid();
            var expenseGlId = Guid.NewGuid();
            var expenseGl2Id = Guid.NewGuid();
            var bankAccountId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var arInvoiceJournalId = Guid.NewGuid();
            var arInvoice2JournalId = Guid.NewGuid();
            var arReceiptJournalId = Guid.NewGuid();
            var apInvoiceJournalId = Guid.NewGuid();
            var apInvoice2JournalId = Guid.NewGuid();
            var apPaymentJournalId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var arInvoiceId = Guid.NewGuid();
            var arInvoice2Id = Guid.NewGuid();
            var arReceiptId = Guid.NewGuid();
            var arReceiptAllocation1Id = Guid.NewGuid();
            var arReceiptAllocation2Id = Guid.NewGuid();
            var arLine1Id = Guid.NewGuid();
            var arLine2Id = Guid.NewGuid();
            var arInvoice2LineId = Guid.NewGuid();
            var apInvoiceId = Guid.NewGuid();
            var apPaymentAllocation1Id = Guid.NewGuid();
            var apPaymentAllocation2Id = Guid.NewGuid();
            var apLine1Id = Guid.NewGuid();
            var apLine2Id = Guid.NewGuid();
            var apInvoice2LineId = Guid.NewGuid();
            var apInvoice2Id = Guid.NewGuid();
            var apPaymentId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var arBudgetLineId = Guid.NewGuid();
            var arBudgetLine2Id = Guid.NewGuid();
            var apBudgetLineId = Guid.NewGuid();
            var apBudgetLine2Id = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var certifierId = Guid.NewGuid();
            var effectiveDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "IPSAS AR AP Settlement MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-SETTLE",
                    Name = "Settlement Vote", Level = FundCentreLevel.Vote, FinancialYear = 2026
                });
                seed.CostCentres.Add(new CostCentre
                {
                    Id = costCentreId, CompanyId = companyId, Code = "CC-SETTLE",
                    Name = "Settlement Cost Centre", IsActive = true
                });
                seed.GlAccounts.AddRange(
                    new GlAccount
                    {
                        Id = bankGlId, CompanyId = companyId, Code = "1000", Name = "Operating Bank",
                        Type = GlAccountType.Asset, SubType = "BANK", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.CashAndCashEquivalents
                    },
                    new GlAccount
                    {
                        Id = arControlGlId, CompanyId = companyId, Code = "1200", Name = "Trade Receivables",
                        Type = GlAccountType.Asset, SubType = "AR-CONTROL", Currency = "USD"
                    },
                    new GlAccount
                    {
                        Id = apControlGlId, CompanyId = companyId, Code = "2100", Name = "Trade Payables",
                        Type = GlAccountType.Liability, SubType = "AP-CONTROL", Currency = "USD"
                    },
                    new GlAccount
                    {
                        Id = revenueGlId, CompanyId = companyId, Code = "4100", Name = "Service Revenue",
                        Type = GlAccountType.Income, SubType = "REV-SVC", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.Operating
                    },
                    new GlAccount
                    {
                        Id = revenueGl2Id, CompanyId = companyId, Code = "4200", Name = "Other Service Revenue",
                        Type = GlAccountType.Income, SubType = "REV-OTHER", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.Operating
                    },
                    new GlAccount
                    {
                        Id = expenseGlId, CompanyId = companyId, Code = "5100", Name = "Operating Supplies",
                        Type = GlAccountType.Expense, SubType = "EXP-SUP", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.Operating
                    },
                    new GlAccount
                    {
                        Id = expenseGl2Id, CompanyId = companyId, Code = "5101", Name = "Other Operating Supplies",
                        Type = GlAccountType.Expense, SubType = "EXP-OTHER", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.Operating
                    });
                seed.BankAccounts.Add(new BankAccount
                {
                    Id = bankAccountId, CompanyId = companyId, Name = "Operating Bank",
                    AccountNo = "SETTLE-001", BankName = "Test Bank", Currency = "USD", GlAccountId = bankGlId
                });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 6,
                    StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                    Status = PeriodStatus.Open
                });
                seed.JournalEntries.AddRange(
                    new JournalEntry { Id = arInvoiceJournalId, CompanyId = companyId, EntryNo = "JE-AR-INV", Description = "AR invoice 1", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AR, Status = JournalStatus.Posted },
                    new JournalEntry { Id = arInvoice2JournalId, CompanyId = companyId, EntryNo = "JE-AR-INV-2", Description = "AR invoice 2", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AR, Status = JournalStatus.Posted },
                    new JournalEntry { Id = arReceiptJournalId, CompanyId = companyId, EntryNo = "JE-AR-REC", Description = "AR receipt", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AR, Status = JournalStatus.Posted },
                    new JournalEntry { Id = apInvoiceJournalId, CompanyId = companyId, EntryNo = "JE-AP-INV", Description = "AP invoice 1", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AP, Status = JournalStatus.Posted },
                    new JournalEntry { Id = apInvoice2JournalId, CompanyId = companyId, EntryNo = "JE-AP-INV-2", Description = "AP invoice 2", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AP, Status = JournalStatus.Posted },
                    new JournalEntry { Id = apPaymentJournalId, CompanyId = companyId, EntryNo = "JE-AP-PAY", Description = "AP payment", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AP, Status = JournalStatus.Posted });
                seed.JournalLines.AddRange(
                    new JournalLine { JournalEntryId = arInvoiceJournalId, GlAccountId = revenueGlId, FundCentreId = fundCentreId, CostCentreId = costCentreId, Credit = 60m, Debit = 0m, BaseCredit = 60m, BaseDebit = 0m },
                    new JournalLine { JournalEntryId = arInvoiceJournalId, GlAccountId = revenueGl2Id, FundCentreId = fundCentreId, Credit = 40m, Debit = 0m, BaseCredit = 40m, BaseDebit = 0m },
                    new JournalLine { JournalEntryId = arInvoiceJournalId, GlAccountId = arControlGlId, Debit = 100m, Credit = 0m, BaseDebit = 100m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = arInvoice2JournalId, GlAccountId = revenueGl2Id, FundCentreId = fundCentreId, Credit = 60m, Debit = 0m, BaseCredit = 60m, BaseDebit = 0m },
                    new JournalLine { JournalEntryId = arInvoice2JournalId, GlAccountId = arControlGlId, Debit = 60m, Credit = 0m, BaseDebit = 60m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = arReceiptJournalId, GlAccountId = bankGlId, Debit = 130m, Credit = 0m, BaseDebit = 130m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = arReceiptJournalId, GlAccountId = arControlGlId, Debit = 0m, Credit = 130m, BaseDebit = 0m, BaseCredit = 130m },
                    new JournalLine { JournalEntryId = apInvoiceJournalId, GlAccountId = expenseGlId, FundCentreId = fundCentreId, CostCentreId = costCentreId, Debit = 120m, Credit = 0m, BaseDebit = 120m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = apInvoiceJournalId, GlAccountId = expenseGl2Id, FundCentreId = fundCentreId, Debit = 80m, Credit = 0m, BaseDebit = 80m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = apInvoiceJournalId, GlAccountId = apControlGlId, Debit = 0m, Credit = 200m, BaseDebit = 0m, BaseCredit = 200m },
                    new JournalLine { JournalEntryId = apInvoice2JournalId, GlAccountId = expenseGl2Id, FundCentreId = fundCentreId, Debit = 120m, Credit = 0m, BaseDebit = 120m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = apInvoice2JournalId, GlAccountId = apControlGlId, Debit = 0m, Credit = 120m, BaseDebit = 0m, BaseCredit = 120m },
                    new JournalLine { JournalEntryId = apPaymentJournalId, GlAccountId = apControlGlId, Debit = 260m, Credit = 0m, BaseDebit = 260m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = apPaymentJournalId, GlAccountId = bankGlId, Debit = 0m, Credit = 260m, BaseDebit = 0m, BaseCredit = 260m });
                seed.Customers.Add(new Customer { Id = customerId, CompanyId = companyId, Code = "C-SETTLE", Name = "Settlement Customer", Currency = "USD", ArAccountId = arControlGlId });
                seed.Suppliers.Add(new Supplier { Id = supplierId, CompanyId = companyId, Code = "S-SETTLE", Name = "Settlement Supplier", Currency = "USD", ApAccountId = apControlGlId });
                seed.ArInvoices.Add(new ArInvoice
                {
                    Id = arInvoiceId, CompanyId = companyId, InvoiceNo = "AR-SETTLE-001",
                    CustomerId = customerId, InvoiceDate = effectiveDate, DueDate = effectiveDate,
                    Currency = "USD", Subtotal = 100m, Total = 100m, Outstanding = 0m,
                    Status = InvoiceStatus.Paid, JournalEntryId = arInvoiceJournalId, FundCentreId = fundCentreId,
                    Lines = new List<ArInvoiceLine>
                    {
                        new() { Id = arLine1Id, ArInvoiceId = arInvoiceId, Description = "Service", Quantity = 1m, UnitPrice = 60m, LineSubtotal = 60m, LineVat = 0m, LineTotal = 60m, GlAccountId = revenueGlId, CostCentreId = costCentreId },
                        new() { Id = arLine2Id, ArInvoiceId = arInvoiceId, Description = "Other service", Quantity = 1m, UnitPrice = 40m, LineSubtotal = 40m, LineVat = 0m, LineTotal = 40m, GlAccountId = revenueGl2Id }
                    }
                });
                seed.ArInvoices.Add(new ArInvoice
                {
                    Id = arInvoice2Id, CompanyId = companyId, InvoiceNo = "AR-SETTLE-002",
                    CustomerId = customerId, InvoiceDate = effectiveDate, DueDate = effectiveDate,
                    Currency = "USD", Subtotal = 60m, Total = 60m, Outstanding = 30m,
                    Status = InvoiceStatus.Partial, JournalEntryId = arInvoice2JournalId, FundCentreId = fundCentreId,
                    Lines = new List<ArInvoiceLine>
                    {
                        new() { Id = arInvoice2LineId, ArInvoiceId = arInvoice2Id, Description = "Additional service", Quantity = 1m, UnitPrice = 60m, LineSubtotal = 60m, LineVat = 0m, LineTotal = 60m, GlAccountId = revenueGl2Id }
                    }
                });
                seed.ArReceipts.Add(new ArReceipt
                {
                    Id = arReceiptId, CompanyId = companyId, ReceiptNo = "RCT-SETTLE-001",
                    CustomerId = customerId, ReceiptDate = effectiveDate, Currency = "USD",
                    Amount = 130m, JournalEntryId = arReceiptJournalId,
                    Allocations = new List<ArReceiptAllocation>
                    {
                        new() { Id = arReceiptAllocation1Id, ArReceiptId = arReceiptId, ArInvoiceId = arInvoiceId, AmountApplied = 100m },
                        new() { Id = arReceiptAllocation2Id, ArReceiptId = arReceiptId, ArInvoiceId = arInvoice2Id, AmountApplied = 30m }
                    }
                });
                seed.ApInvoices.Add(new ApInvoice
                {
                    Id = apInvoiceId, CompanyId = companyId, InvoiceNo = "AP-SETTLE-001",
                    SupplierId = supplierId, InvoiceDate = effectiveDate, DueDate = effectiveDate,
                    Currency = "USD", Subtotal = 200m, Total = 200m, Outstanding = 0m,
                    Status = InvoiceStatus.Paid, JournalEntryId = apInvoiceJournalId, FundCentreId = fundCentreId,
                    Lines = new List<ApInvoiceLine>
                    {
                        new() { Id = apLine1Id, ApInvoiceId = apInvoiceId, Description = "Supplies", Quantity = 1m, UnitCost = 120m, LineSubtotal = 120m, LineVat = 0m, LineTotal = 120m, GlAccountId = expenseGlId, CostCentreId = costCentreId },
                        new() { Id = apLine2Id, ApInvoiceId = apInvoiceId, Description = "Other supplies", Quantity = 1m, UnitCost = 80m, LineSubtotal = 80m, LineVat = 0m, LineTotal = 80m, GlAccountId = expenseGl2Id }
                    }
                });
                seed.ApInvoices.Add(new ApInvoice
                {
                    Id = apInvoice2Id, CompanyId = companyId, InvoiceNo = "AP-SETTLE-002",
                    SupplierId = supplierId, InvoiceDate = effectiveDate, DueDate = effectiveDate,
                    Currency = "USD", Subtotal = 120m, Total = 120m, Outstanding = 60m,
                    Status = InvoiceStatus.Partial, JournalEntryId = apInvoice2JournalId, FundCentreId = fundCentreId,
                    Lines = new List<ApInvoiceLine>
                    {
                        new() { Id = apInvoice2LineId, ApInvoiceId = apInvoice2Id, Description = "Other supplies", Quantity = 1m, UnitCost = 120m, LineSubtotal = 120m, LineVat = 0m, LineTotal = 120m, GlAccountId = expenseGl2Id }
                    }
                });
                seed.ApPayments.Add(new ApPayment
                {
                    Id = apPaymentId, CompanyId = companyId, PaymentNo = "PAY-SETTLE-001",
                    SupplierId = supplierId, PaymentDate = effectiveDate, Currency = "USD",
                    Amount = 260m, JournalEntryId = apPaymentJournalId,
                    PreparedBy = preparerId, AuthorisedBy = certifierId, AuthorisedAt = DateTime.UtcNow,
                    Allocations = new List<ApPaymentAllocation>
                    {
                        new() { Id = apPaymentAllocation1Id, ApPaymentId = apPaymentId, ApInvoiceId = apInvoiceId, AmountApplied = 200m },
                        new() { Id = apPaymentAllocation2Id, ApPaymentId = apPaymentId, ApInvoiceId = apInvoice2Id, AmountApplied = 60m }
                    }
                });
                seed.SettlementComponentAllocations.AddRange(
                    new SettlementComponentAllocation { CompanyId = companyId, SettlementKind = "AR", SettlementAllocationId = arReceiptAllocation1Id, InvoiceId = arInvoiceId, ComponentKind = SettlementComponentKind.InvoiceLine, InvoiceLineId = arLine1Id, AmountApplied = 60m },
                    new SettlementComponentAllocation { CompanyId = companyId, SettlementKind = "AR", SettlementAllocationId = arReceiptAllocation1Id, InvoiceId = arInvoiceId, ComponentKind = SettlementComponentKind.InvoiceLine, InvoiceLineId = arLine2Id, AmountApplied = 40m },
                    new SettlementComponentAllocation { CompanyId = companyId, SettlementKind = "AR", SettlementAllocationId = arReceiptAllocation2Id, InvoiceId = arInvoice2Id, ComponentKind = SettlementComponentKind.InvoiceLine, InvoiceLineId = arInvoice2LineId, AmountApplied = 30m },
                    new SettlementComponentAllocation { CompanyId = companyId, SettlementKind = "AP", SettlementAllocationId = apPaymentAllocation1Id, InvoiceId = apInvoiceId, ComponentKind = SettlementComponentKind.InvoiceLine, InvoiceLineId = apLine1Id, AmountApplied = 120m },
                    new SettlementComponentAllocation { CompanyId = companyId, SettlementKind = "AP", SettlementAllocationId = apPaymentAllocation1Id, InvoiceId = apInvoiceId, ComponentKind = SettlementComponentKind.InvoiceLine, InvoiceLineId = apLine2Id, AmountApplied = 80m },
                    new SettlementComponentAllocation { CompanyId = companyId, SettlementKind = "AP", SettlementAllocationId = apPaymentAllocation2Id, InvoiceId = apInvoice2Id, ComponentKind = SettlementComponentKind.InvoiceLine, InvoiceLineId = apInvoice2LineId, AmountApplied = 60m });
                var version = new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "Cash economic classification",
                    EntityScope = "IPSAS AR AP Settlement MDA",
                    PeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    PeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = certifierId,
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                };
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = arBudgetLineId, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = revenueGlId, CostCentreId = costCentreId, LineNumber = 1,
                    Description = "Service revenue", ClassificationCode = "REV-SVC", Amount = 60m, CreatedBy = preparerId
                });
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = arBudgetLine2Id, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = revenueGl2Id, LineNumber = 2,
                    Description = "Other service revenue", ClassificationCode = "REV-OTHER", Amount = 70m, CreatedBy = preparerId
                });
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = apBudgetLineId, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = expenseGlId, CostCentreId = costCentreId, LineNumber = 3,
                    Description = "Operating supplies", ClassificationCode = "EXP-SUP", Amount = 120m, CreatedBy = preparerId
                });
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = apBudgetLine2Id, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = expenseGl2Id, LineNumber = 4,
                    Description = "Other operating supplies", ClassificationCode = "EXP-OTHER", Amount = 140m, CreatedBy = preparerId
                });
                seed.PublicSectorBudgetVersions.Add(version);
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var arActual = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                arBudgetLineId, PublicSectorBudgetActualEventKind.CashReceipt,
                PublicSectorBudgetActualSourceKind.ArReceipt, arReceiptId,
                effectiveDate, CashFlowCategory.Operating, preparerId);
            await service.SubmitBudgetExecutionActualAsync(companyId, arActual.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CertifyBudgetExecutionActualAsync(companyId, arActual.Id, certifierId));

            var arActual2 = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                arBudgetLine2Id, PublicSectorBudgetActualEventKind.CashReceipt,
                PublicSectorBudgetActualSourceKind.ArReceipt, arReceiptId,
                effectiveDate, CashFlowCategory.Operating, preparerId);
            var apActual = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                apBudgetLineId, PublicSectorBudgetActualEventKind.CashPayment,
                PublicSectorBudgetActualSourceKind.ApPayment, apPaymentId,
                effectiveDate, CashFlowCategory.Operating, preparerId);
            var apActual2 = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                apBudgetLine2Id, PublicSectorBudgetActualEventKind.CashPayment,
                PublicSectorBudgetActualSourceKind.ApPayment, apPaymentId,
                effectiveDate, CashFlowCategory.Operating, preparerId);

            Assert.Equal(60m, arActual.Amount);
            Assert.Equal(70m, arActual2.Amount);
            Assert.Equal("RCT-SETTLE-001", arActual.SourceReference);
            Assert.Equal("RCT-SETTLE-001", arActual2.SourceReference);
            Assert.Equal(120m, apActual.Amount);
            Assert.Equal(140m, apActual2.Amount);
            Assert.Equal("PAY-SETTLE-001", apActual.SourceReference);
            Assert.Equal(PublicSectorBudgetActualStatus.PendingCertification, arActual.Status);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, arActual2.Status);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, apActual.Status);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, apActual2.Status);

            foreach (var item in new[] { arActual2, apActual, apActual2 })
                await service.SubmitBudgetExecutionActualAsync(companyId, item.Id, preparerId);
            foreach (var item in new[] { arActual, arActual2, apActual, apActual2 })
                await service.CertifyBudgetExecutionActualAsync(companyId, item.Id, certifierId);

            // Cash actuals are not comparable until each registered source population has
            // independently reviewed control totals, record-level allocations and a certified
            // completeness attestation for the full reporting entity.
            var sourceReviewerId = Guid.NewGuid();
            var reconciliationPreparerId = Guid.NewGuid();
            var reconciliationReviewerId = Guid.NewGuid();
            var registerCertifierId = Guid.NewGuid();
            var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var arExpectedSource = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "ReceivablesControl", "AR-CONTROL-001", "Trade receivables control",
                arControlGlId.ToString(), true, periodStart, periodEnd, preparerId);
            var apExpectedSource = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "PayablesControl", "AP-CONTROL-001", "Trade payables control",
                apControlGlId.ToString(), true, periodStart, periodEnd, preparerId);
            foreach (var expectedSource in new[] { arExpectedSource, apExpectedSource })
            {
                await service.SubmitExpectedCashSourceAsync(companyId, expectedSource.Id, preparerId);
                await service.ApproveExpectedCashSourceAsync(companyId, expectedSource.Id, sourceReviewerId);
            }

            var arSubmission = await service.CreateCashSourceSubmissionAsync(companyId, arExpectedSource.Id,
                1, 130m, 0m, 30m, "USD", "AR control account statement and receipt listing", preparerId);
            var apSubmission = await service.CreateCashSourceSubmissionAsync(companyId, apExpectedSource.Id,
                1, 260m, 0m, 60m, "USD", "AP control account statement and payment listing", preparerId);
            foreach (var submission in new[] { arSubmission, apSubmission })
            {
                await service.SubmitCashSourceSubmissionAsync(companyId, submission.Id, preparerId);
                await service.ReviewCashSourceSubmissionAsync(companyId, submission.Id, sourceReviewerId,
                    true, "Source control totals independently agreed to supporting statement.");
            }

            await service.AddCashSourceReconciliationLineAsync(companyId, arSubmission.Id, arReceiptId,
                "RCT-SETTLE-001", arActual.Id, false, null, reconciliationPreparerId);
            await service.AddCashSourceReconciliationLineAsync(companyId, arSubmission.Id, arReceiptId,
                "RCT-SETTLE-001", arActual2.Id, false, null, reconciliationPreparerId);
            await service.AddCashSourceReconciliationLineAsync(companyId, apSubmission.Id, apPaymentId,
                "PAY-SETTLE-001", apActual.Id, false, null, reconciliationPreparerId);
            await service.AddCashSourceReconciliationLineAsync(companyId, apSubmission.Id, apPaymentId,
                "PAY-SETTLE-001", apActual2.Id, false, null, reconciliationPreparerId);
            foreach (var submission in new[] { arSubmission, apSubmission })
            {
                await service.SubmitCashSourceRecordReconciliationAsync(companyId, submission.Id, reconciliationPreparerId);
                await service.ReviewCashSourceRecordReconciliationAsync(companyId, submission.Id,
                    reconciliationReviewerId, true, "Every source transaction is fully allocated to certified budget actuals.");
            }

            var registerAttestation = await service.CreateExpectedCashSourceRegisterAttestationAsync(
                companyId, budgetVersionId, 2, "IPSAS AR AP Settlement MDA",
                "Signed register of AR and AP control accounts for the reporting entity", preparerId);
            await service.SubmitExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id, preparerId);
            await service.ReviewExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id,
                registerCertifierId, true, "Register completeness and all source populations independently confirmed.");

            var comparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(comparison.ComparableActualsAvailable, comparison.ComparabilityMessage);
            Assert.Equal(60m, comparison.Lines.Single(x => x.AccountCode == "4100").ActualOnComparableBasis);
            Assert.Equal(70m, comparison.Lines.Single(x => x.AccountCode == "4200").ActualOnComparableBasis);
            Assert.Equal(120m, comparison.Lines.Single(x => x.AccountCode == "5100").ActualOnComparableBasis);
            Assert.Equal(140m, comparison.Lines.Single(x => x.AccountCode == "5101").ActualOnComparableBasis);

            // A complete-looking set of certified actuals must not conceal additional posted
            // AR/AP settlements that have not been captured in the IPSAS 24 source ledger.
            var omittedArJournalId = Guid.NewGuid();
            var omittedApJournalId = Guid.NewGuid();
            await using (var omittedSources = db.NewContext(companyId))
            {
                omittedSources.JournalEntries.AddRange(
                    new JournalEntry
                    {
                        Id = omittedArJournalId, CompanyId = companyId, EntryNo = "JE-AR-OMITTED",
                        Description = "Omitted AR cash receipt", EntryDate = effectiveDate,
                        PeriodId = periodId, Source = JournalSource.AR, Status = JournalStatus.Posted
                    },
                    new JournalEntry
                    {
                        Id = omittedApJournalId, CompanyId = companyId, EntryNo = "JE-AP-OMITTED",
                        Description = "Omitted AP cash payment", EntryDate = effectiveDate,
                        PeriodId = periodId, Source = JournalSource.AP, Status = JournalStatus.Posted
                    });
                omittedSources.ArReceipts.Add(new ArReceipt
                {
                    Id = Guid.NewGuid(), CompanyId = companyId, CustomerId = customerId,
                    ReceiptNo = "RCT-OMITTED-001", ReceiptDate = effectiveDate,
                    Currency = "USD", Amount = 15m, JournalEntryId = omittedArJournalId
                });
                omittedSources.ApPayments.Add(new ApPayment
                {
                    Id = Guid.NewGuid(), CompanyId = companyId, SupplierId = supplierId,
                    PaymentNo = "PAY-OMITTED-001", PaymentDate = effectiveDate,
                    Currency = "USD", Amount = 20m, JournalEntryId = omittedApJournalId
                });
                await omittedSources.SaveChangesAsync();
            }

            var incompleteComparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(incompleteComparison.ComparableActualsAvailable);
            Assert.Contains("posted AR receipts", incompleteComparison.ComparabilityMessage,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("posted AP payments", incompleteComparison.ComparabilityMessage,
                StringComparison.OrdinalIgnoreCase);
        }

        [PostgresFact]
        public async Task Cash_period_zero_activity_requires_independent_attestation_and_rechecks_sources()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var certifierId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var bankAccountId = Guid.NewGuid();
            var bankGlId = Guid.NewGuid();
            var expenseGlId = Guid.NewGuid();
            var payableGlId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Zero Activity MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = bankGlId, CompanyId = companyId, Code = "1000", Name = "Operating Bank",
                    Type = GlAccountType.Asset, SubType = "BANK", Currency = "USD",
                    CashFlowCategory = CashFlowCategory.CashAndCashEquivalents
                });
                seed.BankAccounts.Add(new BankAccount
                {
                    Id = bankAccountId, CompanyId = companyId, Name = "Operating account",
                    AccountNo = "ZERO-OPERATING-01", BankName = "Test Bank",
                    Currency = "USD", GlAccountId = bankGlId
                });
                seed.GlAccounts.AddRange(
                    new GlAccount { Id = expenseGlId, CompanyId = companyId, Code = "6000",
                        Name = "Accrued operating expense", Type = GlAccountType.Expense,
                        CashFlowCategory = CashFlowCategory.NotApplicable },
                    new GlAccount { Id = payableGlId, CompanyId = companyId, Code = "2000",
                        Name = "Accrued operating payable", Type = GlAccountType.Liability,
                        CashFlowCategory = CashFlowCategory.Operating });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, PeriodNo = 6,
                    StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 7, 1),
                    Status = PeriodStatus.Open
                });
                seed.PublicSectorBudgetVersions.Add(new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash,
                    ClassificationBasis = "Cash economic classification",
                    EntityScope = "Zero Activity MDA", PeriodStart = periodStart, PeriodEnd = periodEnd,
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = certifierId,
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                });
                seed.Customers.Add(new Customer
                {
                    Id = customerId, CompanyId = companyId, Code = "ZERO-TEST-CUST",
                    Name = "Zero Activity Test Customer", Currency = "USD"
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var cashFlowService = new CashFlowStatementService(ctx, new ReportingService(ctx));
            var service = new PublicSectorService(ctx, new AuditService(ctx), cashFlowService);
            var blocked = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(blocked.ComparableActualsAvailable);
            Assert.Contains("zero-activity attestation", blocked.ComparabilityMessage, StringComparison.OrdinalIgnoreCase);

            var attestation = await service.CreateCashPeriodAttestationAsync(companyId,
                budgetVersionId, "MDA cashbook nil-return 2026", preparerId);
            Assert.Equal("Draft", attestation.Status);
            await service.SubmitCashPeriodAttestationAsync(companyId, attestation.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CertifyCashPeriodAttestationAsync(companyId, attestation.Id, preparerId));

            var certified = await service.CertifyCashPeriodAttestationAsync(companyId, attestation.Id, certifierId);
            Assert.Equal("Certified", certified.Status);
            Assert.Equal(0, certified.ObservedBankTransactionCount);
            Assert.Equal(0, certified.ObservedArReceiptCount);
            Assert.Equal(0, certified.ObservedApPaymentCount);
            Assert.True(await service.HasValidCertifiedZeroActivityAttestationAsync(companyId, budgetVersionId));

            var sourceReviewerId = Guid.NewGuid();
            var reconciliationPreparerId = Guid.NewGuid();
            var reconciliationReviewerId = Guid.NewGuid();
            var source = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "BankAccount", "ZERO-OPERATING-01", "Operating bank account",
                bankAccountId.ToString(), true, periodStart, periodEnd, preparerId);
            await service.SubmitExpectedCashSourceAsync(companyId, source.Id, preparerId);
            await service.ApproveExpectedCashSourceAsync(companyId, source.Id, sourceReviewerId);
            var submission = await service.CreateCashSourceSubmissionAsync(companyId, source.Id,
                0, 0m, 1000m, 1000m, "USD", "Bank statement nil-activity evidence", preparerId);
            await service.SubmitCashSourceSubmissionAsync(companyId, submission.Id, preparerId);
            await service.ReviewCashSourceSubmissionAsync(companyId, submission.Id, sourceReviewerId,
                true, "Zero bank transactions confirmed.");
            await service.SubmitCashSourceRecordReconciliationAsync(companyId, submission.Id,
                reconciliationPreparerId);
            await service.ReviewCashSourceRecordReconciliationAsync(companyId, submission.Id,
                reconciliationReviewerId, false, "Initial zero-record pack did not include the signed bank confirmation.");

            // A rejected record-level reconciliation remains immutable history, but a new
            // source submission revision can be prepared and independently reviewed.
            var revisedSubmission = await service.CreateCashSourceSubmissionAsync(companyId, source.Id,
                0, 0m, 1000m, 1000m, "USD", "Corrected signed bank statement nil-activity evidence", preparerId,
                default, "The initial record-level pack was rejected because signed bank confirmation was missing.");
            Assert.Equal(2, revisedSubmission.SubmissionRevision);
            await service.SubmitCashSourceSubmissionAsync(companyId, revisedSubmission.Id, preparerId);
            await service.ReviewCashSourceSubmissionAsync(companyId, revisedSubmission.Id, sourceReviewerId,
                true, "Corrected source control totals independently confirmed.");
            await service.SubmitCashSourceRecordReconciliationAsync(companyId, revisedSubmission.Id,
                reconciliationPreparerId);
            await service.ReviewCashSourceRecordReconciliationAsync(companyId, revisedSubmission.Id,
                reconciliationReviewerId, true, "Corrected zero-record population independently confirmed.");

            var registerAttestation = await service.CreateExpectedCashSourceRegisterAttestationAsync(
                companyId, budgetVersionId, 1, "Zero Activity MDA",
                "Signed bank account inventory for the reporting entity", preparerId);
            await service.SubmitExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id, preparerId);
            await service.ReviewExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id,
                certifierId, true, "All expected source populations are complete.");

            var comparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(comparison.ComparableActualsAvailable, comparison.ComparabilityMessage);

            async Task<PublicSectorBudgetReconciliation> PrepareCashFlowAdjustmentAsync(string section, string reference)
            {
                var item = await service.CreateAutomaticCashFlowBudgetReconciliationAsync(companyId,
                    2026, budgetVersionId, section, reference, preparerId);
                Assert.True(item.IsNoDifferenceAttestation);
                var workflowId = Guid.NewGuid();
                ctx.WorkflowRequests.Add(new WorkflowRequest
                {
                    Id = workflowId, CompanyId = companyId, RequestType = "PublicSectorBudgetReconciliation",
                    ReferenceId = item.Id, ReferenceTable = "PublicSectorBudgetReconciliation",
                    SubmittedBy = preparerId, Amount = 0m, Status = WorkflowStatus.Pending
                });
                await ctx.SaveChangesAsync();
                await service.MarkBudgetReconciliationPendingAsync(companyId, item.Id, workflowId);
                var workflow = await ctx.WorkflowRequests.SingleAsync(x => x.Id == workflowId);
                workflow.Status = WorkflowStatus.Approved;
                ctx.WorkflowApprovals.Add(new WorkflowApproval
                {
                    CompanyId = companyId, WorkflowRequestId = workflowId, Tier = 1,
                    ApproverId = certifierId, Approved = true, DecidedAt = DateTime.UtcNow
                });
                await ctx.SaveChangesAsync();
                return item;
            }

            var approvedOperating = await PrepareCashFlowAdjustmentAsync("Operating", "WP-CASHFLOW-OPERATING-NIL");
            await service.FinaliseBudgetReconciliationApprovalAsync(companyId, approvedOperating.Id, certifierId);
            Assert.Equal(PublicSectorBudgetReconciliationStatus.Approved, approvedOperating.Status);

            var pendingInvesting = await PrepareCashFlowAdjustmentAsync("Investing", "WP-CASHFLOW-INVESTING-NIL");
            Assert.Equal(PublicSectorBudgetReconciliationStatus.PendingApproval, pendingInvesting.Status);

            // A non-cash accrual changes the statement-of-cash-flows calculation without
            // changing the registered cash source population. Its changed statement detail
            // must still invalidate the prepared fingerprint.
            var nonCashJournal = new JournalEntry
            {
                CompanyId = companyId, EntryNo = "JE-IPSAS24-NONCASH-ACCRUAL",
                Description = "Accrued operating cost with no cash settlement",
                EntryDate = new DateTime(2026, 6, 30), PeriodId = periodId,
                Source = JournalSource.Manual, Status = JournalStatus.Posted
            };
            ctx.JournalEntries.Add(nonCashJournal);
            ctx.JournalLines.AddRange(
                new JournalLine { JournalEntryId = nonCashJournal.Id, GlAccountId = expenseGlId,
                    Debit = 10m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                    BaseDebit = 10m, BaseCredit = 0m },
                new JournalLine { JournalEntryId = nonCashJournal.Id, GlAccountId = payableGlId,
                    Debit = 0m, Credit = 10m, Currency = "USD", ExchangeRate = 1m,
                    BaseDebit = 0m, BaseCredit = 10m });
            await ctx.SaveChangesAsync();
            var changedEvidence = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.FinaliseBudgetReconciliationApprovalAsync(companyId, pendingInvesting.Id, certifierId));
            Assert.Contains("changed since preparation", changedEvidence.Message, StringComparison.OrdinalIgnoreCase);

            // Certification is not a permanent assertion that later source activity cannot occur.
            // The bridge and comparison must re-count source populations on every read.
            await using (var lateActivity = db.NewContext(companyId))
            {
                lateActivity.ArReceipts.Add(new ArReceipt
                {
                    Id = Guid.NewGuid(), CompanyId = companyId, CustomerId = customerId,
                    ReceiptNo = "POST-CERT-NIL-001",
                    ReceiptDate = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
                    Currency = "USD", Amount = 25m
                });
                await lateActivity.SaveChangesAsync();
            }

            Assert.False(await service.HasValidCertifiedZeroActivityAttestationAsync(companyId, budgetVersionId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.FinaliseBudgetReconciliationApprovalAsync(companyId, pendingInvesting.Id, certifierId));
            var staleComparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.False(staleComparison.ComparableActualsAvailable);
            Assert.Contains("no cash actuals were captured", staleComparison.ComparabilityMessage, StringComparison.OrdinalIgnoreCase);
        }

        [PostgresFact]
        public async Task Cash_period_attestation_is_rejected_when_source_activity_appears_after_submission()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var certifierId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Late Activity MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.PublicSectorBudgetVersions.Add(new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "Cash",
                    EntityScope = "Late Activity MDA", PeriodStart = periodStart, PeriodEnd = periodEnd,
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = certifierId,
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                });
                seed.Customers.Add(new Customer
                {
                    Id = customerId, CompanyId = companyId, Code = "NIL-TEST-CUST",
                    Name = "Nil Test Customer", Currency = "USD"
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var attestation = await service.CreateCashPeriodAttestationAsync(companyId,
                budgetVersionId, "Nil return evidence", preparerId);
            await service.SubmitCashPeriodAttestationAsync(companyId, attestation.Id, preparerId);

            await using (var activity = db.NewContext(companyId))
            {
                activity.ArReceipts.Add(new ArReceipt
                {
                    Id = Guid.NewGuid(), CompanyId = companyId, CustomerId = customerId,
                    ReceiptNo = "LATE-RECEIPT-001", ReceiptDate = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
                    Currency = "USD", Amount = 25m
                });
                await activity.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CertifyCashPeriodAttestationAsync(companyId, attestation.Id, certifierId));
            var rejected = await ctx.PublicSectorCashPeriodAttestations.SingleAsync(x => x.Id == attestation.Id);
            Assert.Equal("Rejected", rejected.Status);
            Assert.Contains("Source activity detected", rejected.RejectionReason);
        }

        [PostgresFact]
        public async Task Expected_cash_source_register_requires_independent_approval_and_is_immutable()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var approverId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var bankAccountId = Guid.NewGuid();
            var bankGlId = Guid.NewGuid();
            var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Expected Source MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = bankGlId, CompanyId = companyId, Code = "1000", Name = "Operating Bank",
                    Type = GlAccountType.Asset, SubType = "BANK", Currency = "USD",
                    CashFlowCategory = CashFlowCategory.CashAndCashEquivalents
                });
                seed.BankAccounts.Add(new BankAccount
                {
                    Id = bankAccountId, CompanyId = companyId, Name = "Operating account",
                    AccountNo = "BANK-OPERATING-01", BankName = "Test Bank",
                    Currency = "USD", GlAccountId = bankGlId
                });
                seed.PublicSectorBudgetVersions.Add(new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "Cash economic classification",
                    EntityScope = "Expected Source MDA", PeriodStart = periodStart, PeriodEnd = periodEnd,
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = approverId,
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var source = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "BankAccount", "BANK-OPERATING-01", "Operating account", bankAccountId.ToString(),
                true, periodStart, periodEnd, preparerId);
            Assert.Equal("Draft", source.Status);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveExpectedCashSourceAsync(companyId, source.Id, preparerId));
            await service.SubmitExpectedCashSourceAsync(companyId, source.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveExpectedCashSourceAsync(companyId, source.Id, preparerId));

            var approved = await service.ApproveExpectedCashSourceAsync(companyId, source.Id, approverId);
            Assert.Equal("Approved", approved.Status);
            Assert.Equal(approverId, approved.ApprovedBy);
            Assert.NotNull(approved.ApprovedAt);

            var register = await service.GetExpectedCashSourcesAsync(companyId, budgetVersionId);
            Assert.Single(register);
            Assert.Equal("BANK-OPERATING-01", register[0].SourceCode);

            // This source has no activity in the period. Its zero control total is only accepted
            // after the separate record-level reconciliation is independently reviewed.
            var submission = await service.CreateCashSourceSubmissionAsync(companyId, approved.Id,
                0, 0m, 1000m, 1000m, "USD", "Bank statement and reconciliation pack", preparerId);
            Assert.Equal("Draft", submission.Status);
            await service.SubmitCashSourceSubmissionAsync(companyId, submission.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReviewCashSourceSubmissionAsync(companyId, submission.Id, preparerId, true, null));
            var reviewed = await service.ReviewCashSourceSubmissionAsync(companyId, submission.Id,
                approverId, true, "Statement control totals agreed.");
            Assert.Equal("Reviewed", reviewed.Status);
            Assert.Equal(approverId, reviewed.ReviewedBy);

            var reconciliationPreparerId = Guid.NewGuid();
            var reconciliationReviewerId = Guid.NewGuid();
            var submittedReconciliation = await service.SubmitCashSourceRecordReconciliationAsync(
                companyId, submission.Id, reconciliationPreparerId);
            Assert.Equal("PendingReview", submittedReconciliation.RecordLevelStatus);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReviewCashSourceRecordReconciliationAsync(companyId, submission.Id,
                    reconciliationPreparerId, true, null));
            var reviewedReconciliation = await service.ReviewCashSourceRecordReconciliationAsync(
                companyId, submission.Id, reconciliationReviewerId, true, "Zero source records confirmed against the reviewed bank statement.");
            Assert.Equal("Reviewed", reviewedReconciliation.RecordLevelStatus);
            Assert.Equal(reconciliationReviewerId, reviewedReconciliation.RecordLevelReviewedBy);

            var registerAttestation = await service.CreateExpectedCashSourceRegisterAttestationAsync(
                companyId, budgetVersionId, 1, "Expected Source MDA",
                "Signed management bank-account inventory", preparerId);
            Assert.Equal("Draft", registerAttestation.Status);
            await service.SubmitExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id, preparerId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReviewExpectedCashSourceRegisterAttestationAsync(companyId, registerAttestation.Id,
                    preparerId, true, null));
            var registerCertified = await service.ReviewExpectedCashSourceRegisterAttestationAsync(
                companyId, registerAttestation.Id, Guid.NewGuid(), true,
                "Register completeness independently checked against the signed inventory.");
            Assert.Equal("Certified", registerCertified.Status);
            Assert.NotNull(registerCertified.CertifiedAt);

            // Database triggers protect both approved register history and independently reviewed controls.
            reviewed.ControlTotal = 999999m;
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());

            await using var immutableContext = db.NewContext(companyId);
            var immutableSource = await immutableContext.PublicSectorExpectedCashSources.SingleAsync(x => x.Id == approved.Id);
            immutableSource.SourceName = "Silently altered account";
            await Assert.ThrowsAsync<DbUpdateException>(() => immutableContext.SaveChangesAsync());

            await using var frozenContext = db.NewContext(companyId);
            // Bypass application validation to exercise the database trigger itself:
            // a certified completeness attestation must prevent direct inserts too.
            frozenContext.PublicSectorExpectedCashSources.Add(new PublicSectorExpectedCashSource
            {
                CompanyId = companyId, BudgetVersionId = budgetVersionId,
                SourceKind = "BankAccount", SourceCode = "BANK-SECONDARY-01",
                SourceName = "Secondary account", SourceIdentifier = approved.SourceIdentifier,
                IsRequired = true, EffectiveFrom = periodStart, EffectiveTo = periodEnd,
                Status = "Draft", CreatedBy = preparerId
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => frozenContext.SaveChangesAsync());
        }

        [PostgresFact]
        public async Task Ipsas24_cash_actual_translates_foreign_currency_with_immutable_rate_provenance()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var bankAccountId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var journalId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var budgetLineId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var certifierId = Guid.NewGuid();
            var effectiveDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "IPSAS FX Cash MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-FX",
                    Name = "FX Vote", Level = FundCentreLevel.Vote, FinancialYear = 2026
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = accountId, CompanyId = companyId, Code = "5102",
                    Name = "Foreign Currency Operating Expenditure",
                    Type = GlAccountType.Expense, SubType = "EC-FX",
                    Currency = "USD", CashFlowCategory = CashFlowCategory.Operating
                });
                seed.BankAccounts.Add(new BankAccount
                {
                    Id = bankAccountId, CompanyId = companyId, Name = "ZWG Operating Bank",
                    AccountNo = "FX-TEST-001", BankName = "Test Bank",
                    Currency = "ZWG", GlAccountId = accountId
                });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 6,
                    StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                    Status = PeriodStatus.Open
                });
                seed.JournalEntries.Add(new JournalEntry
                {
                    Id = journalId, CompanyId = companyId, EntryNo = "JE-IPSAS24-FX",
                    Description = "Foreign currency cash execution", EntryDate = effectiveDate,
                    PeriodId = periodId, Source = JournalSource.Bank, Status = JournalStatus.Posted
                });
                seed.BankTransactions.Add(new BankTransaction
                {
                    Id = transactionId, CompanyId = companyId, BankAccountId = bankAccountId,
                    TransactionDate = effectiveDate, Description = "Foreign currency supplier payment",
                    Debit = 500m, Credit = 0m, Reference = "ZWG-BANK-001",
                    IsReconciled = true, JournalEntryId = journalId
                });
                seed.JournalLines.Add(new JournalLine
                {
                    JournalEntryId = journalId, GlAccountId = accountId, FundCentreId = fundCentreId,
                    Debit = 500m, Credit = 0m, Currency = "ZWG", ExchangeRate = 0.02m,
                    BaseDebit = 10m, BaseCredit = 0m
                });
                seed.ExchangeRates.Add(new ExchangeRate
                {
                    CompanyId = companyId, FromCurrency = "ZWG", ToCurrency = "USD",
                    RateDate = effectiveDate, Rate = 0.02m, IsManualOverride = false,
                    RateSource = "RBZ published rate", SourceReference = "RBZ-DAILY-2026-06-30",
                    CreatedBy = preparerId
                });
                var version = new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "MDA cash classification",
                    EntityScope = "IPSAS FX Cash MDA",
                    PeriodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    PeriodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = certifierId,
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                };
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = budgetLineId, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = accountId, LineNumber = 1,
                    Description = "Foreign currency operating expenditure",
                    ClassificationCode = "EC-FX", Amount = 100m, CreatedBy = preparerId
                });
                seed.PublicSectorBudgetVersions.Add(version);
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                    budgetLineId, PublicSectorBudgetActualEventKind.CashPayment,
                    PublicSectorBudgetActualSourceKind.BankTransaction, transactionId,
                    effectiveDate, CashFlowCategory.Operating, preparerId));

            var rate = await ctx.ExchangeRates.SingleAsync(x =>
                x.CompanyId == companyId && x.FromCurrency == "ZWG" && x.ToCurrency == "USD");
            rate.ApprovedBy = certifierId;
            rate.ApprovedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            var actual = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                budgetLineId, PublicSectorBudgetActualEventKind.CashPayment,
                PublicSectorBudgetActualSourceKind.BankTransaction, transactionId,
                effectiveDate, CashFlowCategory.Operating, preparerId);

            Assert.Equal(500m, actual.SourceAmount);
            Assert.Equal(10m, actual.Amount);
            Assert.Equal(0.02m, actual.ExchangeRateToBudgetCurrency);
            Assert.Equal(effectiveDate.Date, actual.ExchangeRateDate!.Value.Date);
            Assert.Equal("RBZ published rate", actual.ExchangeRateSource);
            Assert.Equal("RBZ-DAILY-2026-06-30", actual.ExchangeRateSourceReference);
            Assert.False(actual.ExchangeRateIsInverse);
            Assert.Equal(certifierId, actual.ExchangeRateApprovedBy);
            Assert.NotNull(actual.ExchangeRateApprovedAt);

            await service.SubmitBudgetExecutionActualAsync(companyId, actual.Id, preparerId);
            await service.CertifyBudgetExecutionActualAsync(companyId, actual.Id, certifierId);
            Assert.True(await service.IsCertifiedCashActualSourceValidAsync(companyId, actual, default));

            rate.Rate = 0.03m;
            await ctx.SaveChangesAsync();
            Assert.False(await service.IsCertifiedCashActualSourceValidAsync(companyId, actual, default));
        }

        [PostgresFact]
        public async Task Ipsas24_ar_ap_opening_and_closing_balances_must_reconcile_to_gl_control_accounts()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var openingPeriodId = Guid.NewGuid();
            var arControlId = Guid.NewGuid();
            var apControlId = Guid.NewGuid();
            var revenueId = Guid.NewGuid();
            var expenseId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var arOpeningJournalId = Guid.NewGuid();
            var arClosingJournalId = Guid.NewGuid();
            var apOpeningJournalId = Guid.NewGuid();
            var apClosingJournalId = Guid.NewGuid();
            var arLateJournalId = Guid.NewGuid();
            var apLateJournalId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var sourceReviewerId = Guid.NewGuid();
            var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "IPSAS AR AP Control MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.AccountingPeriods.AddRange(
                    new AccountingPeriod
                    {
                        Id = openingPeriodId, CompanyId = companyId, Year = 2026, PeriodNo = 1,
                        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        EndDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                        Status = PeriodStatus.Open
                    },
                    new AccountingPeriod
                    {
                        Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 6,
                        StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                        EndDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                        Status = PeriodStatus.Open
                    });
                seed.GlAccounts.AddRange(
                    new GlAccount
                    {
                        Id = arControlId, CompanyId = companyId, Code = "1200",
                        Name = "Trade Receivables Control", Type = GlAccountType.Asset,
                        Currency = "USD", SubType = "AR-CONTROL"
                    },
                    new GlAccount
                    {
                        Id = apControlId, CompanyId = companyId, Code = "2100",
                        Name = "Trade Payables Control", Type = GlAccountType.Liability,
                        Currency = "USD", SubType = "AP-CONTROL"
                    },
                    new GlAccount
                    {
                        Id = revenueId, CompanyId = companyId, Code = "4100",
                        Name = "Service Revenue", Type = GlAccountType.Income,
                        Currency = "USD", SubType = "REV"
                    },
                    new GlAccount
                    {
                        Id = expenseId, CompanyId = companyId, Code = "5100",
                        Name = "Operating Expense", Type = GlAccountType.Expense,
                        Currency = "USD", SubType = "EXP"
                    });
                seed.Customers.Add(new Customer
                {
                    Id = customerId, CompanyId = companyId, Code = "C-AR-01",
                    Name = "AR Control Customer", Currency = "USD", ArAccountId = arControlId
                });
                seed.Suppliers.Add(new Supplier
                {
                    Id = supplierId, CompanyId = companyId, Code = "S-AP-01",
                    Name = "AP Control Supplier", Currency = "USD", ApAccountId = apControlId
                });
                seed.JournalEntries.AddRange(
                    new JournalEntry
                    {
                        Id = arOpeningJournalId, CompanyId = companyId, EntryNo = "JE-AR-OPEN",
                        Description = "Opening AR invoice", EntryDate = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                        PeriodId = openingPeriodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                    },
                    new JournalEntry
                    {
                        Id = arClosingJournalId, CompanyId = companyId, EntryNo = "JE-AR-CLOSE",
                        Description = "In-period AR invoice", EntryDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                        PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                    },
                    new JournalEntry
                    {
                        Id = apOpeningJournalId, CompanyId = companyId, EntryNo = "JE-AP-OPEN",
                        Description = "Opening AP invoice", EntryDate = new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc),
                        PeriodId = openingPeriodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                    },
                    new JournalEntry
                    {
                        Id = apClosingJournalId, CompanyId = companyId, EntryNo = "JE-AP-CLOSE",
                        Description = "In-period AP invoice", EntryDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
                        PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                    });
                seed.JournalLines.AddRange(
                    new JournalLine { JournalEntryId = arOpeningJournalId, GlAccountId = arControlId, Debit = 100m, Credit = 0m, Currency = "USD" },
                    new JournalLine { JournalEntryId = arOpeningJournalId, GlAccountId = revenueId, Debit = 0m, Credit = 100m, Currency = "USD" },
                    new JournalLine { JournalEntryId = arClosingJournalId, GlAccountId = arControlId, Debit = 50m, Credit = 0m, Currency = "USD" },
                    new JournalLine { JournalEntryId = arClosingJournalId, GlAccountId = revenueId, Debit = 0m, Credit = 50m, Currency = "USD" },
                    new JournalLine { JournalEntryId = apOpeningJournalId, GlAccountId = expenseId, Debit = 60m, Credit = 0m, Currency = "USD" },
                    new JournalLine { JournalEntryId = apOpeningJournalId, GlAccountId = apControlId, Debit = 0m, Credit = 60m, Currency = "USD" },
                    new JournalLine { JournalEntryId = apClosingJournalId, GlAccountId = expenseId, Debit = 30m, Credit = 0m, Currency = "USD" },
                    new JournalLine { JournalEntryId = apClosingJournalId, GlAccountId = apControlId, Debit = 0m, Credit = 30m, Currency = "USD" });
                seed.ArInvoices.AddRange(
                    new ArInvoice
                    {
                        CompanyId = companyId, CustomerId = customerId, InvoiceNo = "AR-OPEN",
                        InvoiceDate = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                        DueDate = new DateTime(2026, 2, 9, 0, 0, 0, DateTimeKind.Utc),
                        Currency = "USD", Total = 100m, Outstanding = 100m,
                        Status = InvoiceStatus.Posted, JournalEntryId = arOpeningJournalId
                    },
                    new ArInvoice
                    {
                        CompanyId = companyId, CustomerId = customerId, InvoiceNo = "AR-CLOSE",
                        InvoiceDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                        DueDate = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
                        Currency = "USD", Total = 50m, Outstanding = 50m,
                        Status = InvoiceStatus.Posted, JournalEntryId = arClosingJournalId
                    });
                seed.ApInvoices.AddRange(
                    new ApInvoice
                    {
                        CompanyId = companyId, SupplierId = supplierId, InvoiceNo = "AP-OPEN",
                        InvoiceDate = new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc),
                        DueDate = new DateTime(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc),
                        Currency = "USD", Total = 60m, Outstanding = 60m,
                        Status = InvoiceStatus.Posted, JournalEntryId = apOpeningJournalId
                    },
                    new ApInvoice
                    {
                        CompanyId = companyId, SupplierId = supplierId, InvoiceNo = "AP-CLOSE",
                        InvoiceDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
                        DueDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
                        Currency = "USD", Total = 30m, Outstanding = 30m,
                        Status = InvoiceStatus.Posted, JournalEntryId = apClosingJournalId
                    });
                seed.PublicSectorBudgetVersions.Add(new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "MDA cash classification",
                    EntityScope = "IPSAS AR AP Control MDA", PeriodStart = periodStart, PeriodEnd = periodEnd,
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = Guid.NewGuid(),
                    ApprovedAt = DateTime.UtcNow, CreatedBy = preparerId
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var service = new PublicSectorService(ctx, new AuditService(ctx));
            var arExpected = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "ReceivablesControl", "AR-CONTROL", "Trade receivables control account",
                arControlId.ToString(), true, periodStart, periodEnd, preparerId);
            var apExpected = await service.CreateExpectedCashSourceAsync(companyId, budgetVersionId,
                "PayablesControl", "AP-CONTROL", "Trade payables control account",
                apControlId.ToString(), true, periodStart, periodEnd, preparerId);
            foreach (var expected in new[] { arExpected, apExpected })
            {
                await service.SubmitExpectedCashSourceAsync(companyId, expected.Id, preparerId);
                await service.ApproveExpectedCashSourceAsync(companyId, expected.Id, sourceReviewerId);
            }

            var arSubmission = await service.CreateCashSourceSubmissionAsync(companyId, arExpected.Id,
                0, 0m, 100m, 150m, "USD", "AR control account opening and closing trial-balance workpaper", preparerId);
            var apSubmission = await service.CreateCashSourceSubmissionAsync(companyId, apExpected.Id,
                0, 0m, 60m, 90m, "USD", "AP control account opening and closing trial-balance workpaper", preparerId);
            await service.SubmitCashSourceSubmissionAsync(companyId, arSubmission.Id, preparerId);
            await service.SubmitCashSourceSubmissionAsync(companyId, apSubmission.Id, preparerId);

            ctx.JournalEntries.AddRange(
                new JournalEntry
                {
                    Id = arLateJournalId, CompanyId = companyId, EntryNo = "JE-AR-LATE",
                    Description = "Late AR control adjustment", EntryDate = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc),
                    PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                },
                new JournalEntry
                {
                    Id = apLateJournalId, CompanyId = companyId, EntryNo = "JE-AP-LATE",
                    Description = "Late AP control adjustment", EntryDate = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc),
                    PeriodId = periodId, Source = JournalSource.Manual, Status = JournalStatus.Posted
                });
            ctx.JournalLines.AddRange(
                new JournalLine { JournalEntryId = arLateJournalId, GlAccountId = arControlId, Debit = 5m, Credit = 0m, Currency = "USD" },
                new JournalLine { JournalEntryId = arLateJournalId, GlAccountId = revenueId, Debit = 0m, Credit = 5m, Currency = "USD" },
                new JournalLine { JournalEntryId = apLateJournalId, GlAccountId = expenseId, Debit = 5m, Credit = 0m, Currency = "USD" },
                new JournalLine { JournalEntryId = apLateJournalId, GlAccountId = apControlId, Debit = 0m, Credit = 5m, Currency = "USD" });
            await ctx.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReviewCashSourceSubmissionAsync(companyId, arSubmission.Id, sourceReviewerId,
                    true, "AR control balances independently reviewed."));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReviewCashSourceSubmissionAsync(companyId, apSubmission.Id, sourceReviewerId,
                    true, "AP control balances independently reviewed."));
        }
        [PostgresFact]
        public async Task Ipsas24_statement_issue_requires_three_independent_stages_and_stable_snapshot()
        {
            await using var db = await TestDb.CreateAsync();
            var companyId = Guid.NewGuid();
            var fundCentreId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var budgetLineId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var preparerId = Guid.NewGuid();
            var reviewerId = Guid.NewGuid();
            var approverId = Guid.NewGuid();
            var publisherId = Guid.NewGuid();
            var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await using (var seed = db.NewContext(companyId))
            {
                seed.Companies.Add(new Company
                {
                    Id = companyId, Name = "Statement Issue MDA",
                    BaseCurrency = "USD", FunctionalCurrency = "USD"
                });
                seed.FundCentres.Add(new FundCentre
                {
                    Id = fundCentreId, CompanyId = companyId, Code = "VOTE-ISSUE",
                    Name = "Statement Issue Vote", Level = FundCentreLevel.Vote,
                    FinancialYear = 2026, AppropriationAmount = 100m
                });
                seed.GlAccounts.Add(new GlAccount
                {
                    Id = accountId, CompanyId = companyId, Code = "6100",
                    Name = "Operating expense", SubType = "EC-01",
                    Type = GlAccountType.Expense, Currency = "USD", IsActive = true
                });
                seed.PublicSectorBudgetVersions.Add(new PublicSectorBudgetVersion
                {
                    Id = budgetVersionId, CompanyId = companyId, FinancialYear = 2026,
                    VersionNumber = 1, Kind = PublicSectorBudgetVersionKind.Original,
                    Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                    AccountingBasis = AccountingBasis.Accrual, ClassificationBasis = "IPSAS accrual",
                    EntityScope = "Statement Issue MDA", PeriodStart = periodStart, PeriodEnd = periodEnd,
                    AuthorityReference = "Appropriation Act 2026", ApprovedBy = reviewerId,
                    ApprovedAt = DateTime.UtcNow
                });
                seed.PublicSectorBudgetVersionLines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = budgetLineId, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = accountId, LineNumber = 1,
                    Description = "Operating expense", ClassificationCode = "EC-01", Amount = 100m,
                    NoteReference = "Note 1"
                });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyId, Year = 2026, PeriodNo = 6,
                    StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 7, 1),
                    Status = PeriodStatus.Open
                });
                var journal = new JournalEntry
                {
                    CompanyId = companyId, EntryNo = "JE-IPSAS24-ISSUE",
                    Description = "Actual expenditure for statement issue",
                    EntryDate = new DateTime(2026, 6, 30), PeriodId = periodId,
                    Source = JournalSource.Manual, Status = JournalStatus.Posted
                };
                seed.JournalEntries.Add(journal);
                seed.JournalLines.Add(new JournalLine
                {
                    JournalEntryId = journal.Id, GlAccountId = accountId, FundCentreId = fundCentreId,
                    Debit = 100m, Credit = 0m, Currency = "USD", ExchangeRate = 1m,
                    BaseDebit = 100m, BaseCredit = 0m
                });
                await seed.SaveChangesAsync();
            }

            await using var ctx = db.NewContext(companyId);
            var audit = new AuditService(ctx);
            var cashFlow = new CashFlowStatementService(ctx, new ReportingService(ctx));
            var publicSector = new PublicSectorService(ctx, audit, cashFlow);
            var controller = new PublicSectorController(ctx, publicSector, null!, null!, null!, cashFlow, audit);
            void SetActor(Guid actorId)
            {
                var http = new DefaultHttpContext();
                http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("company_id", companyId.ToString()),
                    new Claim("sub", actorId.ToString())
                }, "integration-test"));
                controller.ControllerContext = new ControllerContext { HttpContext = http };
            }

            var readyReport = await publicSector.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(readyReport.ComparableActualsAvailable, readyReport.ComparabilityMessage);
            Assert.True(readyReport.VarianceExplanationsComplete);
            Assert.True(readyReport.OriginalToFinalExplanationsComplete);
            Assert.True(readyReport.FinancialStatementReconciliation.Complete);

            SetActor(preparerId);
            var create = await controller.CreateBudgetStatementIssue(new CreateBudgetStatementIssueRequest(
                2026, null, null, null, "Entity-approved financial reporting template", "2026.1",
                "IPSAS24-ISSUE-2026-001"), default);
            var draft = Assert.IsType<BudgetStatementIssueDocument>(Assert.IsType<OkObjectResult>(create).Value);
            Assert.Equal("Draft", draft.Status);
            Assert.Equal(64, draft.SnapshotSha256.Length);
            Assert.Equal(preparerId, draft.PreparedBy);

            SetActor(preparerId);
            var selfReview = await controller.ReviewBudgetStatementIssue(draft.Id,
                new ReviewBudgetStatementIssueRequest("Treasury template verification ref", "Reviewed template."), default);
            Assert.IsType<BadRequestObjectResult>(selfReview);

            SetActor(reviewerId);
            var reviewed = Assert.IsType<BudgetStatementIssueDocument>(Assert.IsType<OkObjectResult>(
                await controller.ReviewBudgetStatementIssue(draft.Id,
                    new ReviewBudgetStatementIssueRequest("Treasury circular / template verification ref",
                        "Verified the supplied template reference and statement presentation."), default)).Value);
            Assert.Equal("Reviewed", reviewed.Status);
            Assert.Equal("IndependentlyVerified", reviewed.TemplateReferenceStatus);
            Assert.Equal(draft.SnapshotSha256, reviewed.SnapshotSha256);

            SetActor(reviewerId);
            var selfApproval = await controller.ApproveBudgetStatementIssue(draft.Id,
                new ApproveBudgetStatementIssueRequest("Approval minute 01", "Approved."), default);
            Assert.IsType<BadRequestObjectResult>(selfApproval);

            SetActor(approverId);
            var approved = Assert.IsType<BudgetStatementIssueDocument>(Assert.IsType<OkObjectResult>(
                await controller.ApproveBudgetStatementIssue(draft.Id,
                    new ApproveBudgetStatementIssueRequest("Approval minute 02",
                        "Approved for issue after independent review."), default)).Value);
            Assert.Equal("Approved", approved.Status);
            Assert.Equal(draft.SnapshotSha256, approved.SnapshotSha256);

            SetActor(approverId);
            var selfPublish = await controller.PublishBudgetStatementIssue(draft.Id,
                new PublishBudgetStatementIssueRequest("Publication register 01", "Issued."), default);
            Assert.IsType<BadRequestObjectResult>(selfPublish);

            SetActor(publisherId);
            var issued = Assert.IsType<BudgetStatementIssueDocument>(Assert.IsType<OkObjectResult>(
                await controller.PublishBudgetStatementIssue(draft.Id,
                    new PublishBudgetStatementIssueRequest("Publication register 02",
                        "Published to the entity's approved reporting repository."), default)).Value);
            Assert.Equal("Issued", issued.Status);
            Assert.Equal(preparerId, issued.PreparedBy);
            Assert.Equal(reviewerId, issued.ReviewedBy);
            Assert.Equal(approverId, issued.ApprovedBy);
            Assert.Equal(publisherId, issued.IssuedBy);
            Assert.Equal("Treasury circular / template verification ref", issued.TemplateVerificationReference);
            Assert.Equal("Approval minute 02", issued.ApprovalReference);
            Assert.Equal("Publication register 02", issued.PublicationReference);
            Assert.Equal(draft.SnapshotJson, issued.SnapshotJson);
            Assert.Equal(draft.SnapshotSha256, issued.SnapshotSha256);
        }

    }
}
