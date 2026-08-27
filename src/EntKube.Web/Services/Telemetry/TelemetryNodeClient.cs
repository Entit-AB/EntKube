using System.Collections.Concurrent;
using System.Net.Http.Headers;
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
    VaultService vaultService,
    ILogger<TelemetryNodeClient> logger)
{
    /// <summary>Chart name both telemetry components install — how their ClusterComponents are recognised.</summary>
    private const string ChartName = "entkube-telemetry";

    /// <summary>Vault secret holding the bearer token the node expects on query requests.</summary>
    private const string QueryTokenSecret = "telemetry-query-token";

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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType());

            using HttpResponseMessage response = await k8s.HttpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct);
                // A stale endpoint (component moved or reinstalled) presents as a 404 from the proxy;
                // drop the cache so the next attempt re-resolves rather than failing the same way.
                if (response.StatusCode is System.Net.HttpStatusCode.NotFound) Invalidate(clusterId);
                return KubernetesOperationResult<T>.Failure($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
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

        if (component is null || string.IsNullOrWhiteSpace(component.Kubeconfig)) return null;

        string? token = await vaultService.GetComponentSecretValueAsync(
            component.TenantId, component.Id, QueryTokenSecret, ct);
        if (string.IsNullOrEmpty(token)) return null;

        string ns = string.IsNullOrWhiteSpace(component.Namespace) ? "monitoring" : component.Namespace;

        Kubernetes k8s = clientPool.Get(component.Kubeconfig);
        V1ServiceList services = await k8s.CoreV1.ListNamespacedServiceAsync(
            ns, labelSelector: "app.kubernetes.io/name=entkube-telemetry", cancellationToken: ct);

        // Prefer the querier: if one is deployed, offloading reads from the indexer is exactly why.
        V1Service? chosen =
            services.Items.FirstOrDefault(s => Component(s) == "querier")
            ?? services.Items.FirstOrDefault(s => Component(s) == "indexer");
        if (chosen?.Metadata?.Name is not { } serviceName) return null;

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
