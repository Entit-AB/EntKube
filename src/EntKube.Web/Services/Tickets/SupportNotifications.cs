using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>The notification matrix's defaults and how to read it.</summary>
public static class NotificationPreferences
{
    /// <summary>
    /// What a person hears about before they choose: everything about their own tickets, new and
    /// escalating unassigned ones, and updates to tickets they follow — Zammad's defaults, give or
    /// take the groups this installation does not have.
    /// </summary>
    public static NotificationPreference Defaults(string userId, Guid tenantId) => new()
    {
        UserId = userId,
        TenantId = tenantId,
        NewTicket = NotifyScope.Mine | NotifyScope.Unassigned,
        Update = NotifyScope.Mine | NotifyScope.Followed,
        Assigned = NotifyScope.Mine,
        Reminder = NotifyScope.Mine,
        Escalation = NotifyScope.Mine | NotifyScope.Unassigned,
    };

    /// <summary>The mail flag for a kind of news.</summary>
    public static NotifyByMail MailFlag(NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket => NotifyByMail.NewTicket,
        NotificationKind.Update => NotifyByMail.Update,
        NotificationKind.Assigned => NotifyByMail.Assigned,
        NotificationKind.Reminder => NotifyByMail.Reminder,
        _ => NotifyByMail.Escalation,
    };

    public static NotifyScope For(NotificationPreference p, NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket => p.NewTicket,
        NotificationKind.Update => p.Update,
        NotificationKind.Assigned => p.Assigned,
        NotificationKind.Reminder => p.Reminder,
        _ => p.Escalation,
    };
}

/// <summary>A notification as the bell shows it, with where to go.</summary>
public readonly record struct NotificationView(
    Guid Id, Guid TenantId, string? TenantSlug, int? TicketNumber, NotificationKind Kind, string Text, DateTime CreatedAt, bool Seen);

/// <summary>
/// In-app notifications — the bell (docs/helpdesk.md, phase 4).
///
/// <para><b>Made from the ticket history by the follow-up sweep</b>, like the follower digests,
/// so anything that reaches the history — from the screen, the mailbox, a bridge, the API —
/// reaches the bell without a call at each of those places. The cursor advances by
/// compare-and-set, so with several instances each event is announced once.</para>
///
/// <para>One line per person per ticket per pass: three notes in a minute are "3 updates", not
/// three lines. The person who did something is not told about it.</para>
/// </summary>
/// <summary>How a notification is mailed. <see cref="TicketNotifierMailer"/> in the app; a recorder in tests.</summary>
public interface INotificationMailer
{
    Task<bool> SendAsync(Ticket ticket, string toEmail, string subject, string body, CancellationToken ct);
}

/// <summary>Notifications by mail through the tenant's own relay, as mentions are sent.</summary>
public sealed class TicketNotifierMailer(TicketNotifier notifier) : INotificationMailer
{
    public Task<bool> SendAsync(Ticket ticket, string toEmail, string subject, string body, CancellationToken ct) =>
        notifier.NotifyColleagueAsync(ticket, toEmail, subject, body, ct);
}

public class TicketNotificationService(
    IDbContextFactory<SupportDbContext> dbFactory, SupportDutyService duty, OutOfOfficeService outOfOffice,
    INotificationMailer mailer)
{
    /// <summary>How long a read notification is kept, and any notification at all.</summary>
    public static readonly TimeSpan KeepRead = TimeSpan.FromDays(1), KeepAny = TimeSpan.FromDays(90);

    // ───────────────────────────── Preferences ─────────────────────────────

    public async Task<NotificationPreference> GetPreferencesAsync(Guid tenantId, string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == userId, ct)
            ?? NotificationPreferences.Defaults(userId, tenantId);
    }

    public async Task SavePreferencesAsync(NotificationPreference input, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        NotificationPreference? row = await db.NotificationPreferences
            .FirstOrDefaultAsync(p => p.TenantId == input.TenantId && p.UserId == input.UserId, ct);

        if (row is null)
        {
            row = new NotificationPreference { UserId = input.UserId, TenantId = input.TenantId };
            db.NotificationPreferences.Add(row);
        }

        row.NewTicket = input.NewTicket;
        row.Update = input.Update;
        row.Assigned = input.Assigned;
        row.Reminder = input.Reminder;
        row.Escalation = input.Escalation;
        row.Mail = input.Mail;
        row.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── The bell ─────────────────────────────

    /// <summary>A person's latest notifications, across every tenant they belong to.</summary>
    public async Task<List<NotificationView>> ListAsync(string userId, int take = 30, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.UserNotifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new { n, Slug = db.Tenants.Where(t => t.Id == n.TenantId).Select(t => t.Slug).FirstOrDefault() })
            .ToListAsync(ct);

        return [.. rows.Select(r => new NotificationView(
            r.n.Id, r.n.TenantId, r.Slug, r.n.TicketNumber, r.n.Kind, r.n.Text, r.n.CreatedAt, r.n.SeenAt is not null))];
    }

    public async Task<int> UnreadCountAsync(string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserNotifications.CountAsync(n => n.UserId == userId && n.SeenAt == null, ct);
    }

    /// <summary>Marks one notification read, or all of them with null — only the person's own.</summary>
    public async Task MarkSeenAsync(string userId, Guid? id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime now = DateTime.UtcNow;

        await db.UserNotifications
            .Where(n => n.UserId == userId && n.SeenAt == null && (id == null || n.Id == id))
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.SeenAt, now), ct);
    }

    // ───────────────────────────── Making them ─────────────────────────────

    /// <summary>
    /// Turns the tenant's history since the last pass into notifications. Returns how many lines
    /// were written. A tenant seen for the first time starts from now: a new or restored server
    /// does not announce the past.
    /// </summary>
    public async Task<int> GenerateAsync(Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        NotificationCursor? cursor = await db.NotificationCursors.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        if (cursor is null)
        {
            db.NotificationCursors.Add(new NotificationCursor { TenantId = tenantId, Through = now });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another instance started the cursor first; it carries on from there.
            }

            return 0;
        }

        DateTime from = cursor.Through;

        List<TicketEvent> events = await db.TicketEvents.AsNoTracking()
            .Include(e => e.Ticket)
            .Where(e => e.Ticket.TenantId == tenantId && (e.RecordedAt ?? e.At) > from && (e.RecordedAt ?? e.At) <= now)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return 0;
        }

        DateTime through = events.Max(e => e.RecordedAt ?? e.At);

        int claimed = await db.NotificationCursors
            .Where(c => c.TenantId == tenantId && c.Through == from)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Through, through), ct);

        if (claimed != 1)
        {
            return 0;
        }

        Audience audience = await AudienceAsync(db, tenantId, events.Select(e => e.TicketId).Distinct().ToList(), now, ct);
        List<UserNotification> lines = [];

        foreach (IGrouping<Guid, TicketEvent> onTicket in events.GroupBy(e => e.TicketId))
        {
            Ticket ticket = onTicket.First().Ticket;

            foreach ((string userId, string name) in audience.Members)
            {
                List<TicketEvent> theirs = [.. onTicket.Where(e => e.Actor != name)];

                if (theirs.Count == 0)
                {
                    continue;
                }

                NotificationKind? kind = null;

                foreach (TicketEvent e in theirs)
                {
                    NotificationKind k = KindOf(e);

                    if (audience.Wants(userId, ticket, k) && (kind is null || Rank(k) > Rank(kind.Value)))
                    {
                        kind = k;
                    }
                }

                if (kind is NotificationKind chosen)
                {
                    lines.Add(Line(tenantId, userId, ticket, chosen, Describe(ticket, chosen, theirs), now));
                }
            }
        }

        db.UserNotifications.AddRange(lines);
        await db.SaveChangesAsync(ct);

        // By mail too, where chosen — after the cursor claim above, so once across instances.
        // An update to a followed ticket is left to the follower digest, which already mails it.
        foreach (UserNotification line in lines)
        {
            if (line.Kind == NotificationKind.Update && line.TicketId is Guid onTicket
                && audience.Followers.TryGetValue(onTicket, out HashSet<string>? following) && following.Contains(line.UserId))
            {
                continue;
            }

            await MailAsync(audience, line, events.First(e => e.TicketId == line.TicketId).Ticket, ct);
        }

        return lines.Count;
    }

    /// <summary>
    /// Mails one notification to its person, when they chose mail for its kind and have an address.
    /// A mail that cannot leave is not retried: the bell already has it.
    /// </summary>
    private async Task MailAsync(Audience audience, UserNotification line, Ticket ticket, CancellationToken ct)
    {
        NotificationPreference p = audience.Preferences.TryGetValue(line.UserId, out NotificationPreference? chosen)
            ? chosen
            : NotificationPreferences.Defaults(line.UserId, ticket.TenantId);

        if (!p.Mail.HasFlag(NotificationPreferences.MailFlag(line.Kind))
            || !audience.Emails.TryGetValue(line.UserId, out string? email) || string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        await mailer.SendAsync(ticket, email, line.Text,
            $"{line.Text}\n\nOpen the ticket in EntKube to see it. You chose to have this kind of notification mailed"
            + " (Support › My notifications & absence).", ct);
    }

    /// <summary>
    /// Announces deadlines missed or about to be — once per ticket, clock and deadline, claimed
    /// by inserting that row (a second instance's insert fails and it says nothing).
    /// </summary>
    public async Task<int> EscalationsAsync(Guid tenantId, IReadOnlyList<TicketSlaStatus> queue, DateTime now, CancellationToken ct = default)
    {
        List<(Ticket Ticket, EscalationClock Clock, DateTime Deadline)> due = [];
        DateTime soon = now + TicketOverviews.EscalationLookahead;

        foreach (TicketSlaStatus s in queue.Where(s => s.Ticket.Status != TicketStatus.Resolved))
        {
            if (s.Response is { MetAt: null, Paused: false, Deadline: DateTime r } && s.Response.Budget.Exists && r <= soon)
            {
                due.Add((s.Ticket, EscalationClock.Response, r));
            }
            else if (s.Resolution is { MetAt: null, Paused: false, Deadline: DateTime d } && s.Resolution.Budget.Exists && d <= soon)
            {
                due.Add((s.Ticket, EscalationClock.Resolution, d));
            }
            else if (s.UpdateDue is DateTime u && u <= soon)
            {
                due.Add((s.Ticket, EscalationClock.Update, u));
            }
        }

        if (due.Count == 0)
        {
            return 0;
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Audience audience = await AudienceAsync(db, tenantId, [.. due.Select(d => d.Ticket.Id)], now, ct);
        int written = 0;

        foreach ((Ticket ticket, EscalationClock clock, DateTime deadline) in due)
        {
            db.NotifiedEscalations.Add(new NotifiedEscalation { TicketId = ticket.Id, Clock = clock, Deadline = deadline, NotifiedAt = now });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            string what = clock switch
            {
                EscalationClock.Response => "response",
                EscalationClock.Resolution => "resolution",
                _ => "update to the customer",
            };
            string when = deadline <= now ? "was due" : "is due";
            string text = $"#{ticket.Number} {Clip(ticket.Title)} — {ticket.Priority} {what} {when} {BusinessCalendar.ToLocal(deadline):HH:mm}";

            List<UserNotification> told = [];

            foreach ((string userId, _) in audience.Members.Where(m => audience.Wants(m.UserId, ticket, NotificationKind.Escalation)))
            {
                UserNotification line = Line(tenantId, userId, ticket, NotificationKind.Escalation, text, now);
                db.UserNotifications.Add(line);
                told.Add(line);
                written++;
            }

            await db.SaveChangesAsync(ct);

            // After the NotifiedEscalation insert claimed this deadline: mailed once, by one instance.
            foreach (UserNotification line in told)
            {
                await MailAsync(audience, line, ticket, ct);
            }
        }

        return written;
    }

    /// <summary>Removes read notifications after a day, any after ninety, and presence older than five minutes.</summary>
    public async Task PruneAsync(Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DateTime readBefore = now - KeepRead, anyBefore = now - KeepAny, presenceBefore = now.AddMinutes(-5);

        await db.UserNotifications
            .Where(n => n.TenantId == tenantId && ((n.SeenAt != null && n.SeenAt < readBefore) || n.CreatedAt < anyBefore))
            .ExecuteDeleteAsync(ct);

        await db.TicketPresences.Where(p => p.TenantId == tenantId && p.SeenAt < presenceBefore).ExecuteDeleteAsync(ct);
    }

    // ───────────────────────────── Who hears what ─────────────────────────────

    private sealed class Audience
    {
        public required List<(string UserId, string Name)> Members { get; init; }
        public Dictionary<string, string> Emails { get; init; } = [];

        public required Dictionary<string, NotificationPreference> Preferences { get; init; }
        public required Dictionary<Guid, HashSet<string>> Followers { get; init; }

        /// <summary>Absent person → who covers for them today.</summary>
        public required Dictionary<string, string> Covering { get; init; }

        public bool Wants(string userId, Ticket ticket, NotificationKind kind)
        {
            NotificationPreference p = Preferences.TryGetValue(userId, out NotificationPreference? chosen)
                ? chosen
                : NotificationPreferences.Defaults(userId, ticket.TenantId);

            NotifyScope wanted = NotificationPreferences.For(p, kind);

            bool mine = ticket.AssigneeUserId == userId
                        || (ticket.AssigneeUserId is string holder && Covering.TryGetValue(holder, out string? sub) && sub == userId);

            return (wanted.HasFlag(NotifyScope.All))
                   || (wanted.HasFlag(NotifyScope.Mine) && mine)
                   || (wanted.HasFlag(NotifyScope.Unassigned) && ticket.AssigneeUserId is null)
                   || (wanted.HasFlag(NotifyScope.Followed) && Followers.TryGetValue(ticket.Id, out HashSet<string>? f) && f.Contains(userId));
        }
    }

    private async Task<Audience> AudienceAsync(SupportDbContext db, Guid tenantId, List<Guid> ticketIds, DateTime now, CancellationToken ct)
    {
        List<SupportTechnician> roster = await duty.GetRosterAsync(tenantId, ct);

        var follows = await db.TicketSubscriptions.AsNoTracking()
            .Where(s => ticketIds.Contains(s.TicketId))
            .Select(s => new { s.TicketId, s.UserId })
            .ToListAsync(ct);

        return new Audience
        {
            Members = [.. roster.Select(m => (m.UserId, m.Name))],
            Emails = roster.Where(m => !string.IsNullOrWhiteSpace(m.Email)).ToDictionary(m => m.UserId, m => m.Email!),
            Preferences = await db.NotificationPreferences.AsNoTracking()
                .Where(p => p.TenantId == tenantId).ToDictionaryAsync(p => p.UserId, ct),
            Followers = follows.GroupBy(f => f.TicketId).ToDictionary(g => g.Key, g => g.Select(x => x.UserId).ToHashSet()),
            Covering = await outOfOffice.CoveringAsync(tenantId, DateOnly.FromDateTime(BusinessCalendar.ToLocal(now)), ct),
        };
    }

    /// <summary>What kind of news an entry on the history is.</summary>
    public static NotificationKind KindOf(TicketEvent e) => e.Kind switch
    {
        TicketEventKind.Created => NotificationKind.NewTicket,
        TicketEventKind.Assigned => NotificationKind.Assigned,
        TicketEventKind.Reminder when e.Detail.StartsWith("Reminder due", StringComparison.Ordinal) => NotificationKind.Reminder,
        _ => NotificationKind.Update,
    };

    private static int Rank(NotificationKind k) => k switch
    {
        NotificationKind.Escalation => 5,
        NotificationKind.Assigned => 4,
        NotificationKind.Reminder => 3,
        NotificationKind.NewTicket => 2,
        _ => 1,
    };

    private static string Describe(Ticket ticket, NotificationKind kind, List<TicketEvent> events)
    {
        string head = $"#{ticket.Number} {Clip(ticket.Title)}";
        TicketEvent last = events.OrderBy(e => e.At).Last();

        return kind switch
        {
            NotificationKind.NewTicket => $"{head} — new ticket",
            NotificationKind.Assigned => $"{head} — {Clip(events.Last(e => e.Kind == TicketEventKind.Assigned).Detail)}",
            NotificationKind.Reminder => $"{head} — {Clip(events.Last(e => e.Kind == TicketEventKind.Reminder).Detail)}",
            _ => events.Count == 1
                ? $"{head} — {last.Actor ?? "EntKube"}: {Clip(last.Detail)}"
                : $"{head} — {events.Count} updates, last by {last.Actor ?? "EntKube"}",
        };
    }

    private static UserNotification Line(Guid tenantId, string userId, Ticket ticket, NotificationKind kind, string text, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = userId,
        TicketId = ticket.Id,
        TicketNumber = ticket.Number,
        Kind = kind,
        Text = text.Length > 500 ? text[..500] : text,
        CreatedAt = now,
    };

    private static string Clip(string s)
    {
        string one = s.ReplaceLineEndings(" ");
        return one.Length > 120 ? one[..120] + "…" : one;
    }
}

/// <summary>Out of office, and who covers (docs/helpdesk.md, phase 4).</summary>
public class OutOfOfficeService(IDbContextFactory<SupportDbContext> dbFactory, SupportDutyService duty)
{
    public async Task<List<OutOfOffice>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OutOfOffices.AsNoTracking().Where(o => o.TenantId == tenantId).OrderBy(o => o.From).ToListAsync(ct);
    }

    /// <summary>
    /// Records an absence. The substitute, when named, must be a member of the tenant and not the
    /// absent person — a substitute nobody can reach would silently swallow their notifications.
    /// </summary>
    public async Task<OutOfOffice> SetAsync(
        Guid tenantId, string userId, DateOnly from, DateOnly to, string? substituteUserId, string? note, CancellationToken ct = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The last day away is before the first.");
        }

        List<SupportTechnician> members = await duty.GetRosterAsync(tenantId, ct);

        if (members.All(m => m.UserId != userId))
        {
            throw new InvalidOperationException("Only a member of this tenant can be out of office here.");
        }

        if (substituteUserId is not null && (substituteUserId == userId || members.All(m => m.UserId != substituteUserId)))
        {
            throw new InvalidOperationException("The substitute must be another member of this tenant.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        OutOfOffice row = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, From = from, To = to,
            SubstituteUserId = substituteUserId, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };

        db.OutOfOffices.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Removes an absence — only the person's own, or any when <paramref name="userId"/> is null (an admin).</summary>
    public async Task<bool> ClearAsync(Guid tenantId, Guid id, string? userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OutOfOffices
            .Where(o => o.Id == id && o.TenantId == tenantId && (userId == null || o.UserId == userId))
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Absent person → their substitute, for everybody away on <paramref name="day"/> who named one.</summary>
    public async Task<Dictionary<string, string>> CoveringAsync(Guid tenantId, DateOnly day, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.OutOfOffices.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.From <= day && o.To >= day && o.SubstituteUserId != null)
            .Select(o => new { o.UserId, o.SubstituteUserId })
            .ToListAsync(ct);

        Dictionary<string, string> map = [];
        foreach (var r in rows)
        {
            map[r.UserId] = r.SubstituteUserId!;
        }

        return map;
    }
}

/// <summary>Who has a ticket open right now (docs/helpdesk.md, phase 4).</summary>
public class TicketPresenceService(IDbContextFactory<SupportDbContext> dbFactory)
{
    /// <summary>How recent a heartbeat must be to count as "here".</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(45);

    /// <summary>Records that <paramref name="userId"/> is on the ticket, and whether they are writing.</summary>
    public async Task HeartbeatAsync(Guid tenantId, Guid ticketId, string userId, string name, bool editing, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
        {
            return;
        }

        TicketPresence? row = await db.TicketPresences.FirstOrDefaultAsync(p => p.TicketId == ticketId && p.UserId == userId, ct);

        if (row is null)
        {
            db.TicketPresences.Add(new TicketPresence { TicketId = ticketId, UserId = userId, TenantId = tenantId, Name = name, Editing = editing });
        }
        else
        {
            row.Name = name;
            row.Editing = editing;
            row.SeenAt = DateTime.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two tabs of the same person raced to create the row; the other one did.
        }
    }

    /// <summary>Everybody else on the ticket within the window.</summary>
    public async Task<List<(string Name, bool Editing)>> OthersAsync(Guid tenantId, Guid ticketId, string? userId, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime since = now - Window;

        var rows = await db.TicketPresences.AsNoTracking()
            .Where(p => p.TicketId == ticketId && p.TenantId == tenantId && p.SeenAt >= since && p.UserId != userId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Name, p.Editing })
            .ToListAsync(ct);

        return [.. rows.Select(r => (r.Name, r.Editing))];
    }

    public async Task LeaveAsync(Guid ticketId, string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.TicketPresences.Where(p => p.TicketId == ticketId && p.UserId == userId).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// A personal calendar feed of ticket deadlines (docs/helpdesk.md, phase 4) — reminders and the
/// agreement's response and resolution deadlines of the person's own tickets, as iCalendar.
///
/// <para>The address carries a random token, the only credential a calendar app can present;
/// only its SHA-256 is stored. Resetting issues a new one and the old address stops working.</para>
/// </summary>
public class CalendarFeedService(IDbContextFactory<SupportDbContext> dbFactory, TicketService tickets)
{
    /// <summary>Issues a new token for the person in this tenant, replacing any old one. Returned once.</summary>
    public async Task<string> ResetAsync(Guid tenantId, string userId, CancellationToken ct = default)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.CalendarFeeds.Where(f => f.TenantId == tenantId && f.UserId == userId).ExecuteDeleteAsync(ct);
        db.CalendarFeeds.Add(new CalendarFeed { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, TokenHash = Hash(token) });
        await db.SaveChangesAsync(ct);

        return token;
    }

    public async Task<bool> HasFeedAsync(Guid tenantId, string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CalendarFeeds.AnyAsync(f => f.TenantId == tenantId && f.UserId == userId, ct);
    }

    public async Task RevokeAsync(Guid tenantId, string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.CalendarFeeds.Where(f => f.TenantId == tenantId && f.UserId == userId).ExecuteDeleteAsync(ct);
    }

    /// <summary>The feed for a token, or null for one that is not (or no longer) valid.</summary>
    /// <param name="stillAllowed">
    /// Whether the feed's owner may still see tickets in its tenant, asked every time it is read —
    /// a calendar app goes on polling long after somebody stops working support. Null asks nothing
    /// (callers that have already decided).
    /// </param>
    public async Task<string?> FeedAsync(
        string token, DateTime now,
        Func<Guid, string, CancellationToken, Task<bool>>? stillAllowed = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        CalendarFeed? feed;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            string hash = Hash(token);
            feed = await db.CalendarFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.TokenHash == hash, ct);
        }

        if (feed is null)
        {
            return null;
        }

        // The same nothing as an unknown address: whether a feed exists is not for a stranger to learn.
        if (stillAllowed is not null && !await stillAllowed(feed.TenantId, feed.UserId, ct))
        {
            return null;
        }

        List<TicketSlaStatus> mine = [.. (await tickets.GetTenantQueueAsync(feed.TenantId, now, ct))
            .Where(s => s.Ticket.AssigneeUserId == feed.UserId)];

        StringBuilder ics = new();
        ics.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//EntKube//Support deadlines//EN\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:Ticket deadlines\r\n");

        foreach (TicketSlaStatus s in mine)
        {
            Ticket t = s.Ticket;

            if (t.ReminderAt is DateTime r)
            {
                Event(ics, $"reminder-{t.Id}", r, $"Reminder: #{t.Number} {t.Title}", t.ReminderNote, now);
            }

            if (s.Response is { MetAt: null, Deadline: DateTime rd } && s.Response.Budget.Exists)
            {
                Event(ics, $"response-{t.Id}", rd, $"{t.Priority} response due: #{t.Number} {t.Title}", null, now);
            }

            if (s.Resolution is { MetAt: null, Paused: false, Deadline: DateTime sd } && s.Resolution.Budget.Exists)
            {
                Event(ics, $"resolution-{t.Id}", sd, $"{t.Priority} resolution due: #{t.Number} {t.Title}", null, now);
            }
        }

        ics.Append("END:VCALENDAR\r\n");
        return ics.ToString();
    }

    private static void Event(StringBuilder ics, string uid, DateTime at, string summary, string? description, DateTime now)
    {
        ics.Append("BEGIN:VEVENT\r\n")
            .Append("UID:").Append(uid).Append("@entkube\r\n")
            .Append("DTSTAMP:").Append(Stamp(now)).Append("\r\n")
            .Append("DTSTART:").Append(Stamp(at)).Append("\r\n")
            .Append("DTEND:").Append(Stamp(at.AddMinutes(15))).Append("\r\n")
            .Append("SUMMARY:").Append(Escape(summary)).Append("\r\n");

        if (!string.IsNullOrWhiteSpace(description))
        {
            ics.Append("DESCRIPTION:").Append(Escape(description)).Append("\r\n");
        }

        ics.Append("END:VEVENT\r\n");
    }

    private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>RFC 5545 text escaping, and no line breaks a calendar would read as a new property.</summary>
    public static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").ReplaceLineEndings("\\n");

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
