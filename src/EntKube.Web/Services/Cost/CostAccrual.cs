namespace EntKube.Web.Services.Cost;

/// <summary>One UTC day and the number of hours of a span that fell inside it.</summary>
public readonly record struct DaySlice(DateTime Day, decimal Hours);

/// <summary>
/// The period one sweep is entitled to bill for, and the period it cannot.
/// </summary>
/// <param name="Start">Start of the billable period.</param>
/// <param name="End">End of the billable period — the moment of measurement.</param>
/// <param name="GapStart">Start of the unbillable period, if any.</param>
public readonly record struct AccrualSpan(DateTime Start, DateTime End, DateTime GapStart)
{
    public decimal BillableHours => CostAccrual.HoursBetween(Start, End);

    /// <summary>
    /// Time that elapsed too far from the measurement to be priced by it. Not billed,
    /// and reported, so a month can say how much of itself it actually accounts for.
    /// </summary>
    public decimal GapHours => CostAccrual.HoursBetween(GapStart, Start);

    public bool HasAnything => BillableHours > 0m || GapHours > 0m;
}

/// <summary>
/// Turns a run rate into an accrual: how much money a measured period actually incurred,
/// and which day it belongs to.
///
/// <para>Kept pure — no database, no clock — because this is the arithmetic that decides
/// what a customer is charged over time, and it has to be checkable without standing
/// anything up. The same reasoning as <see cref="CostAllocation"/>, which decides who is
/// charged.</para>
/// </summary>
public static class CostAccrual
{
    /// <summary>
    /// The same 730-hour month the run rate is quoted on. Using it here as well is what
    /// makes the two agree: a namespace held steady for a 730-hour period accrues exactly
    /// its quoted monthly cost, rather than a figure that drifts from the one shown on the
    /// dashboard because the two used different definitions of a month.
    /// </summary>
    public const decimal HoursPerMonth = CostAllocation.HoursPerMonth;

    /// <summary>
    /// How far back one measurement is allowed to speak for. Beyond this the sweep did
    /// not observe the period and pricing it would be a guess — a guess that bills real
    /// money, so the excess is recorded as a gap instead.
    ///
    /// Three hours against an hourly sweep: long enough to absorb a restart, a slow
    /// sweep, or a missed cycle without leaving holes in the ledger, short enough that a
    /// weekend of downtime is reported as downtime rather than invoiced at whatever the
    /// fleet happened to look like on Monday morning.
    /// </summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromHours(3);

    /// <summary>
    /// Works out what a sweep at <paramref name="now"/> may bill for, given when the last
    /// one landed.
    ///
    /// <para>A first sweep (<paramref name="lastSampleAt"/> null) bills nothing. Nothing
    /// measured the time before it, and an opening balance conjured from the first
    /// reading would be indistinguishable in the ledger from a real one.</para>
    ///
    /// <para>When more time has passed than <see cref="MaxSpan"/>, the billable period is
    /// the <em>most recent</em> part of it — the hours closest to the measurement are the
    /// ones it can most defensibly speak for — and everything earlier becomes a gap.</para>
    /// </summary>
    public static AccrualSpan Measure(DateTime? lastSampleAt, DateTime now, TimeSpan? maxSpan = null)
    {
        TimeSpan cap = maxSpan ?? MaxSpan;

        if (lastSampleAt is not DateTime last)
        {
            return new AccrualSpan(now, now, now);
        }

        // A clock that went backwards, or a period already claimed by another writer.
        // Billing a negative span would credit money nobody asked for.
        if (last >= now)
        {
            return new AccrualSpan(now, now, now);
        }

        DateTime start = now - last > cap ? now - cap : last;
        return new AccrualSpan(start, now, last);
    }

    /// <summary>
    /// Splits a span across the UTC days it covers, so a period running over midnight is
    /// charged to both days in proportion rather than to whichever one the sweep happened
    /// to land in.
    /// </summary>
    public static IReadOnlyList<DaySlice> SplitByDay(DateTime start, DateTime end)
    {
        if (end <= start)
        {
            return [];
        }

        List<DaySlice> slices = [];
        DateTime cursor = start;

        while (cursor < end)
        {
            DateTime dayStart = cursor.Date;
            DateTime nextDay = dayStart.AddDays(1);
            DateTime sliceEnd = nextDay < end ? nextDay : end;

            slices.Add(new DaySlice(
                DateTime.SpecifyKind(dayStart, DateTimeKind.Utc),
                HoursBetween(cursor, sliceEnd)));

            cursor = sliceEnd;
        }

        return slices;
    }

    /// <summary>
    /// What a monthly run rate incurs over <paramref name="hours"/>.
    ///
    /// <para>Six decimal places, not two. An hour of a small namespace is routinely
    /// fractions of a cent, and rounding each accrual to money would round most of them
    /// to nothing — a table full of zeroes whose total was correct only for the largest
    /// tenants. Rounding to cents happens once, when a figure is displayed or totalled,
    /// never on the way in.</para>
    /// </summary>
    public static decimal Accrue(decimal monthlyCost, decimal hours)
    {
        if (hours <= 0m || monthlyCost == 0m)
        {
            return 0m;
        }

        return Math.Round(monthlyCost / HoursPerMonth * hours, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>Accrues a resource quantity — cores, GiB — over a period, as resource-hours.</summary>
    public static double AccrueQuantity(double quantity, decimal hours) =>
        quantity <= 0d || hours <= 0m ? 0d : quantity * (double)hours;

    /// <summary>Hours between two instants, never negative.</summary>
    public static decimal HoursBetween(DateTime from, DateTime to) =>
        to <= from ? 0m : (decimal)(to - from).TotalHours;

    /// <summary>
    /// The UTC midnight a moment belongs to. Days are UTC throughout rather than in a
    /// tenant's local time: the sweeps, the cursor and the report all speak UTC, and a
    /// ledger whose day boundary moved with a timezone would double-count or skip an
    /// hour twice a year.
    /// </summary>
    public static DateTime DayOf(DateTime moment) =>
        DateTime.SpecifyKind(moment.Date, DateTimeKind.Utc);
}
