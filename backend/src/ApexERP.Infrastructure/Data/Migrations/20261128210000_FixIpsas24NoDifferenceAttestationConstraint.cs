using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations
{
    [DbContext(typeof(ApexErpDbContext))]
    [Migration("20261128210000_FixIpsas24NoDifferenceAttestationConstraint")]
    public partial class FixIpsas24NoDifferenceAttestationConstraint : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PublicSectorBudgetRecon_NonZero",
                table: "PublicSectorBudgetReconciliations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PublicSectorBudgetRecon_NonZero",
                table: "PublicSectorBudgetReconciliations",
                sql: "(\"IsNoDifferenceAttestation\" = TRUE AND \"RevenueAmount\" = 0 AND \"ExpenseAmount\" = 0 AND \"OperatingAmount\" = 0 AND \"InvestingAmount\" = 0 AND \"FinancingAmount\" = 0) OR (\"IsNoDifferenceAttestation\" = FALSE AND (\"RevenueAmount\" <> 0 OR \"ExpenseAmount\" <> 0 OR \"OperatingAmount\" <> 0 OR \"InvestingAmount\" <> 0 OR \"FinancingAmount\" <> 0))");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PublicSectorBudgetRecon_NonZero",
                table: "PublicSectorBudgetReconciliations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PublicSectorBudgetRecon_NonZero",
                table: "PublicSectorBudgetReconciliations",
                sql: "\"RevenueAmount\" <> 0 OR \"ExpenseAmount\" <> 0 OR \"OperatingAmount\" <> 0 OR \"InvestingAmount\" <> 0 OR \"FinancingAmount\" <> 0");
        }
    }
}