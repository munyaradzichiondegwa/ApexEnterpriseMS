using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128150000_AllowIpsas24SourceSubmissionRevisions")]
public sealed class AllowIpsas24SourceSubmissionRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "SubmissionRevision", table: "PublicSectorCashSourceSubmissions",
            type: "integer", nullable: false, defaultValue: 1);

        migrationBuilder.DropIndex(
            name: "UX_PublicSectorCashSourceSubmission_Source",
            table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorCashSourceSubmission_Revision",
            table: "PublicSectorCashSourceSubmissions",
            columns: new[] { "CompanyId", "ExpectedCashSourceId", "SubmissionRevision" },
            unique: true);

        migrationBuilder.DropIndex(
            name: "UX_CashSourceReconciliationLine_Actual",
            table: "PublicSectorCashSourceReconciliationLines");
        migrationBuilder.CreateIndex(
            name: "UX_CashSourceReconciliationLine_Actual",
            table: "PublicSectorCashSourceReconciliationLines",
            columns: new[] { "CompanyId", "CashSourceSubmissionId", "BudgetExecutionActualId" },
            unique: true,
            filter: "\"BudgetExecutionActualId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_CashSourceReconciliationLine_Actual",
            table: "PublicSectorCashSourceReconciliationLines");
        migrationBuilder.CreateIndex(
            name: "UX_CashSourceReconciliationLine_Actual",
            table: "PublicSectorCashSourceReconciliationLines",
            column: "BudgetExecutionActualId",
            unique: true,
            filter: "\"BudgetExecutionActualId\" IS NOT NULL");

        migrationBuilder.DropIndex(
            name: "UX_PublicSectorCashSourceSubmission_Revision",
            table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorCashSourceSubmission_Source",
            table: "PublicSectorCashSourceSubmissions",
            columns: new[] { "CompanyId", "ExpectedCashSourceId" },
            unique: true);
        migrationBuilder.DropColumn(
            name: "SubmissionRevision", table: "PublicSectorCashSourceSubmissions");
    }
}
