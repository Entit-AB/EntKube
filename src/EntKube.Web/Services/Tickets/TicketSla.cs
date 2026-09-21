using EntKube.Web.Data;
using EntKube.Web.Services.Support;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// A §14.4 target, which is expressed in one of two units depending on the priority: hours
/// of open support window for P1 and P2, arbetsdagar for P3 and P4.
/// </summary>
/// <param name="WindowTime">Hours counted inside the support window, or null.</param>
/// <param name="WorkingDays">Whole arbetsdagar, or null.</param>
public readonly record struct SlaBudget(TimeSpan? WindowTime, int? WorkingDays)
{
    public bool Exists => WindowTime is not null || WorkingDays is not null;

    public static readonly SlaBudget None = new(null, null);

    public static SlaBudget Hours(double hours) => new(TimeSpan.FromHours(hours), null);

    public static SlaBudget Days(int days) => new(null, days);
}

/// <summary>
/// The §14.4 table: response, resolution and update intervals per priority.
///
/// <para>Kept as data in one place because these five rows are quoted in the monthly
/// report, drive the escalation thresholds in §14.5 and decide whether §14.6's penalty
/// applies. A second copy of them somewhere else is a copy that will be wrong.</para>
/// </summary>
public static class TicketSla
{
    /// <summary>Responstid: registration to confirmed, first assessment made, work begun.</summary>
    public static SlaBudget Response(TicketPriority priority) => priority switch
    {
        TicketPriority.P1 => SlaBudget.Hours(2),
        TicketPriority.P2 => SlaBudget.Hours(4),
        TicketPriority.P3 => SlaBudget.Days(1),
        TicketPriority.P4 => SlaBudget.Days(2),
        _ => SlaBudget.None,
    };

    /// <summary>
    /// Lösningstid, which §14.4 calls a goal rather than a guarantee — overrunning it does
    /// not trigger §14.6's penalty, only an explanation. P4 has none: "nästa release eller
    /// enligt överenskommelse" is not a deadline anything can be measured against.
    /// </summary>
    public static SlaBudget Resolution(TicketPriority priority) => priority switch
    {
        TicketPriority.P1 => SlaBudget.Hours(8),
        TicketPriority.P2 => SlaBudget.Hours(24),
        TicketPriority.P3 => SlaBudget.Days(5),
        _ => SlaBudget.None,
    };

    /// <summary>
    /// How often §14.4 requires a status update while the ticket is open. P3 is "at a change
    /// of status, at least every other working day" and P4 "at a change of status", neither
    /// of which is a fixed interval, so both are null.
    /// </summary>
    public static TimeSpan? UpdateInterval(TicketPriority priority) => priority switch
    {
        TicketPriority.P1 => TimeSpan.FromHours(1),
        TicketPriority.P2 => TimeSpan.FromHours(4),
        _ => null,
    };

    /// <summary>
    /// When §14.5 escalates to level 2: a P1 unresolved after four hours, a P2 after sixteen.
    /// Counted the same way as the resolution clock — inside the window, pauses excluded.
    /// </summary>
    public static SlaBudget EscalationToLevelTwo(TicketPriority priority) => priority switch
    {
        TicketPriority.P1 => SlaBudget.Hours(4),
        TicketPriority.P2 => SlaBudget.Hours(16),
        _ => SlaBudget.None,
    };

    /// <summary>
    /// Only a missed <em>response</em> time on a P1 or P2 carries §14.6's penalty of 10% of
    /// the fönsteravgift. An overrun resolution time is a missed goal, not a breach.
    /// </summary>
    public static bool ResponseBreachCarriesPenalty(TicketPriority priority) =>
        priority is TicketPriority.P1 or TicketPriority.P2;

    /// <summary>
    /// The number of response-time breaches §14.6 pays a penalty for in a calendar year.
    /// </summary>
    public const int MaxPenaltiesPerYear = 3;

    /// <summary>The penalty, as a fraction of the month's fönsteravgift (§14.6).</summary>
    public const decimal PenaltyFractionOfWindowFee = 0.10m;

    /// <summary>
    /// More than this many P1–P2 deviations in a quarter obliges us to produce an åtgärdsplan
    /// for the next quarterly meeting (§14.6).
    /// </summary>
    public const int DeviationsBeforeActionPlan = 2;

    /// <summary>Working days after closing a P1 in which §14.6 requires the written report.</summary>
    public const int IncidentReportWorkingDays = 5;
}
