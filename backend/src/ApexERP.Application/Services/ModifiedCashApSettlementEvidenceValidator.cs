using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services;

public sealed record ModifiedCashApSettlementEvidence(
    Guid AllocationId,
    Guid PaymentId,
    Guid InvoiceId,
    Guid BankTransactionId,
    DateTime PaymentDate,
    decimal AllocatedAmount);

/// <summary>
/// Validates a real, posted and independently authorised AP cash settlement against the
/// commitment referenced by the originating invoice. An invoice payment allocation by itself
/// is not accepted as proof of cash liquidation.
/// </summary>
public static class ModifiedCashApSettlementEvidenceValidator
{
    public static async Task<ModifiedCashApSettlementEvidence?> ValidateAsync(
        ApexErpDbContext db, Guid companyId, Guid allocationId, Guid commitmentId,
        CancellationToken ct = default)
    {
        var allocation = await db.ApPaymentAllocations.SingleOrDefaultAsync(x =>
            x.Id == allocationId, ct);
        if (allocation == null || allocation.AmountApplied <= 0m)
            return null;

        var invoice = await db.ApInvoices.SingleOrDefaultAsync(x =>
            x.Id == allocation.ApInvoiceId && x.CompanyId == companyId, ct);
        var payment = await db.ApPayments.SingleOrDefaultAsync(x =>
            x.Id == allocation.ApPaymentId && x.CompanyId == companyId, ct);
        if (invoice == null || payment == null ||
            invoice.CommitmentId != commitmentId ||
            invoice.Status is not (InvoiceStatus.Paid or InvoiceStatus.Partial) ||
            payment.PaymentMethod != PaymentMethod.BankTransfer ||
            !payment.JournalEntryId.HasValue || !payment.BankAccountId.HasValue ||
            !payment.PreparedBy.HasValue || !payment.AuthorisedBy.HasValue ||
            payment.PreparedBy == payment.AuthorisedBy ||
            payment.Amount <= 0m || allocation.AmountApplied > payment.Amount ||
            !string.Equals(invoice.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
            return null;

        var bankAccount = await db.BankAccounts.SingleOrDefaultAsync(x =>
            x.Id == payment.BankAccountId.Value && x.CompanyId == companyId, ct);
        if (bankAccount == null ||
            !string.Equals(bankAccount.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
            return null;

        var journalPosted = await db.JournalEntries.AnyAsync(x =>
            x.Id == payment.JournalEntryId.Value && x.CompanyId == companyId &&
            x.Status == JournalStatus.Posted, ct);
        if (!journalPosted)
            return null;

        var bankTransactions = await db.BankTransactions.Where(x =>
            x.CompanyId == companyId && x.BankAccountId == bankAccount.Id &&
            x.JournalEntryId == payment.JournalEntryId.Value && x.IsReconciled &&
            x.Debit == payment.Amount && x.Credit == 0m).ToListAsync(ct);
        if (bankTransactions.Count != 1)
            return null;

        var bankJournalCredit = await db.JournalLines.Where(x =>
            x.JournalEntryId == payment.JournalEntryId.Value &&
            x.GlAccountId == bankAccount.GlAccountId)
            .SumAsync(x => x.BaseCredit - x.BaseDebit, ct);
        if (bankJournalCredit != payment.Amount)
            return null;

        var company = await db.Companies.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == companyId, ct);
        if (company == null ||
            !string.Equals(company.FunctionalCurrency, payment.Currency, StringComparison.OrdinalIgnoreCase))
            return null;

        return new ModifiedCashApSettlementEvidence(
            allocation.Id, payment.Id, invoice.Id, bankTransactions[0].Id,
            payment.PaymentDate, allocation.AmountApplied);
    }
}
