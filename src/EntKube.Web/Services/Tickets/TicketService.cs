using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>Where a ticket stands against every §14 clock at once.</summary>
/// <param name="Ticket">The ticket itself.</param>
/// <param name="Response">Response time — the only clock §14.6 attaches a penalty to.</param>
/// <param name="Resolution">Resolution time, which §14.4 calls a goal.</param>
/// <param name="EscalationDue">When §14.5 escalation to level 2 falls due, if it applies.</param>
/// <param name="IncidentReportDue">For a closed P1, when §14.6's written report is due.</param>
/// <param name="UpdateDue">
/// When §14.4's next status update falls due — hourly on a P1, four-hourly on a P2.
///
/// <para>The interval was in the SLA table from the beginning and nothing ever read it, so
/// the obligation existed on paper and nowhere else. A customer's experience of a P1 that
/// nobody is reminded to report on is silence.</para>
/// </param>
/// <param name="LastUpdateAt">When the customer was last told anything.</param>
public readonly record struct TicketSlaStatus(
    Ticket Ticket,
    ClockStatus Response,
    ClockStatus Resolution,
    DateTime? EscalationDue,
    DateTime? IncidentReportDue,
    DateTime? UpdateDue,
    DateTime LastUpdateAt)
{
    /// <summary>
    /// Whether this counts as a deviation for §14.6. Only a missed response time on a P1 or
    /// P2 does, only when it has not been excluded with a documented reason, and only when
    /// the agreement's times applied at all.
    ///
    /// <para>That last condition is §4.1's: before the start protocol is signed there is no
    /// guaranteed response to miss. It was written on the field and nowhere else, so an
    /// application still being on-boarded was measured against targets nobody had agreed
    /// to — and §14.6 turns a deviation into ten percent of the window fee.</para>
    /// </summary>
    public bool IsPenaltyDeviation =>
        Ticket.SlaApplied
        && !Ticket.ExcludedFromSla
        && Response.Breached
        && TicketSla.ResponseBreachCarriesPenalty(Ticket.Priority);

    /// <summary>
    /// Whether the agreement's times were only advisory when this arrived — §4.1's
    /// best-effort period. Shown rather than hidden: the clocks are still worth running,
    /// and a red one that carries no penalty should say which it is.
    /// </summary>
    public bool BestEffortOnly => !Ticket.SlaApplied;

    /// <summary>Whether §14.4's update is overdue as of the moment the status was taken.</summary>
    public bool UpdateOverdue(DateTime now) => UpdateDue is DateTime due && due <= now;
}

/// <summary>
/// Registers and moves tickets, and answers where each one stands against §14.
///
/// <para><b>Everything that changes a ticket also writes an event.</b> §14.6 makes the
/// portal's timestamps the record between the parties, so the history is not a convenience
/// log — it is the evidence that a response was made when we say it was. Nothing here
/// edits an event after the fact.</para>
/// </summary>
public class TicketService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ContractService contracts,
    // Required, not optional, and called from inside CreateAsync rather than left to
    // callers. A ticket arrives by three routes — the portal, the mailbox and monitoring —
    // and "remember to tell somebody" at three call sites is how it ends up done at none.
    // Tests that do not care pass a notifier that says nothing.
    TicketNotifier notifier)
{
    /// <summary>
    /// Registers a ticket and starts its clocks.
    ///
    /// <para>Three §14 rules are applied here rather than left to the caller: the support
    /// window and the clock start come from the application's Annex A and §9.1, the
    /// customer's proposed priority stands until the first assessment (§14.3), and a P1
    /// arriving outside the bought window is marked as a call-out (§13).</para>
    /// </summary>
    public async Task<Ticket> CreateAsync(
        Guid tenantId,
        Guid customerId,
        Guid? appId,
        string title,
        string description,
        TicketChannel channel,
        TicketPriority? proposedPriority,
        DateTime reportedAt,
        string? requestedBy = null,
        string? requestedByEmail = null,
        Guid? alertIncidentId = null,
        CancellationToken ct = default)
    {
        SupportWindow window = SupportWindow.S1;
        bool windowKnown = false;

        // §4.1 and §8: until the start protocol is signed, tickets are handled on a
        // best-effort basis with no guaranteed response. Assumed true for a ticket with no
        // application, which is where the agreement's terms cannot be looked up at all —
        // holding ourselves to them is the safe direction to be wrong in.
        bool slaApplied = true;

        if (appId is not null)
        {
            ResolvedServiceLevel? level = await contracts.ResolveServiceLevelAsync(appId.Value, reportedAt, ct);

            if (level is not null)
            {
                slaApplied = level.Value.SlaApplies;
            }

            if (level?.Window is not null)
            {
                window = level.Value.Window.Value;
                windowKnown = true;
            }
        }

        // §14.3: the customer proposes, we confirm at the first assessment. Until then their
        // view stands for P1 and P2, so the proposal is simply adopted and the confirmation
        // is a separate, recorded act.
        TicketPriority priority = proposedPriority ?? TicketPriority.P3;

        bool open = BusinessCalendar.IsOpen(reportedAt, window);

        Ticket ticket = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            AppId = appId,
            Title = title,
            Description = description,
            Channel = channel,
            Status = TicketStatus.New,
            ProposedPriority = proposedPriority,
            Priority = priority,
            PriorityEffectiveFrom = reportedAt,
            ReportedAt = reportedAt,

            // §9.1: registered on arrival, but the clock starts when the window next opens.
            ClockStartsAt = open ? reportedAt : BusinessCalendar.NextOpening(reportedAt, window),
            SupportWindow = window,
            SlaApplied = slaApplied,

            // §13: a P1 worked outside the bought window is a call-out, billed at the
            // callout rate with a two-hour minimum.
            IsCallout = priority == TicketPriority.P1 && !open,

            RequestedBy = requestedBy,
            RequestedByEmail = requestedByEmail,
            AlertIncidentId = alertIncidentId,
        };

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ticket.Number = await NextNumberAsync(db, tenantId, ct);

        ticket.Events.Add(Event(ticket.Id, TicketEventKind.Created, reportedAt, requestedBy,
            channel == TicketChannel.Monitoring
                ? "Opened by monitoring; reported at the time of the alarm (§14.3)."
                : $"Registered via {channel}."));

        if (proposedPriority is not null)
        {
            ticket.Events.Add(Event(ticket.Id, TicketEventKind.PriorityProposed, reportedAt, requestedBy,
                $"Customer proposed {proposedPriority}."));
        }

        if (!windowKnown && appId is not null)
        {
            // Registering the ticket matters more than knowing the terms, but a default that
            // nobody agreed to must not pass silently: S1 is the narrowest window, so it is
            // also the most generous assumption to make against ourselves.
            ticket.Events.Add(Event(ticket.Id, TicketEventKind.Note, reportedAt, null,
                "No support window in force for this application; assumed S1 for SLA purposes.",
                customerVisible: false));
        }

        if (!open)
        {
            ticket.Events.Add(Event(ticket.Id, TicketEventKind.Note, reportedAt, null,
                $"Arrived outside the {window} support window; response time counts from "
                + $"{ticket.ClockStartsAt:yyyy-MM-dd HH:mm} UTC (§9.1)."));
        }

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        // After the save, deliberately: the ticket exists whether or not anybody can be
        // reached, and AnnounceAsync never throws.
        await notifier.AnnounceAsync(
            ticket,
            window,
            TicketClock.Response(
                ticket.ClockStartsAt, ticket.PriorityEffectiveFrom, ticket.Priority, window,
                firstResponseAt: null, now: reportedAt).Deadline,
            ct);

        return ticket;
    }

    /// <summary>
    /// The first assessment: confirms the priority or changes it, with the written reason
    /// §14.3 requires. A change restarts the targets from this moment.
    /// </summary>
    public async Task<Ticket?> ConfirmPriorityAsync(
        Guid ticketId, TicketPriority priority, string reason, string? actor,
        DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return null;
        }

        bool changed = ticket.Priority != priority;

        if (changed)
        {
            // §14.3: the new priority's targets are counted from the reprioritisation, not
            // from registration.
            ticket.PriorityEffectiveFrom = at;
        }

        TicketPriority previous = ticket.Priority;
        ticket.Priority = priority;
        ticket.PriorityReason = reason;
        ticket.PriorityConfirmedBy = actor;
        ticket.PriorityConfirmedAt = at;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(
            ticket.Id,
            changed ? TicketEventKind.Reprioritised : TicketEventKind.PriorityConfirmed,
            at, actor,
            changed ? $"{previous} → {priority}. {reason}" : $"Confirmed as {priority}. {reason}"));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// Records the first response — confirmed, assessed, work begun (§14.4). Only the first
    /// one counts; calling it again leaves the original timestamp alone.
    /// </summary>
    public async Task<Ticket?> RecordResponseAsync(
        Guid ticketId, string? actor, DateTime at, string? note = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.FirstResponseAt is not null)
        {
            return ticket;
        }

        ticket.FirstResponseAt = at;
        ticket.Status = TicketStatus.InProgress;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Responded, at, actor,
            note ?? "First assessment made and work begun."));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// Stops the resolution clock because the ticket is waiting on somebody we do not
    /// control. §14.4 requires the wait to be documented with its time and the party.
    /// </summary>
    public async Task<TicketPause?> StartPauseAsync(
        Guid ticketId, WaitingOn waitingOn, string? party, string reason, string? actor,
        DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets
            .Include(t => t.Pauses)
            // §14.4's update clock reads the customer-visible events; without them
            // every ticket looks as though nobody has said anything since it arrived.
            .Include(t => t.Events)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        if (ticket is null || ticket.Pauses.Any(p => p.EndedAt is null))
        {
            // Already paused: a second open pause would subtract the same period twice.
            return null;
        }

        TicketPause pause = new()
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            StartedAt = at,
            WaitingOn = waitingOn,
            Party = party,
            Reason = reason,
            RecordedBy = actor,
        };

        ticket.Status = TicketStatus.Waiting;
        ticket.UpdatedAt = at;

        db.TicketPauses.Add(pause);
        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.PauseStarted, at, actor,
            $"Waiting on {Describe(waitingOn)}{(party is null ? "" : $" ({party})")}: {reason}"));

        await db.SaveChangesAsync(ct);
        return pause;
    }

    /// <summary>Restarts the resolution clock — the party answered.</summary>
    public async Task<Ticket?> EndPauseAsync(
        Guid ticketId, string? actor, DateTime at, string? note = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets
            .Include(t => t.Pauses)
            // §14.4's update clock reads the customer-visible events; without them
            // every ticket looks as though nobody has said anything since it arrived.
            .Include(t => t.Events)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        TicketPause? open = ticket?.Pauses.FirstOrDefault(p => p.EndedAt is null);
        if (ticket is null || open is null)
        {
            return ticket;
        }

        open.EndedAt = at;
        ticket.Status = ticket.FirstResponseAt is null ? TicketStatus.New : TicketStatus.InProgress;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.PauseEnded, at, actor,
            note ?? $"{Describe(open.WaitingOn)} answered; the clock resumes."));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// Resolves the ticket — service restored, or a workaround the customer accepted
    /// (§14.4). Any open pause is closed at the same instant, since nothing is being waited
    /// for any more.
    /// </summary>
    public async Task<Ticket?> ResolveAsync(
        Guid ticketId, string resolution, string? actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets
            .Include(t => t.Pauses)
            // §14.4's update clock reads the customer-visible events; without them
            // every ticket looks as though nobody has said anything since it arrived.
            .Include(t => t.Events)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        if (ticket is null)
        {
            return null;
        }

        foreach (TicketPause pause in ticket.Pauses.Where(p => p.EndedAt is null))
        {
            pause.EndedAt = at;
        }

        ticket.Status = TicketStatus.Resolved;
        ticket.ResolvedAt = at;
        ticket.Resolution = resolution;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Resolved, at, actor, resolution));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    public async Task<Ticket?> CloseAsync(
        Guid ticketId, string? actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return null;
        }

        ticket.Status = TicketStatus.Closed;
        ticket.ClosedAt = at;
        ticket.ResolvedAt ??= at;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Closed, at, actor, "Closed."));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// §14.4: the fault lies in a system outside the agreement. The investigation is
    /// documented, the time is still billable, and it does not count as an SLA deviation.
    /// </summary>
    public async Task<Ticket?> RejectAsync(
        Guid ticketId, string reason, string? actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return null;
        }

        ticket.Status = TicketStatus.Rejected;
        ticket.ClosedAt = at;
        ticket.Resolution = reason;
        ticket.ExcludedFromSla = true;
        ticket.SlaExclusionReason = $"Rejected under §14.4 — the fault lay outside the agreement. {reason}";
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Rejected, at, actor, reason));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// Reopens a ticket the customer does not accept as resolved.
    ///
    /// <para>§14.4 resolves a P1 or P2 only when service is restored <em>or the customer has
    /// accepted a workaround</em>, so the customer saying otherwise has to be able to undo
    /// it. The resolution clock resumes from where it stopped rather than restarting: the
    /// time already spent was still spent.</para>
    /// </summary>
    public async Task<Ticket?> ReopenAsync(
        Guid ticketId, string reason, string? actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return null;
        }

        ticket.Status = TicketStatus.InProgress;
        ticket.ResolvedAt = null;
        ticket.ClosedAt = null;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Note, at, actor,
            $"Reopened — the customer does not accept this as resolved. {reason}"));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>Adds a note or a status update to the record.</summary>
    public async Task AddEventAsync(
        Guid ticketId, TicketEventKind kind, string detail, string? actor, DateTime at,
        bool customerVisible = true, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        db.TicketEvents.Add(Event(ticketId, kind, at, actor, detail, customerVisible));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Notes that a ticket is not to be counted as a deviation, with the reason §14.6
    /// requires — waiting time, force majeure, or one of the §14.7 exemptions.
    /// </summary>
    public async Task<Ticket?> ExcludeFromSlaAsync(
        Guid ticketId, string reason, string? actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return null;
        }

        ticket.ExcludedFromSla = true;
        ticket.SlaExclusionReason = reason;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Note, at, actor,
            $"Excluded from SLA measurement: {reason}", customerVisible: false));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// Says who is working on a ticket, or clears it.
    ///
    /// <para>A claim rather than a dispatch: nobody is assigned work by the system. What
    /// this prevents is two people working the same fault without either knowing, which is
    /// the failure a queue without ownership actually produces — not idleness.</para>
    /// </summary>
    /// <param name="assignee">The person taking it, or null to put it back.</param>
    public async Task<Ticket?> AssignAsync(
        Guid ticketId, string? assignee, string actor, DateTime at,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        if (ticket is null)
        {
            return null;
        }

        string? previous = ticket.Assignee;
        string? next = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim();

        if (previous == next)
        {
            return ticket;
        }

        ticket.Assignee = next;

        // Not customer-visible: who at ENTIT is holding it is our business, and §14.1
        // promises the customer a named contact, not our rota.
        db.Set<TicketEvent>().Add(Event(
            ticket.Id, TicketEventKind.Note, at, actor,
            next is null
                ? $"Unassigned (was {previous})."
                : previous is null
                    ? $"Assigned to {next}."
                    : $"Reassigned from {previous} to {next}.",
            customerVisible: false));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    public async Task<Ticket?> GetAsync(Guid ticketId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Tickets
            .Include(t => t.Events)
            .Include(t => t.Pauses)
            .Include(t => t.AffectedApps)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    /// <summary>Where a ticket stands against every §14 clock.</summary>
    public static TicketSlaStatus StatusOf(Ticket ticket, DateTime now)
    {
        IReadOnlyList<ClockPause> pauses =
            [.. ticket.Pauses.Select(p => new ClockPause(p.StartedAt, p.EndedAt))];

        ClockStatus response = TicketClock.Response(
            ticket.ClockStartsAt, ticket.PriorityEffectiveFrom, ticket.Priority,
            ticket.SupportWindow, ticket.FirstResponseAt, now);

        ClockStatus resolution = TicketClock.Resolution(
            ticket.ClockStartsAt, ticket.PriorityEffectiveFrom, ticket.Priority,
            ticket.SupportWindow, pauses, ticket.ResolvedAt, now);

        DateTime? escalation = TicketClock.EscalationDue(
            ticket.ClockStartsAt, ticket.PriorityEffectiveFrom, ticket.Priority,
            ticket.SupportWindow, pauses, ticket.ResolvedAt);

        DateTime? reportDue =
            ticket.Priority == TicketPriority.P1 && ticket.ClosedAt is not null
                ? BusinessCalendar.WorkingDaysDeadline(
                    ticket.ClosedAt.Value, TicketSla.IncidentReportWorkingDays)
                : null;

        // The last thing the customer was actually told. Registration counts as the start
        // of the silence: from their side, reporting a fault and hearing nothing is the
        // case §14.4 exists for.
        DateTime lastUpdate = ticket.Events
            .Where(e => e.CustomerVisible)
            .Select(e => e.At)
            .DefaultIfEmpty(ticket.ClockStartsAt)
            .Max();

        DateTime? updateDue = TicketClock.UpdateDue(
            lastUpdate, ticket.Priority, ticket.SupportWindow, pauses,
            ticket.ResolvedAt ?? ticket.ClosedAt);

        return new TicketSlaStatus(
            ticket, response, resolution, escalation, reportDue, updateDue, lastUpdate);
    }

    /// <summary>
    /// The open queue for a customer, worst first, each with its clocks evaluated.
    /// </summary>
    public async Task<List<TicketSlaStatus>> GetOpenQueueAsync(
        Guid customerId, DateTime now, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<Ticket> open = await db.Tickets
            .Include(t => t.Pauses)
            // §14.4's update clock reads the customer-visible events; without them
            // every ticket looks as though nobody has said anything since it arrived.
            .Include(t => t.Events)
            .AsNoTracking()
            .Where(t => t.CustomerId == customerId
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Rejected)
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.ReportedAt)
            .ToListAsync(ct);

        return [.. open.Select(t => StatusOf(t, now))];
    }

    /// <summary>
    /// Tickets reported in a period, for the monthly report of §16.1 and the SLA figures of
    /// §14.6.
    /// </summary>
    public async Task<List<TicketSlaStatus>> GetForPeriodAsync(
        Guid customerId, DateTime from, DateTime to, DateTime now, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<Ticket> tickets = await db.Tickets
            .Include(t => t.Pauses)
            // §14.4's update clock reads the customer-visible events; without them
            // every ticket looks as though nobody has said anything since it arrived.
            .Include(t => t.Events)
            .AsNoTracking()
            .Where(t => t.CustomerId == customerId && t.ReportedAt >= from && t.ReportedAt < to)
            .OrderBy(t => t.ReportedAt)
            .ToListAsync(ct);

        return [.. tickets.Select(t => StatusOf(t, now))];
    }

    /// <summary>
    /// The next ticket number for a tenant. A gap-free sequence read from the table rather
    /// than a database sequence, because all three providers spell those differently; the
    /// unique index is what actually guarantees no two tickets share a number.
    /// </summary>
    private static async Task<int> NextNumberAsync(
        ApplicationDbContext db, Guid tenantId, CancellationToken ct)
    {
        int highest = await db.Tickets
            .Where(t => t.TenantId == tenantId)
            .Select(t => (int?)t.Number)
            .MaxAsync(ct) ?? 0;

        return highest + 1;
    }

    private static TicketEvent Event(
        Guid ticketId, TicketEventKind kind, DateTime at, string? actor, string detail,
        bool customerVisible = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            Kind = kind,
            At = at,
            Actor = actor,
            Detail = detail,
            CustomerVisible = customerVisible,
        };

    private static string Describe(WaitingOn waitingOn) => waitingOn switch
    {
        WaitingOn.Customer => "the customer",
        WaitingOn.CustomerVendor => "the customer's supplier",
        WaitingOn.ThirdParty => "a third party",
        _ => "another party",
    };
}
