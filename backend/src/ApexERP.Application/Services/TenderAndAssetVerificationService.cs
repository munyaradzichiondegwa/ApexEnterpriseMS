using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>
    /// PPDPA-compliant e-tendering (PRD Module 16). The bid-timestamping, late-rejection, and
    /// weighted evaluation scoring below are real, server-enforced logic. A full public-facing
    /// vendor self-registration website is a separate, larger UI investment than this pass covers
    /// -- vendors here submit bids through the same authenticated app as internal users, which is
    /// functionally complete but not yet a distinct public portal experience.
    /// </summary>
    public class TenderService
    {
        private readonly ApexErpDbContext _db;

        public TenderService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<TenderNotice> CreateTenderAsync(Guid companyId, string title, string? specifications, DateTime submissionDeadline, CancellationToken ct = default)
        {
            var prefix = $"TND-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:{prefix}", ct);
            var count = await _db.TenderNotices.IgnoreQueryFilters().CountAsync(t => t.CompanyId == companyId && t.TenderNo.StartsWith(prefix), ct);
            var tender = new TenderNotice
            {
                CompanyId = companyId,
                TenderNo = $"{prefix}{(count + 1):D5}",
                Title = title,
                Specifications = specifications,
                SubmissionDeadline = submissionDeadline,
                Status = TenderStatus.Open
            };
            _db.TenderNotices.Add(tender);
            await _db.SaveChangesAsync(ct);
            return tender;
        }

        /// <summary>Submits a bid. Late bids are rejected automatically by comparing the server's
        /// own clock against the tender's deadline -- not a manual review step.</summary>
        public async Task<TenderBid> SubmitBidAsync(Guid companyId, Guid tenderNoticeId, Guid vendorId, decimal priceAmount, CancellationToken ct = default)
        {
            var tender = await _db.TenderNotices.FirstOrDefaultAsync(t => t.Id == tenderNoticeId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Tender notice not found.");

            var now = DateTime.UtcNow;
            var bid = new TenderBid
            {
                CompanyId = companyId,
                TenderNoticeId = tenderNoticeId,
                VendorId = vendorId,
                PriceAmount = priceAmount,
                SubmittedAt = now,
                IsLateRejected = now > tender.SubmissionDeadline
            };
            _db.TenderBids.Add(bid);
            await _db.SaveChangesAsync(ct);
            return bid;
        }

        /// <summary>Scores a bid on price/technical/local-content criteria (weights configurable per
        /// call) and rolls them into a total score for the evaluation matrix.</summary>
        public async Task<TenderBid> ScoreBidAsync(Guid companyId, Guid bidId, decimal priceScore, decimal technicalScore,
            decimal localContentScore, decimal priceWeight, decimal technicalWeight, decimal localContentWeight, CancellationToken ct = default)
        {
            var bid = await _db.TenderBids.FirstOrDefaultAsync(b => b.Id == bidId && b.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Bid not found.");
            if (bid.IsLateRejected) throw new InvalidOperationException("This bid was submitted after the deadline and cannot be scored.");

            bid.PriceScore = priceScore;
            bid.TechnicalScore = technicalScore;
            bid.LocalContentScore = localContentScore;
            bid.TotalScore = (priceScore * priceWeight) + (technicalScore * technicalWeight) + (localContentScore * localContentWeight);
            await _db.SaveChangesAsync(ct);
            return bid;
        }

        public async Task<TenderNotice> AwardAsync(Guid companyId, Guid tenderNoticeId, Guid winningVendorId, string justification, CancellationToken ct = default)
        {
            var tender = await _db.TenderNotices.FirstOrDefaultAsync(t => t.Id == tenderNoticeId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Tender notice not found.");
            tender.Status = TenderStatus.Awarded;
            tender.AwardedVendorId = winningVendorId;
            tender.AwardJustification = justification;
            await _db.SaveChangesAsync(ct);
            return tender;
        }
    }

    /// <summary>
    /// Physical asset verification (barcode/RFID) for audits (PRD Module 16). The hardware
    /// scan itself (camera/RFID reader integration) is outside what this environment can build;
    /// the verification workflow, register-matching logic, and audit trail are real -- "ScannedTagValue"
    /// is whatever string a barcode scanner, RFID reader, or manual entry produces.
    /// </summary>
    public class AssetVerificationService
    {
        private readonly ApexErpDbContext _db;

        public AssetVerificationService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<AssetVerificationBatch> StartBatchAsync(Guid companyId, string? location, Guid performedBy, CancellationToken ct = default)
        {
            var prefix = $"AVB-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:{prefix}", ct);
            var count = await _db.AssetVerificationBatches.IgnoreQueryFilters().CountAsync(b => b.CompanyId == companyId && b.BatchNo.StartsWith(prefix), ct);
            var batch = new AssetVerificationBatch
            {
                CompanyId = companyId,
                BatchNo = $"{prefix}{(count + 1):D5}",
                VerificationDate = DateTime.UtcNow,
                Location = location,
                PerformedBy = performedBy
            };
            _db.AssetVerificationBatches.Add(batch);
            await _db.SaveChangesAsync(ct);
            return batch;
        }

        /// <summary>Records one scan and immediately determines Found/Missing/ConditionChanged by
        /// matching against the fixed asset register's AssetTagValue.</summary>
        public async Task<AssetVerificationScan> RecordScanAsync(Guid companyId, Guid batchId, string scannedTagValue,
            string? observedConditionNotes, CancellationToken ct = default)
        {
            var asset = await _db.FixedAssets.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AssetTagValue == scannedTagValue, ct);

            var scan = new AssetVerificationScan
            {
                AssetVerificationBatchId = batchId,
                FixedAssetId = asset?.Id,
                ScannedTagValue = scannedTagValue,
                ConditionNotes = observedConditionNotes,
                Result = asset == null
                    ? AssetVerificationResult.Found // an unregistered tag was scanned; still "found" physically, flagged for registration review
                    : (!string.IsNullOrWhiteSpace(observedConditionNotes) ? AssetVerificationResult.ConditionChanged : AssetVerificationResult.Found)
            };
            _db.AssetVerificationScans.Add(scan);
            await _db.SaveChangesAsync(ct);
            return scan;
        }

        /// <summary>Closes the batch and flags every active, tagged asset that was never scanned as Missing.</summary>
        public async Task<AssetVerificationBatch> CompleteBatchAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
        {
            var batch = await _db.AssetVerificationBatches.Include(b => b.Scans)
                .FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Verification batch not found.");

            var scannedAssetIds = batch.Scans.Where(s => s.FixedAssetId.HasValue).Select(s => s.FixedAssetId!.Value).ToHashSet();
            var allTaggedAssets = await _db.FixedAssets
                .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active && a.AssetTagValue != null)
                .ToListAsync(ct);

            foreach (var asset in allTaggedAssets.Where(a => !scannedAssetIds.Contains(a.Id)))
            {
                batch.Scans.Add(new AssetVerificationScan
                {
                    AssetVerificationBatchId = batch.Id,
                    FixedAssetId = asset.Id,
                    ScannedTagValue = asset.AssetTagValue!,
                    Result = AssetVerificationResult.Missing
                });
            }

            batch.IsComplete = true;
            await _db.SaveChangesAsync(ct);
            return batch;
        }
    }
}
