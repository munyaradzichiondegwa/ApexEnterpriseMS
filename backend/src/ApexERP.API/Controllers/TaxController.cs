using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    [Route("api/v1/tax")]
    public class TaxController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly TaxComplianceService _tax;

        public TaxController(ApexErpDbContext db, TaxComplianceService tax)
        {
            _db = db;
            _tax = tax;
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
    }
}
