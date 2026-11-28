using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128320000_AddZimbabweTaxpayerTin")]
public sealed class AddZimbabweTaxpayerTin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ZimbabweTaxpayerTin",
            table: "Companies",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ZimbabweTaxpayerTin",
            table: "Companies");
    }
}
