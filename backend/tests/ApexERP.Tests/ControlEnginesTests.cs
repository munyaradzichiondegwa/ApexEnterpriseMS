using ApexERP.Domain.Controls;
using Xunit;

namespace ApexERP.Tests
{
    /// <summary>Tests for the spec-driven control engines (APEXERP specification sections 4-9, 11, 13, 16-17, 21, 28).</summary>
    public class ControlEnginesTests
    {
        // ---- 4.4 / 5.3: schedules ----
        [Fact]
        public void Prepayment_12000_over_12_months_is_1000_a_month()
        {
            var s = RecognitionSchedule.StraightLine(12000m, new DateTime(2026, 1, 15), 12);
            Assert.Equal(12, s.Count);
            Assert.True(s.All(l => l.Amount == 1000m));
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), s[0].PeriodStart);
            Assert.Equal(12000m, s.Sum(l => l.Amount));
        }

        [Fact]
        public void Revenue_24000_over_24_months_is_1000_a_month_and_liability_unwinds()
        {
            var s = RecognitionSchedule.StraightLine(24000m, new DateTime(2026, 1, 1), 24);
            Assert.Equal(1000m, s[0].Amount);
            Assert.Equal(23000m, RecognitionSchedule.Unrecognised(24000m, s, 1));
            Assert.Equal(0m, RecognitionSchedule.Unrecognised(24000m, s, 24));
        }

        [Fact]
        public void Schedule_always_sums_exactly_even_when_it_does_not_divide_evenly()
        {
            var s = RecognitionSchedule.StraightLine(1000m, new DateTime(2026, 3, 1), 3);
            Assert.Equal(1000m, s.Sum(l => l.Amount));
            Assert.Equal(333.33m, s[0].Amount);
            Assert.Equal(333.34m, s[2].Amount);
        }

        [Fact]
        public void Schedule_rejects_zero_periods_and_negative_totals()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RecognitionSchedule.StraightLine(100m, DateTime.UtcNow, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RecognitionSchedule.StraightLine(-1m, DateTime.UtcNow, 3));
        }

        // ---- 16: three-way match ----
        [Fact]
        public void Three_way_match_clean_match_allows_payment()
        {
            var r = ThreeWayMatch.Evaluate(new[] { new MatchLine("A", 10, 10, 10, 5m, 5m) });
            Assert.Equal(MatchOutcome.Matched, r.Outcome);
            Assert.True(r.PaymentAllowed);
        }

        [Fact]
        public void Three_way_match_blocks_invoice_for_more_than_received()
        {
            var r = ThreeWayMatch.Evaluate(new[] { new MatchLine("A", 10, 6, 10, 5m, 5m) });
            Assert.Equal(MatchOutcome.Discrepancy, r.Outcome);
            Assert.False(r.PaymentAllowed);
            Assert.True(r.Issues.Count >= 1);
        }

        [Fact]
        public void Three_way_match_blocks_price_difference_unless_within_tolerance()
        {
            var lines = new[] { new MatchLine("A", 10, 10, 10, 100m, 102m) };
            Assert.False(ThreeWayMatch.Evaluate(lines).PaymentAllowed);
            var tolerated = ThreeWayMatch.Evaluate(lines, priceTolerancePct: 5m);
            Assert.Equal(MatchOutcome.WithinTolerance, tolerated.Outcome);
            Assert.True(tolerated.PaymentAllowed);
        }

        // ---- 8: approval matrix ----
        private static readonly IReadOnlyList<ApprovalTier> Tiers = new[]
        {
            new ApprovalTier(500m, "Supervisor"), new ApprovalTier(5000m, "DepartmentHead"),
            new ApprovalTier(25000m, "FinanceManager"), new ApprovalTier(100000m, "FinanceDirector"),
            new ApprovalTier(decimal.MaxValue, "Executive")
        };

        [Fact]
        public void Approval_matrix_resolves_the_role_for_each_band()
        {
            Assert.Equal("Supervisor", ApprovalMatrix.RequiredRole(Tiers, 500m));
            Assert.Equal("DepartmentHead", ApprovalMatrix.RequiredRole(Tiers, 500.01m));
            Assert.Equal("FinanceManager", ApprovalMatrix.RequiredRole(Tiers, 25000m));
            Assert.Equal("Executive", ApprovalMatrix.RequiredRole(Tiers, 100000.01m));
        }

        [Fact]
        public void Approver_authority_must_cover_the_value()
        {
            Assert.False(ApprovalMatrix.CanApprove(Tiers, new[] { "Supervisor" }, 4000m));
            Assert.True(ApprovalMatrix.CanApprove(Tiers, new[] { "DepartmentHead" }, 4000m));
            Assert.True(ApprovalMatrix.CanApprove(Tiers, new[] { "FinanceDirector" }, 100m));   // a higher tier covers lower amounts
            Assert.False(ApprovalMatrix.CanApprove(Tiers, new[] { "FinanceManager" }, 50000m));
        }

        [Fact]
        public void Changing_a_material_field_invalidates_an_approval()
        {
            var approved = new Dictionary<string, string?> { ["Amount"] = "1000", ["BankAccount"] = "X1" };
            var same = new Dictionary<string, string?> { ["Amount"] = "1000", ["BankAccount"] = "X1" };
            var changed = new Dictionary<string, string?> { ["Amount"] = "1000", ["BankAccount"] = "X2" };
            Assert.False(ApprovalMatrix.ApprovalInvalidated(approved, same));
            Assert.True(ApprovalMatrix.ApprovalInvalidated(approved, changed));
        }

        [Fact]
        public void Empty_matrix_is_refused_rather_than_approving_everything()
        {
            Assert.Throws<InvalidOperationException>(() => ApprovalMatrix.RequiredRole(Array.Empty<ApprovalTier>(), 10m));
        }

        // ---- 7: segregation of duties ----
        [Fact]
        public void Sod_detects_incompatible_permission_pairs()
        {
            var c = SegregationOfDuties.Detect(new[] { "gl.journals.create", "gl.journals.approve" });
            Assert.Single(c);
            Assert.Equal(1, SegregationOfDuties.Detect(new[] { "gl.*" }).Count(x => x.PermissionA == "gl.journals.create" && x.PermissionB == "gl.journals.approve"));   // wildcards count
            Assert.True(SegregationOfDuties.Detect(new[] { "ap.write", "ap.payments.create" }).Count == 1);
            Assert.True(SegregationOfDuties.Detect(new[] { "gl.journals.create", "reports.financial.read" }).Count == 0);
        }

        [Fact]
        public void Sod_wildcard_holder_conflicts_with_every_rule()
        {
            Assert.Equal(SegregationOfDuties.DefaultRules.Count, SegregationOfDuties.Detect(new[] { "*" }).Count);
        }

        [Fact]
        public void Preparer_cannot_approve_own_transaction_unless_explicitly_allowed()
        {
            var u = Guid.NewGuid();
            Assert.False(SegregationOfDuties.MayApprove(u, u, allowSelfApproval: false));
            Assert.True(SegregationOfDuties.MayApprove(u, Guid.NewGuid(), allowSelfApproval: false));
            Assert.True(SegregationOfDuties.MayApprove(u, u, allowSelfApproval: true));
        }

        // ---- 17: ECL ----
        [Fact]
        public void Expected_credit_loss_uses_the_ageing_matrix()
        {
            var matrix = new[] { new AgeingBucket(0, 30, 1m), new AgeingBucket(31, 90, 5m), new AgeingBucket(91, 100000, 40m) };
            var p = ExpectedCreditLoss.Provision(new[]
            {
                new ReceivableBalance("c1", 10, 10000m), new ReceivableBalance("c2", 45, 2000m), new ReceivableBalance("c3", 120, 1000m)
            }, matrix);
            Assert.Equal(100m + 100m + 400m, p);
        }

        [Fact]
        public void Expected_credit_loss_fails_loudly_when_no_bucket_covers_the_age()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ExpectedCreditLoss.Provision(new[] { new ReceivableBalance("c", 500, 1m) }, new[] { new AgeingBucket(0, 30, 1m) }));
        }

        // ---- 9 / 15: risk ----
        [Fact]
        public void Risk_score_rises_with_value_and_red_flags_and_is_capped()
        {
            var t = new MaterialityThresholds(1000m, 10000m, 100000m);
            var low = ExceptionRisk.Score(new(50m, false, false, false, false, false, false), t);
            var high = ExceptionRisk.Score(new(500000m, true, true, true, true, true, true), t);
            Assert.Equal(RiskLevel.Low, ExceptionRisk.Classify(low));
            Assert.Equal(RiskLevel.Critical, ExceptionRisk.Classify(high));
            Assert.True(high <= 100);
        }

        [Fact]
        public void Exception_lifecycle_moves_one_step_at_a_time()
        {
            Assert.True(ExceptionRisk.CanAdvance("DETECTED", "CLASSIFIED"));
            Assert.False(ExceptionRisk.CanAdvance("DETECTED", "RESOLVED"));
            Assert.False(ExceptionRisk.CanAdvance("RESOLVED", "CLOSED"));       // must be independently reviewed first
            Assert.True(ExceptionRisk.CanAdvance("INDEPENDENTLY_REVIEWED", "CLOSED"));
        }

        // ---- 11: lockout ----
        [Fact]
        public void Lockout_starts_at_the_threshold_and_backs_off_up_to_an_hour()
        {
            var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
            Assert.Null(AccountLockout.NextLockout(4, now));
            Assert.Equal(now.AddMinutes(5), AccountLockout.NextLockout(5, now));
            Assert.Equal(now.AddMinutes(10), AccountLockout.NextLockout(6, now));
            Assert.Equal(now.AddMinutes(60), AccountLockout.NextLockout(50, now));
            Assert.True(AccountLockout.IsLocked(now.AddMinutes(1), now));
            Assert.False(AccountLockout.IsLocked(now.AddMinutes(-1), now));
            Assert.False(AccountLockout.IsLocked(null, now));
        }

        // ---- 13 / 21: identities ----
        [Fact]
        public void Accounting_identities_hold_and_detect_breaks()
        {
            Assert.True(FinancialIdentities.BalanceSheetBalances(1000m, 400m, 600m));
            Assert.False(FinancialIdentities.BalanceSheetBalances(1000m, 400m, 500m));
            Assert.True(FinancialIdentities.EquityRollForwardHolds(500m, 100m, 600m));
            Assert.True(FinancialIdentities.CashReconciles(50m, -20m, 30m));
        }

        [Fact]
        public void Commitments_cannot_exceed_available_budget_without_an_authorised_exception()
        {
            Assert.Equal(300m, FinancialIdentities.AvailableBudget(1000m, 500m, 200m));
            Assert.True(FinancialIdentities.CommitmentAllowed(1000m, 500m, 200m, 300m, false));
            Assert.False(FinancialIdentities.CommitmentAllowed(1000m, 500m, 200m, 300.01m, false));
            Assert.True(FinancialIdentities.CommitmentAllowed(1000m, 500m, 200m, 900m, true));
        }

        // ---- GL-007: idempotency ----
        [Fact]
        public void Posting_fingerprint_is_stable_and_ignores_line_order()
        {
            var co = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var d = new DateTime(2026, 11, 5);
            var f1 = PostingFingerprint.Compute(co, "AR", "INV-1", d, new[] { (a, 100m, 0m, "usd"), (b, 0m, 100m, "USD") });
            var f2 = PostingFingerprint.Compute(co, "AR", " INV-1 ", d, new[] { (b, 0m, 100m, "USD"), (a, 100m, 0m, "USD") });
            var f3 = PostingFingerprint.Compute(co, "AR", "INV-1", d, new[] { (a, 101m, 0m, "USD"), (b, 0m, 101m, "USD") });
            Assert.Equal(f1, f2);
            Assert.True(f1 != f3);
            Assert.True(f1 != PostingFingerprint.Compute(Guid.NewGuid(), "AR", "INV-1", d, new[] { (a, 100m, 0m, "USD"), (b, 0m, 100m, "USD") }));
        }
    }
}
