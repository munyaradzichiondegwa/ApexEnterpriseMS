using ApexERP.Domain.Entities;

namespace ApexERP.Application.Services;

/// <summary>
/// Zimbabwe DSWT decision model based on VAT Act [Chapter 23:12] section 13A as
/// substituted by Finance Act No. 7 of 2025 and ZIMRA Public Notice 05 of 2026.
/// This calculates withholding at a Zimbabwe intermediary; it does not create a
/// separate customer surcharge or replace the supplier's VAT return.
/// </summary>
public sealed record ZimbabweDigitalServicesTaxRequest(
    decimal GrossPaymentAmount,
    ZimbabweDigitalServiceKind ServiceKind,
    ZimbabweSupplierVatStatus SupplierVatStatus,
    ZimbabweDigitalPaymentRoute PaymentRoute,
    bool ConsumedInZimbabwe,
    bool ZeroRatedOrExempt,
    bool RecipientAlreadySelfAccountedVat);

public sealed record ZimbabweDigitalServicesTaxResult(
    bool WithholdingRequired,
    decimal TaxablePaymentAmount,
    decimal WithholdingRate,
    decimal WithholdingAmount,
    decimal SupplierRemittanceAmount,
    string TaxTreatment,
    string Reason,
    string LegalReference);

public static class ZimbabweDigitalServicesTaxEvaluator
{
    public const decimal StandardRate = 0.155m;
    public const decimal RegisteredSupplierTaxFraction = 3m / 23m;
    public const string LegalReference =
        "VAT Act [Chapter 23:12], section 13A and Second Schedule; Finance Act No. 7 of 2025; ZIMRA Public Notice 05 of 2026.";

    public static ZimbabweDigitalServicesTaxResult Evaluate(ZimbabweDigitalServicesTaxRequest request)
    {
        if (request.GrossPaymentAmount < 0m)
            throw new ArgumentOutOfRangeException(nameof(request.GrossPaymentAmount), "Payment amount cannot be negative.");

        if (request.GrossPaymentAmount == 0m)
            return Excluded(request, "Zero-value payment; no withholding arises.");

        if (request.ServiceKind != ZimbabweDigitalServiceKind.ElectronicService)
            return Excluded(request, request.ServiceKind switch
            {
                ZimbabweDigitalServiceKind.PhysicalGoods => "Physical goods are outside DSWT; apply import VAT and customs rules instead.",
                ZimbabweDigitalServiceKind.NonElectronicImportedService => "Non-electronic imported services fall under the separate imported-services VAT self-accounting rules.",
                ZimbabweDigitalServiceKind.OtherNonResidentPayment => "Royalties, dividends, management fees and professional fees may trigger separate non-resident income-tax withholding; assess those rules independently of DSWT.",
                ZimbabweDigitalServiceKind.LocalService => "Payments to local suppliers do not attract DSWT.",
                _ => "The supply is outside the electronic imported-services scope."
            });

        if (!request.ConsumedInZimbabwe)
            return Excluded(request, "The electronic service is not consumed or utilised in Zimbabwe.");

        if (request.ZeroRatedOrExempt)
            return Excluded(request, "Zero-rated or exempt services retain their ordinary VAT treatment.");

        if (request.RecipientAlreadySelfAccountedVat)
            return Excluded(request, "The Zimbabwe recipient has already self-accounted and remitted VAT on this same supply; avoid duplicate collection.");

        if (request.PaymentRoute == ZimbabweDigitalPaymentRoute.LocalPayment)
            return Excluded(request, "A local payment route does not constitute a remittance through a Zimbabwe intermediary to a non-resident supplier.");

        if (request.PaymentRoute == ZimbabweDigitalPaymentRoute.PaidOutsideZimbabwe)
            return Excluded(request, "Where the service is consumed in Zimbabwe but payment is made outside Zimbabwe, the non-resident supplier accounts for VAT directly to ZIMRA where registered.");

        var rate = request.SupplierVatStatus == ZimbabweSupplierVatStatus.Registered
            ? RegisteredSupplierTaxFraction
            : StandardRate;
        var amount = decimal.Round(request.GrossPaymentAmount * rate, 2, MidpointRounding.AwayFromZero);
        var remittance = request.GrossPaymentAmount - amount;

        return new ZimbabweDigitalServicesTaxResult(
            true,
            request.GrossPaymentAmount,
            rate,
            amount,
            remittance,
            request.SupplierVatStatus == ZimbabweSupplierVatStatus.Registered
                ? "Withhold tax fraction 3/23"
                : "Withhold 15.5%",
            "A qualifying electronic service supplied by a non-resident is consumed in Zimbabwe and paid through a Zimbabwe intermediary. The intermediary withholds from the amount being remitted and issues the prescribed certificate.",
            LegalReference);
    }

    private static ZimbabweDigitalServicesTaxResult Excluded(
        ZimbabweDigitalServicesTaxRequest request, string reason) =>
        new(false, 0m, 0m, 0m, request.GrossPaymentAmount,
            "No DSWT withholding", reason, LegalReference);
}
