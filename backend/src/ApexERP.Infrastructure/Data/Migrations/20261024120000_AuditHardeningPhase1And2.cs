using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Repository-audit hardening (findings 4, 5, 6, 8 and the design document's "essential database safeguards"):
    ///   * ExceptionItems -- the visible exception queue (failed inter-company mirrors, ...);
    ///   * unique IX_JournalEntries_ReversalOfId -- a journal can be reversed at most once;
    ///   * unique IX_AccountingPeriods_CompanyId_Year_PeriodNo -- one period per company per month;
    ///   * unique IX_IntercompanyTransactions_SourceArInvoiceId -- an AR invoice is mirrored at most once;
    ///   * CK_JournalLines_Amounts / CK_JournalLines_ExchangeRate -- non-negative single-sided amounts, positive FX rate.
    ///
    /// BEFORE APPLYING to a database that already holds data, confirm these return no rows / zero (otherwise the
    /// matching index or constraint cannot be created and must be fixed first):
    ///   SELECT "CompanyId","Year","PeriodNo",count(*) FROM "AccountingPeriods" GROUP BY 1,2,3 HAVING count(*) > 1;
    ///   SELECT "ReversalOfId",count(*) FROM "JournalEntries" WHERE "ReversalOfId" IS NOT NULL GROUP BY 1 HAVING count(*) > 1;
    ///   SELECT "SourceArInvoiceId",count(*) FROM "IntercompanyTransactions" WHERE "SourceArInvoiceId" IS NOT NULL GROUP BY 1 HAVING count(*) > 1;
    ///   SELECT count(*) FROM "JournalLines" WHERE "Debit" < 0 OR "Credit" < 0 OR ("Debit" > 0 AND "Credit" > 0) OR "ExchangeRate" <= 0;
    /// </summary>
    public partial class AuditHardeningPhase1And2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExceptionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExceptionItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPeriods_CompanyId_Year_PeriodNo",
                table: "AccountingPeriods",
                columns: new[] { "CompanyId", "Year", "PeriodNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionItems_CompanyId_Status_Category",
                table: "ExceptionItems",
                columns: new[] { "CompanyId", "Status", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyTransactions_SourceArInvoiceId",
                table: "IntercompanyTransactions",
                column: "SourceArInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversalOfId",
                table: "JournalEntries",
                column: "ReversalOfId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_JournalLines_Amounts",
                table: "JournalLines",
                sql: "\"Debit\" >= 0 AND \"Credit\" >= 0 AND NOT (\"Debit\" > 0 AND \"Credit\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JournalLines_ExchangeRate",
                table: "JournalLines",
                sql: "\"ExchangeRate\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_JournalLines_ExchangeRate",
                table: "JournalLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JournalLines_Amounts",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_ReversalOfId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_IntercompanyTransactions_SourceArInvoiceId",
                table: "IntercompanyTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountingPeriods_CompanyId_Year_PeriodNo",
                table: "AccountingPeriods");

            migrationBuilder.DropTable(
                name: "ExceptionItems");
        }
    }
}
