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

        public PublicSectorService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<FundCentre> CreateFundCentreAsync(Guid companyId, string code, string name, FundCentreLevel level,
            Guid? parentId, int financialYear, decimal appropriationAmount, decimal carriedForward, CancellationToken ct = default)
        {
            if (parentId.HasValue)
            {
                var parent = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == parentId && f.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("Parent fund centre not found.");
                if (level <= parent.Level) throw new InvalidOperationException("A child fund centre must be one level below its parent (Vote > Program > Sub-Program).");
            }

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
            bool allowOverride, CancellationToken ct = default)
        {
            var utilisation = await GetBudgetUtilizationAsync(companyId, fundCentreId, ct);
            if (utilisation.Available < amount && !allowOverride)
                throw new InvalidOperationException(
                    $"Committing {amount:N2} would exceed available budget for {utilisation.Code} (available: {utilisation.Available:N2}). Requires an approved override.");

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
            await _db.SaveChangesAsync(ct);
            return commitment;
        }

        /// <summary>Releases a commitment when the GRN/invoice posts (the actual expenditure replaces
        /// the estimate). Call this from SupplyChainService/ApService when a PO with a FundCentreId is fulfilled.</summary>
        public async Task ReleaseCommitmentForPoAsync(Guid companyId, Guid purchaseOrderId, CancellationToken ct = default)
        {
            var commitment = await _db.Commitments.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.PurchaseOrderId == purchaseOrderId && c.Status == CommitmentStatus.Active, ct);
            if (commitment == null) return; // no active commitment for this PO -- not a public-sector-tracked purchase
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
                .SumAsync(c => c.Amount, ct);

            // Actuals: posted AP invoices and payroll (via JournalLine.FundCentreId) tagged against this fund centre.
            var actualsFromInvoices = await _db.ApInvoices
                .Where(i => i.CompanyId == companyId && i.FundCentreId == fundCentreId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled)
                .SumAsync(i => i.Total, ct);
            var actualsFromJournals = await _db.JournalLines
                .Where(l => l.FundCentreId == fundCentreId && l.JournalEntry!.CompanyId == companyId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
                .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);
            var actuals = Math.Max(actualsFromInvoices, actualsFromJournals); // avoid double-counting if AP invoices already flow through JournalLine tagging

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
            var fc = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == fundCentreId && f.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Fund centre not found.");
            if (fc.IsClosed) throw new InvalidOperationException("This fund centre is already closed.");

            var utilisation = await GetBudgetUtilizationAsync(companyId, fundCentreId, ct);
            var unexpended = utilisation.Available;

            Guid? newFundCentreId = null;
            if (carryForward && unexpended > 0)
            {
                var next = new FundCentre
                {
                    CompanyId = companyId,
                    Code = fc.Code,
                    Name = fc.Name,
                    Level = fc.Level,
                    ParentId = fc.ParentId,
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
            await _db.SaveChangesAsync(ct);

            return new YearEndCloseResult(fundCentreId, unexpended, carryForward ? unexpended : 0, carryForward ? 0 : Math.Max(unexpended, 0), newFundCentreId);
        }
    }
}
