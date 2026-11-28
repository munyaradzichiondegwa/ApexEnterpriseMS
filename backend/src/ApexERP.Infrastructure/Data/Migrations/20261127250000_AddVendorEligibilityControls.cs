using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127250000_AddVendorEligibilityControls")]
public sealed class AddVendorEligibilityControls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsBlacklisted", table: "Vendors", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(
            name: "BlacklistedReason", table: "Vendors", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "TaxClearanceValidTo", table: "Vendors", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "RegistrationValidTo", table: "Vendors", type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IsBlacklisted", table: "Vendors");
        migrationBuilder.DropColumn(name: "BlacklistedReason", table: "Vendors");
        migrationBuilder.DropColumn(name: "TaxClearanceValidTo", table: "Vendors");
        migrationBuilder.DropColumn(name: "RegistrationValidTo", table: "Vendors");
    }
}
