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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    KubernetesProxyClientPool clientPool,
    IngestTokenService ingestTokens,
    ILogger<TelemetryNodeClient> logger)
{
    /// <summary>Chart name both telemetry components install — how their ClusterComponents are recognised.</summary>
    private const string ChartName = "entkube-telemetry";

    /// <summary>What it takes to talk to one cluster's node.</summary>
    public sealed record NodeEndpoint(string Kubeconfig, string Namespace, string ServiceName, int Port, string Token);

    // Resolving the endpoint means a DB read, a vault decrypt and a Service lookup in the cluster. That is
    // far too much to repeat for every panel on a page, and none of it changes minute to minute.
    private static readonly ConcurrentDictionary<Guid, (DateTime At, NodeEndpoint? Endpoint)> EndpointCache = new();
    private static readonly TimeSpan EndpointTtl = TimeSpan.FromSeconds(60);

    /// <summary>True when this cluster has a telemetry node the management plane can query.</summary>
    public async Task<bool> IsAvailableAsync(Guid clusterId, CancellationToken ct = default)
        => await ResolveAsync(clusterId, ct) is not null;

    /// <summary>Forgets a cached endpoint — call after installing, moving or removing the components.</summary>
    public static void Invalidate(Guid clusterId) => EndpointCache.TryRemove(clusterId, out _);

    public Task<KubernetesOperationResult<T>> GetAsync<T>(Guid clusterId, string path, CancellationToken ct = default)
        => SendAsync<T>(clusterId, HttpMethod.Get, path, null, ct);

    public Task<KubernetesOperationResult<T>> PostAsync<T>(
        Guid clusterId, string path, object body, CancellationToken ct = default)
        => SendAsync<T>(clusterId, HttpMethod.Post, path, body, ct);

    private async Task<KubernetesOperationResult<T>> SendAsync<T>(
        Guid clusterId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        NodeEndpoint? endpoint = await ResolveAsync(clusterId, ct);
        if (endpoint is null)
            return KubernetesOperationResult<T>.Failure(
                "This cluster has no EntKube telemetry node installed, or its Service could not be found.");

        try
        {
            Kubernetes k8s = clientPool.Get(endpoint.Kubeconfig);
            string baseUrl = k8s.BaseUri.ToString().TrimEnd('/')
                + $"/api/v1/namespaces/{endpoint.Namespace}/services/{endpoint.ServiceName}:{endpoint.Port}/proxy";

            using HttpRequestMessage request = new(method, $"{baseUrl}/{path}");

            // NOT Authorization. This request is addressed to the API server's proxy endpoint, and
            // Authorization is what authenticates to the API SERVER — the k8s client sets it from the
            // kubeconfig. Overwriting it with the node's token makes the API server reject the call with
            // 401 before it is ever forwarded, which reads exactly like the node rejecting the token and
            // is nothing of the sort. The API server passes unknown headers through untouched, so the
            // node's own credential travels in one of those instead.
            request.Headers.Add(EntKube.Telemetry.NodeApi.TokenHeader, endpoint.Token);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType());

            using HttpResponseMessage response = await k8s.HttpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct);
                // A stale endpoint (component moved or reinstalled) presents as a 404 from the proxy;
                // drop the cache so the next attempt re-resolves rather than failing the same way.
                if (response.StatusCode is System.Net.HttpStatusCode.NotFound) Invalidate(clusterId);

                // Say WHO refused. A request to a node travels through the Kubernetes API server's proxy,
                // so a 401 can come from either end, and the two need completely different fixes: the API
                // server means the kubeconfig was not accepted, the node means its own token was not. The
                // bare "401 Unauthorized" this used to report is the same either way, which is exactly how
                // a header conflict at the API server gets mistaken for a token problem at the node.
                string who = DescribeRejector(response, detail);
                logger.LogWarning(
                    "Telemetry node query failed: {Method} {Path} on cluster {ClusterId} returned {Status} "
                    + "({Who}). Body: {Detail}",
                    method, path, clusterId, (int)response.StatusCode, who, Truncate(detail));

                return KubernetesOperationResult<T>.Failure(
                    $"{(int)response.StatusCode} {response.ReasonPhrase} — {who}"
                    + (string.IsNullOrWhiteSpace(detail) ? "" : $": {Truncate(detail)}"));
            }

            T? value = await response.Content.ReadFromJsonAsync<T>(ct);
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
    /// Works out which end of the proxy refused the request.
    ///
    /// The Kubernetes API server answers with a JSON <c>Status</c> object — <c>{"kind":"Status",…}</c> —
    /// whether it is rejecting credentials or RBAC. The node answers with an empty body for an auth
    /// failure and its own JSON otherwise. That difference is enough to tell them apart, and telling them
    /// apart is the difference between "fix the kubeconfig" and "fix the node's token".
    /// </summary>
    private static string DescribeRejector(HttpResponseMessage response, string body)
    {
        bool fromApiServer = body.Contains("\"kind\"", StringComparison.Ordinal)
                             && body.Contains("Status", StringComparison.Ordinal);

        if (fromApiServer)
        {
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "rejected by the Kubernetes API server — the cluster's stored kubeconfig was not accepted",
                System.Net.HttpStatusCode.Forbidden =>
                    "rejected by the Kubernetes API server — the kubeconfig lacks permission on services/proxy "
                    + "for this verb (a read needs get, a search needs create)",
                _ => "reported by the Kubernetes API server",
            };
        }

        return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? "rejected by the telemetry node — it did not accept the query token"
            : "reported by the telemetry node";
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= 400 ? s : s[..400] + "…";

    /// <summary>Finds the cluster's node: its component, its query token, and its Service.</summary>
    private async Task<NodeEndpoint?> ResolveAsync(Guid clusterId, CancellationToken ct)
    {
        if (EndpointCache.TryGetValue(clusterId, out (DateTime At, NodeEndpoint? Endpoint) hit)
            && DateTime.UtcNow - hit.At < EndpointTtl)
            return hit.Endpoint;

        NodeEndpoint? endpoint = null;
        try
        {
            endpoint = await ResolveUncachedAsync(clusterId, ct);
        }
        catch (Exception ex)
        {
            // Cache the negative result too. Without that, a cluster with no node re-runs this lookup on
            // every call of every panel — the routing probe is on the hot path precisely for clusters that
            // have not been cut over.
            logger.LogDebug(ex, "No telemetry node resolved for cluster {ClusterId}.", clusterId);
        }

        EndpointCache[clusterId] = (DateTime.UtcNow, endpoint);
        return endpoint;
    }

    private async Task<NodeEndpoint?> ResolveUncachedAsync(Guid clusterId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.ClusterId == clusterId
                        && c.Status == ComponentStatus.Installed
                        && c.HelmChartName == ChartName)
            .Select(c => new { c.Id, c.Namespace, c.Cluster.TenantId, c.Cluster.Kubeconfig })
            .FirstOrDefaultAsync(ct);

        if (component is null) return null;

        // Everything from here on is a way for a node that is installed to be unreachable — and every one
        // of them ends with reads quietly going to the management plane's store instead, which for a
        // cut-over cluster is empty. "Empty with no error" is the single hardest state to diagnose in this
        // system, so each of these says so rather than returning null in silence.
        if (string.IsNullOrWhiteSpace(component.Kubeconfig))
        {
            logger.LogWarning(
                "Cluster {ClusterId} has an installed telemetry component but no kubeconfig, so its node "
                + "cannot be queried; telemetry reads fall back to the management-plane store.", clusterId);
            return null;
        }

        // Derived, not looked up. A cluster can carry more than one telemetry component, and reading the
        // token off whichever row came back first is only correct while every row agrees — which is
        // precisely the assumption that fails, as a 401 from a node that is otherwise perfectly healthy.
        // Both sides computing it from (cluster, tenant) removes the possibility.
        string token = ingestTokens.MintQuery(clusterId, component.TenantId);

        string ns = string.IsNullOrWhiteSpace(component.Namespace) ? "monitoring" : component.Namespace;

        Kubernetes k8s = clientPool.Get(component.Kubeconfig);
        V1ServiceList services = await k8s.CoreV1.ListNamespacedServiceAsync(
            ns, labelSelector: "app.kubernetes.io/name=entkube-telemetry", cancellationToken: ct);

        // Prefer the querier: if one is deployed, offloading reads from the indexer is exactly why.
        V1Service? chosen =
            services.Items.FirstOrDefault(s => Component(s) == "querier")
            ?? services.Items.FirstOrDefault(s => Component(s) == "indexer");
        if (chosen?.Metadata?.Name is not { } serviceName)
        {
            logger.LogWarning(
                "Cluster {ClusterId} has an installed telemetry component, but namespace {Namespace} has no "
                + "Service labelled app.kubernetes.io/name=entkube-telemetry — the release may be in another "
                + "namespace, or its pods may never have started. Telemetry reads fall back to the "
                + "management-plane store, which for a cut-over cluster holds nothing.", clusterId, ns);
            return null;
        }

        int port = chosen.Spec?.Ports?.FirstOrDefault(p => p.Name == "http")?.Port
                   ?? chosen.Spec?.Ports?.FirstOrDefault()?.Port
                   ?? 8080;

        logger.LogDebug("Telemetry node for cluster {ClusterId}: {Service}:{Port} in {Namespace}",
            clusterId, serviceName, port, ns);
        return new NodeEndpoint(component.Kubeconfig, ns, serviceName, port, token);

        static string? Component(V1Service s) =>
            s.Metadata?.Labels is { } labels && labels.TryGetValue("app.kubernetes.io/component", out string? v)
                ? v : null;
    }
}
