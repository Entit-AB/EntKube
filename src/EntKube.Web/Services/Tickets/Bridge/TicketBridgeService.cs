using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>What a delivery did.</summary>
/// <param name="Accepted">Whether it was taken in at all.</param>
/// <param name="TicketNumber">The ticket it became or was added to.</param>
/// <param name="Created">True when this opened a ticket rather than adding to one.</param>
/// <param name="Detail">What happened, for the sender's log and for ours.</param>
public readonly record struct BridgeDelivery(
    bool Accepted, int? TicketNumber, bool Created, string Detail)
{
    public static BridgeDelivery Refused(string why) => new(false, null, false, why);
}

/// <summary>
/// Takes in a ticket raised in a customer's own system.
///
/// <para><b>What this will not do, and why that is the design.</b> §14.6 makes our
/// timestamps the record between the parties and §28 makes the monthly report binding
/// unless disputed within thirty days. A ticket whose clocks were driven from somebody
/// else's database could not survive that: a dispute would turn into an argument about
/// whose row was right, argued from a copy. So a delivery may <em>open</em> a ticket and
/// <em>add</em> to its history, and it may do nothing else.</para>
///
/// <list type="bullet">
/// <item><b>It cannot confirm a priority.</b> An arriving priority is a proposal (§14.3),
/// exactly as a customer's is in the portal. Confirming one is a written, reasoned act with
/// a person's name on it.</item>
/// <item><b>It cannot pause a clock.</b> §14.4's pause stops the resolution clock, which
/// lowers measured breach time and therefore our own §14.6 penalties. An integration that
/// quietly reduced our liability is one no customer should believe — so a remote system
/// going "on hold" is recorded as a note and nothing moves.</item>
/// <item><b>It cannot resolve or close.</b> §14.4 makes resolution something the customer
/// accepts and we record. A service desk closing its own copy is not that act.</item>
/// </list>
///
/// <para>All three read as restrictions and are really the same one: the bridge is a
/// channel, not a second owner of the record.</para>
/// </summary>
public class TicketBridgeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TicketService tickets,
    IEnumerable<IInboundTicketAdapter> adapters,
    ILogger<TicketBridgeService> logger)
{
    /// <summary>
    /// One delivery per connection at a time.
    ///
    /// <para>A sending system retrying while its first attempt is still in flight would
    /// otherwise have both look for an existing link, both find none, and both open a
    /// ticket — the unique index would then refuse the second link and leave a duplicate
    /// ticket with nothing pointing at it. Serialising per connection removes that window.
    /// It removes it <em>within this process</em>: across two instances the unique index is
    /// still the only guard, which is the honest limit of this and the same one the
    /// presence tracker has.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    /// <summary>
    /// Takes a delivery for a connection, having already established that the caller
    /// presented its secret.
    /// </summary>
    /// <param name="connectionId">Which connection the request was addressed to.</param>
    /// <param name="payload">The body, as sent.</param>
    /// <param name="at">Now, in UTC.</param>
    public async Task<BridgeDelivery> DeliverAsync(
        Guid connectionId, string payload, DateTime at, CancellationToken ct = default)
    {
        SemaphoreSlim gate = Gates.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            return await DeliverInternalAsync(connectionId, payload, at, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BridgeDelivery> DeliverInternalAsync(
        Guid connectionId, string payload, DateTime at, CancellationToken ct)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketBridgeConnection? connection = await db.TicketBridgeConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId, ct);

        if (connection is null)
        {
            return BridgeDelivery.Refused("No such connection.");
        }

        if (!connection.IsEnabled)
        {
            return await FailAsync(db, connection, at, "The connection is switched off.", ct);
        }

        IInboundTicketAdapter? adapter = adapters.FirstOrDefault(a => a.System == connection.System);

        if (adapter is null)
        {
            return await FailAsync(
                db, connection, at, $"Nothing here can read {connection.System}.", ct);
        }

        InboundTicketRead read = adapter.Read(payload, connection);

        if (!read.IsOk)
        {
            return await FailAsync(db, connection, at, read.Error ?? "Unreadable.", ct);
        }

        InboundTicketReport report = read.Report!;

        ExternalTicketLink? existing = await db.ExternalTicketLinks
            .Include(l => l.Ticket)
            .FirstOrDefaultAsync(
                l => l.TenantId == connection.TenantId
                     && l.System == connection.System
                     && l.Instance == connection.Instance
                     && l.ExternalId == report.ExternalId,
                ct);

        BridgeDelivery result = existing is null
            ? await RaiseAsync(db, connection, report, at, ct)
            : await AppendAsync(db, connection, existing, report, at, ct);

        connection.LastDeliveryAt = at;
        connection.LastError = null;
        connection.ConsecutiveFailures = 0;
        connection.UpdatedAt = at;
        await db.SaveChangesAsync(ct);

        return result;
    }

    /// <summary>Opens a ticket for something we have not seen before.</summary>
    private async Task<BridgeDelivery> RaiseAsync(
        ApplicationDbContext db, TicketBridgeConnection connection,
        InboundTicketReport report, DateTime at, CancellationToken ct)
    {
        // §14.3 counts from when the fault was reported, and for a ticket raised in the
        // customer's own system that happened there. A payload that did not say falls back
        // to now — later than the truth, and so never inventing SLA time we did not have.
        DateTime reportedAt = report.ReportedAt ?? at;

        if (reportedAt > at)
        {
            // Their clock is ahead of ours. Believing it would start a clock in the future.
            reportedAt = at;
        }

        Guid? appId = await ResolveAppAsync(db, connection, report, ct);

        TicketPriority? proposed = PriorityMap.Proposed(
            report.RawPriority, PriorityMap.Parse(connection.PriorityMap));

        Ticket ticket = await tickets.CreateAsync(
            connection.TenantId,
            connection.CustomerId,
            appId,
            report.Title,
            Describe(report),
            TicketChannel.Integration,
            proposed,
            reportedAt,
            requestedBy: report.RequestedBy,
            requestedByEmail: report.RequestedByEmail,
            ct: ct);

        db.ExternalTicketLinks.Add(new ExternalTicketLink
        {
            Id = Guid.NewGuid(),
            TenantId = connection.TenantId,
            TicketId = ticket.Id,
            ConnectionId = connection.Id,
            System = connection.System,
            Instance = connection.Instance,
            ExternalId = report.ExternalId,
            ExternalKey = report.ExternalKey,
            Url = report.Url,
            CreatedAt = at,
            LastSeenAt = at,
        });

        // Said out loud on the ticket rather than left to be inferred from the channel:
        // which system, which reference, and — where it matters most — that the priority
        // is theirs and has not been confirmed.
        await tickets.AddEventAsync(
            ticket.Id, TicketEventKind.Note,
            Provenance(connection, report, proposed),
            actor: Actor(connection), at: at, customerVisible: true, ct: ct);

        logger.LogInformation(
            "Ticket #{Number} raised from {System} {Key}.",
            ticket.Number, connection.System, report.ExternalKey ?? report.ExternalId);

        return new BridgeDelivery(true, ticket.Number, true, "Ticket opened.");
    }

    /// <summary>
    /// Adds a delivery about a ticket we already have to its history.
    ///
    /// <para>Only ever an event. A closure on their side is recorded as one and leaves the
    /// ticket open, because §14.4's resolution is ours to record and theirs to accept.</para>
    /// </summary>
    private async Task<BridgeDelivery> AppendAsync(
        ApplicationDbContext db, TicketBridgeConnection connection,
        ExternalTicketLink link, InboundTicketReport report, DateTime at, CancellationToken ct)
    {
        link.LastSeenAt = at;
        link.ExternalKey ??= report.ExternalKey;
        link.Url ??= report.Url;

        string detail = report.Kind switch
        {
            InboundTicketKind.Closed =>
                $"{Reference(connection, report)} was closed in {connection.Instance}. "
                + "That is their record; this ticket stays open until it is resolved here "
                + "and the customer accepts it (§14.4).",

            _ when !string.IsNullOrWhiteSpace(report.Note) =>
                $"From {Reference(connection, report)}: {report.Note!.Trim()}",

            _ => $"{Reference(connection, report)} was updated in {connection.Instance}.",
        };

        await tickets.AddEventAsync(
            link.TicketId, TicketEventKind.Note, detail,
            actor: Actor(connection), at: at, customerVisible: true, ct: ct);

        return new BridgeDelivery(
            true, link.Ticket?.Number, false,
            report.Kind == InboundTicketKind.Closed
                ? "Recorded. The ticket here stays open."
                : "Added to the ticket.");
    }

    /// <summary>
    /// Which application the ticket is about: what the payload hinted at, matched by name
    /// against this customer's applications, and otherwise the connection's default.
    ///
    /// <para>No application is a real answer and a visible one — the ticket then has no
    /// support window and says so. Guessing would produce a ticket measured against the
    /// wrong window, which looks entirely fine.</para>
    /// </summary>
    private static async Task<Guid?> ResolveAppAsync(
        ApplicationDbContext db, TicketBridgeConnection connection,
        InboundTicketReport report, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(report.AppHint))
        {
            string hint = report.AppHint.Trim();

            List<App> apps = await db.Apps.AsNoTracking()
                .Where(a => a.CustomerId == connection.CustomerId)
                .ToListAsync(ct);

            App? named = apps.FirstOrDefault(
                a => a.Name.Equals(hint, StringComparison.OrdinalIgnoreCase));

            if (named is not null)
            {
                return named.Id;
            }
        }

        return connection.DefaultAppId;
    }

    /// <summary>The ticket's description, with a line saying where it came from.</summary>
    private static string Describe(InboundTicketReport report)
    {
        string body = report.Description.Trim();

        return string.IsNullOrWhiteSpace(report.Url)
            ? body
            : $"{body}\n\n{report.Url}".Trim();
    }

    private static string Reference(TicketBridgeConnection connection, InboundTicketReport report) =>
        report.ExternalKey is { Length: > 0 } key
            ? key
            : $"{connection.System} {report.ExternalId}";

    /// <summary>
    /// Who the history says did this. A system, named — not a person, and not blank, which
    /// would read as one of ours having done it.
    /// </summary>
    private static string Actor(TicketBridgeConnection connection) =>
        $"{connection.System} ({connection.Instance})";

    private static string Provenance(
        TicketBridgeConnection connection, InboundTicketReport report, TicketPriority? proposed)
    {
        string origin =
            $"Raised in {connection.Instance} as {Reference(connection, report)}"
            + (report.RequestedBy is { Length: > 0 } who ? $", by {who}." : ".");

        string priority = report.RawPriority is { Length: > 0 } raw
            ? proposed is TicketPriority mapped
                ? $" Their priority \"{raw}\" is taken as a proposed {mapped}, to be confirmed "
                  + "here in writing (§14.3)."
                : $" Their priority \"{raw}\" is not mapped to one of §14.2's, so no priority "
                  + "has been proposed and the first assessment decides it."
            : " Their system proposed no priority.";

        return origin + priority;
    }

    /// <summary>
    /// Records a refused delivery on the connection, so a service desk sending us something
    /// unreadable is visible here rather than only in their own logs.
    /// </summary>
    private static async Task<BridgeDelivery> FailAsync(
        ApplicationDbContext db, TicketBridgeConnection connection,
        DateTime at, string why, CancellationToken ct)
    {
        connection.LastError = why;
        connection.ConsecutiveFailures++;
        connection.LastDeliveryAt = at;
        connection.UpdatedAt = at;
        await db.SaveChangesAsync(ct);

        return BridgeDelivery.Refused(why);
    }
}
