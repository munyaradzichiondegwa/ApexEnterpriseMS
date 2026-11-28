using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations
{
    [DbContext(typeof(ApexErpDbContext))]
    [Migration("20261128200000_AddIpsas24DisclosureReferences")]
    public partial class AddIpsas24DisclosureReferences : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NoteReference",
                table: "PublicSectorBudgetVersionLines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryClassificationCode",
                table: "PublicSectorBudgetVersionLines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommentReference",
                table: "PublicSectorBudgetVarianceExplanations",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TreasuryClassificationCode",
                table: "PublicSectorBudgetVersionLines");

            migrationBuilder.DropColumn(
                name: "NoteReference",
                table: "PublicSectorBudgetVersionLines");

            migrationBuilder.DropColumn(
                name: "CommentReference",
                table: "PublicSectorBudgetVarianceExplanations");
        }
    }
}