using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>Declares and pays dividends, posting the correct double entry at each stage and
    /// feeding both the Statement of Changes in Equity and the Statement of Cash Flows.</summary>
    public class DividendService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        public DividendService(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        /// <summary>Dr Retained Earnings, Cr Dividends Payable -- recognises the liability at declaration.</summary>
        public async Task<DividendDeclaration> DeclareAsync(Guid companyId, DateTime declarationDate, decimal amount,
            string currency, Guid retainedEarningsAccountId, Guid dividendsPayableAccountId, CancellationToken ct = default)
        {
            var je = await _gl.PostAsync(companyId, declarationDate, JournalSource.Manual, "Dividend declaration", null,
                new[]
                {
                    new GlLineRequest(retainedEarningsAccountId, amount, 0, currency, 1m, null, "Dividend declared"),
                    new GlLineRequest(dividendsPayableAccountId, 0, amount, currency, 1m, null, "Dividends payable to shareholders")
                }, ct);

            var declaration = new DividendDeclaration
            {
                CompanyId = companyId,
                DeclarationDate = declarationDate,
                Amount = amount,
                Currency = currency,
                DeclarationJournalEntryId = je.Id
            };
            _db.DividendDeclarations.Add(declaration);
            await _db.SaveChangesAsync(ct);
            return declaration;
        }

        /// <summary>Dr Dividends Payable, Cr Bank -- settles the declared dividend in cash.</summary>
        public async Task<DividendDeclaration> PayAsync(Guid companyId, Guid dividendDeclarationId, DateTime paymentDate,
            Guid dividendsPayableAccountId, Guid bankAccountId, CancellationToken ct = default)
        {
            var declaration = await _db.DividendDeclarations.FirstOrDefaultAsync(d => d.Id == dividendDeclarationId && d.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Dividend declaration not found.");
            if (declaration.IsPaid) throw new InvalidOperationException("This dividend has already been paid.");

            var je = await _gl.PostAsync(companyId, paymentDate, JournalSource.Bank, "Dividend payment", null,
                new[]
                {
                    new GlLineRequest(dividendsPayableAccountId, declaration.Amount, 0, declaration.Currency, 1m, null, "Dividends payable settled"),
                    new GlLineRequest(bankAccountId, 0, declaration.Amount, declaration.Currency, 1m, null, "Cash paid to shareholders")
                }, ct);

            declaration.IsPaid = true;
            declaration.PaymentDate = paymentDate;
            declaration.PaymentJournalEntryId = je.Id;
            await _db.SaveChangesAsync(ct);
            return declaration;
        }
    }
}
