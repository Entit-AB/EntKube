using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// Opens tickets for alerts that fire against an application under management.
///
/// <para><b>Why this is a bridge and not a conversion.</b> §14.3 says an issue our own
/// monitoring finds is registered by us, with the time of the alarm as the reporting time —
/// so the alert becomes a ticket's <em>origin</em>, and the alert incident goes on living
/// its own life as the thing that fires and resolves. The ticket then runs the §14.4
/// clocks, which an alert incident has no concept of.</para>
///
/// <para><b>The machine proposes; a person confirms.</b> The priority is derived from the
/// alert's severity and left unconfirmed, exactly as a customer's proposal would be. §14.3
/// makes confirming it a written act with reasons, and an alert label is not a judgement
/// about patient safety or whether a workaround exists.</para>
///
/// <para><b>Only applications under management.</b> A platform alert with no application
/// behind it, or one against an application with no Annex A, stays an incident. Opening
/// tickets nobody agreed to support would fill the queue with work that has no SLA, no
/// price and no customer.</para>
/// </summary>
public class AlertTicketBridge(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ContractService contracts,
    TicketService tickets,
    ILogger<AlertTicketBridge> logger)
{
    /// <summary>
    /// Opens a ticket for each firing alert that maps to a managed application and does not
    /// already have one. Returns the tickets created.
    ///
    /// <para>Best-effort: one alert that cannot be mapped must not stop the rest, because
    /// this runs inside the alert sweep and a thrown exception there costs the whole batch
    /// of notifications.</para>
    /// </summary>
    public async Task<List<Ticket>> OpenForIncidentsAsync(
        Guid tenantId, IReadOnlyList<AlertIncident> firing, CancellationToken ct = default)
    {
        if (firing.Count == 0)
        {
            return [];
        }

        List<Ticket> opened = [];

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        HashSet<Guid> alreadyTicketed = [.. await db.Tickets
            .Where(t => t.AlertIncidentId != null && t.TenantId == tenantId)
            .Select(t => t.AlertIncidentId!.Value)
            .ToListAsync(ct)];

        HashSet<Guid> clusterIds = [.. firing.Select(i => i.ClusterId)];

        // Deployment → app → customer, which is how an alert finds the agreement it falls
        // under. Matched on cluster and namespace, the same pairing the portal uses.
        var deployments = await db.AppDeployments
            .AsNoTracking()
            .Where(d => clusterIds.Contains(d.ClusterId))
            .Select(d => new { d.ClusterId, d.Namespace, d.AppId, d.App.CustomerId })
            .ToListAsync(ct);

        foreach (AlertIncident incident in firing)
        {
            if (alreadyTicketed.Contains(incident.Id))
            {
                continue;
            }

            try
            {
                string? ns = NamespaceOf(incident);
                if (ns is null)
                {
                    continue;
                }

                var match = deployments.FirstOrDefault(d =>
                    d.ClusterId == incident.ClusterId && d.Namespace == ns);

                if (match is null)
                {
                    continue;
                }

                // No Annex A means no SLA, no price and nothing agreed to respond to.
                ResolvedServiceLevel? level =
                    await contracts.ResolveServiceLevelAsync(match.AppId, incident.StartsAt, ct);

                if (level is null)
                {
                    continue;
                }

                Ticket ticket = await tickets.CreateAsync(
                    tenantId,
                    match.CustomerId,
                    match.AppId,
                    Title(incident),
                    Description(incident),
                    TicketChannel.Monitoring,
                    ProposedPriority(incident.Severity),

                    // §14.3: the reporting time is the time of the alarm.
                    incident.StartsAt,
                    requestedBy: "EntKube monitoring",
                    alertIncidentId: incident.Id,
                    ct: ct);

                opened.Add(ticket);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not open a ticket for alert incident {Incident} ({Alert}).",
                    incident.Id, incident.AlertName);
            }
        }

        return opened;
    }

    /// <summary>
    /// Notes on the ticket that the alert stopped firing. Deliberately not a resolution:
    /// §14.4 says a P1 or P2 is resolved when service is restored <em>or the customer has
    /// accepted a workaround</em>, and an alert going quiet is evidence of neither. Somebody
    /// closes the ticket.
    /// </summary>
    public async Task NoteIncidentResolvedAsync(
        IReadOnlyList<AlertIncident> resolved, DateTime at, CancellationToken ct = default)
    {
        if (resolved.Count == 0)
        {
            return;
        }

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        HashSet<Guid> ids = [.. resolved.Select(i => i.Id)];

        List<Ticket> open = await db.Tickets
            .Where(t => t.AlertIncidentId != null
                        && ids.Contains(t.AlertIncidentId!.Value)
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Rejected)
            .ToListAsync(ct);

        foreach (Ticket ticket in open)
        {
            db.TicketEvents.Add(new TicketEvent
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                Kind = TicketEventKind.Note,
                At = at,
                Actor = "EntKube monitoring",
                Detail = "The alert stopped firing. The ticket stays open until the service is "
                         + "confirmed restored or a workaround is accepted (§14.4).",
            });

            ticket.UpdatedAt = at;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A first guess at the priority, from the alert's severity. It is a proposal: §14.3
    /// makes the confirmation a written act, and no label knows whether a workaround exists
    /// or whether patient safety is at stake.
    /// </summary>
    public static TicketPriority ProposedPriority(string severity) =>
        severity.Trim().ToLowerInvariant() switch
        {
            "critical" or "crit" or "page" or "fatal" => TicketPriority.P1,
            "warning" or "warn" or "major" => TicketPriority.P3,
            _ => TicketPriority.P4,
        };

    /// <summary>The namespace the alert carries, which is what ties it to a deployment.</summary>
    public static string? NamespaceOf(AlertIncident incident)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(incident.LabelsJson);

            return doc.RootElement.TryGetProperty("namespace", out JsonElement ns)
                ? ns.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Title(AlertIncident incident) =>
        string.IsNullOrWhiteSpace(incident.Summary) ? incident.AlertName : incident.Summary;

    private static string Description(AlertIncident incident)
    {
        List<string> parts = [$"Opened automatically from the alert {incident.AlertName}."];

        if (!string.IsNullOrWhiteSpace(incident.Description))
        {
            parts.Add(incident.Description);
        }

        if (!string.IsNullOrWhiteSpace(incident.RunbookUrl))
        {
            parts.Add($"Runbook: {incident.RunbookUrl}");
        }

        return string.Join("\n\n", parts);
    }
}
