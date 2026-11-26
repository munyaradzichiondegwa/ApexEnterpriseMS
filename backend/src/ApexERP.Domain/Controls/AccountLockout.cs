namespace ApexERP.Domain.Controls
{
    /// <summary>Failed-sign-in lockout (spec section 11). Pure decision logic so it is testable without a database.</summary>
    public static class AccountLockout
    {
        public static bool IsLocked(DateTime? lockedUntilUtc, DateTime nowUtc) =>
            lockedUntilUtc is { } until && until > nowUtc;

        /// <summary>Returns the new lock-out expiry when this failure trips the threshold, otherwise null. Back-off doubles per further failure, capped at 1 hour.</summary>
        public static DateTime? NextLockout(int failedAttemptsAfterThisFailure, DateTime nowUtc, int maxAttempts = 5, int baseMinutes = 5)
        {
            if (failedAttemptsAfterThisFailure < maxAttempts) return null;
            var extra = Math.Min(failedAttemptsAfterThisFailure - maxAttempts, 4);
            return nowUtc.AddMinutes(Math.Min(60, baseMinutes * (1 << extra)));
        }
    }
}
