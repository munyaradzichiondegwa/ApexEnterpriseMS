using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateArInvoiceRequest(Guid CustomerId, DateTime InvoiceDate, string Currency, decimal ExchangeRate,
        List<ArInvoiceLineRequest> Lines, Guid VatControlAccountId, Guid? GrantId);
    public record PostArInvoiceRequest(Guid ArControlAccountId, Guid VatOutputAccountId, bool FiscaliseWithFdms = false);
    public record ArReceiptRequest(Guid CustomerId, DateTime ReceiptDate, string Currency, decimal Amount,
        PaymentMethod Method, string? Reference, Guid BankGlAccountId, Guid ArControlAccountId, Dictionary<Guid, decimal> Allocations);

    [Route("api/v1/ar")]
    public class ArController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly ArService _ar;
        private readonly FdmsService _fdms;
        private readonly InterCompanyService _interCompany;

        public ArController(ApexErpDbContext db, ArService ar, FdmsService fdms, InterCompanyService interCompany)
        {
            _db = db;
            _ar = ar;
            _fdms = fdms;
            _interCompany = interCompany;
        }

        [HttpGet("customers")]
        public async Task<IActionResult> GetCustomers() => Ok(await _db.Customers.Where(c => c.CompanyId == CompanyId).ToListAsync());

        [HttpPost("customers")]
        public async Task<IActionResult> CreateCustomer([FromBody] Customer c)
        {
            c.CompanyId = CompanyId;
            _db.Customers.Add(c);
            await _db.SaveChangesAsync();
            return Ok(c);
        }

        [HttpGet("invoices")]
        public async Task<IActionResult> GetInvoices() =>
            Ok(await _db.ArInvoices.Include(i => i.Lines).Include(i => i.Customer).Where(i => i.CompanyId == CompanyId)
                .OrderByDescending(i => i.InvoiceDate).Take(200).ToListAsync());

        [HttpPost("invoices")]
        public async Task<IActionResult> CreateInvoice(CreateArInvoiceRequest req)
        {
            try
            {
                var invoice = await _ar.CreateInvoiceAsync(CompanyId, req.CustomerId, req.InvoiceDate, req.Currency,
                    req.ExchangeRate, req.Lines, req.VatControlAccountId, req.GrantId);
                return Ok(invoice);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>Posts the invoice to the GL. If the customer is linked to another group company,
        /// automatically creates the mirror AP invoice there (PRD Module 2). If FiscaliseWithFdms is
        /// set, also queues real ECDSA signing + QR generation for ZIMRA fiscalisation (Module 9) --
        /// see FdmsService for exactly what's real vs. placeholder in that step.</summary>
        [HttpPost("invoices/{id}/post")]
        public async Task<IActionResult> PostInvoice(Guid id, PostArInvoiceRequest req)
        {
            try
            {
                var invoice = await _ar.PostInvoiceAsync(CompanyId, id, req.ArControlAccountId, req.VatOutputAccountId);

                var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == invoice.CustomerId && c.CompanyId == CompanyId);
                if (customer?.LinkedCompanyId != null)
                {
                    try { await _interCompany.CreateMirrorPostingAsync(CompanyId, invoice.Id); }
                    catch (InvalidOperationException) { /* mirror rule not configured yet -- invoice still posts locally */ }
                }

                if (req.FiscaliseWithFdms)
                    await _fdms.FiscaliseInvoiceAsync(CompanyId, invoice.Id);

                return Ok(invoice);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("receipts")]
        public async Task<IActionResult> CreateReceipt(ArReceiptRequest req)
        {
            try
            {
                var receipt = await _ar.ReceiveAndAllocateAsync(CompanyId, req.CustomerId, req.ReceiptDate, req.Currency,
                    req.Amount, req.Method, req.Reference, req.BankGlAccountId, req.ArControlAccountId, req.Allocations);
                return Ok(receipt);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("ageing")]
        public async Task<IActionResult> GetAgeing([FromQuery] DateTime? asOf)
        {
            var result = await _ar.GetAgeingAsync(CompanyId, asOf ?? DateTime.UtcNow);
            return Ok(result.Select(r => new { r.CustomerId, r.CustomerName, r.Current, r.Days30, r.Days60, r.Days90Plus, r.Total }));
        }
    }
}
