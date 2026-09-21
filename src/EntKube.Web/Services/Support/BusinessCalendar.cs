using EntKube.Web.Data;

namespace EntKube.Web.Services.Support;

/// <summary>
/// The support windows of §9 and the time categories of §13, applied to instants.
///
/// <para><b>Everything commercial in the förvaltningsavtal rests on this.</b> Response and
/// resolution times are counted inside the chosen support window (§14.4), a ticket that
/// arrives outside it starts its clock at the next opening (§9.1), and what an hour is
/// billed at — and how many hours it draws from a timbank — depends on when it was worked
/// (§13). Get this wrong and an SLA breach is reported that did not happen, or one that did
/// is missed; §14.6 attaches 10% of the fönsteravgift to each of the latter.</para>
///
/// <para><b>UTC in, UTC out; Swedish time in the middle.</b> Callers pass and receive
/// <see cref="DateTimeKind.Utc"/> instants, matching the rest of EntKube. Classification
/// happens in Europe/Stockholm, because that is what §9 means by 08:00. None of the window
/// boundaries (05:00, 08:00, 17:00, 22:00, midnight) fall in the 02:00–03:00 hour Swedish
/// DST transitions move, so no boundary is ever an invalid or ambiguous local time — but
/// durations that span a transition are still correct, because the arithmetic is done on
/// the UTC instants rather than on the wall clock.</para>
///
/// <para>Pure and clock-free, like <c>CostAccrual</c>: no database, no
/// <c>DateTime.UtcNow</c>. This is the arithmetic that decides whether a deadline was
/// missed, and it has to be checkable without standing anything up.</para>
/// </summary>
public static class BusinessCalendar
{
    /// <summary>
    /// Ceiling on how far a deadline search will walk before giving up. Ten years of days:
    /// far beyond any SLA budget, close enough that a bug producing an unreachable deadline
    /// fails loudly instead of spinning.
    /// </summary>
    private const int MaxDeadlineSearchDays = 3_660;

    private static readonly TimeOnly FiveAm = new(5, 0);
    private static readonly TimeOnly EightAm = new(8, 0);
    private static readonly TimeOnly FivePm = new(17, 0);
    private static readonly TimeOnly TenPm = new(22, 0);

    /// <summary>
    /// When the Swedish working day ends. Targets counted in arbetsdagar expire here, and it
    /// is also S1's closing time, so the two cannot disagree.
    /// </summary>
    public static readonly TimeOnly WorkingDayEnd = FivePm;

    /// <summary>
    /// Swedish time — what §9 means by "08:00", and what the footnote confirms by naming
    /// CET/CEST. Resolved once; the IANA id works on Linux and on Windows through ICU, with
    /// the legacy Windows id kept as a fallback for hosts without it.
    /// </summary>
    public static readonly TimeZoneInfo SwedishTime = ResolveSwedishTimeZone();

    /// <summary>Whether an instant falls inside the given support window.</summary>
    public static bool IsOpen(DateTime instantUtc, SupportWindow window)
    {
        DateTime local = ToLocal(instantUtc);
        DaySpan? span = OpenSpanOn(DateOnly.FromDateTime(local), window);

        if (span is null)
        {
            return false;
        }

        TimeSpan timeOfDay = local.TimeOfDay;
        return timeOfDay >= span.Value.Start && timeOfDay < span.Value.End;
    }

    /// <summary>
    /// The instant the window is next open, which is <paramref name="instantUtc"/> itself
    /// when it already is. This is §9.1: a ticket that arrives outside the window is
    /// registered on arrival but its response time is counted from the next opening.
    /// </summary>
    public static DateTime NextOpening(DateTime instantUtc, SupportWindow window)
    {
        if (IsOpen(instantUtc, window))
        {
            return instantUtc;
        }

        DateTime local = ToLocal(instantUtc);
        DateOnly date = DateOnly.FromDateTime(local);

        for (int i = 0; i <= MaxDeadlineSearchDays; i++)
        {
            DaySpan? span = OpenSpanOn(date, window);
            if (span is not null)
            {
                DateTime opensAt = ToUtc(date, span.Value.Start);
                if (opensAt > instantUtc)
                {
                    return opensAt;
                }
            }

            date = date.AddDays(1);
        }

        throw new InvalidOperationException(
            $"Support window {window} never opens within {MaxDeadlineSearchDays} days of {instantUtc:O}.");
    }

    /// <summary>
    /// How much of the span between two instants fell inside the support window — the
    /// elapsed time §14.4 measures response and resolution against. Returns zero when
    /// <paramref name="toUtc"/> is at or before <paramref name="fromUtc"/>.
    /// </summary>
    public static TimeSpan OpenTimeBetween(DateTime fromUtc, DateTime toUtc, SupportWindow window)
    {
        if (toUtc <= fromUtc)
        {
            return TimeSpan.Zero;
        }

        // S4 never closes, so no day-by-day walk is needed — and doing one would only
        // introduce rounding where there is none.
        if (window == SupportWindow.S4)
        {
            return toUtc - fromUtc;
        }

        TimeSpan total = TimeSpan.Zero;
        DateOnly date = DateOnly.FromDateTime(ToLocal(fromUtc));
        DateOnly last = DateOnly.FromDateTime(ToLocal(toUtc));

        while (date <= last)
        {
            DaySpan? span = OpenSpanOn(date, window);
            if (span is not null)
            {
                DateTime openStart = ToUtc(date, span.Value.Start);
                DateTime openEnd = ToUtc(date, span.Value.End);

                DateTime overlapStart = openStart > fromUtc ? openStart : fromUtc;
                DateTime overlapEnd = openEnd < toUtc ? openEnd : toUtc;

                if (overlapEnd > overlapStart)
                {
                    total += overlapEnd - overlapStart;
                }
            }

            date = date.AddDays(1);
        }

        return total;
    }

    /// <summary>
    /// The instant by which a target expires, counting only time inside the support window.
    /// A P1 response target of two hours raised at 16:00 on a Friday under S1 falls at 09:00
    /// on the following Monday — or later still, if that Monday is a röd dag.
    ///
    /// <para>A zero budget returns the next opening, which is the right answer for "when does
    /// this clock start".</para>
    /// </summary>
    public static DateTime Deadline(DateTime fromUtc, TimeSpan budget, SupportWindow window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero, nameof(budget));

        if (window == SupportWindow.S4)
        {
            return fromUtc + budget;
        }

        TimeSpan remaining = budget;
        DateOnly date = DateOnly.FromDateTime(ToLocal(fromUtc));

        for (int i = 0; i <= MaxDeadlineSearchDays; i++)
        {
            DaySpan? span = OpenSpanOn(date, window);
            if (span is not null)
            {
                DateTime openEnd = ToUtc(date, span.Value.End);

                if (openEnd > fromUtc)
                {
                    DateTime openStart = ToUtc(date, span.Value.Start);
                    DateTime from = openStart > fromUtc ? openStart : fromUtc;
                    TimeSpan available = openEnd - from;

                    if (available >= remaining)
                    {
                        return from + remaining;
                    }

                    remaining -= available;
                }
            }

            date = date.AddDays(1);
        }

        throw new InvalidOperationException(
            $"A budget of {budget} does not fit within {MaxDeadlineSearchDays} days of {fromUtc:O} in window {window}.");
    }

    /// <summary>
    /// The §13 time category an instant falls in, from the clock alone. Night wins over the
    /// weekend, because §13 puts 22:00–05:00 at "samtliga dagar".
    ///
    /// <para>This never returns <see cref="SupportTimeCategory.Callout"/>: an utryckning
    /// depends on the ticket's priority and on which window was bought, neither of which is
    /// a property of the instant. Use the overload that takes them.</para>
    /// </summary>
    public static SupportTimeCategory CategoryAt(DateTime instantUtc)
    {
        DateTime local = ToLocal(instantUtc);
        TimeOnly time = TimeOnly.FromDateTime(local);

        if (time >= TenPm || time < FiveAm)
        {
            return SupportTimeCategory.Night;
        }

        if (!SwedishHolidays.IsWorkingDay(DateOnly.FromDateTime(local)))
        {
            return SupportTimeCategory.WeekendOrRedDay;
        }

        return time >= EightAm && time < FivePm
            ? SupportTimeCategory.Ordinary
            : SupportTimeCategory.EveningMorning;
    }

    /// <summary>
    /// The §13 category including utryckning: work on a P1 outside the application's chosen
    /// support window is a callout, whatever the clock says. Everything else falls back to
    /// the clock.
    /// </summary>
    public static SupportTimeCategory CategoryAt(DateTime instantUtc, SupportWindow window, bool isP1Callout) =>
        isP1Callout && !IsOpen(instantUtc, window)
            ? SupportTimeCategory.Callout
            : CategoryAt(instantUtc);

    /// <summary>
    /// The close of the working day <paramref name="workingDays"/> arbetsdagar after an
    /// instant — the deadline shape behind "inom fem (5) arbetsdagar" (§14.6 incident
    /// report), "tio (10) arbetsdagar" (§23 test acceptance, §18 subconsultant approval) and
    /// the P3/P4 targets in §14.4.
    ///
    /// <para>The target expires when the Swedish working day ends — <see cref="WorkingDayEnd"/>,
    /// 17:00 — on the Nth arbetsdag. Not at midnight, which would make a deliverable due at an
    /// hour nobody works, and not at the same clock time N days later.</para>
    /// </summary>
    public static DateTime WorkingDaysDeadline(DateTime fromUtc, int workingDays)
    {
        DateOnly start = DateOnly.FromDateTime(ToLocal(fromUtc));
        DateOnly due = SwedishHolidays.AddWorkingDays(start, workingDays);

        return ToUtc(due, WorkingDayEnd.ToTimeSpan());
    }

    /// <summary>
    /// The window's open hours on a local date, or null when it is closed all day.
    /// </summary>
    private static DaySpan? OpenSpanOn(DateOnly date, SupportWindow window)
    {
        bool workingDay = SwedishHolidays.IsWorkingDay(date);

        return window switch
        {
            SupportWindow.S1 => workingDay ? new DaySpan(EightAm.ToTimeSpan(), FivePm.ToTimeSpan()) : null,
            SupportWindow.S2 => workingDay ? new DaySpan(FiveAm.ToTimeSpan(), TenPm.ToTimeSpan()) : null,
            SupportWindow.S3 => new DaySpan(FiveAm.ToTimeSpan(), TenPm.ToTimeSpan()),
            SupportWindow.S4 => new DaySpan(TimeSpan.Zero, TimeSpan.FromDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, null),
        };
    }

    private static DateTime ToLocal(DateTime instantUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            instantUtc.Kind == DateTimeKind.Utc ? instantUtc : instantUtc.ToUniversalTime(),
            SwedishTime);

    /// <summary>
    /// A local date plus an offset from its midnight, as a UTC instant. The offset may reach
    /// or exceed 24 hours (S4's end-of-day), which rolls into the next date.
    /// </summary>
    private static DateTime ToUtc(DateOnly date, TimeSpan offsetFromMidnight)
    {
        DateTime midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        DateTime local = midnight + offsetFromMidnight;

        // None of the window boundaries land in a DST gap, but a future edit to the hours
        // could, and silently shifting one would misprice an hour rather than fail.
        if (SwedishTime.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, SwedishTime);
    }

    private static TimeZoneInfo ResolveSwedishTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    /// <summary>Open hours on one local day, as offsets from that day's midnight.</summary>
    private readonly record struct DaySpan(TimeSpan Start, TimeSpan End);
}
