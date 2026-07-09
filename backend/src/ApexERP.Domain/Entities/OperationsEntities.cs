using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    // ---------- Supply Chain & Procurement ----------

    public class Vendor : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Contact { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string Currency { get; set; } = "USD";
        public string? BankDetailsJson { get; set; }
        public bool IsApproved { get; set; }
        public string? Category { get; set; }
    }

    public class PurchaseOrder : TenantEntity
    {
        public string PoNo { get; set; } = string.Empty;
        public Guid VendorId { get; set; }
        public DateTime IssueDate { get; set; }
        public DateTime? ExpectedDelivery { get; set; }
        public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
        public decimal Total { get; set; }
        public Guid? ApprovedBy { get; set; }

        /// <summary>Public Sector (Module 16): fund centre this PO commits budget against.
        /// Required for public-sector entities; null for standard commercial procurement.</summary>
        public Guid? FundCentreId { get; set; }
        /// <summary>Grants & NGO (Module 15): tags this procurement to a specific grant.</summary>
        public Guid? GrantId { get; set; }

        public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
    }

    public class PurchaseOrderLine : BaseEntity
    {
        public Guid PurchaseOrderId { get; set; }
        public Guid InventoryItemId { get; set; }
        public decimal QuantityOrdered { get; set; }
        public decimal QuantityReceived { get; set; }
        public decimal UnitCost { get; set; }
        public decimal LineTotal { get; set; }
    }

    public class GoodsReceipt : TenantEntity
    {
        public string GrnNo { get; set; } = string.Empty;
        public Guid PurchaseOrderId { get; set; }
        public DateTime ReceiptDate { get; set; }
        public Guid? WarehouseId { get; set; }
        public string? Notes { get; set; }

        public ICollection<GoodsReceiptLine> Lines { get; set; } = new List<GoodsReceiptLine>();
    }

    public class GoodsReceiptLine : BaseEntity
    {
        public Guid GoodsReceiptId { get; set; }
        public Guid PurchaseOrderLineId { get; set; }
        public Guid InventoryItemId { get; set; }
        public decimal QuantityReceived { get; set; }
        public decimal UnitCost { get; set; }
    }

    // ---------- CRM & Sales ----------

    public class CrmLead : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? CompanyName { get; set; }
        public string? Source { get; set; }
        public LeadStatus Status { get; set; } = LeadStatus.New;
        public Guid? AssignedTo { get; set; }
        public decimal ValueEstimate { get; set; }
        public string? Notes { get; set; }
    }

    public class SalesOrder : TenantEntity
    {
        public string OrderNo { get; set; } = string.Empty;
        public Guid CustomerId { get; set; }
        public DateTime OrderDate { get; set; }
        public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;
        public decimal Total { get; set; }
        public Guid? ArInvoiceId { get; set; }

        public ICollection<SalesOrderLine> Lines { get; set; } = new List<SalesOrderLine>();
    }

    public class SalesOrderLine : BaseEntity
    {
        public Guid SalesOrderId { get; set; }
        public Guid InventoryItemId { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineTotal { get; set; }
    }

    // ---------- Manufacturing & Production ----------

    public class BomHeader : TenantEntity
    {
        public Guid FinishedItemId { get; set; }
        public int Version { get; set; } = 1;
        public DateTime EffectiveDate { get; set; }
        public decimal StandardBatchQty { get; set; } = 1;
        public BomStatus Status { get; set; } = BomStatus.Draft;

        public ICollection<BomLine> Lines { get; set; } = new List<BomLine>();
    }

    public class BomLine : BaseEntity
    {
        public Guid BomHeaderId { get; set; }
        public Guid ComponentItemId { get; set; }
        public decimal QtyPerUnit { get; set; }
        public string UnitOfMeasure { get; set; } = "EA";
        public decimal ScrapFactor { get; set; } // e.g. 0.05 = 5% scrap allowance
    }

    public class WorkOrder : TenantEntity
    {
        public string WoNo { get; set; } = string.Empty;
        public Guid BomHeaderId { get; set; }
        public decimal QtyToProduce { get; set; }
        public decimal QtyProduced { get; set; }
        public DateTime? ScheduledStart { get; set; }
        public DateTime? ScheduledEnd { get; set; }
        public DateTime? ActualStart { get; set; }
        public DateTime? ActualEnd { get; set; }
        public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Draft;
        public Guid? CostCentreId { get; set; }
        public decimal TotalMaterialCost { get; set; }
        public decimal TotalLabourCost { get; set; }
        public decimal TotalOverheadCost { get; set; }
    }

    // ---------- Project Management ----------

    public class Project : TenantEntity
    {
        public string ProjectNo { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Guid? ClientId { get; set; } // FK to Customer, nullable
        public Guid ManagerId { get; set; } // FK to User
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public decimal Budget { get; set; }
        public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
        public Guid? CostCentreId { get; set; }
        /// <summary>Grants & NGO (Module 15): if set, this project's timesheet/expense costs
        /// auto-allocate to the linked grant.</summary>
        public Guid? GrantId { get; set; }

        public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
    }

    public class ProjectTask : TenantEntity
    {
        public Guid ProjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid? AssignedTo { get; set; }
        public DateTime? DueDate { get; set; }
        public bool IsComplete { get; set; }
        public decimal BudgetedHours { get; set; }
    }

    public class Timesheet : TenantEntity
    {
        public Guid EmployeeId { get; set; }
        public Guid ProjectId { get; set; }
        public Guid? TaskId { get; set; }
        public DateTime Date { get; set; }
        public decimal Hours { get; set; }
        public string? Description { get; set; }
        public bool IsApproved { get; set; }
        public Guid? ApprovedBy { get; set; }
        public decimal? BillRate { get; set; }
    }
}
