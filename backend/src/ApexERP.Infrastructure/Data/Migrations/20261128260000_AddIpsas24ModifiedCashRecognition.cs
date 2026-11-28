using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128260000_AddIpsas24ModifiedCashRecognition")]
public sealed class AddIpsas24ModifiedCashRecognition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ModifiedCashRecognitionPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                FinancialYear = table.Column<int>(type: "integer", nullable: false),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                PolicyReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ReportingPeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReportingPeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PostPeriodRecognitionWindowDays = table.Column<int>(type: "integer", nullable: false),
                Treatment = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                ApprovalWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
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
                table.PrimaryKey("PK_ModifiedCashRecognitionPolicies", x => x.Id);
                table.CheckConstraint("CK_ModifiedCashPolicy_Period", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\"");
                table.CheckConstraint("CK_ModifiedCashPolicy_ReportingPeriod", "\"ReportingPeriodEnd\" > \"ReportingPeriodStart\"");
                table.CheckConstraint("CK_ModifiedCashPolicy_Window", "\"PostPeriodRecognitionWindowDays\" >= 0");
                table.CheckConstraint("CK_ModifiedCashPolicy_Version", "\"VersionNumber\" > 0");
                table.ForeignKey("FK_ModifiedCashPolicy_BudgetVersion", x => x.BudgetVersionId,
                    "PublicSectorBudgetVersions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ModifiedCashRecognitionMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceEventKind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                BudgetVersionLineId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModifiedCashRecognitionMappings", x => x.Id);
                table.ForeignKey("FK_ModifiedCashMapping_Policy", x => x.PolicyId,
                    "ModifiedCashRecognitionPolicies", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ModifiedCashMapping_BudgetLine", x => x.BudgetVersionLineId,
                    "PublicSectorBudgetVersionLines", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ModifiedCashRecognitionLedgerEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                CommitmentId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceEventKind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                EventKind = table.Column<int>(type: "integer", nullable: false),
                OriginalEventId = table.Column<Guid>(type: "uuid", nullable: true),
                Amount = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EvidenceAcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EvidenceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                ApprovalWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CertifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                CertifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModifiedCashRecognitionLedgerEvents", x => x.Id);
                table.CheckConstraint("CK_ModifiedCashLedger_Amount", "\"Amount\" > 0");
                table.ForeignKey("FK_ModifiedCashLedger_Policy", x => x.PolicyId,
                    "ModifiedCashRecognitionPolicies", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ModifiedCashLedger_Commitment", x => x.CommitmentId,
                    "Commitments", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ModifiedCashLedger_OriginalEvent", x => x.OriginalEventId,
                    "ModifiedCashRecognitionLedgerEvents", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("UX_ModifiedCashPolicy_Version",
            "ModifiedCashRecognitionPolicies", new[] { "CompanyId", "BudgetVersionId", "VersionNumber" }, unique: true);
        migrationBuilder.CreateIndex("IX_ModifiedCashRecognitionPolicies_BudgetVersionId",
            "ModifiedCashRecognitionPolicies", "BudgetVersionId");
        migrationBuilder.CreateIndex("UX_ModifiedCashMapping_EventKind",
            "ModifiedCashRecognitionMappings", new[] { "CompanyId", "PolicyId", "SourceEventKind" }, unique: true);
        migrationBuilder.CreateIndex("IX_ModifiedCashRecognitionMappings_PolicyId",
            "ModifiedCashRecognitionMappings", "PolicyId");
        migrationBuilder.CreateIndex("IX_ModifiedCashRecognitionMappings_BudgetVersionLineId",
            "ModifiedCashRecognitionMappings", "BudgetVersionLineId");
        migrationBuilder.CreateIndex("UX_ModifiedCashLedger_SourceEvent",
            "ModifiedCashRecognitionLedgerEvents", new[] { "CompanyId", "SourceEventId", "EventKind" }, unique: true);
        migrationBuilder.CreateIndex("UX_ModifiedCashLedger_OneReversal",
            "ModifiedCashRecognitionLedgerEvents", "OriginalEventId", unique: true,
            filter: "\"OriginalEventId\" IS NOT NULL");
        migrationBuilder.CreateIndex("IX_ModifiedCashLedger_CommitmentStatusDate",
            "ModifiedCashRecognitionLedgerEvents", new[] { "CompanyId", "CommitmentId", "Status", "EventDate" });
        migrationBuilder.CreateIndex("IX_ModifiedCashRecognitionLedgerEvents_PolicyId",
            "ModifiedCashRecognitionLedgerEvents", "PolicyId");
        migrationBuilder.CreateIndex("IX_ModifiedCashRecognitionLedgerEvents_CommitmentId",
            "ModifiedCashRecognitionLedgerEvents", "CommitmentId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ModifiedCashRecognitionLedgerEvents");
        migrationBuilder.DropTable("ModifiedCashRecognitionMappings");
        migrationBuilder.DropTable("ModifiedCashRecognitionPolicies");
    }
}
