using EntKube.Web.Data;
using EntKube.Web.Services.Support;

namespace EntKube.Web.Services.Time;

/// <summary>Part of a work pass that fell in one §13 time category.</summary>
/// <param name="Category">Which category, and so which rate and which hour bank factor.</param>
/// <param name="WorkedHours">Time actually spent in this category.</param>
/// <param name="BilledHours">
/// Hours billed here after the pass was rounded up to a started hour. Sums, across a pass's
/// parts, to the pass's billed total.
/// </param>
public readonly record struct WorkPassPart(
    SupportTimeCategory Category,
    decimal WorkedHours,
    decimal BilledHours)
{
    /// <summary>Hours drawn from a Model A hour bank: the billed hours times the §13 factor.</summary>
    public decimal BankHours => BilledHours * Category.BankFactor();
}

/// <summary>
/// One work pass: contiguous work on one ticket, billed as a unit.
/// </summary>
/// <param name="TicketId">The ticket the pass belongs to, when it belongs to one.</param>
/// <param name="AppId">The application worked on.</param>
/// <param name="Kind">What kind of work it was.</param>
/// <param name="StartedAt">When the pass began.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="Parts">The pass split by time category.</param>
/// <param name="FreeHours">
/// Hours not billed because §10.3 includes the first thirty minutes of assessing an
/// incident in the base fee.
/// </param>
public readonly record struct WorkPass(
    Guid? TicketId,
    Guid? AppId,
    WorkKind Kind,
    DateTime StartedAt,
    DateTime EndedAt,
    IReadOnlyList<WorkPassPart> Parts,
    decimal FreeHours)
{
    public decimal WorkedHours => Parts.Sum(p => p.WorkedHours);

    public decimal BilledHours => Parts.Sum(p => p.BilledHours);

    /// <summary>Hours this pass takes out of a Model A hour bank.</summary>
    public decimal BankHours => Parts.Sum(p => p.BankHours);
}

/// <summary>
/// The §13 billing arithmetic: what a set of worked minutes actually costs.
///
/// <para><b>Three rules, and they interact.</b> Work is billed per started hour, but
/// contiguous work on one ticket is a single work pass and short bursts inside it are not
/// rounded separately — so three ten-minute touches on one ticket in an afternoon are one
/// hour, not three. The rate follows when the work happened, so a pass that crosses 17:00
/// is part ordinary and part evening rate. And a call-out bills at least two hours however
/// short it was.</para>
///
/// <para><b>Where the rounding lands.</b> When a pass is rounded up, the extra minutes are
/// added to the category the pass ended in — the work ran into that started hour, so that
/// is the hour it ran into. Splitting the remainder proportionally would be defensible too,
/// but it produces amounts nobody can check by hand.</para>
///
/// <para>Pure: no database, no clock. This decides what a customer is invoiced.</para>
/// </summary>
public static class WorkPassCalculator
{
    /// <summary>
    /// How long a break may be before contiguous work becomes two passes. §13 says
    /// "sammanhängande" without defining it; an hour is long enough to cover a meeting or
    /// lunch in the middle of one investigation, short enough that the afternoon's separate
    /// visit to the same ticket is its own started hour.
    /// </summary>
    public static readonly TimeSpan MaxGapWithinPass = TimeSpan.FromHours(1);

    /// <summary>
    /// The assessment §10.3 includes in the base fee: "Första bedömning av inkommen
    /// incident, upp till 30 minuter per incident."
    /// </summary>
    public static readonly TimeSpan FreeIncidentAssessment = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Turns raw entries into billable passes.
    /// </summary>
    /// <param name="entries">The worked stretches, in any order.</param>
    /// <param name="calloutTickets">
    /// Tickets whose work is a call-out under §13 — a P1 outside the bought window. Their
    /// passes bill at the callout category with a two-hour minimum.
    /// </param>
    public static IReadOnlyList<WorkPass> BuildPasses(
        IEnumerable<TimeEntry> entries, IReadOnlySet<Guid>? calloutTickets = null)
    {
        List<TimeEntry> ordered = [.. entries
            .Where(e => e.EndedAt > e.StartedAt)
            .OrderBy(e => e.StartedAt)
            .ThenBy(e => e.Id)];

        List<WorkPass> passes = [];

        // §10.3's free half hour is per incident, so it is consumed across whichever passes
        // that incident's assessment happens to fall into.
        Dictionary<Guid, TimeSpan> assessmentRemaining = [];

        foreach (List<TimeEntry> group in GroupIntoPasses(ordered))
        {
            bool callout = group[0].TicketId is Guid id
                           && calloutTickets?.Contains(id) == true;

            passes.Add(BuildPass(group, callout, assessmentRemaining));
        }

        return passes;
    }

    /// <summary>
    /// Groups entries into passes: same ticket, same kind, and no gap longer than
    /// <see cref="MaxGapWithinPass"/>. Entries with no ticket group by application instead,
    /// which is the closest thing to a ticket they have.
    /// </summary>
    private static IEnumerable<List<TimeEntry>> GroupIntoPasses(List<TimeEntry> ordered)
    {
        Dictionary<string, List<TimeEntry>> open = [];

        foreach (TimeEntry entry in ordered)
        {
            string key = $"{entry.TicketId?.ToString() ?? entry.AppId?.ToString() ?? "none"}|{entry.Kind}";

            if (open.TryGetValue(key, out List<TimeEntry>? current)
                && entry.StartedAt - current[^1].EndedAt <= MaxGapWithinPass)
            {
                current.Add(entry);
                continue;
            }

            if (current is not null)
            {
                yield return current;
            }

            open[key] = [entry];
        }

        foreach (List<TimeEntry> remaining in open.Values)
        {
            yield return remaining;
        }
    }

    private static WorkPass BuildPass(
        List<TimeEntry> group, bool callout, Dictionary<Guid, TimeSpan> assessmentRemaining)
    {
        TimeEntry first = group[0];

        // Worked time per category, in ticks. Integers all the way until the end: adding
        // fractional hours together drifts, and these numbers are invoiced.
        Dictionary<SupportTimeCategory, long> worked = [];
        List<SupportTimeCategory> order = [];
        long freeTicks = 0;

        foreach (TimeEntry entry in group)
        {
            TimeSpan billableSpan = entry.EndedAt - entry.StartedAt;
            DateTime from = entry.StartedAt;

            // §10.3: the first half hour of assessing an incident is in the base fee.
            if (entry.Kind == WorkKind.IncidentAssessment && entry.TicketId is Guid ticketId)
            {
                if (!assessmentRemaining.TryGetValue(ticketId, out TimeSpan left))
                {
                    left = FreeIncidentAssessment;
                }

                TimeSpan used = billableSpan < left ? billableSpan : left;
                assessmentRemaining[ticketId] = left - used;

                freeTicks += used.Ticks;
                from = entry.StartedAt + used;

                if (from >= entry.EndedAt)
                {
                    continue;
                }
            }

            foreach (BusinessCalendar.CategorySpan span in
                     BusinessCalendar.SplitByCategory(from, entry.EndedAt))
            {
                SupportTimeCategory category = callout ? SupportTimeCategory.Callout : span.Category;

                if (!worked.ContainsKey(category))
                {
                    order.Add(category);
                }

                worked[category] = worked.GetValueOrDefault(category) + span.Duration.Ticks;
            }
        }

        decimal free = Hours(freeTicks);
        long totalTicks = worked.Values.Sum();

        if (totalTicks <= 0)
        {
            return new WorkPass(
                first.TicketId, first.AppId, first.Kind,
                first.StartedAt, group[^1].EndedAt, [], free);
        }

        decimal totalWorked = Hours(totalTicks);

        // Per started hour, with the §13 floor for the category — two hours for an
        // call-out, one for everything else.
        decimal minimum = order.Max(c => c.MinimumBillableHours());
        decimal billedTotal = Math.Max(Math.Ceiling(totalWorked), minimum);

        List<WorkPassPart> parts = [];
        decimal allocated = 0m;

        for (int i = 0; i < order.Count; i++)
        {
            SupportTimeCategory category = order[i];
            decimal hours = Hours(worked[category]);

            // The pass ran into the started hour it ended in, so that is where the rounding
            // goes — not spread across categories it had already left. Taking the last part
            // as the remainder also makes the parts sum to the billed total exactly.
            decimal billed = i == order.Count - 1 ? billedTotal - allocated : hours;
            allocated += billed;

            parts.Add(new WorkPassPart(category, hours, billed));
        }

        return new WorkPass(
            first.TicketId, first.AppId, first.Kind,
            first.StartedAt, group[^1].EndedAt, parts, free);
    }

    /// <summary>Ticks as hours, rounded to the precision an invoice is read at.</summary>
    private static decimal Hours(long ticks) =>
        Math.Round((decimal)ticks / TimeSpan.TicksPerHour, 6, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether this kind of work draws on a Model A hour bank. §11.1 keeps on-boarding and
    /// development assignments out of it; they are billed separately.
    /// </summary>
    public static bool DrawsOnTimebank(WorkKind kind) =>
        kind is WorkKind.Management or WorkKind.IncidentAssessment;
}
