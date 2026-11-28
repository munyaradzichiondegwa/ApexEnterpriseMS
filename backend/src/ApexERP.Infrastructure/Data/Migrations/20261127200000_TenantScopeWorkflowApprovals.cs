using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127200000_TenantScopeWorkflowApprovals")]
public sealed class TenantScopeWorkflowApprovals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CompanyId",
            table: "WorkflowApprovals",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "WorkflowApprovals" AS wa
            SET "CompanyId" = wr."CompanyId"
            FROM "WorkflowRequests" AS wr
            WHERE wr."Id" = wa."WorkflowRequestId";
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "CompanyId",
            table: "WorkflowApprovals",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowApprovals_CompanyId",
            table: "WorkflowApprovals",
            column: "CompanyId");

        migrationBuilder.AddForeignKey(
            name: "FK_WorkflowApprovals_Companies_CompanyId",
            table: "WorkflowApprovals",
            column: "CompanyId",
            principalTable: "Companies",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_WorkflowApprovals_Companies_CompanyId", table: "WorkflowApprovals");
        migrationBuilder.DropIndex(name: "IX_WorkflowApprovals_CompanyId", table: "WorkflowApprovals");
        migrationBuilder.DropColumn(name: "CompanyId", table: "WorkflowApprovals");
    }
}
