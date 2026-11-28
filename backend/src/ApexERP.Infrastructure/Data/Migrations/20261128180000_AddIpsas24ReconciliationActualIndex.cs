using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128180000_AddIpsas24ReconciliationActualIndex")]
public sealed class AddIpsas24ReconciliationActualIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_PublicSectorCashSourceReconciliationLines_BudgetExecutionActualId",
            table: "PublicSectorCashSourceReconciliationLines",
            column: "BudgetExecutionActualId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PublicSectorCashSourceReconciliationLines_BudgetExecutionActualId",
            table: "PublicSectorCashSourceReconciliationLines");
    }
}
