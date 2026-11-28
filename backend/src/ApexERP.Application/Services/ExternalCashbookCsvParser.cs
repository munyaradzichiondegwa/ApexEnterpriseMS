using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Application.Services;

public sealed record ParsedExternalCashbookRow(
    string ExternalTransactionId,
    DateTime TransactionDate,
    DateTime? PostingDate,
    string SourceDocumentReference,
    string Description,
    decimal Debit,
    decimal Credit,
    string Currency,
    string ExternalAccountCode,
    bool IsReversal,
    string? OriginalTransactionId,
    int SourceLineNumber,
    string TransactionFingerprint);

public sealed record ParsedExternalCashbook(
    IReadOnlyList<ParsedExternalCashbookRow> Transactions,
    decimal TotalDebits,
    decimal TotalCredits);

/// <summary>
/// Strict parser for the provider-neutral IPSAS 24 external cashbook CSV contract.
/// It validates source control totals and stable transaction identity only. It does not
/// authenticate the source owner, map transactions to budget actuals or certify completeness.
/// </summary>
public static class ExternalCashbookCsvParser
{
    public const string ParserVersion = "apex-ipsas24-cashbook-v1";
    private static readonly string[] RequiredHeaders =
    [
        "ExternalTransactionId", "TransactionDate", "PostingDate", "SourceDocumentReference",
        "Description", "Debit", "Credit", "Currency", "ExternalAccountCode",
        "ReversalIndicator", "OriginalTransactionId"
    ];

    public static ParsedExternalCashbook Parse(
        ReadOnlySpan<byte> originalBytes,
        string expectedCurrency,
        DateTime periodStart,
        DateTime periodEnd,
        decimal declaredDebits,
        decimal declaredCredits,
        decimal openingBalance,
        decimal closingBalance,
        int declaredTransactionCount)
    {
        if (originalBytes.IsEmpty || originalBytes.Length > 20 * 1024 * 1024)
            throw new InvalidOperationException("Cashbook CSV must be between 1 byte and 20 MB.");
        if (periodStart.Kind != DateTimeKind.Utc || periodEnd.Kind != DateTimeKind.Utc ||
            periodStart >= periodEnd)
            throw new InvalidOperationException("Cashbook period boundaries must be valid UTC timestamps.");
        if (string.IsNullOrWhiteSpace(expectedCurrency) || expectedCurrency.Trim().Length != 3)
            throw new InvalidOperationException("The registered source account must have a three-letter currency.");
        if (declaredTransactionCount < 0 || declaredDebits < 0m || declaredCredits < 0m)
            throw new InvalidOperationException("Cashbook count and debit/credit control totals cannot be negative.");

        string csv;
        try { csv = new UTF8Encoding(false, true).GetString(originalBytes); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("Cashbook CSV must be valid UTF-8."); }
        if (csv.Length > 0 && csv[0] == '\uFEFF') csv = csv[1..];

        var rows = ParseRecords(csv);
        if (rows.Count < 1 || rows[0].Count != RequiredHeaders.Length ||
            !rows[0].Select(x => x.Trim()).SequenceEqual(RequiredHeaders, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "CSV header must be exactly: " + string.Join(",", RequiredHeaders) + ".");

        var parsed = new List<ParsedExternalCashbookRow>(rows.Count - 1);
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        decimal debits = 0m, credits = 0m;
        var lineNumber = 1;
        foreach (var row in rows.Skip(1))
        {
            lineNumber++;
            if (row.Count != RequiredHeaders.Length)
                throw new InvalidOperationException($"CSV line {lineNumber} must contain exactly {RequiredHeaders.Length} fields.");

            var id = row[0].Trim();
            var document = row[3].Trim();
            var description = row[4].Trim();
            var currency = row[7].Trim().ToUpperInvariant();
            var accountCode = row[8].Trim();
            var reversalText = row[9].Trim();
            var originalId = row[10].Trim();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 ||
                string.IsNullOrWhiteSpace(document) || document.Length > 160 ||
                string.IsNullOrWhiteSpace(description) || description.Length > 500 ||
                string.IsNullOrWhiteSpace(accountCode) || accountCode.Length > 100)
                throw new InvalidOperationException($"CSV line {lineNumber} has a missing or oversized source identity, document reference, description or account code.");
            if (!externalIds.Add(id))
                throw new InvalidOperationException($"CSV line {lineNumber} repeats external transaction identifier '{id}'.");

            if (!DateTimeOffset.TryParse(row[1].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var transactionDate))
                throw new InvalidOperationException($"CSV line {lineNumber} has an invalid transaction date.");
            var date = transactionDate.UtcDateTime;
            if (date < periodStart || date >= periodEnd)
                throw new InvalidOperationException($"CSV line {lineNumber} falls outside the declared cashbook period.");

            DateTime? postingDate = null;
            if (!string.IsNullOrWhiteSpace(row[2]))
            {
                if (!DateTimeOffset.TryParse(row[2].Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedPostingDate))
                    throw new InvalidOperationException($"CSV line {lineNumber} has an invalid posting date.");
                postingDate = parsedPostingDate.UtcDateTime;
            }

            if (!decimal.TryParse(row[5].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var debit) ||
                !decimal.TryParse(row[6].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var credit) ||
                debit < 0m || credit < 0m || (debit > 0m && credit > 0m) || (debit == 0m && credit == 0m))
                throw new InvalidOperationException($"CSV line {lineNumber} must have exactly one positive debit or credit amount.");
            if (!string.Equals(currency, expectedCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"CSV line {lineNumber} currency does not match the registered source account.");

            if (!bool.TryParse(reversalText, out var isReversal) ||
                (isReversal && (string.IsNullOrWhiteSpace(originalId) || originalId.Length > 160)) ||
                (!isReversal && !string.IsNullOrWhiteSpace(originalId)))
                throw new InvalidOperationException($"CSV line {lineNumber} has an invalid reversal indicator or original transaction reference.");

            var fingerprintInput = string.Join("|", id, date.ToString("O", CultureInfo.InvariantCulture),
                postingDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                document, debit.ToString("0.000000", CultureInfo.InvariantCulture),
                credit.ToString("0.000000", CultureInfo.InvariantCulture), currency, accountCode,
                isReversal, originalId);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
            if (!fingerprints.Add(fingerprint))
                throw new InvalidOperationException($"CSV line {lineNumber} duplicates a transaction fingerprint within this cashbook.");

            parsed.Add(new ParsedExternalCashbookRow(id, date, postingDate, document, description,
                debit, credit, currency, accountCode, isReversal,
                string.IsNullOrWhiteSpace(originalId) ? null : originalId,
                lineNumber, fingerprint));
            debits += debit;
            credits += credit;
        }

        if (parsed.Count != declaredTransactionCount ||
            debits != declaredDebits || credits != declaredCredits)
            throw new InvalidOperationException("Parsed transaction count and debit/credit totals do not match the declared cashbook control totals.");
        if (openingBalance + credits - debits != closingBalance)
            throw new InvalidOperationException("Opening balance plus credits less debits does not reconcile to the declared closing balance.");

        foreach (var reversal in parsed.Where(x => x.IsReversal))
        {
            if (string.Equals(reversal.ExternalTransactionId, reversal.OriginalTransactionId, StringComparison.Ordinal))
                throw new InvalidOperationException($"CSV line {reversal.SourceLineNumber} cannot reverse itself.");
        }

        return new ParsedExternalCashbook(parsed, debits, credits);
    }

    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quoteClosed = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; quoteClosed = true; }
                }
                else field.Append(ch);
                continue;
            }
            if (quoteClosed)
            {
                if (ch != ',' && ch != '\r' && ch != '\n')
                {
                    if (char.IsWhiteSpace(ch)) continue;
                    throw new InvalidOperationException("Malformed CSV quoting after a closing quote.");
                }
                quoteClosed = false;
            }
            if (ch == '"')
            {
                if (fieldStarted || field.Length != 0)
                    throw new InvalidOperationException("Malformed CSV quoting: quote must begin a field.");
                inQuotes = true;
                fieldStarted = true;
            }
            else if (ch == ',')
            {
                record.Add(field.ToString()); field.Clear(); fieldStarted = false;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear(); fieldStarted = false;
                if (record.Any(x => x.Length != 0)) records.Add(record);
                record = new List<string>();
            }
            else
            {
                if (fieldStarted && field.Length == 0 && !char.IsWhiteSpace(ch))
                    throw new InvalidOperationException("Malformed CSV quoting after an opening quote.");
                field.Append(ch);
                if (!char.IsWhiteSpace(ch)) fieldStarted = true;
            }
        }
        if (inQuotes) throw new InvalidOperationException("Malformed CSV: unterminated quoted field.");
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            if (record.Any(x => x.Length != 0)) records.Add(record);
        }
        return records;
    }
}
