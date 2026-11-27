using ApexERP.Application.Security;
using ApexERP.API.Authorization;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateFundCentreRequest(string Code, string Name, FundCentreLevel Level, Guid? ParentId, int FinancialYear, decimal AppropriationAmount, decimal CarriedForward);
    public record CommitRequest(Guid FundCentreId, Guid PurchaseOrderId, decimal Amount, bool AllowOverride, string? OverrideReason = null);
    public record SubmitBudgetOverrideApprovalRequest(Guid FundCentreId, Guid PurchaseOrderId, decimal Amount, string Reason);
    public record CreateTenderRequest(string Title, string? Specifications, DateTime SubmissionDeadline);
    public record SubmitBidRequest(Guid VendorId, decimal PriceAmount);
    public record ScoreBidRequest(decimal PriceScore, decimal TechnicalScore, decimal LocalContentScore, decimal PriceWeight, decimal TechnicalWeight, decimal LocalContentWeight);
    public record AwardTenderRequest(Guid WinningVendorId, string Justification);
    public record SubmitTenderAwardApprovalRequest(Guid WinningVendorId);
    public record RecordScanRequest(string ScannedTagValue, string? ConditionNotes);

    [Route("api/v1/public-sector")]
    public class PublicSectorController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly PublicSectorService _publicSector;
        private readonly TenderService _tender;
        private readonly AssetVerificationService _assetVerification;
        private readonly ApprovalChainService _approvalChains;

        public PublicSectorController(ApexErpDbContext db, PublicSectorService publicSector, TenderService tender,
            AssetVerificationService assetVerification, ApprovalChainService approvalChains)
        {
            _db = db;
            _publicSector = publicSector;
            _tender = tender;
            _assetVerification = assetVerification;
            _approvalChains = approvalChains;
        }

        // ---- Fund Centres & Commitments ----

        [HttpGet("fund-centres")]
        public async Task<IActionResult> GetFundCentres([FromQuery] int? year)
        {
            var q = _db.FundCentres.Where(f => f.CompanyId == CompanyId);
            if (year.HasValue) q = q.Where(f => f.FinancialYear == year.Value);
            return Ok(await q.OrderBy(f => f.Code).ToListAsync());
        }

        [HttpPost("fund-centres")]
        public async Task<IActionResult> CreateFundCentre(CreateFundCentreRequest req)
        {
            try
            {
                var fc = await _publicSector.CreateFundCentreAsync(CompanyId, req.Code, req.Name, req.Level, req.ParentId, req.FinancialYear, req.AppropriationAmount, req.CarriedForward);
                return Ok(fc);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("fund-centres/{id}/utilization")]
        public async Task<IActionResult> GetUtilization(Guid id)
        {
            try { return Ok(await _publicSector.GetBudgetUtilizationAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("fund-centres/utilization")]
        public async Task<IActionResult> GetAllUtilization([FromQuery] int year) => Ok(await _publicSector.GetAllBudgetUtilizationAsync(CompanyId, year));

        /// <summary>Vote/program/sub-program expenditure reconciled to posted general-ledger actuals.</summary>
        [HttpGet("fund-centres/expenditure-report")]
        [HasPermission(Permissions.ReportsFinancialRead)]
        public async Task<IActionResult> GetExpenditureReport([FromQuery] int year)
        {
            try { return Ok(await _publicSector.GetExpenditureReportAsync(CompanyId, year)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("commitments/override/submit-for-approval")]
        [HasPermission(Permissions.PublicSectorBudgetOverride)]
        public async Task<IActionResult> SubmitBudgetOverrideForApproval(SubmitBudgetOverrideApprovalRequest req)
        {
            try
            {
                if (req.Amount <= 0 || string.IsNullOrWhiteSpace(req.Reason))
                    return BadRequest(new { error = "A positive override amount and documented reason are required." });
                var fundCentre = await _db.FundCentres.FirstOrDefaultAsync(f => f.Id == req.FundCentreId && f.CompanyId == CompanyId);
                var purchaseOrder = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == req.PurchaseOrderId && p.CompanyId == CompanyId);
                if (fundCentre == null || purchaseOrder == null)
                    return NotFound(new { error = "The fund centre or purchase order was not found in this company." });
                if (purchaseOrder.Status != PurchaseOrderStatus.Approved || purchaseOrder.FundCentreId != req.FundCentreId ||
                    purchaseOrder.Total != req.Amount)
                    return BadRequest(new { error = "The override request must match an approved purchase order assigned to the selected fund centre and its exact total." });
                var workflow = await _approvalChains.SubmitForApprovalAsync(CompanyId, "PublicSectorBudgetOverride",
                    purchaseOrder.Id, "PurchaseOrder", UserId, req.Amount, req.FundCentreId, req.Reason.Trim());
                return Ok(workflow);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("commitments")]
        public async Task<IActionResult> Commit(CommitRequest req)
        {
            try
            {
                if (req.AllowOverride && !HasPermission(Permissions.PublicSectorBudgetOverride))
                    return Forbid();
                if (req.AllowOverride && string.IsNullOrWhiteSpace(req.OverrideReason))
                    return BadRequest(new { error = "An override justification is required." });
                var commitment = await _publicSector.CommitAsync(CompanyId, req.FundCentreId, req.PurchaseOrderId,
                    req.Amount, req.AllowOverride, req.OverrideReason, UserId,
                    HttpContext.Connection.RemoteIpAddress?.ToString());
                return Ok(commitment);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("fund-centres/{id}/year-end-close")]
        [HasPermission(Permissions.PublicSectorYearEndClose)]
        public async Task<IActionResult> YearEndClose(Guid id, [FromQuery] bool carryForward)
        {
            try { return Ok(await _publicSector.CloseYearEndAsync(CompanyId, id, carryForward)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---- E-Tendering ----

        [HttpGet("tenders")]
        public async Task<IActionResult> GetTenders() => Ok(await _db.TenderNotices.Include(t => t.Bids).Where(t => t.CompanyId == CompanyId).ToListAsync());

        [HttpPost("tenders")]
        public async Task<IActionResult> CreateTender(CreateTenderRequest req) =>
            Ok(await _tender.CreateTenderAsync(CompanyId, req.Title, req.Specifications, req.SubmissionDeadline));

        [HttpPost("tenders/{id}/bids")]
        public async Task<IActionResult> SubmitBid(Guid id, SubmitBidRequest req) =>
            Ok(await _tender.SubmitBidAsync(CompanyId, id, req.VendorId, req.PriceAmount));

        [HttpPost("tenders/{id}/evaluation")]
        [HasPermission(Permissions.PublicSectorTendersAward)]
        public async Task<IActionResult> BeginTenderEvaluation(Guid id)
        {
            try { return Ok(await _tender.BeginEvaluationAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("tenders/bids/{bidId}/score")]
        public async Task<IActionResult> ScoreBid(Guid bidId, ScoreBidRequest req)
        {
            try
            {
                var bid = await _tender.ScoreBidAsync(CompanyId, bidId, req.PriceScore, req.TechnicalScore, req.LocalContentScore,
                    req.PriceWeight, req.TechnicalWeight, req.LocalContentWeight);
                return Ok(bid);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("tenders/{id}/award/submit-for-approval")]
        [HasPermission(Permissions.PublicSectorTendersAward)]
        public async Task<IActionResult> SubmitTenderAwardForApproval(Guid id, SubmitTenderAwardApprovalRequest req)
        {
            try { return Ok(await _tender.SubmitAwardForApprovalAsync(CompanyId, id, req.WinningVendorId, UserId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("tenders/{id}/award")]
        [HasPermission(Permissions.PublicSectorTendersAward)]
        public async Task<IActionResult> AwardTender(Guid id, AwardTenderRequest req) =>
            Ok(await _tender.AwardAsync(CompanyId, id, req.WinningVendorId, req.Justification));

        // ---- Asset Verification ----

        [HttpPost("asset-verification/batches")]
        public async Task<IActionResult> StartVerificationBatch([FromQuery] string? location) =>
            Ok(await _assetVerification.StartBatchAsync(CompanyId, location, UserId));

        [HttpPost("asset-verification/batches/{id}/scan")]
        public async Task<IActionResult> RecordScan(Guid id, RecordScanRequest req) =>
            Ok(await _assetVerification.RecordScanAsync(CompanyId, id, req.ScannedTagValue, req.ConditionNotes));

        [HttpPost("asset-verification/batches/{id}/complete")]
        public async Task<IActionResult> CompleteBatch(Guid id)
        {
            try { return Ok(await _assetVerification.CompleteBatchAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("asset-verification/batches")]
        public async Task<IActionResult> GetBatches() =>
            Ok(await _db.AssetVerificationBatches.Include(b => b.Scans).Where(b => b.CompanyId == CompanyId).OrderByDescending(b => b.VerificationDate).ToListAsync());
    }
}
