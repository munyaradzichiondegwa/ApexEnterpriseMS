using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class FxRevaluationTests
{
    [Theory]
    [InlineData(100m, 100m, 1.1m, 10m)]
    [InlineData(-100m, -100m, 1.1m, -10m)]
    [InlineData(100m, 110m, 1.1m, 0m)]
    [InlineData(0m, 0m, 1.1m, 0m)]
    [InlineData(100m, 100m, 0.9m, -10m)]
    public void Monetary_item_revaluation_uses_closing_rate(
        decimal foreignBalance, decimal carryingAmount, decimal closingRate, decimal expected)
    {
        Assert.Equal(expected, FxRevaluationService.CalculateAdjustment(foreignBalance, carryingAmount, closingRate));
    }

    [Fact]
    public void Revaluation_rejects_non_positive_closing_rates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FxRevaluationService.CalculateAdjustment(100m, 100m, 0m));
    }
}
