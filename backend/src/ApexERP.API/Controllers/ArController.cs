using ApexERP.Application.Security;
using ApexERP.API.Authorization;
using ApexERP.API.Common;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateArInvoiceRequest(Guid CustomerId, DateTime InvoiceDate, string Currency, decimal ExchangeRate,
        List<ArInvoiceLineRequest> Lines, Guid VatControlAccountId, Guid? GrantId, Guid? FundCentreId = null);
    public record PostArInvoiceRequest(Guid ArControlAccountId, Guid VatOutputAccountId, bool FiscaliseWithFdms = false);
    public record ArReceiptRequest(Guid CustomerId, DateTime ReceiptDate, string Currency, decimal Amount,
        PaymentMethod Method, string? Reference, Guid BankGlAccountId, Guid ArControlAccountId, Dictionary<Guid, decimal> Allocations);

    public record CreateCustomerRequest(string Code, string Name, string? Email, string? Phone, string? Currency, decimal CreditLimit, Guid? ArAccountId, string? VatNo,
        int PaymentTermsDays, string? Tin = null, string? LegalName = null, string? FiscalAddress = null);

    [Route("api/v1/ar")]
    public class ArController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly ArService _ar;
        private readonly FdmsService _fdms;
        private readonly ReceivablesControlService _credit;
        private readonly InterCompanyService _interCompany;

        public ArController(ApexErpDbContext db, ArService ar, FdmsService fdms, InterCompanyService interCompany, ReceivablesControlService credit)
        {
            _db = db;
            _ar = ar;
            _fdms = fdms;
            _interCompany = interCompany;
            _credit = credit;
        }

        [HttpGet("customers")]
        public async Task<IActionResult> GetCustomers() => Ok(await _db.Customers.Where(c => c.CompanyId == CompanyId).ToListAsync());

        [HttpPost("customers")]
        public async Task<IActionResult> CreateCustomer(CreateCustomerRequest r)
        {
            // A request model, not the entity: the client cannot set the id, the tenant or the inter-company link.
            if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) return BadRequest(new { error = "A customer code and name are required." });
            if (r.CreditLimit < 0) return BadRequest(new { error = "The credit limit cannot be negative." });
            if (await _db.Customers.AnyAsync(x => x.CompanyId == CompanyId && x.Code == r.Code.Trim())) return Conflict(new { error = $"Customer code {r.Code.Trim()} already exists." });
            var c = new Customer
            {
                CompanyId = CompanyId, Code = r.Code.Trim(), Name = r.Name.Trim(), Email = r.Email, Phone = r.Phone, Currency = string.IsNullOrWhiteSpace(r.Currency) ? "USD" : r.Currency,
                CreditLimit = r.CreditLimit, ArAccountId = r.ArAccountId, VatNo = r.VatNo, PaymentTermsDays = r.PaymentTermsDays, Tin = r.Tin, LegalName = r.LegalName, FiscalAddress = r.FiscalAddress, CreatedBy = UserId
            };
            _db.Customers.Add(c);
            await _db.SaveChangesAsync();
            return Ok(c);
        }

        /// <summary>Without <c>page</c> the latest 200 invoices come back as before; with <c>page</c> (and optional <c>pageSize</c>, max 200) the response is paged.</summary>
        [HttpGet("invoices")]
        public async Task<IActionResult> GetInvoices([FromQuery] int? page, [FromQuery] int pageSize = 50)
        {
            var q = _db.ArInvoices.Include(i => i.Lines).Include(i => i.Customer).Where(i => i.CompanyId == CompanyId).OrderByDescending(i => i.InvoiceDate).ThenBy(i => i.Id);
            return page == null ? Ok(await q.Take(200).ToListAsync()) : Ok(await q.ToPagedAsync(page, pageSize));
        }

        [HttpPost("invoices")]
        public async Task<IActionResult> CreateInvoice(CreateArInvoiceRequest req)
        {
            try
            {
                var invoice = await _ar.CreateInvoiceAsync(CompanyId, req.CustomerId, req.InvoiceDate, req.Currency,
                    req.ExchangeRate, req.Lines, req.VatControlAccountId, req.GrantId, fundCentreId: req.FundCentreId);
                return Ok(invoice);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>Posts the invoice to the GL. If the customer is linked to another group company,
        /// automatically creates the mirror AP invoice there (PRD Module 2); the response's interCompany.status is
        /// NotApplicable, Completed or Failed (a Failed mirror is queued as an exception and can be retried). If FiscaliseWithFdms is
        /// set, also queues real ECDSA signing + QR generation for ZIMRA fiscalisation (Module 9) --
        /// see FdmsService for exactly what's real vs. placeholder in that step.</summary>
        [HttpPost("invoices/{id}/post")]
        [HasPermission(Permissions.ArInvoicesPost)]
        public async Task<IActionResult> PostInvoice(Guid id, PostArInvoiceRequest req)
        {
            try
            {
                // Credit control (spec 17): the sale may not take the customer over their limit unless an approved override covers it.
                var draft = await _db.ArInvoices.FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == CompanyId);
                if (draft != null && draft.Status == InvoiceStatus.Draft)
                    await _credit.EnsureWithinCreditLimitAsync(CompanyId, draft.CustomerId, draft.Total * draft.ExchangeRate);

                var invoice = await _ar.PostInvoiceAsync(CompanyId, id, req.ArControlAccountId, req.VatOutputAccountId);

                // A failed inter-company mirror is NEVER swallowed: the local invoice stays posted, an exception item
                // is raised for follow-up/retry (Controls > Exceptions), and the response says so explicitly.
                var interCompany = await _interCompany.TryMirrorAsync(CompanyId, invoice.Id, UserId);

                if (req.FiscaliseWithFdms)
                    await _fdms.FiscaliseInvoiceAsync(CompanyId, invoice.Id);

                return Ok(new { invoice, interCompany });
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
