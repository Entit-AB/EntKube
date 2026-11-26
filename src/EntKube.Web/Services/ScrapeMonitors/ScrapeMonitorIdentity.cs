using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Which monitors are the engine's own, and what the engine's monitor for a component or a data service is
/// called. Said once, here, for the planner and for anything outside it that has to ask (the CNPG and
/// Keycloak handovers, which switch an operator's or a chart's own monitor off only where the engine's
/// stands).
///
/// <para><b>Ours is the one label.</b> A monitor is the engine's when it carries
/// <see cref="OwnershipLabel"/> with the id of this cluster registration: that, and nothing else, is what the
/// planner keeps, rewrites and prunes by. The id and not just "yes", because one physical cluster can be
/// registered under two tenants, or in two EntKube installations, and each would otherwise prune the
/// other's. <see cref="ManagedByLabel"/> is written beside it for people and tools; it says who manages
/// the object, not for which registration, and does not make a monitor ours.</para>
///
/// <para><b>Neither is a selector's to set.</b> The engine adds to its monitors whatever a Prometheus selects
/// monitors by, except these two: a selector that demands another value for either
/// (<see cref="ConflictWith"/>) gets no monitor of the engine's, and the planner's row says why, rather than
/// one that calls itself somebody else's.</para>
/// </summary>
public static class ScrapeMonitorIdentity
{
    /// <summary>The label that makes a monitor the engine's, carrying the id of the cluster it was written for.</summary>
    public const string OwnershipLabel = "entkube.io/scrape-monitor";

    /// <summary>Who manages the object, by Kubernetes' recommended label.</summary>
    public const string ManagedByLabel = "app.kubernetes.io/managed-by";

    /// <summary>What the engine writes into <see cref="ManagedByLabel"/>.</summary>
    public const string ManagedBy = "entkube";

    /// <summary>Whether a monitor with these labels is the engine's own for this cluster registration.</summary>
    public static bool IsOurs(IReadOnlyDictionary<string, string>? labels, Guid clusterId)
        => labels is not null
           && labels.TryGetValue(OwnershipLabel, out string? owner)
           && owner == clusterId.ToString();

    /// <summary>
    /// Whether a live monitor is the engine's own, the whole rule the planner keeps, rewrites and prunes by: labelled
    /// for this registration (<see cref="IsOurs(IReadOnlyDictionary{string, string}?, Guid)"/>), or the unlabelled one
    /// that a monitor the engine wants takes over (<see cref="TakesOver"/>).
    /// </summary>
    /// <param name="monitor">The live monitor.</param>
    /// <param name="wanted">The monitors the engine wants, of which only one of the same name can take this one over.</param>
    /// <param name="clusterId">The cluster registration asking.</param>
    public static bool IsOurs(LiveMonitor monitor, IEnumerable<MonitorSpec> wanted, Guid clusterId)
        => IsOurs(monitor.Labels, clusterId) || wanted.Any(w => TakesOver(w, monitor));

    /// <summary>
    /// Whether the live monitor of a name is the one the engine wrote before it labelled what it writes, and so
    /// taken over by <paramref name="wanted"/>: the same name, no ownership label at all, and selecting exactly what
    /// <paramref name="wanted"/> selects. Only a monitor marked <see cref="MonitorSpec.TakesOverUnlabelled"/> takes
    /// one over (RabbitMQ's <c>{name}-metrics</c>, from the old "Enable metrics" button).
    /// </summary>
    public static bool TakesOver(MonitorSpec wanted, LiveMonitor existing)
        => wanted.TakesOverUnlabelled
           && existing.Identity == wanted.Identity
           && !existing.Labels.ContainsKey(OwnershipLabel)
           && System.Text.Json.Nodes.JsonNode.DeepEquals(existing.Spec["selector"], wanted.Selector.ToJson());

    /// <summary>
    /// Whether the engine's monitor <paramref name="wanted"/> stands among those <paramref name="listed"/> on the
    /// cluster registered as <paramref name="clusterId"/>: a monitor of its name that is ours by
    /// <see cref="IsOurs(LiveMonitor, IEnumerable{MonitorSpec}, Guid)"/>, the planner's own rule. This is what the
    /// CNPG and Keycloak handovers ask before they switch an operator's or a chart's own monitor off, so they hand
    /// over to exactly the monitors the planner keeps.
    /// </summary>
    public static bool Stands(IEnumerable<LiveMonitor> listed, MonitorSpec wanted, Guid clusterId)
        => listed.Any(m => m.Identity == wanted.Identity && IsOurs(m, [wanted], clusterId));

    /// <summary>The labels every monitor of the engine's carries, which no selector's labels are written over.</summary>
    public static IReadOnlyDictionary<string, string> Labels(Guid clusterId)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedByLabel] = ManagedBy,
            [OwnershipLabel] = clusterId.ToString(),
        };

    /// <summary>
    /// The label selector, as <c>kubectl -l</c> takes it, that lists the engine's monitors for this cluster
    /// registration and no others. What comes back is still to be checked with <see cref="IsOurs"/>: the
    /// answer decides, not the question.
    /// </summary>
    public static string ListSelector(Guid clusterId) => $"{OwnershipLabel}={clusterId}";

    /// <summary>
    /// The one of the engine's own labels that a Prometheus monitor selector demands another value of, or that
    /// it demands be absent; null where it demands none. A requirement this cannot evaluate is no conflict
    /// here: whether that Prometheus selects the monitor at all is then "cannot tell", which the planner
    /// already answers by changing nothing.
    /// </summary>
    public static string? ConflictWith(LabelSelector? selector, Guid clusterId)
    {
        if (selector is null) return null;

        IReadOnlyDictionary<string, string> ours = Labels(clusterId);

        foreach ((string key, string value) in ours)
        {
            if (selector.MatchLabels.TryGetValue(key, out string? demanded) && demanded != value) return key;

            foreach (LabelRequirement requirement in selector.MatchExpressions.Where(r => r.Key == key))
            {
                if (new LabelSelector(new Dictionary<string, string>(), [requirement]).Matches(ours) is false) return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The monitors the engine gives an installed component, as the catalog names them, whatever its status:
    /// the name does not depend on it, and an apply runs while it is Installing. None where the engine
    /// makes none (no namespace recorded, a chart it does not monitor, a component the cluster provides).
    /// </summary>
    public static IReadOnlyList<MonitorIdentity> For(InstalledComponent component)
        => Identities(MonitorsFor(component));

    /// <summary>The monitors the engine gives a data service, as the catalog names them, whatever its status.</summary>
    public static IReadOnlyList<MonitorIdentity> For(ManagedServiceSummary service)
        => Identities(MonitorsFor(service));

    /// <summary>The monitors the engine gives an installed component, whole, as the planner is given them; see <see cref="For(InstalledComponent)"/>.</summary>
    public static IReadOnlyList<MonitorSpec> MonitorsFor(InstalledComponent component)
        => [.. ScrapeTargetCatalog.PartsOf(component).Select(t => t.Monitor).OfType<MonitorSpec>()];

    /// <summary>The monitors the engine gives a data service, whole, as the planner is given them.</summary>
    public static IReadOnlyList<MonitorSpec> MonitorsFor(ManagedServiceSummary service)
        => [.. ScrapeTargetCatalog.PartsOf(service).Select(t => t.Monitor).OfType<MonitorSpec>()];

    private static IReadOnlyList<MonitorIdentity> Identities(IEnumerable<MonitorSpec> monitors)
        => [.. monitors.Select(m => m.Identity).Distinct()];
}
