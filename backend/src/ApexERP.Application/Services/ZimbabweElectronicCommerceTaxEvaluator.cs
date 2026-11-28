namespace ApexERP.Application.Services;

/// <summary>
/// Zimbabwe Electronic Commerce Operators' Tax (often called DST), under Income Tax Act
/// [Chapter 23:06] section 12A and Finance Act [Chapter 23:04] section 14(2)(k).
/// This is the non-resident operator's income-tax liability; it is not DSWT and is not
/// a customer surcharge or an intermediary withholding.
/// </summary>
public enum ZimbabweDigitalOperatorKind
{
    ElectronicCommerceOperator,
    SatelliteBroadcastingService,
    Other
}

public sealed record ZimbabweElectronicCommerceTaxRequest(
    decimal QualifyingGrossRevenue,
    string Currency,
    ZimbabweDigitalOperatorKind OperatorKind,
    bool OperatorDomiciledOutsideZimbabwe,
    bool CustomerResidentInZimbabwe,
    bool ExcludedByIncomeTaxActSection12);

public sealed record ZimbabweElectronicCommerceTaxResult(
    bool Taxable,
    decimal TaxableGrossRevenue,
    decimal TaxRate,
    decimal TaxLiability,
    string Currency,
    string StatutoryTaxpayer,
    string Reason,
    string LegalReference);

/// <summary>
/// Calculates only the statutory operator-level liability. It deliberately does not create
/// invoice surcharges, withholding entries, journals, returns or remittances.
/// </summary>
public static class ZimbabweElectronicCommerceTaxEvaluator
{
    public const decimal Rate = 0.05m;
    public static readonly DateTime EffectiveFrom = new(2026, 1, 1);
    public const string LegalReference =
        "Income Tax Act [Chapter 23:06], sections 12(6), 12(7) and 12A; Finance Act [Chapter 23:04], section 14(2)(k); Finance Act No. 7 of 2025, section 14.";

    public static ZimbabweElectronicCommerceTaxResult Evaluate(
        ZimbabweElectronicCommerceTaxRequest request,
        DateTime revenueDate)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.QualifyingGrossRevenue < 0m)
            throw new ArgumentOutOfRangeException(nameof(request.QualifyingGrossRevenue),
                "Gross revenue cannot be negative.");

        if (string.IsNullOrWhiteSpace(request.Currency) ||
            request.Currency.Length != 3 ||
            request.Currency.Any(c => !char.IsAsciiLetter(c)))
            throw new ArgumentException("A three-letter ISO currency code is required.", nameof(request));

        var currency = request.Currency.ToUpperInvariant();
        if (!string.Equals(currency, "USD", StringComparison.Ordinal))
            throw new InvalidOperationException("Only USD revenue is supported. Non-USD revenue requires an approved, evidenced ZIMRA-compliant conversion before it can enter the tax base.");

        if (revenueDate.Date < EffectiveFrom)
            return Excluded(request, currency, "Pre-2026 revenue requires the prior-year US$500,000 threshold assessment; it must not be treated as exempt or silently included in the 2026 no-threshold calculation.");

        if (request.QualifyingGrossRevenue == 0m)
            return Excluded(request, currency, "No gross revenue was recorded for this transaction.");

        if (!request.OperatorDomiciledOutsideZimbabwe)
            return Excluded(request, currency, "Section 12A applies to qualifying foreign-domiciled operators.");

        if (request.OperatorKind == ZimbabweDigitalOperatorKind.Other)
            return Excluded(request, currency, "The taxpayer is not identified as a satellite broadcasting service or electronic commerce operator.");

        if (!request.CustomerResidentInZimbabwe)
            return Excluded(request, currency, "The customer is not resident in Zimbabwe; the section 12 source rule is not established.");

        if (request.ExcludedByIncomeTaxActSection12)
            return Excluded(request, currency, "The revenue has been identified as falling within an exclusion under Income Tax Act section 12(1)(d) or 12(4).");

        var tax = decimal.Round(request.QualifyingGrossRevenue * Rate, 2, MidpointRounding.AwayFromZero);
        return new ZimbabweElectronicCommerceTaxResult(
            true,
            request.QualifyingGrossRevenue,
            Rate,
            tax,
            currency,
            "Foreign-domiciled electronic commerce operator or satellite broadcasting service",
            "The operator is liable on qualifying gross revenue deemed to be from a Zimbabwe source. The former annual revenue threshold was removed with effect from 1 January 2026. This calculation is not a customer surcharge or DSWT withholding.",
            LegalReference);
    }

    private static ZimbabweElectronicCommerceTaxResult Excluded(
        ZimbabweElectronicCommerceTaxRequest request, string currency, string reason) =>
        new(false, 0m, 0m, 0m, currency,
            "No operator tax liability calculated", reason, LegalReference);
}

/// <summary>
/// Section 12A quarterly revenue windows and statutory payment dates. Return due dates
/// are kept separate because the prescribed return calendar is not the same as the
/// quarterly payment dates.
/// </summary>
public static class ZimbabweElectronicCommerceTaxCalendar
{
    public static ZimbabweElectronicCommerceTaxPeriod GetPeriod(int taxYear, int quarter)
    {
        if (taxYear < 2026)
            throw new ArgumentOutOfRangeException(nameof(taxYear), "This implementation starts in tax year 2026.");
        if (quarter is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(quarter), "Quarter must be between 1 and 4.");

        var (start, end, paymentDue, returnDue) = quarter switch
        {
            1 => (new DateTime(taxYear - 1, 12, 1), new DateTime(taxYear, 3, 1),
                new DateTime(taxYear, 3, 25), new DateTime(taxYear, 4, 5)),
            2 => (new DateTime(taxYear, 3, 1), new DateTime(taxYear, 6, 1),
                new DateTime(taxYear, 6, 25), new DateTime(taxYear, 7, 5)),
            3 => (new DateTime(taxYear, 6, 1), new DateTime(taxYear, 9, 1),
                new DateTime(taxYear, 9, 25), new DateTime(taxYear, 10, 5)),
            _ => (new DateTime(taxYear, 9, 1), new DateTime(taxYear, 12, 1),
                new DateTime(taxYear, 12, 20), new DateTime(taxYear + 1, 1, 5))
        };

        return new ZimbabweElectronicCommerceTaxPeriod(taxYear, quarter, start, end, paymentDue, returnDue);
    }
}

public sealed record ZimbabweElectronicCommerceTaxPeriod(
    int TaxYear,
    int Quarter,
    DateTime PeriodStartInclusive,
    DateTime PeriodEndExclusive,
    DateTime PaymentDueDate,
    DateTime ReturnDueDate);
