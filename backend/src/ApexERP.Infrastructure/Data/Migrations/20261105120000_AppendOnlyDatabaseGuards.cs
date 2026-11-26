using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Database-level append-only guards (APEXERP spec 12.4 / 23.2): triggers that make PostgreSQL itself refuse
    /// updates/deletes/truncation of the audit log, edits to journal lines, and edits/deletion of posted journals
    /// (only Posted -> Reversed is allowed). SQL-only: it changes no table or column, so the model snapshot is
    /// unchanged. The same SQL lives in backend/database/hardening/001_append_only_guards.sql and was exercised
    /// against PostgreSQL 16 (allowed workflow moves succeed; seven tamper attempts and an invalid transition fail).
    /// </summary>
    [DbContext(typeof(ApexErpDbContext))]
    [Migration("20261105120000_AppendOnlyDatabaseGuards")]
    public partial class AppendOnlyDatabaseGuards : Migration
    {
        private const string GuardSql = """
-- ApexERP database-level append-only guards (spec sections 12.4, 23.2, 28.3 "audit-log tampering").
-- The application already refuses these changes (ApexErpDbContext.EnforceLedgerImmutability); this makes the
-- DATABASE refuse them too, so a bug, a raw ExecuteUpdate/ExecuteDelete, or a person with SQL access to the
-- application login cannot silently alter posted history.
-- JournalStatus values: 0 Draft, 1 Posted, 2 Reversed, 3 Submitted, 4 Approved, 5 Rejected, 6 Cancelled.
-- Idempotent: safe to run more than once.

CREATE OR REPLACE FUNCTION apex_block_audit_changes() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'The audit log is append-only: % is not permitted.', TG_OP USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_auditlogs_append_only ON "AuditLogs";
CREATE TRIGGER trg_auditlogs_append_only BEFORE UPDATE OR DELETE ON "AuditLogs"
    FOR EACH ROW EXECUTE FUNCTION apex_block_audit_changes();
DROP TRIGGER IF EXISTS trg_auditlogs_no_truncate ON "AuditLogs";
CREATE TRIGGER trg_auditlogs_no_truncate BEFORE TRUNCATE ON "AuditLogs"
    FOR EACH STATEMENT EXECUTE FUNCTION apex_block_audit_changes();

CREATE OR REPLACE FUNCTION apex_guard_journal_lines() RETURNS trigger AS $$
DECLARE parent_status integer;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'Journal lines are immutable. Correct a posted entry by reversing it.' USING ERRCODE = 'restrict_violation';
    END IF;
    SELECT "Status" INTO parent_status FROM "JournalEntries" WHERE "Id" = OLD."JournalEntryId";
    IF parent_status IN (1, 2) THEN
        RAISE EXCEPTION 'Lines of a posted journal cannot be deleted. Reverse the journal instead.' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_journallines_guard ON "JournalLines";
CREATE TRIGGER trg_journallines_guard BEFORE UPDATE OR DELETE ON "JournalLines"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_journal_lines();

CREATE OR REPLACE FUNCTION apex_guard_posted_journal() RETURNS trigger AS $$
BEGIN
    IF OLD."Status" NOT IN (1, 2) THEN
        IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
        RETURN NEW;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'A posted journal entry cannot be deleted. Reverse it instead.' USING ERRCODE = 'restrict_violation';
    END IF;

    -- Only Status (Posted -> Reversed) and the audit stamps may change on a posted entry.
    IF (to_jsonb(NEW) - 'Status' - 'UpdatedAt' - 'UpdatedBy') IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'A posted journal entry cannot be edited. Reverse it and post a corrected entry.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW."Status" <> OLD."Status" AND NOT (OLD."Status" = 1 AND NEW."Status" = 2) THEN
        RAISE EXCEPTION 'A journal entry cannot move from status % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_journalentries_guard ON "JournalEntries";
CREATE TRIGGER trg_journalentries_guard BEFORE UPDATE OR DELETE ON "JournalEntries"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_posted_journal();
""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_journalentries_guard ON "JournalEntries";
DROP TRIGGER IF EXISTS trg_journallines_guard ON "JournalLines";
DROP TRIGGER IF EXISTS trg_auditlogs_no_truncate ON "AuditLogs";
DROP TRIGGER IF EXISTS trg_auditlogs_append_only ON "AuditLogs";
DROP FUNCTION IF EXISTS apex_guard_posted_journal();
DROP FUNCTION IF EXISTS apex_guard_journal_lines();
DROP FUNCTION IF EXISTS apex_block_audit_changes();
""");
        }
    }
}
