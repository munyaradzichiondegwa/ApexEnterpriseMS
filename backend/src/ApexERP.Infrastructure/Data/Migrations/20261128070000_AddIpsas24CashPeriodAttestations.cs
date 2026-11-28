using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128070000_AddIpsas24CashPeriodAttestations")]
public sealed class AddIpsas24CashPeriodAttestations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorCashPeriodAttestations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ObservedBankTransactionCount = table.Column<int>(type: "integer", nullable: false),
                ObservedArReceiptCount = table.Column<int>(type: "integer", nullable: false),
                ObservedApPaymentCount = table.Column<int>(type: "integer", nullable: false),
                EvidenceReference = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CertifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                CertifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RejectionReason = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorCashPeriodAttestations", x => x.Id);
                table.CheckConstraint("CK_PublicSectorCashPeriodAttestation_Period", "\"PeriodEnd\" > \"PeriodStart\"");
                table.CheckConstraint("CK_PublicSectorCashPeriodAttestation_Counts",
                    "\"ObservedBankTransactionCount\" >= 0 AND \"ObservedArReceiptCount\" >= 0 AND \"ObservedApPaymentCount\" >= 0");
                table.ForeignKey("FK_CashPeriodAttestation_BudgetVersion",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_PublicSectorCashPeriodAttestations_BudgetVersionId",
            "PublicSectorCashPeriodAttestations", "BudgetVersionId");
        migrationBuilder.CreateIndex("UX_PublicSectorCashPeriodAttestation_Period",
            "PublicSectorCashPeriodAttestations",
            new[] { "CompanyId", "BudgetVersionId", "PeriodStart", "PeriodEnd" }, unique: true);

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_cash_period_attestation() RETURNS trigger AS $ipsas_attest$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Cash period attestation evidence cannot be deleted.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."CertifiedBy" IS NOT NULL OR NEW."CertifiedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Cash period attestations must begin as uncertified drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Certified', 'Rejected') THEN
        RAISE EXCEPTION 'Final cash period attestations are immutable.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF (to_jsonb(NEW) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'CertifiedBy' - 'CertifiedAt' - 'RejectionReason' - 'UpdatedAt' - 'UpdatedBy')
       IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'CertifiedBy' - 'CertifiedAt' - 'RejectionReason' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'Cash period attestation scope and evidence are immutable.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'PendingCertification') OR
            (OLD."Status" = 'PendingCertification' AND NEW."Status" IN ('Certified', 'Rejected'))) THEN
        RAISE EXCEPTION 'Invalid cash period attestation transition from % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_attest$ LANGUAGE plpgsql;
CREATE TRIGGER trg_cash_period_attestation_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON "PublicSectorCashPeriodAttestations"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_cash_period_attestation();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_cash_period_attestation_immutable ON "PublicSectorCashPeriodAttestations";
DROP FUNCTION IF EXISTS apex_guard_cash_period_attestation();
""");
        migrationBuilder.DropTable(name: "PublicSectorCashPeriodAttestations");
    }
}
