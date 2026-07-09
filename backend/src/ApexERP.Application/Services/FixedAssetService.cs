using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>PRD Module 7 (Fixed Assets): asset register, straight-line and reducing-balance
    /// depreciation, monthly batch depreciation run, and disposal accounting.</summary>
    public class FixedAssetService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        public FixedAssetService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        public async Task<FixedAsset> RegisterAssetAsync(Guid companyId, string assetNo, string name, Guid categoryId,
            DateTime acquisitionDate, decimal acquisitionCost, DepreciationMethod method, int usefulLifeMonths,
            decimal residualValue, Guid? costCentreId, CancellationToken ct = default)
        {
            var asset = new FixedAsset
            {
                CompanyId = companyId,
                AssetNo = assetNo,
                Name = name,
                CategoryId = categoryId,
                AcquisitionDate = acquisitionDate,
                AcquisitionCost = acquisitionCost,
                DepreciationMethod = method,
                UsefulLifeMonths = usefulLifeMonths,
                ResidualValue = residualValue,
                AccumulatedDepreciation = 0,
                CurrentNbv = acquisitionCost,
                Status = AssetStatus.Active,
                CostCentreId = costCentreId
            };
            _db.FixedAssets.Add(asset);
            await _db.SaveChangesAsync(ct);
            return asset;
        }

        /// <summary>Computes one period's depreciation charge for a single asset without persisting it.
        /// Straight-line: (Cost - Residual) / UsefulLifeMonths.
        /// Reducing-balance: NBV * (2 / UsefulLifeMonths) -- double-declining-balance rate, floored at residual.</summary>
        public decimal CalculateMonthlyDepreciation(FixedAsset asset)
        {
            if (asset.Status != AssetStatus.Active || asset.CurrentNbv <= asset.ResidualValue) return 0m;

            decimal charge = asset.DepreciationMethod switch
            {
                DepreciationMethod.StraightLine => (asset.AcquisitionCost - asset.ResidualValue) / asset.UsefulLifeMonths,
                DepreciationMethod.ReducingBalance => asset.CurrentNbv * (2m / asset.UsefulLifeMonths),
                _ => 0m
            };

            var maxAllowable = asset.CurrentNbv - asset.ResidualValue;
            return Math.Round(Math.Min(charge, maxAllowable), 6);
        }

        /// <summary>Runs depreciation for every active asset for the given period and posts one
        /// consolidated journal per asset category (Dr Depreciation Expense, Cr Accumulated Depreciation).</summary>
        public async Task<List<DepreciationEntry>> RunMonthlyDepreciationAsync(Guid companyId, DateTime periodDate, CancellationToken ct = default)
        {
            var assets = await _db.FixedAssets
                .Include(a => a.Category)
                .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active)
                .ToListAsync(ct);

            var entries = new List<DepreciationEntry>();
            var byCategory = assets.GroupBy(a => a.CategoryId);

            foreach (var group in byCategory)
            {
                var category = group.First().Category!;
                decimal categoryTotal = 0;
                var lineEntries = new List<(FixedAsset asset, decimal amount)>();

                foreach (var asset in group)
                {
                    var amount = CalculateMonthlyDepreciation(asset);
                    if (amount <= 0) continue;
                    asset.AccumulatedDepreciation += amount;
                    asset.CurrentNbv -= amount;
                    categoryTotal += amount;
                    lineEntries.Add((asset, amount));
                }

                if (categoryTotal <= 0) continue;

                var je = await _gl.PostAsync(companyId, periodDate, JournalSource.Assets,
                    $"Depreciation - {category.Name} - {periodDate:yyyy-MM}", $"DEPR-{periodDate:yyyyMM}-{category.Id.ToString()[..8]}",
                    new[]
                    {
                        new GlLineRequest(category.DepreciationExpenseAccountId, categoryTotal, 0, "USD", 1m, null, "Depreciation expense"),
                        new GlLineRequest(category.AccumulatedDepreciationAccountId, 0, categoryTotal, "USD", 1m, null, "Accumulated depreciation")
                    }, ct);

                foreach (var (asset, amount) in lineEntries)
                {
                    entries.Add(new DepreciationEntry
                    {
                        CompanyId = companyId,
                        FixedAssetId = asset.Id,
                        PeriodDate = periodDate,
                        Amount = amount,
                        NbvAfter = asset.CurrentNbv,
                        JournalEntryId = je.Id
                    });
                }
            }

            _db.DepreciationEntries.AddRange(entries);
            await _db.SaveChangesAsync(ct);
            return entries;
        }

        /// <summary>Disposes an asset: writes off remaining NBV against proceeds, posting the gain/loss.</summary>
        public async Task<FixedAsset> DisposeAsync(Guid companyId, Guid assetId, DateTime disposalDate, decimal proceeds,
            Guid bankOrReceivableAccountId, Guid gainLossOnDisposalAccountId, CancellationToken ct = default)
        {
            var asset = await _db.FixedAssets.Include(a => a.Category)
                .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Asset not found.");
            if (asset.Status != AssetStatus.Active) throw new InvalidOperationException("Asset is already disposed.");

            var category = asset.Category!;
            var gainLoss = proceeds - asset.CurrentNbv; // positive = gain, negative = loss

            var glLines = new List<GlLineRequest>
            {
                new(category.AccumulatedDepreciationAccountId, asset.AccumulatedDepreciation, 0, "USD", 1m, null, "Clear accumulated depreciation"),
                new(bankOrReceivableAccountId, proceeds, 0, "USD", 1m, null, "Disposal proceeds"),
            };
            var costLessAccDep = asset.AcquisitionCost;
            glLines.Add(new GlLineRequest(category.AssetAccountId, 0, costLessAccDep, "USD", 1m, null, "Remove asset at cost"));

            if (gainLoss > 0)
                glLines.Add(new GlLineRequest(gainLossOnDisposalAccountId, 0, gainLoss, "USD", 1m, null, "Gain on disposal"));
            else if (gainLoss < 0)
                glLines.Add(new GlLineRequest(gainLossOnDisposalAccountId, -gainLoss, 0, "USD", 1m, null, "Loss on disposal"));

            await _gl.PostAsync(companyId, disposalDate, JournalSource.Assets, $"Disposal of {asset.AssetNo}", asset.AssetNo, glLines, ct);

            asset.Status = AssetStatus.Disposed;
            asset.DisposalDate = disposalDate;
            asset.DisposalProceeds = proceeds;
            asset.CurrentNbv = 0;
            await _db.SaveChangesAsync(ct);
            return asset;
        }
        /// <summary>IAS 16 revaluation model: restates an asset to fair value. An increase is
        /// recognised in Other Comprehensive Income (Revaluation Surplus equity account); this
        /// implementation does not yet handle the reversal-through-P&amp;L case for a decrease below
        /// a prior surplus (a refinement for a future pass -- flagged here rather than silently
        /// mishandled) and simply posts the OCI side for any surplus amount, positive or negative.</summary>
        public async Task<AssetRevaluation> RevalueAsync(Guid companyId, Guid assetId, DateTime revaluationDate,
            decimal fairValue, string? valuerName, CancellationToken ct = default)
        {
            var asset = await _db.FixedAssets.Include(a => a.Category)
                .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Asset not found.");
            if (asset.Status != AssetStatus.Active) throw new InvalidOperationException("Cannot revalue a disposed asset.");
            var category = asset.Category!;
            if (!category.RevaluationSurplusEquityAccountId.HasValue)
                throw new InvalidOperationException($"Asset category '{category.Name}' has no Revaluation Surplus equity account configured.");

            var nbvBefore = asset.CurrentNbv;
            var surplus = fairValue - nbvBefore;

            JournalEntry? je = null;
            if (surplus != 0)
            {
                je = await _gl.PostAsync(companyId, revaluationDate, JournalSource.Assets, $"Revaluation of {asset.AssetNo}", asset.AssetNo,
                    surplus > 0
                        ? new[]
                        {
                            new GlLineRequest(category.AssetAccountId, surplus, 0, "USD", 1m, null, "Revaluation increase"),
                            new GlLineRequest(category.RevaluationSurplusEquityAccountId.Value, 0, surplus, "USD", 1m, null, "Revaluation surplus (OCI)")
                        }
                        : new[]
                        {
                            new GlLineRequest(category.RevaluationSurplusEquityAccountId.Value, -surplus, 0, "USD", 1m, null, "Revaluation decrease (OCI)"),
                            new GlLineRequest(category.AssetAccountId, 0, -surplus, "USD", 1m, null, "Revaluation decrease")
                        }, ct);
            }

            asset.CurrentNbv = fairValue;
            asset.CumulativeRevaluationSurplus += surplus;

            var revaluation = new AssetRevaluation
            {
                CompanyId = companyId,
                FixedAssetId = assetId,
                RevaluationDate = revaluationDate,
                NbvBeforeRevaluation = nbvBefore,
                FairValue = fairValue,
                SurplusAmount = surplus,
                JournalEntryId = je?.Id,
                ValuerName = valuerName
            };
            _db.AssetRevaluations.Add(revaluation);
            await _db.SaveChangesAsync(ct);
            return revaluation;
        }
    }
}
