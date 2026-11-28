using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128170000_AddIpsas24BudgetAmendmentNature")]
public sealed class AddIpsas24BudgetAmendmentNature : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AmendmentNature",
            table: "PublicSectorBudgetVersions",
            type: "integer",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_PublicSectorBudgetVersion_AmendmentNature",
            table: "PublicSectorBudgetVersions",
            sql: "\"AmendmentNature\" IS NULL OR \"AmendmentNature\" IN (0, 1, 2)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_PublicSectorBudgetVersion_AmendmentNature",
            table: "PublicSectorBudgetVersions");
        migrationBuilder.DropColumn(
            name: "AmendmentNature",
            table: "PublicSectorBudgetVersions");
    }
}
