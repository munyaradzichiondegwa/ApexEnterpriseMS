using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128160000_AddIpsas24RevenueExpenseReconciliation")]
public sealed class AddIpsas24RevenueExpenseReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "RevenueAmount",
            table: "PublicSectorBudgetReconciliations",
            type: "numeric(20,6)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<decimal>(
            name: "ExpenseAmount",
            table: "PublicSectorBudgetReconciliations",
            type: "numeric(20,6)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.DropCheckConstraint(
            name: "CK_PublicSectorBudgetRecon_NonZero",
            table: "PublicSectorBudgetReconciliations");

        migrationBuilder.AddCheckConstraint(
            name: "CK_PublicSectorBudgetRecon_NonZero",
            table: "PublicSectorBudgetReconciliations",
            sql: "\"RevenueAmount\" <> 0 OR \"ExpenseAmount\" <> 0 OR \"OperatingAmount\" <> 0 OR \"InvestingAmount\" <> 0 OR \"FinancingAmount\" <> 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_PublicSectorBudgetRecon_NonZero",
            table: "PublicSectorBudgetReconciliations");

        migrationBuilder.DropColumn(
            name: "RevenueAmount",
            table: "PublicSectorBudgetReconciliations");

        migrationBuilder.DropColumn(
            name: "ExpenseAmount",
            table: "PublicSectorBudgetReconciliations");

        migrationBuilder.AddCheckConstraint(
            name: "CK_PublicSectorBudgetRecon_NonZero",
            table: "PublicSectorBudgetReconciliations",
            sql: "\"OperatingAmount\" <> 0 OR \"InvestingAmount\" <> 0 OR \"FinancingAmount\" <> 0");
    }
}
