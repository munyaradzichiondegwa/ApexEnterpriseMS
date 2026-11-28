using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128240000_AddIpsas24TaxAllocationControls")]
public sealed class AddIpsas24TaxAllocationControls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "TaxCodeId", table: "JournalLines", type: "uuid", nullable: true);

        migrationBuilder.CreateTable(
            name: "TaxAccountMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxType = table.Column<int>(type: "integer", nullable: false),
                Purpose = table.Column<string>(type: "text", nullable: false),
                TaxCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                GlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                ApprovalWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxAccountMappings", x => x.Id);
                table.CheckConstraint("CK_TaxAccountMappings_Version", "\"VersionNumber\" > 0");
                table.CheckConstraint("CK_TaxAccountMappings_Period", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\"");
                            table.CheckConstraint("CK_TaxAccountMappings_Purpose", "((\"Purpose\" IN ('AR-VAT-OUTPUT','AP-VAT-INPUT') AND \"TaxType\" = 0 AND \"TaxCodeId\" IS NOT NULL) OR (\"Purpose\" = 'AP-WHT-PAYABLE' AND \"TaxType\" = 1 AND \"TaxCodeId\" IS NULL))");
            });

        migrationBuilder.CreateTable(
            name: "InvoiceTaxComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceKind = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceLineId = table.Column<Guid>(type: "uuid", nullable: true),
                TaxCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                TaxType = table.Column<int>(type: "integer", nullable: false),
                TaxableBase = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Rate = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                GlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxAccountMappingId = table.Column<Guid>(type: "uuid", nullable: false),
                MappingVersionNumber = table.Column<int>(type: "integer", nullable: false),
                JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                JournalLineId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InvoiceTaxComponents", x => x.Id);
                table.CheckConstraint("CK_InvoiceTaxComponents_Amounts", "\"TaxableBase\" >= 0 AND \"Rate\" >= 0 AND \"Amount\" > 0");
                table.CheckConstraint("CK_InvoiceTaxComponents_Kind", "\"InvoiceKind\" IN ('AR','AP')");
                            table.CheckConstraint("CK_InvoiceTaxComponents_TaxCode", "((\"TaxType\" = 0 AND \"TaxCodeId\" IS NOT NULL) OR (\"TaxType\" = 1 AND \"TaxCodeId\" IS NULL))");
            });

        migrationBuilder.CreateTable(
            name: "PublicSectorTaxBudgetPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxType = table.Column<int>(type: "integer", nullable: false),
                TaxCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                Treatment = table.Column<int>(type: "integer", nullable: false),
                BudgetGlAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                FundCentreId = table.Column<Guid>(type: "uuid", nullable: true),
                CostCentreId = table.Column<Guid>(type: "uuid", nullable: true),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                ApprovalWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorTaxBudgetPolicies", x => x.Id);
                table.CheckConstraint("CK_PublicSectorTaxBudgetPolicies_Version", "\"VersionNumber\" > 0");
                table.CheckConstraint("CK_PublicSectorTaxBudgetPolicies_Period", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\"");
                table.CheckConstraint("CK_PublicSectorTaxBudgetPolicies_Separate", "\"Treatment\" <> 2 OR \"BudgetGlAccountId\" IS NOT NULL");
                            table.CheckConstraint("CK_PublicSectorTaxBudgetPolicies_TaxCode", "((\"TaxType\" = 0 AND \"TaxCodeId\" IS NOT NULL) OR (\"TaxType\" = 1 AND \"TaxCodeId\" IS NULL))");
            });

        migrationBuilder.CreateTable(
            name: "SettlementComponentAllocations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                SettlementKind = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                SettlementAllocationId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                ComponentKind = table.Column<int>(type: "integer", nullable: false),
                InvoiceLineId = table.Column<Guid>(type: "uuid", nullable: true),
                TaxComponentId = table.Column<Guid>(type: "uuid", nullable: true),
                AmountApplied = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SettlementComponentAllocations", x => x.Id);
                table.CheckConstraint("CK_SettlementComponentAllocations_Component",
                    "((\"ComponentKind\" = 0 AND \"InvoiceLineId\" IS NOT NULL AND \"TaxComponentId\" IS NULL) OR (\"ComponentKind\" = 1 AND \"InvoiceLineId\" IS NULL AND \"TaxComponentId\" IS NOT NULL))");
                table.CheckConstraint("CK_SettlementComponentAllocations_Amount", "\"AmountApplied\" > 0");
                table.CheckConstraint("CK_SettlementComponentAllocations_Kind", "\"SettlementKind\" IN ('AR','AP')");
                            table.CheckConstraint("CK_SettlementComponentAllocations_TaxRemittance", "\"SettlementKind\" <> 'TX' OR \"ComponentKind\" = 1");
            });

        migrationBuilder.CreateIndex(name: "IX_TaxAccountMappings_CompanyId_Purpose_TaxCodeId_VersionNumber",
            table: "TaxAccountMappings", columns: new[] { "CompanyId", "Purpose", "TaxCodeId", "VersionNumber" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_TaxAccountMappings_Effective", table: "TaxAccountMappings",
            columns: new[] { "CompanyId", "Purpose", "TaxCodeId", "EffectiveFrom", "EffectiveTo" });
        migrationBuilder.CreateIndex(name: "IX_InvoiceTaxComponents_InvoiceJournalLine",
            table: "InvoiceTaxComponents", columns: new[] { "CompanyId", "InvoiceKind", "InvoiceId", "JournalLineId", "InvoiceLineId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_InvoiceTaxComponents_Mapping",
            table: "InvoiceTaxComponents", columns: new[] { "CompanyId", "TaxAccountMappingId", "InvoiceId" });
        migrationBuilder.CreateIndex(name: "IX_PublicSectorTaxBudgetPolicies_CompanyId_TaxType_TaxCodeId_VersionNumber",
            table: "PublicSectorTaxBudgetPolicies", columns: new[] { "CompanyId", "TaxType", "TaxCodeId", "VersionNumber" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_PublicSectorTaxBudgetPolicies_Effective",
            table: "PublicSectorTaxBudgetPolicies", columns: new[] { "CompanyId", "TaxType", "TaxCodeId", "EffectiveFrom", "EffectiveTo" });
        migrationBuilder.CreateIndex(name: "IX_SettlementComponentAllocations_Allocation",
            table: "SettlementComponentAllocations", columns: new[] { "CompanyId", "SettlementKind", "SettlementAllocationId", "InvoiceLineId", "TaxComponentId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_SettlementComponentAllocations_TaxComponentId",
            table: "SettlementComponentAllocations", column: "TaxComponentId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SettlementComponentAllocations");
        migrationBuilder.DropTable(name: "PublicSectorTaxBudgetPolicies");
        migrationBuilder.DropTable(name: "InvoiceTaxComponents");
        migrationBuilder.DropTable(name: "TaxAccountMappings");
        migrationBuilder.DropColumn(name: "TaxCodeId", table: "JournalLines");
    }
}
