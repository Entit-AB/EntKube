using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Configuration extracted from a kube-prometheus-stack component that tells
/// us where to find the Prometheus and Alertmanager services inside the cluster.
/// </summary>
public class PrometheusConfig
{
    public string Namespace { get; set; } = "monitoring";
    public string ServiceName { get; set; } = "prometheus-kube-prometheus-prometheus";
    public int ServicePort { get; set; } = 9090;
    public string AlertmanagerServiceName { get; set; } = "prometheus-kube-prometheus-alertmanager";
    public int AlertmanagerServicePort { get; set; } = 9093;
}

/// <summary>
/// A single instant-query result from Prometheus — one metric with its labels and value.
/// </summary>
public class PrometheusMetricResult
{
    public Dictionary<string, string> Labels { get; set; } = new();
    public double Value { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// A time-series result from a Prometheus range query — a set of data points over time.
/// </summary>
public class PrometheusTimeSeries
{
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<TimeSeriesDataPoint> DataPoints { get; set; } = [];
}

/// <summary>
/// Summary of cluster health metrics retrieved from Prometheus.
/// </summary>
public class ClusterHealthSummary
{
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public int TotalNodes { get; set; }
    public int ReadyNodes { get; set; }
    public int TotalPods { get; set; }
    public int RunningPods { get; set; }
    public int PendingPods { get; set; }
    public int FailedPods { get; set; }
    public double DiskUsagePercent { get; set; }
    public List<NodeHealthInfo> Nodes { get; set; } = [];
    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-node health information within a cluster.
/// </summary>
public class NodeHealthInfo
{
    public string Name { get; set; } = "";
    public bool Ready { get; set; }
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
}

/// <summary>
/// An alert from Alertmanager.
/// </summary>
public class AlertInfo
{
    public string Name { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string State { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string RunbookUrl { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
}

/// <summary>
/// A silence from Alertmanager.
/// </summary>
public class SilenceInfo
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public string Comment { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public List<SilenceMatcher> Matchers { get; set; } = [];
}

/// <summary>
/// A matcher used in an Alertmanager silence to select which alerts to suppress.
/// </summary>
public class SilenceMatcher
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool IsEqual { get; set; } = true;
}

/// <summary>
/// Namespace-scoped resource metrics for a single app deployment.
/// </summary>
public class DeploymentMetricsSummary
{
    public string Namespace { get; set; } = "";
    public double CpuCores { get; set; }
    public double MemoryMiB { get; set; }
    public int PodCount { get; set; }
    public int RestartCount { get; set; }
    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// CPU%, memory%, and pod-count time series for a cluster's monitoring graphs.
/// </summary>
public sealed record ClusterMetricsHistory(
    List<TimeSeriesDataPoint> CpuPercent,
    List<TimeSeriesDataPoint> MemoryPercent,
    List<TimeSeriesDataPoint> PodCount);

/// <summary>
/// A single Prometheus scrape target with its health status.
/// </summary>
public class ScrapeTarget
{
    public string Pool { get; set; } = "";
    public string ScrapeUrl { get; set; } = "";
    public string Health { get; set; } = "";   // "up" | "down" | "unknown"
    public string LastError { get; set; } = "";
    public DateTime LastScrape { get; set; }
    public double LastScrapeDurationSeconds { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
}

/// <summary>
/// A single Prometheus alerting rule with its current evaluation state.
/// </summary>
public class AlertRule
{
    public string Name { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Query { get; set; } = "";
    public string State { get; set; } = "";    // inactive, pending, firing
    public string Severity { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RunbookUrl { get; set; } = "";
    public double DurationSeconds { get; set; }
    public double EvaluationTimeSeconds { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public Dictionary<string, string> Annotations { get; set; } = new();
}

/// <summary>
/// RabbitMQ cluster metrics scraped from Prometheus via the RabbitMQ Prometheus plugin.
///
/// The plugin's default <c>/metrics</c> endpoint aggregates per-object metrics, so everything
/// here is cluster- or node-wide. Per-queue depth is not available this way and comes from
/// <c>rabbitmqctl list_queues</c> instead (see <c>RabbitMQService.GetQueuesAsync</c>).
/// </summary>
public class RabbitMQMetricsSummary
{
    public string ClusterName { get; set; } = "";

    /// <summary>
    /// False when Prometheus holds no rabbitmq_* series for this cluster at all — a broker that
    /// is not being scraped, which the all-zeros card would otherwise render identically to a
    /// perfectly healthy idle one.
    /// </summary>
    public bool HasData { get; set; }

    public int Nodes { get; set; }

    // ── Depth ──
    public long TotalMessages { get; set; }
    public long ReadyMessages { get; set; }
    public long UnackedMessages { get; set; }
    public long MessageBytes { get; set; }

    // ── Topology / clients ──
    public int Connections { get; set; }
    public int Channels { get; set; }
    public int Consumers { get; set; }
    public int Queues { get; set; }

    // ── Throughput (per second, averaged over the query window) ──
    public double PublishRatePerSec { get; set; }

    /// <summary>Deliveries in every acknowledgement mode, plus basic.get.</summary>
    public double DeliverRatePerSec { get; set; }

    /// <summary>
    /// The manual-acknowledgement share of <see cref="DeliverRatePerSec"/>. Auto-ack deliveries
    /// are never acknowledged, so only this half is comparable with the ack rate.
    /// </summary>
    public double ManualAckDeliverPerSec { get; set; }

    public double AckRatePerSec { get; set; }
    public double RedeliverRatePerSec { get; set; }

    /// <summary>
    /// Messages published into an exchange that matched no queue — dropped or returned to the
    /// publisher. Sustained non-zero means a routing key or binding is wrong and messages are
    /// being lost, which no queue-depth metric reveals.
    /// </summary>
    public double UnroutableRatePerSec { get; set; }

    /// <summary>Published but not yet confirmed. A rising value means publishers are outrunning
    /// the broker's ability to confirm, and will eventually block.</summary>
    public long UnconfirmedMessages { get; set; }

    public double ConnectionsClosedPerSec { get; set; }
    public double ChannelsClosedPerSec { get; set; }

    /// <summary>Per-node resource usage, where alarms and single-node problems show up.</summary>
    public List<RabbitMQNodeMetrics> NodeMetrics { get; set; } = [];

    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Messages sitting unconsumed with nothing attached to drain them.
    ///
    /// The idle delivery rate is part of the test on purpose: a broker whose plugin does not
    /// export <c>rabbitmq_consumers</c> reports zero consumers, and without the second condition
    /// every healthy broker with a queue would be flagged. The per-queue table, which reads
    /// consumer counts straight from the broker, is the authoritative view.
    /// </summary>
    public bool StalledBacklog => ReadyMessages > 0 && Consumers == 0 && DeliverRatePerSec == 0;

    /// <summary>
    /// Consumers are being handed messages faster than they acknowledge them.
    ///
    /// Measured against manual-ack deliveries only. Comparing against all deliveries would read
    /// as permanent lag on auto-ack consumers, which never acknowledge anything by design.
    /// </summary>
    public bool AckLag => ManualAckDeliverPerSec > 0 && AckRatePerSec < ManualAckDeliverPerSec * 0.9;

    public bool AnyNodeAlarm => NodeMetrics.Any(n => n.MemoryAlarm || n.DiskAlarm);
}

/// <summary>
/// One RabbitMQ node's resource usage against the limits that trigger its alarms.
///
/// RabbitMQ blocks publishers when a node crosses its memory high watermark or drops below the
/// free-disk limit, so proximity to these limits is the earliest warning of a broker that is
/// about to stop accepting traffic.
/// </summary>
public class RabbitMQNodeMetrics
{
    public string Node { get; set; } = "";

    public double MemoryUsedBytes { get; set; }
    public double MemoryLimitBytes { get; set; }
    public double DiskFreeBytes { get; set; }
    public double DiskFreeLimitBytes { get; set; }
    public double OpenFds { get; set; }
    public double MaxFds { get; set; }
    public double OpenSockets { get; set; }
    public double MaxSockets { get; set; }

    public double MemoryUsedPercent => MemoryLimitBytes > 0 ? MemoryUsedBytes / MemoryLimitBytes * 100 : 0;
    public double FdUsedPercent     => MaxFds > 0 ? OpenFds / MaxFds * 100 : 0;
    public double SocketUsedPercent => MaxSockets > 0 ? OpenSockets / MaxSockets * 100 : 0;

    /// <summary>Memory alarm: usage has reached the high watermark and publishers are blocked.</summary>
    public bool MemoryAlarm => MemoryLimitBytes > 0 && MemoryUsedBytes >= MemoryLimitBytes;

    /// <summary>Disk alarm: free space has fallen to the configured floor.</summary>
    public bool DiskAlarm => DiskFreeLimitBytes > 0 && DiskFreeBytes <= DiskFreeLimitBytes;

    /// <summary>Approaching a limit without having crossed it yet.</summary>
    public bool NearLimit => (!MemoryAlarm && MemoryUsedPercent >= 80)
                             || (!DiskAlarm && DiskFreeLimitBytes > 0 && DiskFreeBytes <= DiskFreeLimitBytes * 2)
                             || FdUsedPercent >= 80
                             || SocketUsedPercent >= 80;
}

/// <summary>
/// Keycloak instance metrics scraped from Prometheus via Keycloak's management interface
/// (port 9000, <c>/metrics</c>), which serves Quarkus/Micrometer metrics.
/// </summary>
public class KeycloakMetricsSummary
{
    public string InstanceName { get; set; } = "";
    public string Namespace { get; set; } = "";

    /// <summary>False when no http_server_* series exist for this namespace — Keycloak is not
    /// being scraped, as opposed to serving no traffic.</summary>
    public bool HasData { get; set; }

    // ── HTTP ──
    public double RequestsPerSec { get; set; }
    public double ServerErrorsPerSec { get; set; }
    public double ClientErrorsPerSec { get; set; }
    public double AvgLatencyMs { get; set; }
    public double ActiveRequests { get; set; }

    /// <summary>Share of requests answered 5xx. The clearest single "something is broken" signal.</summary>
    public double ServerErrorPercent => RequestsPerSec > 0 ? ServerErrorsPerSec / RequestsPerSec * 100 : 0;

    // ── JVM ──
    public double HeapUsedBytes { get; set; }
    public double HeapCommittedBytes { get; set; }
    public double HeapUsedPercent => HeapCommittedBytes > 0 ? HeapUsedBytes / HeapCommittedBytes * 100 : 0;

    /// <summary>Seconds of GC pause per second of wall clock, for the worst pod. Above ~0.1 that
    /// JVM is spending more than a tenth of its time stopped, and latency will show it.</summary>
    public double GcPauseSecondsPerSec { get; set; }
    public double GcOverheadPercent { get; set; }

    // ── Database connection pool (Agroal) ──
    //
    // Every figure below describes the busiest single pod rather than the deployment as a whole:
    // each replica has its own pool, and a pool is exhausted or not on a per-pod basis.

    public double DbActiveConnections { get; set; }

    /// <summary>Spare connections in the most constrained pod's pool.</summary>
    public double DbAvailableConnections { get; set; }

    /// <summary>Threads blocked waiting for a connection. Anything above zero means the pool is
    /// too small for the load and requests are queueing on the database.</summary>
    public double DbAwaitingThreads { get; set; }
    public double DbBlockingTimeAvgMs { get; set; }
    public double DbMaxUsedConnections { get; set; }

    // ── Keycloak user events ──
    /// <summary>False when keycloak_user_events_total is absent — event metrics are off by
    /// default and need <c>--event-metrics-user-enabled=true</c>.</summary>
    public bool HasUserEventMetrics { get; set; }
    public double LoginsPerSec { get; set; }
    public double LoginErrorsPerSec { get; set; }
    public double TokenRefreshPerSec { get; set; }
    public double RegistrationsPerSec { get; set; }

    /// <summary>Share of login attempts that failed — a spike is either an outage or an attack.</summary>
    public double LoginErrorPercent =>
        LoginsPerSec + LoginErrorsPerSec > 0 ? LoginErrorsPerSec / (LoginsPerSec + LoginErrorsPerSec) * 100 : 0;

    /// <summary>Top failing user events, broken down by realm and error.</summary>
    public List<KeycloakEventBreakdown> TopErrors { get; set; } = [];

    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;

    public bool DbPoolSaturated => DbAwaitingThreads > 0;
    public bool HeapPressure    => HeapUsedPercent >= 90 || GcPauseSecondsPerSec >= 0.1;
}

/// <summary>
/// Why a workload's metrics are missing, told from what is actually in the cluster: whether a
/// ServiceMonitor exists, and whether Prometheus's selector accepts it.
/// </summary>
public class ScrapeDiagnosis
{
    public bool MonitorExists { get; init; }

    /// <summary>Labels on the ServiceMonitor — what Prometheus's selector is matched against.</summary>
    public Dictionary<string, string> MonitorLabels { get; init; } = [];

    /// <summary>matchLabels of the Prometheus resource's serviceMonitorSelector.</summary>
    public Dictionary<string, string> PrometheusSelector { get; init; } = [];

    public bool SelectorAcceptsMonitor { get; init; }

    public List<string> Findings { get; init; } = [];
    public string? Remedy { get; init; }
}

/// <summary>One row of the Keycloak failing-event breakdown.</summary>
public class KeycloakEventBreakdown
{
    public string Realm { get; set; } = "";
    public string Event { get; set; } = "";
    public string Error { get; set; } = "";
    public double RatePerSec { get; set; }
}

/// <summary>
/// Metrics for a CloudNativePG cluster scraped from Prometheus.
/// </summary>
public class CnpgMetricsSummary
{
    public string ClusterName { get; set; } = "";

    /// <summary>
    /// False when Prometheus returned no series at all for this cluster — it is not being
    /// scraped (no PodMonitor, or Prometheus does not select it) rather than merely idle.
    /// </summary>
    public bool HasData { get; set; }
    public double ReplicationLagSeconds { get; set; }
    public int TotalBackends { get; set; }
    public int ActiveQueries { get; set; }
    public List<CnpgDatabaseSize> DatabaseSizes { get; set; } = [];
    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;
}

public class CnpgDatabaseSize
{
    public string DatabaseName { get; set; } = "";
    public double SizeMiB { get; set; }
}

/// <summary>
/// Connects to a kube-prometheus-stack running in a cluster and retrieves
/// health metrics, time-series data, alerts, and silences via port-forwarded
/// or direct service access through the Kubernetes API proxy.
///
/// The service locates the Prometheus component on the cluster, builds a
/// Kubernetes client from the stored kubeconfig, and queries the Prometheus
/// HTTP API via the K8s API server proxy endpoint.
/// </summary>
public class PrometheusService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    KubernetesProxyClientPool clientPool,
    EntKube.Web.Services.Telemetry.PromQueryCache queryCache,
    ILogger<PrometheusService> logger)
{
    // ──────── Public API ────────

    /// <summary>
    /// Retrieves a health summary for the cluster by querying key Prometheus
    /// metrics (CPU, memory, nodes, pods, disk). Returns a failure result if
    /// the cluster isn't found, has no Prometheus component, or lacks kubeconfig.
    /// </summary>
    public async Task<KubernetesOperationResult<ClusterHealthSummary>> GetClusterHealthAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<ClusterHealthSummary>.Failure(error!);

        return await WithServiceAsync<ClusterHealthSummary>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                static async Task<double> Scalar(HttpClient h, string url, CancellationToken t) =>
                    ExtractScalarValue(await h.GetStringAsync(url, t));

                // Detect which metric sources are available so we can use the right queries.
                double hasKsm = await Scalar(http, $"{baseUrl}/api/v1/query?query=count%28kube_pod_info%29", token);
                double hasNex = await Scalar(http, $"{baseUrl}/api/v1/query?query=count%28node_cpu_seconds_total%29", token);

                double cpu, mem, nodes, rNode, pods, rPods, disk;

                if (hasNex > 0 && hasKsm > 0)
                {
                    // Preferred: kube-prometheus-stack full stack (node-exporter + kube-state-metrics).
                    cpu   = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("100 - (avg(rate(node_cpu_seconds_total{mode=\"idle\"}[5m])) * 100)")}", token);
                    mem   = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("100 - (avg(node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes) * 100)")}", token);
                    nodes = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("count(kube_node_info)")}", token);
                    rNode = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("count(kube_node_status_condition{condition=\"Ready\",status=\"true\"})")}", token);
                    pods  = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("count(kube_pod_info)")}", token);
                    rPods = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("sum(kube_pod_status_phase{phase=\"Running\"})")}", token);
                    disk  = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("100 - (avg(node_filesystem_avail_bytes{mountpoint=\"/\"} / node_filesystem_size_bytes{mountpoint=\"/\"}) * 100)")}", token);
                }
                else
                {
                    // Fallback: kubelet/cAdvisor metrics only (no kube-state-metrics or node-exporter).
                    // These are available from the kubelet job in any kube-prometheus-stack installation.
                    logger.LogInformation(
                        "kube-state-metrics/node-exporter not scraped — using kubelet/cAdvisor metrics for cluster health");

                    cpu   = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("100 * sum(rate(container_cpu_usage_seconds_total{container!=\"\",namespace!=\"\"}[5m])) / sum(machine_cpu_cores)")}", token);
                    mem   = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("100 * sum(container_memory_working_set_bytes{container!=\"\",namespace!=\"\"}) / sum(machine_memory_bytes)")}", token);
                    nodes = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("count(count by (node) (kubelet_running_pods))")}", token);
                    rNode = nodes; // kubelet_running_pods only reports healthy nodes
                    pods  = await Scalar(http, $"{baseUrl}/api/v1/query?query={Q("sum(kubelet_running_pods)")}", token);
                    rPods = pods;
                    disk  = 0; // not available without node-exporter
                }

                return new ClusterHealthSummary
                {
                    CpuUsagePercent    = cpu,
                    MemoryUsagePercent = mem,
                    TotalNodes         = (int)nodes,
                    ReadyNodes         = (int)rNode,
                    TotalPods          = (int)pods,
                    RunningPods        = (int)rPods,
                    DiskUsagePercent   = disk,
                    QueriedAt          = DateTime.UtcNow
                };
            },
            $"cluster health for {clusterId}", ct);
    }

    /// <summary>
    /// Queries a Prometheus range query over the given duration and returns time-series data.
    /// </summary>
    public async Task<KubernetesOperationResult<List<PrometheusTimeSeries>>> GetMetricRangeAsync(
        Guid clusterId, string query, TimeSpan duration, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<PrometheusTimeSeries>>.Failure(error!);

        long end   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = end - (long)duration.TotalSeconds;
        int  step  = Math.Max(15, (int)(duration.TotalSeconds / 100));
        string encodedQuery = Q(query);

        // Keyed on the query and window, not on `end` — that moves every second, which would make every
        // call a distinct key and cache nothing. Within the cache's few seconds, a chart whose window
        // slid by that much is indistinguishable from a fresh one.
        return await queryCache.GetOrFetchAsync(
            EntKube.Web.Services.Telemetry.PromQueryCache.Key(clusterId, "range", query, (long)duration.TotalSeconds),
            async () => await WithServiceAsync<List<PrometheusTimeSeries>>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query_range?query={encodedQuery}&start={start}&end={end}&step={step}", token);
                return ParseRangeQueryResult(json);
            },
            $"range query for {clusterId}", ct));
    }

    /// <summary>
    /// Prometheus label-values lookup (<c>/api/v1/label/&lt;label&gt;/values</c>), optionally constrained by a
    /// series <paramref name="matchSelector"/> (e.g. <c>{k8s_namespace_name=~"a|b"}</c>) over a lookback
    /// window. Powers the metrics explorer's metric-name and service dropdowns now that metrics live in
    /// Prometheus rather than the native store.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetLabelValuesAsync(
        Guid clusterId, string label, string? matchSelector, TimeSpan lookback, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<string>>.Failure(error!);

        long end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = end - (long)lookback.TotalSeconds;
        string path = $"/api/v1/label/{Uri.EscapeDataString(label)}/values?start={start}&end={end}";
        if (!string.IsNullOrEmpty(matchSelector))
            path += $"&match[]={Q(matchSelector)}";

        return await queryCache.GetOrFetchAsync(
            EntKube.Web.Services.Telemetry.PromQueryCache.Key(clusterId, "labels", label, matchSelector, (long)lookback.TotalSeconds),
            async () => await WithServiceAsync<List<string>>(
                info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
                async (http, baseUrl, token) => ParseStringArray(await http.GetStringAsync($"{baseUrl}{path}", token)),
                $"label values {label} for {clusterId}", ct));
    }

    private static List<string> ParseStringArray(string json)
    {
        var result = new List<string>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in data.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) result.Add(e.GetString()!);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Retrieves active alerts from Alertmanager.
    /// </summary>
    public async Task<KubernetesOperationResult<List<AlertInfo>>> GetAlertsAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<AlertInfo>>.Failure(error!);

        return await WithServiceAsync<List<AlertInfo>>(
            info.Kubeconfig, info.Config.Namespace, info.Config.AlertmanagerServiceName, info.Config.AlertmanagerServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}/api/v2/alerts", token);
                return ParseAlertmanagerAlerts(json);
            },
            $"alerts for {clusterId}", ct);
    }

    /// <summary>
    /// Retrieves silences from Alertmanager.
    /// </summary>
    public async Task<KubernetesOperationResult<List<SilenceInfo>>> GetSilencesAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<SilenceInfo>>.Failure(error!);

        return await WithServiceAsync<List<SilenceInfo>>(
            info.Kubeconfig, info.Config.Namespace, info.Config.AlertmanagerServiceName, info.Config.AlertmanagerServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}/api/v2/silences", token);
                return ParseAlertmanagerSilences(json);
            },
            $"silences for {clusterId}", ct);
    }

    /// <summary>
    /// Creates a new silence in Alertmanager.
    /// </summary>
    public async Task<KubernetesOperationResult> CreateSilenceAsync(
        Guid clusterId, string comment, string createdBy, TimeSpan duration,
        List<SilenceMatcher> matchers, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult.Failure(error!);

        string body = JsonSerializer.Serialize(new
        {
            matchers = matchers.Select(m => new { name = m.Name, value = m.Value, isRegex = m.IsRegex, isEqual = m.IsEqual }).ToArray(),
            startsAt = DateTime.UtcNow.ToString("o"),
            endsAt = DateTime.UtcNow.Add(duration).ToString("o"),
            createdBy,
            comment
        });

        var result = await WithServiceAsync<bool>(
            info.Kubeconfig, info.Config.Namespace, info.Config.AlertmanagerServiceName, info.Config.AlertmanagerServicePort,
            async (http, baseUrl, token) =>
            {
                using StringContent content = new(body, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await http.PostAsync($"{baseUrl}/api/v2/silences", content, token);
                response.EnsureSuccessStatusCode();
                return true;
            },
            $"create silence for {clusterId}", ct);

        return result.IsSuccess ? KubernetesOperationResult.Success() : KubernetesOperationResult.Failure(result.Error!);
    }

    /// <summary>
    /// Queries namespace-scoped CPU, memory, pod count, and restart metrics for a
    /// specific app deployment. Requires kube-prometheus-stack on the target cluster.
    /// </summary>
    public async Task<KubernetesOperationResult<DeploymentMetricsSummary>> GetDeploymentMetricsAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        string kubeconfig;
        PrometheusConfig config;
        string ns;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            AppDeployment? deployment = await db.AppDeployments
                .Include(d => d.Cluster)
                    .ThenInclude(c => c.Components)
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);

            if (deployment is null)
                return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("Deployment not found.");

            if (string.IsNullOrWhiteSpace(deployment.Cluster.Kubeconfig))
                return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("No kubeconfig configured.");

            ClusterComponent? prometheusComponent = deployment.Cluster.Components.FirstOrDefault(c =>
                c.Name.Contains("prometheus", StringComparison.OrdinalIgnoreCase));

            if (prometheusComponent is null)
                return KubernetesOperationResult<DeploymentMetricsSummary>.Failure(
                    "No prometheus component found on this cluster.");

            kubeconfig = deployment.Cluster.Kubeconfig;
            config = GetPrometheusConfig(prometheusComponent) ?? new PrometheusConfig();
            ns = deployment.Namespace;
        }

        return await WithServiceAsync<DeploymentMetricsSummary>(
            kubeconfig, config.Namespace, config.ServiceName, config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string cpuQ = Q($"sum(rate(container_cpu_usage_seconds_total{{namespace=\"{ns}\",container!=\"\"}}[5m]))");
                string memQ = Q($"sum(container_memory_working_set_bytes{{namespace=\"{ns}\",container!=\"\"}})");

                double cpuVal   = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={cpuQ}", token));
                double memBytes = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={memQ}", token));

                // Try kube-state-metrics for pod/restart counts; fall back to cAdvisor-derived counts.
                string ksmPodQ = Q($"count(kube_pod_info{{namespace=\"{ns}\"}})");
                double podCount = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={ksmPodQ}", token));
                double restarts = 0;

                if (podCount == 0)
                {
                    // kube-state-metrics not available — count distinct pods from cAdvisor
                    string cAdvisorPodQ = Q($"count(count by (pod) (container_cpu_usage_seconds_total{{namespace=\"{ns}\",container!=\"\"}}))");
                    podCount = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={cAdvisorPodQ}", token));
                }
                else
                {
                    // kube-state-metrics available — also get restart count
                    string restartQ = Q($"sum(kube_pod_container_status_restarts_total{{namespace=\"{ns}\"}})");
                    restarts = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={restartQ}", token));
                }

                return new DeploymentMetricsSummary
                {
                    Namespace    = ns,
                    CpuCores     = Math.Round(cpuVal, 4),
                    MemoryMiB    = Math.Round(memBytes / (1024 * 1024), 1),
                    PodCount     = (int)podCount,
                    RestartCount = (int)restarts,
                    QueriedAt    = DateTime.UtcNow
                };
            },
            $"deployment metrics {deploymentId}", ct);
    }

    /// <summary>
    /// Returns CPU%, memory%, and pod-count time series for a cluster, selecting
    /// node-exporter/kube-state-metrics queries when available and falling back to
    /// kubelet/cAdvisor metrics when they are not scraped by this Prometheus instance.
    /// </summary>
    public async Task<KubernetesOperationResult<ClusterMetricsHistory>> GetClusterMetricsHistoryAsync(
        Guid clusterId, TimeSpan duration, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<ClusterMetricsHistory>.Failure(error!);

        long end   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = end - (long)duration.TotalSeconds;
        int  step  = Math.Max(15, (int)(duration.TotalSeconds / 100));

        return await WithServiceAsync<ClusterMetricsHistory>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                static async Task<double> Scalar(HttpClient h, string url, CancellationToken t) =>
                    ExtractScalarValue(await h.GetStringAsync(url, t));

                double hasNex = await Scalar(http, $"{baseUrl}/api/v1/query?query=count%28node_cpu_seconds_total%29", token);
                double hasKsm = await Scalar(http, $"{baseUrl}/api/v1/query?query=count%28kube_pod_info%29", token);

                string cpuQ, memQ, podQ;

                if (hasNex > 0 && hasKsm > 0)
                {
                    cpuQ = Q("100 - (avg(rate(node_cpu_seconds_total{mode=\"idle\"}[5m])) * 100)");
                    memQ = Q("100 - (avg(node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes) * 100)");
                    podQ = Q("sum(kube_pod_status_phase{phase=\"Running\"})");
                }
                else
                {
                    cpuQ = Q("100 * sum(rate(container_cpu_usage_seconds_total{container!=\"\",namespace!=\"\"}[5m])) / sum(machine_cpu_cores)");
                    memQ = Q("100 * sum(container_memory_working_set_bytes{container!=\"\",namespace!=\"\"}) / sum(machine_memory_bytes)");
                    podQ = Q("sum(kubelet_running_pods)");
                }

                string cpuJson = await http.GetStringAsync($"{baseUrl}/api/v1/query_range?query={cpuQ}&start={start}&end={end}&step={step}", token);
                string memJson = await http.GetStringAsync($"{baseUrl}/api/v1/query_range?query={memQ}&start={start}&end={end}&step={step}", token);
                string podJson = await http.GetStringAsync($"{baseUrl}/api/v1/query_range?query={podQ}&start={start}&end={end}&step={step}", token);

                List<PrometheusTimeSeries> cpuSeries = ParseRangeQueryResult(cpuJson);
                List<PrometheusTimeSeries> memSeries = ParseRangeQueryResult(memJson);
                List<PrometheusTimeSeries> podSeries = ParseRangeQueryResult(podJson);

                return new ClusterMetricsHistory(
                    cpuSeries.Count > 0 ? cpuSeries[0].DataPoints : [],
                    memSeries.Count > 0 ? memSeries[0].DataPoints : [],
                    podSeries.Count > 0 ? podSeries[0].DataPoints : []);
            },
            $"metrics history for {clusterId}", ct);
    }

    /// <summary>
    /// Aggregate CPU, memory, and pod metrics for all deployments in a single app
    /// by querying each cluster's Prometheus with a namespace regex filter.
    /// </summary>
    public async Task<KubernetesOperationResult<DeploymentMetricsSummary>> GetAppMetricsAsync(
        Guid appId, CancellationToken ct = default)
    {
        List<(string Kubeconfig, PrometheusConfig Config, string NsRegex)> clusterQueries;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            List<AppDeployment> deployments = await db.AppDeployments
                .Include(d => d.Cluster)
                    .ThenInclude(c => c.Components)
                .Where(d => d.AppId == appId && d.Cluster.KubeconfigSecretId != null)
                .ToListAsync(ct);

            if (deployments.Count == 0)
                return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("No deployments with clusters configured for this app.");

            clusterQueries = deployments
                .GroupBy(d => d.ClusterId)
                .Select(g =>
                {
                    AppDeployment first = g.First();
                    ClusterComponent? pc = first.Cluster.Components
                        .FirstOrDefault(c => c.Name.Contains("prometheus", StringComparison.OrdinalIgnoreCase));
                    PrometheusConfig cfg = pc is not null ? GetPrometheusConfig(pc) : new PrometheusConfig();
                    string nsRegex = string.Join("|", g.Select(d => Regex.Escape(d.Namespace)));
                    return (first.Cluster.Kubeconfig!, cfg, nsRegex);
                })
                .ToList();
        }

        return await AggregateMetricsAsync(clusterQueries, $"app metrics {appId}", ct);
    }

    /// <summary>
    /// Aggregate CPU, memory, and pod metrics across all apps belonging to a customer.
    /// </summary>
    public async Task<KubernetesOperationResult<DeploymentMetricsSummary>> GetCustomerMetricsAsync(
        Guid customerId, CancellationToken ct = default)
    {
        List<(string Kubeconfig, PrometheusConfig Config, string NsRegex)> clusterQueries;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            List<AppDeployment> deployments = await db.AppDeployments
                .Include(d => d.App)
                .Include(d => d.Cluster)
                    .ThenInclude(c => c.Components)
                .Where(d => d.App.CustomerId == customerId
                         && d.Cluster.KubeconfigSecretId != null)
                .ToListAsync(ct);

            if (deployments.Count == 0)
                return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("No deployments with clusters configured for this customer.");

            clusterQueries = deployments
                .GroupBy(d => d.ClusterId)
                .Select(g =>
                {
                    AppDeployment first = g.First();
                    ClusterComponent? pc = first.Cluster.Components
                        .FirstOrDefault(c => c.Name.Contains("prometheus", StringComparison.OrdinalIgnoreCase));
                    PrometheusConfig cfg = pc is not null ? GetPrometheusConfig(pc) : new PrometheusConfig();
                    string nsRegex = string.Join("|", g.Select(d => Regex.Escape(d.Namespace)).Distinct());
                    return (first.Cluster.Kubeconfig!, cfg, nsRegex);
                })
                .ToList();
        }

        return await AggregateMetricsAsync(clusterQueries, $"customer metrics {customerId}", ct);
    }

    /// <summary>
    /// Retrieves CloudNativePG cluster metrics from Prometheus: replication lag,
    /// backend count, active queries, and per-database sizes.
    /// </summary>
    public async Task<KubernetesOperationResult<CnpgMetricsSummary>> GetCnpgClusterMetricsAsync(
        Guid clusterId, string cnpgClusterName, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<CnpgMetricsSummary>.Failure(error!);

        return await WithServiceAsync<CnpgMetricsSummary>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string lagJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"max({CnpgSelector("cnpg_pg_replication_lag", cnpgClusterName)})")}",
                    token);
                string backendsJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"sum({CnpgSelector("cnpg_backends_total", cnpgClusterName)})")}",
                    token);
                string queriesJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"sum({CnpgSelector("cnpg_pg_stat_activity_count", cnpgClusterName, "state=\"active\"")})")}",
                    token);
                string sizesJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q(CnpgSelector("cnpg_pg_database_size_bytes", cnpgClusterName, "datname!=\"\""))}",
                    token);

                List<PrometheusMetricResult> lagResults = ParseInstantQueryResult(lagJson);
                List<PrometheusMetricResult> backendResults = ParseInstantQueryResult(backendsJson);
                List<PrometheusMetricResult> queryResults = ParseInstantQueryResult(queriesJson);
                List<PrometheusMetricResult> sizeResults = ParseInstantQueryResult(sizesJson);

                return new CnpgMetricsSummary
                {
                    ClusterName = cnpgClusterName,
                    // Every query answering with zero series means Prometheus holds no cnpg_*
                    // data for this cluster at all — a different situation from a healthy idle
                    // database, which the all-zeros card would otherwise look identical to.
                    HasData = lagResults.Count > 0 || backendResults.Count > 0
                              || queryResults.Count > 0 || sizeResults.Count > 0,
                    ReplicationLagSeconds = lagResults.Count > 0 ? lagResults[0].Value : 0,
                    TotalBackends = backendResults.Count > 0 ? (int)backendResults[0].Value : 0,
                    ActiveQueries = queryResults.Count > 0 ? (int)queryResults[0].Value : 0,
                    DatabaseSizes = sizeResults
                        .Select(r => new CnpgDatabaseSize
                        {
                            DatabaseName = r.Labels.TryGetValue("datname", out string? dn) ? dn : "",
                            SizeMiB = r.Value / 1024.0 / 1024.0
                        })
                        .OrderByDescending(d => d.SizeMiB)
                        .ToList()
                };
            },
            $"CNPG metrics for cluster {cnpgClusterName}", ct);
    }

    /// <summary>
    /// Builds the PromQL selector for one CNPG metric, scoped to a single database cluster.
    ///
    /// Every matcher goes in ONE brace block: two adjacent blocks
    /// (<c>metric{a="1"}{b="2"}</c>) are a PromQL parse error, and Prometheus answers the whole
    /// request with 400 Bad Request rather than ignoring the second one.
    ///
    /// Which label carries the cluster name depends on how the instances are scraped: CNPG's own
    /// exporter emits <c>cluster</c>, while some setups relabel it to <c>cluster_name</c>. Both are
    /// matched with <c>or</c> so the panel works either way — an unmatched alternative contributes
    /// no series rather than an error.
    /// </summary>
    public static string CnpgSelector(string metric, string cnpgClusterName, string? extraMatcher = null)
    {
        string extra = string.IsNullOrEmpty(extraMatcher) ? "" : $",{extraMatcher}";
        return $"{metric}{{cluster=\"{cnpgClusterName}\"{extra}}}"
             + $" or {metric}{{cluster_name=\"{cnpgClusterName}\"{extra}}}";
    }

    /// <summary>
    /// Works out why a CNPG database produces no metrics, by comparing the two halves that have
    /// to line up: the PodMonitor CNPG creates for the database, and the selector the Prometheus
    /// resource uses to decide which PodMonitors it will scrape.
    ///
    /// Both halves are read from the cluster, so this reports what is actually there rather than
    /// what the configuration implies.
    /// </summary>
    public async Task<KubernetesOperationResult<CnpgScrapeDiagnosis>> DiagnoseCnpgScrapeAsync(
        Guid clusterId, string cnpgClusterName, string cnpgNamespace, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<CnpgScrapeDiagnosis>.Failure(error!);

        try
        {
            Kubernetes k8s = CreateK8sClient(info.Kubeconfig);

            JsonNode? podMonitor = await FindCnpgPodMonitorAsync(k8s, cnpgNamespace, cnpgClusterName, ct);
            JsonNode? prometheus = await FindPrometheusResourceAsync(k8s, ct);

            Dictionary<string, string> pmLabels = ReadLabels(podMonitor?["metadata"]?["labels"]);
            Dictionary<string, string> selector = ReadLabels(prometheus?["spec"]?["podMonitorSelector"]?["matchLabels"]);

            // An empty (but present) podMonitorSelector means "every PodMonitor"; a selector with
            // matchLabels means "only the ones carrying these labels", which is what the
            // kube-prometheus-stack default produces via its release label.
            bool selectorPresent = prometheus?["spec"]?["podMonitorSelector"] is not null;
            bool selectsAll = !selectorPresent || selector.Count == 0;
            bool matches = selectsAll || selector.All(kv =>
                pmLabels.TryGetValue(kv.Key, out string? v) && v == kv.Value);

            List<string> findings = [];
            string? remedy = null;

            if (podMonitor is null)
            {
                findings.Add(
                    $"No PodMonitor for '{cnpgClusterName}' exists in namespace {cnpgNamespace}. " +
                    "CNPG creates one only when the Cluster resource sets spec.monitoring.enablePodMonitor.");
                remedy = "Use \"Enable metrics\" above, then give the operator a few seconds to reconcile.";
            }
            else if (!matches)
            {
                string want = string.Join(", ", selector.Select(kv => $"{kv.Key}={kv.Value}"));
                string have = pmLabels.Count == 0 ? "(no labels)" : string.Join(", ", pmLabels.Select(kv => $"{kv.Key}={kv.Value}"));
                findings.Add($"The PodMonitor exists but Prometheus does not select it: it scrapes only PodMonitors labelled {want}, and this one has {have}.");
                remedy =
                    "Add these to the kube-prometheus-stack component's values (Cluster → Components → " +
                    "kube-prometheus-stack → Values), then Save & Apply:\n" +
                    "prometheus:\n  prometheusSpec:\n    podMonitorSelectorNilUsesHelmValues: false\n" +
                    "    serviceMonitorSelectorNilUsesHelmValues: false";
            }
            else
            {
                findings.Add("The PodMonitor exists and Prometheus's selector accepts it.");
                remedy =
                    "Scraping should be running. If metrics are still missing, check the CNPG pods' " +
                    "metrics port in Prometheus → Targets — a target listed as down points at the database, not the wiring.";
            }

            // A namespace selector that is absent (rather than empty) confines Prometheus to its
            // own namespace, which no database namespace would ever satisfy.
            bool nsSelectorPresent = prometheus?["spec"]?["podMonitorNamespaceSelector"] is not null;
            if (podMonitor is not null && !nsSelectorPresent)
            {
                findings.Add(
                    "Prometheus has no podMonitorNamespaceSelector, so it only looks in its own namespace — " +
                    $"it will not see anything in {cnpgNamespace}.");
            }

            return KubernetesOperationResult<CnpgScrapeDiagnosis>.Success(new CnpgScrapeDiagnosis
            {
                PodMonitorExists = podMonitor is not null,
                PodMonitorLabels = pmLabels,
                PrometheusSelector = selector,
                SelectorAcceptsPodMonitor = podMonitor is not null && matches,
                Findings = findings,
                Remedy = remedy,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CNPG scrape diagnosis failed for {Cluster}", cnpgClusterName);
            return KubernetesOperationResult<CnpgScrapeDiagnosis>.Failure(DescribeK8sError(ex));
        }
    }

    /// <summary>
    /// CNPG names the PodMonitor after the Cluster, but a hand-written or relabelled one may not
    /// follow that, so a name miss falls back to the label CNPG stamps on everything it owns.
    /// </summary>
    private static async Task<JsonNode?> FindCnpgPodMonitorAsync(
        Kubernetes k8s, string ns, string cnpgClusterName, CancellationToken ct)
    {
        JsonArray items = await ListCustomObjectsAsync(
            k8s, "monitoring.coreos.com", "v1", ns, "podmonitors", ct);

        foreach (JsonNode? item in items)
        {
            if (item?["metadata"]?["name"]?.GetValue<string>() == cnpgClusterName)
                return item;
        }

        foreach (JsonNode? item in items)
        {
            string? owned = item?["spec"]?["selector"]?["matchLabels"]?["cnpg.io/cluster"]?.GetValue<string>();
            if (owned == cnpgClusterName) return item;
        }

        return null;
    }

    private static async Task<JsonNode?> FindPrometheusResourceAsync(Kubernetes k8s, CancellationToken ct)
    {
        try
        {
            object raw = await k8s.CustomObjects.ListCustomObjectForAllNamespacesAsync(
                "monitoring.coreos.com", "v1", "prometheuses", cancellationToken: ct);
            JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(raw));
            return (root?["items"] as JsonArray)?.FirstOrDefault();
        }
        catch
        {
            return null;   // no prometheus-operator CRDs — the caller reports what it can
        }
    }

    private static async Task<JsonArray> ListCustomObjectsAsync(
        Kubernetes k8s, string group, string version, string ns, string plural, CancellationToken ct)
    {
        try
        {
            object raw = await k8s.CustomObjects.ListNamespacedCustomObjectAsync(
                group, version, ns, plural, cancellationToken: ct);
            JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(raw));
            return root?["items"] as JsonArray ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, string> ReadLabels(JsonNode? node)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (node is not JsonObject obj) return result;
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (kv.Value?.GetValue<string>() is { } value)
                result[kv.Key] = value;
        }
        return result;
    }

    private static string DescribeK8sError(Exception ex) =>
        ex is k8s.Autorest.HttpOperationException http && http.Response is not null
            ? $"{http.Response.StatusCode}: {http.Message}"
            : ex.Message;

    /// <summary>
    /// Retrieves all active scrape targets from Prometheus with their health (up/down).
    /// </summary>
    public async Task<KubernetesOperationResult<List<ScrapeTarget>>> GetScrapeTargetsAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<ScrapeTarget>>.Failure(error!);

        return await WithServiceAsync<List<ScrapeTarget>>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}/api/v1/targets?state=active", token);
                return ParseScrapeTargets(json);
            },
            $"scrape targets for cluster {clusterId}", ct);
    }

    /// <summary>
    /// Retrieves all alerting rules from Prometheus with their current evaluation state.
    /// </summary>
    public async Task<KubernetesOperationResult<List<AlertRule>>> GetAlertRulesAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<AlertRule>>.Failure(error!);

        return await WithServiceAsync<List<AlertRule>>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}/api/v1/rules?type=alert", token);
                return ParseAlertRules(json);
            },
            $"alert rules for cluster {clusterId}", ct);
    }

    /// <summary>
    /// Retrieves RabbitMQ metrics from Prometheus (via RabbitMQ Prometheus plugin).
    /// </summary>
    public async Task<KubernetesOperationResult<RabbitMQMetricsSummary>> GetRabbitMQMetricsAsync(
        Guid clusterId, string rabbitMQName, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<RabbitMQMetricsSummary>.Failure(error!);

        return await WithServiceAsync<RabbitMQMetricsSummary>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                Dictionary<string, List<PrometheusMetricResult>> r =
                    await QueryManyAsync(http, baseUrl, BuildRabbitMQQueries(rabbitMQName), token);

                Dictionary<string, RabbitMQNodeMetrics> nodes = [];
                void PerNode(string key, Action<RabbitMQNodeMetrics, double> assign)
                {
                    foreach (PrometheusMetricResult m in r[key])
                    {
                        // The node families carry no labels of their own; rabbitmq_node arrives
                        // from the identity join. Falling back to the scrape target keeps the
                        // rows distinguishable if that join ever comes back without it.
                        string node = m.Labels.GetValueOrDefault("rabbitmq_node", "");
                        if (string.IsNullOrEmpty(node)) node = m.Labels.GetValueOrDefault("instance", "");
                        if (string.IsNullOrEmpty(node)) continue;

                        if (!nodes.TryGetValue(node, out RabbitMQNodeMetrics? nm))
                            nodes[node] = nm = new RabbitMQNodeMetrics { Node = node };
                        assign(nm, m.Value);
                    }
                }

                PerNode("mem",        (n, v) => n.MemoryUsedBytes    = v);
                PerNode("memLimit",   (n, v) => n.MemoryLimitBytes   = v);
                PerNode("disk",       (n, v) => n.DiskFreeBytes      = v);
                PerNode("diskLimit",  (n, v) => n.DiskFreeLimitBytes = v);
                PerNode("fds",        (n, v) => n.OpenFds            = v);
                PerNode("fdsMax",     (n, v) => n.MaxFds             = v);
                PerNode("sockets",    (n, v) => n.OpenSockets        = v);
                PerNode("socketsMax", (n, v) => n.MaxSockets         = v);

                return new RabbitMQMetricsSummary
                {
                    ClusterName             = rabbitMQName,
                    HasData                 = r.Values.Any(v => v.Count > 0),
                    Nodes                   = (int)Scalar(r["nodes"]),
                    TotalMessages           = (long)Scalar(r["total"]),
                    ReadyMessages           = (long)Scalar(r["ready"]),
                    UnackedMessages         = (long)Scalar(r["unacked"]),
                    MessageBytes            = (long)Scalar(r["bytes"]),
                    Connections             = (int)Scalar(r["connections"]),
                    Channels                = (int)Scalar(r["channels"]),
                    Consumers               = (int)Scalar(r["consumers"]),
                    Queues                  = (int)Scalar(r["queues"]),
                    PublishRatePerSec       = Math.Round(Scalar(r["publish"]), 2),
                    DeliverRatePerSec       = Math.Round(Scalar(r["deliver"]), 2),
                    ManualAckDeliverPerSec  = Math.Round(Scalar(r["deliverAck"]), 2),
                    AckRatePerSec           = Math.Round(Scalar(r["ack"]), 2),
                    RedeliverRatePerSec     = Math.Round(Scalar(r["redeliver"]), 2),
                    UnroutableRatePerSec    = Math.Round(Scalar(r["unroutable"]), 2),
                    UnconfirmedMessages     = (long)Scalar(r["unconfirmed"]),
                    ConnectionsClosedPerSec = Math.Round(Scalar(r["connClosed"]), 2),
                    ChannelsClosedPerSec    = Math.Round(Scalar(r["chanClosed"]), 2),
                    NodeMetrics             = [.. nodes.Values.OrderBy(n => n.Node)],
                };
            },
            $"RabbitMQ metrics for {rabbitMQName}", ct);
    }

    /// <summary>
    /// The PromQL behind the RabbitMQ panel, keyed by the field it populates.
    ///
    /// Separated from the query execution so the metric names are assertable: a name that does
    /// not exist returns an empty vector rather than an error, so a typo shows up as a tile that
    /// reads zero forever — indistinguishable from a genuinely idle broker.
    ///
    /// Every name here comes from the rabbitmq_prometheus plugin's own metrics reference. Note
    /// that the counters for delivery, acknowledgement and routing failures live on the
    /// <c>channel</c>, not the queue; only publishes are counted on both.
    /// </summary>
    public static Dictionary<string, string> BuildRabbitMQQueries(string rabbitMQName)
    {
        // Identity is the only family carrying the cluster name, so it doubles as the node count.
        string identity = RabbitMQIdentity(rabbitMQName);

        return new Dictionary<string, string>
        {
            ["nodes"]       = $"sum({identity})",
            ["total"]       = Agg("rabbitmq_queue_messages", rabbitMQName),
            ["ready"]       = Agg("rabbitmq_queue_messages_ready", rabbitMQName),
            ["unacked"]     = Agg("rabbitmq_queue_messages_unacked", rabbitMQName),
            ["bytes"]       = Agg("rabbitmq_queue_messages_bytes", rabbitMQName),
            ["connections"] = Agg("rabbitmq_connections", rabbitMQName),
            ["channels"]    = Agg("rabbitmq_channels", rabbitMQName),
            ["consumers"]   = Agg("rabbitmq_consumers", rabbitMQName),
            ["queues"]      = Agg("rabbitmq_queues", rabbitMQName),

            ["publish"]     = Rate("rabbitmq_channel_messages_published_total", rabbitMQName),

            // Delivery is split by acknowledgement mode, and the two counters are disjoint:
            // "_delivered_total" counts only auto-ack deliveries, "_delivered_ack_total" only
            // manual-ack ones. Querying just the former reports zero for every manual-ack
            // consumer, which is the normal case. basic.get is included so polling consumers
            // are not invisible either.
            ["deliver"]     = RateSum(rabbitMQName,
                                  "rabbitmq_channel_messages_delivered_ack_total",
                                  "rabbitmq_channel_messages_delivered_total",
                                  "rabbitmq_channel_get_ack_total",
                                  "rabbitmq_channel_get_total"),

            // Only manual-ack deliveries can be acknowledged, so this is the half that the ack
            // rate is comparable with — auto-ack traffic never produces an ack.
            ["deliverAck"]  = RateSum(rabbitMQName,
                                  "rabbitmq_channel_messages_delivered_ack_total",
                                  "rabbitmq_channel_get_ack_total"),

            ["ack"]         = Rate("rabbitmq_channel_messages_acked_total", rabbitMQName),
            ["redeliver"]   = Rate("rabbitmq_channel_messages_redelivered_total", rabbitMQName),
            // Dropped (published non-mandatory) and returned (published mandatory) are two halves
            // of the same fault — a message that matched no binding — so they are one rate here.
            ["unroutable"]  = RateSum(rabbitMQName,
                                  "rabbitmq_channel_messages_unroutable_dropped_total",
                                  "rabbitmq_channel_messages_unroutable_returned_total"),
            ["unconfirmed"] = Agg("rabbitmq_channel_messages_unconfirmed", rabbitMQName),
            ["connClosed"]  = Rate("rabbitmq_connections_closed_total", rabbitMQName),
            ["chanClosed"]  = Rate("rabbitmq_channels_closed_total", rabbitMQName),

            // Per-node, deliberately unaggregated: one node hitting its memory watermark blocks
            // publishers cluster-wide, and a sum across nodes would hide which one. The join also
            // supplies rabbitmq_node, since the node families themselves carry no labels at all.
            ["mem"]         = PerNode("rabbitmq_process_resident_memory_bytes", rabbitMQName),
            ["memLimit"]    = PerNode("rabbitmq_resident_memory_limit_bytes", rabbitMQName),
            ["disk"]        = PerNode("rabbitmq_disk_space_available_bytes", rabbitMQName),
            ["diskLimit"]   = PerNode("rabbitmq_disk_space_available_limit_bytes", rabbitMQName),
            ["fds"]         = PerNode("rabbitmq_process_open_fds", rabbitMQName),
            ["fdsMax"]      = PerNode("rabbitmq_process_max_fds", rabbitMQName),
            ["sockets"]     = PerNode("rabbitmq_process_open_tcp_sockets", rabbitMQName),
            ["socketsMax"]  = PerNode("rabbitmq_process_max_tcp_sockets", rabbitMQName),
        };
    }

    /// <summary>
    /// The <c>rabbitmq_identity_info</c> series for one cluster — the join partner that carries
    /// the cluster name.
    ///
    /// The endpoint is pinned because the plugin emits identity info once per scrape path: a
    /// ServiceMonitor that also scrapes <c>/metrics/detailed</c> (the operator's published one
    /// does) produces a second series per node, distinguished only by <c>rabbitmq_endpoint</c>.
    /// Left unpinned that doubles the node count and makes every join ambiguous, which Prometheus
    /// rejects outright with "multiple matches for labels".
    /// </summary>
    public static string RabbitMQIdentity(string rabbitMQName) =>
        $"rabbitmq_identity_info{{rabbitmq_cluster=\"{rabbitMQName}\",rabbitmq_endpoint=\"aggregated\"}}";

    /// <summary>
    /// Scopes a metric to one cluster.
    ///
    /// The plugin labels almost nothing: every aggregated family is emitted with no labels at
    /// all, and <c>rabbitmq_cluster</c> exists only on <c>rabbitmq_identity_info</c> and
    /// <c>rabbitmq_build_info</c>. Writing <c>metric{rabbitmq_cluster="x"}</c> therefore matches
    /// no series and silently yields zero — so the cluster name has to be brought in by joining
    /// against identity info on the scrape target, which is what RabbitMQ's own dashboards do.
    /// </summary>
    public static string RabbitMQScoped(string expr, string rabbitMQName, string? extraJoinLabels = null)
    {
        string labels = string.IsNullOrEmpty(extraJoinLabels)
            ? "rabbitmq_cluster"
            : $"rabbitmq_cluster,{extraJoinLabels}";

        return $"{expr} * on(instance, job) group_left({labels}) {RabbitMQIdentity(rabbitMQName)}";
    }

    private static string Agg(string metric, string cluster) =>
        $"sum({RabbitMQScoped(metric, cluster)})";

    private static string Rate(string metric, string cluster) =>
        $"sum({RabbitMQScoped($"rate({metric}[5m])", cluster)})";

    private static string PerNode(string metric, string cluster) =>
        RabbitMQScoped(metric, cluster, "rabbitmq_node");

    /// <summary>
    /// Adds several counters' rates into one figure.
    ///
    /// Each term is defaulted with <c>or vector(0)</c> because vector arithmetic drops to an
    /// empty result when either side has no series: one counter a broker happens not to export
    /// would otherwise zero the whole sum and, with it, the warning that depends on it.
    /// </summary>
    private static string RateSum(string cluster, params string[] metrics) =>
        string.Join(" + ", metrics.Select(m => $"({Rate(m, cluster)} or vector(0))"));

    private async Task<KeycloakMetricsSummary> QueryKeycloakMetricsAsync(
        HttpClient http, string baseUrl, string instanceName, string ns, CancellationToken ct)
    {
        Dictionary<string, List<PrometheusMetricResult>> r =
            await QueryManyAsync(http, baseUrl, BuildKeycloakQueries(ns), ct);

        return new KeycloakMetricsSummary
        {
            InstanceName           = instanceName,
            Namespace              = ns,
            // Keyed on the HTTP series specifically: those exist for any running Keycloak, while
            // the JVM and pool series would also be absent on a partial scrape.
            HasData                = r["requests"].Count > 0 || r["active"].Count > 0
                                     || r["heapUsed"].Count > 0,
            RequestsPerSec         = Math.Round(Scalar(r["requests"]), 2),
            ServerErrorsPerSec     = Math.Round(Scalar(r["errors5xx"]), 3),
            ClientErrorsPerSec     = Math.Round(Scalar(r["errors4xx"]), 3),
            AvgLatencyMs           = Math.Round(Scalar(r["latency"]) * 1000, 1),
            ActiveRequests         = Scalar(r["active"]),

            HeapUsedBytes          = Scalar(r["heapUsed"]),
            HeapCommittedBytes     = Scalar(r["heapComm"]),
            GcPauseSecondsPerSec   = Math.Round(Scalar(r["gcPause"]), 4),
            GcOverheadPercent      = Math.Round(Scalar(r["gcOverhead"]) * 100, 2),

            DbActiveConnections    = Scalar(r["dbActive"]),
            DbAvailableConnections = Scalar(r["dbAvail"]),
            DbAwaitingThreads      = Scalar(r["dbAwait"]),
            DbBlockingTimeAvgMs    = Math.Round(Scalar(r["dbBlock"]), 2),
            DbMaxUsedConnections   = Scalar(r["dbMaxUsed"]),

            HasUserEventMetrics    = r["anyEvent"].Count > 0,
            LoginsPerSec           = Math.Round(Scalar(r["logins"]), 3),
            LoginErrorsPerSec      = Math.Round(Scalar(r["loginErr"]), 3),
            TokenRefreshPerSec     = Math.Round(Scalar(r["refresh"]), 3),
            RegistrationsPerSec    = Math.Round(Scalar(r["register"]), 3),
            TopErrors =
            [
                .. r["topErrors"]
                    .Select(m => new KeycloakEventBreakdown
                    {
                        Realm      = m.Labels.GetValueOrDefault("realm", ""),
                        Event      = m.Labels.GetValueOrDefault("event", ""),
                        Error      = m.Labels.GetValueOrDefault("error", ""),
                        RatePerSec = Math.Round(m.Value, 4),
                    })
                    .Where(e => e.RatePerSec > 0)
                    .OrderByDescending(e => e.RatePerSec)
            ],
        };
    }

    /// <summary>
    /// The PromQL behind the Keycloak panel, keyed by the field it populates.
    ///
    /// Separated from the query execution so the metric names are assertable — see the note on
    /// <see cref="BuildRabbitMQQueries"/> for why a wrong name fails silently.
    ///
    /// The HTTP, JVM and pool names are Quarkus/Micrometer conventions rather than Keycloak's
    /// own; only <c>keycloak_user_events_total</c> is Keycloak-specific, and it is absent unless
    /// user event metrics were explicitly enabled.
    /// </summary>
    public static Dictionary<string, string> BuildKeycloakQueries(string ns)
    {
        string sel = $"{{namespace=\"{ns}\"}}";
        string evt = $"namespace=\"{ns}\"";

        return new Dictionary<string, string>
        {
            ["requests"]   = $"sum(rate(http_server_requests_seconds_count{sel}[5m]))",
            ["errors5xx"]  = $"sum(rate(http_server_requests_seconds_count{{{evt},status=~\"5..\"}}[5m]))",
            ["errors4xx"]  = $"sum(rate(http_server_requests_seconds_count{{{evt},status=~\"4..\"}}[5m]))",
            // Micrometer timers expose _sum and _count; their ratio is the mean latency over the
            // window. Keycloak publishes no histogram buckets by default, so a true quantile is
            // not available without turning them on.
            ["latency"]    = $"sum(rate(http_server_requests_seconds_sum{sel}[5m]))"
                             + $" / sum(rate(http_server_requests_seconds_count{sel}[5m]))",
            ["active"]     = $"sum(http_server_active_requests{sel})",

            ["heapUsed"]   = $"sum(jvm_memory_used_bytes{{{evt},area=\"heap\"}})",
            ["heapComm"]   = $"sum(jvm_memory_committed_bytes{{{evt},area=\"heap\"}})",
            // Per-JVM quantities are reduced with max, not sum: these are compared against
            // per-instance thresholds, and summing three healthy replicas' GC time would cross
            // a single JVM's threshold while every JVM is fine. Max reports the worst pod.
            ["gcPause"]    = $"max(rate(jvm_gc_pause_seconds_sum{sel}[5m]))",
            ["gcOverhead"] = $"max(jvm_gc_overhead{sel})",

            // Pool figures are per-pod for the same reason, and mixing aggregations across them
            // produces impossible readings — a summed "active" happily exceeds a maxed "peak
            // used". Available takes min, since the pod with the fewest spare connections is the
            // one about to block.
            ["dbActive"]   = $"max(agroal_active_count{sel})",
            ["dbAvail"]    = $"min(agroal_available_count{sel})",
            ["dbAwait"]    = $"max(agroal_awaiting_count{sel})",
            ["dbBlock"]    = $"max(agroal_blocking_time_average_milliseconds{sel})",
            ["dbMaxUsed"]  = $"max(agroal_max_used_count{sel})",

            // A failed login is not its own event type: it is event="login" carrying a non-empty
            // error label. Querying event="login_error" matches nothing, and counting every
            // event="login" as a success silently folds the failures back in.
            ["logins"]     = $"sum(rate(keycloak_user_events_total{{{evt},event=\"login\",error=\"\"}}[5m]))",
            ["loginErr"]   = $"sum(rate(keycloak_user_events_total{{{evt},event=\"login\",error!=\"\"}}[5m]))",
            ["refresh"]    = $"sum(rate(keycloak_user_events_total{{{evt},event=\"refresh_token\"}}[5m]))",
            ["register"]   = $"sum(rate(keycloak_user_events_total{{{evt},event=\"register\"}}[5m]))",
            ["anyEvent"]   = $"sum(keycloak_user_events_total{sel})",
            ["topErrors"]  = $"topk(5, sum by (realm,event,error) "
                             + $"(rate(keycloak_user_events_total{{{evt},error!=\"\"}}[5m])))",
        };
    }

    /// <summary>
    /// Retrieves Keycloak instance metrics from Prometheus. Keycloak serves Quarkus/Micrometer
    /// metrics on its management interface (port 9000), so the series carry no Keycloak-specific
    /// identity label — they are scoped by the namespace the release was installed into.
    /// </summary>
    public async Task<KubernetesOperationResult<KeycloakMetricsSummary>> GetKeycloakMetricsAsync(
        Guid clusterId, string instanceName, string keycloakNamespace, CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<KeycloakMetricsSummary>.Failure(error!);

        return await WithServiceAsync<KeycloakMetricsSummary>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            (http, baseUrl, token) => QueryKeycloakMetricsAsync(http, baseUrl, instanceName, keycloakNamespace, token),
            $"Keycloak metrics for {instanceName}", ct);
    }

    /// <summary>
    /// Works out why a workload that should be exporting metrics produces none, by comparing the
    /// two halves that have to line up: a ServiceMonitor in the workload's namespace, and the
    /// selector the Prometheus resource uses to decide which ServiceMonitors it will scrape.
    ///
    /// Shared by RabbitMQ and Keycloak, which differ only in what the monitor is expected to be
    /// called and what creates it. Both halves are read from the cluster, so this reports what is
    /// actually there rather than what the configuration implies.
    /// </summary>
    /// <param name="workloadNamespace">Namespace the workload runs in.</param>
    /// <param name="monitorNameHint">Substring identifying the workload's own ServiceMonitor.</param>
    /// <param name="absentRemedy">What to tell the operator when no monitor exists at all.</param>
    public async Task<KubernetesOperationResult<ScrapeDiagnosis>> DiagnoseServiceMonitorScrapeAsync(
        Guid clusterId, string workloadNamespace, string monitorNameHint, string absentRemedy,
        CancellationToken ct = default)
    {
        var (info, error) = await ResolvePrometheusInfoAsync(clusterId, ct);
        if (info is null) return KubernetesOperationResult<ScrapeDiagnosis>.Failure(error!);

        try
        {
            Kubernetes k8s = CreateK8sClient(info.Kubeconfig);

            JsonArray monitors = await ListCustomObjectsAsync(
                k8s, "monitoring.coreos.com", "v1", workloadNamespace, "servicemonitors", ct);

            JsonNode? monitor = monitors.FirstOrDefault(m =>
                m?["metadata"]?["name"]?.GetValue<string>() is { } n
                && n.Contains(monitorNameHint, StringComparison.OrdinalIgnoreCase));

            JsonNode? prometheus = await FindPrometheusResourceAsync(k8s, ct);

            Dictionary<string, string> smLabels = ReadLabels(monitor?["metadata"]?["labels"]);
            Dictionary<string, string> selector =
                ReadLabels(prometheus?["spec"]?["serviceMonitorSelector"]?["matchLabels"]);

            // An empty (but present) serviceMonitorSelector means "every ServiceMonitor"; one with
            // matchLabels means "only those carrying these labels", which is what an untouched
            // kube-prometheus-stack produces via its release label.
            bool selectorPresent = prometheus?["spec"]?["serviceMonitorSelector"] is not null;
            bool selectsAll = !selectorPresent || selector.Count == 0;
            bool matches = selectsAll || selector.All(kv =>
                smLabels.TryGetValue(kv.Key, out string? v) && v == kv.Value);

            List<string> findings = [];
            string? remedy = null;

            if (monitor is null)
            {
                findings.Add(
                    $"No ServiceMonitor matching '{monitorNameHint}' exists in namespace "
                    + $"{workloadNamespace}, so Prometheus was never told to scrape this workload.");
                remedy = absentRemedy;
            }
            else if (!matches)
            {
                string want = string.Join(", ", selector.Select(kv => $"{kv.Key}={kv.Value}"));
                string have = smLabels.Count == 0
                    ? "(no labels)"
                    : string.Join(", ", smLabels.Select(kv => $"{kv.Key}={kv.Value}"));

                findings.Add(
                    $"The ServiceMonitor exists but Prometheus does not select it: it scrapes only "
                    + $"ServiceMonitors labelled {want}, and this one has {have}.");
                remedy =
                    "Add these to the kube-prometheus-stack component's values (Cluster → Components → "
                    + "kube-prometheus-stack → Values), then Save & Apply:\n"
                    + "prometheus:\n  prometheusSpec:\n    serviceMonitorSelectorNilUsesHelmValues: false\n"
                    + "    podMonitorSelectorNilUsesHelmValues: false";
            }
            else
            {
                findings.Add("The ServiceMonitor exists and Prometheus's selector accepts it.");
                remedy =
                    "Scraping should be running. If metrics are still missing, check Prometheus → Targets — "
                    + "a target listed as down points at the workload's metrics port, not the wiring.";
            }

            // A namespace selector that is absent (rather than empty) confines Prometheus to its
            // own namespace, which the workload's namespace would never satisfy.
            bool nsSelectorPresent = prometheus?["spec"]?["serviceMonitorNamespaceSelector"] is not null;
            if (monitor is not null && !nsSelectorPresent)
            {
                findings.Add(
                    "Prometheus has no serviceMonitorNamespaceSelector, so it only looks in its own "
                    + $"namespace — it will not see anything in {workloadNamespace}.");
            }

            return KubernetesOperationResult<ScrapeDiagnosis>.Success(new ScrapeDiagnosis
            {
                MonitorExists          = monitor is not null,
                MonitorLabels          = smLabels,
                PrometheusSelector     = selector,
                SelectorAcceptsMonitor = monitor is not null && matches,
                Findings               = findings,
                Remedy                 = remedy,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scrape diagnosis failed for {Hint} in {Namespace}",
                monitorNameHint, workloadNamespace);
            return KubernetesOperationResult<ScrapeDiagnosis>.Failure(DescribeK8sError(ex));
        }
    }

    /// <summary>
    /// Runs several instant queries against one Prometheus concurrently.
    ///
    /// Prometheus evaluates each of these in single-digit milliseconds, so the round-trip through
    /// the API-server pod proxy — not the evaluation — dominates; issuing them together makes a
    /// wide panel cost roughly its slowest query instead of the sum of all of them.
    ///
    /// A query that fails contributes an empty vector rather than propagating, so one metric the
    /// running broker or server happens not to export cannot blank out the whole panel.
    /// </summary>
    private async Task<Dictionary<string, List<PrometheusMetricResult>>> QueryManyAsync(
        HttpClient http, string baseUrl, Dictionary<string, string> queries, CancellationToken ct)
    {
        KeyValuePair<string, string>[] pairs = [.. queries];

        List<PrometheusMetricResult>[] results = await Task.WhenAll(
            pairs.Select(kv => InstantAsync(http, baseUrl, kv.Value, ct)));

        Dictionary<string, List<PrometheusMetricResult>> byKey = [];
        for (int i = 0; i < pairs.Length; i++)
            byKey[pairs[i].Key] = results[i];

        return byKey;
    }

    private async Task<List<PrometheusMetricResult>> InstantAsync(
        HttpClient http, string baseUrl, string query, CancellationToken ct)
    {
        try
        {
            string json = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q(query)}", ct);
            return ParseInstantQueryResult(json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Instant query failed, treating as no data: {Query}", query);
            return [];
        }
    }

    /// <summary>First value of a vector, or 0 when the query matched nothing.</summary>
    private static double Scalar(List<PrometheusMetricResult> results)
        => results.Count > 0 ? results[0].Value : 0;

    // ──────── Static Parsing Methods ────────

    /// <summary>
    /// Parses a Prometheus instant query response (vector or scalar) into metric results.
    /// </summary>
    public static List<PrometheusMetricResult> ParseInstantQueryResult(string json)
    {
        List<PrometheusMetricResult> results = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success")
            {
                return results;
            }

            JsonElement data = root.GetProperty("data");
            string resultType = data.GetProperty("resultType").GetString() ?? "";

            if (resultType == "vector")
            {
                foreach (JsonElement item in data.GetProperty("result").EnumerateArray())
                {
                    PrometheusMetricResult metric = new();

                    if (item.TryGetProperty("metric", out JsonElement metricLabels))
                    {
                        foreach (JsonProperty prop in metricLabels.EnumerateObject())
                        {
                            metric.Labels[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }

                    JsonElement value = item.GetProperty("value");
                    metric.Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)value[0].GetDouble()).UtcDateTime;
                    metric.Value = double.Parse(value[1].GetString() ?? "0", CultureInfo.InvariantCulture);

                    results.Add(metric);
                }
            }
            else if (resultType == "scalar")
            {
                JsonElement result = data.GetProperty("result");
                PrometheusMetricResult metric = new()
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)result[0].GetDouble()).UtcDateTime,
                    Value = double.Parse(result[1].GetString() ?? "0", CultureInfo.InvariantCulture)
                };
                results.Add(metric);
            }
        }
        catch
        {
            // Graceful degradation — return empty on parse failure.
        }

        return results;
    }

    /// <summary>
    /// Parses a Prometheus range query response (matrix) into time-series data.
    /// </summary>
    public static List<PrometheusTimeSeries> ParseRangeQueryResult(string json)
    {
        List<PrometheusTimeSeries> results = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success")
            {
                return results;
            }

            JsonElement data = root.GetProperty("data");

            if (data.GetProperty("resultType").GetString() != "matrix")
            {
                return results;
            }

            foreach (JsonElement item in data.GetProperty("result").EnumerateArray())
            {
                PrometheusTimeSeries series = new();

                if (item.TryGetProperty("metric", out JsonElement metricLabels))
                {
                    foreach (JsonProperty prop in metricLabels.EnumerateObject())
                    {
                        series.Labels[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                if (item.TryGetProperty("values", out JsonElement values))
                {
                    foreach (JsonElement point in values.EnumerateArray())
                    {
                        TimeSeriesDataPoint dp = new()
                        {
                            Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)point[0].GetDouble()).UtcDateTime,
                            Value = double.Parse(point[1].GetString() ?? "0", CultureInfo.InvariantCulture)
                        };
                        series.DataPoints.Add(dp);
                    }
                }

                results.Add(series);
            }
        }
        catch
        {
            // Graceful degradation.
        }

        return results;
    }

    /// <summary>
    /// Parses an Alertmanager /api/v2/alerts JSON response into AlertInfo objects.
    /// </summary>
    public static List<AlertInfo> ParseAlertmanagerAlerts(string json)
    {
        List<AlertInfo> results = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (JsonElement alert in doc.RootElement.EnumerateArray())
            {
                AlertInfo info = new();

                if (alert.TryGetProperty("labels", out JsonElement labels))
                {
                    foreach (JsonProperty prop in labels.EnumerateObject())
                    {
                        info.Labels[prop.Name] = prop.Value.GetString() ?? "";
                    }

                    info.Name = info.Labels.GetValueOrDefault("alertname") ?? "";
                    info.Severity = info.Labels.GetValueOrDefault("severity") ?? "";
                }

                if (alert.TryGetProperty("annotations", out JsonElement annotations))
                {
                    info.Summary = annotations.TryGetProperty("summary", out JsonElement s)
                        ? s.GetString() ?? "" : "";
                    info.Description = annotations.TryGetProperty("description", out JsonElement d)
                        ? d.GetString() ?? "" : "";
                    info.RunbookUrl = annotations.TryGetProperty("runbook_url", out JsonElement rbu)
                        ? rbu.GetString() ?? "" : "";
                }

                if (alert.TryGetProperty("startsAt", out JsonElement startsAt)
                    && DateTimeOffset.TryParse(startsAt.GetString(), out DateTimeOffset parsedStart))
                {
                    info.StartsAt = parsedStart.UtcDateTime;
                }

                if (alert.TryGetProperty("endsAt", out JsonElement endsAt)
                    && DateTimeOffset.TryParse(endsAt.GetString(), out DateTimeOffset parsedEnd))
                {
                    info.EndsAt = parsedEnd.UtcDateTime;
                }

                if (alert.TryGetProperty("status", out JsonElement status)
                    && status.TryGetProperty("state", out JsonElement state))
                {
                    info.State = state.GetString() ?? "";
                }

                if (alert.TryGetProperty("fingerprint", out JsonElement fp))
                {
                    info.Fingerprint = fp.GetString() ?? "";
                }

                results.Add(info);
            }
        }
        catch
        {
            // Graceful degradation.
        }

        return results;
    }

    /// <summary>
    /// Parses an Alertmanager /api/v2/silences JSON response into SilenceInfo objects.
    /// </summary>
    public static List<SilenceInfo> ParseAlertmanagerSilences(string json)
    {
        List<SilenceInfo> results = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (JsonElement silence in doc.RootElement.EnumerateArray())
            {
                SilenceInfo info = new();

                if (silence.TryGetProperty("id", out JsonElement id))
                {
                    info.Id = id.GetString() ?? "";
                }

                if (silence.TryGetProperty("status", out JsonElement status)
                    && status.TryGetProperty("state", out JsonElement state))
                {
                    info.State = state.GetString() ?? "";
                }

                if (silence.TryGetProperty("comment", out JsonElement comment))
                {
                    info.Comment = comment.GetString() ?? "";
                }

                if (silence.TryGetProperty("createdBy", out JsonElement createdBy))
                {
                    info.CreatedBy = createdBy.GetString() ?? "";
                }

                if (silence.TryGetProperty("startsAt", out JsonElement startsAt)
                    && DateTimeOffset.TryParse(startsAt.GetString(), out DateTimeOffset parsedStart))
                {
                    info.StartsAt = parsedStart.UtcDateTime;
                }

                if (silence.TryGetProperty("endsAt", out JsonElement endsAt)
                    && DateTimeOffset.TryParse(endsAt.GetString(), out DateTimeOffset parsedEnd))
                {
                    info.EndsAt = parsedEnd.UtcDateTime;
                }

                if (silence.TryGetProperty("matchers", out JsonElement matchers)
                    && matchers.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement m in matchers.EnumerateArray())
                    {
                        SilenceMatcher matcher = new()
                        {
                            Name = m.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                            Value = m.TryGetProperty("value", out JsonElement v) ? v.GetString() ?? "" : "",
                            IsRegex = m.TryGetProperty("isRegex", out JsonElement r) && r.GetBoolean(),
                            IsEqual = !m.TryGetProperty("isEqual", out JsonElement e) || e.GetBoolean()
                        };
                        info.Matchers.Add(matcher);
                    }
                }

                results.Add(info);
            }
        }
        catch
        {
            // Graceful degradation.
        }

        return results;
    }

    /// <summary>
    /// Extracts Prometheus configuration for a component, deriving sensible defaults
    /// from the component's own metadata (ReleaseName, Namespace) when no explicit
    /// JSON configuration is stored.
    ///
    /// For kube-prometheus-stack, the Helm release name determines the service names:
    ///   Prometheus:   {releaseName}-kube-prometheus-prometheus
    ///   Alertmanager: {releaseName}-kube-prometheus-alertmanager
    ///
    /// An explicit Configuration JSON always wins over the derived defaults.
    /// </summary>
    public static PrometheusConfig GetPrometheusConfig(ClusterComponent component)
    {
        // Derive defaults from component metadata so they work regardless of release name.
        string releaseName = component.ReleaseName ?? component.Name;
        string ns          = component.Namespace ?? "monitoring";

        PrometheusConfig derived = new()
        {
            Namespace               = ns,
            ServiceName             = $"{releaseName}-prometheus",
            AlertmanagerServiceName = $"{releaseName}-alertmanager",
        };

        if (string.IsNullOrWhiteSpace(component.Configuration))
            return derived;

        try
        {
            PrometheusConfig? explicit_ = JsonSerializer.Deserialize<PrometheusConfig>(
                component.Configuration,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (explicit_ is null) return derived;

            // Merge: only override a derived field when the JSON has a non-default value.
            if (!string.IsNullOrWhiteSpace(explicit_.Namespace))
                derived.Namespace = explicit_.Namespace;
            if (!string.IsNullOrWhiteSpace(explicit_.ServiceName))
                derived.ServiceName = explicit_.ServiceName;
            if (explicit_.ServicePort != 9090)
                derived.ServicePort = explicit_.ServicePort;
            if (!string.IsNullOrWhiteSpace(explicit_.AlertmanagerServiceName))
                derived.AlertmanagerServiceName = explicit_.AlertmanagerServiceName;
            if (explicit_.AlertmanagerServicePort != 9093)
                derived.AlertmanagerServicePort = explicit_.AlertmanagerServicePort;

            return derived;
        }
        catch
        {
            return derived;
        }
    }

    /// <summary>
    /// Queries each cluster/namespace-regex pair, sums CPU, memory, pods across all clusters,
    /// and returns a single aggregated DeploymentMetricsSummary.
    /// </summary>
    private async Task<KubernetesOperationResult<DeploymentMetricsSummary>> AggregateMetricsAsync(
        IEnumerable<(string Kubeconfig, PrometheusConfig Config, string NsRegex)> clusterQueries,
        string logContext,
        CancellationToken ct)
    {
        double totalCpu = 0, totalMemBytes = 0, totalPods = 0, totalRestarts = 0;
        bool anySuccess = false;

        foreach ((string kubeconfig, PrometheusConfig cfg, string nsRegex) in clusterQueries)
        {
            KubernetesOperationResult<(double, double, double, double)> result =
                await WithServiceAsync<(double, double, double, double)>(
                    kubeconfig, cfg.Namespace, cfg.ServiceName, cfg.ServicePort,
                    async (http, baseUrl, token) =>
                    {
                        string cpuQ = Q($"sum(rate(container_cpu_usage_seconds_total{{namespace=~\"{nsRegex}\",container!=\"\"}}[5m]))");
                        string memQ = Q($"sum(container_memory_working_set_bytes{{namespace=~\"{nsRegex}\",container!=\"\"}})");
                        string podQ = Q($"count(count by (pod, namespace) (container_cpu_usage_seconds_total{{namespace=~\"{nsRegex}\",container!=\"\"}}))");
                        string rstQ = Q($"sum(kube_pod_container_status_restarts_total{{namespace=~\"{nsRegex}\"}})");

                        double cpu  = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={cpuQ}", token));
                        double mem  = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={memQ}", token));
                        double pods = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={podQ}", token));
                        double rst  = ExtractScalarValue(await http.GetStringAsync($"{baseUrl}/api/v1/query?query={rstQ}", token));

                        return (cpu, mem, pods, rst);
                    },
                    logContext, ct);

            if (result.IsSuccess)
            {
                anySuccess      =  true;
                totalCpu      += result.Data.Item1;
                totalMemBytes += result.Data.Item2;
                totalPods     += result.Data.Item3;
                totalRestarts += result.Data.Item4;
            }
        }

        if (!anySuccess)
            return KubernetesOperationResult<DeploymentMetricsSummary>.Failure(
                "Could not reach Prometheus on any cluster for this resource.");

        return KubernetesOperationResult<DeploymentMetricsSummary>.Success(new DeploymentMetricsSummary
        {
            Namespace    = "aggregated",
            CpuCores     = Math.Round(totalCpu, 4),
            MemoryMiB    = Math.Round(totalMemBytes / (1024 * 1024), 1),
            PodCount     = (int)totalPods,
            RestartCount = (int)totalRestarts,
            QueriedAt    = DateTime.UtcNow
        });
    }

    // ──────── Internal Helpers ────────

    private sealed record ResolvedPrometheusInfo(string Kubeconfig, PrometheusConfig Config);

    private async Task<(ResolvedPrometheusInfo? Info, string? Error)> ResolvePrometheusInfoAsync(
        Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters
            .Include(c => c.Components)
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null)
            return (null, "Cluster not found.");

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (null, "No kubeconfig configured for this cluster.");

        ClusterComponent? prometheusComponent = cluster.Components.FirstOrDefault(c =>
            c.Name.Contains("prometheus", StringComparison.OrdinalIgnoreCase));

        if (prometheusComponent is null)
            return (null, "No prometheus component found on this cluster.");

        PrometheusConfig config = GetPrometheusConfig(prometheusComponent) ?? new PrometheusConfig();
        return (new ResolvedPrometheusInfo(cluster.Kubeconfig, config), null);
    }

    /// <summary>
    /// Routes HTTP requests to an in-cluster service via the Kubernetes API server's
    /// pod proxy endpoint: /api/v1/namespaces/{ns}/pods/{pod}:{port}/proxy/{path}.
    ///
    /// This is equivalent to what "kubectl proxy" does — no WebSocket, no local TCP
    /// listener, no subprocess. The k8s client's HttpClient already carries authentication
    /// (Bearer token / client cert), so the same credentials that work for API calls
    /// also work for the pod proxy.
    ///
    /// Requires get permission on pods/proxy in the pod's namespace, which is present
    /// in any standard cluster-admin or operator kubeconfig.
    /// </summary>
    private async Task<KubernetesOperationResult<T>> WithServiceAsync<T>(
        string kubeconfig,
        string ns,
        string svcName,
        int svcPort,
        Func<HttpClient, string, CancellationToken, Task<T>> action,
        string logContext,
        CancellationToken ct)
    {
        try
        {
            Kubernetes k8s = CreateK8sClient(kubeconfig);

            V1EndpointAddress? addr = await FindEndpointAddressAsync(k8s, ns, svcName, svcPort, ct);
            if (addr?.TargetRef is null)
                throw new InvalidOperationException(
                    $"No ready pods found for service {svcName}:{svcPort} in {ns}. " +
                    "Check the component's ReleaseName and Namespace, or set an explicit Configuration JSON.");

            string podName = addr.TargetRef.Name;
            string podNs   = addr.TargetRef.NamespaceProperty ?? ns;

            // Build the pod-proxy base URL. All requests go:
            //   app → k8s API server → pod:{svcPort}
            // Authentication is already wired into k8s.HttpClient.
            string baseUrl = k8s.BaseUri.ToString().TrimEnd('/')
                + $"/api/v1/namespaces/{podNs}/pods/{podName}:{svcPort}/proxy";

            logger.LogDebug("Prometheus proxy → pod {Pod} ({PodNs}) at {BaseUrl}", podName, podNs, baseUrl);

            await VerifyPrometheusConnectionAsync(k8s.HttpClient, baseUrl, ct);

            T result = await action(k8s.HttpClient, baseUrl, ct);
            return KubernetesOperationResult<T>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prometheus query failed ({Context})", logContext);
            return KubernetesOperationResult<T>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Probes /-/healthy. Not all services expose this (some Alertmanager versions don't),
    /// so a 404 is treated as "endpoint absent, continue anyway" rather than a hard failure.
    /// Any other non-2xx (401, 403, 503 …) is a real error and is re-thrown.
    /// </summary>
    private async Task VerifyPrometheusConnectionAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            string body = await http.GetStringAsync($"{baseUrl}/-/healthy", ct);
            logger.LogDebug("Health check response from {BaseUrl}: {Body}", baseUrl, body);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogDebug("/-/healthy not found on {BaseUrl} — skipping health probe", baseUrl);
        }
    }

    /// <summary>
    /// Finds a ready pod backing the given service. First tries the configured service
    /// name directly. If the endpoint resource is not found (wrong release name, different
    /// namespace), falls back to listing all endpoints in the namespace and finding one
    /// whose name contains "prometheus" (or "alertmanager") and exposes the expected port.
    /// Logs what it found so operators can tune the component configuration.
    /// </summary>
    private async Task<V1EndpointAddress?> FindEndpointAddressAsync(
        Kubernetes k8s, string ns, string svcName, int svcPort, CancellationToken ct)
    {
        // 1. Try the configured/derived name first.
        try
        {
            V1Endpoints ep = await k8s.CoreV1.ReadNamespacedEndpointsAsync(svcName, ns, cancellationToken: ct);
            V1EndpointAddress? direct = ReadyPodAddress(ep, svcPort);
            if (direct is not null) return direct;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }

        // 2. Fall back: scan all endpoints in the namespace for a Prometheus-looking one.
        logger.LogWarning(
            "Endpoint {SvcName} not found in {Ns} — scanning namespace for a matching service on port {Port}",
            svcName, ns, svcPort);

        V1EndpointsList all = await k8s.CoreV1.ListNamespacedEndpointsAsync(ns, cancellationToken: ct);

        // Match by port first (most specific), then by name containing the right keyword.
        string keyword = svcName.Contains("alertmanager", StringComparison.OrdinalIgnoreCase)
            ? "alertmanager" : "prometheus";

        foreach (V1Endpoints candidate in all.Items.OrderBy(e => e.Metadata.Name))
        {
            if (!candidate.Metadata.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;

            V1EndpointAddress? addr = ReadyPodAddress(candidate, svcPort);
            if (addr is null) continue;

            logger.LogInformation(
                "Auto-discovered {Keyword} endpoint {Found} (configured name was {Configured}). " +
                "Set the component's Configuration JSON to fix this permanently.",
                keyword, candidate.Metadata.Name, svcName);

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

    /// <summary>
    /// Parses a Prometheus /api/v1/targets JSON response into ScrapeTarget objects.
    /// </summary>
    public static List<ScrapeTarget> ParseScrapeTargets(string json)
    {
        List<ScrapeTarget> results = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success") return results;

            JsonElement activeTargets = root.GetProperty("data").GetProperty("activeTargets");
            foreach (JsonElement t in activeTargets.EnumerateArray())
            {
                ScrapeTarget target = new()
                {
                    Pool      = t.TryGetProperty("scrapePool",   out JsonElement pool) ? pool.GetString()   ?? "" : "",
                    ScrapeUrl = t.TryGetProperty("scrapeUrl",    out JsonElement url)  ? url.GetString()    ?? "" : "",
                    Health    = t.TryGetProperty("health",       out JsonElement h)    ? h.GetString()      ?? "" : "",
                    LastError = t.TryGetProperty("lastError",    out JsonElement le)   ? le.GetString()     ?? "" : "",
                    LastScrapeDurationSeconds = t.TryGetProperty("lastScrapeDuration", out JsonElement ld) ? ld.GetDouble() : 0
                };

                if (t.TryGetProperty("lastScrape", out JsonElement ls) && ls.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ls.GetString(), out DateTimeOffset scraped))
                    target.LastScrape = scraped.UtcDateTime;

                if (t.TryGetProperty("labels", out JsonElement labels))
                    foreach (JsonProperty p in labels.EnumerateObject())
                        target.Labels[p.Name] = p.Value.GetString() ?? "";

                results.Add(target);
            }
        }
        catch { }
        return results;
    }

    /// <summary>
    /// Parses a Prometheus /api/v1/rules?type=alert JSON response into AlertRule objects.
    /// </summary>
    public static List<AlertRule> ParseAlertRules(string json)
    {
        List<AlertRule> results = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success") return results;

            JsonElement groups = root.GetProperty("data").GetProperty("groups");
            foreach (JsonElement group in groups.EnumerateArray())
            {
                string groupName = group.TryGetProperty("name", out JsonElement gn) ? gn.GetString() ?? "" : "";

                if (!group.TryGetProperty("rules", out JsonElement rules)) continue;

                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (!rule.TryGetProperty("type", out JsonElement typeEl) || typeEl.GetString() != "alerting") continue;

                    AlertRule r = new()
                    {
                        GroupName = groupName,
                        Name      = rule.TryGetProperty("name",           out JsonElement n)  ? n.GetString()  ?? "" : "",
                        Query     = rule.TryGetProperty("query",          out JsonElement q)  ? q.GetString()  ?? "" : "",
                        State     = rule.TryGetProperty("state",          out JsonElement st) ? st.GetString() ?? "" : "",
                        EvaluationTimeSeconds = rule.TryGetProperty("evaluationTime", out JsonElement et) ? et.GetDouble() : 0
                    };

                    if (rule.TryGetProperty("duration", out JsonElement dur))
                        r.DurationSeconds = dur.GetDouble();

                    if (rule.TryGetProperty("labels", out JsonElement labels))
                        foreach (JsonProperty p in labels.EnumerateObject())
                        {
                            r.Labels[p.Name] = p.Value.GetString() ?? "";
                            if (p.Name == "severity") r.Severity = p.Value.GetString() ?? "";
                        }

                    if (rule.TryGetProperty("annotations", out JsonElement annotations))
                        foreach (JsonProperty p in annotations.EnumerateObject())
                        {
                            r.Annotations[p.Name] = p.Value.GetString() ?? "";
                            if (p.Name == "summary")    r.Summary    = p.Value.GetString() ?? "";
                            if (p.Name == "runbook_url") r.RunbookUrl = p.Value.GetString() ?? "";
                        }

                    results.Add(r);
                }
            }
        }
        catch { }
        return results;
    }

    /// <summary>
    /// Returns the shared client for this cluster. Deliberately NOT disposed by callers: it is pooled per
    /// kubeconfig so a dashboard's dozen PromQL queries reuse one warm TLS connection to the API server
    /// instead of handshaking once per panel. See <see cref="KubernetesProxyClientPool"/>.
    /// </summary>
    private Kubernetes CreateK8sClient(string kubeconfig) => clientPool.Get(kubeconfig);

    private static string Q(string promQuery) => Uri.EscapeDataString(promQuery);

    private static double ExtractScalarValue(string json)
    {
        List<PrometheusMetricResult> results = ParseInstantQueryResult(json);
        return results.Count > 0 ? results[0].Value : 0;
    }
}

/// <summary>
/// Why a CNPG database's metrics are (or are not) reaching Prometheus. Read from the cluster:
/// the PodMonitor CNPG created for the database, and the selector Prometheus filters with.
/// </summary>
public class CnpgScrapeDiagnosis
{
    public bool PodMonitorExists { get; init; }

    /// <summary>Labels on the PodMonitor — what Prometheus's selector is matched against.</summary>
    public Dictionary<string, string> PodMonitorLabels { get; init; } = [];

    /// <summary>The Prometheus resource's podMonitorSelector.matchLabels. Empty means "select all".</summary>
    public Dictionary<string, string> PrometheusSelector { get; init; } = [];

    public bool SelectorAcceptsPodMonitor { get; init; }

    /// <summary>What was found, in plain words, worst first.</summary>
    public List<string> Findings { get; init; } = [];

    /// <summary>The concrete next step, including any YAML to paste.</summary>
    public string? Remedy { get; init; }
}
