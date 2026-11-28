using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128130000_AddIpsas24SourceRegisterAttestation")]
public sealed class AddIpsas24SourceRegisterAttestation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorExpectedCashSourceRegisterAttestations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                ExpectedSourceCount = table.Column<int>(type: "integer", nullable: false),
                EntityScope = table.Column<string>(type: "text", nullable: false),
                EvidenceReference = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CertifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                CertifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ReviewNote = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorExpectedCashSourceRegisterAttestations", x => x.Id);
                table.CheckConstraint("CK_ExpectedCashSourceRegisterAttestation_Count", "\"ExpectedSourceCount\" > 0");
                table.ForeignKey("FK_ExpectedCashSourceRegisterAttestation_BudgetVersion",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_ExpectedCashSourceRegisterAttestation_BudgetVersion",
            "PublicSectorExpectedCashSourceRegisterAttestations",
            new[] { "CompanyId", "BudgetVersionId" });
        migrationBuilder.CreateIndex("IX_ExpectedCashSourceRegisterAttestation_BudgetVersionId",
            "PublicSectorExpectedCashSourceRegisterAttestations", "BudgetVersionId");

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_expected_cash_source_register_attestation() RETURNS trigger AS $ipsas_register$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Expected-source register attestations cannot be deleted.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."SubmittedBy" IS NOT NULL OR NEW."SubmittedAt" IS NOT NULL
           OR NEW."CertifiedBy" IS NOT NULL OR NEW."CertifiedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Source-register attestations must begin as drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Certified', 'Rejected') THEN
        RAISE EXCEPTION 'Final source-register attestations are immutable.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF (to_jsonb(NEW) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'CertifiedBy' - 'CertifiedAt'
        - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy')
       IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'CertifiedBy' - 'CertifiedAt'
        - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'Source-register attestation scope and evidence are immutable after creation.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'PendingCertification') OR
            (OLD."Status" = 'PendingCertification' AND NEW."Status" IN ('Certified', 'Rejected'))) THEN
        RAISE EXCEPTION 'Invalid source-register attestation transition from % to %.',
            OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_register$ LANGUAGE plpgsql;
CREATE TRIGGER trg_expected_cash_source_register_attestation_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON "PublicSectorExpectedCashSourceRegisterAttestations"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_expected_cash_source_register_attestation();

CREATE OR REPLACE FUNCTION apex_block_cash_source_after_register_certification() RETURNS trigger AS $ipsas_freeze$
BEGIN
    IF EXISTS (
        SELECT 1 FROM "PublicSectorExpectedCashSourceRegisterAttestations" a
         WHERE a."CompanyId" = NEW."CompanyId"
           AND a."BudgetVersionId" = NEW."BudgetVersionId"
           AND a."Status" = 'Certified'
    ) THEN
        RAISE EXCEPTION 'The expected-source register is frozen by a certified completeness attestation. Reopen through a controlled new budget version.' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_freeze$ LANGUAGE plpgsql;
CREATE TRIGGER trg_expected_cash_source_register_frozen
    BEFORE INSERT ON "PublicSectorExpectedCashSources"
    FOR EACH ROW EXECUTE FUNCTION apex_block_cash_source_after_register_certification();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_expected_cash_source_register_frozen ON "PublicSectorExpectedCashSources";
DROP FUNCTION IF EXISTS apex_block_cash_source_after_register_certification();
DROP TRIGGER IF EXISTS trg_expected_cash_source_register_attestation_immutable ON "PublicSectorExpectedCashSourceRegisterAttestations";
DROP FUNCTION IF EXISTS apex_guard_expected_cash_source_register_attestation();
""");
        migrationBuilder.DropTable(name: "PublicSectorExpectedCashSourceRegisterAttestations");
    }
}
