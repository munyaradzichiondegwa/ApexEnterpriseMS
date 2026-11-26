using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Domain.Controls
{
    /// <summary>
    /// Deterministic fingerprint of a posting request (GL-007). The same company, source, reference, date and
    /// lines always hash to the same value, regardless of line order, so a repeated request can be recognised
    /// and answered with the original journal instead of creating a duplicate.
    /// </summary>
    public static class PostingFingerprint
    {
        public static string Compute(Guid companyId, string source, string? reference, DateTime entryDate,
            IEnumerable<(Guid AccountId, decimal Debit, decimal Credit, string Currency)> lines)
        {
            var parts = lines
                .Select(l => $"{l.AccountId:N}|{l.Debit:0.000000}|{l.Credit:0.000000}|{l.Currency.ToUpperInvariant()}")
                .OrderBy(s => s, StringComparer.Ordinal);
            var canonical = $"{companyId:N}|{source}|{reference?.Trim()}|{entryDate:yyyy-MM-dd}|{string.Join(";", parts)}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }
}
