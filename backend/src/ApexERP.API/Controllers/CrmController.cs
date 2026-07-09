using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateSalesOrderRequest(Guid CustomerId, DateTime OrderDate, List<SalesOrderLineRequest> Lines);
    public record ConvertToInvoiceRequest(Guid IncomeAccountId, Guid VatControlAccountId);

    [Route("api/v1/crm")]
    public class CrmController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly CrmService _crm;

        public CrmController(ApexErpDbContext db, CrmService crm)
        {
            _db = db;
            _crm = crm;
        }

        [HttpGet("leads")]
        public async Task<IActionResult> GetLeads() => Ok(await _db.CrmLeads.Where(l => l.CompanyId == CompanyId).ToListAsync());

        [HttpPost("leads")]
        public async Task<IActionResult> CreateLead([FromBody] CrmLead lead)
        {
            lead.CompanyId = CompanyId;
            _db.CrmLeads.Add(lead);
            await _db.SaveChangesAsync();
            return Ok(lead);
        }

        [HttpPost("leads/{id}/status")]
        public async Task<IActionResult> UpdateLeadStatus(Guid id, [FromBody] LeadStatus status)
        {
            try
            {
                var lead = await _crm.ConvertLeadStatusAsync(CompanyId, id, status);
                return Ok(lead);
            }
            catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpGet("sales-orders")]
        public async Task<IActionResult> GetSalesOrders() =>
            Ok(await _db.SalesOrders.Include(o => o.Lines).Where(o => o.CompanyId == CompanyId).OrderByDescending(o => o.OrderDate).ToListAsync());

        [HttpPost("sales-orders")]
        public async Task<IActionResult> CreateSalesOrder(CreateSalesOrderRequest req)
        {
            var order = await _crm.CreateSalesOrderAsync(CompanyId, req.CustomerId, req.OrderDate, req.Lines);
            return Ok(order);
        }

        [HttpPost("sales-orders/{id}/confirm")]
        public async Task<IActionResult> ConfirmSalesOrder(Guid id)
        {
            var order = await _db.SalesOrders.FirstOrDefaultAsync(o => o.Id == id && o.CompanyId == CompanyId);
            if (order == null) return NotFound();
            order.Status = SalesOrderStatus.Confirmed;
            await _db.SaveChangesAsync();
            return Ok(order);
        }

        [HttpPost("sales-orders/{id}/invoice")]
        public async Task<IActionResult> ConvertToInvoice(Guid id, ConvertToInvoiceRequest req)
        {
            try
            {
                var invoice = await _crm.ConvertToInvoiceAsync(CompanyId, id, req.IncomeAccountId, req.VatControlAccountId);
                return Ok(invoice);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
