namespace ApexERP.Domain.Controls
{
    public sealed record AgeingBucket(int FromDays, int ToDays, decimal LossRatePct);
    public sealed record ReceivableBalance(string CustomerKey, int DaysPastDue, decimal Amount);

    /// <summary>IFRS 9 simplified-approach provision matrix (spec section 17). Loss rates are configuration (approved, versioned).</summary>
    public static class ExpectedCreditLoss
    {
        public static decimal Provision(IEnumerable<ReceivableBalance> balances, IReadOnlyList<AgeingBucket> matrix, int scale = 2)
        {
            decimal total = 0m;
            foreach (var b in balances)
            {
                var bucket = matrix.FirstOrDefault(m => b.DaysPastDue >= m.FromDays && b.DaysPastDue <= m.ToDays)
                    ?? throw new InvalidOperationException($"No loss-rate bucket covers {b.DaysPastDue} days past due.");
                total += b.Amount * bucket.LossRatePct / 100m;
            }
            return Math.Round(total, scale, MidpointRounding.ToEven);
        }
    }
}
