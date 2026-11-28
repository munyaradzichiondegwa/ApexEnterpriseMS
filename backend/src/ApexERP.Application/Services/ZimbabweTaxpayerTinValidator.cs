namespace ApexERP.Application.Services;

/// <summary>Conservative input validation for the ZIMRA taxpayer identifier stored on a legal entity.</summary>
public static class ZimbabweTaxpayerTinValidator
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var tin = value.Trim();
        return tin.Length <= 40 &&
            tin.Any(char.IsAsciiLetterOrDigit) &&
            tin.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '/');
    }
}
