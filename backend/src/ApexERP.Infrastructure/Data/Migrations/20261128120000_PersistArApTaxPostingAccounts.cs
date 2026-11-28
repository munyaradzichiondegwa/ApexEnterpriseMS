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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "VatOutputAccountId", table: "ArInvoices");
        migrationBuilder.DropColumn(name: "VatInputAccountId", table: "ApInvoices");
        migrationBuilder.DropColumn(name: "WhtPayableAccountId", table: "ApInvoices");
    }
}
