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
        => Identities(ScrapeTargetCatalog.PartsOf(component));

    /// <summary>The monitors the engine gives a data service, as the catalog names them, whatever its status.</summary>
    public static IReadOnlyList<MonitorIdentity> For(ManagedServiceSummary service)
        => Identities(ScrapeTargetCatalog.PartsOf(service));

    private static IReadOnlyList<MonitorIdentity> Identities(IEnumerable<ScrapeTargetSpec> targets)
        => [.. targets.Select(t => t.Monitor?.Identity).OfType<MonitorIdentity>().Distinct()];
}
