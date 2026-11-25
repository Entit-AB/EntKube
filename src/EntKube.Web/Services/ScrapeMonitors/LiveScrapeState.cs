using System.Text.Json;
using System.Text.Json.Nodes;
using k8s.Models;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// A cluster's Prometheus as EntKube installed it: where the release is and what it is called. Which
/// of the live <see cref="LivePrometheus"/> objects that is, the planner decides.
/// </summary>
public sealed record PrometheusRelease(string Namespace, string Name);

/// <summary>One <c>Prometheus</c> object, with the parts that say what it selects.</summary>
public sealed record LivePrometheus(
    string Namespace, string Name, IReadOnlyDictionary<string, string> Labels, JsonObject Spec)
{
    /// <summary>Which monitors of a kind it selects, by their own labels. Absent: none.</summary>
    public LabelSelector? MonitorSelector(MonitorKind kind) => LabelSelector.From(Spec[
        kind == MonitorKind.PodMonitor ? "podMonitorSelector" : "serviceMonitorSelector"]);

    /// <summary>In which namespaces it looks for them. Absent: only its own.</summary>
    public LabelSelector? NamespaceSelector(MonitorKind kind) => LabelSelector.From(Spec[
        kind == MonitorKind.PodMonitor ? "podMonitorNamespaceSelector" : "serviceMonitorNamespaceSelector"]);

    /// <summary>
    /// True when the targets are spread over several Prometheus servers. Each then knows only its
    /// own share, so the target list EntKube read is not everything that is scraped.
    /// </summary>
    public bool IsSharded => Spec["shards"] is JsonValue shards
        && shards.GetValueKind() == JsonValueKind.Number && shards.GetValue<double>() > 1;
}

/// <summary>One ServiceMonitor or PodMonitor on the cluster, whoever made it.</summary>
public sealed record LiveMonitor(
    MonitorIdentity Identity,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Annotations,
    JsonObject Spec)
{
    /// <summary>Whether it looks for pods or Services in <paramref name="ns"/>. Only its own namespace, unless it says.</summary>
    public bool LooksIn(string ns)
    {
        if (Spec["namespaceSelector"] is not JsonObject selector) return ns == Identity.Namespace;

        if (selector["any"] is JsonValue any && any.GetValueKind() == JsonValueKind.True) return true;

        return selector["matchNames"] is JsonArray { Count: > 0 } names
            ? names.Any(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == ns)
            : ns == Identity.Namespace;
    }
}

/// <summary>One target Prometheus is scraping right now, as it says itself.</summary>
/// <param name="Pool">
/// The scrape job. The operator names its own <c>serviceMonitor/namespace/name/index</c> or
/// <c>podMonitor/…</c>; any other name is a job from somewhere else.
/// </param>
/// <param name="Host">The address it scrapes.</param>
/// <param name="Port">The port it scrapes.</param>
/// <param name="Path">The path it scrapes.</param>
/// <param name="Up">Whether the last scrape succeeded.</param>
public sealed record ActiveTarget(string Pool, string Host, int Port, string Path, bool Up)
{
    /// <summary>The monitor behind the pool, or null when the pool is not an operator's monitor.</summary>
    public MonitorIdentity? Monitor => Pool.Split('/') is [string kind, string ns, string name, ..]
        ? kind switch
        {
            "serviceMonitor" => new MonitorIdentity(MonitorKind.ServiceMonitor, ns, name),
            "podMonitor" => new MonitorIdentity(MonitorKind.PodMonitor, ns, name),
            _ => null,
        }
        : null;
}

/// <summary>What runs in one namespace that a target lives in.</summary>
public sealed record LiveNamespace(
    string Name,
    IReadOnlyList<V1Pod> Pods,
    IReadOnlyList<V1Service> Services,
    IReadOnlyList<V1NetworkPolicy> NetworkPolicies);

/// <summary>
/// What Prometheus is told to scrape and what it says it scrapes. Read before the database, so that
/// a monitor found here whose owner is then missing from the database is a true orphan: a service
/// created between the two reads has a row, and no monitor yet.
/// </summary>
public sealed record LiveMonitoring(
    IReadOnlyList<LivePrometheus> Prometheuses,
    IReadOnlyList<LiveMonitor> Monitors,
    IReadOnlyList<ActiveTarget> Targets);

/// <summary>
/// The workloads behind the targets: every namespace's labels, and the contents of the ones that
/// matter. A namespace that does not exist, or is being deleted, is in neither.
/// </summary>
public sealed record LiveWorkloads(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NamespaceLabels,
    IReadOnlyDictionary<string, LiveNamespace> Namespaces);

/// <summary>
/// Everything <see cref="ScrapeMonitorPlanner"/> knows about one cluster, read in full. There is no
/// half of it: a read that failed is an exception long before one of these exists, because "could
/// not list" must never look like "there are none".
/// </summary>
/// <param name="ClusterId">EntKube's own id for the cluster, which its monitors are labelled with.</param>
/// <param name="Release">The cluster's Prometheus, as Catalog records it.</param>
/// <param name="Monitoring">What is scraped.</param>
/// <param name="Workloads">What runs.</param>
public sealed record LiveScrapeState(
    Guid ClusterId,
    PrometheusRelease Release,
    LiveMonitoring Monitoring,
    LiveWorkloads Workloads);
