using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using System.Globalization;
using Xunit;

namespace ApexERP.Tests;

public sealed class FxRevaluationTests
{
    [Theory]
    [InlineData("100", "100", "1.1", "10")]
    [InlineData("1000", "1200", "1.1", "-100")]
    [InlineData("-1000", "-1000", "1.1", "-100")]
    [InlineData("-1000", "-1200", "1.1", "100")]
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
    [Fact]
    public void Effective_rate_selection_uses_newest_quote_even_when_it_is_inverse()
    {
        var direct = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "EUR", ToCurrency = "USD",
            RateDate = new DateTime(2026, 1, 1), Rate = 1.10m
        };
        var inverse = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "USD", ToCurrency = "EUR",
            RateDate = new DateTime(2026, 1, 2), Rate = 0.80m
        };

        var selected = ExchangeRateResolver.SelectEffectiveRate(
            new[] { direct }, new[] { inverse }, new DateTime(2026, 1, 2));

        Assert.NotNull(selected);
        Assert.True(selected.IsInverse);
        Assert.Equal(inverse.Id, selected.Rate.Id);
    }

    [Fact]
    public void Same_day_manual_override_takes_precedence_over_system_rate_regardless_of_direction()
    {
        var systemDirect = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "EUR", ToCurrency = "USD",
            RateDate = new DateTime(2026, 1, 2), Rate = 1.10m
        };
        var manualInverse = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "USD", ToCurrency = "EUR",
            RateDate = new DateTime(2026, 1, 2), Rate = 0.82m, IsManualOverride = true
        };

        var selected = ExchangeRateResolver.SelectEffectiveRate(
            new[] { systemDirect }, new[] { manualInverse }, new DateTime(2026, 1, 2));

        Assert.NotNull(selected);
        Assert.True(selected.IsInverse);
        Assert.True(selected.Rate.IsManualOverride);
    }

    [Fact]
    public void Same_day_manual_direct_quote_wins_a_full_tie_and_future_rates_are_ignored()
    {
        var manualDirect = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "EUR", ToCurrency = "USD",
            RateDate = new DateTime(2026, 1, 2), Rate = 1.20m, IsManualOverride = true
        };
        var manualInverse = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "USD", ToCurrency = "EUR",
            RateDate = new DateTime(2026, 1, 2), Rate = 0.83m, IsManualOverride = true
        };
        var future = new ExchangeRate
        {
            Id = Guid.NewGuid(), FromCurrency = "EUR", ToCurrency = "USD",
            RateDate = new DateTime(2026, 1, 3), Rate = 1.30m, IsManualOverride = true
        };

        var selected = ExchangeRateResolver.SelectEffectiveRate(
            new[] { manualDirect, future }, new[] { manualInverse }, new DateTime(2026, 1, 2));

        Assert.NotNull(selected);
        Assert.False(selected.IsInverse);
        Assert.Equal(manualDirect.Id, selected.Rate.Id);
    }

}
