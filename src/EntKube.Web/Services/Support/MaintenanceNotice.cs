using EntKube.Web.Data;

namespace EntKube.Web.Services.Support;

/// <summary>
/// How much notice a maintenance window was given, measured in working days.
/// </summary>
/// <param name="Required">Working days of notice the agreement asks for.</param>
/// <param name="Given">Working days actually given, counted from the announcement.</param>
/// <param name="Deadline">
/// The last date the announcement could have been made. Past it, the window is short notice
/// however early in the day it was sent.
/// </param>
/// <param name="Exempt">
/// Whether the requirement does not apply — emergency maintenance, which is allowed to
/// happen at once and is reported afterwards rather than announced beforehand.
/// </param>
public readonly record struct NoticeGiven(
    int Required,
    int Given,
    DateOnly Deadline,
    bool Exempt)
{
    /// <summary>Whether enough notice was given, or none was owed.</summary>
    public bool IsSufficient => Exempt || Given >= Required;

    /// <summary>
    /// Working days short. Zero when the notice was sufficient, so a caller can print
    /// "two working days short" without checking twice.
    /// </summary>
    public int Shortfall => IsSufficient ? 0 : Required - Given;

    /// <summary>A phrase for a badge or a report line.</summary>
    public string Describe() => Exempt
        ? "Emergency — no notice owed"
        : IsSufficient
            ? $"{Given} working day{(Given == 1 ? "" : "s")}' notice"
            : $"{Given} working day{(Given == 1 ? "" : "s")}' notice — {Shortfall} short of {Required}";
}

/// <summary>
/// The notice owed before planned maintenance.
///
/// <para><b>Why this is computed and not merely stored.</b> Notice is owed in working days,
/// and a window announced on the Thursday before Easter gives less of it than the calendar
/// suggests. Counting the days by hand is where the mistake happens, and the mistake is
/// only found when a customer counts them back. This computes both directions — the days
/// given, and the date by which the announcement had to go out — from the same calendar
/// that decides SLA clocks and time categories.</para>
///
/// <para><b>Nothing here refuses a window.</b> Maintenance sometimes has to happen sooner
/// than the agreement would like, and an operator who knows that is not helped by a
/// disabled button. What the system owes is that the shortfall be visible at the moment
/// it is created and again in the monthly report, under the name of whoever scheduled
/// it — not that it be impossible.</para>
///
/// <para>Pure and clock-free, like the rest of the calendar arithmetic.</para>
/// </summary>
public static class MaintenanceNotice
{
    /// <summary>
    /// The notice the management agreement asks for before planned maintenance. Passed as
    /// a parameter everywhere below rather than read as a constant, because another
    /// customer can reasonably be sold a different number.
    /// </summary>
    public const int DefaultWorkingDays = 5;

    /// <summary>
    /// How much notice a window gives, from when it was announced to when it starts.
    /// </summary>
    /// <param name="announcedAt">When the customer was told. UTC.</param>
    /// <param name="startsAt">When the maintenance begins. UTC.</param>
    /// <param name="kind">Emergency maintenance owes no notice.</param>
    /// <param name="requiredWorkingDays">The agreed notice period.</param>
    public static NoticeGiven Assess(
        DateTime announcedAt,
        DateTime startsAt,
        MaintenanceKind kind = MaintenanceKind.Planned,
        int requiredWorkingDays = DefaultWorkingDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredWorkingDays);

        // Notice is counted in Swedish calendar days, so both ends are read in Swedish time.
        // A window announced at 23:30 UTC on a Sunday was announced on Monday in Stockholm,
        // and counting it from the UTC date would credit a working day nobody had.
        DateOnly announced = DateOnly.FromDateTime(BusinessCalendar.ToLocal(announcedAt));
        DateOnly start = DateOnly.FromDateTime(BusinessCalendar.ToLocal(startsAt));

        return new NoticeGiven(
            requiredWorkingDays,
            SwedishHolidays.WorkingDaysBetween(announced, start),
            SwedishHolidays.WorkingDaysBefore(start, requiredWorkingDays),
            kind == MaintenanceKind.Emergency);
    }

    /// <summary>
    /// How much notice a recorded window gave. The announcement is
    /// <see cref="MaintenanceWindow.AnnouncedAt"/> when someone recorded it — for a window
    /// entered here after the customer was already told — and otherwise the moment the
    /// window was created, which is the only announcement the system can vouch for.
    /// </summary>
    public static NoticeGiven Assess(
        MaintenanceWindow window,
        int requiredWorkingDays = DefaultWorkingDays) =>
        Assess(
            window.AnnouncedAt ?? window.CreatedAt,
            window.StartsAt,
            window.Kind,
            requiredWorkingDays);
}
