namespace ApexERP.Domain.Controls
{
    public enum MatchOutcome { Matched, WithinTolerance, Discrepancy }

    public sealed record MatchLine(string ItemKey, decimal OrderedQty, decimal ReceivedQty, decimal InvoicedQty,
        decimal OrderedUnitPrice, decimal InvoicedUnitPrice);

    public sealed record MatchIssue(string ItemKey, string Message);

    public sealed record MatchResult(MatchOutcome Outcome, IReadOnlyList<MatchIssue> Issues)
    {
        /// <summary>Payment must not proceed while a material discrepancy is unresolved (spec section 16).</summary>
        public bool PaymentAllowed => Outcome != MatchOutcome.Discrepancy;
    }

    /// <summary>Purchase order vs goods received vs supplier invoice (spec section 16).</summary>
    public static class ThreeWayMatch
    {
        public static MatchResult Evaluate(IEnumerable<MatchLine> lines, decimal quantityTolerancePct = 0m, decimal priceTolerancePct = 0m)
        {
            if (quantityTolerancePct < 0 || priceTolerancePct < 0) throw new ArgumentOutOfRangeException(nameof(quantityTolerancePct));
            var issues = new List<MatchIssue>();
            var withinTolerance = false;

            foreach (var l in lines)
            {
                if (l.InvoicedQty > l.ReceivedQty)
                {
                    if (Within(l.InvoicedQty, l.ReceivedQty, quantityTolerancePct)) withinTolerance = true;
                    else issues.Add(new(l.ItemKey, $"Invoiced quantity {l.InvoicedQty} exceeds goods received {l.ReceivedQty}."));
                }
                if (l.InvoicedQty > l.OrderedQty)
                {
                    if (Within(l.InvoicedQty, l.OrderedQty, quantityTolerancePct)) withinTolerance = true;
                    else issues.Add(new(l.ItemKey, $"Invoiced quantity {l.InvoicedQty} exceeds ordered quantity {l.OrderedQty}."));
                }
                if (l.InvoicedUnitPrice != l.OrderedUnitPrice)
                {
                    if (Within(l.InvoicedUnitPrice, l.OrderedUnitPrice, priceTolerancePct)) withinTolerance = true;
                    else issues.Add(new(l.ItemKey, $"Invoiced price {l.InvoicedUnitPrice} differs from order price {l.OrderedUnitPrice}."));
                }
            }

            var outcome = issues.Count > 0 ? MatchOutcome.Discrepancy : withinTolerance ? MatchOutcome.WithinTolerance : MatchOutcome.Matched;
            return new MatchResult(outcome, issues);
        }

        private static bool Within(decimal actual, decimal expected, decimal tolerancePct)
        {
            if (expected == 0m) return false;
            return Math.Abs(actual - expected) / expected * 100m <= tolerancePct;
        }
    }
}
