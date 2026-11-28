using System;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261128270000_AddIpsas24BankStatementImports")]
public sealed class AddIpsas24BankStatementImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BankStatementImportBatches",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceInstitution = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SourceAccountReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                StatementReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                OpeningBalance = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                ClosingBalance = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                TotalDebits = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                TotalCredits = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                TransactionCount = table.Column<int>(type: "integer", nullable: false),
                OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                MediaType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ParserVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ByteLength = table.Column<long>(type: "bigint", nullable: false),
                OriginalBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                UploadedBy = table.Column<Guid>(type: "uuid", nullable: false),
                UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                AuthenticationMethod = table.Column<int>(type: "integer", nullable: false),
                AuthenticationEvidenceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                AuthenticationWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                AuthenticatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                AuthenticatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                CompletenessCertifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                CompletenessCertifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BankStatementImportBatches", x => x.Id);
                table.CheckConstraint("CK_BankStatementBatch_Period", "\"PeriodEnd\" > \"PeriodStart\"");
                table.CheckConstraint("CK_BankStatementBatch_ByteLength", "\"ByteLength\" > 0");
                table.CheckConstraint("CK_BankStatementBatch_Totals",
                    "\"TotalDebits\" >= 0 AND \"TotalCredits\" >= 0 AND \"TransactionCount\" >= 0");
                table.ForeignKey("FK_BankStatementBatch_BankAccount", x => x.BankAccountId,
                    "BankAccounts", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "BankStatementImportedTransactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                ImportBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalTransactionId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                Debit = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Credit = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                SourceLineNumber = table.Column<int>(type: "integer", nullable: false),
                TransactionFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                MatchedBankTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                MatchedBy = table.Column<Guid>(type: "uuid", nullable: true),
                MatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ExclusionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ExcludedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ExcludedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BankStatementImportedTransactions", x => x.Id);
                table.CheckConstraint("CK_BankStatementTransaction_Amounts",
                    "\"Debit\" >= 0 AND \"Credit\" >= 0 AND NOT (\"Debit\" > 0 AND \"Credit\" > 0)");
                table.CheckConstraint("CK_BankStatementTransaction_SourceLine", "\"SourceLineNumber\" > 1");
                table.ForeignKey("FK_BankStatementTransaction_Batch", x => x.ImportBatchId,
                    "BankStatementImportBatches", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_BankStatementTransaction_BankAccount", x => x.BankAccountId,
                    "BankAccounts", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_BankStatementTransaction_MatchedBankTransaction", x => x.MatchedBankTransactionId,
                    "BankTransactions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("UX_BankStatementBatch_StatementIdentity",
            "BankStatementImportBatches", new[] { "CompanyId", "BankAccountId", "StatementReference" }, unique: true);
        migrationBuilder.CreateIndex("IX_BankStatementBatch_AccountPeriod",
            "BankStatementImportBatches", new[] { "CompanyId", "BankAccountId", "PeriodStart", "PeriodEnd" });
        migrationBuilder.CreateIndex("IX_BankStatementImportBatches_BankAccountId",
            "BankStatementImportBatches", "BankAccountId");
        migrationBuilder.CreateIndex("UX_BankStatementTransaction_ExternalIdentity",
            "BankStatementImportedTransactions", new[] { "CompanyId", "BankAccountId", "ExternalTransactionId" }, unique: true);
        migrationBuilder.CreateIndex("UX_BankStatementTransaction_Fingerprint",
            "BankStatementImportedTransactions", new[] { "CompanyId", "BankAccountId", "TransactionFingerprint" }, unique: true);
        migrationBuilder.CreateIndex("UX_BankStatementTransaction_MatchedBankTransaction",
            "BankStatementImportedTransactions", "MatchedBankTransactionId", unique: true,
            filter: "\"MatchedBankTransactionId\" IS NOT NULL");
        migrationBuilder.CreateIndex("IX_BankStatementTransaction_BatchStatus",
            "BankStatementImportedTransactions", new[] { "CompanyId", "ImportBatchId", "Status" });
        migrationBuilder.CreateIndex("IX_BankStatementImportedTransactions_ImportBatchId",
            "BankStatementImportedTransactions", "ImportBatchId");
        migrationBuilder.CreateIndex("IX_BankStatementImportedTransactions_BankAccountId",
            "BankStatementImportedTransactions", "BankAccountId");
        migrationBuilder.CreateIndex("IX_BankStatementImportedTransactions_MatchedBankTransactionId",
            "BankStatementImportedTransactions", "MatchedBankTransactionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BankStatementImportedTransactions");
        migrationBuilder.DropTable("BankStatementImportBatches");
    }
}
