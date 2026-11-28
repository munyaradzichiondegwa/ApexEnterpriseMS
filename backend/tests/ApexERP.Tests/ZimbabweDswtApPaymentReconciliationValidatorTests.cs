using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class ZimbabweDswtApPaymentReconciliationValidatorTests
{
    private static ZimbabweDswtApPaymentEvidence Valid(
        decimal amount = 100m,
        string currency = "USD",
        string supplier = "Foreign Cloud Ltd",
        bool hasBank = true,
        string? bankCurrency = "USD",
        bool posted = true) =>
        new(amount, currency, supplier, hasBank, bankCurrency, posted);

    [Fact]
    public void Accepts_gross_ap_payment_and_does_not_compare_to_supplier_net_remittance()
    {
        ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
            100m, "USD", "Foreign Cloud Ltd", Valid());
    }

    [Fact]
    public void Rejects_net_supplier_remittance_as_the_ap_settlement_amount()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
                100m, "USD", "Foreign Cloud Ltd", Valid(amount: 84.50m)));
        Assert.Contains("gross payment", error.Message);
    }

    [Fact]
    public void Rejects_currency_mismatch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
                100m, "USD", "Foreign Cloud Ltd", Valid(currency: "ZWL")));
    }

    [Fact]
    public void Rejects_supplier_mismatch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
                100m, "USD", "Foreign Cloud Ltd", Valid(supplier: "Different Supplier")));
    }

    [Fact]
    public void Requires_originating_bank_account()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
                100m, "USD", "Foreign Cloud Ltd", Valid(hasBank: false)));
    }

    [Fact]
    public void Requires_bank_account_currency_to_match_payment()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
                100m, "USD", "Foreign Cloud Ltd", Valid(bankCurrency: "ZWL")));
    }

    [Fact]
    public void Requires_posted_gl_journal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ZimbabweDswtApPaymentReconciliationValidator.EnsureValid(
                100m, "USD", "Foreign Cloud Ltd", Valid(posted: false)));
    }
}
