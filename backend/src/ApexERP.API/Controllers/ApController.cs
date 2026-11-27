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
    public record CreateApInvoiceRequest(Guid SupplierId, DateTime InvoiceDate, string Currency, decimal ExchangeRate,
        List<ApInvoiceLineRequest> Lines, Guid? FundCentreId, Guid? GrantId, Guid? CommitmentId,
        string? SupplierInvoiceNo = null, Guid? PurchaseOrderId = null);
    public record CreateSupplierRequest(string Code, string Name, string? Email, string? Phone, string? Currency, Guid? ApAccountId, string? VatNo,
        int PaymentTermsDays, bool WhtApplicable, decimal WhtRate, string? BankName = null, string? BankAccountNo = null, string? BankBranch = null);
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

        private readonly ApControlsService _controls;
        private readonly SettingsService _settings;

        public ApController(ApexErpDbContext db, ApService ap, GrantService grants, PublicSectorService publicSector, ApControlsService controls, SettingsService settings)
        {
            _db = db;
            _ap = ap;
            _grants = grants;
            _publicSector = publicSector;
            _controls = controls;
            _settings = settings;
        }

        [HttpGet("suppliers")]
        public async Task<IActionResult> GetSuppliers() => Ok(await _db.Suppliers.Where(s => s.CompanyId == CompanyId).ToListAsync());

        [HttpPost("suppliers")]
        public async Task<IActionResult> CreateSupplier(CreateSupplierRequest r)
        {
            // A request model, not the entity: the client cannot set the id, the tenant, the payment hold or the inter-company link.
            // Bank details given here are the supplier's FIRST details; any later change goes through the verified bank-change workflow.
            if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) return BadRequest(new { error = "A supplier code and name are required." });
            if (r.WhtRate < 0 || r.WhtRate > 1) return BadRequest(new { error = "The withholding rate must be between 0 and 1 (for example 0.10 for 10%)." });
            if (await _db.Suppliers.AnyAsync(x => x.CompanyId == CompanyId && x.Code == r.Code.Trim())) return Conflict(new { error = $"Supplier code {r.Code.Trim()} already exists." });
            var s = new Supplier
            {
                CompanyId = CompanyId, Code = r.Code.Trim(), Name = r.Name.Trim(), Email = r.Email, Phone = r.Phone, Currency = string.IsNullOrWhiteSpace(r.Currency) ? "USD" : r.Currency,
                ApAccountId = r.ApAccountId, VatNo = r.VatNo, PaymentTermsDays = r.PaymentTermsDays, WhtApplicable = r.WhtApplicable, WhtRate = r.WhtRate,
                BankName = r.BankName, BankAccountNo = r.BankAccountNo, BankBranch = r.BankBranch, CreatedBy = UserId
            };
            _db.Suppliers.Add(s);
            await _db.SaveChangesAsync();
            return Ok(s);
        }

        /// <summary>Without <c>page</c> the latest 200 invoices come back as before; with <c>page</c> (and optional <c>pageSize</c>, max 200) the response is paged.</summary>
        [HttpGet("invoices")]
        public async Task<IActionResult> GetInvoices([FromQuery] int? page, [FromQuery] int pageSize = 50)
        {
            var q = _db.ApInvoices.Include(i => i.Lines).Include(i => i.Supplier).Where(i => i.CompanyId == CompanyId).OrderByDescending(i => i.InvoiceDate).ThenBy(i => i.Id);
            return page == null ? Ok(await q.Take(200).ToListAsync()) : Ok(await q.ToPagedAsync(page, pageSize));
        }

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
                    req.Lines, req.FundCentreId, req.GrantId, req.CommitmentId, default, req.SupplierInvoiceNo, req.PurchaseOrderId);
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
                    await _publicSector.ReleaseCommitmentAsync(CompanyId, invoice.CommitmentId.Value);

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
                // Supplier hold, pending bank change and unresolved three-way-match discrepancies stop a payment (spec 16).
                await _controls.ValidatePaymentAsync(CompanyId, req.SupplierId, req.Allocations);

                // Above the configured limit a payment must go through a payment run so a second person authorises it.
                var limit = await _settings.GetDecimalAsync(CompanyId, "ap.directPayment.maxAmount", decimal.MaxValue);
                if (req.Amount > limit)
                    return BadRequest(new { error = $"Payments above {limit:N2} must be made through a payment run, authorised by a second person." });

                var payment = await _ap.PayAndAllocateAsync(CompanyId, req.SupplierId, req.PaymentDate, req.Currency,
                    req.Amount, req.Method, req.BankGlAccountId, req.ApControlAccountId, req.Allocations);
                payment.PreparedBy = UserId;
                await _db.SaveChangesAsync();
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
