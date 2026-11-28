using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128090000_AddIpsas24ExpectedCashSources")]
public sealed class AddIpsas24ExpectedCashSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorExpectedCashSources",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceKind = table.Column<string>(type: "text", nullable: false),
                SourceCode = table.Column<string>(type: "text", nullable: false),
                SourceName = table.Column<string>(type: "text", nullable: false),
                SourceIdentifier = table.Column<string>(type: "text", nullable: true),
                IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicSectorExpectedCashSources", x => x.Id);
                table.CheckConstraint("CK_PublicSectorExpectedCashSource_Period", "\"EffectiveTo\" > \"EffectiveFrom\"");
                table.ForeignKey("FK_ExpectedCashSource_BudgetVersion",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_PublicSectorExpectedCashSources_BudgetVersionId",
            "PublicSectorExpectedCashSources", "BudgetVersionId");
        migrationBuilder.CreateIndex("UX_PublicSectorExpectedCashSource_Key",
            "PublicSectorExpectedCashSources",
            new[] { "CompanyId", "BudgetVersionId", "SourceKind", "SourceCode" }, unique: true);

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_expected_cash_source() RETURNS trigger AS $ipsas_source$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Expected cash source register entries cannot be deleted; retire through a controlled successor register.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."SubmittedBy" IS NOT NULL OR NEW."SubmittedAt" IS NOT NULL
           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Expected cash sources must begin as unsubmitted drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Approved', 'Rejected') THEN
        RAISE EXCEPTION 'Final expected cash source register entries are immutable.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF (to_jsonb(NEW) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ApprovedBy' - 'ApprovedAt' - 'UpdatedAt' - 'UpdatedBy')
       IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ApprovedBy' - 'ApprovedAt' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'Expected cash source identity and scope are immutable after creation.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'PendingApproval') OR
            (OLD."Status" = 'PendingApproval' AND NEW."Status" IN ('Approved', 'Rejected'))) THEN
        RAISE EXCEPTION 'Invalid expected cash source transition from % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_source$ LANGUAGE plpgsql;
CREATE TRIGGER trg_expected_cash_source_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON "PublicSectorExpectedCashSources"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_expected_cash_source();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_expected_cash_source_immutable ON "PublicSectorExpectedCashSources";
DROP FUNCTION IF EXISTS apex_guard_expected_cash_source();
""");
        migrationBuilder.DropTable(name: "PublicSectorExpectedCashSources");
    }
}
