using EntKube.Web.Data;
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
public class TicketBridgeAdmin(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<List<BridgeConnectionSummary>> ListAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

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

    public async Task<TicketBridgeConnection?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TicketBridgeConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>
    /// Creates or updates a connection. Every field is copied, because a field left out of
    /// this list saves silently and leaves the bridge using the old value.
    /// </summary>
    public async Task<TicketBridgeConnection> SaveAsync(
        TicketBridgeConnection settings, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketBridgeConnection? existing = settings.Id == Guid.Empty
            ? null
            : await db.TicketBridgeConnections.FirstOrDefaultAsync(c => c.Id == settings.Id, ct);

        if (existing is null)
        {
            settings.Id = settings.Id == Guid.Empty ? Guid.NewGuid() : settings.Id;
            settings.CreatedAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            db.TicketBridgeConnections.Add(settings);
            existing = settings;
        }
        else
        {
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
    /// Issues a new secret and returns it — the only time anybody sees it.
    ///
    /// <para>Reissuing immediately stops the old one working, which is the point: it is
    /// what you do when a secret has leaked, and a grace period would defeat that.</para>
    /// </summary>
    public async Task<string?> IssueSecretAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketBridgeConnection? connection = await db.TicketBridgeConnections
            .FirstOrDefaultAsync(c => c.Id == id, ct);

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
    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketBridgeConnection? connection = await db.TicketBridgeConnections
            .FirstOrDefaultAsync(c => c.Id == id, ct);

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
