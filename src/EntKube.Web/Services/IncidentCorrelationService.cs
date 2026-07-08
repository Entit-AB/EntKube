using System.Text.Json;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// A set of active incidents that share a root cause — same alert firing across
/// many pods/instances on one cluster — collapsed into a single actionable group.
/// This is what prevents "a million alerts for the same error": the incident layer
/// already de-dupes byte-identical alerts by fingerprint, but a node failure or a
/// bad deploy still produces many *distinct* incidents; this groups those too.
/// </summary>
public sealed record IncidentGroup
{
    public required string Key { get; init; }
    public required string AlertName { get; init; }
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }
    /// <summary>Highest severity among the grouped incidents ("critical" &gt; "warning" &gt; "info").</summary>
    public required string Severity { get; init; }
    public required int Count { get; init; }
    public required DateTime EarliestStartsAt { get; init; }
    public required DateTime LatestStartsAt { get; init; }
    /// <summary>Distinct namespaces the incidents touch — the blast radius.</summary>
    public required IReadOnlyList<string> Namespaces { get; init; }
    public required IReadOnlyList<Guid> IncidentIds { get; init; }
    /// <summary>The single most-severe incident that represents the group (e.g. for one notification).</summary>
    public required Guid LeadIncidentId { get; init; }
    public bool AnyAcknowledged { get; init; }
    public string Summary { get; init; } = "";
    public string RunbookUrl { get; init; } = "";
}

/// <summary>
/// Groups a tenant's active incidents by root cause so downstream consumers
/// (the Operations Advisor today; notification suppression and the incidents UI
/// later) see one item per problem rather than one per firing alert instance.
/// </summary>
public class IncidentCorrelationService(IncidentService incidents)
{
    public async Task<List<IncidentGroup>> GetActiveGroupsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        List<AlertIncident> open = await incidents.GetActiveIncidentsForTenantAsync(tenantId, ct);
        return Correlate(open);
    }

    /// <summary>
    /// Correlates by (cluster, alert name): the same alert firing on 30 pods becomes
    /// one group of 30. Pure and static so it can be reused/unit-tested off any source.
    /// </summary>
    public static List<IncidentGroup> Correlate(IEnumerable<AlertIncident> incidents)
    {
        return incidents
            .GroupBy(i => (i.ClusterId, i.AlertName))
            .Select(g =>
            {
                // Most severe incident represents the group.
                AlertIncident lead = g.OrderBy(i => SeverityRank(i.Severity))
                                      .ThenByDescending(i => i.StartsAt)
                                      .First();

                List<string> namespaces = g
                    .Select(i => NamespaceOf(i.LabelsJson))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                return new IncidentGroup
                {
                    Key = $"{g.Key.ClusterId}:{g.Key.AlertName}",
                    AlertName = g.Key.AlertName,
                    ClusterId = g.Key.ClusterId,
                    ClusterName = lead.Cluster?.Name ?? "—",
                    Severity = lead.Severity,
                    Count = g.Count(),
                    EarliestStartsAt = g.Min(i => i.StartsAt),
                    LatestStartsAt = g.Max(i => i.StartsAt),
                    Namespaces = namespaces,
                    IncidentIds = g.Select(i => i.Id).ToList(),
                    LeadIncidentId = lead.Id,
                    AnyAcknowledged = g.Any(i => i.Status == IncidentStatus.Acknowledged),
                    Summary = string.IsNullOrWhiteSpace(lead.Summary) ? lead.AlertName : lead.Summary,
                    RunbookUrl = lead.RunbookUrl,
                };
            })
            .ToList();
    }

    private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2,
    };

    private static string? NamespaceOf(string labelsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(labelsJson);
            return doc.RootElement.TryGetProperty("namespace", out JsonElement ns) ? ns.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
