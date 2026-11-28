using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128080000_AllowIpsas24SourceSplitAllocations")]
public sealed class AllowIpsas24SourceSplitAllocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_PublicSectorBudgetExecutionActual_Source",
            table: "PublicSectorBudgetExecutionActuals");

        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetExecutionActual_SourceAllocation",
            table: "PublicSectorBudgetExecutionActuals",
            columns: new[] { "CompanyId", "SourceKind", "SourceRecordId", "BudgetVersionLineId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_PublicSectorBudgetExecutionActual_SourceAllocation",
            table: "PublicSectorBudgetExecutionActuals");

        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetExecutionActual_Source",
            table: "PublicSectorBudgetExecutionActuals",
            columns: new[] { "CompanyId", "SourceKind", "SourceRecordId" },
            unique: true);
    }
}
