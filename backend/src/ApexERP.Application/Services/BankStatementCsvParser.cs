using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Application.Services;

public sealed record ParsedBankStatementRow(
    string ExternalTransactionId,
    DateTime TransactionDate,
    string Description,
    string? Reference,
    decimal Debit,
    decimal Credit,
    string Currency,
    int SourceLineNumber,
    string TransactionFingerprint);

public sealed record ParsedBankStatement(
    IReadOnlyList<ParsedBankStatementRow> Transactions,
    decimal TotalDebits,
    decimal TotalCredits);

/// <summary>
/// Strict versioned CSV parser for the explicitly documented bank-statement interchange format.
/// It rejects ambiguous rows rather than inferring missing identifiers, dates, currencies or amounts.
/// </summary>
public static class BankStatementCsvParser
{
    public const string ParserVersion = "apex-bank-csv-v1";
    private static readonly string[] RequiredHeaders =
    [
        "ExternalTransactionId", "TransactionDate", "Description", "Reference",
        "Debit", "Credit", "Currency"
    ];

    public static ParsedBankStatement Parse(
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
        if (originalBytes.IsEmpty || originalBytes.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("Statement CSV must be between 1 byte and 10 MB.");
        if (periodStart.Kind != DateTimeKind.Utc || periodEnd.Kind != DateTimeKind.Utc ||
            periodStart >= periodEnd)
            throw new InvalidOperationException("Statement period boundaries must be valid UTC timestamps.");
        if (string.IsNullOrWhiteSpace(expectedCurrency) || expectedCurrency.Trim().Length != 3)
            throw new InvalidOperationException("The bank account must have a valid three-letter currency.");

        string csv;
        try { csv = new UTF8Encoding(false, true).GetString(originalBytes); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("Statement CSV must be valid UTF-8."); }
        if (csv.Length > 0 && csv[0] == '\uFEFF') csv = csv[1..];

        var rows = ParseRecords(csv);
        if (rows.Count < 1)
            throw new InvalidOperationException("Statement CSV must contain the required header; an authenticated zero-activity statement may contain no transaction rows.");
        if (rows[0].Count != RequiredHeaders.Length ||
            !rows[0].Select(x => x.Trim()).SequenceEqual(RequiredHeaders, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "CSV header must be exactly: ExternalTransactionId,TransactionDate,Description,Reference,Debit,Credit,Currency.");

        var parsed = new List<ParsedBankStatementRow>(rows.Count - 1);
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        decimal debits = 0m, credits = 0m;
        var lineNumber = 1;
        foreach (var row in rows.Skip(1))
        {
            lineNumber++;
            if (row.Count != RequiredHeaders.Length)
                throw new InvalidOperationException($"CSV line {lineNumber} must contain exactly seven fields.");
            var externalId = row[0].Trim();
            var description = row[2].Trim();
            var reference = row[3].Trim();
            var currency = row[6].Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 160 ||
                string.IsNullOrWhiteSpace(description) || description.Length > 500 ||
                reference.Length > 160)
                throw new InvalidOperationException($"CSV line {lineNumber} has a missing or oversized transaction identity or description.");
            if (!externalIds.Add(externalId))
                throw new InvalidOperationException($"CSV line {lineNumber} repeats external transaction identifier '{externalId}'.");
            if (!DateTimeOffset.TryParse(row[1].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var transactionDate))
                throw new InvalidOperationException($"CSV line {lineNumber} has an invalid transaction date.");
            var date = transactionDate.UtcDateTime;
            if (date < periodStart || date >= periodEnd)
                throw new InvalidOperationException($"CSV line {lineNumber} falls outside the declared statement period.");
            if (!decimal.TryParse(row[4].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var debit) ||
                !decimal.TryParse(row[5].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var credit) ||
                debit < 0m || credit < 0m || (debit > 0m && credit > 0m) || (debit == 0m && credit == 0m))
                throw new InvalidOperationException($"CSV line {lineNumber} must have exactly one positive debit or credit amount.");
            if (!string.Equals(currency, expectedCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"CSV line {lineNumber} currency does not match the bank account currency.");

            var fingerprintInput = string.Join("|",
                externalId,
                date.ToString("O", CultureInfo.InvariantCulture),
                debit.ToString("0.000000", CultureInfo.InvariantCulture),
                credit.ToString("0.000000", CultureInfo.InvariantCulture),
                currency,
                reference);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
            if (!fingerprints.Add(fingerprint))
                throw new InvalidOperationException($"CSV line {lineNumber} duplicates a transaction fingerprint within this statement.");
            parsed.Add(new ParsedBankStatementRow(externalId, date, description, string.IsNullOrEmpty(reference) ? null : reference,
                debit, credit, currency, lineNumber, fingerprint));
            debits += debit;
            credits += credit;
        }

        if (parsed.Count != declaredTransactionCount ||
            debits != declaredDebits || credits != declaredCredits)
            throw new InvalidOperationException("Parsed transaction count and debit/credit totals do not match the declared statement control totals.");
        if (openingBalance + credits - debits != closingBalance)
            throw new InvalidOperationException("Opening balance plus credits less debits does not reconcile to the declared closing balance.");

        return new ParsedBankStatement(parsed, debits, credits);
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
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        quoteClosed = true;
                    }
                }
                else field.Append(ch);
                continue;
            }

            if (quoteClosed && ch != '\r' && ch != '\n' && char.IsWhiteSpace(ch)) continue;
                throw new InvalidOperationException("Malformed CSV quoting: unexpected character after closing quote.");
            if (quoteClosed && ch != '\\r' && ch != '\\n' && char.IsWhiteSpace(ch)) continue;

            if (ch == '"')
            {
                if (fieldStarted || field.Length != 0)
                    throw new InvalidOperationException("Malformed CSV quoting: quote must begin a field.");
                inQuotes = true;
                fieldStarted = true;
            }
            else if (ch == ',')
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                quoteClosed = false;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                quoteClosed = false;
                if (record.Any(x => x.Length != 0)) records.Add(record);
                record = new List<string>();
            }
            else
            {
                if (fieldStarted && field.Length == 0 && !char.IsWhiteSpace(ch))
                    throw new InvalidOperationException("Malformed CSV quoting: unexpected character after opening quote.");
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
