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
    public record CreateApInvoiceRequest(Guid SupplierId, DateTime InvoiceDate, string Currency, decimal ExchangeRate,
        List<ApInvoiceLineRequest> Lines, Guid? FundCentreId, Guid? GrantId, Guid? CommitmentId);
    public record PostApInvoiceRequest(Guid ApControlAccountId, Guid VatInputAccountId, Guid WhtPayableAccountId);
    public record ApPaymentRequest(Guid SupplierId, DateTime PaymentDate, string Currency, decimal Amount,
        PaymentMethod Method, Guid BankGlAccountId, Guid ApControlAccountId, Dictionary<Guid, decimal> Allocations);

    [Route("api/v1/ap")]
    public class ApController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly ApService _ap;
        private readonly GrantService _grants;
        private readonly PublicSectorService _publicSector;

        public ApController(ApexErpDbContext db, ApService ap, GrantService grants, PublicSectorService publicSector)
        {
            _db = db;
            _ap = ap;
            _grants = grants;
            _publicSector = publicSector;
        }

        [HttpGet("suppliers")]
        public async Task<IActionResult> GetSuppliers() => Ok(await _db.Suppliers.Where(s => s.CompanyId == CompanyId).ToListAsync());

        [HttpPost("suppliers")]
        public async Task<IActionResult> CreateSupplier([FromBody] Supplier s)
        {
            s.CompanyId = CompanyId;
            _db.Suppliers.Add(s);
            await _db.SaveChangesAsync();
            return Ok(s);
        }

        [HttpGet("invoices")]
        public async Task<IActionResult> GetInvoices() =>
            Ok(await _db.ApInvoices.Include(i => i.Lines).Include(i => i.Supplier).Where(i => i.CompanyId == CompanyId)
                .OrderByDescending(i => i.InvoiceDate).Take(200).ToListAsync());

        /// <summary>Creates a draft AP invoice. If GrantId is supplied and that grant enforces
        /// restrictions, each line's spend is checked against its GL-account budget before the
        /// invoice is created -- a genuinely-over-budget grant expenditure is rejected outright.</summary>
        [HttpPost("invoices")]
        public async Task<IActionResult> CreateInvoice(CreateApInvoiceRequest req)
        {
            try
            {
                if (req.GrantId.HasValue)
                {
                    foreach (var line in req.Lines)
                    {
                        var lineAmount = line.Quantity * line.UnitCost;
                        if (await _grants.WouldExceedBudgetAsync(CompanyId, req.GrantId.Value, line.GlAccountId, lineAmount))
                            return BadRequest(new { error = $"Posting {lineAmount:N2} to this account would exceed the grant's approved budget for that line." });
                    }
                }

                var invoice = await _ap.CreateInvoiceAsync(CompanyId, req.SupplierId, req.InvoiceDate, req.Currency, req.ExchangeRate,
                    req.Lines, req.FundCentreId, req.GrantId, req.CommitmentId);
                return Ok(invoice);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("invoices/{id}/post")]
        [HasPermission(Permissions.ApInvoicesPost)]
        public async Task<IActionResult> PostInvoice(Guid id, PostApInvoiceRequest req)
        {
            try
            {
                var invoice = await _ap.PostInvoiceAsync(CompanyId, id, req.ApControlAccountId, req.VatInputAccountId, req.WhtPayableAccountId);

                // Public Sector: posting the actual invoice replaces the estimate -- release the commitment.
                if (invoice.CommitmentId.HasValue)
                    await _publicSector.ReleaseCommitmentForPoAsync(CompanyId, invoice.CommitmentId.Value);

                return Ok(invoice);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("payments")]
        [HasPermission(Permissions.ApPaymentsCreate)]
        public async Task<IActionResult> CreatePayment(ApPaymentRequest req)
        {
            try
            {
                var payment = await _ap.PayAndAllocateAsync(CompanyId, req.SupplierId, req.PaymentDate, req.Currency,
                    req.Amount, req.Method, req.BankGlAccountId, req.ApControlAccountId, req.Allocations);
                return Ok(payment);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("ageing")]
        public async Task<IActionResult> GetAgeing([FromQuery] DateTime? asOf)
        {
            var result = await _ap.GetAgeingAsync(CompanyId, asOf ?? DateTime.UtcNow);
            return Ok(result.Select(r => new { r.SupplierId, r.SupplierName, r.Current, r.Days30, r.Days60, r.Days90Plus, r.Total }));
        }
    }
}
