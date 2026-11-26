namespace ApexERP.Domain.Controls
{
    /// <summary>One period's share of a schedule (spec sections 4 and 5).</summary>
    public sealed record ScheduleLine(int Sequence, DateTime PeriodStart, decimal Amount);

    /// <summary>
    /// Straight-line allocation used by prepayments (spec 4.4), accruals reversal and ratable
    /// IFRS 15 revenue (spec 5.3). Amounts are rounded to the currency scale and the rounding
    /// difference is absorbed by the LAST line, so the schedule always sums exactly to the total.
    /// </summary>
    public static class RecognitionSchedule
    {
        public static IReadOnlyList<ScheduleLine> StraightLine(decimal total, DateTime start, int periods, int scale = 2)
        {
            if (periods <= 0) throw new ArgumentOutOfRangeException(nameof(periods), "A schedule needs at least one period.");
            if (total < 0) throw new ArgumentOutOfRangeException(nameof(total), "Total cannot be negative.");

            var first = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var each = Math.Round(total / periods, scale, MidpointRounding.ToEven);
            var lines = new List<ScheduleLine>(periods);
            var allocated = 0m;
            for (var i = 0; i < periods; i++)
            {
                var amount = i == periods - 1 ? total - allocated : each;
                allocated += amount;
                lines.Add(new ScheduleLine(i + 1, first.AddMonths(i), amount));
            }
            return lines;
        }

        /// <summary>Unrecognised (still prepaid / still a contract liability) balance after the given number of periods.</summary>
        public static decimal Unrecognised(decimal total, IReadOnlyList<ScheduleLine> schedule, int periodsRecognised) =>
            total - schedule.Take(Math.Max(0, periodsRecognised)).Sum(l => l.Amount);
    }
}
