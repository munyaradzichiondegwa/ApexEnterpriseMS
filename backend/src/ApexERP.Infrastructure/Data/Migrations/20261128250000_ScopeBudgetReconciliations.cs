using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128250000_ScopeBudgetReconciliations")]
public sealed class ScopeBudgetReconciliations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "BudgetVersionId", table: "PublicSectorBudgetReconciliations",
            type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "StatementPeriodStart", table: "PublicSectorBudgetReconciliations",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "StatementPeriodEnd", table: "PublicSectorBudgetReconciliations",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "StatementEntityScope", table: "PublicSectorBudgetReconciliations",
            type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "SourceFingerprint", table: "PublicSectorBudgetReconciliations",
            type: "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "CalculatedAt", table: "PublicSectorBudgetReconciliations",
            type: "timestamp with time zone", nullable: true);

        migrationBuilder.DropIndex(
            name: "UX_PublicSectorBudgetRecon_Description",
            table: "PublicSectorBudgetReconciliations");
        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetRecon_ScopeDescription",
            table: "PublicSectorBudgetReconciliations",
            columns: new[] { "CompanyId", "BudgetVersionId", "StatementPeriodStart", "StatementPeriodEnd", "Type", "Description" },
            unique: true);
        migrationBuilder.AddCheckConstraint(
            name: "CK_PublicSectorBudgetRecon_StatementPeriod",
            table: "PublicSectorBudgetReconciliations",
            sql: "\"StatementPeriodStart\" IS NULL OR \"StatementPeriodEnd\" IS NULL OR \"StatementPeriodEnd\" > \"StatementPeriodStart\"");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_PublicSectorBudgetRecon_StatementPeriod",
            table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropIndex(
            name: "UX_PublicSectorBudgetRecon_ScopeDescription",
            table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropColumn(name: "BudgetVersionId", table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropColumn(name: "StatementPeriodStart", table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropColumn(name: "StatementPeriodEnd", table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropColumn(name: "StatementEntityScope", table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropColumn(name: "SourceFingerprint", table: "PublicSectorBudgetReconciliations");
        migrationBuilder.DropColumn(name: "CalculatedAt", table: "PublicSectorBudgetReconciliations");
        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetRecon_Description",
            table: "PublicSectorBudgetReconciliations",
            columns: new[] { "CompanyId", "FinancialYear", "Type", "Description" },
            unique: true);
    }
}
