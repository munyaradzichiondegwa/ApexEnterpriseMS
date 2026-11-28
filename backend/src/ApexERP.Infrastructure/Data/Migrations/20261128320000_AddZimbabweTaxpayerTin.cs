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


[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128330000_AddZimbabweDswtFilingWorkflow")]
public sealed class AddZimbabweDswtFilingWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ZimbabweDswtFilings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxYear = table.Column<int>(type: "integer", nullable: false),
                TaxPeriod = table.Column<int>(type: "integer", nullable: false),
                ReturnVersion = table.Column<int>(type: "integer", nullable: false),
                TaxpayerTin = table.Column<string>(type: "text", nullable: false),
                TaxpayerName = table.Column<string>(type: "text", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                TaxableIncome = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                TaxAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                PaymentCount = table.Column<int>(type: "integer", nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                PayloadSha256 = table.Column<string>(type: "text", nullable: false),
                IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                SubmissionChannel = table.Column<string>(type: "text", nullable: false),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmissionReference = table.Column<string>(type: "text", nullable: true),
                SubmissionEvidenceReference = table.Column<string>(type: "text", nullable: true),
                AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                AcknowledgementReference = table.Column<string>(type: "text", nullable: true),
                AcknowledgementEvidenceReference = table.Column<string>(type: "text", nullable: true),
                PaymentConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                PaymentReference = table.Column<string>(type: "text", nullable: true),
                PaymentAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                PaymentCurrency = table.Column<string>(type: "text", nullable: true),
                PaymentEvidenceReference = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ZimbabweDswtFilings", x => x.Id);
                table.CheckConstraint("CK_ZimbabweDswtFilings_Period",
                    "\"TaxYear\" >= 2026 AND \"TaxPeriod\" BETWEEN 1 AND 12 AND \"ReturnVersion\" >= 1");
                table.CheckConstraint("CK_ZimbabweDswtFilings_Amounts",
                    "\"TaxableIncome\" >= 0 AND \"TaxAmount\" >= 0 AND \"PaymentCount\" >= 0");
                table.CheckConstraint("CK_ZimbabweDswtFilings_Hash",
                    "char_length(\"PayloadSha256\") = 64 AND char_length(\"IdempotencyKey\") = 64");
            });

        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtFilings_CompanyId_TaxYear_TaxPeriod_ReturnVersion",
            table: "ZimbabweDswtFilings",
            columns: new[] { "CompanyId", "TaxYear", "TaxPeriod", "ReturnVersion" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtFilings_CompanyId_IdempotencyKey",
            table: "ZimbabweDswtFilings",
            columns: new[] { "CompanyId", "IdempotencyKey" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtFilings_CompanyId_SubmissionReference",
            table: "ZimbabweDswtFilings",
            columns: new[] { "CompanyId", "SubmissionReference" },
            unique: true,
            filter: "\"SubmissionReference\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ZimbabweDswtFilings");
    }
}
