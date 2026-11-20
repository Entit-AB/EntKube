using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using k8s;
using k8s.Autorest;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>One policy rule's verdict on one resource, as Kyverno's background scan recorded it.</summary>
public sealed record PolicyResultRow(
    string Cluster,
    string Namespace,
    string Policy,
    string? Rule,
    string Result,
    string? Message,
    string? Severity,
    string ResourceKind,
    string ResourceName,
    DateTimeOffset? Timestamp)
{
    /// <summary>A verdict somebody has to act on: a failure, an error evaluating it, or a warning.</summary>
    public bool NeedsAttention => Result is "fail" or "error" or "warn";
}

/// <summary>How one policy stands across the tenant's namespaces in an environment.</summary>
public sealed record PolicyResultSummary(
    string Policy, int Pass, int Fail, int Warn, int Error, int Skip, IReadOnlyList<PolicyResultRow> Findings);

/// <summary>Where results could not be read. A partial answer must never look like an all-clear.</summary>
public sealed record PolicyReportGap(string Cluster, string? Namespace, string Reason);

/// <summary>
/// What a namespace read returned: the report documents, or that this cluster has no reports API
/// (Kyverno absent, or its reports controller turned off).
/// </summary>
public sealed record NamespaceReports(bool ReportsApiPresent, IReadOnlyList<string> ListJson);

/// <summary>The policy results for a tenant's app namespaces in one environment.</summary>
public sealed record KyvernoReportOverview(
    IReadOnlyList<PolicyResultSummary> Policies,
    IReadOnlyList<PolicyReportGap> Gaps,
    int NamespacesAsked,
    int NamespacesRead)
{
    public int Failing => Policies.Sum(p => p.Fail + p.Error);
    public int Warnings => Policies.Sum(p => p.Warn);
    public int Passing => Policies.Sum(p => p.Pass);

    /// <summary>Every namespace asked about was read.</summary>
    public bool IsComplete => Gaps.Count == 0 && NamespacesRead == NamespacesAsked;

    /// <summary>
    /// Reports were read and none of them says anything. That is not "everything passes": the
    /// background scan may not have run yet, or no policy matches anything in these namespaces.
    /// </summary>
    public bool HasNoResults => Policies.Count == 0;
}

/// <summary>
/// Reads Kyverno's policy reports for the namespaces a tenant's apps run in.
///
/// <para>The kyverno catalog component turns the reports controller on, so every cluster with
/// Kyverno records a verdict for every resource a policy matches, including those created before
/// the policy and everything an Audit-mode policy lets through. Audit mode reports and does not
/// block, so these reports are the only place its findings exist.</para>
///
/// <para>Scoped to the namespaces <see cref="KyvernoPolicyService"/> applies policies to, which
/// come from the tenant's own deployments: on a shared cluster another tenant's namespaces are
/// never read. Results come from the <c>wgpolicyk8s.io</c> PolicyReport API (Kyverno's default)
/// and from <c>openreports.io</c>, which Kyverno writes instead when OpenReports is enabled.</para>
/// </summary>
public class KyvernoPolicyReportService(
    IDbContextFactory<DeliveryDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients)
{
    private const int ParallelNamespaceReads = 8;

    /// <summary>(group, version, plural) of each report API, most common first.</summary>
    private static readonly (string Group, string Version, string Plural)[] ReportApis =
    [
        ("wgpolicyk8s.io", "v1alpha2", "policyreports"),
        ("openreports.io", "v1alpha1", "reports"),
    ];

    /// <summary>The tenant's policy results in one environment, read live from its clusters.</summary>
    public async Task<KyvernoReportOverview> GetOverviewAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        // The SDK reads are the only part not exercised by tests: a hand-over to the pooled client.
        Dictionary<Guid, Kubernetes?> clients = [];

        return await GetOverviewAsync(tenantId, environmentId, async (clusterId, ns, token) =>
        {
            Kubernetes? client;

            lock (clients)
            {
                clients.TryGetValue(clusterId, out client);
            }

            if (client is null)
            {
                EntKube.Web.Services.Clusters.IClusterClient? cluster = await clusterClients.ForAsync(tenantId, clusterId, token);
                client = cluster?.GetPooledSdkClient()
                    ?? throw new InvalidOperationException("no stored credential for this cluster");

                lock (clients)
                {
                    clients[clusterId] = client;
                }
            }

            return await ReadNamespaceAsync(client, ns, token);
        }, ct);
    }

    /// <summary>
    /// <see cref="GetOverviewAsync(Guid, Guid, CancellationToken)"/> with the namespace read supplied,
    /// so scoping, gaps and aggregation can be tested without a cluster.
    /// </summary>
    public async Task<KyvernoReportOverview> GetOverviewAsync(
        Guid tenantId, Guid environmentId,
        Func<Guid, string, CancellationToken, Task<NamespaceReports>> readNamespace, CancellationToken ct)
    {
        List<(KubernetesCluster Cluster, string Namespace)> targets;

        using (DeliveryDbContext db = dbFactory.CreateDbContext())
        {
            targets = await KyvernoPolicyService.ResolveTargetsAsync(db, tenantId, environmentId, ct);
        }

        ConcurrentBag<PolicyResultRow> rows = [];
        ConcurrentBag<PolicyReportGap> gaps = [];
        ConcurrentDictionary<Guid, bool> clustersWithoutApi = new();
        int read = 0;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = ParallelNamespaceReads, CancellationToken = ct },
            async (target, token) =>
            {
                NamespaceReports reports;

                try
                {
                    reports = await readNamespace(target.Cluster.Id, target.Namespace, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    gaps.Add(new PolicyReportGap(target.Cluster.Name, target.Namespace, ex.Message));
                    return;
                }

                if (!reports.ReportsApiPresent)
                {
                    clustersWithoutApi.TryAdd(target.Cluster.Id, true);
                    return;
                }

                foreach (string json in reports.ListJson)
                {
                    foreach (PolicyResultRow row in ParseReports(json, target.Cluster.Name, target.Namespace))
                    {
                        rows.Add(row);
                    }
                }

                Interlocked.Increment(ref read);
            });

        // One gap per cluster for a missing API, not one per namespace.
        foreach (KubernetesCluster cluster in targets.Select(t => t.Cluster).DistinctBy(c => c.Id)
                     .Where(c => clustersWithoutApi.ContainsKey(c.Id)))
        {
            gaps.Add(new PolicyReportGap(cluster.Name, null,
                "No policy report API on this cluster: Kyverno is not installed, or its reports controller is turned off."));
        }

        return new KyvernoReportOverview(
            Summarize(rows),
            gaps.OrderBy(g => g.Cluster).ThenBy(g => g.Namespace).ToList(),
            NamespacesAsked: targets.Count,
            NamespacesRead: read);
    }

    /// <summary>
    /// Lists the report documents in one namespace from each report API the cluster serves. Reads only.
    /// </summary>
    public static async Task<NamespaceReports> ReadNamespaceAsync(Kubernetes client, string ns, CancellationToken ct)
    {
        List<string> lists = [];
        bool anyApi = false;

        foreach ((string group, string version, string plural) in ReportApis)
        {
            try
            {
                object raw = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                    group, version, ns, plural, cancellationToken: ct);
                lists.Add(JsonSerializer.Serialize(raw));
                anyApi = true;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                // That API is not served here; the other may be.
            }
        }

        return new NamespaceReports(anyApi, lists);
    }

    /// <summary>
    /// Flattens a list of report documents into one row per (result, resource). Handles both report
    /// shapes Kyverno has written: one report per resource with the resource in <c>scope</c> (1.10+),
    /// and older aggregated reports that name resources on each result.
    /// </summary>
    public static IReadOnlyList<PolicyResultRow> ParseReports(string listJson, string cluster, string ns)
    {
        using JsonDocument doc = JsonDocument.Parse(listJson);

        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PolicyResultRow> rows = [];

        foreach (JsonElement report in items.EnumerateArray())
        {
            (string Kind, string Name)? scope = report.TryGetProperty("scope", out JsonElement s) ? ResourceRef(s) : null;

            if (!report.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement result in results.EnumerateArray())
            {
                string? policy = Str(result, "policy");
                string? verdict = Str(result, "result")?.ToLowerInvariant();

                if (policy is null || verdict is null)
                {
                    continue;
                }

                List<(string Kind, string Name)> resources = [];

                if (result.TryGetProperty("resources", out JsonElement rs) && rs.ValueKind == JsonValueKind.Array)
                {
                    resources.AddRange(rs.EnumerateArray().Select(ResourceRef).OfType<(string, string)>());
                }

                if (resources.Count == 0 && scope is { } scoped)
                {
                    resources.Add(scoped);
                }

                if (resources.Count == 0)
                {
                    resources.Add(("", ""));
                }

                foreach ((string kind, string name) in resources)
                {
                    rows.Add(new PolicyResultRow(
                        cluster, ns, policy, Str(result, "rule"), verdict, Str(result, "message"),
                        Str(result, "severity"), kind, name, Timestamp(result)));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Per-policy counts, with the findings that need attention. Policies with failures or errors come
    /// first, then warnings, then those that only pass.
    /// </summary>
    public static IReadOnlyList<PolicyResultSummary> Summarize(IEnumerable<PolicyResultRow> rows) =>
        rows.GroupBy(r => r.Policy, StringComparer.Ordinal)
            .Select(g => new PolicyResultSummary(
                g.Key,
                Pass: g.Count(r => r.Result == "pass"),
                Fail: g.Count(r => r.Result == "fail"),
                Warn: g.Count(r => r.Result == "warn"),
                Error: g.Count(r => r.Result == "error"),
                Skip: g.Count(r => r.Result == "skip"),
                Findings: g.Where(r => r.NeedsAttention)
                    .OrderBy(r => Rank(r.Result))
                    .ThenBy(r => r.Namespace, StringComparer.Ordinal)
                    .ThenBy(r => r.ResourceKind, StringComparer.Ordinal)
                    .ThenBy(r => r.ResourceName, StringComparer.Ordinal)
                    .ToList()))
            .OrderBy(p => p.Fail + p.Error > 0 ? 0 : p.Warn > 0 ? 1 : 2)
            .ThenByDescending(p => p.Fail + p.Error + p.Warn)
            .ThenBy(p => p.Policy, StringComparer.Ordinal)
            .ToList();

    private static int Rank(string result) => result switch { "fail" => 0, "error" => 1, _ => 2 };

    private static (string Kind, string Name)? ResourceRef(JsonElement r) =>
        r.ValueKind == JsonValueKind.Object && Str(r, "name") is { } name
            ? (Str(r, "kind") ?? "", name)
            : null;

    private static DateTimeOffset? Timestamp(JsonElement result) =>
        result.TryGetProperty("timestamp", out JsonElement ts)
        && ts.ValueKind == JsonValueKind.Object
        && ts.TryGetProperty("seconds", out JsonElement seconds)
        && seconds.TryGetInt64(out long s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
