using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Reaches a cluster's in-cluster telemetry node from the management plane, through the Kubernetes API
/// server's service proxy — the same route <see cref="LokiService"/> and <see cref="PrometheusService"/>
/// take, so no ingress, no public hostname and no extra certificate are needed to read a cluster's logs.
///
/// This is what turns the whole redesign into a user-visible change: instead of the management plane
/// holding every cluster's log data and searching it locally, it makes <b>one</b> request per user action
/// to a node sitting next to the data.
///
/// The node is found by label rather than by a name derived from the Helm release, because release names
/// are an operator's choice and the chart's labels are not. A querier is preferred when one is deployed —
/// that is the whole point of deploying it — and the indexer answers otherwise.
/// </summary>
public sealed class TelemetryNodeClient(
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    IngestTokenService ingestTokens,
    ILogger<TelemetryNodeClient> logger)
{
    /// <summary>Chart name both telemetry components install — how their ClusterComponents are recognised.</summary>
    private const string ChartName = "entkube-telemetry";

    /// <summary>
    /// What it takes to talk to one cluster's node.
    ///
    /// <para>It used to carry the cluster's <c>Kubeconfig</c>. It does not any more: the seam sends
    /// the request, so what is cached is where the node is and the token it expects — never the
    /// credential that reaches it.</para>
    /// </summary>
    public sealed record NodeEndpoint(string Namespace, string ServiceName, int Port, string Token);

    // Resolving the endpoint means a DB read, a vault decrypt and a Service lookup in the cluster. That is
    // far too much to repeat for every panel on a page, and none of it changes minute to minute.
    // ⚠️ Keyed by (tenant, cluster), not by cluster alone. Resolving the endpoint is now a
    // tenant-scoped question — a tenant who may not see the cluster resolves nothing — and a
    // cluster-only key would hand that "no node here" answer to the OWNER on the next call, whose
    // telemetry would then quietly read from the management-plane store instead. Adding a tenant to
    // a cached question changes the question, so the key has to change with it.
    private static readonly ConcurrentDictionary<(Guid TenantId, Guid ClusterId),
        (DateTime At, NodeEndpoint? Endpoint)> EndpointCache = new();
    private static readonly TimeSpan EndpointTtl = TimeSpan.FromSeconds(60);

    /// <summary>True when this cluster has a telemetry node the management plane can query.</summary>
    public async Task<bool> IsAvailableAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
        => await ResolveAsync(tenantId, clusterId, ct) is not null;

    /// <summary>
    /// Forgets a cached endpoint — call after installing, moving or removing the components.
    /// Every tenant's entry for the cluster goes, because the thing that changed is the cluster.
    /// </summary>
    public static void Invalidate(Guid clusterId)
    {
        foreach ((Guid TenantId, Guid ClusterId) key in EndpointCache.Keys)
        {
            if (key.ClusterId == clusterId) EndpointCache.TryRemove(key, out _);
        }
    }

    public Task<KubernetesOperationResult<T>> GetAsync<T>(
        Guid tenantId, Guid clusterId, string path, CancellationToken ct = default)
        => SendAsync<T>(tenantId, clusterId, HttpMethod.Get, path, null, ct);

    public Task<KubernetesOperationResult<T>> PostAsync<T>(
        Guid tenantId, Guid clusterId, string path, object body, CancellationToken ct = default)
        => SendAsync<T>(tenantId, clusterId, HttpMethod.Post, path, body, ct);

    private async Task<KubernetesOperationResult<T>> SendAsync<T>(
        Guid tenantId, Guid clusterId, HttpMethod method, string path, object? body,
        CancellationToken ct)
    {
        EntKube.Web.Services.Clusters.IClusterClient? access =
            await clusterAccess.ForAsync(tenantId, clusterId, ct);

        if (access is null)
            return KubernetesOperationResult<T>.Failure(
                "This cluster is not this tenant's, or has no kubeconfig configured.");

        NodeEndpoint? endpoint = await ResolveAsync(tenantId, clusterId, ct);
        if (endpoint is null)
            return KubernetesOperationResult<T>.Failure(
                "This cluster has no EntKube telemetry node installed, or its Service could not be found.");

        try
        {
            // The seam sends it. Note what is NOT passed: Authorization. That header authenticates
            // to the API SERVER and the seam sets it from the credential it holds; overwriting it
            // with the node's token makes the API server reject the call with a 401 before it is
            // ever forwarded, which reads exactly like the node rejecting the token and is nothing
            // of the sort. Unknown headers pass through untouched, so the node's own credential
            // travels in one of those.
            ServiceProxyResponse response = await access.ProxyServiceAsync(
                endpoint.Namespace, endpoint.ServiceName, endpoint.Port, method, path,
                body,
                new Dictionary<string, string> { [EntKube.Telemetry.NodeApi.TokenHeader] = endpoint.Token },
                ct);

            if (!response.Success)
            {
                // A stale endpoint (component moved or reinstalled) presents as a 404 from the proxy;
                // drop the cache so the next attempt re-resolves rather than failing the same way.
                if (response.Status == (int)System.Net.HttpStatusCode.NotFound) Invalidate(clusterId);

                // Say WHO refused. A request to a node travels through the Kubernetes API server's
                // proxy, so a 401 can come from either end, and the two need completely different
                // fixes: the API server means the kubeconfig was not accepted, the node means its
                // own token was not. The bare "401 Unauthorized" this used to report is the same
                // either way, which is exactly how a header conflict at the API server gets
                // mistaken for a token problem at the node.
                string who = DescribeRejector(response);
                logger.LogWarning(
                    "Telemetry node query failed: {Method} {Path} on cluster {ClusterId} returned {Status} "
                    + "({Who}). Body: {Detail}",
                    method, path, clusterId, response.Status, who, Truncate(response.Body));

                return KubernetesOperationResult<T>.Failure(
                    $"{response.Status} {response.ReasonPhrase} — {who}"
                    + (string.IsNullOrWhiteSpace(response.Body) ? "" : $": {Truncate(response.Body)}"));
            }

            T? value = string.IsNullOrWhiteSpace(response.Body)
                ? default
                : System.Text.Json.JsonSerializer.Deserialize<T>(response.Body, JsonOptions);

            return value is null
                ? KubernetesOperationResult<T>.Failure("The telemetry node returned an empty body.")
                : KubernetesOperationResult<T>.Success(value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry node query {Path} failed for cluster {ClusterId}.", path, clusterId);
            return KubernetesOperationResult<T>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Matches what <c>HttpContent.ReadFromJsonAsync</c> used before the send moved to the seam:
    /// the node serialises with web defaults, so the reader has to use them too. Left explicit
    /// because switching from the typed helper to a string is exactly where a casing assumption
    /// would otherwise change silently.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Works out which end of the proxy refused the request.
    ///
    /// The Kubernetes API server answers with a JSON <c>Status</c> object — <c>{"kind":"Status",…}</c> —
    /// whether it is rejecting credentials or RBAC. The node answers with an empty body for an auth
    /// failure and its own JSON otherwise. That difference is enough to tell them apart, and telling them
    /// apart is the difference between "fix the kubeconfig" and "fix the node's token".
    /// </summary>
    private static string DescribeRejector(ServiceProxyResponse response)
    {
        bool fromApiServer = response.Body.Contains("\"kind\"", StringComparison.Ordinal)
                             && response.Body.Contains("Status", StringComparison.Ordinal);

        if (fromApiServer)
        {
            return (System.Net.HttpStatusCode)response.Status switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "rejected by the Kubernetes API server — the cluster's stored kubeconfig was not accepted",
                System.Net.HttpStatusCode.Forbidden =>
                    "rejected by the Kubernetes API server — the kubeconfig lacks permission on services/proxy "
                    + "for this verb (a read needs get, a search needs create)",
                _ => "reported by the Kubernetes API server",
            };
        }

        return response.Status == (int)System.Net.HttpStatusCode.Unauthorized
            ? "rejected by the telemetry node — it did not accept the query token"
            : "reported by the telemetry node";
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= 400 ? s : s[..400] + "…";

    /// <summary>Finds the cluster's node: its component, its query token, and its Service.</summary>
    private async Task<NodeEndpoint?> ResolveAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        if (EndpointCache.TryGetValue((tenantId, clusterId), out (DateTime At, NodeEndpoint? Endpoint) hit)
            && DateTime.UtcNow - hit.At < EndpointTtl)
            return hit.Endpoint;

        NodeEndpoint? endpoint = null;
        try
        {
            endpoint = await ResolveUncachedAsync(tenantId, clusterId, ct);
        }
        catch (Exception ex)
        {
            // Cache the negative result too. Without that, a cluster with no node re-runs this lookup on
            // every call of every panel — the routing probe is on the hot path precisely for clusters that
            // have not been cut over.
            logger.LogDebug(ex, "No telemetry node resolved for cluster {ClusterId}.", clusterId);
        }

        EndpointCache[(tenantId, clusterId)] = (DateTime.UtcNow, endpoint);
        return endpoint;
    }

    private async Task<NodeEndpoint?> ResolveUncachedAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        EntKube.Web.Services.Clusters.IClusterClient? access =
            await clusterAccess.ForAsync(tenantId, clusterId, ct);

        if (access is null) return null;

        // Asked of Catalog as this tenant. It used to be a direct ClusterComponents query selecting
        // the cluster's Kubeconfig in the same projection — which is why this file carried five
        // credential sites and the last of the contract's blocked queries at once.
        EntKube.Contracts.Catalog.InstalledComponent? component =
            (await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct))
            .FirstOrDefault(c => c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed
                                 && c.HelmChartName == ChartName);

        if (component is null) return null;

        // Derived from the ASKING tenant, not from the component's cluster row. A cluster can carry
        // more than one telemetry component, and reading the token off whichever row came back
        // first is only correct while every row agrees — which is precisely the assumption that
        // fails, as a 401 from a node that is otherwise perfectly healthy. Both sides computing it
        // from (cluster, tenant) removes the possibility; taking the tenant as an input removes the
        // second way to get it wrong.
        string token = ingestTokens.MintQuery(clusterId, tenantId);

        string ns = string.IsNullOrWhiteSpace(component.Namespace) ? "monitoring" : component.Namespace;

        // The Service list through the seam — the same question, without the credential.
        string json = await access.GetJsonAsync(
            "services", ns, "app.kubernetes.io/name=entkube-telemetry", ct);

        List<(string Name, string? Role, int Port)> candidates = ReadServices(json);

        // Prefer the querier: if one is deployed, offloading reads from the indexer is exactly why.
        (string Name, string? Role, int Port) chosen =
            candidates.FirstOrDefault(s => s.Role == "querier");

        if (chosen.Name is null)
            chosen = candidates.FirstOrDefault(s => s.Role == "indexer");

        if (chosen.Name is null)
        {
            logger.LogWarning(
                "Cluster {ClusterId} has an installed telemetry component, but namespace {Namespace} has no "
                + "Service labelled app.kubernetes.io/name=entkube-telemetry — the release may be in another "
                + "namespace, or its pods may never have started. Telemetry reads fall back to the "
                + "management-plane store, which for a cut-over cluster holds nothing.", clusterId, ns);
            return null;
        }

        logger.LogDebug("Telemetry node for cluster {ClusterId}: {Service}:{Port} in {Namespace}",
            clusterId, chosen.Name, chosen.Port, ns);

        return new NodeEndpoint(ns, chosen.Name, chosen.Port, token);
    }

    /// <summary>
    /// The Services in the node's namespace, as name/role/port.
    ///
    /// <para>Public and static so the choice between querier and indexer — and the port fallback
    /// chain — can be exercised without a cluster. It used to read a typed <c>V1ServiceList</c>;
    /// the seam answers in JSON, and the fallbacks are the part worth keeping under test, since a
    /// wrong port is a node that is installed and unreachable.</para>
    /// </summary>
    public static List<(string Name, string? Role, int Port)> ReadServices(string json)
    {
        List<(string, string?, int)> found = [];

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items))
            return found;

        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)
                || !meta.TryGetProperty("name", out System.Text.Json.JsonElement name)
                || name.GetString() is not { } serviceName)
            {
                continue;
            }

            string? role =
                meta.TryGetProperty("labels", out System.Text.Json.JsonElement labels)
                && labels.TryGetProperty("app.kubernetes.io/component", out System.Text.Json.JsonElement c)
                    ? c.GetString()
                    : null;

            found.Add((serviceName, role, ReadPort(item)));
        }

        return found;
    }

    /// <summary>The http port, else the first, else the chart's default.</summary>
    private static int ReadPort(System.Text.Json.JsonElement service)
    {
        if (!service.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
            || !spec.TryGetProperty("ports", out System.Text.Json.JsonElement ports))
        {
            return 8080;
        }

        int? first = null;

        foreach (System.Text.Json.JsonElement port in ports.EnumerateArray())
        {
            if (!port.TryGetProperty("port", out System.Text.Json.JsonElement number)
                || !number.TryGetInt32(out int value))
            {
                continue;
            }

            first ??= value;

            if (port.TryGetProperty("name", out System.Text.Json.JsonElement portName)
                && portName.GetString() == "http")
            {
                return value;
            }
        }

        return first ?? 8080;
    }
}
