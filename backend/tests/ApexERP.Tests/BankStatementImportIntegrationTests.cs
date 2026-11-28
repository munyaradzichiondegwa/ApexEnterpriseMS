using System.Security.Cryptography;
using System.Text;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests;

public sealed class BankStatementImportIntegrationTests
{
    [PostgresFact]
    public async Task Bank_statement_source_bytes_and_transactions_are_tenant_scoped_and_immutable()
    {
        await using var db = await TestDb.CreateAsync();
        var companyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var uploader = Guid.NewGuid();
        var authenticator = Guid.NewGuid();
        var bankAccountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var originalBytes = Encoding.UTF8.GetBytes("ExternalTransactionId,TransactionDate,Description,Reference,Debit,Credit,Currency\nTX-001,2026-12-15T10:00:00Z,Test,REF-001,10,0,USD");
        var digest = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var periodStart = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = db.NewContext(companyId, uploader))
        {
            seed.Companies.AddRange(
                new Company { Id = companyId, Name = "Bank Evidence Test", BaseCurrency = "USD", FunctionalCurrency = "USD" },
                new Company { Id = otherCompanyId, Name = "Other Bank Tenant", BaseCurrency = "USD", FunctionalCurrency = "USD" });
            var bankGl = new GlAccount
            {
                CompanyId = companyId, Code = "MC-BANK-001", Name = "Statement Bank",
                Type = GlAccountType.Asset, IsActive = true, IsBankAccount = true
            };
            var account = new BankAccount
            {
                Id = bankAccountId, CompanyId = companyId, Name = "Operating USD",
                AccountNo = "ACC-001", BankName = "Test Bank", Currency = "USD",
                GlAccountId = bankGl.Id
            };
            var batch = new BankStatementImportBatch
            {
                Id = batchId, CompanyId = companyId, BankAccountId = bankAccountId,
                SourceInstitution = "Test Bank", SourceAccountReference = "ACC-001",
                StatementReference = "STMT-2026-12", PeriodStart = periodStart, PeriodEnd = periodEnd,
                OpeningBalance = 100m, ClosingBalance = 90m, TotalDebits = 10m, TotalCredits = 0m,
                TransactionCount = 1, OriginalFileName = "statement.csv", MediaType = "text/csv",
                ParserVersion = "apex-bank-csv-v1", Sha256 = digest,
                ByteLength = originalBytes.Length, OriginalBytes = originalBytes,
                UploadedBy = uploader, UploadedAt = periodStart,
                AuthenticationMethod = BankStatementOriginAuthenticationMethod.UnverifiedUpload,
                Status = BankStatementImportStatus.PendingAuthentication
            };
            var imported = new BankStatementImportedTransaction
            {
                Id = transactionId, CompanyId = companyId, ImportBatchId = batchId,
                BankAccountId = bankAccountId, ExternalTransactionId = "TX-001",
                TransactionDate = periodStart.AddDays(14).AddHours(10), Description = "Test",
                Reference = "REF-001", Debit = 10m, Credit = 0m, Currency = "USD",
                SourceLineNumber = 2, TransactionFingerprint = new string('a', 64),
                Status = BankStatementImportedTransactionStatus.Imported
            };
            seed.GlAccounts.Add(bankGl);
            seed.BankAccounts.Add(account);
            seed.BankStatementImportBatches.Add(batch);
            seed.BankStatementImportedTransactions.Add(imported);
            await seed.SaveChangesAsync();
        }

        await using (var otherTenant = db.NewContext(otherCompanyId))
        {
            Assert.Empty(await otherTenant.BankStatementImportBatches.ToListAsync());
            Assert.Empty(await otherTenant.BankStatementImportedTransactions.ToListAsync());
        }

        await using (var authentication = db.NewContext(companyId, authenticator))
        {
            var batch = await authentication.BankStatementImportBatches.SingleAsync(x => x.Id == batchId);
            batch.AuthenticationMethod = BankStatementOriginAuthenticationMethod.IndependentBankConfirmation;
            batch.AuthenticationEvidenceReference = "BANK-CONFIRM-001";
            batch.AuthenticatedBy = authenticator;
            batch.AuthenticatedAt = periodStart.AddDays(20);
            batch.Status = BankStatementImportStatus.Authenticated;
            await authentication.SaveChangesAsync();
        }

        await using (var immutableFile = db.NewContext(companyId))
        {
            var batch = await immutableFile.BankStatementImportBatches.SingleAsync(x => x.Id == batchId);
            batch.OriginalBytes = Encoding.UTF8.GetBytes("tampered");
            await Assert.ThrowsAsync<InvalidOperationException>(() => immutableFile.SaveChangesAsync());
        }

        await using (var immutableTransaction = db.NewContext(companyId))
        {
            var imported = await immutableTransaction.BankStatementImportedTransactions.SingleAsync(x => x.Id == transactionId);
            imported.Debit = 999m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => immutableTransaction.SaveChangesAsync());
        }

        await using (var verify = db.NewContext(companyId))
        {
            var batch = await verify.BankStatementImportBatches.SingleAsync(x => x.Id == batchId);
            var imported = await verify.BankStatementImportedTransactions.SingleAsync(x => x.Id == transactionId);
            Assert.Equal(digest, batch.Sha256);
            Assert.Equal(originalBytes, batch.OriginalBytes);
            Assert.Equal(BankStatementImportStatus.Authenticated, batch.Status);
            Assert.Equal(10m, imported.Debit);
            Assert.Equal(BankStatementImportedTransactionStatus.Imported, imported.Status);
        }
    }
}
