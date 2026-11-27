using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record BudgetUtilization(Guid FundCentreId, string Code, string Name, decimal Appropriation,
        decimal Commitments, decimal Actuals, decimal Available, decimal PercentUtilised, bool OverNinetyPercent);
    public record YearEndCloseResult(Guid FundCentreId, decimal UnexpendedBalance, decimal CarriedForward, decimal Lapsed, Guid? NewFundCentreId);

    /// <summary>
    /// PRD Module 16 (Public Sector & Government): Vote/Program/Sub-Program fund hierarchy with
    /// commitment (encumbrance) accounting. Commitments are tracked as a budgetary-control ledger
    /// separate from the double-entry GL (the standard approach -- encumbrances are not GAAP/IFRS
    /// balance sheet transactions, they are an internal appropriation-control mechanism), so
    /// Available Budget = Appropriation - Commitments - Actuals is always computable in real time
    /// without risking the integrity of the real accounting ledger.
    /// </summary>
    public class PublicSectorService
    {
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;

        public PublicSectorService(ApexErpDbContext db, AuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        public async Task<FundCentre> CreateFundCentreAsync(Guid companyId, string code, string name, FundCentreLevel level,
            Guid? parentId, int financialYear, decimal appropriationAmount, decimal carriedForward, CancellationToken ct = default)
        {
            if (parentId.HasValue)
            {
                var parent = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == parentId && f.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("Parent fund centre not found.");
                if (level != (FundCentreLevel)((int)parent.Level + 1)) throw new InvalidOperationException("A child fund centre must be exactly one level below its parent (Vote > Program > Sub-Program).");
                if (parent.FinancialYear != financialYear) throw new InvalidOperationException("A child fund centre must belong to the same financial year as its parent.");
            }

            else if (level != FundCentreLevel.Vote)
                throw new InvalidOperationException("A root fund centre must be a Vote.");
            if (financialYear < 2000 || financialYear > 2200 || appropriationAmount < 0 || carriedForward < 0)
                throw new InvalidOperationException("Financial year and appropriation amounts are invalid.");

            var fc = new FundCentre
            {
                CompanyId = companyId,
                Code = code,
                Name = name,
                Level = level,
                ParentId = parentId,
                FinancialYear = financialYear,
                AppropriationAmount = appropriationAmount,
                CarriedForwardAmount = carriedForward
            };
            _db.FundCentres.Add(fc);
            await _db.SaveChangesAsync(ct);
            return fc;
        }

        /// <summary>Creates an encumbrance against a fund centre when a PO is approved. Blocks the
        /// commitment (unless overridden) if it would exceed available budget.</summary>
        public async Task<Commitment> CommitAsync(Guid companyId, Guid fundCentreId, Guid purchaseOrderId, decimal amount,
            bool allowOverride, string? overrideReason, Guid actorId, string? ipAddress = null, CancellationToken ct = default)
        {
            if (amount <= 0) throw new InvalidOperationException("A commitment amount must be greater than zero.");
            if (allowOverride && string.IsNullOrWhiteSpace(overrideReason))
                throw new InvalidOperationException("A budget override requires a recorded justification.");

            return await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-budget:{companyId:N}:{fundCentreId:N}", token);
                var fundCentre = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Fund centre not found.");
                if (fundCentre.IsClosed) throw new InvalidOperationException("A closed fund centre cannot accept new commitments.");

                var purchaseOrder = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == purchaseOrderId && p.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Purchase order not found in this company.");
                if (purchaseOrder.Status != PurchaseOrderStatus.Approved || purchaseOrder.FundCentreId != fundCentreId)
                    throw new InvalidOperationException("A commitment requires an approved purchase order assigned to the same fund centre.");
                if (allowOverride)
                {
                    var overrideApproval = await _db.WorkflowRequests.Include(w => w.Approvals)
                        .FirstOrDefaultAsync(w => w.CompanyId == companyId &&
                            w.RequestType == "PublicSectorBudgetOverride" && w.ReferenceTable == "PurchaseOrder" &&
                            w.ReferenceId == purchaseOrderId && w.SubmittedBy == actorId &&
                            w.Notes == overrideReason!.Trim() && w.Status == WorkflowStatus.Approved &&
                            w.ApprovalChainId != null, token);
                    if (overrideApproval == null || overrideApproval.Approvals.Count == 0 ||
                        overrideApproval.Approvals.Any(a => !a.Approved))
                        throw new InvalidOperationException("A budget override requires independent approval through the configured PublicSectorBudgetOverride approval chain before commitment.");
                }
                if (amount != purchaseOrder.Total)
                    throw new InvalidOperationException("The commitment amount must equal the approved purchase order total.");
                if (await _db.Commitments.AnyAsync(c => c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId, token))
                    throw new InvalidOperationException("This purchase order already has a budget commitment.");

                var utilisation = await GetBudgetUtilizationAsync(companyId, fundCentreId, token);
                var exceedsBudget = utilisation.Available < amount;
                if (exceedsBudget && !allowOverride)
                    throw new InvalidOperationException(
                        $"Committing {amount:N2} would exceed available budget for {utilisation.Code} (available: {utilisation.Available:N2}). An authorised budget override is required.");

                var commitment = new Commitment
                {
                    CompanyId = companyId,
                    FundCentreId = fundCentreId,
                    PurchaseOrderId = purchaseOrderId,
                    Amount = amount,
                    Status = CommitmentStatus.Active,
                    CommittedDate = DateTime.UtcNow
                };
                _db.Commitments.Add(commitment);
                _audit.Record(companyId, actorId, allowOverride ? "BudgetCommitment.Override" : "BudgetCommitment.Created",
                    "PublicSector", nameof(Commitment), commitment.Id,
                    new { utilisation.Available, utilisation.Commitments, utilisation.Actuals },
                    new { amount, purchaseOrderId, fundCentreId, exceedsBudget, overrideReason }, ipAddress);
                await _db.SaveChangesAsync(token);
                return commitment;
            }, ct);
        }

        /// <summary>
        /// Applies a posted AP invoice's base-currency expense amount against its PO encumbrance.
        /// Partial invoices reduce the remaining commitment; the encumbrance closes only when fully realised.
        /// This operation shares the posting transaction so GL actuals and budget commitments cannot diverge.
        /// </summary>
        public Task RealiseCommitmentAsync(Guid companyId, Guid commitmentId, decimal realisedAmount, CancellationToken ct = default)
        {
            if (realisedAmount <= 0m) throw new InvalidOperationException("A realised commitment amount must be greater than zero.");
            return _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-commitment:{companyId:N}:{commitmentId:N}", token);
                var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                    c.CompanyId == companyId && c.Id == commitmentId, token)
                    ?? throw new InvalidOperationException("Commitment not found in this company.");
                if (commitment.Status != CommitmentStatus.Active)
                    throw new InvalidOperationException("Only an active commitment can be realised.");
                var remaining = commitment.Amount - commitment.RealisedAmount;
                if (realisedAmount > remaining + 0.000001m)
                    throw new InvalidOperationException($"The invoice exceeds the remaining commitment ({remaining:N2}).");

                commitment.RealisedAmount = Math.Min(commitment.Amount, commitment.RealisedAmount + realisedAmount);
                if (commitment.Amount - commitment.RealisedAmount <= 0.000001m)
                {
                    commitment.RealisedAmount = commitment.Amount;
                    commitment.Status = CommitmentStatus.Released;
                    commitment.ReleasedDate = DateTime.UtcNow;
                }
                _audit.Record(companyId, _db.CurrentUserId, "BudgetCommitment.Realised", "PublicSector",
                    nameof(Commitment), commitment.Id,
                    new { previousRealisedAmount = commitment.RealisedAmount - realisedAmount },
                    new { invoiceRealisedAmount = realisedAmount, cumulativeRealisedAmount = commitment.RealisedAmount,
                        remainingCommitmentAmount = commitment.Amount - commitment.RealisedAmount });
                await _db.SaveChangesAsync(token);
                return true;
            }, ct);
        }

        /// <summary>Releases a commitment when the GRN/invoice posts (the actual expenditure replaces
        /// the estimate). Call this from SupplyChainService/ApService when a PO with a FundCentreId is fulfilled.</summary>
        public async Task ReleaseCommitmentForPoAsync(Guid companyId, Guid purchaseOrderId, CancellationToken ct = default)
        {
            var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId &&
                c.Status == CommitmentStatus.Active, ct);
            if (commitment == null) return; // no active commitment for this PO -- idempotent for untracked/already released POs
            await ReleaseCommitmentAsync(companyId, commitment.Id, ct);
        }

        /// <summary>Releases a specific encumbrance when its linked AP invoice is posted.
        /// This is keyed by Commitment.Id, not PurchaseOrderId.</summary>
        public async Task ReleaseCommitmentAsync(Guid companyId, Guid commitmentId, CancellationToken ct = default)
        {
            var commitment = await _db.Commitments.FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && c.Id == commitmentId, ct);
            if (commitment == null)
                throw new InvalidOperationException("Commitment not found in this company.");
            if (commitment.Status != CommitmentStatus.Active) return;

            commitment.Status = CommitmentStatus.Released;
            commitment.ReleasedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Fully reverses a commitment on PO cancellation.</summary>
        public async Task CancelCommitmentForPoAsync(Guid companyId, Guid purchaseOrderId, CancellationToken ct = default)
        {
            var commitment = await _db.Commitments.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId && c.Status == CommitmentStatus.Active, ct);
            if (commitment == null) return;
            commitment.Status = CommitmentStatus.Cancelled;
            commitment.ReleasedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<BudgetUtilization> GetBudgetUtilizationAsync(Guid companyId, Guid fundCentreId, CancellationToken ct = default)
        {
            var fc = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Fund centre not found.");

            var totalAppropriation = fc.AppropriationAmount + fc.CarriedForwardAmount;
            var commitments = await _db.Commitments
                .Where(c => c.CompanyId == companyId && c.FundCentreId == fundCentreId && c.Status == CommitmentStatus.Active)
                 .SumAsync(c => c.Amount - c.RealisedAmount, ct);

            // The posted GL is the authoritative actuals ledger. AP invoice expense lines and payroll
            // postings are tagged with FundCentreId and stored in base currency; counting AP headers as
            // well would double-count the same expenditure and mix foreign currency with base currency.
            var actuals = await _db.JournalLines
                .Where(l => l.FundCentreId == fundCentreId && l.JournalEntry!.CompanyId == companyId &&
                    (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);

            var available = totalAppropriation - commitments - actuals;
            var pctUtilised = totalAppropriation == 0 ? 0 : Math.Round((commitments + actuals) / totalAppropriation * 100, 2);

            return new BudgetUtilization(fc.Id, fc.Code, fc.Name, totalAppropriation, commitments, actuals, available, pctUtilised, pctUtilised > 90);
        }

        public async Task<List<BudgetUtilization>> GetAllBudgetUtilizationAsync(Guid companyId, int financialYear, CancellationToken ct = default)
        {
            var fundCentres = await _db.FundCentres.Where(f => f.CompanyId == companyId && f.FinancialYear == financialYear).ToListAsync(ct);
            var result = new List<BudgetUtilization>();
            foreach (var fc in fundCentres) result.Add(await GetBudgetUtilizationAsync(companyId, fc.Id, ct));
            return result;
        }

        /// <summary>Year-end workflow: computes the unexpended balance and either carries it forward
        /// (creating next year's fund centre pre-seeded with that balance) or lapses it to the
        /// appropriation surplus, per policy.</summary>
        public async Task<YearEndCloseResult> CloseYearEndAsync(Guid companyId, Guid fundCentreId, bool carryForward, CancellationToken ct = default)
        {
            return await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"public-sector-year-end:{companyId:N}:{fundCentreId:N}", token);
                var fc = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Fund centre not found.");
                if (fc.IsClosed) throw new InvalidOperationException("This fund centre is already closed.");

                var utilisation = await GetBudgetUtilizationAsync(companyId, fundCentreId, token);
                var unexpended = utilisation.Available;

                Guid? nextParentId = null;
                if (carryForward && unexpended > 0 && fc.ParentId.HasValue)
                {
                    var priorParent = await _db.FundCentres.FirstOrDefaultAsync(
                        f => f.Id == fc.ParentId.Value && f.CompanyId == companyId, token)
                        ?? throw new InvalidOperationException("The parent fund centre could not be found.");
                    var nextParent = await _db.FundCentres.FirstOrDefaultAsync(
                        f => f.CompanyId == companyId && f.Code == priorParent.Code &&
                             f.Level == priorParent.Level && f.FinancialYear == fc.FinancialYear + 1, token);
                    if (nextParent == null)
                        throw new InvalidOperationException(
                            "Carry forward the parent fund centre first so the next-year hierarchy remains intact.");
                    nextParentId = nextParent.Id;
                }

                Guid? newFundCentreId = null;
                if (carryForward && unexpended > 0)
                {
                    var alreadyExists = await _db.FundCentres.AnyAsync(
                        f => f.CompanyId == companyId && f.Code == fc.Code &&
                             f.FinancialYear == fc.FinancialYear + 1, token);
                    if (alreadyExists)
                        throw new InvalidOperationException("A next-year fund centre with this code already exists.");

                    var next = new FundCentre
                    {
                        CompanyId = companyId,
                        Code = fc.Code,
                        Name = fc.Name,
                        Level = fc.Level,
                        ParentId = nextParentId,
                        FinancialYear = fc.FinancialYear + 1,
                        AppropriationAmount = 0,
                        CarriedForwardAmount = unexpended
                    };
                    _db.FundCentres.Add(next);
                    fc.CarriedForwardAmount = 0; // moved forward, not double-counted in the old year
                    newFundCentreId = next.Id;
                }
                else if (unexpended > 0)
                {
                    fc.LapsedAmount = unexpended;
                }

                fc.IsClosed = true;
                await _db.SaveChangesAsync(token);

                return new YearEndCloseResult(fundCentreId, unexpended,
                    carryForward ? unexpended : 0, carryForward ? 0 : Math.Max(unexpended, 0), newFundCentreId);
            }, ct);
        }
    }
}
