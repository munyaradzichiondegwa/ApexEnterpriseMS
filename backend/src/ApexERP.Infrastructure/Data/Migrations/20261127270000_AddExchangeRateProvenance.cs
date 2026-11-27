using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127270000_AddExchangeRateProvenance")]
public sealed class AddExchangeRateProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RateSource",
            table: "ExchangeRates",
            type: "text",
            nullable: false,
            defaultValue: "Unspecified");

        migrationBuilder.AddColumn<string>(
            name: "SourceReference",
            table: "ExchangeRates",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RateSource", table: "ExchangeRates");
        migrationBuilder.DropColumn(name: "SourceReference", table: "ExchangeRates");
    }
}
