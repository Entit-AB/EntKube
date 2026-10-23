using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>How much traffic a connection has carried, for the screen that lists them.</summary>
/// <param name="Connection">The connection itself.</param>
/// <param name="CustomerName">Whose it is.</param>
/// <param name="TicketsRaised">How many tickets have come in on it.</param>
/// <param name="LastTicketAt">When the most recent one arrived.</param>
public readonly record struct BridgeConnectionSummary(
    TicketBridgeConnection Connection, string CustomerName, int TicketsRaised, DateTime? LastTicketAt);

/// <summary>
/// Configuring the customers' ticketing systems.
///
/// <para><b>The secret is the awkward part of the screen.</b> It is shown once, when it is
/// issued, and never again — so the form has to be honest that copying it now is the only
/// chance, and reissuing it has to be easy, because somebody will lose it.</para>
/// </summary>
public class TicketBridgeAdmin(IDbContextFactory<SupportDbContext> dbFactory)
{
    public async Task<List<BridgeConnectionSummary>> ListAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<TicketBridgeConnection> connections = await db.TicketBridgeConnections
            .AsNoTracking()
            .Include(c => c.Customer)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Customer.Name)
            .ThenBy(c => c.Instance)
            .ToListAsync(ct);

        List<BridgeConnectionSummary> summaries = [];

        foreach (TicketBridgeConnection connection in connections)
        {
            IQueryable<ExternalTicketLink> links = db.ExternalTicketLinks
                .AsNoTracking()
                .Where(l => l.ConnectionId == connection.Id);

            summaries.Add(new BridgeConnectionSummary(
                connection,
                connection.Customer?.Name ?? "(unknown)",
                await links.CountAsync(ct),
                await links.MaxAsync(l => (DateTime?)l.CreatedAt, ct)));
        }

        return summaries;
    }

    public async Task<TicketBridgeConnection?> GetAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TicketBridgeConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
    }

    /// <summary>
    /// Creates or updates a connection. Every field is copied, because a field left out of
    /// this list saves silently and leaves the bridge using the old value.
    /// </summary>
    /// <param name="tenantId">
    /// Whose connection. The TenantId on the entity is the caller's to fill in, so the row is
    /// filed under this instead — and the customer is checked too, because the update copies
    /// CustomerId straight across and would otherwise point the connection at somebody
    /// else's customer.
    /// </param>
    /// <param name="settings">
    /// What to store. ⚠️ Two of these fields are not free-standing values:
    /// <c>DefaultAppId</c> is only meaningful together with <c>CustomerId</c>, and the pair
    /// (<c>System</c>, <c>Instance</c>) is half of the deduplication identity that existing
    /// <see cref="ExternalTicketLink"/> rows are keyed on. Both are reconciled below rather
    /// than copied blindly.
    /// </param>
    public async Task<TicketBridgeConnection> SaveAsync(
        Guid tenantId, TicketBridgeConnection settings, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Customers.AnyAsync(
                c => c.Id == settings.CustomerId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException(
                "Customer not found, or it is not this tenant's.");
        }

        // ⭐ The application has to belong to the customer, and nothing else checks it.
        // The form filters the application options by the chosen customer, so a stale id
        // from a previously chosen customer renders as no selection at all — it reads as
        // "None — decide per ticket" while the value underneath is somebody else's app.
        // Downstream there is no second chance: TicketBridgeService.ResolveAppAsync
        // constrains only its name-hint path to `a.CustomerId == connection.CustomerId`
        // and falls back to a bare `return connection.DefaultAppId;`, and
        // TicketService.CreateAsync takes the id without an owner check — so the ticket's
        // support window, clock start and §14.6 exposure come from the wrong contract.
        // TicketService.MoveAsync refuses exactly this pairing, and calls it "the one
        // combination that would be silently wrong rather than obviously wrong."
        if (settings.DefaultAppId is Guid defaultAppId
            && !await db.Apps.AnyAsync(
                a => a.Id == defaultAppId && a.CustomerId == settings.CustomerId, ct))
        {
            throw new InvalidOperationException(
                "That application belongs to another customer. Choose one of this "
                + "customer's, or none.");
        }

        TicketBridgeConnection? existing = settings.Id == Guid.Empty
            ? null
            : await db.TicketBridgeConnections.FirstOrDefaultAsync(
                  c => c.Id == settings.Id && c.TenantId == tenantId, ct)
              ?? throw new InvalidOperationException(
                  "Connection not found, or it is not this tenant's.");

        if (existing is null)
        {
            settings.Id = settings.Id == Guid.Empty ? Guid.NewGuid() : settings.Id;
            settings.TenantId = tenantId;
            settings.CreatedAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            db.TicketBridgeConnections.Add(settings);
            existing = settings;
        }
        else
        {
            // ⚠️ Before the new values are copied on, while the old key is still readable:
            // System and Instance are half of what deliveries are deduped on.
            await RekeyLinksAsync(db, existing, settings.System, settings.Instance, ct);

            existing.CustomerId = settings.CustomerId;
            existing.System = settings.System;
            existing.Instance = settings.Instance;
            existing.IsEnabled = settings.IsEnabled;
            existing.DefaultAppId = settings.DefaultAppId;
            existing.PriorityMap = settings.PriorityMap;
            existing.UpdatedAt = DateTime.UtcNow;

            // A fresh attempt deserves a clean slate, or a connection that failed once goes
            // on looking broken after it was fixed.
            existing.ConsecutiveFailures = 0;
            existing.LastError = null;
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Moves this connection's existing <see cref="ExternalTicketLink"/> rows onto the
    /// system and instance it is about to be saved with, so deduplication keeps matching.
    ///
    /// <para>⭐ <b>Editing the Instance used to re-key deduplication and silently split
    /// every open incident in two.</b> A delivery is matched against existing links on
    /// (<c>TenantId</c>, <c>System</c>, <c>Instance</c>, <c>ExternalId</c>) —
    /// <c>TicketBridgeService.DeliverInternalAsync</c> builds that lookup from the
    /// <i>connection's</i> current values, and the unique index in
    /// <c>SupportModel</c> enforces the same four columns. The link rows keep the instance
    /// copied when they were created. So correcting a typo, or following a customer to a
    /// renamed host, left every existing link at the old address: the next delivery about
    /// an already-mirrored incident matched nothing, took the raise branch, and opened a
    /// second ticket — with no index violation, because the instance differed. Sending
    /// systems resend on every field change, so that happens within minutes, and the
    /// customer ends up with two tickets for one incident, each with its own SLA clock and
    /// §14.6 penalty exposure, and the §28 report double-counting it.</para>
    ///
    /// <para>The mail side is the control: <see cref="ExternalTicketLink"/>'s own doc says
    /// "the external id is the identity to dedupe on, exactly as
    /// <c>InboundMailMessage.MessageId</c> is for mail", and <c>SupportMailService</c>
    /// dedupes on (<c>TenantId</c>, <c>MessageId</c>) alone — no field of the mailbox
    /// configuration is in that key, so editing a mailbox cannot re-key it. This class
    /// already uses the immutable <c>ConnectionId</c> everywhere it counts links
    /// (<c>ListAsync</c>, <c>DeleteAsync</c>); only raise-vs-append used the mutable pair.</para>
    ///
    /// <para>⚠️ <b>The deeper fix is outside this file.</b> Keying the delivery lookup on
    /// <c>ConnectionId</c> — which <c>ExternalTicketLink</c> already carries and which no
    /// edit changes — would remove the hazard rather than compensate for it. That lookup
    /// lives in <c>src/EntKube.Web/Services/Tickets/Bridge/TicketBridgeService.cs</c> and
    /// the index in <c>src/EntKube.Web/Data/Modules/SupportModel.cs</c>; changing either
    /// means a migration, so this keeps the two in step from the only place that edits
    /// them. <c>src/EntKube.Web/Data/TicketBridge.cs</c> still describes the link's
    /// Instance as "copied at the time", which is now "kept in step with the connection".</para>
    /// </summary>
    private static async Task RekeyLinksAsync(
        SupportDbContext db,
        TicketBridgeConnection existing,
        TicketSystem system,
        string instance,
        CancellationToken ct)
    {
        if (existing.System == system && existing.Instance == instance)
        {
            return;
        }

        List<ExternalTicketLink> links = await db.ExternalTicketLinks
            .Where(l => l.ConnectionId == existing.Id)
            .ToListAsync(ct);

        if (links.Count == 0)
        {
            return;
        }

        // ⚠️ The unique index spans the tenant, not the connection, so another connection
        // may already hold one of these external ids at the address this one is moving to.
        // Moving onto it would throw from SaveChangesAsync half-way through the save, with
        // nothing on screen to explain it.
        List<string> externalIds = [.. links.Select(l => l.ExternalId)];

        bool taken = await db.ExternalTicketLinks.AnyAsync(
            l => l.ConnectionId != existing.Id
                 && l.TenantId == existing.TenantId
                 && l.System == system
                 && l.Instance == instance
                 && externalIds.Contains(l.ExternalId),
            ct);

        if (taken)
        {
            throw new InvalidOperationException(
                $"Another connection already carries tickets from {system} at "
                + $"\"{instance}\" under the same reference. Moving this one there would "
                + "make two tickets claim the same incident.");
        }

        foreach (ExternalTicketLink link in links)
        {
            link.System = system;
            link.Instance = instance;
        }
    }

    /// <summary>
    /// Issues a new secret and returns it — the only time anybody sees it.
    ///
    /// <para>Reissuing immediately stops the old one working, which is the point: it is
    /// what you do when a secret has leaked, and a grace period would defeat that.</para>
    /// </summary>
    /// <param name="tenantId">
    /// Whose connection. ⚠️ This method both <b>returns a working secret</b> and stops the
    /// previous one working, so by id alone it handed the caller a credential for another
    /// tenant's bridge — which can then submit tickets as that customer's system — and broke
    /// the integration that was using the old one.
    /// </param>
    public async Task<string?> IssueSecretAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketBridgeConnection? connection = await db.TicketBridgeConnections
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);

        if (connection is null)
        {
            return null;
        }

        string secret = BridgeSecret.Issue();

        connection.SecretHash = BridgeSecret.Store(secret);
        connection.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return secret;
    }

    /// <summary>
    /// Removes a connection that has never taken a ticket in.
    ///
    /// <para>Refused once it has, and deliberately: the links are how a ticket says where it
    /// came from, and §14.6 makes that part of the record. A connection that should stop
    /// receiving is switched off, which keeps the history and stops the traffic.</para>
    /// </summary>
    public async Task<string?> DeleteAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketBridgeConnection? connection = await db.TicketBridgeConnections
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);

        if (connection is null)
        {
            return null;
        }

        int raised = await db.ExternalTicketLinks.CountAsync(l => l.ConnectionId == id, ct);

        if (raised > 0)
        {
            return $"{raised} ticket(s) came in on this connection and point at it. "
                   + "Switch it off instead — deleting it would erase where they came from.";
        }

        db.TicketBridgeConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return null;
    }
}
