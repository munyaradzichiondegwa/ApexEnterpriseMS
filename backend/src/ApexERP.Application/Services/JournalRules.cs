using System.Globalization;
using ApexERP.Domain.Common;

namespace ApexERP.Application.Services
{
    /// <summary>A journal line after validation and normalisation (amounts rounded to the storage scale).</summary>
    public sealed record ValidatedLine(GlLineRequest Source, string Currency, decimal Debit, decimal Credit,
        decimal BaseDebit, decimal BaseCredit);

    /// <summary>
    /// Pure validation rules for journal lines (no database access, so they are unit-tested directly).
    /// Every rule here exists to keep the ledger trustworthy: balanced, single-sided, non-negative lines with a
    /// positive exchange rate and a real currency code (repository audit finding 7, design document section 2).
    /// </summary>
    public static class JournalValidator
    {
        /// <summary>Amounts are stored as NUMERIC(20,6); PostgreSQL rounds half away from zero.</summary>
        public const int AmountScale = 6;

        public static decimal Round(decimal value) => Math.Round(value, AmountScale, MidpointRounding.AwayFromZero);

        /// <summary>Returns the upper-cased ISO-style 3-letter code, or null when it is not one.</summary>
        public static string? NormaliseCurrency(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var trimmed = code.Trim();
            if (trimmed.Length != 3) return null;
            foreach (var ch in trimmed)
                if (ch < 'A' || (ch > 'Z' && ch < 'a') || ch > 'z') return null;
            return trimmed.ToUpperInvariant();
        }

        /// <param name="allowZeroLines">System postings may legitimately carry a zero line (e.g. a zero-value
        /// invoice); manual journals may not, because a 0/0 line is always a data-entry mistake.</param>
        public static IReadOnlyList<ValidatedLine> ValidateLines(IReadOnlyCollection<GlLineRequest> lines, bool allowZeroLines)
        {
            if (lines == null || lines.Count < 2)
                throw new InvalidOperationException("A journal entry requires at least two lines.");

            var result = new List<ValidatedLine>(lines.Count);
            decimal totalBaseDebit = 0m, totalBaseCredit = 0m;
            var n = 0;

            foreach (var l in lines)
            {
                n++;
                if (l.GlAccountId == Guid.Empty)
                    throw new InvalidOperationException($"Line {n}: a GL account is required.");
                if (l.Debit < 0 || l.Credit < 0)
                    throw new InvalidOperationException($"Line {n}: debit and credit amounts cannot be negative.");
                if (l.Debit > 0 && l.Credit > 0)
                    throw new InvalidOperationException($"Line {n}: a single journal line cannot carry both a debit and a credit.");
                if (!allowZeroLines && l.Debit == 0 && l.Credit == 0)
                    throw new InvalidOperationException($"Line {n}: enter a debit or a credit amount.");

                var currency = NormaliseCurrency(l.Currency)
                    ?? throw new InvalidOperationException($"Line {n}: '{l.Currency}' is not a valid 3-letter currency code.");

                if (l.ExchangeRate <= 0)
                    throw new InvalidOperationException($"Line {n}: the exchange rate must be greater than zero.");

                var debit = Round(l.Debit);
                var credit = Round(l.Credit);
                var baseDebit = Round(debit * l.ExchangeRate);
                var baseCredit = Round(credit * l.ExchangeRate);

                totalBaseDebit += baseDebit;
                totalBaseCredit += baseCredit;
                result.Add(new ValidatedLine(l, currency, debit, credit, baseDebit, baseCredit));
            }

            if (Round(totalBaseDebit - totalBaseCredit) != 0m)
                throw new InvalidOperationException(
                    $"Journal entry is not balanced: base debit total {totalBaseDebit:N6} != base credit total {totalBaseCredit:N6}.");

            return result;
        }
    }

    /// <summary>
    /// Journal lifecycle: Draft -> Submitted -> Approved -> Posted -> Reversed, with Rejected and Cancelled as exits.
    /// A transition not listed here is rejected, which is what makes the approval policy enforceable.
    /// </summary>
    public static class JournalWorkflow
    {
        private static readonly IReadOnlyDictionary<JournalStatus, JournalStatus[]> Allowed =
            new Dictionary<JournalStatus, JournalStatus[]>
            {
                [JournalStatus.Draft] = new[] { JournalStatus.Submitted, JournalStatus.Cancelled },
                [JournalStatus.Submitted] = new[] { JournalStatus.Approved, JournalStatus.Rejected, JournalStatus.Cancelled },
                [JournalStatus.Approved] = new[] { JournalStatus.Posted, JournalStatus.Cancelled },
                [JournalStatus.Rejected] = new[] { JournalStatus.Cancelled },
                [JournalStatus.Posted] = new[] { JournalStatus.Reversed },
                [JournalStatus.Reversed] = Array.Empty<JournalStatus>(),
                [JournalStatus.Cancelled] = Array.Empty<JournalStatus>()
            };

        public static bool CanTransition(JournalStatus from, JournalStatus to) =>
            Allowed.TryGetValue(from, out var next) && next.Contains(to);

        public static void EnsureTransition(JournalStatus from, JournalStatus to)
        {
            if (!CanTransition(from, to))
                throw new InvalidOperationException($"A journal entry that is {from} cannot be changed to {to}.");
        }

        /// <summary>Segregation of duties: whoever prepared a journal may not approve it (unless explicitly allowed).</summary>
        public static void EnsureSeparationOfDuties(Guid? preparerId, Guid approverId, bool allowSelfApproval)
        {
            if (!allowSelfApproval && preparerId.HasValue && preparerId.Value == approverId)
                throw new InvalidOperationException(
                    "Segregation of duties: you prepared this journal, so a different user must approve it.");
        }
    }

    /// <summary>Journal number formatting helpers (pure).</summary>
    public static class JournalNumbering
    {
        public static string Prefix(string kind, DateTime date) =>
            $"{kind}-{date.ToString("yyyyMM", CultureInfo.InvariantCulture)}-";

        /// <summary>Next sequence number given the highest existing number with this prefix (null = none yet).</summary>
        public static int NextSequence(string? lastEntryNo, string prefix)
        {
            if (string.IsNullOrEmpty(lastEntryNo)) return 1;
            if (!lastEntryNo.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(lastEntryNo.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var current))
                throw new InvalidOperationException($"Unrecognised journal number '{lastEntryNo}'.");
            return checked(current + 1);
        }

        public static string Format(string prefix, int sequence) => $"{prefix}{sequence:D5}";

        /// <summary>Key used for the transaction lock that serialises number allocation and period changes.</summary>
        public static string PeriodLockKey(Guid companyId, int year, int month) =>
            $"period:{companyId:N}:{year}-{month:D2}";

        public static string NumberLockKey(Guid companyId, string prefix) => $"entryno:{companyId:N}:{prefix}";
    }

    /// <summary>Reporting date helpers (pure).</summary>
    public static class ReportingDates
    {
        /// <summary>
        /// Exclusive upper bound that includes the WHOLE of <paramref name="date"/>. A date-only "as of" value is midnight,
        /// so a plain <c>EntryDate &lt;= asOf</c> would drop everything posted later that same day (for example a reversal
        /// dated "now"). Use <c>EntryDate &lt; EndExclusive(asOf)</c> instead.
        /// </summary>
        public static DateTime EndExclusive(DateTime date) => DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Utc);

        public static DateTime StartInclusive(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
    }

    /// <summary>Fiscal-calendar helpers (pure).</summary>
    public static class FiscalCalendar
    {
        /// <summary>First day of the fiscal year that contains <paramref name="asOf"/>, for a year starting in
        /// <paramref name="fiscalYearStartMonth"/> (1-12; anything else is treated as January).</summary>
        public static DateTime FiscalYearStart(DateTime asOf, int fiscalYearStartMonth)
        {
            var month = fiscalYearStartMonth is >= 1 and <= 12 ? fiscalYearStartMonth : 1;
            var year = asOf.Month >= month ? asOf.Year : asOf.Year - 1;
            return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
