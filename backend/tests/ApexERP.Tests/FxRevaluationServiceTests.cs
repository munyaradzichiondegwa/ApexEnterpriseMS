using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class FxRevaluationServiceTests
{
    [Theory]
    [InlineData("1000", "1000", "1.1", "100")]
    [InlineData("1000", "1200", "1.1", "-100")]
    [InlineData("-1000", "-1000", "1.1", "-100")]
    [InlineData("-1000", "-1200", "1.1", "100")]
    [InlineData("1000", "1100", "1.1", "0")]
    public void CalculateAdjustment_uses_signed_monetary_balance_and_closing_rate(
        string foreignBalance, string carryingBalance, string closingRate, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            FxRevaluationService.CalculateAdjustment(
                decimal.Parse(foreignBalance, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(carryingBalance, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(closingRate, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void CalculateAdjustment_rejects_non_positive_closing_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FxRevaluationService.CalculateAdjustment(100m, 100m, 0m));
    }
}
