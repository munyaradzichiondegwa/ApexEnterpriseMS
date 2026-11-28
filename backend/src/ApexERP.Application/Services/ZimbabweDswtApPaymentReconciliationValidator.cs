namespace ApexERP.Application.Services;

public sealed record ZimbabweDswtApPaymentEvidence(
    decimal PaymentAmount,
    string PaymentCurrency,
    string SupplierName,
    bool HasBankAccount,
    string? BankAccountCurrency,
    bool HasPostedJournal);

/// <summary>
/// Pure control rules for linking a DSWT intermediary deduction to an AP settlement.
/// The AP payment represents the gross cash debit; the supplier's net remittance is not
/// substituted for that gross amount and no duplicate tax payable is generated.
/// </summary>
public static class ZimbabweDswtApPaymentReconciliationValidator
{
    public static void EnsureValid(
        decimal grossPaymentAmount,
        string currency,
        string supplierName,
        ZimbabweDswtApPaymentEvidence evidence)
    {
        if (grossPaymentAmount <= 0m)
            throw new InvalidOperationException("The DSWT gross payment amount must be positive.");
        if (evidence.PaymentAmount != grossPaymentAmount)
            throw new InvalidOperationException("The AP payment must equal the gross payment before intermediary withholding, not the supplier's net remittance.");
        if (!string.Equals(evidence.PaymentCurrency, currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AP payment currency does not match the DSWT payment currency.");
        if (!string.Equals(evidence.SupplierName?.Trim(), supplierName?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AP payment supplier does not match the DSWT supplier.");
        if (!evidence.HasBankAccount)
            throw new InvalidOperationException("The AP payment must identify its originating bank account before DSWT reconciliation.");
        if (!string.Equals(evidence.BankAccountCurrency, evidence.PaymentCurrency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AP payment and originating bank account currencies do not match.");
        if (!evidence.HasPostedJournal)
            throw new InvalidOperationException("The AP payment must have a posted GL journal before DSWT reconciliation.");
    }
}
