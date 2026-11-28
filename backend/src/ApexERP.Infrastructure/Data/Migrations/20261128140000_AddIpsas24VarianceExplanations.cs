using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128140000_AddIpsas24VarianceExplanations")]
public sealed class AddIpsas24VarianceExplanations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PublicSectorBudgetVarianceExplanations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionLineId = table.Column<Guid>(type: "uuid", nullable: false),
                VarianceAmount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Explanation = table.Column<string>(type: "text", nullable: false),
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
                table.PrimaryKey("PK_PublicSectorBudgetVarianceExplanations", x => x.Id);
                table.CheckConstraint("CK_PublicSectorBudgetVarianceExplanation_Amount", "\"VarianceAmount\" <> 0");
                table.ForeignKey("FK_PublicSectorBudgetVarianceExplanation_BudgetVersion",
                    x => x.BudgetVersionId, "PublicSectorBudgetVersions", "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_PublicSectorBudgetVarianceExplanation_BudgetLine",
                    x => x.BudgetVersionLineId, "PublicSectorBudgetVersionLines", "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("UX_PublicSectorBudgetVarianceExplanation_Line",
            "PublicSectorBudgetVarianceExplanations",
            new[] { "CompanyId", "BudgetVersionLineId" }, unique: true);
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetVarianceExplanations_BudgetVersionId",
            "PublicSectorBudgetVarianceExplanations", "BudgetVersionId");
        migrationBuilder.CreateIndex("IX_PublicSectorBudgetVarianceExplanations_BudgetVersionLineId",
            "PublicSectorBudgetVarianceExplanations", "BudgetVersionLineId");

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_public_sector_budget_variance_explanation() RETURNS trigger AS $ipsas_variance$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Budget variance explanations cannot be deleted.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."SubmittedBy" IS NOT NULL OR NEW."SubmittedAt" IS NOT NULL
           OR NEW."ReviewedBy" IS NOT NULL OR NEW."ReviewedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Budget variance explanations must begin as drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Approved', 'Rejected') THEN
        RAISE EXCEPTION 'Final budget variance explanations are immutable.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF (to_jsonb(NEW) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ReviewedBy' - 'ReviewedAt'
        - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy')
       IS DISTINCT FROM
       (to_jsonb(OLD) - 'Status' - 'SubmittedBy' - 'SubmittedAt' - 'ReviewedBy' - 'ReviewedAt'
        - 'ReviewNote' - 'UpdatedAt' - 'UpdatedBy') THEN
        RAISE EXCEPTION 'Variance amount, explanation and evidence are immutable after creation.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'PendingReview') OR
            (OLD."Status" = 'PendingReview' AND NEW."Status" IN ('Approved', 'Rejected'))) THEN
        RAISE EXCEPTION 'Invalid variance explanation transition from % to %.',
            OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$ipsas_variance$ LANGUAGE plpgsql;
CREATE TRIGGER trg_public_sector_budget_variance_explanation_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON "PublicSectorBudgetVarianceExplanations"
    FOR EACH ROW EXECUTE FUNCTION apex_guard_public_sector_budget_variance_explanation();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_public_sector_budget_variance_explanation_immutable ON "PublicSectorBudgetVarianceExplanations";
DROP FUNCTION IF EXISTS apex_guard_public_sector_budget_variance_explanation();
""");
        migrationBuilder.DropTable(name: "PublicSectorBudgetVarianceExplanations");
    }
}
