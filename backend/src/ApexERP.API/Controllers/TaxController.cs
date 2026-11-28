using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public sealed record ZimbabweDswtCertificateRequest(string CertificateNumber, DateTime IssuedAt);
    public sealed record ZimbabweDswtRemittanceRequest(DateTime RemittedAt, string EvidenceReference);
    public sealed record ZimbabweDswtApPaymentReconciliationRequest(Guid ApPaymentId);
    public sealed record ZimbabweDswtBankReconciliationRequest(Guid StatementTransactionId);

    [Route("api/v1/tax")]
    public class TaxController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly TaxComplianceService _tax;
        private readonly ZimbabweDswtWithholdingRegisterService _dswt;
        private readonly ZimbabweDswtFilingWorkflowService _dswtFilings;

        public TaxController(ApexErpDbContext db, TaxComplianceService tax,
            ZimbabweDswtWithholdingRegisterService dswt, ZimbabweDswtFilingWorkflowService dswtFilings)
        {
            _db = db;
            _tax = tax;
            _dswt = dswt;
            _dswtFilings = dswtFilings;
        }

        [HttpGet("codes")]
        public async Task<IActionResult> GetTaxCodes() => Ok(await _db.TaxCodes.Where(t => t.CompanyId == CompanyId).ToListAsync());

        [HttpPost("codes")]
        public async Task<IActionResult> CreateTaxCode([FromBody] TaxCode t)
        {
            t.CompanyId = CompanyId;
            _db.TaxCodes.Add(t);
            await _db.SaveChangesAsync();
            return Ok(t);
        }

        /// <summary>ZIMRA VAT return: Output VAT (from AR sales) minus Input VAT (from AP purchases)
        /// for the period = net amount payable/(refundable). Actual e-filing submission to the
        /// ZIMRA portal is a placeholder integration -- see README "External Integrations".</summary>
        [HttpGet("vat-return")]
        public async Task<IActionResult> GetVatReturn([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var result = await _tax.GenerateVatReturnAsync(CompanyId, from, to);
            return Ok(result);
        }

        [HttpGet("wht-report")]
        public async Task<IActionResult> GetWhtReport([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var result = await _tax.GenerateWhtReportAsync(CompanyId, from, to);
            return Ok(result);
        }
        /// <summary>Records an intermediary-confirmed DSWT deduction with its source evidence.</summary>
        [HttpPost("dswt/withholdings")]
        public async Task<IActionResult> RecordDswtWithholding([FromBody] ZimbabweDswtRecordRequest request)
        {
            var record = await _dswt.RecordWithholdingAsync(CompanyId, request);
            return Ok(record);
        }


        /// <summary>Returns a USD-validated DSWT monthly preparation summary; it does not submit to TaRMS.</summary>
        [HttpGet("dswt/monthly-return")]
        public async Task<IActionResult> GetDswtMonthlyReturn(
            [FromQuery] int year, [FromQuery] int month, [FromQuery] string tin)
        {
            try
            {
                return Ok(await _dswt.GenerateMonthlyReturnSummaryAsync(CompanyId, year, month, tin));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>Downloads the monthly DSWT preparation schedule as CSV for review before TaRMS filing.</summary>
        [HttpGet("dswt/monthly-return.csv")]
        public async Task<IActionResult> ExportDswtMonthlyReturnCsv(
            [FromQuery] int year, [FromQuery] int month, [FromQuery] string tin)
        {
            try
            {
                var csv = await _dswt.GenerateMonthlyReturnCsvAsync(CompanyId, year, month, tin);
                return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv",
                    $"DSWT_{year}_{month:00}_preparation.csv");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>Returns the company-scoped DSWT monthly register for reconciliation and TaRMS preparation.</summary>
        [HttpGet("dswt/monthly-register")]
        public async Task<IActionResult> GetDswtMonthlyRegister([FromQuery] int year, [FromQuery] int month)
        {
            return Ok(await _dswt.GenerateMonthlyRegisterAsync(CompanyId, year, month));
        }


        [HttpPut("dswt/withholdings/{id:guid}/ap-payment-reconciliation")]
        public async Task<IActionResult> ReconcileDswtToApPayment(Guid id, [FromBody] ZimbabweDswtApPaymentReconciliationRequest request)
        {
            return Ok(await _dswt.ReconcileToApPaymentAsync(CompanyId, id, request.ApPaymentId));
        }


        [HttpPut("dswt/withholdings/{id:guid}/bank-reconciliation")]
        public async Task<IActionResult> ReconcileDswtToBankStatement(Guid id, [FromBody] ZimbabweDswtBankReconciliationRequest request)
        {
            return Ok(await _dswt.ReconcileToBankStatementAsync(CompanyId, id, request.StatementTransactionId));
        }

        [HttpPut("dswt/withholdings/{id:guid}/certificate")]
        public async Task<IActionResult> IssueDswtCertificate(Guid id, [FromBody] ZimbabweDswtCertificateRequest request)
        {
            return Ok(await _dswt.IssueCertificateAsync(CompanyId, id, request.CertificateNumber, request.IssuedAt));
        }

        [HttpPut("dswt/withholdings/{id:guid}/remittance")]
        public async Task<IActionResult> MarkDswtRemitted(Guid id, [FromBody] ZimbabweDswtRemittanceRequest request)
        {
            return Ok(await _dswt.MarkRemittedToZimraAsync(CompanyId, id, request.RemittedAt, request.EvidenceReference));
        }

        /// <summary>Creates an immutable DSWT return snapshot for subsequent manual TaRMS filing.</summary>
        [HttpPost("dswt/filings/prepare")]
        public async Task<IActionResult> PrepareDswtFiling(
            [FromBody] ZimbabweDswtFilingPreparationRequest request, CancellationToken ct)
        {
            try { return Ok(await _dswtFilings.PrepareAsync(CompanyId, request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("dswt/filings/{id:guid}")]
        public async Task<IActionResult> GetDswtFiling(Guid id, CancellationToken ct)
        {
            var filing = await _dswtFilings.GetAsync(CompanyId, id, ct);
            return filing is null ? NotFound() : Ok(filing);
        }

        /// <summary>Records a human-confirmed TaRMS submission and its evidence; no API transmission occurs.</summary>
        [HttpPut("dswt/filings/{id:guid}/submission")]
        public async Task<IActionResult> MarkDswtFilingSubmitted(
            Guid id, [FromBody] ZimbabweDswtFilingSubmissionRequest request, CancellationToken ct)
        {
            try { return Ok(await _dswtFilings.MarkSubmittedAsync(CompanyId, id, request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPut("dswt/filings/{id:guid}/acknowledgement")]
        public async Task<IActionResult> RecordDswtFilingAcknowledgement(
            Guid id, [FromBody] ZimbabweDswtFilingAcknowledgementRequest request, CancellationToken ct)
        {
            try { return Ok(await _dswtFilings.RecordAcknowledgementAsync(CompanyId, id, request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPut("dswt/filings/{id:guid}/payment")]
        public async Task<IActionResult> ConfirmDswtFilingPayment(
            Guid id, [FromBody] ZimbabweDswtFilingPaymentRequest request, CancellationToken ct)
        {
            try { return Ok(await _dswtFilings.ConfirmPaymentAsync(CompanyId, id, request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { return BadRequest(new { error = ex.Message }); }
        }
    }
}
