using EntKube.Web.Data;
using EntKube.Web.Services.Support;

namespace EntKube.Web.Services.Tickets;

/// <summary>A period during which a clock was stopped, reduced to the two instants that matter.</summary>
/// <param name="StartedAt">When the wait began.</param>
/// <param name="EndedAt">When the party answered, or null while still waiting.</param>
public readonly record struct ClockPause(DateTime StartedAt, DateTime? EndedAt);

/// <summary>Where a clock stands against its target.</summary>
/// <param name="Deadline">
/// When the target expires. Null when there is no target, or when an open pause means the
/// clock is not running — a ticket waiting on a third party is not late.
/// </param>
/// <param name="Elapsed">Time counted so far: inside the window, pauses excluded.</param>
/// <param name="Budget">The target itself, for display beside the elapsed time.</param>
/// <param name="MetAt">When the target was met, if it was.</param>
/// <param name="Breached">True when the target passed unmet, or was met after it passed.</param>
/// <param name="Paused">True when a pause is open and the clock is not moving.</param>
public readonly record struct ClockStatus(
    DateTime? Deadline,
    TimeSpan Elapsed,
    SlaBudget Budget,
    DateTime? MetAt,
    bool Breached,
    bool Paused);

/// <summary>
/// The §14.4 clocks: how much of a target a ticket has used, when it runs out, and whether
/// it was missed.
///
/// <para><b>Three rules do all the work.</b> Time counts only inside the application's
/// support window (§14.4), so a P1 raised on Friday evening under S1 is not late on
/// Saturday. The resolution clock stops while we are waiting on the customer or a third
/// party, and §14.4 says so explicitly, including when that party is only reachable during
/// office hours. And a reprioritisation restarts the targets from the moment it happened
/// (§14.3), so the clock is measured from the later of registration and the current
/// priority taking effect.</para>
///
/// <para><b>Pauses do not stop the response clock.</b> §14.4 pauses the resolution time by name
/// and says nothing about response time; §23 is broader but less specific. Since a missed
/// response time is the only thing that carries a penalty, the narrower reading is the one
/// that cannot be accused of excusing our own lateness — so response runs unpaused.</para>
///
/// <para>Pure, like the calendar underneath it: no database, no clock of its own. Callers
/// pass <c>now</c>.</para>
/// </summary>
public static class TicketClock
{
    /// <summary>
    /// Where the response target stands. Measured from the later of the clock start and the
    /// current priority taking effect, and stopped the moment the first response was made.
    /// </summary>
    public static ClockStatus Response(
        DateTime clockStartsAt,
        DateTime priorityEffectiveFrom,
        TicketPriority priority,
        SupportWindow window,
        DateTime? firstResponseAt,
        DateTime now)
    {
        DateTime from = Later(clockStartsAt, priorityEffectiveFrom);
        SlaBudget budget = TicketSla.Response(priority);

        return Evaluate(from, budget, window, [], firstResponseAt, now);
    }

    /// <summary>
    /// Where the resolution target stands, with the pause ledger subtracted. Stopped when the
    /// ticket was resolved.
    /// </summary>
    public static ClockStatus Resolution(
        DateTime clockStartsAt,
        DateTime priorityEffectiveFrom,
        TicketPriority priority,
        SupportWindow window,
        IReadOnlyList<ClockPause> pauses,
        DateTime? resolvedAt,
        DateTime now)
    {
        DateTime from = Later(clockStartsAt, priorityEffectiveFrom);
        SlaBudget budget = TicketSla.Resolution(priority);

        return Evaluate(from, budget, window, pauses, resolvedAt, now);
    }

    /// <summary>
    /// When §14.5 escalation to level 2 is due — a P1 unresolved after four hours, a P2 after
    /// sixteen — or null when it does not apply or the ticket is already resolved.
    /// </summary>
    public static DateTime? EscalationDue(
        DateTime clockStartsAt,
        DateTime priorityEffectiveFrom,
        TicketPriority priority,
        SupportWindow window,
        IReadOnlyList<ClockPause> pauses,
        DateTime? resolvedAt)
    {
        if (resolvedAt is not null)
        {
            return null;
        }

        SlaBudget budget = TicketSla.EscalationToLevelTwo(priority);

        return budget.Exists
            ? DeadlineFor(Later(clockStartsAt, priorityEffectiveFrom), budget, window, pauses)
            : null;
    }

    /// <summary>
    /// When the next status update falls due under §14.4 — hourly on a P1, every four hours
    /// on a P2 — or null when the priority has no fixed interval or the ticket is settled.
    ///
    /// <para><b>Counted from the last thing the customer was actually told</b>, not from
    /// registration. The obligation is that they are not left in silence, and it resets
    /// every time the silence is broken.</para>
    ///
    /// <para><b>Paused with the resolution clock</b>, which is the one judgement here.
    /// Elsewhere this subsystem takes the reading that does not excuse us — but the point
    /// of §14.4's updates is that the customer is not left wondering, and while a pause is
    /// theirs they are the one holding the information. Demanding hourly updates from us
    /// in the meantime would fill the queue with red and teach everybody to ignore the
    /// colour, which costs more than it protects.</para>
    /// </summary>
    public static DateTime? UpdateDue(
        DateTime lastUpdateAt,
        TicketPriority priority,
        SupportWindow window,
        IReadOnlyList<ClockPause> pauses,
        DateTime? settledAt)
    {
        if (settledAt is not null)
        {
            return null;
        }

        TimeSpan? interval = TicketSla.UpdateInterval(priority);

        return interval is null
            ? null
            : DeadlineFor(lastUpdateAt, new SlaBudget(interval, null), window, pauses);
    }

    /// <summary>
    /// Time counted against a target between two instants: window time, less any window time
    /// that fell inside a pause.
    /// </summary>
    public static TimeSpan CountedTime(
        DateTime from, DateTime to, SupportWindow window, IReadOnlyList<ClockPause> pauses)
    {
        if (to <= from)
        {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed = BusinessCalendar.OpenTimeBetween(from, to, window);

        foreach (ClockPause pause in pauses)
        {
            DateTime pauseStart = Later(pause.StartedAt, from);
            DateTime pauseEnd = Earlier(pause.EndedAt ?? to, to);

            if (pauseEnd > pauseStart)
            {
                elapsed -= BusinessCalendar.OpenTimeBetween(pauseStart, pauseEnd, window);
            }
        }

        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>
    /// When a budget starting at <paramref name="from"/> runs out, walking past any pauses.
    /// Null while a pause is open: the clock is not running, so there is no deadline to miss.
    /// </summary>
    public static DateTime? DeadlineFor(
        DateTime from, SlaBudget budget, SupportWindow window, IReadOnlyList<ClockPause> pauses)
    {
        if (!budget.Exists)
        {
            return null;
        }

        // A working-day target is a date, not an amount of window time. Each pause pushes it
        // out by the working days it covered — coarser than the hour-by-hour arithmetic
        // below, and the same unit the target itself is written in.
        if (budget.WorkingDays is int days)
        {
            DateTime due = BusinessCalendar.WorkingDaysDeadline(from, days);

            foreach (ClockPause pause in pauses.OrderBy(p => p.StartedAt))
            {
                if (pause.EndedAt is null)
                {
                    return null;
                }

                // Through ToLocal, not straight off the UTC instant: these are stored in
                // UTC and the agreement counts Swedish calendar days. A pause running
                // from Saturday 00:30 to Monday 00:30 in Stockholm covers one working
                // day; read as UTC dates the same instants are Friday to Sunday and
                // cover none, so the target would not move and we would be held to a
                // date the customer's own wait had already pushed past.
                int paused = SwedishHolidays.WorkingDaysBetween(
                    DateOnly.FromDateTime(BusinessCalendar.ToLocal(pause.StartedAt)),
                    DateOnly.FromDateTime(BusinessCalendar.ToLocal(pause.EndedAt.Value)));

                if (paused > 0)
                {
                    due = BusinessCalendar.WorkingDaysDeadline(due, paused);
                }
            }

            return due;
        }

        TimeSpan remaining = budget.WindowTime!.Value;
        DateTime cursor = from;

        foreach (ClockPause pause in pauses.OrderBy(p => p.StartedAt))
        {
            DateTime pauseStart = Later(pause.StartedAt, cursor);

            if (pause.EndedAt is not null && pause.EndedAt <= cursor)
            {
                continue;
            }

            if (pauseStart > cursor)
            {
                TimeSpan available = BusinessCalendar.OpenTimeBetween(cursor, pauseStart, window);

                if (available >= remaining)
                {
                    return BusinessCalendar.Deadline(cursor, remaining, window);
                }

                remaining -= available;
            }

            if (pause.EndedAt is null)
            {
                return null;
            }

            cursor = pause.EndedAt.Value;
        }

        return BusinessCalendar.Deadline(cursor, remaining, window);
    }

    private static ClockStatus Evaluate(
        DateTime from,
        SlaBudget budget,
        SupportWindow window,
        IReadOnlyList<ClockPause> pauses,
        DateTime? metAt,
        DateTime now)
    {
        bool paused = metAt is null && pauses.Any(p => p.EndedAt is null && p.StartedAt <= now);
        DateTime measureTo = metAt ?? now;
        TimeSpan elapsed = CountedTime(from, measureTo, window, pauses);

        if (!budget.Exists)
        {
            return new ClockStatus(null, elapsed, budget, metAt, false, paused);
        }

        DateTime? deadline = DeadlineFor(from, budget, window, pauses);
        bool breached = deadline is not null && measureTo > deadline;

        return new ClockStatus(deadline, elapsed, budget, metAt, breached, paused);
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;
}
