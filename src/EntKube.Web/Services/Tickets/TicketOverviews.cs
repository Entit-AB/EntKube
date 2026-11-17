using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// The fixed views of a tenant's open tickets — Zammad's overviews, adapted to a queue that is
/// ordered by the agreement's clocks rather than by age.
/// </summary>
public enum TicketOverview
{
    /// <summary>Everything not yet closed, worst first. The queue as it has always been.</summary>
    Open,

    /// <summary>Open, and assigned to me.</summary>
    Mine,

    /// <summary>Open, and nobody has it.</summary>
    Unassigned,

    /// <summary>My tickets whose reminder has come due.</summary>
    MyReminders,

    /// <summary>Anybody's tickets whose reminder has come due.</summary>
    Reminders,

    /// <summary>
    /// Needs attention against the agreement now: a response, resolution or update deadline
    /// already missed, or one falling due within <see cref="TicketOverviews.EscalationLookahead"/>.
    /// </summary>
    Escalated,

    /// <summary>Tickets I follow without owning.</summary>
    Following,

    /// <summary>The tickets of colleagues who are away today and named me as their substitute.</summary>
    Covering,
}

/// <summary>Who is asking, for the overviews that are about "me".</summary>
/// <param name="UserId">Their account.</param>
/// <param name="Name">Their name as the ticket's assignee field holds it.</param>
public readonly record struct OverviewViewer(string? UserId, string? Name);

/// <summary>The agent dashboard (docs/helpdesk.md, phase 4), each figure beside the tenant's own.</summary>
public sealed record SupportDashboard(
    int MyOpen, int Unassigned, int MyRemindersDue, int MyEscalated, int Escalated,
    TimeSpan? MyFirstResponseToday, TimeSpan? FirstResponseToday,
    IReadOnlyList<(TicketChannel Channel, int Tickets)> ChannelsLastWeek,
    int ReopenedLastWeek,
    IReadOnlyList<(int Number, string Title, string Actor, string What, DateTime At)> Activity);

/// <summary>
/// Answers the overviews. Built on <see cref="TicketService.GetTenantQueueAsync"/>, so every
/// overview is a filter over the same SLA-evaluated queue and they cannot disagree about a
/// ticket's clocks.
/// </summary>
public class TicketOverviews(
    TicketService tickets, IDbContextFactory<SupportDbContext> dbFactory, OutOfOfficeService? outOfOffice = null)
{
    /// <summary>How far ahead "escalated" looks — Zammad's ten minutes.</summary>
    public static readonly TimeSpan EscalationLookahead = TimeSpan.FromMinutes(10);

    /// <summary>Every overview at once, with its tickets — what the queue's tabs show.</summary>
    public async Task<Dictionary<TicketOverview, List<TicketSlaStatus>>> AllAsync(
        Guid tenantId, OverviewViewer viewer, DateTime now, CancellationToken ct = default)
    {
        List<TicketSlaStatus> queue = await tickets.GetTenantQueueAsync(tenantId, now, ct);
        HashSet<Guid> following = await FollowingAsync(tenantId, viewer, ct);
        HashSet<string> covering = await CoveringAsync(tenantId, viewer, now, ct);

        return Enum.GetValues<TicketOverview>().ToDictionary(
            o => o,
            o => queue.Where(s => o == TicketOverview.Covering
                ? s.Ticket.AssigneeUserId is string holder && covering.Contains(holder)
                : Matches(o, s, viewer, following, now)).ToList());
    }

    /// <summary>One overview's tickets.</summary>
    public async Task<List<TicketSlaStatus>> GetAsync(
        Guid tenantId, TicketOverview overview, OverviewViewer viewer, DateTime now,
        CancellationToken ct = default)
    {
        List<TicketSlaStatus> queue = await tickets.GetTenantQueueAsync(tenantId, now, ct);
        HashSet<Guid> following = overview == TicketOverview.Following
            ? await FollowingAsync(tenantId, viewer, ct)
            : [];

        return [.. queue.Where(s => Matches(overview, s, viewer, following, now))];
    }

    /// <summary>Whether a ticket belongs in an overview. Pure, so it can be tested on its own.</summary>
    public static bool Matches(
        TicketOverview overview, TicketSlaStatus status, OverviewViewer viewer,
        IReadOnlySet<Guid> following, DateTime now)
    {
        Ticket t = status.Ticket;

        return overview switch
        {
            TicketOverview.Open => true,
            TicketOverview.Mine => IsMine(t, viewer),
            TicketOverview.Unassigned => t.Assignee is null,
            TicketOverview.MyReminders => IsMine(t, viewer) && ReminderDue(t, now),
            TicketOverview.Reminders => ReminderDue(t, now),
            TicketOverview.Escalated => IsEscalated(status, now),
            TicketOverview.Following => following.Contains(t.Id),
            _ => false,
        };
    }

    /// <summary>Who the viewer covers for today: the people away who named them as substitute.</summary>
    private async Task<HashSet<string>> CoveringAsync(Guid tenantId, OverviewViewer viewer, DateTime now, CancellationToken ct)
    {
        if (outOfOffice is null || viewer.UserId is null)
        {
            return [];
        }

        Dictionary<string, string> map = await outOfOffice.CoveringAsync(
            tenantId, DateOnly.FromDateTime(Support.BusinessCalendar.ToLocal(now)), ct);

        return [.. map.Where(kv => kv.Value == viewer.UserId).Select(kv => kv.Key)];
    }

    /// <summary>
    /// The dashboard, computed on read: the person's own figures beside the tenant's. First
    /// response time is measured the way §14.4 measures it — inside the support window — so the
    /// figure is the one the agreement would count.
    /// </summary>
    public async Task<SupportDashboard> DashboardAsync(Guid tenantId, OverviewViewer viewer, DateTime now, CancellationToken ct = default)
    {
        List<TicketSlaStatus> queue = await tickets.GetTenantQueueAsync(tenantId, now, ct);
        HashSet<Guid> none = [];

        bool Mine(TicketSlaStatus s) => Matches(TicketOverview.Mine, s, viewer, none, now);

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DateOnly today = DateOnly.FromDateTime(Support.BusinessCalendar.ToLocal(now));
        DateTime dayStart = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), Support.BusinessCalendar.SwedishTime);
        DateTime weekAgo = now.AddDays(-7);

        List<Ticket> answeredToday = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.FirstResponseAt != null && t.FirstResponseAt >= dayStart)
            .ToListAsync(ct);

        TimeSpan? Average(IEnumerable<Ticket> ts)
        {
            List<TimeSpan> spans = [.. ts.Select(t => TicketService.StatusOf(t, now).Response.Elapsed)];
            return spans.Count == 0 ? null : TimeSpan.FromTicks((long)spans.Average(s => s.Ticks));
        }

        var channels = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ReportedAt >= weekAgo)
            .GroupBy(t => t.Channel)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int reopened = await db.TicketEvents.AsNoTracking()
            .CountAsync(e => e.Ticket.TenantId == tenantId && e.At >= weekAgo && e.Detail.StartsWith("Reopened"), ct);

        var activity = await db.TicketEvents.AsNoTracking()
            .Where(e => e.Ticket.TenantId == tenantId && e.Actor != null && e.Actor != viewer.Name)
            .OrderByDescending(e => e.RecordedAt ?? e.At)
            .Take(15)
            .Select(e => new { e.Ticket.Number, e.Ticket.Title, e.Actor, e.Kind, e.At })
            .ToListAsync(ct);

        return new SupportDashboard(
            MyOpen: queue.Count(Mine),
            Unassigned: queue.Count(s => s.Ticket.Assignee is null),
            MyRemindersDue: queue.Count(s => Mine(s) && ReminderDue(s.Ticket, now)),
            MyEscalated: queue.Count(s => Mine(s) && IsEscalated(s, now)),
            Escalated: queue.Count(s => IsEscalated(s, now)),
            MyFirstResponseToday: Average(answeredToday.Where(t =>
                (viewer.UserId is not null && t.AssigneeUserId == viewer.UserId) || (viewer.Name is not null && t.Assignee == viewer.Name))),
            FirstResponseToday: Average(answeredToday),
            ChannelsLastWeek: [.. channels.OrderByDescending(c => c.Count).Select(c => (c.Key, c.Count))],
            ReopenedLastWeek: reopened,
            Activity: [.. activity.Select(a => (a.Number, a.Title, a.Actor!, a.Kind.ToString(), a.At))]);
    }

    /// <summary>Whether a ticket's reminder has come due.</summary>
    public static bool ReminderDue(Ticket t, DateTime now) => t.ReminderAt is DateTime r && r <= now;

    /// <summary>
    /// Whether any deadline the agreement sets is missed or about to be. A resolved ticket
    /// is not escalated: it is waiting for the customer to agree, not for us.
    /// </summary>
    public static bool IsEscalated(TicketSlaStatus s, DateTime now)
    {
        if (s.Ticket.Status == TicketStatus.Resolved)
        {
            return false;
        }

        DateTime soon = now + EscalationLookahead;

        bool Due(ClockStatus c) =>
            c.Budget.Exists && c.MetAt is null && !c.Paused
            && (c.Breached || c.Deadline is DateTime d && d <= soon);

        return Due(s.Response)
            || Due(s.Resolution)
            || s.UpdateDue is DateTime u && u <= soon;
    }

    /// <summary>
    /// Mine by account when the ticket has one, by name otherwise — tickets claimed before
    /// assignment carried an account only have the name.
    /// </summary>
    private static bool IsMine(Ticket t, OverviewViewer viewer) =>
        (viewer.UserId is not null && t.AssigneeUserId == viewer.UserId)
        || (t.AssigneeUserId is null && viewer.Name is not null && t.Assignee == viewer.Name);

    private async Task<HashSet<Guid>> FollowingAsync(Guid tenantId, OverviewViewer viewer, CancellationToken ct)
    {
        if (viewer.UserId is null)
        {
            return [];
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return [.. await db.TicketSubscriptions.AsNoTracking()
            .Where(s => s.UserId == viewer.UserId && s.Ticket.TenantId == tenantId)
            .Select(s => s.TicketId)
            .ToListAsync(ct)];
    }
}
