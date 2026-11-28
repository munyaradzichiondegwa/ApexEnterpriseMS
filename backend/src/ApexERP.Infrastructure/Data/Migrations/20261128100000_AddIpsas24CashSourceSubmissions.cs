using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128100000_AddIpsas24CashSourceSubmissions")]
public sealed class AddIpsas24CashSourceSubmissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorCashSourceSubmissions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                ExpectedCashSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                RecordCount = table.Column<int>(type: "integer", nullable: false),
                ControlTotal = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                OpeningBalance = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                ClosingBalance = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                EvidenceReference = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ReviewedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ReviewNote = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorCashSourceSubmissions", x => x.Id);
                table.CheckConstraint("CK_PublicSectorCashSourceSubmission_Period", "\"PeriodEnd\" > \"PeriodStart\"");
                table.CheckConstraint("CK_PublicSectorCashSourceSubmission_RecordCount", "\"RecordCount\" >= 0");
                table.CheckConstraint("CK_PublicSectorCashSourceSubmission_Currency", "char_length(\"Currency\") = 3");
                table.ForeignKey("FK_CashSourceSubmission_ExpectedSource",
                    x => x.ExpectedCashSourceId, "PublicSectorExpectedCashSources", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_CashSourceSubmission_BudgetVersion",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_PublicSectorCashSourceSubmissions_ExpectedCashSourceId",
            "PublicSectorCashSourceSubmissions", "ExpectedCashSourceId");
        migrationBuilder.CreateIndex("UX_PublicSectorCashSourceSubmission_Source",
            "PublicSectorCashSourceSubmissions", new[] { "CompanyId", "ExpectedCashSourceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_PublicSectorCashSourceSubmissions_BudgetVersionId",
            "PublicSectorCashSourceSubmissions", "BudgetVersionId");

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_cash_source_submission() RETURNS trigger AS $ipsas_submission$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Cash source submissions cannot be deleted.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."SubmittedBy" IS NOT NULL OR NEW."SubmittedAt" IS NOT NULL
           OR NEW."ReviewedBy" IS NOT NULL OR NEW."ReviewedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Cash source submissions must begin as unsubmitted drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Reviewed', 'Rejected') THEN
        RAISE EXCEPTION 'Reviewed cash source submissions are immutable.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF (to_jsonb(NEW) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ReviewedBy' - 'ReviewedAt' - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy')
       IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ReviewedBy' - 'ReviewedAt' - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'Cash source submission scope and control totals are immutable after creation.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'PendingReview') OR
            (OLD."Status" = 'PendingReview' AND NEW."Status" IN ('Reviewed', 'Rejected'))) THEN
        RAISE EXCEPTION 'Invalid cash source submission transition from % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_submission$ LANGUAGE plpgsql;
CREATE TRIGGER trg_cash_source_submission_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON "PublicSectorCashSourceSubmissions"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_cash_source_submission();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_cash_source_submission_immutable ON "PublicSectorCashSourceSubmissions";
DROP FUNCTION IF EXISTS apex_guard_cash_source_submission();
""");
        migrationBuilder.DropTable(name: "PublicSectorCashSourceSubmissions");
    }
}
