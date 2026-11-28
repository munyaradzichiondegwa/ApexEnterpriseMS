using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128020000_AddPublicSectorBudgetAllocations")]
public sealed class AddPublicSectorBudgetAllocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorBudgetAllocations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                FundCentreId = table.Column<Guid>(type: "uuid", nullable: false),
                GlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CostCentreId = table.Column<Guid>(type: "uuid", nullable: true),
                FinancialYear = table.Column<int>(type: "integer", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                ApprovalWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorBudgetAllocations", x => x.Id);
                table.CheckConstraint("CK_PublicSectorBudgetAllocations_Amount", "\"Amount\" > 0");
                table.ForeignKey(
                    name: "FK_PublicSectorBudgetAllocations_CostCentres_CostCentreId",
                    column: x => x.CostCentreId,
                    principalTable: "CostCentres",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PublicSectorBudgetAllocations_FundCentres_FundCentreId",
                    column: x => x.FundCentreId,
                    principalTable: "FundCentres",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PublicSectorBudgetAllocations_GlAccounts_GlAccountId",
                    column: x => x.GlAccountId,
                    principalTable: "GlAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PublicSectorBudgetAllocations_CompanyId_FundCentreId_GlAccountId_CostCentreId_FinancialYear",
            table: "PublicSectorBudgetAllocations",
            columns: new[] { "CompanyId", "FundCentreId", "GlAccountId", "CostCentreId", "FinancialYear" },
            unique: true,
            filter: "\"CostCentreId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PublicSectorBudgetAllocations_CompanyId_FundCentreId_GlAccountId_FinancialYear",
            table: "PublicSectorBudgetAllocations",
            columns: new[] { "CompanyId", "FundCentreId", "GlAccountId", "FinancialYear" },
            unique: true,
            filter: "\"CostCentreId\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PublicSectorBudgetAllocations_CostCentreId",
            table: "PublicSectorBudgetAllocations",
            column: "CostCentreId");

        migrationBuilder.CreateIndex(
            name: "IX_PublicSectorBudgetAllocations_FundCentreId",
            table: "PublicSectorBudgetAllocations",
            column: "FundCentreId");

        migrationBuilder.CreateIndex(
            name: "IX_PublicSectorBudgetAllocations_GlAccountId",
            table: "PublicSectorBudgetAllocations",
            column: "GlAccountId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PublicSectorBudgetAllocations");
    }
}
