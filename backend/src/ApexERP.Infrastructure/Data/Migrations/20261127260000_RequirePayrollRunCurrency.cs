using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127260000_RequirePayrollRunCurrency")]
public sealed class RequirePayrollRunCurrency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Currency",
            table: "PayrollRuns",
            type: "text",
            nullable: false,
            defaultValue: "USD",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true,
            oldDefaultValue: "USD");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Currency",
            table: "PayrollRuns",
            type: "text",
            nullable: true,
            defaultValue: "USD",
            oldClrType: typeof(string),
            oldType: "text",
            oldDefaultValue: "USD");
    }
}
