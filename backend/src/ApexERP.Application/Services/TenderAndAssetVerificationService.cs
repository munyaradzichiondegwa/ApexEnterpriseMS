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
            if (priceAmount <= 0) throw new InvalidOperationException("A bid price must be greater than zero.");
            var tender = await _db.TenderNotices.FirstOrDefaultAsync(t => t.Id == tenderNoticeId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Tender notice not found.");
            if (tender.Status != TenderStatus.Open)
                throw new InvalidOperationException("Bids can only be submitted while the tender is open.");
            var vendor = await _db.Vendors.FirstOrDefaultAsync(v => v.Id == vendorId && v.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Vendor is not registered in this company.");
            if (!vendor.IsApproved)
                throw new InvalidOperationException("Only an approved vendor may submit a tender bid.");

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

        /// <summary>Moves an expired open tender into evaluation. The server clock is authoritative.</summary>
        public async Task<TenderNotice> BeginEvaluationAsync(Guid companyId, Guid tenderNoticeId, CancellationToken ct = default)
        {
            var tender = await _db.TenderNotices.FirstOrDefaultAsync(t => t.Id == tenderNoticeId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Tender notice not found.");
            if (tender.Status != TenderStatus.Open)
                throw new InvalidOperationException("Only an open tender can enter evaluation.");
            if (DateTime.UtcNow <= tender.SubmissionDeadline)
                throw new InvalidOperationException("Tender evaluation cannot begin before the submission deadline.");
            tender.Status = TenderStatus.Evaluation;
            await _db.SaveChangesAsync(ct);
            return tender;
        }

        /// <summary>Scores a bid on price/technical/local-content criteria (weights configurable per
        /// call) and rolls them into a total score for the evaluation matrix.</summary>
        public async Task<TenderBid> ScoreBidAsync(Guid companyId, Guid bidId, decimal priceScore, decimal technicalScore,
            decimal localContentScore, decimal priceWeight, decimal technicalWeight, decimal localContentWeight, CancellationToken ct = default)
        {
            var bid = await _db.TenderBids.FirstOrDefaultAsync(b => b.Id == bidId && b.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Bid not found.");
            if (bid.IsLateRejected) throw new InvalidOperationException("This bid was submitted after the deadline and cannot be scored.");
            var tender = await _db.TenderNotices.FirstOrDefaultAsync(t => t.Id == bid.TenderNoticeId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Tender notice not found.");
            if (tender.Status != TenderStatus.Evaluation)
                throw new InvalidOperationException("A tender must be in evaluation before bids can be scored.");
            if (priceScore is < 0 or > 100 || technicalScore is < 0 or > 100 || localContentScore is < 0 or > 100)
                throw new InvalidOperationException("Each evaluation score must be between 0 and 100.");
            if (priceWeight < 0 || technicalWeight < 0 || localContentWeight < 0 ||
                Math.Abs(priceWeight + technicalWeight + localContentWeight - 1m) > 0.0001m)
                throw new InvalidOperationException("Evaluation weights must be non-negative and total exactly 100%.");

            bid.PriceScore = priceScore;
            bid.TechnicalScore = technicalScore;
            bid.LocalContentScore = localContentScore;
            bid.TotalScore = (priceScore * priceWeight) + (technicalScore * technicalWeight) + (localContentScore * localContentWeight);
            await _db.SaveChangesAsync(ct);
            return bid;
        }

        public async Task<TenderNotice> AwardAsync(Guid companyId, Guid tenderNoticeId, Guid winningVendorId, string justification, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(justification))
                throw new InvalidOperationException("An award justification is required.");
            var tender = await _db.TenderNotices.FirstOrDefaultAsync(t => t.Id == tenderNoticeId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Tender notice not found.");
            if (tender.Status != TenderStatus.Evaluation)
                throw new InvalidOperationException("Only a tender under evaluation can be awarded.");
            var winningBid = await _db.TenderBids.FirstOrDefaultAsync(b => b.CompanyId == companyId &&
                b.TenderNoticeId == tenderNoticeId && b.VendorId == winningVendorId && !b.IsLateRejected, ct)
                ?? throw new InvalidOperationException("The selected vendor has no eligible bid for this tender.");
            var winningVendor = await _db.Vendors.FirstOrDefaultAsync(v => v.Id == winningVendorId && v.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("The selected vendor is no longer registered in this company.");
            if (!winningVendor.IsApproved)
                throw new InvalidOperationException("A vendor whose approval has been revoked cannot receive a tender award.");
            if (winningBid.TotalScore <= 0)
                throw new InvalidOperationException("The selected bid must be evaluated before award.");
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
