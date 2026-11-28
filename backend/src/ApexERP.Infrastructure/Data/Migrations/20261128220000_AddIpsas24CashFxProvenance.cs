using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128220000_AddIpsas24CashFxProvenance")]
public sealed class AddIpsas24CashFxProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "SourceAmount",
            table: "PublicSectorBudgetExecutionActuals",
            type: "numeric(20,6)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<DateTime>(
            name: "ExchangeRateDate",
            table: "PublicSectorBudgetExecutionActuals",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExchangeRateSource",
            table: "PublicSectorBudgetExecutionActuals",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExchangeRateSourceReference",
            table: "PublicSectorBudgetExecutionActuals",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ExchangeRateIsInverse",
            table: "PublicSectorBudgetExecutionActuals",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE "PublicSectorBudgetExecutionActuals"
            SET "SourceAmount" = "Amount",
                "ExchangeRateDate" = "SourceDate",
                "ExchangeRateSource" = 'IDENTITY',
                "ExchangeRateSourceReference" = 'Historical same-currency capture'
            """);

        migrationBuilder.AlterColumn<decimal>(
            name: "SourceAmount",
            table: "PublicSectorBudgetExecutionActuals",
            type: "numeric(20,6)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(20,6)",
            oldDefaultValue: 0m);

        migrationBuilder.AlterColumn<bool>(
            name: "ExchangeRateIsInverse",
            table: "PublicSectorBudgetExecutionActuals",
            type: "boolean",
            nullable: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: false);

        migrationBuilder.AddCheckConstraint(
            name: "CK_PublicSectorBudgetExecutionActual_SourceAmount",
            table: "PublicSectorBudgetExecutionActuals",
            sql: "\"SourceAmount\" > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SourceAmount", table: "PublicSectorBudgetExecutionActuals");
        migrationBuilder.DropColumn(name: "ExchangeRateDate", table: "PublicSectorBudgetExecutionActuals");
        migrationBuilder.DropColumn(name: "ExchangeRateSource", table: "PublicSectorBudgetExecutionActuals");
        migrationBuilder.DropColumn(name: "ExchangeRateSourceReference", table: "PublicSectorBudgetExecutionActuals");
        migrationBuilder.DropColumn(name: "ExchangeRateIsInverse", table: "PublicSectorBudgetExecutionActuals");
    }
}
