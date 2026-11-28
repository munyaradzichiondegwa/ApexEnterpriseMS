using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128120000_AddIpsas24RecordReconciliation")]
public sealed class AddIpsas24RecordReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RecordLevelStatus", table: "PublicSectorCashSourceSubmissions",
            type: "text", nullable: false, defaultValue: "NotStarted");
        migrationBuilder.AddColumn<Guid>(
            name: "RecordLevelSubmittedBy", table: "PublicSectorCashSourceSubmissions",
            type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "RecordLevelSubmittedAt", table: "PublicSectorCashSourceSubmissions",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "RecordLevelReviewedBy", table: "PublicSectorCashSourceSubmissions",
            type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "RecordLevelReviewedAt", table: "PublicSectorCashSourceSubmissions",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RecordLevelReviewNote", table: "PublicSectorCashSourceSubmissions",
            type: "text", nullable: true);

        migrationBuilder.CreateTable(
            name: "PublicSectorCashSourceReconciliationLines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                CashSourceSubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceReference = table.Column<string>(type: "text", nullable: false),
                SourceAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                BudgetExecutionActualId = table.Column<Guid>(type: "uuid", nullable: true),
                AllocatedAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                IsExcluded = table.Column<bool>(type: "boolean", nullable: false),
                ExclusionReason = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorCashSourceReconciliationLines", x => x.Id);
                table.CheckConstraint("CK_CashSourceReconciliationLine_SourceAmount", "\"SourceAmount\" > 0");
                table.CheckConstraint("CK_CashSourceReconciliationLine_Amounts",
                    "(\"IsExcluded\" = TRUE AND \"BudgetExecutionActualId\" IS NULL AND \"AllocatedAmount\" = 0 AND length(trim(\"ExclusionReason\")) > 0) OR (\"IsExcluded\" = FALSE AND \"BudgetExecutionActualId\" IS NOT NULL AND \"AllocatedAmount\" > 0 AND \"ExclusionReason\" IS NULL)");
                table.ForeignKey("FK_CashSourceReconciliationLine_Submission",
                    x => x.CashSourceSubmissionId, "PublicSectorCashSourceSubmissions", "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_CashSourceReconciliationLine_Actual",
                    x => x.BudgetExecutionActualId, "PublicSectorBudgetExecutionActuals", "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_CashSourceReconciliationLines_Submission",
            "PublicSectorCashSourceReconciliationLines", "CashSourceSubmissionId");
        migrationBuilder.CreateIndex("UX_CashSourceReconciliationLine_Allocation",
            "PublicSectorCashSourceReconciliationLines",
            new[] { "CompanyId", "CashSourceSubmissionId", "SourceRecordId", "BudgetExecutionActualId" },
            unique: true);
        migrationBuilder.CreateIndex("UX_CashSourceReconciliationLine_Actual",
            "PublicSectorCashSourceReconciliationLines", "BudgetExecutionActualId",
            unique: true, filter: "\"BudgetExecutionActualId\" IS NOT NULL");
        migrationBuilder.CreateIndex("UX_CashSourceReconciliationLine_Exclusion",
            "PublicSectorCashSourceReconciliationLines",
            new[] { "CompanyId", "CashSourceSubmissionId", "SourceRecordId" },
            unique: true, filter: "\"IsExcluded\" = TRUE");

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_cash_source_submission() RETURNS trigger AS $ipsas_submission$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Cash source submissions cannot be deleted.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."SubmittedBy" IS NOT NULL OR NEW."SubmittedAt" IS NOT NULL
           OR NEW."ReviewedBy" IS NOT NULL OR NEW."ReviewedAt" IS NOT NULL
           OR NEW."RecordLevelStatus" <> 'NotStarted' THEN
            RAISE EXCEPTION 'Cash source submissions must begin as unsubmitted drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;

    IF OLD."Status" IN ('Reviewed', 'Rejected') THEN
        IF OLD."Status" <> 'Reviewed' OR OLD."RecordLevelStatus" IN ('Reviewed', 'Rejected') THEN
            RAISE EXCEPTION 'Final cash source reconciliation is immutable.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (to_jsonb(NEW) - 'RecordLevelStatus' - 'RecordLevelSubmittedBy' - 'RecordLevelSubmittedAt'
            - 'RecordLevelReviewedBy' - 'RecordLevelReviewedAt' - 'RecordLevelReviewNote' - 'UpdatedAt' - 'UpdatedBy')
           IS DISTINCT FROM
           (to_jsonb(OLD) - 'RecordLevelStatus' - 'RecordLevelSubmittedBy' - 'RecordLevelSubmittedAt'
            - 'RecordLevelReviewedBy' - 'RecordLevelReviewedAt' - 'RecordLevelReviewNote' - 'UpdatedAt' - 'UpdatedBy') THEN
            RAISE EXCEPTION 'Reviewed source controls are immutable.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NOT ((OLD."RecordLevelStatus" = 'NotStarted' AND NEW."RecordLevelStatus" = 'PendingReview') OR
                (OLD."RecordLevelStatus" = 'PendingReview' AND NEW."RecordLevelStatus" IN ('Reviewed', 'Rejected'))) THEN
            RAISE EXCEPTION 'Invalid record reconciliation transition from % to %.',
                OLD."RecordLevelStatus", NEW."RecordLevelStatus" USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;

    IF (to_jsonb(NEW) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ReviewedBy' - 'ReviewedAt'
        - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy')
       IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ReviewedBy' - 'ReviewedAt'
        - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'Cash source submission scope and control totals are immutable after creation.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'PendingReview') OR
            (OLD."Status" = 'PendingReview' AND NEW."Status" IN ('Reviewed', 'Rejected'))) THEN
        RAISE EXCEPTION 'Invalid cash source submission transition from % to %.',
            OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_submission$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION apex_guard_cash_source_reconciliation_line() RETURNS trigger AS $ipsas_line$
DECLARE parent_status text;
DECLARE record_status text;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Source reconciliation lines are append-only.' USING ERRCODE = 'restrict_violation';
    END IF;
    SELECT "Status", "RecordLevelStatus" INTO parent_status, record_status
      FROM "PublicSectorCashSourceSubmissions"
     WHERE "Id" = NEW."CashSourceSubmissionId" AND "CompanyId" = NEW."CompanyId";
    IF parent_status IS DISTINCT FROM 'Reviewed' OR record_status IS DISTINCT FROM 'NotStarted' THEN
        RAISE EXCEPTION 'Source reconciliation lines can only be added to a reviewed source submission before record-level submission.' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_line$ LANGUAGE plpgsql;
CREATE TRIGGER trg_cash_source_reconciliation_line_append_only
    BEFORE INSERT OR UPDATE OR DELETE ON "PublicSectorCashSourceReconciliationLines"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_cash_source_reconciliation_line();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_cash_source_reconciliation_line_append_only ON "PublicSectorCashSourceReconciliationLines";
DROP FUNCTION IF EXISTS apex_guard_cash_source_reconciliation_line();
""");
        migrationBuilder.DropTable(name: "PublicSectorCashSourceReconciliationLines");
        migrationBuilder.DropColumn(name: "RecordLevelStatus", table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.DropColumn(name: "RecordLevelSubmittedBy", table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.DropColumn(name: "RecordLevelSubmittedAt", table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.DropColumn(name: "RecordLevelReviewedBy", table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.DropColumn(name: "RecordLevelReviewedAt", table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.DropColumn(name: "RecordLevelReviewNote", table: "PublicSectorCashSourceSubmissions");
        migrationBuilder.Sql("""
DROP FUNCTION IF EXISTS apex_guard_cash_source_submission();
""");
    }
}
