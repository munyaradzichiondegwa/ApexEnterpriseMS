using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128030000_AddIpsas24BudgetComparison")]
public sealed class AddIpsas24BudgetComparison : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorBudgetVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                FinancialYear = table.Column<int>(type: "integer", nullable: false),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                AccountingBasis = table.Column<int>(type: "integer", nullable: false),
                ClassificationBasis = table.Column<string>(type: "text", nullable: false),
                EntityScope = table.Column<string>(type: "text", nullable: false),
                PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                AuthorityReference = table.Column<string>(type: "text", nullable: false),
                AmendmentReason = table.Column<string>(type: "text", nullable: true),
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
                table.PrimaryKey("PK_PublicSectorBudgetVersions", x => x.Id);
                table.CheckConstraint("CK_PublicSectorBudgetVersion_Period", "\"PeriodEnd\" > \"PeriodStart\"");
                table.CheckConstraint("CK_PublicSectorBudgetVersion_Sequence", "\"VersionNumber\" > 0");
            });

        migrationBuilder.CreateTable(
            name: "PublicSectorBudgetReconciliations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                FinancialYear = table.Column<int>(type: "integer", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false),
                Description = table.Column<string>(type: "text", nullable: false),
                StatementSection = table.Column<string>(type: "text", nullable: false),
                OperatingAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                InvestingAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                FinancingAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                SupportingReference = table.Column<string>(type: "text", nullable: false),
                ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorBudgetReconciliations", x => x.Id);
                table.CheckConstraint("CK_PublicSectorBudgetRecon_NonZero",
                    "\"OperatingAmount\" <> 0 OR \"InvestingAmount\" <> 0 OR \"FinancingAmount\" <> 0");
            });

        migrationBuilder.CreateTable(
            name: "PublicSectorBudgetVersionLines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                FundCentreId = table.Column<Guid>(type: "uuid", nullable: false),
                GlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CostCentreId = table.Column<Guid>(type: "uuid", nullable: true),
                LineNumber = table.Column<int>(type: "integer", nullable: false),
                Description = table.Column<string>(type: "text", nullable: false),
                ClassificationCode = table.Column<string>(type: "text", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorBudgetVersionLines", x => x.Id);
                table.CheckConstraint("CK_PublicSectorBudgetVersionLine_Amount", "\"Amount\" >= 0");
                table.ForeignKey("FK_PublicSectorBudgetVersionLines_PublicSectorBudgetVersions_BudgetVersionId",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PublicSectorBudgetVersionLines_FundCentres_FundCentreId",
                    x => x.FundCentreId, "FundCentres", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_PublicSectorBudgetVersionLines_GlAccounts_GlAccountId",
                    x => x.GlAccountId, "GlAccounts", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_PublicSectorBudgetVersionLines_CostCentres_CostCentreId",
                    x => x.CostCentreId, "CostCentres", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("UX_PublicSectorBudgetVersion_Sequence", "PublicSectorBudgetVersions",
            new[] { "CompanyId", "FinancialYear", "Kind", "VersionNumber" }, unique: true);
        migrationBuilder.CreateIndex("UX_PublicSectorBudgetVersionLine_Number", "PublicSectorBudgetVersionLines",
            new[] { "CompanyId", "BudgetVersionId", "LineNumber" }, unique: true);
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetVersionLines_BudgetVersionId", "PublicSectorBudgetVersionLines", "BudgetVersionId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetVersionLines_FundCentreId", "PublicSectorBudgetVersionLines", "FundCentreId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetVersionLines_GlAccountId", "PublicSectorBudgetVersionLines", "GlAccountId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetVersionLines_CostCentreId", "PublicSectorBudgetVersionLines", "CostCentreId");
        migrationBuilder.CreateIndex("UX_PublicSectorBudgetRecon_Description", "PublicSectorBudgetReconciliations",
            new[] { "CompanyId", "FinancialYear", "Type", "Description" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PublicSectorBudgetVersionLines");
        migrationBuilder.DropTable(name: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropTable(name: "PublicSectorBudgetVersions");
    }
}
