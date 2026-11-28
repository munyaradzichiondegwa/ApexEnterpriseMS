using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128040000_AddIpsas24BudgetExecutionActuals")]
public sealed class AddIpsas24BudgetExecutionActuals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorBudgetExecutionActuals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                FinancialYear = table.Column<int>(type: "integer", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionLineId = table.Column<Guid>(type: "uuid", nullable: false),
                FundCentreId = table.Column<Guid>(type: "uuid", nullable: false),
                GlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CostCentreId = table.Column<Guid>(type: "uuid", nullable: true),
                EventKind = table.Column<int>(type: "integer", nullable: false),
                SourceKind = table.Column<int>(type: "integer", nullable: false),
                SourceRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceReference = table.Column<string>(type: "text", nullable: false),
                SourceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                ExchangeRateToBudgetCurrency = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                CashFlowSection = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                ReversalOfId = table.Column<Guid>(type: "uuid", nullable: true),
                CertifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                CertifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorBudgetExecutionActuals", x => x.Id);
                table.CheckConstraint("CK_PublicSectorBudgetExecutionActual_Amount", "\"Amount\" > 0");
                table.CheckConstraint("CK_PublicSectorBudgetExecutionActual_ExchangeRate", "\"ExchangeRateToBudgetCurrency\" > 0");
                table.ForeignKey("FK_PublicSectorBudgetExecutionActuals_PublicSectorBudgetVersions_BudgetVersionId",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_PublicSectorBudgetExecutionActuals_PublicSectorBudgetVersionLines_BudgetVersionLineId",
                    x => x.BudgetVersionLineId, "PublicSectorBudgetVersionLines", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_PublicSectorBudgetExecutionActuals_FundCentres_FundCentreId",
                    x => x.FundCentreId, "FundCentres", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_PublicSectorBudgetExecutionActuals_GlAccounts_GlAccountId",
                    x => x.GlAccountId, "GlAccounts", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_PublicSectorBudgetExecutionActuals_BudgetVersionLineId",
            "PublicSectorBudgetExecutionActuals", "BudgetVersionLineId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetExecutionActuals_FundCentreId",
            "PublicSectorBudgetExecutionActuals", "FundCentreId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetExecutionActuals_GlAccountId",
            "PublicSectorBudgetExecutionActuals", "GlAccountId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetExecutionActuals_BudgetVersionId_EffectiveDate",
            "PublicSectorBudgetExecutionActuals", new[] { "BudgetVersionId", "EffectiveDate" });
        migrationBuilder.CreateIndex("UX_PublicSectorBudgetExecutionActual_Source",
            "PublicSectorBudgetExecutionActuals", new[] { "CompanyId", "SourceKind", "SourceRecordId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PublicSectorBudgetExecutionActuals");
    }
}
