using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127210000_AddWorkflowApprovalAmountAndFundCentre")]
public sealed class AddWorkflowApprovalAmountAndFundCentre : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "Amount",
            table: "WorkflowRequests",
            type: "numeric(20,6)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<Guid>(
            name: "FundCentreId",
            table: "WorkflowRequests",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRequests_FundCentreId",
            table: "WorkflowRequests",
            column: "FundCentreId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRequests_FundCentreId",
            table: "WorkflowRequests",
            column: "FundCentreId");

        migrationBuilder.AddForeignKey(
            name: "FK_WorkflowRequests_FundCentres_FundCentreId",
            table: "WorkflowRequests",
            column: "FundCentreId",
            principalTable: "FundCentres",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_WorkflowRequests_FundCentres_FundCentreId", table: "WorkflowRequests");
        migrationBuilder.DropIndex(name: "IX_WorkflowRequests_FundCentreId", table: "WorkflowRequests");
        migrationBuilder.DropColumn(name: "Amount", table: "WorkflowRequests");
        migrationBuilder.DropColumn(name: "FundCentreId", table: "WorkflowRequests");
    }
}
