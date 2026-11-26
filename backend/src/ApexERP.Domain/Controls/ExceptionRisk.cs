namespace ApexERP.Domain.Controls
{
    public enum RiskLevel { Low, Medium, High, Critical }

    public sealed record MaterialityThresholds(decimal Medium, decimal High, decimal Critical);

    public sealed record JournalRiskInput(decimal Amount, bool IsManual, bool PostedOutsideBusinessHours, bool IsRoundAmount,
        bool TouchesSuspenseAccount, bool IsRelatedParty, bool InClosedPeriodAttempt);

    /// <summary>Manual-journal / transaction risk scoring (spec sections 9 and 15). A score assists investigation; it never establishes fraud.</summary>
    public static class ExceptionRisk
    {
        public static int Score(JournalRiskInput i, MaterialityThresholds t)
        {
            var s = 0;
            if (i.Amount >= t.Critical) s += 40; else if (i.Amount >= t.High) s += 25; else if (i.Amount >= t.Medium) s += 10;
            if (i.IsManual) s += 10;
            if (i.PostedOutsideBusinessHours) s += 10;
            if (i.IsRoundAmount && i.Amount >= t.Medium) s += 5;
            if (i.TouchesSuspenseAccount) s += 15;
            if (i.IsRelatedParty) s += 15;
            if (i.InClosedPeriodAttempt) s += 30;
            return Math.Min(100, s);
        }

        public static RiskLevel Classify(int score) => score >= 70 ? RiskLevel.Critical : score >= 50 ? RiskLevel.High : score >= 25 ? RiskLevel.Medium : RiskLevel.Low;

        /// <summary>Exception lifecycle (spec 9.3): each state may only move to the next.</summary>
        public static readonly IReadOnlyList<string> Lifecycle = new[]
        { "DETECTED", "CLASSIFIED", "ASSIGNED", "INVESTIGATED", "RESOLVED", "INDEPENDENTLY_REVIEWED", "CLOSED" };

        public static bool CanAdvance(string from, string to)
        {
            var a = Array.IndexOf(Lifecycle.ToArray(), from); var b = Array.IndexOf(Lifecycle.ToArray(), to);
            return a >= 0 && b == a + 1;
        }
    }
}
