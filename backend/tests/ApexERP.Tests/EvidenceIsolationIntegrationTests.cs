using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexERP.Tests
{
    public class EvidenceIsolationIntegrationTests
    {
        [PostgresFact]
        public async Task Evidence_download_and_attachment_are_confined_to_the_authenticated_company()
        {
            await using var db = await TestDb.CreateAsync();
            var companyA = Guid.NewGuid();
            var companyB = Guid.NewGuid();
            var journalId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var periodId = Guid.NewGuid();

            await using (var seed = db.NewContext(null))
            {
                seed.Companies.AddRange(
                    new Company { Id = companyA, Name = "Evidence Company A", BaseCurrency = "USD", FunctionalCurrency = "USD" },
                    new Company { Id = companyB, Name = "Evidence Company B", BaseCurrency = "USD", FunctionalCurrency = "USD" });
                seed.AccountingPeriods.Add(new AccountingPeriod
                {
                    Id = periodId, CompanyId = companyA, Year = 2026, PeriodNo = 10,
                    StartDate = new DateTime(2026, 10, 1), EndDate = new DateTime(2026, 10, 31)
                });
                seed.JournalEntries.Add(new JournalEntry
                {
                    Id = journalId, CompanyId = companyA, PeriodId = periodId,
                    EntryNo = "EVIDENCE-001", EntryDate = new DateTime(2026, 10, 15),
                    Source = JournalSource.Manual, Status = JournalStatus.Draft
                });
                await seed.SaveChangesAsync();
            }

            var payload = System.Text.Encoding.UTF8.GetBytes("company A confidential evidence");
            Guid evidenceId;
            await using (var companyContext = db.NewContext(companyA, userId))
            {
                var evidence = new EvidenceService(companyContext, new AuditService(companyContext));
                var attached = await evidence.AttachAsync(companyA, nameof(JournalEntry), journalId,
                    "support.txt", "text/plain", payload, "Company A evidence", userId);
                evidenceId = attached.Id;

                var (downloaded, integrityOk) = await evidence.DownloadAsync(companyA, evidenceId, userId);
                Assert.True(integrityOk);
                Assert.Equal(payload, downloaded.Content);
                Assert.Single(await evidence.ListAsync(companyA, nameof(JournalEntry), journalId));
            }

            await using (var otherCompanyContext = db.NewContext(companyB, userId))
            {
                var evidence = new EvidenceService(otherCompanyContext, new AuditService(otherCompanyContext));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    evidence.AttachAsync(companyB, nameof(JournalEntry), journalId, "stolen.txt",
                        "text/plain", payload, null, userId));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    evidence.DownloadAsync(companyB, evidenceId, userId));
                Assert.Empty(await evidence.ListAsync(companyB, nameof(JournalEntry), journalId));
            }

            await using (var mismatchedContext = db.NewContext(companyB, userId))
            {
                var evidence = new EvidenceService(mismatchedContext, new AuditService(mismatchedContext));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    evidence.DownloadAsync(companyA, evidenceId, userId));
            }
        }
    }
}
