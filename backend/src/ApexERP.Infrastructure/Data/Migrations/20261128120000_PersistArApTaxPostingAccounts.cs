using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128120000_PersistArApTaxPostingAccounts")]
public sealed class PersistArApTaxPostingAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "VatOutputAccountId",
            table: "ArInvoices",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "VatInputAccountId",
            table: "ApInvoices",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "WhtPayableAccountId",
            table: "ApInvoices",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ArInvoices_VatOutputAccountId",
            table: "ArInvoices",
            column: "VatOutputAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_ApInvoices_VatInputAccountId",
            table: "ApInvoices",
            column: "VatInputAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_ApInvoices_WhtPayableAccountId",
            table: "ApInvoices",
            column: "WhtPayableAccountId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_ArInvoices_VatOutputAccountId", table: "ArInvoices");
        migrationBuilder.DropIndex(name: "IX_ApInvoices_VatInputAccountId", table: "ApInvoices");
        migrationBuilder.DropIndex(name: "IX_ApInvoices_WhtPayableAccountId", table: "ApInvoices");

        migrationBuilder.DropColumn(name: "VatOutputAccountId", table: "ArInvoices");
        migrationBuilder.DropColumn(name: "VatInputAccountId", table: "ApInvoices");
        migrationBuilder.DropColumn(name: "WhtPayableAccountId", table: "ApInvoices");
    }
}
