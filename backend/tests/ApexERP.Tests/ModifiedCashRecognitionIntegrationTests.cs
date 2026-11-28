using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests;

public sealed class ModifiedCashRecognitionIntegrationTests
{
    [PostgresFact]
    public async Task Modified_cash_policy_and_ledger_are_tenant_scoped_idempotent_and_append_only()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var preparerId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        var commitmentId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = db.NewContext(companyId, preparerId))
        {
            seed.Companies.AddRange(
                new Company { Id = companyId, Name = "Modified Cash Test", BaseCurrency = "USD", FunctionalCurrency = "USD" },
                new Company { Id = otherCompanyId, Name = "Other Company", BaseCurrency = "USD", FunctionalCurrency = "USD" });
            var vendor = new Vendor { CompanyId = companyId, Code = "MC-V-001", Name = "Modified Cash Vendor" };
            var fund = new FundCentre
            {
                CompanyId = companyId, Code = "MC-VOTE-01", Name = "Modified Cash Vote",
                Level = FundCentreLevel.Vote, FinancialYear = 2026, AppropriationAmount = 1000m
            };
            var account = new GlAccount
            {
                CompanyId = companyId, Code = "MC-EXP-001", Name = "Operating Expense",
                Type = GlAccountType.Expense, IsActive = true
            };
            var po = new PurchaseOrder
            {
                CompanyId = companyId, VendorId = vendor.Id, PoNo = "MC-PO-001",
                IssueDate = periodStart, Status = PurchaseOrderStatus.Approved,
                Total = 100m, FundCentreId = fund.Id
            };
            var version = new PublicSectorBudgetVersion
            {
                CompanyId = companyId, FinancialYear = 2026, VersionNumber = 1,
                Kind = PublicSectorBudgetVersionKind.Original,
                Status = PublicSectorBudgetVersionStatus.Approved, Currency = "USD",
                AccountingBasis = AccountingBasis.Cash, ClassificationBasis = "Test classification",
                EntityScope = "Registered entity", PeriodStart = periodStart, PeriodEnd = periodEnd,
                AuthorityReference = "MC-AUTH-001"
            };
            var line = new PublicSectorBudgetVersionLine
            {
                Id = lineId, CompanyId = companyId, BudgetVersionId = version.Id,
                FundCentreId = fund.Id, GlAccountId = account.Id, LineNumber = 1,
                Description = "Operating goods", ClassificationCode = "GOODS", Amount = 1000m
            };
            version.Lines.Add(line);
            var commitment = new Commitment
            {
                Id = commitmentId, CompanyId = companyId, FundCentreId = fund.Id,
                PurchaseOrderId = po.Id, Amount = 100m, RealisedAmount = 0m,
                Status = CommitmentStatus.Active, CommittedDate = periodStart
            };
            var policy = new ModifiedCashRecognitionPolicy
            {
                Id = policyId, CompanyId = companyId, BudgetVersionId = version.Id,
                FinancialYear = 2026, VersionNumber = 1, PolicyReference = "MC-POL-001",
                EffectiveFrom = periodStart, EffectiveTo = periodEnd,
                ReportingPeriodStart = periodStart, ReportingPeriodEnd = periodEnd,
                PostPeriodRecognitionWindowDays = 30,
                Treatment = ModifiedCashBudgetTreatment.IncludeInBudgetActual,
                Status = ModifiedCashRecognitionPolicyStatus.Approved,
                PreparedBy = preparerId, ApprovedBy = approverId,
                ApprovedAt = periodStart.AddDays(2)
            };
            var mapping = new ModifiedCashRecognitionMapping
            {
                CompanyId = companyId, PolicyId = policy.Id, SourceEventKind = "GoodsReceived",
                BudgetVersionLineId = lineId
            };
            var ledgerEvent = new ModifiedCashRecognitionLedgerEvent
            {
                CompanyId = companyId, PolicyId = policy.Id, CommitmentId = commitment.Id,
                SourceEventId = sourceEventId, SourceEventKind = "GoodsReceived",
                EventKind = ModifiedCashLedgerEventKind.Recognition, Amount = 40m,
                EventDate = periodStart.AddDays(10), EvidenceAcceptedAt = periodStart.AddDays(11),
                EvidenceReference = "GRN:MC-001", Status = ModifiedCashLedgerEventStatus.Certified,
                PreparedBy = preparerId, CertifiedBy = approverId, CertifiedAt = periodStart.AddDays(12)
            };
            seed.Vendors.Add(vendor);
            seed.FundCentres.Add(fund);
            seed.GlAccounts.Add(account);
            seed.PurchaseOrders.Add(po);
            seed.PublicSectorBudgetVersions.Add(version);
            seed.Commitments.Add(commitment);
            seed.ModifiedCashRecognitionPolicies.Add(policy);
            seed.ModifiedCashRecognitionMappings.Add(mapping);
            seed.ModifiedCashRecognitionLedgerEvents.Add(ledgerEvent);
            await seed.SaveChangesAsync();
        }

        await using (var otherTenant = db.NewContext(otherCompanyId))
        {
            Assert.Empty(await otherTenant.ModifiedCashRecognitionPolicies.ToListAsync());
            Assert.Empty(await otherTenant.ModifiedCashRecognitionMappings.ToListAsync());
            Assert.Empty(await otherTenant.ModifiedCashRecognitionLedgerEvents.ToListAsync());
        }

        await using (var duplicate = db.NewContext(companyId))
        {
            var secondPolicy = new ModifiedCashRecognitionPolicy
            {
                CompanyId = companyId, BudgetVersionId = await duplicate.ModifiedCashRecognitionPolicies
                    .Where(x => x.Id == policyId).Select(x => x.BudgetVersionId).SingleAsync(),
                FinancialYear = 2026, VersionNumber = 2, PolicyReference = "MC-POL-002",
                EffectiveFrom = periodStart, EffectiveTo = periodEnd,
                ReportingPeriodStart = periodStart, ReportingPeriodEnd = periodEnd,
                PostPeriodRecognitionWindowDays = 30,
                Treatment = ModifiedCashBudgetTreatment.IncludeInBudgetActual,
                Status = ModifiedCashRecognitionPolicyStatus.Approved,
                PreparedBy = preparerId, ApprovedBy = approverId, ApprovedAt = periodStart.AddDays(2)
            };
            duplicate.ModifiedCashRecognitionPolicies.Add(secondPolicy);
            duplicate.ModifiedCashRecognitionMappings.Add(new ModifiedCashRecognitionMapping
            {
                CompanyId = companyId, PolicyId = secondPolicy.Id, SourceEventKind = "GoodsReceived",
                BudgetVersionLineId = lineId
            });
            duplicate.ModifiedCashRecognitionLedgerEvents.Add(new ModifiedCashRecognitionLedgerEvent
            {
                CompanyId = companyId, PolicyId = secondPolicy.Id, CommitmentId = commitmentId,
                SourceEventId = sourceEventId, SourceEventKind = "GoodsReceived",
                EventKind = ModifiedCashLedgerEventKind.Recognition, Amount = 40m,
                EventDate = periodStart.AddDays(10), EvidenceAcceptedAt = periodStart.AddDays(11),
                EvidenceReference = "GRN:MC-001 duplicate", Status = ModifiedCashLedgerEventStatus.Certified,
                PreparedBy = preparerId, CertifiedBy = approverId, CertifiedAt = periodStart.AddDays(12)
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        await using (var partialSettlements = db.NewContext(companyId, preparerId))
        {
            var original = await partialSettlements.ModifiedCashRecognitionLedgerEvents
                .SingleAsync(x => x.SourceEventId == sourceEventId &&
                    x.EventKind == ModifiedCashLedgerEventKind.Recognition);
            partialSettlements.ModifiedCashRecognitionLedgerEvents.AddRange(
                new ModifiedCashRecognitionLedgerEvent
                {
                    CompanyId = companyId, PolicyId = policyId, CommitmentId = commitmentId,
                    SourceEventId = Guid.NewGuid(), SourceEventKind = "GoodsReceived",
                    EventKind = ModifiedCashLedgerEventKind.Liquidation, OriginalEventId = original.Id,
                    Amount = 10m, EventDate = periodStart.AddDays(20),
                    EvidenceAcceptedAt = periodStart.AddDays(21), EvidenceReference = "AP allocation 1",
                    Status = ModifiedCashLedgerEventStatus.Draft, PreparedBy = preparerId
                },
                new ModifiedCashRecognitionLedgerEvent
                {
                    CompanyId = companyId, PolicyId = policyId, CommitmentId = commitmentId,
                    SourceEventId = Guid.NewGuid(), SourceEventKind = "GoodsReceived",
                    EventKind = ModifiedCashLedgerEventKind.Liquidation, OriginalEventId = original.Id,
                    Amount = 15m, EventDate = periodStart.AddDays(22),
                    EvidenceAcceptedAt = periodStart.AddDays(23), EvidenceReference = "AP allocation 2",
                    Status = ModifiedCashLedgerEventStatus.Draft, PreparedBy = preparerId
                });
            await partialSettlements.SaveChangesAsync();
            Assert.Equal(2, await partialSettlements.ModifiedCashRecognitionLedgerEvents.CountAsync(x =>
                x.OriginalEventId == original.Id && x.EventKind == ModifiedCashLedgerEventKind.Liquidation));
        }

        await using (var immutable = db.NewContext(companyId))
        {
            var certified = await immutable.ModifiedCashRecognitionLedgerEvents
                .SingleAsync(x => x.SourceEventId == sourceEventId);
            certified.Amount = 45m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => immutable.SaveChangesAsync());
        }

        await using (var verify = db.NewContext(companyId))
        {
            var persisted = await verify.ModifiedCashRecognitionLedgerEvents
                .SingleAsync(x => x.SourceEventId == sourceEventId);
            Assert.Equal(40m, persisted.Amount);
        }
    }
}
