using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using Xunit;

namespace ApexERP.Tests;

public sealed class ZimbabweDigitalServicesTaxEvaluatorTests
{
    private static ZimbabweDigitalServicesTaxRequest Eligible(
        decimal amount = 100m,
        ZimbabweSupplierVatStatus status = ZimbabweSupplierVatStatus.NotRegistered) =>
        new(amount, ZimbabweDigitalServiceKind.ElectronicService, status,
            ZimbabweDigitalPaymentRoute.ZimbabweIntermediary, true, false, false);

    [Fact]
    public void Unregistered_nonresident_supplier_withholds_15_5_percent_from_remittance()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(Eligible());

        Assert.True(result.WithholdingRequired);
        Assert.Equal(15.50m, result.WithholdingAmount);
        Assert.Equal(84.50m, result.SupplierRemittanceAmount);
        Assert.Equal(0.155m, result.WithholdingRate);
    }

    [Fact]
    public void Vat_registered_supplier_uses_three_over_twenty_three_fraction()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(
            Eligible(status: ZimbabweSupplierVatStatus.Registered));

        Assert.True(result.WithholdingRequired);
        Assert.Equal(3m / 23m, result.WithholdingRate);
        Assert.Equal(13.04m, result.WithholdingAmount);
        Assert.Equal(86.96m, result.SupplierRemittanceAmount);
    }

    [Theory]
    [InlineData(ZimbabweDigitalServiceKind.PhysicalGoods)]
    [InlineData(ZimbabweDigitalServiceKind.NonElectronicImportedService)]
    [InlineData(ZimbabweDigitalServiceKind.LocalService)]
    public void Goods_non_electronic_and_local_services_are_excluded(ZimbabweDigitalServiceKind kind)
    {
        var request = Eligible() with { ServiceKind = kind };
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(request);
        Assert.False(result.WithholdingRequired);
        Assert.Equal(0m, result.WithholdingAmount);
    }

    [Fact]
    public void Other_nonresident_payment_categories_are_not_misclassified_as_dswt()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(
            Eligible() with { ServiceKind = ZimbabweDigitalServiceKind.OtherNonResidentPayment });
        Assert.False(result.WithholdingRequired);
        Assert.Contains("separate non-resident income-tax withholding", result.Reason);
    }

    [Fact]
    public void Excludes_zero_rated_or_exempt_services()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(Eligible() with { ZeroRatedOrExempt = true });
        Assert.False(result.WithholdingRequired);
    }

    [Fact]
    public void Excludes_services_consumed_outside_zimbabwe()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(Eligible() with { ConsumedInZimbabwe = false });
        Assert.False(result.WithholdingRequired);
    }

    [Fact]
    public void Excludes_when_recipient_has_already_self_accounted_to_prevent_double_collection()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(Eligible() with { RecipientAlreadySelfAccountedVat = true });
        Assert.False(result.WithholdingRequired);
    }

    [Fact]
    public void Outside_zimbabwe_payment_route_requires_supplier_direct_accounting_not_intermediary_withholding()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(
            Eligible() with { PaymentRoute = ZimbabweDigitalPaymentRoute.PaidOutsideZimbabwe });
        Assert.False(result.WithholdingRequired);
        Assert.Contains("supplier accounts for VAT directly", result.Reason);
    }

    [Fact]
    public void Negative_payment_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZimbabweDigitalServicesTaxEvaluator.Evaluate(Eligible(-1m)));
    }

    [Fact]
    public void Zero_payment_does_not_create_withholding()
    {
        var result = ZimbabweDigitalServicesTaxEvaluator.Evaluate(Eligible(0m));
        Assert.False(result.WithholdingRequired);
        Assert.Equal(0m, result.WithholdingAmount);
    }
}
