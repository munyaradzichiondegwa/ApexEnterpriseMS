using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record SalesOrderLineRequest(Guid InventoryItemId, decimal Quantity, decimal UnitPrice);

    /// <summary>PRD Module 12 (CRM & Sales): lead pipeline management and sales order to AR-invoice conversion.</summary>
    public class CrmService
    {
        private readonly ApexErpDbContext _db;
        private readonly ArService _ar;

        public CrmService(ApexErpDbContext db, ArService ar)
        {
            _db = db;
            _ar = ar;
        }

        public async Task<CrmLead> ConvertLeadStatusAsync(Guid companyId, Guid leadId, LeadStatus newStatus, CancellationToken ct = default)
        {
            var lead = await _db.CrmLeads.FirstOrDefaultAsync(l => l.Id == leadId && l.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Lead not found.");
            lead.Status = newStatus;
            await _db.SaveChangesAsync(ct);
            return lead;
        }

        public async Task<SalesOrder> CreateSalesOrderAsync(Guid companyId, Guid customerId, DateTime orderDate,
            IEnumerable<SalesOrderLineRequest> lines, CancellationToken ct = default)
        {
            var lineEntities = lines.Select(l => new SalesOrderLine
            {
                InventoryItemId = l.InventoryItemId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                LineTotal = Math.Round(l.Quantity * l.UnitPrice, 6)
            }).ToList();

            var prefix = $"SO-{DateTime.UtcNow:yyyy}-";
            // Serialise concurrent number generation (the lock lasts until the request's transaction ends).
            await _db.TryAcquireTransactionLockAsync($"docno:{companyId:N}:{prefix}", ct);
            var count = await _db.SalesOrders.IgnoreQueryFilters().CountAsync(o => o.CompanyId == companyId && o.OrderNo.StartsWith(prefix), ct);

            var order = new SalesOrder
            {
                CompanyId = companyId,
                OrderNo = $"{prefix}{(count + 1):D5}",
                CustomerId = customerId,
                OrderDate = orderDate,
                Status = SalesOrderStatus.Draft,
                Total = lineEntities.Sum(l => l.LineTotal),
                Lines = lineEntities
            };
            _db.SalesOrders.Add(order);
            await _db.SaveChangesAsync(ct);
            return order;
        }

        /// <summary>Converts a confirmed sales order into a draft AR invoice, carrying over lines
        /// (no VAT code applied here -- assigned by the AR clerk before posting, per PRD workflow).</summary>
        public async Task<ArInvoice> ConvertToInvoiceAsync(Guid companyId, Guid salesOrderId, Guid incomeAccountId,
            Guid vatControlAccountId, CancellationToken ct = default)
        {
            var order = await _db.SalesOrders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.Id == salesOrderId && o.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Sales order not found.");
            if (order.Status != SalesOrderStatus.Confirmed)
                throw new InvalidOperationException("Only confirmed sales orders can be invoiced.");

            var items = await _db.InventoryItems.Where(i => order.Lines.Select(l => l.InventoryItemId).Contains(i.Id)).ToListAsync(ct);
            var lineRequests = order.Lines.Select(l =>
            {
                var item = items.First(i => i.Id == l.InventoryItemId);
                return new ArInvoiceLineRequest(item.Name, l.Quantity, l.UnitPrice, null, item.SalesAccountId ?? incomeAccountId);
            });

            var invoice = await _ar.CreateInvoiceAsync(companyId, order.CustomerId, DateTime.UtcNow, "USD", 1m, lineRequests, vatControlAccountId, null, ct);
            order.ArInvoiceId = invoice.Id;
            order.Status = SalesOrderStatus.Invoiced;
            await _db.SaveChangesAsync(ct);
            return invoice;
        }
    }

    /// <summary>PRD Module 13 (Project Management): project cost tracking via approved timesheets
    /// rolled up against budget, feeding cost-centre reporting.</summary>
    public class ProjectService
    {
        private readonly ApexErpDbContext _db;

        public ProjectService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<Timesheet> SubmitTimesheetAsync(Guid companyId, Guid employeeId, Guid projectId, Guid? taskId,
            DateTime date, decimal hours, string? description, CancellationToken ct = default)
        {
            if (hours <= 0 || hours > 24) throw new InvalidOperationException("Hours must be between 0 and 24.");
            var ts = new Timesheet
            {
                CompanyId = companyId,
                EmployeeId = employeeId,
                ProjectId = projectId,
                TaskId = taskId,
                Date = date,
                Hours = hours,
                Description = description,
                IsApproved = false
            };
            _db.Timesheets.Add(ts);
            await _db.SaveChangesAsync(ct);
            return ts;
        }

        public async Task<Timesheet> ApproveTimesheetAsync(Guid companyId, Guid timesheetId, Guid approvedBy, CancellationToken ct = default)
        {
            var ts = await _db.Timesheets.FirstOrDefaultAsync(t => t.Id == timesheetId && t.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Timesheet not found.");
            ts.IsApproved = true;
            ts.ApprovedBy = approvedBy;
            await _db.SaveChangesAsync(ct);
            return ts;
        }

        /// <summary>Budget vs actual: sums approved timesheet hours * bill rate against the project budget.</summary>
        public async Task<(decimal BudgetedCost, decimal ActualCost, decimal VariancePct)> GetBudgetVsActualAsync(
            Guid companyId, Guid projectId, CancellationToken ct = default)
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Project not found.");

            var actual = await _db.Timesheets
                .Where(t => t.ProjectId == projectId && t.IsApproved)
                .SumAsync(t => t.Hours * (t.BillRate ?? 0), ct);

            var variance = project.Budget == 0 ? 0 : Math.Round((actual - project.Budget) / project.Budget * 100, 2);
            return (project.Budget, actual, variance);
        }
    }
}
