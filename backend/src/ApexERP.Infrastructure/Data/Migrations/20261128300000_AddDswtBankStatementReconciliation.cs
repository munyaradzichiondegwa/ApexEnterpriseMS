using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128300000_AddDswtBankStatementReconciliation")]
public sealed class AddDswtBankStatementReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "BankStatementTransactionId",
            table: "ZimbabweDswtWithholdingRecords",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BankReconciledAt",
            table: "ZimbabweDswtWithholdingRecords",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtWithholdingRecords_CompanyId_BankStatementTransactionId",
            table: "ZimbabweDswtWithholdingRecords",
            columns: new[] { "CompanyId", "BankStatementTransactionId" },
            unique: true,
            filter: "\"BankStatementTransactionId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ZimbabweDswtWithholdingRecords_CompanyId_BankStatementTransactionId",
            table: "ZimbabweDswtWithholdingRecords");

        migrationBuilder.DropColumn(
            name: "BankStatementTransactionId",
            table: "ZimbabweDswtWithholdingRecords");

        migrationBuilder.DropColumn(
            name: "BankReconciledAt",
            table: "ZimbabweDswtWithholdingRecords");
    }
}
