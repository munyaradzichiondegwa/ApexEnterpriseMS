using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    public class Warehouse : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Location { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class InventoryItem : TenantEntity
    {
        public string Sku { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string UnitOfMeasure { get; set; } = "EA";
        public decimal ReorderLevel { get; set; }
        public decimal ReorderQuantity { get; set; }
        public ValuationMethod ValuationMethod { get; set; } = ValuationMethod.AVCO;
        public Guid AssetAccountId { get; set; }
        public Guid CogsAccountId { get; set; }
        public Guid? SalesAccountId { get; set; }

        /// <summary>Denormalised running totals maintained by InventoryService for fast lookups.</summary>
        public decimal QuantityOnHand { get; set; }
        public decimal AverageCost { get; set; }
        public decimal StandardSalesPrice { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsManufactured { get; set; } // true if this item is a BOM finished good
    }

    /// <summary>Individual FIFO cost layer. One row per receipt while quantity remains > 0.</summary>
    public class InventoryCostLayer : TenantEntity
    {
        public Guid InventoryItemId { get; set; }
        public Guid? WarehouseId { get; set; }
        public DateTime ReceiptDate { get; set; }
        public decimal OriginalQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }
        public decimal UnitCost { get; set; }
        public Guid SourceMovementId { get; set; }
    }

    public class InventoryMovement : TenantEntity
    {
        public Guid InventoryItemId { get; set; }
        public Guid? WarehouseId { get; set; }
        public DateTime MovementDate { get; set; }
        public MovementType MovementType { get; set; }
        public decimal Quantity { get; set; } // positive = in, negative = out
        public decimal UnitCost { get; set; }
        public string? Reference { get; set; }
        public Guid? JournalEntryId { get; set; }
    }

    public class AssetCategory : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public DepreciationMethod DefaultMethod { get; set; } = DepreciationMethod.StraightLine;
        public int DefaultUsefulLifeMonths { get; set; } = 60;
        public Guid AssetAccountId { get; set; }
        public Guid DepreciationExpenseAccountId { get; set; }
        public Guid AccumulatedDepreciationAccountId { get; set; }
        /// <summary>IAS 16 revaluation model equity account (OCI). Optional -- only needed if this
        /// category's assets are ever revalued.</summary>
        public Guid? RevaluationSurplusEquityAccountId { get; set; }
    }

    public class FixedAsset : TenantEntity
    {
        public string AssetNo { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Guid CategoryId { get; set; }
        public AssetCategory? Category { get; set; }
        public DateTime AcquisitionDate { get; set; }
        public decimal AcquisitionCost { get; set; }
        public DepreciationMethod DepreciationMethod { get; set; }
        public int UsefulLifeMonths { get; set; }
        public decimal ResidualValue { get; set; }
        public decimal AccumulatedDepreciation { get; set; }
        public decimal CurrentNbv { get; set; }
        public AssetStatus Status { get; set; } = AssetStatus.Active;
        public DateTime? DisposalDate { get; set; }
        public decimal? DisposalProceeds { get; set; }
        public Guid? CostCentreId { get; set; }

        /// <summary>Public Sector (Module 16): barcode or RFID tag value for physical verification audits.</summary>
        public string? AssetTagValue { get; set; }
        /// <summary>IAS 16 revaluation model: cumulative revaluation surplus recognised in OCI/equity
        /// for this asset (as opposed to the cost model's accumulated depreciation).</summary>
        public decimal CumulativeRevaluationSurplus { get; set; }
    }

    /// <summary>An IAS 16 revaluation event: restates an asset to fair value, recognising the
    /// increase in Other Comprehensive Income (Revaluation Surplus), feeding both the OCI section
    /// of the Income Statement and the Statement of Changes in Equity.</summary>
    public class AssetRevaluation : TenantEntity
    {
        public Guid FixedAssetId { get; set; }
        public DateTime RevaluationDate { get; set; }
        public decimal NbvBeforeRevaluation { get; set; }
        public decimal FairValue { get; set; }
        public decimal SurplusAmount { get; set; } // FairValue - NbvBeforeRevaluation (can be negative = impairment through OCI reversal)
        public Guid? JournalEntryId { get; set; }
        public string? ValuerName { get; set; }
    }

    public class DepreciationEntry : TenantEntity
    {
        public Guid FixedAssetId { get; set; }
        public DateTime PeriodDate { get; set; }
        public decimal Amount { get; set; }
        public decimal NbvAfter { get; set; }
        public Guid? JournalEntryId { get; set; }
    }
}
