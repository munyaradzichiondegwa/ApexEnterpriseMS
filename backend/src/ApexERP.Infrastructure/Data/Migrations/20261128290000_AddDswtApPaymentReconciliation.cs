using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128290000_AddDswtApPaymentReconciliation")]
public sealed class AddDswtApPaymentReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtWithholdingRecords_CompanyId_ApPaymentId",
            table: "ZimbabweDswtWithholdingRecords",
            columns: new[] { "CompanyId", "ApPaymentId" },
            unique: true,
            filter: "\"ApPaymentId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ZimbabweDswtWithholdingRecords_CompanyId_ApPaymentId",
            table: "ZimbabweDswtWithholdingRecords");
    }
}
