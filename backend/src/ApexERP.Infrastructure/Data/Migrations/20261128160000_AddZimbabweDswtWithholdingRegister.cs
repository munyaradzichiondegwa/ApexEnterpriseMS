using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128160000_AddZimbabweDswtWithholdingRegister")]
public sealed class AddZimbabweDswtWithholdingRegister : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ZimbabweDswtWithholdingRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                PaymentReference = table.Column<string>(type: "text", nullable: false),
                IntermediaryName = table.Column<string>(type: "text", nullable: false),
                SupplierName = table.Column<string>(type: "text", nullable: false),
                SupplierCountryCode = table.Column<string>(type: "text", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                GrossPaymentAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                WithholdingRate = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                WithheldAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                SupplierRemittanceAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                ServiceKind = table.Column<int>(type: "integer", nullable: false),
                SupplierVatStatus = table.Column<int>(type: "integer", nullable: false),
                PaymentRoute = table.Column<int>(type: "integer", nullable: false),
                ConsumedInZimbabwe = table.Column<bool>(type: "boolean", nullable: false),
                ZeroRatedOrExempt = table.Column<bool>(type: "boolean", nullable: false),
                RecipientAlreadySelfAccountedVat = table.Column<bool>(type: "boolean", nullable: false),
                WithheldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                TaxPeriodYear = table.Column<int>(type: "integer", nullable: false),
                TaxPeriodMonth = table.Column<int>(type: "integer", nullable: false),
                CertificateNumber = table.Column<string>(type: "text", nullable: true),
                CertificateIssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RemittedToZimraAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                EvidenceReference = table.Column<string>(type: "text", nullable: true),
                ApPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ZimbabweDswtWithholdingRecords", x => x.Id);
                table.CheckConstraint("CK_ZimbabweDswtWithholdingRecords_Amounts",
                    "\"GrossPaymentAmount\" > 0 AND \"WithholdingRate\" > 0 AND \"WithheldAmount\" > 0 AND \"SupplierRemittanceAmount\" >= 0 AND \"GrossPaymentAmount\" = \"WithheldAmount\" + \"SupplierRemittanceAmount\"");
                table.CheckConstraint("CK_ZimbabweDswtWithholdingRecords_TaxPeriod",
                    "\"TaxPeriodYear\" BETWEEN 2026 AND 9999 AND \"TaxPeriodMonth\" BETWEEN 1 AND 12");
            });

        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtWithholdingRecords_CompanyId_PaymentReference",
            table: "ZimbabweDswtWithholdingRecords",
            columns: new[] { "CompanyId", "PaymentReference" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ZimbabweDswtWithholdingRecords_CompanyId_TaxPeriodYear_TaxPeriodMonth_Status",
            table: "ZimbabweDswtWithholdingRecords",
            columns: new[] { "CompanyId", "TaxPeriodYear", "TaxPeriodMonth", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ZimbabweDswtWithholdingRecords");
    }
}
