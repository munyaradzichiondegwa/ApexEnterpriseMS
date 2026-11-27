using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services;

public sealed record FxRevaluationLine(
    Guid GlAccountId, string AccountCode, string AccountName, string Currency,
    decimal ForeignCurrencyBalance, decimal CurrentFunctionalBalance, decimal ClosingRate,
    decimal RevaluedFunctionalBalance, decimal Adjustment);

public sealed record FxRevaluationPreview(
    Guid CompanyId, string FunctionalCurrency, DateTime AsOfDate,
    IReadOnlyList<FxRevaluationLine> Lines, IReadOnlyList<string> MissingRates,
    decimal NetAdjustment);

/// <summary>
/// IAS 21 monetary-item revaluation worksheet and controlled GL posting.
/// Revaluation is based on the outstanding foreign-currency monetary balance and its
/// existing functional-currency carrying amount; it does not retranslate non-monetary
/// historical-cost items or rewrite posted source transactions.
/// </summary>
public sealed class FxRevaluationService
{
    private readonly ApexErpDbContext _db;
    private readonly GlPostingService _gl;

    public FxRevaluationService(ApexErpDbContext db, GlPostingService gl)
    {
        _db = db;
        _gl = gl;
    }

    public async Task<FxRevaluationPreview> PreviewAsync(Guid companyId, DateTime asOfDate, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new InvalidOperationException("Company not found.");
        var functionalCurrency = company.FunctionalCurrency.Trim().ToUpperInvariant();
        var asOfExclusive = ReportingDates.EndExclusive(asOfDate);

        var balances = await _db.JournalLines
            .Where(l => l.JournalEntry!.CompanyId == companyId &&
                (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed) &&
                l.JournalEntry.EntryDate < asOfExclusive &&
                l.Currency != functionalCurrency)
            .GroupBy(l => new { l.GlAccountId, l.Currency })
            .Select(g => new
            {
                g.Key.GlAccountId,
                Currency = g.Key.Currency,
                ForeignBalance = g.Sum(x => x.Debit - x.Credit),
                FunctionalBalance = g.Sum(x => x.BaseDebit - x.BaseCredit)
            })
            .Where(x => x.ForeignBalance != 0m)
            .ToListAsync(ct);

        var accountIds = balances.Select(x => x.GlAccountId).Distinct().ToList();
        var accounts = await _db.GlAccounts.Where(a => a.CompanyId == companyId && accountIds.Contains(a.Id) &&
            (a.Type == GlAccountType.Asset || a.Type == GlAccountType.Liability) && a.IsMonetary).ToDictionaryAsync(a => a.Id, ct);

        var currencies = balances.Select(x => x.Currency.ToUpper()).Distinct().ToList();
        var directRates = await _db.ExchangeRates
            .Where(r => r.CompanyId == companyId && currencies.Contains(r.FromCurrency.ToUpper()) &&
                r.ToCurrency.ToUpper() == functionalCurrency && r.RateDate < asOfExclusive && r.Rate > 0)
            .OrderByDescending(r => r.RateDate)
            .ToListAsync(ct);
        var inverseRates = await _db.ExchangeRates
            .Where(r => r.CompanyId == companyId && currencies.Contains(r.ToCurrency.ToUpper()) &&
                r.FromCurrency.ToUpper() == functionalCurrency && r.RateDate < asOfExclusive && r.Rate > 0)
            .OrderByDescending(r => r.RateDate)
            .ToListAsync(ct);

        var lines = new List<FxRevaluationLine>();
        var missing = new List<string>();
        foreach (var balance in balances)
        {
            if (!accounts.TryGetValue(balance.GlAccountId, out var account))
                continue; // Non-monetary / non-balance-sheet accounts are not revalued here.
            if (!string.Equals(account.Currency, balance.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Monetary account {account.Code} is configured for {account.Currency}, but its ledger contains {balance.Currency} balances.");

            var rate = directRates.FirstOrDefault(r => string.Equals(r.FromCurrency, balance.Currency, StringComparison.OrdinalIgnoreCase));
            var inverse = false;
            if (rate == null)
            {
                rate = inverseRates.FirstOrDefault(r => string.Equals(r.ToCurrency, balance.Currency, StringComparison.OrdinalIgnoreCase));
                inverse = rate != null;
            }
            if (rate == null)
            {
                missing.Add(balance.Currency.ToUpperInvariant());
                continue;
            }

            var closingRate = inverse ? 1m / rate.Rate : rate.Rate;
            var revalued = decimal.Round(balance.ForeignBalance * closingRate, 2, MidpointRounding.AwayFromZero);
            var adjustment = decimal.Round(revalued - balance.FunctionalBalance, 2, MidpointRounding.AwayFromZero);
            if (adjustment == 0m) continue;

            lines.Add(new FxRevaluationLine(account.Id, account.Code, account.Name, balance.Currency.ToUpperInvariant(),
                balance.ForeignBalance, balance.FunctionalBalance, closingRate, revalued, adjustment));
        }

        return new FxRevaluationPreview(companyId, functionalCurrency, asOfDate.Date,
            lines.OrderBy(x => x.AccountCode).ThenBy(x => x.Currency).ToList(),
            missing.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
            lines.Sum(x => x.Adjustment));
    }

    public Task<JournalEntry> PostAsync(Guid companyId, DateTime asOfDate, Guid fxGainAccountId,
        Guid fxLossAccountId, Guid postedBy, CancellationToken ct = default)
    {
        return _db.InTransactionAsync(async token =>
        {
            await _db.AcquireTransactionLockAsync($"fx-revaluation:{companyId:N}:{asOfDate:yyyyMMdd}", token);
            var preview = await PreviewAsync(companyId, asOfDate, token);
            if (preview.MissingRates.Count > 0)
                throw new InvalidOperationException($"Missing closing exchange rates for: {string.Join(", ", preview.MissingRates)}.");
            if (preview.Lines.Count == 0)
                throw new InvalidOperationException("There are no foreign-currency monetary balances requiring revaluation.");

            var gainAccount = await _db.GlAccounts.FirstOrDefaultAsync(a => a.Id == fxGainAccountId &&
                a.CompanyId == companyId && a.IsActive && a.IsPostable, token)
                ?? throw new InvalidOperationException("The foreign-exchange gain account is not an active postable account in this company.");
            var lossAccount = await _db.GlAccounts.FirstOrDefaultAsync(a => a.Id == fxLossAccountId &&
                a.CompanyId == companyId && a.IsActive && a.IsPostable, token)
                ?? throw new InvalidOperationException("The foreign-exchange loss account is not an active postable account in this company.");
            if (gainAccount.Type != GlAccountType.Income || lossAccount.Type != GlAccountType.Expense)
                throw new InvalidOperationException("Configure an Income account for FX gains and an Expense account for FX losses.");
            if (gainAccount.Id == lossAccount.Id)
                throw new InvalidOperationException("FX gain and loss accounts must be different.");

            var reference = $"FX-REVAL-{asOfDate:yyyyMMdd}";
            if (await _db.JournalEntries.AnyAsync(j => j.CompanyId == companyId && j.Reference == reference &&
                (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed), token))
                throw new InvalidOperationException($"An FX revaluation journal already exists for {asOfDate:yyyy-MM-dd}.");

            var journalLines = new List<GlLineRequest>();
            decimal totalGains = 0m, totalLosses = 0m;
            foreach (var line in preview.Lines)
            {
                var amount = Math.Abs(line.Adjustment);
                if (line.Adjustment > 0)
                {
                    journalLines.Add(new GlLineRequest(line.GlAccountId, amount, 0m, preview.FunctionalCurrency, 1m,
                        Narrative: $"IAS 21 closing-rate revaluation {line.Currency}"));
                    totalGains += amount;
                }
                else
                {
                    journalLines.Add(new GlLineRequest(line.GlAccountId, 0m, amount, preview.FunctionalCurrency, 1m,
                        Narrative: $"IAS 21 closing-rate revaluation {line.Currency}"));
                    totalLosses += amount;
                }
            }

            if (totalGains > 0)
                journalLines.Add(new GlLineRequest(gainAccount.Id, 0m, totalGains, preview.FunctionalCurrency, 1m,
                    Narrative: "Unrealised foreign-exchange gain"));
            if (totalLosses > 0)
                journalLines.Add(new GlLineRequest(lossAccount.Id, totalLosses, 0m, preview.FunctionalCurrency, 1m,
                    Narrative: "Unrealised foreign-exchange loss"));

            return await _gl.PostAsync(companyId, asOfDate.Date, JournalSource.FxRevaluation,
                "IAS 21 foreign-currency monetary-item revaluation", reference, journalLines, token, postedBy);
        }, ct);
    }

    public static decimal CalculateAdjustment(decimal foreignCurrencyBalance, decimal currentFunctionalBalance, decimal closingRate)
    {
        if (closingRate <= 0m) throw new ArgumentOutOfRangeException(nameof(closingRate), "Closing exchange rate must be positive.");
        var revalued = decimal.Round(foreignCurrencyBalance * closingRate, 2, MidpointRounding.AwayFromZero);
        return decimal.Round(revalued - currentFunctionalBalance, 2, MidpointRounding.AwayFromZero);
    }
}
