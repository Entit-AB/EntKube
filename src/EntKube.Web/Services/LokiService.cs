using System.Globalization;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class LokiConfig
{
    public string Namespace { get; set; } = "monitoring";
    public string ServiceName { get; set; } = "loki";
    public int ServicePort { get; set; } = 3100;
    public Guid? StorageLinkId { get; set; }
}

public class LokiLogStream
{
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<LokiLogEntry> Entries { get; set; } = [];
}

public class LokiLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Line { get; set; } = "";
    public LogLevel DetectedLevel { get; set; } = LogLevel.None;
}

public enum LogLevel { None, Debug, Info, Warn, Error, Fatal }

/// <summary>
/// Queries Grafana Loki for logs via the Kubernetes API proxy endpoint,
/// using the same pattern as PrometheusService. Detects Loki automatically
/// by looking for a ClusterComponent with HelmChartName containing "loki".
///
/// Also handles S3 chunk storage configuration: WriteStorageHelmValuesAsync
/// injects non-sensitive S3 values (region, bucket, endpoint) into HelmValues
/// and stores credentials as vault secrets for injection at install time.
/// </summary>
public class LokiService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<LokiService> logger,
    VaultService vaultService,
    StorageService storageService)
{
    public async Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolveLokiInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<string>>.Failure(error!);

        return await WithLokiAsync<List<string>>(info,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}/loki/api/v1/label/namespace/values", token);
                return ParseLabelValues(json);
            },
            $"loki namespaces {clusterId}", ct);
    }

    public async Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
    {
        var (info, error) = await ResolveLokiInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<string>>.Failure(error!);

        string query = string.IsNullOrEmpty(namespaceName)
            ? "/loki/api/v1/label/pod/values"
            : $"/loki/api/v1/label/pod/values?query={Uri.EscapeDataString($"{{namespace=\"{namespaceName}\"}}")}";

        return await WithLokiAsync<List<string>>(info,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}{query}", token);
                return ParseLabelValues(json);
            },
            $"loki pods {clusterId} ns={namespaceName}", ct);
    }

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryRangeAsync(
        Guid clusterId,
        string namespaceName,
        string? podName,
        string? containerName,
        string? textFilter,
        DateTime from,
        DateTime to,
        int limit = 200,
        CancellationToken ct = default)
    {
        return await QueryRangeMultiAsync(
            clusterId, [namespaceName], podName, containerName, textFilter, from, to, limit, ct);
    }

    /// <summary>
    /// Queries logs across multiple namespaces on a single cluster in one request,
    /// using a <c>namespace=~"a|b|c"</c> LogQL matcher. Used by the customer
    /// operations view to aggregate logs across all of an app's deployments
    /// (which may live in different namespaces on the same cluster).
    /// </summary>
    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryRangeMultiAsync(
        Guid clusterId,
        IReadOnlyCollection<string> namespaces,
        string? podName,
        string? containerName,
        string? textFilter,
        DateTime from,
        DateTime to,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (namespaces.Count == 0)
            return KubernetesOperationResult<List<LokiLogStream>>.Success([]);

        var (info, error) = await ResolveLokiInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<LokiLogStream>>.Failure(error!);

        string logQuery = BuildLogQL(namespaces, podName, containerName, textFilter);
        long fromNs = new DateTimeOffset(from.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;
        long toNs   = new DateTimeOffset(to.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;

        string url = $"/loki/api/v1/query_range?query={Uri.EscapeDataString(logQuery)}" +
                     $"&start={fromNs}&end={toNs}&limit={limit}&direction=backward";

        return await WithLokiAsync<List<LokiLogStream>>(info,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}{url}", token);
                return ParseQueryRange(json);
            },
            $"loki query {clusterId}", ct);
    }

    public async Task<bool> IsAvailableAsync(Guid clusterId, CancellationToken ct = default)
    {
        var (info, _) = await ResolveLokiInfoAsync(clusterId, ct);
        return info is not null;
    }

    // ──────── Storage configuration ────────

    /// <summary>
    /// Injects S3-compatible storage configuration into the component's Helm values
    /// and stores the access/secret key as vault secrets for injection at install time.
    ///
    /// Non-sensitive values (region, bucket names, endpoint, storage type) are written
    /// directly to HelmValues. Credentials are stored encrypted in the vault and
    /// injected by InjectSecretsIntoValuesAsync via the hidden catalog fields.
    /// </summary>
    public async Task WriteStorageHelmValuesAsync(
        Guid tenantId, Guid clusterComponentId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        string region = link.Region ?? "us-east-1";
        string bucket = link.BucketName ?? "";

        Dictionary<string, string> s3Values = new()
        {
            ["loki.storage.type"] = "s3",
            ["loki.storage.s3.region"] = region,
            ["loki.storage.s3.s3ForcePathStyle"] = "true",
            ["loki.storage.bucketNames.chunks"] = bucket,
            ["loki.storage.bucketNames.ruler"] = bucket,
            ["loki.storage.bucketNames.admin"] = bucket,
            // The active schema period's object_store — NOT storage.type — is what actually
            // decides where the ingester writes chunks. The catalog default seeds this as
            // "filesystem", so without flipping it here Loki ignores the S3 client we just
            // wired up and writes chunks to the (read-only, unconfigured) filesystem store
            // instead: every flush fails with "store put chunk: mkdir fake: read-only file
            // system", chunks accumulate in the ingester, and the pod OOMKills in a loop
            // while the S3 bucket stays empty. Index 0 is the sole schema period the catalog
            // seeds (ComponentCatalog loki DefaultValues). The list already exists in
            // HelmValues at this point, so the numeric-index merge path resolves.
            ["loki.schemaConfig.configs.0.object_store"] = "s3"
        };

        if (!string.IsNullOrWhiteSpace(link.Endpoint))
        {
            s3Values["loki.storage.s3.endpoint"] = link.Endpoint;
        }

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", s3Values);

        // Persist the storage link ID in Configuration so the UI can re-populate the dropdown.
        LokiConfig storedConfig = TryDeserializeConfig(component.Configuration) ?? new LokiConfig();
        storedConfig.StorageLinkId = storageLinkId;
        component.Configuration = JsonSerializer.Serialize(storedConfig);

        await db.SaveChangesAsync(ct);

        // Store credentials as vault secrets — injected at install time via hidden catalog fields
        // loki-s3-access-key (→ loki.storage.s3.accessKeyId) and
        // loki-s3-secret-key (→ loki.storage.s3.secretAccessKey).
        await vaultService.InitializeVaultAsync(tenantId, ct);
        (string accessKey, string secretKey) = await storageService.GetStoredCredentialsInternalAsync(tenantId, storageLinkId, ct);

        if (!string.IsNullOrEmpty(accessKey))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, "loki-s3-access-key", accessKey, ct);
        }

        if (!string.IsNullOrEmpty(secretKey))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, "loki-s3-secret-key", secretKey, ct);
        }
    }

    public async Task<Guid?> GetStorageLinkIdForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct);
        if (component is null) return null;
        return TryDeserializeConfig(component.Configuration)?.StorageLinkId;
    }

    private static LokiConfig? TryDeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<LokiConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    // ──────── Internal ────────

    private static string BuildLogQL(
        IReadOnlyCollection<string> namespaces, string? pod, string? container, string? text)
    {
        StringBuilder sb = new("{");
        if (namespaces.Count == 1)
            sb.Append($"namespace=\"{namespaces.First()}\"");
        else
            sb.Append($"namespace=~\"{string.Join("|", namespaces.Select(System.Text.RegularExpressions.Regex.Escape))}\"");
        if (!string.IsNullOrEmpty(pod))       sb.Append($", pod=~\"{pod}.*\"");
        if (!string.IsNullOrEmpty(container)) sb.Append($", container=\"{container}\"");
        sb.Append('}');

        if (!string.IsNullOrEmpty(text))
            sb.Append($" |= \"{text.Replace("\"", "\\\"")}\"");

        return sb.ToString();
    }

    private static List<string> ParseLabelValues(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("status").GetString() != "success") return [];
            return [.. doc.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderBy(s => s)];
        }
        catch { return []; }
    }

    private static List<LokiLogStream> ParseQueryRange(string json)
    {
        List<LokiLogStream> results = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success") return results;

            JsonElement data = root.GetProperty("data");
            if (!data.GetProperty("resultType").GetString()!.Equals("streams", StringComparison.OrdinalIgnoreCase))
                return results;

            foreach (JsonElement stream in data.GetProperty("result").EnumerateArray())
            {
                LokiLogStream ls = new();

                if (stream.TryGetProperty("stream", out JsonElement labels))
                    foreach (JsonProperty p in labels.EnumerateObject())
                        ls.Labels[p.Name] = p.Value.GetString() ?? "";

                if (stream.TryGetProperty("values", out JsonElement values))
                    foreach (JsonElement entry in values.EnumerateArray())
                    {
                        if (entry.GetArrayLength() < 2) continue;
                        string tsNs = entry[0].GetString() ?? "0";
                        string line = entry[1].GetString() ?? "";

                        DateTime ts = long.TryParse(tsNs, out long ns)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000).UtcDateTime
                            : DateTime.UtcNow;

                        ls.Entries.Add(new LokiLogEntry
                        {
                            Timestamp = ts,
                            Line = line,
                            DetectedLevel = DetectLevel(line)
                        });
                    }

                if (ls.Entries.Count > 0) results.Add(ls);
            }
        }
        catch { }
        return results;
    }

    private static LogLevel DetectLevel(string line)
    {
        if (line.Contains("FATAL",    StringComparison.OrdinalIgnoreCase) ||
            line.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)) return LogLevel.Fatal;
        if (line.Contains("ERROR",    StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" ERR ",    StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
        if (line.Contains("WARN",     StringComparison.OrdinalIgnoreCase)) return LogLevel.Warn;
        if (line.Contains("DEBUG",    StringComparison.OrdinalIgnoreCase)) return LogLevel.Debug;
        if (line.Contains("INFO",     StringComparison.OrdinalIgnoreCase)) return LogLevel.Info;
        return LogLevel.None;
    }

    private sealed record ResolvedLokiInfo(string Kubeconfig, string ClusterName, LokiConfig Config);

    private async Task<(ResolvedLokiInfo? Info, string? Error)> ResolveLokiInfoAsync(
        Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters
            .Include(c => c.Components)
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null) return (null, "Cluster not found.");
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (null, $"No kubeconfig configured for cluster '{cluster.Name}'.");

        ClusterComponent? lokiComponent = cluster.Components.FirstOrDefault(c =>
            (c.HelmChartName ?? "").Contains("loki", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("loki", StringComparison.OrdinalIgnoreCase));

        if (lokiComponent is null) return (null, $"No Loki component found on cluster '{cluster.Name}'.");
        if (lokiComponent.Status != ComponentStatus.Installed)
            return (null, $"Loki is not installed on cluster '{cluster.Name}'.");

        LokiConfig config = BuildLokiConfig(lokiComponent);
        return (new ResolvedLokiInfo(cluster.Kubeconfig, cluster.Name, config), null);
    }

    private static LokiConfig BuildLokiConfig(ClusterComponent component)
    {
        string ns = component.Namespace ?? "monitoring";
        string releaseName = component.ReleaseName ?? component.Name;

        LokiConfig derived = new()
        {
            Namespace   = ns,
            ServiceName = releaseName.Contains("loki", StringComparison.OrdinalIgnoreCase)
                ? releaseName : $"{releaseName}-loki"
        };

        LokiConfig? explicit_ = TryDeserializeConfig(component.Configuration);
        if (explicit_ is null) return derived;
        if (!string.IsNullOrWhiteSpace(explicit_.Namespace))   derived.Namespace   = explicit_.Namespace;
        if (!string.IsNullOrWhiteSpace(explicit_.ServiceName)) derived.ServiceName = explicit_.ServiceName;
        if (explicit_.ServicePort != 3100)                     derived.ServicePort = explicit_.ServicePort;
        return derived;
    }

    private async Task<KubernetesOperationResult<T>> WithLokiAsync<T>(
        ResolvedLokiInfo info,
        Func<HttpClient, string, CancellationToken, Task<T>> action,
        string logContext,
        CancellationToken ct)
    {
        try
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(info.Kubeconfig));
            KubernetesClientConfiguration k8sConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
            using Kubernetes k8s = new(k8sConfig);

            string baseUrl = await BuildLokiProxyBaseAsync(k8s, info, ct);
            logger.LogDebug("Loki proxy base {BaseUrl}", baseUrl);

            T result = await action(k8s.HttpClient, baseUrl, ct);
            return KubernetesOperationResult<T>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Loki query failed ({Context})", logContext);
            return KubernetesOperationResult<T>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Resolves the API-server proxy base URL used to reach Loki's HTTP API. Prefers a specific
    /// Ready pod behind the Loki service (lowest latency, no kube-proxy hop). When no Ready pod
    /// endpoint can be parsed — e.g. on newer Kubernetes where the legacy core/v1 Endpoints API
    /// is incomplete — it falls back to proxying via the Service itself, letting the API server
    /// route to a healthy backend. Throws (naming the cluster) only when neither is reachable.
    /// </summary>
    private async Task<string> BuildLokiProxyBaseAsync(
        Kubernetes k8s, ResolvedLokiInfo info, CancellationToken ct)
    {
        string apiBase = k8s.BaseUri.ToString().TrimEnd('/');
        int port = info.Config.ServicePort;
        string ns = info.Config.Namespace;

        // Preferred: a Ready pod behind the service.
        V1EndpointAddress? addr = await FindLokiEndpointAsync(k8s, info.Config, ct);
        if (addr?.TargetRef is not null)
        {
            string podName = addr.TargetRef.Name;
            string podNs   = addr.TargetRef.NamespaceProperty ?? ns;
            return apiBase + $"/api/v1/namespaces/{podNs}/pods/{podName}:{port}/proxy";
        }

        // Fallback: proxy through the Service so the API server selects a healthy endpoint.
        string? svc = await FindLokiServiceNameAsync(k8s, info.Config, ct);
        if (svc is not null)
            return apiBase + $"/api/v1/namespaces/{ns}/services/{svc}:{port}/proxy";

        throw new InvalidOperationException(
            $"No reachable Loki endpoint for service '{info.Config.ServiceName}' in namespace " +
            $"'{ns}' on cluster '{info.ClusterName}'. The Loki pod may not be Ready yet.");
    }

    /// <summary>
    /// Finds a non-headless Loki ClusterIP service exposing the query port, for service-proxy
    /// fallback. Tries the configured name first, then scans for any "loki" service on that port.
    /// </summary>
    private async Task<string?> FindLokiServiceNameAsync(
        Kubernetes k8s, LokiConfig config, CancellationToken ct)
    {
        try
        {
            await k8s.CoreV1.ReadNamespacedServiceAsync(config.ServiceName, config.Namespace, cancellationToken: ct);
            return config.ServiceName;
        }
        catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }

        V1ServiceList services = await k8s.CoreV1.ListNamespacedServiceAsync(config.Namespace, cancellationToken: ct);
        V1Service? match = services.Items
            .Where(s => s.Metadata.Name.Contains("loki", StringComparison.OrdinalIgnoreCase))
            // Skip headless services — the API server can't proxy to ClusterIP: None.
            .Where(s => !string.Equals(s.Spec?.ClusterIP, "None", StringComparison.OrdinalIgnoreCase))
            // Match the query port (3100) — naturally excludes gateway (80), canary (3500), memberlist (7946).
            .Where(s => s.Spec?.Ports?.Any(p => p.Port == config.ServicePort) ?? false)
            // Prefer query-serving components. In simple-scalable mode the namespace holds
            // loki-read/-write/-backend all on 3100; plain alphabetical order would pick
            // loki-backend first, which doesn't serve /query_range and answers 503. Rank the
            // read path ahead of write/backend, then fall back to name for stable ordering.
            .OrderBy(s => QueryServiceRank(s.Metadata.Name))
            .ThenBy(s => s.Metadata.Name)
            .FirstOrDefault();

        return match?.Metadata.Name;
    }

    /// <summary>Lower rank = more likely to serve read queries. Used to break ties when
    /// multiple Loki services share the query port (simple-scalable deployments).</summary>
    private static int QueryServiceRank(string name)
    {
        string n = name.ToLowerInvariant();
        // Write-path / storage components don't serve query_range → would 503. Pick last.
        if (n.Contains("write") || n.Contains("backend") || n.Contains("ingester") ||
            n.Contains("distributor") || n.Contains("compactor")) return 2;
        // Read path / queriers explicitly serve queries.
        if (n.Contains("read") || n.Contains("querier") || n.Contains("query-frontend")) return 0;
        // Single-binary / all-in-one service (serves everything).
        return 1;
    }

    private async Task<V1EndpointAddress?> FindLokiEndpointAsync(
        Kubernetes k8s, LokiConfig config, CancellationToken ct)
    {
        try
        {
            V1Endpoints ep = await k8s.CoreV1.ReadNamespacedEndpointsAsync(
                config.ServiceName, config.Namespace, cancellationToken: ct);
            V1EndpointAddress? direct = ReadyPodAddress(ep, config.ServicePort);
            if (direct is not null) return direct;
        }
        catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }

        // Fallback: scan namespace for anything named "loki"
        V1EndpointsList all = await k8s.CoreV1.ListNamespacedEndpointsAsync(
            config.Namespace, cancellationToken: ct);

        foreach (V1Endpoints candidate in all.Items.OrderBy(e => e.Metadata.Name))
        {
            if (!candidate.Metadata.Name.Contains("loki", StringComparison.OrdinalIgnoreCase)) continue;
            V1EndpointAddress? addr = ReadyPodAddress(candidate, config.ServicePort);
            if (addr is null) continue;
            logger.LogInformation("Auto-discovered Loki endpoint {Found}", candidate.Metadata.Name);
            return addr;
        }
        return null;
    }

    private static V1EndpointAddress? ReadyPodAddress(V1Endpoints ep, int port)
    {
        if (ep.Subsets is null) return null;
        foreach (V1EndpointSubset subset in ep.Subsets)
        {
            bool portMatches = subset.Ports?.Any(p => p.Port == port) ?? true;
            if (!portMatches) continue;
            V1EndpointAddress? addr = (subset.Addresses ?? [])
                .FirstOrDefault(a => a.TargetRef?.Kind == "Pod");
            if (addr is not null) return addr;
        }
        return null;
    }
}
