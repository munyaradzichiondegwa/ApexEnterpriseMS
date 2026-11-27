using ApexERP.Domain.Entities;

namespace ApexERP.Application.Services;

/// <summary>
/// Selects the most recent effective direct or inverse exchange rate. At the same effective
/// date a manual override wins; a direct quote wins only when source and date are otherwise tied.
/// </summary>
public sealed record SelectedExchangeRate(ExchangeRate Rate, bool IsInverse);

public static class ExchangeRateResolver
{
    public static SelectedExchangeRate? SelectEffectiveRate(
        IEnumerable<ExchangeRate> directRates,
        IEnumerable<ExchangeRate> inverseRates,
        DateTime effectiveOn)
    {
        return directRates.Select(rate => new SelectedExchangeRate(rate, false))
            .Concat(inverseRates.Select(rate => new SelectedExchangeRate(rate, true)))
            .Where(candidate => candidate.Rate.Rate > 0m &&
                candidate.Rate.RateDate.Date <= effectiveOn.Date)
            .OrderByDescending(candidate => candidate.Rate.RateDate)
            .ThenByDescending(candidate => candidate.Rate.IsManualOverride)
            .ThenBy(candidate => candidate.IsInverse)
            .ThenBy(candidate => candidate.Rate.Id)
            .FirstOrDefault();
    }
}
