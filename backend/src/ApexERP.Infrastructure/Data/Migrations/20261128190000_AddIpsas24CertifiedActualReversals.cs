using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128190000_AddIpsas24CertifiedActualReversals")]
public sealed class AddIpsas24CertifiedActualReversals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ReversalReason",
            table: "PublicSectorBudgetExecutionActuals",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: true);

        migrationBuilder.DropIndex(
            name: "UX_PublicSectorBudgetExecutionActual_SourceAllocation",
            table: "PublicSectorBudgetExecutionActuals");

        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetExecutionActual_SourceAllocation",
            table: "PublicSectorBudgetExecutionActuals",
            columns: new[] { "CompanyId", "SourceKind", "SourceRecordId", "BudgetVersionLineId" },
            unique: true,
            filter: "\"ReversalOfId\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetExecutionActual_OneReversal",
            table: "PublicSectorBudgetExecutionActuals",
            column: "ReversalOfId",
            unique: true,
            filter: "\"ReversalOfId\" IS NOT NULL");

        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_budget_execution_actual() RETURNS trigger AS $ipsas_actual$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 0 OR NEW."CertifiedBy" IS NOT NULL OR NEW."CertifiedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Budget execution actuals must be inserted as uncertified drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NEW."ReversalOfId" IS NOT NULL AND
           (NEW."ReversalReason" IS NULL OR length(trim(NEW."ReversalReason")) < 10) THEN
            RAISE EXCEPTION 'A reversal requires a documented reason.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Budget execution actual evidence is immutable; a controlled reversal workflow is required.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW."Status" = OLD."Status" THEN
        IF (to_jsonb(NEW) - 'UpdatedAt' - 'UpdatedBy') IS DISTINCT FROM
           (to_jsonb(OLD) - 'UpdatedAt' - 'UpdatedBy') THEN
            RAISE EXCEPTION 'Budget execution actual evidence and certification are immutable.' USING ERRCODE = 'restrict_violation';
        END IF;
    ELSE
        IF (to_jsonb(NEW) - 'Status' - 'CertifiedBy' - 'CertifiedAt' - 'UpdatedAt' - 'UpdatedBy') IS DISTINCT FROM
           (to_jsonb(OLD) - 'Status' - 'CertifiedBy' - 'CertifiedAt' - 'UpdatedAt' - 'UpdatedBy') THEN
            RAISE EXCEPTION 'Budget execution actual source evidence cannot be edited.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NOT ((OLD."Status" = 0 AND NEW."Status" = 1) OR
                (OLD."Status" = 1 AND NEW."Status" IN (2, 3)) OR
                (OLD."Status" = 2 AND NEW."Status" = 4)) THEN
            RAISE EXCEPTION 'Invalid budget actual status transition from % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$ipsas_actual$ LANGUAGE plpgsql;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION apex_guard_budget_execution_actual() RETURNS trigger AS $ipsas_actual$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 0 OR NEW."CertifiedBy" IS NOT NULL OR NEW."CertifiedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'Budget execution actuals must be inserted as uncertified drafts.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Budget execution actual evidence is immutable; a controlled reversal workflow is required.' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW."Status" = OLD."Status" THEN
        IF (to_jsonb(NEW) - 'UpdatedAt' - 'UpdatedBy') IS DISTINCT FROM
           (to_jsonb(OLD) - 'UpdatedAt' - 'UpdatedBy') THEN
            RAISE EXCEPTION 'Budget execution actual evidence and certification are immutable.' USING ERRCODE = 'restrict_violation';
        END IF;
    ELSE
        IF (to_jsonb(NEW) - 'Status' - 'CertifiedBy' - 'CertifiedAt' - 'UpdatedAt' - 'UpdatedBy') IS DISTINCT FROM
           (to_jsonb(OLD) - 'Status' - 'CertifiedBy' - 'CertifiedAt' - 'UpdatedAt' - 'UpdatedBy') THEN
            RAISE EXCEPTION 'Budget execution actual source evidence cannot be edited.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NOT ((OLD."Status" = 0 AND NEW."Status" = 1) OR
                (OLD."Status" = 1 AND NEW."Status" IN (2, 3))) THEN
            RAISE EXCEPTION 'Invalid budget actual status transition from % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$ipsas_actual$ LANGUAGE plpgsql;
""");
        migrationBuilder.DropIndex("UX_PublicSectorBudgetExecutionActual_OneReversal", "PublicSectorBudgetExecutionActuals");
        migrationBuilder.DropIndex("UX_PublicSectorBudgetExecutionActual_SourceAllocation", "PublicSectorBudgetExecutionActuals");
        migrationBuilder.CreateIndex(
            name: "UX_PublicSectorBudgetExecutionActual_SourceAllocation",
            table: "PublicSectorBudgetExecutionActuals",
            columns: new[] { "CompanyId", "SourceKind", "SourceRecordId", "BudgetVersionLineId" },
            unique: true);
        migrationBuilder.DropColumn(name: "ReversalReason", table: "PublicSectorBudgetExecutionActuals");
    }
}
