using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class ZimbabweElectronicCommerceTaxEvaluatorTests
{
    private static ZimbabweElectronicCommerceTaxRequest Eligible(decimal revenue = 100m) =>
        new(revenue, "USD", ZimbabweDigitalOperatorKind.ElectronicCommerceOperator,
            true, true, false);

    [Fact]
    public void Foreign_ecommerce_operator_pays_five_percent_of_qualifying_gross_revenue()
    {
        var result = ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible(), new DateTime(2026, 1, 1));

        Assert.True(result.Taxable);
        Assert.Equal(100m, result.TaxableGrossRevenue);
        Assert.Equal(0.05m, result.TaxRate);
        Assert.Equal(5m, result.TaxLiability);
        Assert.Equal("USD", result.Currency);
        Assert.Contains("operator", result.StatutoryTaxpayer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a customer surcharge", result.Reason);
    }

    [Fact]
    public void Satellite_broadcasting_service_is_within_operator_scope()
    {
        var result = ZimbabweElectronicCommerceTaxEvaluator.Evaluate(
            Eligible() with { OperatorKind = ZimbabweDigitalOperatorKind.SatelliteBroadcastingService },
            new DateTime(2026, 2, 1));
        Assert.True(result.Taxable);
        Assert.Equal(5m, result.TaxLiability);
    }

    [Theory]
    [InlineData(false, true, false, ZimbabweDigitalOperatorKind.ElectronicCommerceOperator)]
    [InlineData(true, false, false, ZimbabweDigitalOperatorKind.ElectronicCommerceOperator)]
    [InlineData(true, true, true, ZimbabweDigitalOperatorKind.ElectronicCommerceOperator)]
    [InlineData(true, true, false, ZimbabweDigitalOperatorKind.Other)]
    public void Out_of_scope_revenue_is_not_taxed(
        bool foreignOperator, bool residentCustomer, bool excluded, ZimbabweDigitalOperatorKind kind)
    {
        var request = Eligible() with
        {
            OperatorDomiciledOutsideZimbabwe = foreignOperator,
            CustomerResidentInZimbabwe = residentCustomer,
            ExcludedByIncomeTaxActSection12 = excluded,
            OperatorKind = kind
        };
        var result = ZimbabweElectronicCommerceTaxEvaluator.Evaluate(request, new DateTime(2026, 1, 1));
        Assert.False(result.Taxable);
        Assert.Equal(0m, result.TaxLiability);
    }

    [Fact]
    public void No_former_five_hundred_thousand_dollar_threshold_is_applied()
    {
        var result = ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible(1m), new DateTime(2026, 1, 1));
        Assert.True(result.Taxable);
        Assert.Equal(0.05m, result.TaxLiability);
    }

    [Fact]
    public void Revenue_before_effective_date_is_not_calculated_under_2026_rules()
    {
        var result = ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible(), new DateTime(2025, 12, 31));
        Assert.False(result.Taxable);
        Assert.Contains("prior-year", result.Reason);
    }

    [Fact]
    public void Revenue_is_rounded_to_two_decimal_places_away_from_zero()
    {
        var result = ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible(1.11m), new DateTime(2026, 1, 1));
        Assert.Equal(0.06m, result.TaxLiability);
    }

    [Fact]
    public void Negative_revenue_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible(-1m), new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void Currency_must_be_a_three_letter_code()
    {
        Assert.Throws<ArgumentException>(() =>
            ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible() with { Currency = "US Dollars" },
                new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void Non_usd_revenue_is_rejected_until_an_approved_fx_conversion_exists()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ZimbabweElectronicCommerceTaxEvaluator.Evaluate(Eligible() with { Currency = "EUR" },
                new DateTime(2026, 1, 1)));
    }

    [Theory]
    [InlineData(1, 2025, 12, 1, 2026, 3, 1, 2026, 3, 25, 2026, 4, 5)]
    [InlineData(2, 2026, 3, 1, 2026, 6, 1, 2026, 6, 25, 2026, 7, 5)]
    [InlineData(3, 2026, 6, 1, 2026, 9, 1, 2026, 9, 25, 2026, 10, 5)]
    [InlineData(4, 2026, 9, 1, 2026, 12, 1, 2026, 12, 20, 2027, 1, 5)]
    public void Quarterly_period_and_payment_and_return_due_dates_are_distinct(
        int quarter, int sy, int sm, int sd, int ey, int em, int ed,
        int py, int pm, int pd, int ry, int rm, int rd)
    {
        var period = ZimbabweElectronicCommerceTaxCalendar.GetPeriod(2026, quarter);
        Assert.Equal(new DateTime(sy, sm, sd), period.PeriodStartInclusive);
        Assert.Equal(new DateTime(ey, em, ed), period.PeriodEndExclusive);
        Assert.Equal(new DateTime(py, pm, pd), period.PaymentDueDate);
        Assert.Equal(new DateTime(ry, rm, rd), period.ReturnDueDate);
    }

    [Fact]
    public void Calendar_rejects_invalid_quarters_and_pre_2026_tax_years()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ZimbabweElectronicCommerceTaxCalendar.GetPeriod(2025, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZimbabweElectronicCommerceTaxCalendar.GetPeriod(2026, 5));
    }
}
