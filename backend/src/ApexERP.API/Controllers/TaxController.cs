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

    [Route("api/v1/tax")]
    public class TaxController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly TaxComplianceService _tax;
        private readonly ZimbabweDswtWithholdingRegisterService _dswt;

        public TaxController(ApexErpDbContext db, TaxComplianceService tax, ZimbabweDswtWithholdingRegisterService dswt)
        {
            _db = db;
            _tax = tax;
            _dswt = dswt;
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
    }
}
