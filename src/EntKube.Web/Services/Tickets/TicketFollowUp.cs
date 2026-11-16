using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>What one pass over one tenant did.</summary>
public readonly record struct FollowUpPass(int RemindersSent, int Closed, int DigestsSent);

/// <summary>
/// The time-driven half of ticket handling, for one tenant at a time: reminders that have
/// come due, resolved tickets whose automatic close has arrived, and digests for the people
/// following a ticket.
///
/// <para><b>Each of the three is claimed before it is acted on</b>, with a conditional update
/// that only one caller can win — <c>ReminderSentAt</c> still null, the status still
/// Resolved, the digest cursor still where it was read. The sweep runs as singleton work, but
/// singleton placement is a configuration that can be wrong (see <c>SingletonWorkWitness</c>),
/// and a customer-visible close or a mail must not happen twice because of it.</para>
/// </summary>
public class TicketFollowUp(
    IDbContextFactory<SupportDbContext> dbFactory,
    TicketNotifier notifier,
    SupportDutyService duty,
    ILogger<TicketFollowUp> logger)
{
    /// <summary>
    /// How long a ticket must be quiet before its followers are sent what happened. Three
    /// notes in two minutes are one digest, not three mails.
    /// </summary>
    public static readonly TimeSpan DigestSettle = TimeSpan.FromMinutes(2);

    /// <summary>The longest an entry's text runs in a digest before it is cut.</summary>
    private const int DigestEntryLength = 600;

    public async Task<FollowUpPass> RunAsync(Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        // The roster is everybody with access to the tenant — read once, for the reminder's
        // recipient and for whether a follower is still a colleague at all.
        List<SupportTechnician> members = await duty.GetRosterAsync(tenantId, ct);

        int reminders = await RemindersAsync(tenantId, now, members, ct);
        int closed = await AutoCloseAsync(tenantId, now, ct);
        int digests = await DigestsAsync(tenantId, now, members, ct);

        return new FollowUpPass(reminders, closed, digests);
    }

    // ───────────────────────────── Reminders ─────────────────────────────

    private async Task<int> RemindersAsync(
        Guid tenantId, DateTime now, List<SupportTechnician> members, CancellationToken ct)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<Ticket> due = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId
                        && t.ReminderAt != null && t.ReminderAt <= now
                        && t.ReminderSentAt == null
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Rejected
                        && t.Status != TicketStatus.Merged)
            .ToListAsync(ct);

        int sent = 0;

        foreach (Ticket ticket in due)
        {
            int claimed = await db.Tickets
                .Where(t => t.Id == ticket.Id && t.TenantId == tenantId && t.ReminderSentAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.ReminderSentAt, now), ct);

            if (claimed != 1)
            {
                continue;
            }

            string what = ticket.ReminderNote is null ? "Reminder due." : $"Reminder due: {ticket.ReminderNote}";

            db.TicketEvents.Add(new TicketEvent
            {
                Id = Guid.NewGuid(), TicketId = ticket.Id, Kind = TicketEventKind.Reminder,
                At = now, Actor = null, Detail = what, CustomerVisible = false,
            });

            await db.SaveChangesAsync(ct);

            // To whoever has it. An unowned ticket's reminder still lands in the history, so
            // its followers hear of it in their digest and it shows in the reminder overview.
            string? to = members.FirstOrDefault(m => m.UserId == ticket.AssigneeUserId).Email;

            if (!string.IsNullOrWhiteSpace(to))
            {
                await notifier.NotifyColleagueAsync(
                    ticket, to, $"Reminder: {ticket.Title}",
                    $"The reminder you set on ticket #{ticket.Number}, “{ticket.Title}”, has come due."
                    + (ticket.ReminderNote is null ? "" : $"\n\n{ticket.ReminderNote}"),
                    ct);
            }

            sent++;
        }

        return sent;
    }

    // ───────────────────────────── Automatic close ─────────────────────────────

    private async Task<int> AutoCloseAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<Ticket> due = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId
                        && t.Status == TicketStatus.Resolved
                        && t.AutoCloseAt != null && t.AutoCloseAt <= now)
            .ToListAsync(ct);

        int closed = 0;

        foreach (Ticket ticket in due)
        {
            // Still resolved and still due: a reopen between the read and here wins.
            int claimed = await db.Tickets
                .Where(t => t.Id == ticket.Id && t.TenantId == tenantId
                            && t.Status == TicketStatus.Resolved
                            && t.AutoCloseAt != null && t.AutoCloseAt <= now)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.Status, TicketStatus.Closed)
                    .SetProperty(t => t.ClosedAt, now)
                    .SetProperty(t => t.AutoCloseAt, (DateTime?)null)
                    .SetProperty(t => t.ReminderAt, (DateTime?)null)
                    .SetProperty(t => t.UpdatedAt, now), ct);

            if (claimed != 1)
            {
                continue;
            }

            db.TicketEvents.Add(new TicketEvent
            {
                Id = Guid.NewGuid(), TicketId = ticket.Id, Kind = TicketEventKind.Closed,
                At = now, Actor = null,
                Detail = "Closed automatically: it was resolved, and it was not reopened in the time given.",
                CustomerVisible = true,
            });

            await db.SaveChangesAsync(ct);
            closed++;
        }

        return closed;
    }

    // ───────────────────────────── Digests ─────────────────────────────

    private async Task<int> DigestsAsync(
        Guid tenantId, DateTime now, List<SupportTechnician> members, CancellationToken ct)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<TicketSubscription> subscriptions = await db.TicketSubscriptions.AsNoTracking()
            .Include(s => s.Ticket)
            .Where(s => s.Ticket.TenantId == tenantId)
            .ToListAsync(ct);

        HashSet<string> colleagues = [.. members.Select(m => m.UserId)];
        int sent = 0;

        foreach (TicketSubscription s in subscriptions)
        {
            // Somebody who has left the tenant is no longer told about its customers' tickets.
            // Dropped here rather than when they left, because leaving happens in another
            // module that knows nothing about following tickets.
            if (!colleagues.Contains(s.UserId))
            {
                await db.TicketSubscriptions
                    .Where(x => x.TicketId == s.TicketId && x.UserId == s.UserId)
                    .ExecuteDeleteAsync(ct);
                continue;
            }

            DateTime cursor = s.NotifiedThrough;

            List<TicketEvent> pending = await db.TicketEvents.AsNoTracking()
                .Where(e => e.TicketId == s.TicketId && (e.RecordedAt ?? e.At) > cursor)
                .ToListAsync(ct);

            if (pending.Count == 0)
            {
                continue;
            }

            DateTime newest = pending.Max(e => e.RecordedAt ?? e.At);

            if (newest > now - DigestSettle)
            {
                // Still busy. The next pass picks it up once it has been quiet.
                continue;
            }

            int claimed = await db.TicketSubscriptions
                .Where(x => x.TicketId == s.TicketId && x.UserId == s.UserId && x.NotifiedThrough == cursor)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.NotifiedThrough, newest), ct);

            if (claimed != 1)
            {
                continue;
            }

            // Their own doings are not news to them.
            List<TicketEvent> news = [.. pending.Where(e => e.Actor != s.Name).OrderBy(e => e.At)];

            if (news.Count == 0)
            {
                continue;
            }

            bool delivered = await notifier.NotifyColleagueAsync(
                s.Ticket, s.Email, $"Update: {s.Ticket.Title}", Digest(s.Ticket, news), ct);

            if (delivered)
            {
                sent++;
            }
            else
            {
                logger.LogWarning(
                    "A digest for ticket #{Number} did not reach {Email}; it is not retried.",
                    s.Ticket.Number, s.Email);
            }
        }

        return sent;
    }

    /// <summary>The body of a digest: what happened, oldest first, in Swedish wall-clock time.</summary>
    public static string Digest(Ticket ticket, IReadOnlyList<TicketEvent> news)
    {
        StringBuilder body = new();
        body.Append($"What happened on ticket #{ticket.Number}, “{ticket.Title}” (now {ticket.Status}):\n");

        foreach (TicketEvent e in news)
        {
            string detail = e.Detail.Length > DigestEntryLength ? e.Detail[..DigestEntryLength] + "…" : e.Detail;

            body.Append('\n')
                .Append(BusinessCalendar.ToLocal(e.At).ToString("yyyy-MM-dd HH:mm"))
                .Append("  ")
                .Append(e.Actor ?? "EntKube")
                .Append(" — ")
                .Append(e.Kind)
                .Append(e.CustomerVisible ? "" : " (internal)")
                .Append('\n')
                .Append(detail)
                .Append('\n');
        }

        body.Append("\nYou are receiving this because you follow the ticket.");
        return body.ToString();
    }
}
