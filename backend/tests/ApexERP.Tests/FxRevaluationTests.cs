using ApexERP.Application.Services;
using System.Globalization;
using Xunit;

namespace ApexERP.Tests;

public sealed class FxRevaluationTests
{
    [Theory]
    [InlineData("100", "100", "1.1", "10")]
    [InlineData("-100", "-100", "1.1", "-10")]
    [InlineData("100", "110", "1.1", "0")]
    [InlineData("0", "0", "1.1", "0")]
    [InlineData("100", "100", "0.9", "-10")]
    public void Monetary_item_revaluation_uses_closing_rate(
        string foreignBalance, string carryingAmount, string closingRate, string expected)
    {
        static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
        Assert.Equal(D(expected), FxRevaluationService.CalculateAdjustment(
            D(foreignBalance), D(carryingAmount), D(closingRate)));
    }

    [Fact]
    public void Revaluation_rejects_non_positive_closing_rates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FxRevaluationService.CalculateAdjustment(100m, 100m, 0m));
    }
}
