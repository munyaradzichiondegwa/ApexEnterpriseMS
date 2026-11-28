using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128230000_AddIpsas24InvoiceLineCostCentres")]
public sealed class AddIpsas24InvoiceLineCostCentres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CostCentreId",
            table: "ArInvoiceLines",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CostCentreId",
            table: "ApInvoiceLines",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ArInvoiceLines_CostCentreId",
            table: "ArInvoiceLines",
            column: "CostCentreId");

        migrationBuilder.CreateIndex(
            name: "IX_ApInvoiceLines_CostCentreId",
            table: "ApInvoiceLines",
            column: "CostCentreId");

        migrationBuilder.AddForeignKey(
            name: "FK_ArInvoiceLines_CostCentres_CostCentreId",
            table: "ArInvoiceLines",
            column: "CostCentreId",
            principalTable: "CostCentres",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_ApInvoiceLines_CostCentres_CostCentreId",
            table: "ApInvoiceLines",
            column: "CostCentreId",
            principalTable: "CostCentres",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ArInvoiceLines_CostCentres_CostCentreId",
            table: "ArInvoiceLines");

        migrationBuilder.DropForeignKey(
            name: "FK_ApInvoiceLines_CostCentres_CostCentreId",
            table: "ApInvoiceLines");

        migrationBuilder.DropIndex(name: "IX_ArInvoiceLines_CostCentreId", table: "ArInvoiceLines");
        migrationBuilder.DropIndex(name: "IX_ApInvoiceLines_CostCentreId", table: "ApInvoiceLines");

        migrationBuilder.DropColumn(name: "CostCentreId", table: "ArInvoiceLines");
        migrationBuilder.DropColumn(name: "CostCentreId", table: "ApInvoiceLines");
    }
}
