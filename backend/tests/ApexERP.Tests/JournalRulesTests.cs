using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using Xunit;

namespace ApexERP.Tests
{
    public class JournalValidatorTests
    {
        private static readonly Guid Cash = Guid.NewGuid();
        private static readonly Guid Sales = Guid.NewGuid();

        private static GlLineRequest Dr(decimal amount, string ccy = "USD", decimal rate = 1m, Guid? account = null) =>
            new(account ?? Cash, amount, 0m, ccy, rate);

        private static GlLineRequest Cr(decimal amount, string ccy = "USD", decimal rate = 1m, Guid? account = null) =>
            new(account ?? Sales, 0m, amount, ccy, rate);

        [Fact]
        public void A_balanced_two_line_journal_is_accepted()
        {
            var result = JournalValidator.ValidateLines(new[] { Dr(100m), Cr(100m) }, allowZeroLines: false);
            Assert.Equal(2, result.Count);
            Assert.Equal(100m, result[0].BaseDebit);
            Assert.Equal(100m, result[1].BaseCredit);
        }

        [Fact]
        public void A_journal_needs_at_least_two_lines()
        {
            Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(new[] { Dr(100m) }, false));
            Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(Array.Empty<GlLineRequest>(), false));
        }

        [Fact]
        public void An_unbalanced_journal_is_rejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(new[] { Dr(100m), Cr(99.99m) }, false));
            Assert.Contains("not balanced", ex.Message);
        }

        [Fact]
        public void Negative_amounts_are_rejected()
        {
            Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(new[] { Dr(-100m), Cr(-100m) }, false));
        }

        [Fact]
        public void A_line_cannot_carry_both_a_debit_and_a_credit()
        {
            var both = new GlLineRequest(Cash, 50m, 50m, "USD", 1m);
            var ex = Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(new[] { both, Cr(0m) }, true));
            Assert.Contains("both a debit and a credit", ex.Message);
        }

        [Fact]
        public void Zero_lines_are_rejected_for_manual_journals_but_tolerated_for_system_postings()
        {
            var zero = new GlLineRequest(Cash, 0m, 0m, "USD", 1m);
            Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(new[] { Dr(10m), Cr(10m), zero }, allowZeroLines: false));
            Assert.Equal(3, JournalValidator.ValidateLines(new[] { Dr(10m), Cr(10m), zero }, allowZeroLines: true).Count);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-0.000001)]
        public void A_zero_or_negative_exchange_rate_is_rejected(double rate)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                JournalValidator.ValidateLines(new[] { Dr(100m, "USD", (decimal)rate), Cr(100m, "USD", (decimal)rate) }, false));
            Assert.Contains("exchange rate must be greater than zero", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("US")]
        [InlineData("USDX")]
        [InlineData("U$D")]
        [InlineData("123")]
        [InlineData(null)]
        public void An_invalid_currency_code_is_rejected(string? code)
        {
            Assert.Throws<InvalidOperationException>(() =>
                JournalValidator.ValidateLines(new[] { Dr(10m, code!), Cr(10m) }, false));
        }

        [Theory]
        [InlineData("usd", "USD")]
        [InlineData(" zwg ", "ZWG")]
        [InlineData("Eur", "EUR")]
        public void Currency_codes_are_normalised_to_upper_case(string input, string expected)
        {
            Assert.Equal(expected, JournalValidator.NormaliseCurrency(input));
        }

        [Fact]
        public void A_missing_account_is_rejected()
        {
            var noAccount = new GlLineRequest(Guid.Empty, 10m, 0m, "USD", 1m);
            Assert.Throws<InvalidOperationException>(() => JournalValidator.ValidateLines(new[] { noAccount, Cr(10m) }, false));
        }

        [Fact]
        public void A_multi_currency_journal_balances_in_base_currency()
        {
            // EUR 100 at 1.10 = USD 110 debit; USD 110 credit.
            var result = JournalValidator.ValidateLines(new[] { Dr(100m, "EUR", 1.10m), Cr(110m) }, false);
            Assert.Equal(110m, result[0].BaseDebit);
            Assert.Equal(110m, result[1].BaseCredit);
        }

        [Fact]
        public void Amounts_are_rounded_to_the_storage_scale_before_balancing()
        {
            // 0.1234565 rounds half away from zero to 0.123457 (as PostgreSQL does when it stores NUMERIC(20,6)).
            var result = JournalValidator.ValidateLines(new[] { Dr(0.1234565m), Cr(0.123457m) }, false);
            Assert.Equal(0.123457m, result[0].Debit);
            Assert.Equal(0.123457m, result[0].BaseDebit);
        }
    }

    public class JournalWorkflowTests
    {
        [Theory]
        [InlineData(JournalStatus.Draft, JournalStatus.Submitted)]
        [InlineData(JournalStatus.Draft, JournalStatus.Cancelled)]
        [InlineData(JournalStatus.Submitted, JournalStatus.Approved)]
        [InlineData(JournalStatus.Submitted, JournalStatus.Rejected)]
        [InlineData(JournalStatus.Submitted, JournalStatus.Cancelled)]
        [InlineData(JournalStatus.Approved, JournalStatus.Posted)]
        [InlineData(JournalStatus.Approved, JournalStatus.Cancelled)]
        [InlineData(JournalStatus.Rejected, JournalStatus.Cancelled)]
        [InlineData(JournalStatus.Posted, JournalStatus.Reversed)]
        public void Allowed_transitions(JournalStatus from, JournalStatus to)
        {
            Assert.True(JournalWorkflow.CanTransition(from, to));
            JournalWorkflow.EnsureTransition(from, to);
        }

        [Theory]
        [InlineData(JournalStatus.Draft, JournalStatus.Posted)]        // cannot skip approval
        [InlineData(JournalStatus.Draft, JournalStatus.Approved)]
        [InlineData(JournalStatus.Submitted, JournalStatus.Posted)]    // cannot post without approval
        [InlineData(JournalStatus.Approved, JournalStatus.Draft)]
        [InlineData(JournalStatus.Approved, JournalStatus.Submitted)]
        [InlineData(JournalStatus.Rejected, JournalStatus.Approved)]
        [InlineData(JournalStatus.Posted, JournalStatus.Cancelled)]    // posted history is never cancelled
        [InlineData(JournalStatus.Posted, JournalStatus.Draft)]
        [InlineData(JournalStatus.Reversed, JournalStatus.Posted)]
        [InlineData(JournalStatus.Reversed, JournalStatus.Reversed)]
        [InlineData(JournalStatus.Cancelled, JournalStatus.Draft)]
        public void Forbidden_transitions(JournalStatus from, JournalStatus to)
        {
            Assert.False(JournalWorkflow.CanTransition(from, to));
            Assert.Throws<InvalidOperationException>(() => JournalWorkflow.EnsureTransition(from, to));
        }

        [Fact]
        public void The_preparer_cannot_approve_their_own_journal()
        {
            var user = Guid.NewGuid();
            Assert.Throws<InvalidOperationException>(() => JournalWorkflow.EnsureSeparationOfDuties(user, user, allowSelfApproval: false));
        }

        [Fact]
        public void A_different_user_may_approve()
        {
            JournalWorkflow.EnsureSeparationOfDuties(Guid.NewGuid(), Guid.NewGuid(), allowSelfApproval: false);
        }

        [Fact]
        public void Self_approval_can_be_enabled_explicitly_for_single_user_setups()
        {
            var user = Guid.NewGuid();
            JournalWorkflow.EnsureSeparationOfDuties(user, user, allowSelfApproval: true);
        }

        [Fact]
        public void A_legacy_journal_without_a_recorded_preparer_can_be_approved()
        {
            JournalWorkflow.EnsureSeparationOfDuties(null, Guid.NewGuid(), allowSelfApproval: false);
        }

        [Fact]
        public void Existing_status_values_keep_their_stored_numbers()
        {
            // Status is persisted as an integer; the original three values must never be renumbered.
            Assert.Equal(0, (int)JournalStatus.Draft);
            Assert.Equal(1, (int)JournalStatus.Posted);
            Assert.Equal(2, (int)JournalStatus.Reversed);
        }
    }

    public class JournalNumberingTests
    {
        [Fact]
        public void Prefix_uses_the_year_and_month_regardless_of_culture()
        {
            Assert.Equal("JE-202610-", JournalNumbering.Prefix("JE", new DateTime(2026, 10, 25)));
            Assert.Equal("DR-202601-", JournalNumbering.Prefix("DR", new DateTime(2026, 1, 3)));
        }

        [Fact]
        public void The_first_number_of_a_month_is_one()
        {
            Assert.Equal(1, JournalNumbering.NextSequence(null, "JE-202610-"));
            Assert.Equal("JE-202610-00001", JournalNumbering.Format("JE-202610-", 1));
        }

        [Fact]
        public void The_next_number_follows_the_highest_existing_one()
        {
            Assert.Equal(42, JournalNumbering.NextSequence("JE-202610-00041", "JE-202610-"));
        }

        [Fact]
        public void Numbers_keep_counting_past_five_digits()
        {
            Assert.Equal(100000, JournalNumbering.NextSequence("JE-202610-99999", "JE-202610-"));
            Assert.Equal("JE-202610-100000", JournalNumbering.Format("JE-202610-", 100000));
        }

        [Theory]
        [InlineData("JE-202610-ABC")]
        [InlineData("INV-2026-00001")]
        [InlineData("JE-202610-")]
        public void An_unrecognised_existing_number_is_an_error_not_a_silent_restart(string existing)
        {
            Assert.Throws<InvalidOperationException>(() => JournalNumbering.NextSequence(existing, "JE-202610-"));
        }

        [Fact]
        public void Lock_keys_are_stable_and_scoped_per_company_and_period()
        {
            var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
            Assert.Equal(JournalNumbering.PeriodLockKey(a, 2026, 10), JournalNumbering.PeriodLockKey(a, 2026, 10));
            Assert.NotEqual(JournalNumbering.PeriodLockKey(a, 2026, 10), JournalNumbering.PeriodLockKey(b, 2026, 10));
            Assert.NotEqual(JournalNumbering.PeriodLockKey(a, 2026, 10), JournalNumbering.PeriodLockKey(a, 2026, 11));
            Assert.Equal("period:11111111111111111111111111111111:2026-10", JournalNumbering.PeriodLockKey(a, 2026, 10));
        }
    }

    public class ReportingDateTests
    {
        [Fact]
        public void End_exclusive_includes_the_whole_of_a_date_only_value()
        {
            var end = ReportingDates.EndExclusive(new DateTime(2026, 10, 31));
            Assert.Equal(new DateTime(2026, 11, 1), end);
            Assert.Equal(DateTimeKind.Utc, end.Kind);
            // An entry made at 14:30 on 31 October is before the exclusive bound, so it is reported.
            Assert.True(new DateTime(2026, 10, 31, 14, 30, 0) < end);
        }

        [Fact]
        public void End_exclusive_ignores_the_time_part_of_the_input()
        {
            Assert.Equal(new DateTime(2026, 11, 1), ReportingDates.EndExclusive(new DateTime(2026, 10, 31, 23, 59, 59)));
        }

        [Fact]
        public void Start_inclusive_is_midnight_utc()
        {
            var start = ReportingDates.StartInclusive(new DateTime(2026, 10, 5, 13, 0, 0));
            Assert.Equal(new DateTime(2026, 10, 5), start);
            Assert.Equal(DateTimeKind.Utc, start.Kind);
        }
    }

    public class FiscalCalendarTests
    {
        [Theory]
        [InlineData(2026, 10, 25, 1, 2026, 1)]    // calendar fiscal year
        [InlineData(2026, 1, 1, 1, 2026, 1)]
        [InlineData(2026, 10, 25, 7, 2026, 7)]    // July-June year, already into it
        [InlineData(2026, 7, 1, 7, 2026, 7)]      // first day of the fiscal year
        [InlineData(2026, 3, 10, 7, 2025, 7)]     // before the start month => previous calendar year
        [InlineData(2026, 10, 25, 0, 2026, 1)]    // invalid month => January
        [InlineData(2026, 10, 25, 13, 2026, 1)]
        public void Fiscal_year_start(int y, int m, int d, int startMonth, int expectedYear, int expectedMonth)
        {
            var start = FiscalCalendar.FiscalYearStart(new DateTime(y, m, d), startMonth);
            Assert.Equal(new DateTime(expectedYear, expectedMonth, 1), start);
            Assert.Equal(DateTimeKind.Utc, start.Kind);
        }
    }
}
