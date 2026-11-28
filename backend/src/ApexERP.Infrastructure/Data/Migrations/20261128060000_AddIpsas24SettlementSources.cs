using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128060000_AddIpsas24SettlementSources")]
public sealed class AddIpsas24SettlementSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "FundCentreId",
            table: "ArInvoices",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ArInvoices_FundCentreId",
            table: "ArInvoices",
            column: "FundCentreId");

        migrationBuilder.AddForeignKey(
            name: "FK_ArInvoices_FundCentres_FundCentreId",
            table: "ArInvoices",
            column: "FundCentreId",
            principalTable: "FundCentres",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ArInvoice_FundCentre",
            table: "ArInvoices");
        migrationBuilder.DropIndex(
            name: "IX_ArInvoices_FundCentreId",
            table: "ArInvoices");
        migrationBuilder.DropColumn(
            name: "FundCentreId",
            table: "ArInvoices");
    }
}
