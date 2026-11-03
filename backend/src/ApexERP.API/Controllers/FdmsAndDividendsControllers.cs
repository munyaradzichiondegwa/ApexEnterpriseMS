using ApexERP.Application.Security;
using ApexERP.API.Authorization;
using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record DeclareDividendRequest(DateTime DeclarationDate, decimal Amount, string Currency, Guid RetainedEarningsAccountId, Guid DividendsPayableAccountId);
    public record PayDividendRequest(DateTime PaymentDate, Guid DividendsPayableAccountId, Guid BankAccountId);
    public record RevalueAssetRequest(DateTime RevaluationDate, decimal FairValue, string? ValuerName);

    [Route("api/v1/fdms")]
    public class FdmsController : ApiControllerBase
    {
        private readonly FdmsService _fdms;
        private readonly ApexErpDbContext _db;

        public FdmsController(FdmsService fdms, ApexErpDbContext db)
        {
            _fdms = fdms;
            _db = db;
        }

        /// <summary>Fiscalises an already-posted AR invoice: real ECDSA signing + QR digest, queued
        /// for ZIMRA submission. See FdmsService for exactly what's real vs. placeholder here.</summary>
        [HttpPost("fiscalise/{arInvoiceId}")]
        public async Task<IActionResult> Fiscalise(Guid arInvoiceId)
        {
            try { return Ok(await _fdms.FiscaliseInvoiceAsync(CompanyId, arInvoiceId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("submissions/{id}/retry")]
        public async Task<IActionResult> Retry(Guid id)
        {
            try { return Ok(await _fdms.AttemptSubmissionAsync(CompanyId, id)); }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("queue")]
        public async Task<IActionResult> GetQueue() => Ok(await _fdms.GetPendingQueueAsync(CompanyId));

        [HttpGet("submissions")]
        public async Task<IActionResult> GetAll() => Ok(await _db.FdmsSubmissions.Where(s => s.CompanyId == CompanyId).OrderByDescending(s => s.QueuedAt).ToListAsync());
    }

    [Route("api/v1/equity")]
    public class DividendsController : ApiControllerBase
    {
        private readonly DividendService _dividends;
        private readonly ApexErpDbContext _db;
        private readonly FixedAssetService _assets;

        public DividendsController(DividendService dividends, ApexErpDbContext db, FixedAssetService assets)
        {
            _dividends = dividends;
            _db = db;
            _assets = assets;
        }

        [HttpGet("dividends")]
        public async Task<IActionResult> GetDividends() => Ok(await _db.DividendDeclarations.Where(d => d.CompanyId == CompanyId).OrderByDescending(d => d.DeclarationDate).ToListAsync());

        [HttpPost("dividends/declare")]
        [HasPermission(Permissions.EquityDividendsManage)]
        public async Task<IActionResult> Declare(DeclareDividendRequest req) =>
            Ok(await _dividends.DeclareAsync(CompanyId, req.DeclarationDate, req.Amount, req.Currency, req.RetainedEarningsAccountId, req.DividendsPayableAccountId));

        [HttpPost("dividends/{id}/pay")]
        [HasPermission(Permissions.EquityDividendsManage)]
        public async Task<IActionResult> Pay(Guid id, PayDividendRequest req)
        {
            try { return Ok(await _dividends.PayAsync(CompanyId, id, req.PaymentDate, req.DividendsPayableAccountId, req.BankAccountId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("assets/{id}/revalue")]
        [HasPermission(Permissions.FixedAssetsRevalue)]
        public async Task<IActionResult> RevalueAsset(Guid id, RevalueAssetRequest req)
        {
            try { return Ok(await _assets.RevalueAsync(CompanyId, id, req.RevaluationDate, req.FairValue, req.ValuerName)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
