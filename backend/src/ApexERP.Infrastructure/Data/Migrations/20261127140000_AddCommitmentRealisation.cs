using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127140000_AddCommitmentRealisation")]
public sealed class AddCommitmentRealisation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "RealisedAmount",
            table: "Commitments",
            type: "numeric(20,6)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Commitments_RealisedAmount",
            table: "Commitments",
            sql: "\"RealisedAmount\" >= 0 AND \"RealisedAmount\" <= \"Amount\"");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Commitments_RealisedAmount",
            table: "Commitments");

        migrationBuilder.DropColumn(
            name: "RealisedAmount",
            table: "Commitments");
    }
}
