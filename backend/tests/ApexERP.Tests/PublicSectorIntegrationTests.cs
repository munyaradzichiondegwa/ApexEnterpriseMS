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
                    new GlAccount { Id = addedExpenseId, CompanyId = companyId, Code = "5200", Name = "New Programme Expense", SubType = "EC-02", Type = GlAccountType.Expense, Currency = "USD" });
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
            var original = await service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Original, "USD", AccountingBasis.Accrual,
                "Tenant approved programme/economic classification", "IPSAS 24 MDA",
                periodStart, periodEnd, "Appropriation Act 2026", null,
                new[]
                {
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, expenseId, null, "Operating Expense", "EC-01", 100m),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, incomeId, null, "Service Revenue", "REV-01", 50m)
                }, Guid.NewGuid());
            original.Status = PublicSectorBudgetVersionStatus.Approved;
            original.ApprovedBy = Guid.NewGuid();
            original.ApprovedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            var revised = await service.CreateBudgetVersionAsync(companyId, 2026,
                PublicSectorBudgetVersionKind.Revised, "USD", AccountingBasis.Accrual,
                "Tenant approved programme/economic classification", "IPSAS 24 MDA",
                periodStart, periodEnd, "Supplementary Appropriation 2026",
                "Supplementary funding and reprioritisation approved.",
                new[]
                {
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, expenseId, null, "Operating Expense", "EC-01", 120m),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, incomeId, null, "Service Revenue", "REV-01", 70m),
                    new PublicSectorService.BudgetVersionLineInput(fundCentreId, addedExpenseId, null, "New Programme Expense", "EC-02", 30m)
                }, Guid.NewGuid());
            revised.Status = PublicSectorBudgetVersionStatus.Approved;
            revised.ApprovedBy = Guid.NewGuid();
            revised.ApprovedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            var preparerId = Guid.NewGuid();
            var approverId = Guid.NewGuid();
            var reconciliation = await service.CreateBudgetReconciliationAsync(companyId, 2026,
                PublicSectorBudgetReconciliationType.Basis, "Modified cash commitments not yet paid",
                "Operating", 5m, -2m, 0m, "Reconciliation workpaper WP-IPSAS24-01", preparerId);
            var workflowId = Guid.NewGuid();
            ctx.WorkflowRequests.Add(new WorkflowRequest
            {
                Id = workflowId, CompanyId = companyId, RequestType = "PublicSectorBudgetReconciliation",
                ReferenceId = reconciliation.Id, ReferenceTable = "PublicSectorBudgetReconciliation",
                SubmittedBy = preparerId, Amount = 7m, Status = WorkflowStatus.Pending
            });
            await ctx.SaveChangesAsync();
            await service.MarkBudgetReconciliationPendingAsync(companyId, reconciliation.Id, workflowId);
            var workflow = await ctx.WorkflowRequests.SingleAsync(x => x.Id == workflowId);
            workflow.Status = WorkflowStatus.Approved;
            ctx.WorkflowApprovals.Add(new WorkflowApproval
            {
                CompanyId = companyId, WorkflowRequestId = workflowId, Tier = 1,
                ApproverId = approverId, Approved = true, DecidedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
            await service.FinaliseBudgetReconciliationApprovalAsync(companyId, reconciliation.Id, approverId);

            var report = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.Equal(original.Id, report.OriginalBudgetVersionId);
            Assert.Equal(revised.Id, report.FinalBudgetVersionId);
            Assert.True(report.FinalBudgetAvailable);
            Assert.True(report.ComparableActualsAvailable, report.ComparabilityMessage);
            Assert.Equal(3, report.Lines.Count);
            var expense = Assert.Single(report.Lines, x => x.AccountCode == "5100");
            Assert.Equal(100m, expense.OriginalBudget);
            Assert.Equal(120m, expense.FinalBudget);
            Assert.Equal(60m, expense.ActualOnComparableBasis);
            Assert.Equal(60m, expense.Variance);
            var income = Assert.Single(report.Lines, x => x.AccountCode == "4100");
            Assert.Equal(20m, income.ActualOnComparableBasis);
            var added = Assert.Single(report.Lines, x => x.AccountCode == "5200");
            Assert.Equal(0m, added.OriginalBudget);
            Assert.Equal(30m, added.FinalBudget);
            Assert.Equal(10m, added.ActualOnComparableBasis);
            var approvedRecon = Assert.Single(report.Reconciliations);
            Assert.Equal(PublicSectorBudgetReconciliationStatus.Approved, approvedRecon.Status);
            Assert.Equal(5m, approvedRecon.OperatingAmount);
            Assert.Equal(-2m, approvedRecon.InvestingAmount);
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
            Assert.Contains("Load and approve actuals on the budget basis", report.ComparabilityMessage);
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
            var bankTransactionId = Guid.NewGuid();
            var unmappedBankTransactionId = Guid.NewGuid();
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
                seed.JournalEntries.Add(new JournalEntry
                {
                    Id = journalId, CompanyId = companyId, EntryNo = "JE-CASH-ACT",
                    Description = "Reconciled cash execution", EntryDate = effectiveDate,
                    PeriodId = periodId, Source = JournalSource.Bank, Status = JournalStatus.Posted
                });
                seed.BankTransactions.Add(new BankTransaction
                {
                    Id = bankTransactionId, CompanyId = companyId, BankAccountId = bankAccountId,
                    TransactionDate = effectiveDate, Description = "Verified supplier payment",
                    Debit = 75m, Credit = 0m, Reference = "BANK-REF-001",
                    IsReconciled = true, JournalEntryId = journalId
                });
                seed.BankTransactions.Add(new BankTransaction
                {
                    Id = unmappedBankTransactionId, CompanyId = companyId, BankAccountId = bankAccountId,
                    TransactionDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
                    Description = "Unmapped bank movement",
                    Debit = 25m, Credit = 0m, Reference = "BANK-REF-002",
                    IsReconciled = true, JournalEntryId = null
                });
                seed.JournalLines.Add(new JournalLine
                {
                    JournalEntryId = journalId, GlAccountId = accountId, FundCentreId = fundCentreId,
                    Debit = 75m, Credit = 0m, BaseDebit = 75m, BaseCredit = 0m
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

            // Cash-basis IPSAS 24 comparison must use certified source actuals, never
            // accrual GL totals. An unlinked bank movement is not treated as a posted
            // cash event and remains ineligible for capture.
            var comparison = await service.GetBudgetComparisonAsync(companyId, 2026);
            Assert.True(comparison.ComparableActualsAvailable, comparison.ComparabilityMessage);
            var comparisonLine = Assert.Single(comparison.Lines);
            Assert.Equal(75m, comparisonLine.ActualOnComparableBasis);

            // An unlinked bank statement movement inside the budget period must block
            // comparability rather than disappear from the source-coverage denominator.
            var unlinkedMovement = await ctx.BankTransactions.SingleAsync(x => x.Id == unmappedBankTransactionId);
            unlinkedMovement.TransactionDate = effectiveDate;
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
            var bankGlId = Guid.NewGuid();
            var revenueGlId = Guid.NewGuid();
            var expenseGlId = Guid.NewGuid();
            var bankAccountId = Guid.NewGuid();
            var periodId = Guid.NewGuid();
            var arInvoiceJournalId = Guid.NewGuid();
            var arReceiptJournalId = Guid.NewGuid();
            var apInvoiceJournalId = Guid.NewGuid();
            var apPaymentJournalId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var arInvoiceId = Guid.NewGuid();
            var arReceiptId = Guid.NewGuid();
            var apInvoiceId = Guid.NewGuid();
            var apPaymentId = Guid.NewGuid();
            var budgetVersionId = Guid.NewGuid();
            var arBudgetLineId = Guid.NewGuid();
            var apBudgetLineId = Guid.NewGuid();
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
                seed.GlAccounts.AddRange(
                    new GlAccount
                    {
                        Id = bankGlId, CompanyId = companyId, Code = "1000", Name = "Operating Bank",
                        Type = GlAccountType.Asset, SubType = "BANK", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.CashAndCashEquivalents
                    },
                    new GlAccount
                    {
                        Id = revenueGlId, CompanyId = companyId, Code = "4100", Name = "Service Revenue",
                        Type = GlAccountType.Income, SubType = "REV-SVC", Currency = "USD",
                        CashFlowCategory = CashFlowCategory.Operating
                    },
                    new GlAccount
                    {
                        Id = expenseGlId, CompanyId = companyId, Code = "5100", Name = "Operating Supplies",
                        Type = GlAccountType.Expense, SubType = "EXP-SUP", Currency = "USD",
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
                    new JournalEntry { Id = arInvoiceJournalId, CompanyId = companyId, EntryNo = "JE-AR-INV", Description = "AR invoice", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AR, Status = JournalStatus.Posted },
                    new JournalEntry { Id = arReceiptJournalId, CompanyId = companyId, EntryNo = "JE-AR-REC", Description = "AR receipt", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AR, Status = JournalStatus.Posted },
                    new JournalEntry { Id = apInvoiceJournalId, CompanyId = companyId, EntryNo = "JE-AP-INV", Description = "AP invoice", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AP, Status = JournalStatus.Posted },
                    new JournalEntry { Id = apPaymentJournalId, CompanyId = companyId, EntryNo = "JE-AP-PAY", Description = "AP payment", EntryDate = effectiveDate, PeriodId = periodId, Source = JournalSource.AP, Status = JournalStatus.Posted });
                seed.JournalLines.AddRange(
                    new JournalLine { JournalEntryId = arInvoiceJournalId, GlAccountId = revenueGlId, FundCentreId = fundCentreId, Credit = 100m, Debit = 0m, BaseCredit = 100m, BaseDebit = 0m },
                    new JournalLine { JournalEntryId = arReceiptJournalId, GlAccountId = bankGlId, Debit = 100m, Credit = 0m, BaseDebit = 100m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = arReceiptJournalId, GlAccountId = Guid.NewGuid(), Debit = 0m, Credit = 100m, BaseDebit = 0m, BaseCredit = 100m },
                    new JournalLine { JournalEntryId = apInvoiceJournalId, GlAccountId = expenseGlId, FundCentreId = fundCentreId, Debit = 200m, Credit = 0m, BaseDebit = 200m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = apPaymentJournalId, GlAccountId = Guid.NewGuid(), Debit = 200m, Credit = 0m, BaseDebit = 200m, BaseCredit = 0m },
                    new JournalLine { JournalEntryId = apPaymentJournalId, GlAccountId = bankGlId, Debit = 0m, Credit = 200m, BaseDebit = 0m, BaseCredit = 200m });
                seed.Customers.Add(new Customer { Id = customerId, CompanyId = companyId, Code = "C-SETTLE", Name = "Settlement Customer", Currency = "USD" });
                seed.Suppliers.Add(new Supplier { Id = supplierId, CompanyId = companyId, Code = "S-SETTLE", Name = "Settlement Supplier", Currency = "USD" });
                seed.ArInvoices.Add(new ArInvoice
                {
                    Id = arInvoiceId, CompanyId = companyId, InvoiceNo = "AR-SETTLE-001",
                    CustomerId = customerId, InvoiceDate = effectiveDate, DueDate = effectiveDate,
                    Currency = "USD", Subtotal = 100m, Total = 100m, Outstanding = 0m,
                    Status = InvoiceStatus.Paid, JournalEntryId = arInvoiceJournalId, FundCentreId = fundCentreId,
                    Lines = new List<ArInvoiceLine>
                    {
                        new() { ArInvoiceId = arInvoiceId, Description = "Service", Quantity = 1m, UnitPrice = 100m, LineSubtotal = 100m, LineVat = 0m, LineTotal = 100m, GlAccountId = revenueGlId }
                    }
                });
                seed.ArReceipts.Add(new ArReceipt
                {
                    Id = arReceiptId, CompanyId = companyId, ReceiptNo = "RCT-SETTLE-001",
                    CustomerId = customerId, ReceiptDate = effectiveDate, Currency = "USD",
                    Amount = 100m, JournalEntryId = arReceiptJournalId,
                    Allocations = new List<ArReceiptAllocation>
                    {
                        new() { ArReceiptId = arReceiptId, ArInvoiceId = arInvoiceId, AmountApplied = 100m }
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
                        new() { ApInvoiceId = apInvoiceId, Description = "Supplies", Quantity = 1m, UnitCost = 200m, LineSubtotal = 200m, LineVat = 0m, LineTotal = 200m, GlAccountId = expenseGlId }
                    }
                });
                seed.ApPayments.Add(new ApPayment
                {
                    Id = apPaymentId, CompanyId = companyId, PaymentNo = "PAY-SETTLE-001",
                    SupplierId = supplierId, PaymentDate = effectiveDate, Currency = "USD",
                    Amount = 200m, JournalEntryId = apPaymentJournalId,
                    PreparedBy = preparerId, AuthorisedBy = certifierId, AuthorisedAt = DateTime.UtcNow,
                    Allocations = new List<ApPaymentAllocation>
                    {
                        new() { ApPaymentId = apPaymentId, ApInvoiceId = apInvoiceId, AmountApplied = 200m }
                    }
                });
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
                    FundCentreId = fundCentreId, GlAccountId = revenueGlId, LineNumber = 1,
                    Description = "Service revenue", ClassificationCode = "REV-SVC", Amount = 100m, CreatedBy = preparerId
                });
                version.Lines.Add(new PublicSectorBudgetVersionLine
                {
                    Id = apBudgetLineId, CompanyId = companyId, BudgetVersionId = budgetVersionId,
                    FundCentreId = fundCentreId, GlAccountId = expenseGlId, LineNumber = 2,
                    Description = "Operating supplies", ClassificationCode = "EXP-SUP", Amount = 200m, CreatedBy = preparerId
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
            var apActual = await service.CreateBudgetExecutionActualAsync(companyId, budgetVersionId,
                apBudgetLineId, PublicSectorBudgetActualEventKind.CashPayment,
                PublicSectorBudgetActualSourceKind.ApPayment, apPaymentId,
                effectiveDate, CashFlowCategory.Operating, preparerId);

            Assert.Equal(100m, arActual.Amount);
            Assert.Equal("RCT-SETTLE-001", arActual.SourceReference);
            Assert.Equal(200m, apActual.Amount);
            Assert.Equal("PAY-SETTLE-001", apActual.SourceReference);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, arActual.Status);
            Assert.Equal(PublicSectorBudgetActualStatus.Draft, apActual.Status);
        }
    }
}
