namespace ApexERP.Domain.Controls
{
    public sealed record ApprovalTier(decimal UpTo, string RequiredRole);

    /// <summary>
    /// Delegation-of-authority lookup (spec 8.3). Thresholds are configuration, never hard-coded policy:
    /// each organisation supplies its own tiers. The last tier is the catch-all (UpTo = decimal.MaxValue).
    /// </summary>
    public static class ApprovalMatrix
    {
        public static string RequiredRole(IReadOnlyList<ApprovalTier> tiers, decimal amount)
        {
            if (tiers.Count == 0) throw new InvalidOperationException("No approval tiers are configured.");
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            foreach (var t in tiers.OrderBy(t => t.UpTo))
                if (amount <= t.UpTo) return t.RequiredRole;
            throw new InvalidOperationException("The approval matrix has no tier for this amount; add a catch-all tier.");
        }

        /// <summary>Every role whose authority covers the amount: the tier's own role and any higher tier (a Finance Director may approve a Supervisor-level amount).</summary>
        public static IReadOnlyList<string> EligibleRoles(IReadOnlyList<ApprovalTier> tiers, decimal amount) =>
            tiers.Where(t => t.UpTo >= amount).Select(t => t.RequiredRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Approver authority must cover the transaction value (spec 8.6).</summary>
        public static bool CanApprove(IReadOnlyList<ApprovalTier> tiers, IReadOnlyCollection<string> approverRoles, decimal amount)
        {
            if (tiers.Count == 0) throw new InvalidOperationException("No approval tiers are configured.");
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            var eligible = EligibleRoles(tiers, amount);
            return approverRoles.Any(r => eligible.Contains(r, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Material fields whose change invalidates an existing approval (spec 8.5).</summary>
        public static readonly IReadOnlyList<string> MaterialFields = new[]
        { "Amount", "CounterpartyId", "BankAccount", "GlAccountId", "FundingSource", "CostCentreId", "PaymentDestination" };

        public static bool ApprovalInvalidated(IReadOnlyDictionary<string, string?> approved, IReadOnlyDictionary<string, string?> current) =>
            MaterialFields.Any(f => !string.Equals(approved.GetValueOrDefault(f), current.GetValueOrDefault(f), StringComparison.Ordinal));
    }
}
