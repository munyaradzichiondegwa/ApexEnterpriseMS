namespace ApexERP.Domain.Controls
{
    public sealed record ObligationPrice(string Key, decimal StandaloneSellingPrice);

    /// <summary>IFRS 15 step 4, IAS 36, IAS 2 and credit/cash rules as pure, testable functions (spec 5, 6, 17, 19).</summary>
    public static class ValuationRules
    {
        /// <summary>Allocates a transaction price across obligations in proportion to standalone selling price. Rounding remainder goes to the largest obligation so the total is exact.</summary>
        public static IReadOnlyDictionary<string, decimal> AllocateTransactionPrice(decimal transactionPrice, IReadOnlyList<ObligationPrice> obligations, int scale = 2)
        {
            if (obligations.Count == 0) throw new InvalidOperationException("A contract needs at least one performance obligation.");
            var totalSsp = obligations.Sum(o => o.StandaloneSellingPrice);
            if (totalSsp <= 0) throw new InvalidOperationException("Standalone selling prices must be positive.");
            var result = new Dictionary<string, decimal>();
            foreach (var o in obligations)
                result[o.Key] = Math.Round(transactionPrice * o.StandaloneSellingPrice / totalSsp, scale, MidpointRounding.ToEven);
            var diff = transactionPrice - result.Values.Sum();
            if (diff != 0m) { var largest = obligations.OrderByDescending(o => o.StandaloneSellingPrice).First().Key; result[largest] += diff; }
            return result;
        }

        /// <summary>Recoverable amount is the higher of fair value less costs to sell and value in use; the loss is carrying less recoverable, never negative.</summary>
        public static (decimal Recoverable, decimal Loss) Impairment(decimal carrying, decimal fairValueLessCosts, decimal valueInUse)
        {
            var recoverable = Math.Max(fairValueLessCosts, valueInUse);
            return (recoverable, Math.Max(0m, carrying - recoverable));
        }

        /// <summary>Present value of annual cash flows at a discount rate (value in use). Flow 1 is one year away.</summary>
        public static decimal PresentValue(IReadOnlyList<decimal> annualCashFlows, decimal discountRatePct)
        {
            if (discountRatePct <= -100m) throw new ArgumentOutOfRangeException(nameof(discountRatePct));
            var r = 1m + discountRatePct / 100m; decimal pv = 0m, factor = 1m;
            foreach (var cf in annualCashFlows) { factor *= r; pv += cf / factor; }
            return Math.Round(pv, 2, MidpointRounding.ToEven);
        }

        /// <summary>Inventory is carried at the lower of cost and net realisable value (IAS 2). Returns per-unit NRV and total write-down.</summary>
        public static (decimal NrvPerUnit, decimal WriteDown) NrvWriteDown(decimal quantity, decimal unitCost, decimal estimatedSellingPrice, decimal costsToSell)
        {
            var nrv = estimatedSellingPrice - costsToSell;
            return (nrv, nrv >= unitCost ? 0m : Math.Round((unitCost - nrv) * quantity, 2, MidpointRounding.ToEven));
        }

        /// <summary>Credit exposure check (spec 17): open balance plus the new sale against the limit or an unexpired override.</summary>
        public static bool WithinCreditLimit(decimal limit, decimal? activeOverride, decimal openBalance, decimal newAmount) =>
            limit <= 0m /* 0 = no limit configured */ || openBalance + newAmount <= Math.Max(limit, activeOverride ?? 0m);

        /// <summary>Normalises a supplier invoice number so INV-001, inv 001 and "INV001" collide (spec 16 duplicate detection).</summary>
        public static string NormaliseInvoiceNo(string? no) =>
            new string((no ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()).TrimStart('0');

        public static bool LikelyDuplicate(string? noA, decimal amountA, DateTime dateA, string? noB, decimal amountB, DateTime dateB)
        {
            var sameNo = NormaliseInvoiceNo(noA).Length > 0 && NormaliseInvoiceNo(noA) == NormaliseInvoiceNo(noB);
            var sameAmountAndDate = amountA == amountB && dateA.Date == dateB.Date;
            return sameNo || sameAmountAndDate;
        }

        /// <summary>Withholding tax on the VAT-exclusive amount (spec 16 / 25.2). Rate is configuration.</summary>
        public static decimal Withholding(decimal vatExclusive, decimal ratePct) => Math.Round(vatExclusive * ratePct / 100m, 2, MidpointRounding.ToEven);

        /// <summary>13-week style forecast: receipts by due-date week from open receivables minus payments by due-date week from open payables.</summary>
        public static IReadOnlyList<(int Week, decimal Inflow, decimal Outflow, decimal Net, decimal Closing)> CashForecast(
            decimal openingCash, DateTime asOf, IEnumerable<(DateTime Due, decimal Amount)> receivables, IEnumerable<(DateTime Due, decimal Amount)> payables, int weeks = 13)
        {
            var rows = new List<(int, decimal, decimal, decimal, decimal)>();
            var rec = receivables.ToList(); var pay = payables.ToList(); var closing = openingCash;
            for (var w = 1; w <= weeks; w++)
            {
                var start = asOf.Date.AddDays((w - 1) * 7); var end = start.AddDays(7);
                // overdue items land in week 1
                bool InWeek(DateTime d) => (w == 1 && d.Date < end) || (d.Date >= start && d.Date < end);
                var inflow = rec.Where(r => InWeek(r.Due)).Sum(r => r.Amount);
                var outflow = pay.Where(p => InWeek(p.Due)).Sum(p => p.Amount);
                closing += inflow - outflow;
                rows.Add((w, inflow, outflow, inflow - outflow, closing));
            }
            return rows;
        }
    }
}
