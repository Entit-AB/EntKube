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
/// One instant query's answer with its failure kept: <paramref name="Error"/> is null when
/// Prometheus answered, and then an empty <paramref name="Results"/> really is "no series".
/// </summary>
public sealed record InstantQueryOutcome(List<PrometheusMetricResult> Results, string? Error)
{
    public bool Succeeded => Error is null;
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
/// <summary>
/// An aggregate across clusters that keeps what each cluster said: the totals of those that
/// answered, the scopes to chart them with, and why every other cluster contributed nothing.
/// </summary>
public sealed class AppMetricsOverview
{
    /// <summary>The sum over the clusters that answered; null when none did, never a zero for "nothing answered".</summary>
    public DeploymentMetricsSummary? Totals { get; init; }

    /// <summary>The clusters that answered, with their namespaces — what the charts query.</summary>
    public List<AppMetricsScope> Scopes { get; init; } = [];

    /// <summary>
    /// Clusters EntKube reached that have no Prometheus component and where nothing answered on
    /// the default service either: an empty state, not an error. A component-less cluster whose
    /// default service does answer is not here — it contributes like any other.
    /// </summary>
    public List<string> ClustersWithoutPrometheus { get; init; } = [];

    /// <summary>Every other cluster that did not answer, as "cluster: why".</summary>
    public List<string> Problems { get; init; } = [];

    /// <summary>The sentence shown for a cluster that has no Prometheus.</summary>
    public static string NotInstalled(string clusterName) => $"Prometheus is not installed on {clusterName}.";

    /// <summary>Every reason, one sentence each: what a failure carries.</summary>
    public string Describe() =>
        string.Join(" ", ClustersWithoutPrometheus.Select(NotInstalled).Concat(Problems));
}

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
    /// False when Prometheus holds no <c>rabbitmq_identity_info</c> for this broker: it is not
    /// being scraped, which the all-zeros card would otherwise render identically to a perfectly
    /// healthy idle one. A scraped broker always exports its identity, idle or not.
    ///
    /// <para>Not "any query answered". The delivery and unroutable rates end in
    /// <c>or vector(0)</c> and so answer for a broker Prometheus has never heard of: an unscraped
    /// broker read as scraped, all zeros and "Healthy", and the way to fix it stayed hidden.</para>
    /// </summary>
    public bool HasData { get; set; }

    /// <summary>
    /// Why some of the figures are missing, or null when every query was answered. A query
    /// Prometheus refused reads as zero below, so a broker with one of these is not "Healthy".
    /// </summary>
    public string? UnansweredQueries { get; set; }

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
/// Which broker's series to read: its name is unique only within its namespace.
/// </summary>
/// <param name="Name">The broker's name, matched against what RabbitMQ reports as <c>rabbitmq_cluster</c>.</param>
/// <param name="Namespace">The namespace it runs in.</param>
/// <param name="IsOperatorManaged">
/// False for a broker adopted from a plain StatefulSet, which may be scraped by a job that adds no
/// namespace label. See <see cref="PrometheusService.RabbitMQIdentity"/>.
/// </param>
public sealed record RabbitMQBrokerIdentity(string Name, string Namespace, bool IsOperatorManaged);

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

    /// <summary>
    /// TCP sockets in use and allowed. Null on RabbitMQ 4, which stopped exporting both gauges
    /// (<c>rabbitmq_process_open_tcp_sockets</c>, <c>rabbitmq_process_max_tcp_sockets</c>). Read
    /// as 0 they drew a meter at "0 / 0" that no amount of load would ever move.
    /// </summary>
    public double? OpenSockets { get; set; }

    /// <inheritdoc cref="OpenSockets"/>
    public double? MaxSockets { get; set; }

    public double MemoryUsedPercent => MemoryLimitBytes > 0 ? MemoryUsedBytes / MemoryLimitBytes * 100 : 0;
    public double FdUsedPercent     => MaxFds > 0 ? OpenFds / MaxFds * 100 : 0;
    public double SocketUsedPercent => ReportsSockets && MaxSockets > 0 ? OpenSockets!.Value / MaxSockets!.Value * 100 : 0;

    /// <summary>Whether this node exports its socket gauges at all. A cluster halfway from 3.13 to 4 has nodes of both kinds.</summary>
    public bool ReportsSockets => OpenSockets is not null && MaxSockets is not null;

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
    EntKube.Contracts.Delivery.IDeliveryApi delivery,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    EntKube.Web.Services.Telemetry.PromQueryCache queryCache,
    ILogger<PrometheusService> logger)
{
    // ──────── Public API ────────

    /// <summary>
    /// How long one cluster's health summary may take before it is abandoned.
    ///
    /// Without a bound this inherits <see cref="HttpClient"/>'s 100-second default, and a
    /// single unreachable cluster holds the monitoring page for a minute and a half — the
    /// page is a set of tiles, and a tile that says "unreachable" after ten seconds is
    /// worth far more than the true number after ninety.
    /// </summary>
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Above this, a cluster's health query is worth naming in the log.</summary>
    private static readonly TimeSpan SlowHealthThreshold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Retrieves a health summary for the cluster by querying key Prometheus
    /// metrics (CPU, memory, nodes, pods, disk). Returns a failure result if
    /// the cluster isn't found, has no Prometheus component, or lacks kubeconfig.
    ///
    /// <para>The seven metrics are fetched <b>concurrently</b>. They were sequential, which
    /// made the summary cost seven WAN round-trips through the API server's pod proxy
    /// rather than one — the queries themselves are trivial for Prometheus, so almost the
    /// whole of that time was latency being paid over and over.</para>
    ///
    /// <para>The result goes through the same short-lived single-flight cache as range
    /// queries. That matters most for the first paint: a prerendered page runs its
    /// initialisation twice, and without the cache the second pass repeats every query
    /// while the user is still waiting for the first.</para>
    /// </summary>
    public async Task<KubernetesOperationResult<ClusterHealthSummary>> GetClusterHealthAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        return await queryCache.GetOrFetchAsync(
            EntKube.Web.Services.Telemetry.PromQueryCache.Key(clusterId, "health", "cluster-health", 0),
            async () =>
            {
                var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
                if (info is null) return KubernetesOperationResult<ClusterHealthSummary>.Failure(error!);

                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HealthTimeout);

                long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

                KubernetesOperationResult<ClusterHealthSummary> result =
                    await WithServiceAsync<ClusterHealthSummary>(
                        info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
                        (http, baseUrl, token) => QueryClusterHealthAsync(http, baseUrl, clusterId, token),
                        $"cluster health for {clusterId}", timeout.Token, probeHealthEndpoint: false);

                TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

                // Logged only when it is slow enough to be felt. The monitoring page is
                // one tile per cluster, so when it drags, the question is always *which*
                // cluster — and that is not answerable from a page that has simply not
                // finished loading.
                if (elapsed > SlowHealthThreshold)
                {
                    logger.LogInformation(
                        "Cluster health for {ClusterId} took {Elapsed:N1}s ({Outcome})",
                        clusterId, elapsed.TotalSeconds, result.IsSuccess ? "ok" : result.Error);
                }

                return result;
            });
    }

    /// <summary>
    /// The health summary's queries, issued in two rounds: one to find out which metric
    /// sources this cluster actually scrapes, then one for every figure at once.
    /// </summary>
    private async Task<ClusterHealthSummary> QueryClusterHealthAsync(
        HttpClient http, string baseUrl, Guid clusterId, CancellationToken ct)
    {
        // An instant query has no step, so the token resolves to PromRateWindow.FloorSeconds —
        // 300s, exactly the 5m these queries asked for before. The substitution happens here only
        // so that one constant can serve both the instant and the range caller.
        Task<double> Scalar(string promQl) => ScalarAsync(
            http, $"{baseUrl}/api/v1/query?query={Q(PromRateWindow.Resolve(promQl, 0))}", ct);

        // Which sources exist is a property of how the cluster is scraped, so it is asked
        // once and remembered — in steady state this leaves a single round of queries.
        (bool hasKsm, bool hasNex) = await DetectMetricSourcesAsync(http, baseUrl, clusterId, ct);

        double cpu, mem, nodes, rNode, pods, rPods, disk;

        if (hasNex && hasKsm)
        {
            // Preferred: kube-prometheus-stack full stack (node-exporter + kube-state-metrics).
            Task<double> cpuT   = Scalar(ClusterHealthQueries.NodeCpuPercent);
            Task<double> memT   = Scalar(ClusterHealthQueries.NodeMemoryPercent);
            Task<double> nodesT = Scalar(ClusterHealthQueries.TotalNodes);
            Task<double> rNodeT = Scalar(ClusterHealthQueries.ReadyNodes);
            Task<double> podsT  = Scalar(ClusterHealthQueries.TotalPods);
            Task<double> rPodsT = Scalar(ClusterHealthQueries.RunningPods);
            Task<double> diskT  = Scalar(ClusterHealthQueries.NodeDiskPercent);

            await Task.WhenAll(cpuT, memT, nodesT, rNodeT, podsT, rPodsT, diskT);

            (cpu, mem, nodes, rNode, pods, rPods, disk) =
                (cpuT.Result, memT.Result, nodesT.Result, rNodeT.Result, podsT.Result, rPodsT.Result, diskT.Result);
        }
        else
        {
            // Fallback: kubelet/cAdvisor metrics only (no kube-state-metrics or node-exporter).
            // These are available from the kubelet job in any kube-prometheus-stack installation.
            logger.LogInformation(
                "kube-state-metrics/node-exporter not scraped — using kubelet/cAdvisor metrics for cluster health");

            Task<double> cpuT   = Scalar(ClusterHealthQueries.KubeletCpuPercent);
            Task<double> memT   = Scalar(ClusterHealthQueries.KubeletMemoryPercent);
            Task<double> nodesT = Scalar(ClusterHealthQueries.KubeletNodes);
            Task<double> podsT  = Scalar(ClusterHealthQueries.KubeletRunningPods);

            await Task.WhenAll(cpuT, memT, nodesT, podsT);

            cpu   = cpuT.Result;
            mem   = memT.Result;
            nodes = nodesT.Result;
            rNode = nodes; // kubelet_running_pods only reports healthy nodes
            pods  = podsT.Result;
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
    }

    /// <summary>
    /// Whether this cluster scrapes kube-state-metrics and node-exporter.
    ///
    /// Remembered for an hour: installing or removing a metrics stack is a deliberate act,
    /// not something that changes between page loads, and paying two round-trips to
    /// re-establish it on every render is most of what made the health summary slow.
    /// </summary>
    private async Task<(bool HasKsm, bool HasNex)> DetectMetricSourcesAsync(
        HttpClient http, string baseUrl, Guid clusterId, CancellationToken ct)
    {
        if (metricSources.TryGetValue(clusterId, out (DateTime At, bool Ksm, bool Nex) cached)
            && DateTime.UtcNow - cached.At < MetricSourceTtl)
        {
            return (cached.Ksm, cached.Nex);
        }

        Task<double> ksm = ScalarAsync(http, $"{baseUrl}/api/v1/query?query=count%28kube_pod_info%29", ct);
        Task<double> nex = ScalarAsync(http, $"{baseUrl}/api/v1/query?query=count%28node_cpu_seconds_total%29", ct);

        await Task.WhenAll(ksm, nex);

        (bool hasKsm, bool hasNex) = (ksm.Result > 0, nex.Result > 0);
        metricSources[clusterId] = (DateTime.UtcNow, hasKsm, hasNex);
        return (hasKsm, hasNex);
    }

    private static async Task<double> ScalarAsync(HttpClient http, string url, CancellationToken ct) =>
        ExtractScalarValue(await http.GetStringAsync(url, ct));

    /// <summary>Per-cluster memo of which metric sources are scraped. See <see cref="DetectMetricSourcesAsync"/>.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTime At, bool Ksm, bool Nex)>
        metricSources = new();

    private static readonly TimeSpan MetricSourceTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Queries a Prometheus range query over the given duration and returns time-series data.
    /// </summary>
    /// <param name="requireComponent">
    /// False only for a scope the aggregate already found answering on the default service
    /// (<see cref="AppMetricsScope.ViaDefaultService"/>); see <see cref="ResolveReachableAsync"/>.
    /// </param>
    public async Task<KubernetesOperationResult<List<PrometheusTimeSeries>>> GetMetricRangeAsync(
        Guid tenantId, Guid clusterId, string query, TimeSpan duration, CancellationToken ct = default,
        bool requireComponent = true)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct, requireComponent);
        if (info is null) return KubernetesOperationResult<List<PrometheusTimeSeries>>.Failure(error!);

        long end   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = end - (long)duration.TotalSeconds;
        int  step  = Math.Max(15, (int)(duration.TotalSeconds / 100));

        // ⚠️ The rate window is sized from the step here, not written into the query text. At 12h
        // the step is 432s and at 24h it is 864s, both past the 5m these queries used to hard-code,
        // so two thirds of the 24h timeline was never looked at and a ten-minute burst between
        // sample points plotted as zero. Opt-in by token: an operator's own PromQL carries no
        // token and is passed through exactly as typed. See PromRateWindow.
        string encodedQuery = Q(PromRateWindow.Resolve(query, step));

        // Keyed on the query and window, not on `end` — that moves every second, which would make every
        // call a distinct key and cache nothing. Within the cache's few seconds, a chart whose window
        // slid by that much is indistinguishable from a fresh one.
        //
        // No tenant in the key, deliberately: a cluster has exactly one owner, so the cluster id
        // already implies it, and the resolution above has happened before anything is cached.
        return await queryCache.GetOrFetchAsync(
            EntKube.Web.Services.Telemetry.PromQueryCache.Key(clusterId, "range", query, (long)duration.TotalSeconds),
            async () => await WithServiceAsync<List<PrometheusTimeSeries>>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
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
        Guid tenantId, Guid clusterId, string label, string? matchSelector, TimeSpan lookback,
        CancellationToken ct = default)
        => await GetLabelValuesMatchingAnyAsync(
            tenantId, clusterId, label,
            string.IsNullOrEmpty(matchSelector) ? [] : [matchSelector], lookback, ct);

    /// <summary>
    /// The label-values lookup with several series selectors, of which a series has to match
    /// <b>any</b> — Prometheus's repeated <c>match[]</c>, which is a union. A selector cannot
    /// express "this label or that one", so the metrics explorer asks with one selector per
    /// label spelling. No selectors means no constraint.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetLabelValuesMatchingAnyAsync(
        Guid tenantId, Guid clusterId, string label, IReadOnlyList<string> matchSelectors, TimeSpan lookback,
        CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<string>>.Failure(error!);

        long end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = end - (long)lookback.TotalSeconds;
        string path = $"/api/v1/label/{Uri.EscapeDataString(label)}/values?start={start}&end={end}";
        foreach (string matchSelector in matchSelectors.Where(s => !string.IsNullOrEmpty(s)))
            path += $"&match[]={Q(matchSelector)}";

        return await queryCache.GetOrFetchAsync(
            EntKube.Web.Services.Telemetry.PromQueryCache.Key(
                clusterId, "labels", label, string.Join("\n", matchSelectors), (long)lookback.TotalSeconds),
            async () => await WithServiceAsync<List<string>>(
                info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
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
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<AlertInfo>>.Failure(error!);

        return await WithServiceAsync<List<AlertInfo>>(
            info.Cluster, info.Config.Namespace, info.Config.AlertmanagerServiceName, info.Config.AlertmanagerServicePort,
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
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<SilenceInfo>>.Failure(error!);

        return await WithServiceAsync<List<SilenceInfo>>(
            info.Cluster, info.Config.Namespace, info.Config.AlertmanagerServiceName, info.Config.AlertmanagerServicePort,
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
        Guid tenantId, Guid clusterId, string comment, string createdBy, TimeSpan duration,
        List<SilenceMatcher> matchers, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
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
            info.Cluster, info.Config.Namespace, info.Config.AlertmanagerServiceName, info.Config.AlertmanagerServicePort,
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
        Guid tenantId, Guid deploymentId, CancellationToken ct = default)
    {
        // Asked of Delivery rather than read out of its table — and the question is now
        // tenant-scoped, where the direct read was by id alone. The old comment here argued that
        // was safe because ForAsync decides whether the credential may be held; that is still
        // true, and this is better: a deployment that is not this tenant's does not resolve at
        // all, so the failure says "not found" rather than "cannot reach that cluster".
        EntKube.Contracts.Delivery.DeploymentSummary? deployment =
            await delivery.GetDeploymentAsync(tenantId, deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("Deployment not found.");

        Guid clusterId = deployment.ClusterId;
        string ns = deployment.Namespace ?? "";

        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<DeploymentMetricsSummary>.Failure(error!);

        return await WithServiceAsync<DeploymentMetricsSummary>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
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
    /// The PromQL for container restarts <b>inside a window</b>, as a rollout verdict needs them.
    ///
    /// <para>⚠️ <c>kube_pod_container_status_restarts_total</c> is a <b>cumulative counter</b>:
    /// an instant <c>sum()</c> of it is every restart every container in the namespace has ever
    /// had. <see cref="GetDeploymentMetricsAsync"/> reports exactly that, correctly, for the
    /// dashboards — "has anything here been restarting" is a reasonable question to answer with a
    /// lifetime total. It is the wrong answer for "did <em>this</em> rollout restart", which is a
    /// rate question, so this wraps it in <c>increase()</c> over the analysis window.</para>
    ///
    /// <para><c>increase()</c> is right across a rollout specifically because old pods' series end
    /// and new pods' series begin at zero: a replacement pod that has restarted twice contributes
    /// about two, not two plus the history of the pod it replaced.</para>
    ///
    /// <para>⚠️ Still namespace-scoped, which is a known imprecision rather than an oversight: a
    /// namespace hosting two deployments will count a neighbour's restarts toward this rollout.
    /// That is a far smaller error than counting all of history and it needs pod-ownership
    /// labels to fix, so it is left stated rather than silently narrowed.</para>
    /// </summary>
    /// <summary>
    /// The range to ask for, floored at one minute.
    ///
    /// <para>Prometheus cannot compute an <c>increase()</c> over a range shorter than its scrape
    /// interval, and a rollout measured moments after it began would otherwise ask for a range of
    /// zero and get nothing back. <c>RolloutAnalysis</c> reads nothing as <b>"restarts
    /// unavailable"</b> and drops the check from the verdict — so the window that most needs the
    /// check would silently lose it, which is a quieter failure than the one this whole change is
    /// about and worth not trading for.</para>
    /// </summary>
    public static int RestartWindowSeconds(TimeSpan window) =>
        Math.Max(60, (int)window.TotalSeconds);

    public static string RestartsInWindowQuery(string ns, int windowSeconds) =>
        $"sum(increase(kube_pod_container_status_restarts_total{{namespace=\"{ns}\"}}[{windowSeconds}s]))";

    /// <summary>
    /// Container restarts in the namespace over the last <paramref name="window"/>, for a rollout
    /// verdict. See <see cref="RestartsInWindowQuery"/> for why this is not
    /// <see cref="GetDeploymentMetricsAsync"/>'s <c>RestartCount</c>.
    /// </summary>
    public async Task<KubernetesOperationResult<int>> GetRestartsInWindowAsync(
        Guid tenantId, Guid deploymentId, TimeSpan window, CancellationToken ct = default)
    {
        EntKube.Contracts.Delivery.DeploymentSummary? deployment =
            await delivery.GetDeploymentAsync(tenantId, deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<int>.Failure("Deployment not found.");

        string ns = deployment.Namespace ?? "";

        var (info, error) = await ResolveReachableAsync(tenantId, deployment.ClusterId, ct);
        if (info is null) return KubernetesOperationResult<int>.Failure(error!);

        int seconds = RestartWindowSeconds(window);

        return await WithServiceAsync<int>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string q = Q(RestartsInWindowQuery(ns, seconds));
                double v = ExtractScalarValue(
                    await http.GetStringAsync($"{baseUrl}/api/v1/query?query={q}", token));

                return (int)Math.Round(v);
            },
            $"rollout restarts {deploymentId}", ct);
    }

    /// <summary>
    /// Returns CPU%, memory%, and pod-count time series for a cluster, selecting
    /// node-exporter/kube-state-metrics queries when available and falling back to
    /// kubelet/cAdvisor metrics when they are not scraped by this Prometheus instance.
    /// </summary>
    public async Task<KubernetesOperationResult<ClusterMetricsHistory>> GetClusterMetricsHistoryAsync(
        Guid tenantId, Guid clusterId, TimeSpan duration, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<ClusterMetricsHistory>.Failure(error!);

        long end   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = end - (long)duration.TotalSeconds;
        int  step  = Math.Max(15, (int)(duration.TotalSeconds / 100));

        return await WithServiceAsync<ClusterMetricsHistory>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                static async Task<double> Scalar(HttpClient h, string url, CancellationToken t) =>
                    ExtractScalarValue(await h.GetStringAsync(url, t));

                double hasNex = await Scalar(http, $"{baseUrl}/api/v1/query?query=count%28node_cpu_seconds_total%29", token);
                double hasKsm = await Scalar(http, $"{baseUrl}/api/v1/query?query=count%28kube_pod_info%29", token);

                string cpuQ, memQ, podQ;

                if (hasNex > 0 && hasKsm > 0)
                {
                    // Sized from this query's own step, for the reason on PromRateWindow.
                    cpuQ = Q(PromRateWindow.Resolve(ClusterHealthQueries.NodeCpuPercent, step));
                    memQ = Q(ClusterHealthQueries.NodeMemoryPercent);
                    podQ = Q(ClusterHealthQueries.RunningPods);
                }
                else
                {
                    cpuQ = Q(PromRateWindow.Resolve(ClusterHealthQueries.KubeletCpuPercent, step));
                    memQ = Q(ClusterHealthQueries.KubeletMemoryPercent);
                    podQ = Q(ClusterHealthQueries.KubeletRunningPods);
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
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        // Delivery's question, asked of Delivery. What is left here is the grouping, and the
        // per-cluster Prometheus lookup moved into AggregateMetricsAsync — which already resolved
        // each cluster through the seam, so this method was resolving half of it a second time.
        // The "has a stored kubeconfig" filter went with it: ForAsync refuses a cluster with no
        // credential, which is the same answer read from the place that owns it.
        List<ClusterQuery> clusterQueries = ByCluster(
            (await delivery.GetDeploymentsForTenantAsync(tenantId, ct)).Where(d => d.AppId == appId));

        if (clusterQueries.Count == 0)
            return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("No deployments with clusters configured for this app.");

        return TotalsOrFailure(await AggregateMetricsAsync(tenantId, clusterQueries, $"app metrics {appId}", ct));
    }

    /// <summary>
    /// CPU, memory, pods and restarts for what one app runs in <b>one environment</b>, with the
    /// per-cluster scopes the environment's charts query.
    ///
    /// <para>⚠️ <b>The environment's Metrics tab used to call <see cref="GetAppMetricsAsync"/></b>,
    /// which sums every environment of the app, under a heading that said "aggregate across all
    /// deployments in this environment" — so production's tab showed production plus staging plus
    /// test, and nothing on the screen said so. This asks Delivery for the environment's own
    /// deployments.</para>
    ///
    /// <para>Succeeds without <see cref="AppMetricsOverview.Totals"/> when every cluster simply has
    /// no Prometheus: that is a true answer, shown as an empty state naming the cluster, not a zero.
    /// Fails when nothing answered for any other reason.</para>
    /// </summary>
    public async Task<KubernetesOperationResult<AppMetricsOverview>> GetEnvironmentMetricsAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        List<ClusterQuery> clusterQueries = ByCluster(
            await delivery.GetDeploymentsForAppEnvironmentAsync(tenantId, appId, environmentId, ct));

        if (clusterQueries.Count == 0)
            return KubernetesOperationResult<AppMetricsOverview>.Failure(
                "No deployments with clusters configured for this app in this environment.");

        AppMetricsOverview overview = await AggregateMetricsAsync(
            tenantId, clusterQueries, $"environment metrics {appId}/{environmentId}", ct);

        return overview.Totals is null && overview.Problems.Count > 0
            ? KubernetesOperationResult<AppMetricsOverview>.Failure(overview.Describe())
            : KubernetesOperationResult<AppMetricsOverview>.Success(overview);
    }

    /// <summary>
    /// The volume claims of one app's namespaces on one cluster: used, capacity and free for each
    /// mounted claim, and the requested size of each unmounted one. See
    /// <see cref="AppWorkloadQueries.PvcRows"/>.
    ///
    /// <para>Fails when any of the five volume queries failed — see <see cref="ReadPvcUsageAsync"/>.</para>
    /// </summary>
    /// <param name="requireComponent">As on <see cref="GetMetricRangeAsync"/>.</param>
    public async Task<KubernetesOperationResult<List<PvcUsageRow>>> GetPvcUsageAsync(
        Guid tenantId, Guid clusterId, string nsRegex, CancellationToken ct = default,
        bool requireComponent = true)
    {
        if (string.IsNullOrWhiteSpace(nsRegex))
            return KubernetesOperationResult<List<PvcUsageRow>>.Failure(
                "No namespace to look for volume claims in.");

        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct, requireComponent);
        if (info is null) return KubernetesOperationResult<List<PvcUsageRow>>.Failure(error!);

        KubernetesOperationResult<KubernetesOperationResult<List<PvcUsageRow>>> asked =
            await WithServiceAsync<KubernetesOperationResult<List<PvcUsageRow>>>(
                info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
                (http, baseUrl, token) => ReadPvcUsageAsync(http, baseUrl, nsRegex, token),
                $"volume usage for {clusterId}", ct, probeHealthEndpoint: false);

        if (!asked.IsSuccess) return KubernetesOperationResult<List<PvcUsageRow>>.Failure(asked.Error!);
        if (!asked.Data!.IsSuccess)
            logger.LogWarning("Volume usage for {ClusterId} not shown: {Error}", clusterId, asked.Data.Error);
        return asked.Data;
    }

    /// <summary>
    /// The five volume queries against one Prometheus, joined into rows — or a failure naming the
    /// queries that did not answer.
    ///
    /// <para>⚠️ This used to go through <see cref="QueryManyAsync"/>, which turns a failed query
    /// into an empty vector so one metric a broker does not export cannot blank a whole panel.
    /// For this table that is the wrong trade: the rows are a <i>join</i> of the five vectors, and
    /// with the health probe skipped (<c>probeHealthEndpoint: false</c>) nothing else notices a
    /// Prometheus answering 503 or timing out. All five failing read as "No persistent volume
    /// claims"; only the mounted query failing turned every claim on local-path storage (which
    /// reports no kubelet stats) into "not mounted"; only the requested-size query failing showed
    /// every unmounted claim's size as "—". Each of those is a confident statement about the app's
    /// storage that nobody measured, so a partial table is not shown at all.</para>
    /// </summary>
    public static async Task<KubernetesOperationResult<List<PvcUsageRow>>> ReadPvcUsageAsync(
        HttpClient http, string baseUrl, string nsRegex, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> all = AppWorkloadQueries.Build(nsRegex);
        Dictionary<string, string> queries = AppWorkloadQueries.PvcKeys.ToDictionary(k => k, k => all[k]);

        return AppWorkloadQueries.PvcUsage(await QueryManyReportingAsync(http, baseUrl, queries, ct));
    }

    /// <summary>
    /// Aggregate CPU, memory, and pod metrics across all apps belonging to a customer.
    /// </summary>
    public async Task<KubernetesOperationResult<DeploymentMetricsSummary>> GetCustomerMetricsAsync(
        Guid tenantId, Guid customerId, CancellationToken ct = default)
    {
        // A customer's deployments are two hops in the model — customer → apps → deployments —
        // and the contract answers both, so this composes them rather than asking for a third
        // method that nothing else would use. The tenant scopes both halves.
        HashSet<Guid> customerApps =
            [.. (await delivery.GetAppsAsync(tenantId, ct))
                .Where(a => a.CustomerId == customerId)
                .Select(a => a.Id)];

        List<ClusterQuery> clusterQueries = ByCluster(
            (await delivery.GetDeploymentsForTenantAsync(tenantId, ct)).Where(d => customerApps.Contains(d.AppId)));

        if (clusterQueries.Count == 0)
            return KubernetesOperationResult<DeploymentMetricsSummary>.Failure("No deployments with clusters configured for this customer.");

        return TotalsOrFailure(await AggregateMetricsAsync(tenantId, clusterQueries, $"customer metrics {customerId}", ct));
    }

    /// <summary>
    /// Retrieves CloudNativePG cluster metrics from Prometheus: replication lag,
    /// backend count, active connections, and per-database sizes.
    /// </summary>
    /// <param name="cnpgNamespace">
    /// The database's namespace. Two CNPG clusters may share a name in different namespaces, and
    /// the series carry no cluster label to tell them apart — see <see cref="CnpgSelector"/>.
    /// </param>
    public async Task<KubernetesOperationResult<CnpgMetricsSummary>> GetCnpgClusterMetricsAsync(
        Guid tenantId, Guid clusterId, string cnpgNamespace, string cnpgClusterName, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<CnpgMetricsSummary>.Failure(error!);

        return await WithServiceAsync<CnpgMetricsSummary>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                (string lag, string backends, string active, string sizes) =
                    CnpgPanelQueries(cnpgNamespace, cnpgClusterName);
                string lagJson = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q(lag)}", token);
                string backendsJson = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q(backends)}", token);
                string queriesJson = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q(active)}", token);
                string sizesJson = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q(sizes)}", token);

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
    /// The four queries behind the database monitoring panel, in one place so the metric names can
    /// be checked against what CNPG actually exports. A name CNPG does not export returns an empty
    /// result, not an error, so a wrong one reads as zero forever: "active queries" used to read
    /// <c>cnpg_pg_stat_activity_count</c>, which CNPG 1.30 does not export. Active connections are
    /// <c>cnpg_backends_total</c> with <c>state="active"</c>.
    /// </summary>
    public static (string Lag, string Backends, string Active, string Sizes) CnpgPanelQueries(
        string cnpgNamespace, string cnpgClusterName) => (
        $"max({CnpgSelector("cnpg_pg_replication_lag", cnpgNamespace, cnpgClusterName)})",
        $"sum({CnpgSelector("cnpg_backends_total", cnpgNamespace, cnpgClusterName)})",
        $"sum({CnpgSelector("cnpg_backends_total", cnpgNamespace, cnpgClusterName, "state=\"active\"")})",
        // Every instance exports the size of every database, so the bare selector listed each
        // database once per instance: three times in a three-instance cluster.
        $"max by (datname) ({CnpgSelector("cnpg_pg_database_size_bytes", cnpgNamespace, cnpgClusterName, "datname!=\"\"")})");

    /// <summary>
    /// Builds the PromQL selector for one CNPG metric, scoped to a single database cluster.
    ///
    /// <para><b>By namespace and pod, never by a <c>cluster</c> label.</b> This used to match
    /// <c>cluster="name"</c> or <c>cluster_name="name"</c>, on the belief that CNPG's exporter
    /// labels its series with the cluster. It does not. Measured on CNPG 1.30.2 behind
    /// kube-prometheus-stack, of the ~80 <c>cnpg_*</c> series only <c>cnpg_collector_up</c> and
    /// <c>cnpg_collector_postgres_version</c> carry <c>cluster</c>. Everything the panel reads
    /// (<c>cnpg_backends_total</c>, <c>cnpg_pg_replication_lag</c>, <c>cnpg_pg_database_size_bytes</c>)
    /// carries only what Kubernetes service discovery adds: <c>namespace</c>, <c>pod</c>,
    /// <c>job</c>, <c>instance</c>. So the panel showed "no metrics" for every database, even
    /// after the "Enable metrics" button had made Prometheus scrape it — and the button looked broken.</para>
    ///
    /// <para>Instance pods are named <c>&lt;cluster&gt;-&lt;serial&gt;</c>, and PromQL anchors
    /// regex matchers at both ends, so <c>shop-[0-9]+</c> matches <c>shop-1</c> but neither
    /// <c>shop-2-1</c> (cluster <c>shop-2</c>) nor a Job pod such as <c>shop-1-initdb-x</c>.</para>
    ///
    /// <para>Every matcher goes in ONE brace block: two adjacent blocks
    /// (<c>metric{a="1"}{b="2"}</c>) are a PromQL parse error, and Prometheus answers the whole
    /// request with 400 Bad Request rather than ignoring the second one.</para>
    /// </summary>
    public static string CnpgSelector(
        string metric, string cnpgNamespace, string cnpgClusterName, string? extraMatcher = null)
    {
        string extra = string.IsNullOrEmpty(extraMatcher) ? "" : $",{extraMatcher}";
        // A name may contain '.', which is a regex wildcard; escaped for RE2, and the backslash
        // doubled for the PromQL string literal it sits in.
        string podPattern = cnpgClusterName.Replace(".", "\\\\.") + "-[0-9]+";
        return $"{metric}{{namespace=\"{cnpgNamespace}\",pod=~\"{podPattern}\"{extra}}}";
    }

    /// <summary>
    /// Works out why a CNPG database produces no metrics: whether a PodMonitor selects its
    /// instance pods, and whether Prometheus reads that PodMonitor.
    /// See <see cref="ScrapeDiagnostician"/> for what it says and why.
    /// </summary>
    public Task<KubernetesOperationResult<ScrapeDiagnosis>> DiagnoseCnpgScrapeAsync(
        Guid tenantId, Guid clusterId, string cnpgClusterName, string cnpgNamespace,
        CancellationToken ct = default) =>
        DiagnoseScrapeAsync(tenantId, clusterId, MonitoredWorkload.Cnpg(cnpgClusterName, cnpgNamespace), ct);

    private async Task<KubernetesOperationResult<ScrapeDiagnosis>> DiagnoseScrapeAsync(
        Guid tenantId, Guid clusterId, MonitoredWorkload workload, CancellationToken ct)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<ScrapeDiagnosis>.Failure(error!);

        try
        {
            ScrapeEvidence seen = await ReadScrapeEvidenceAsync(
                info.Cluster.GetPooledSdkClient(), info.Config, workload, ct);

            return KubernetesOperationResult<ScrapeDiagnosis>.Success(
                ScrapeDiagnostician.Diagnose(workload, seen));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Scrape diagnosis failed for {Workload} in {Namespace}",
                workload.Title, workload.Namespace);
            return KubernetesOperationResult<ScrapeDiagnosis>.Failure(
                ex is ScrapeReadException ? ex.Message : DescribeK8sError(ex));
        }
    }

    /// <summary>
    /// Reads everything the diagnosis decides over. Any read that fails throws, naming what could
    /// not be read. They used to be caught into an empty list, and a 403 on the monitors became
    /// the definite finding "No PodMonitor exists" — with advice to create one.
    /// </summary>
    private static async Task<ScrapeEvidence> ReadScrapeEvidenceAsync(
        Kubernetes k8s, PrometheusConfig config, MonitoredWorkload workload, CancellationToken ct)
    {
        MonitorType type = workload.Type;

        Task<List<JsonNode>?> monitors = ListMonitoringResourcesAsync(k8s, type.Plural, $"the {type.Kind}s", ct);
        Task<List<JsonNode>?> prometheuses = ListMonitoringResourcesAsync(k8s, "prometheuses", "the Prometheus resource", ct);
        Task<List<V1Pod>> workloadPods = type == MonitorType.Pod
            ? ReadWorkloadPodsAsync(k8s, workload, ct)
            : Task.FromResult<List<V1Pod>>([]);
        Task<List<V1Service>> services = type == MonitorType.Service
            ? ReadWorkloadServicesAsync(k8s, workload, ct)
            : Task.FromResult<List<V1Service>>([]);

        await Task.WhenAll(monitors, prometheuses, workloadPods, services);

        // A ServiceMonitor's pods are behind its Services, and only matter where two monitors could
        // end at the same one. They are read only then, so no other diagnosis pays for the list call.
        List<V1Pod> pods = await workloadPods;
        if (ScrapeDiagnostician.NeedsPodsBehindServices(workload, await monitors))
        {
            V1PodList inNamespace = await ReadAsync($"the pods in {workload.Namespace}",
                () => k8s.CoreV1.ListNamespacedPodAsync(workload.Namespace, cancellationToken: ct), ct);
            pods = [.. inNamespace.Items];
        }

        // Only a selector that asks for namespace labels needs them; a kubeconfig that may not
        // list namespaces still gets its answer on every other cluster.
        Dictionary<string, Dictionary<string, string>> namespaceLabels = [];
        if ((await prometheuses)?.Any(p => p["spec"]?[type.NamespaceSelector] is JsonObject { Count: > 0 }) == true)
        {
            V1NamespaceList namespaces = await ReadAsync("the namespaces",
                () => k8s.CoreV1.ListNamespaceAsync(cancellationToken: ct), ct);
            namespaceLabels = namespaces.Items.ToDictionary(
                n => n.Metadata.Name, n => LabelsOf(n.Metadata), StringComparer.Ordinal);
        }

        return new ScrapeEvidence
        {
            Monitors = await monitors,
            Prometheuses = await prometheuses,
            NamespaceLabels = namespaceLabels,
            Live = type == MonitorType.Pod
                ? [.. pods.Select(p => LabelsOf(p.Metadata))]
                : [.. (await services).Select(s => LabelsOf(s.Metadata))],
            Pods = pods,
            Services = await services,
            PrometheusNamespace = config.Namespace,
            PrometheusService = config.ServiceName,
        };
    }

    /// <summary>
    /// Every object of one monitoring.coreos.com type, in all namespaces: a monitor may select
    /// across namespaces, so the one that scrapes a workload need not be beside it. Null when the
    /// cluster has no such type — the one failure (404 on the collection) that is an answer.
    /// </summary>
    private static async Task<List<JsonNode>?> ListMonitoringResourcesAsync(
        Kubernetes k8s, string plural, string what, CancellationToken ct) =>
        await ReadAsync(what, async () =>
        {
            try
            {
                object raw = await k8s.CustomObjects.ListCustomObjectForAllNamespacesAsync(
                    "monitoring.coreos.com", "v1", plural, cancellationToken: ct);

                return JsonNode.Parse(JsonSerializer.Serialize(raw))?["items"] is JsonArray items
                    ? items.OfType<JsonNode>().ToList()
                    : throw new FormatException("the answer holds no list");
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }, ct);

    private static async Task<List<V1Pod>> ReadWorkloadPodsAsync(
        Kubernetes k8s, MonitoredWorkload workload, CancellationToken ct)
    {
        V1PodList pods = await ReadAsync($"the pods in {workload.Namespace}",
            () => k8s.CoreV1.ListNamespacedPodAsync(
                workload.Namespace,
                labelSelector: string.Join(",", workload.Labels.Select(l => $"{l.Key}={l.Value}")),
                cancellationToken: ct), ct);

        return [.. pods.Items];
    }

    /// <summary>
    /// The workload's Services: those carrying its labels, or — where only its own chart knows the
    /// labels — those that send traffic to its StatefulSet's pods.
    /// </summary>
    private static async Task<List<V1Service>> ReadWorkloadServicesAsync(
        Kubernetes k8s, MonitoredWorkload workload, CancellationToken ct)
    {
        V1ServiceList services = await ReadAsync($"the Services in {workload.Namespace}",
            () => k8s.CoreV1.ListNamespacedServiceAsync(workload.Namespace, cancellationToken: ct), ct);

        if (workload.StatefulSet is not { } statefulSet)
        {
            return [.. services.Items
                .Where(s => workload.Labels.All(l => LabelsOf(s.Metadata).GetValueOrDefault(l.Key) == l.Value))];
        }

        Dictionary<string, string>? podLabels = await ReadAsync($"StatefulSet {statefulSet}", async () =>
        {
            try
            {
                V1StatefulSet set = await k8s.AppsV1.ReadNamespacedStatefulSetAsync(
                    statefulSet, workload.Namespace, cancellationToken: ct);
                return LabelsOf(set.Spec?.Template?.Metadata);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }, ct);

        return podLabels is null
            ? []
            : [.. services.Items
                .Where(s => s.Spec?.Selector is { Count: > 0 } selector
                            && selector.All(l => podLabels.GetValueOrDefault(l.Key) == l.Value))];
    }

    private static Dictionary<string, string> LabelsOf(V1ObjectMeta? metadata) =>
        metadata?.Labels is { } labels ? new(labels, StringComparer.Ordinal) : [];

    private static async Task<T> ReadAsync<T>(string what, Func<Task<T>> read, CancellationToken ct)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new ScrapeReadException($"Could not read {what}: {DescribeK8sError(ex)}", ex);
        }
    }

    /// <summary>A part of the scrape path that could not be read, named. Not a finding about it.</summary>
    private sealed class ScrapeReadException(string message, Exception inner) : Exception(message, inner);

    private static string DescribeK8sError(Exception ex) =>
        ex switch
        {
            k8s.Autorest.HttpOperationException { Response: not null } http => $"{http.Response.StatusCode}: {http.Message}",
            // Not the caller giving up — that propagates — but the HTTP client's own timeout.
            OperationCanceledException => "the cluster did not answer in time.",
            _ => ex.Message,
        };

    /// <summary>
    /// Retrieves all active scrape targets from Prometheus with their health (up/down).
    /// </summary>
    public async Task<KubernetesOperationResult<List<ScrapeTarget>>> GetScrapeTargetsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<ScrapeTarget>>.Failure(error!);

        return await WithServiceAsync<List<ScrapeTarget>>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync($"{baseUrl}/api/v1/targets?state=active", token);
                return ParseScrapeTargets(json);
            },
            $"scrape targets for cluster {clusterId}", ct);
    }

    /// <summary>
    /// What Prometheus answers when asked which targets it is scraping, as the JSON it sent — or an
    /// exception, when it did not answer.
    ///
    /// <para><see cref="GetScrapeTargetsAsync"/> is for showing, and turns an answer it cannot read
    /// into an empty list. This is for deciding: the monitor reconcile creates a monitor for whatever
    /// is in nobody's list, so an answer that did not arrive must not look like a list with nothing
    /// in it. Reading the JSON is the caller's, for the same reason.</para>
    /// </summary>
    public async Task<string> ReadActiveTargetsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) throw new InvalidOperationException(error);

        KubernetesOperationResult<string> answer = await WithServiceAsync<string>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            (http, baseUrl, token) => http.GetStringAsync($"{baseUrl}/api/v1/targets?state=active", token),
            $"active targets for cluster {clusterId}", ct, probeHealthEndpoint: false);

        ct.ThrowIfCancellationRequested();

        return answer.IsSuccess
            ? answer.Data!
            : throw new InvalidOperationException($"Prometheus could not be asked what it scrapes: {answer.Error}");
    }

    /// <summary>
    /// Retrieves all alerting rules from Prometheus with their current evaluation state.
    /// </summary>
    public async Task<KubernetesOperationResult<List<AlertRule>>> GetAlertRulesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<List<AlertRule>>.Failure(error!);

        return await WithServiceAsync<List<AlertRule>>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
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
        Guid tenantId, Guid clusterId, RabbitMQBrokerIdentity broker, CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<RabbitMQMetricsSummary>.Failure(error!);

        return await WithServiceAsync<RabbitMQMetricsSummary>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                Dictionary<string, InstantQueryOutcome> answers =
                    await QueryManyReportingAsync(http, baseUrl, BuildRabbitMQQueries(broker), token);

                // Whether the broker is scraped at all rests on this one query, so a query that
                // failed must not read as "it is not". The others still fold: one metric the
                // running broker does not export must not blank the rest.
                if (answers["nodes"].Error is { } identityError)
                    throw new InvalidOperationException(
                        $"Whether this broker is scraped could not be read: {identityError}");

                Dictionary<string, List<PrometheusMetricResult>> r =
                    answers.ToDictionary(a => a.Key, a => a.Value.Results);
                List<string> refused = [.. answers.Values.Select(a => a.Error).OfType<string>()];

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
                    ClusterName             = broker.Name,
                    HasData                 = r["nodes"].Count > 0,
                    UnansweredQueries       = refused.Count == 0
                        ? null
                        : $"{refused.Count} of {answers.Count} queries were not answered: {refused[0]}",
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
            $"RabbitMQ metrics for {broker.Namespace}/{broker.Name}", ct);
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
    public static Dictionary<string, string> BuildRabbitMQQueries(RabbitMQBrokerIdentity broker)
    {
        // Identity is the only family carrying the cluster name, so it doubles as the node count.
        string identity = RabbitMQIdentity(broker);

        return new Dictionary<string, string>
        {
            ["nodes"]       = $"sum({identity})",
            ["total"]       = Agg("rabbitmq_queue_messages", broker),
            ["ready"]       = Agg("rabbitmq_queue_messages_ready", broker),
            ["unacked"]     = Agg("rabbitmq_queue_messages_unacked", broker),
            ["bytes"]       = Agg("rabbitmq_queue_messages_bytes", broker),
            ["connections"] = Agg("rabbitmq_connections", broker),
            ["channels"]    = Agg("rabbitmq_channels", broker),
            ["consumers"]   = Agg("rabbitmq_consumers", broker),
            ["queues"]      = Agg("rabbitmq_queues", broker),

            ["publish"]     = Rate("rabbitmq_channel_messages_published_total", broker),

            // Delivery is split by acknowledgement mode, and the two counters are disjoint:
            // "_delivered_total" counts only auto-ack deliveries, "_delivered_ack_total" only
            // manual-ack ones. Querying just the former reports zero for every manual-ack
            // consumer, which is the normal case. basic.get is included so polling consumers
            // are not invisible either.
            ["deliver"]     = RateSum(broker,
                                  "rabbitmq_channel_messages_delivered_ack_total",
                                  "rabbitmq_channel_messages_delivered_total",
                                  "rabbitmq_channel_get_ack_total",
                                  "rabbitmq_channel_get_total"),

            // Only manual-ack deliveries can be acknowledged, so this is the half that the ack
            // rate is comparable with — auto-ack traffic never produces an ack.
            ["deliverAck"]  = RateSum(broker,
                                  "rabbitmq_channel_messages_delivered_ack_total",
                                  "rabbitmq_channel_get_ack_total"),

            ["ack"]         = Rate("rabbitmq_channel_messages_acked_total", broker),
            ["redeliver"]   = Rate("rabbitmq_channel_messages_redelivered_total", broker),
            // Dropped (published non-mandatory) and returned (published mandatory) are two halves
            // of the same fault — a message that matched no binding — so they are one rate here.
            ["unroutable"]  = RateSum(broker,
                                  "rabbitmq_channel_messages_unroutable_dropped_total",
                                  "rabbitmq_channel_messages_unroutable_returned_total"),
            ["unconfirmed"] = Agg("rabbitmq_channel_messages_unconfirmed", broker),
            ["connClosed"]  = Rate("rabbitmq_connections_closed_total", broker),
            ["chanClosed"]  = Rate("rabbitmq_channels_closed_total", broker),

            // Per-node, deliberately unaggregated: one node hitting its memory watermark blocks
            // publishers cluster-wide, and a sum across nodes would hide which one. The join also
            // supplies rabbitmq_node, since the node families themselves carry no labels at all.
            ["mem"]         = PerNode("rabbitmq_process_resident_memory_bytes", broker),
            ["memLimit"]    = PerNode("rabbitmq_resident_memory_limit_bytes", broker),
            ["disk"]        = PerNode("rabbitmq_disk_space_available_bytes", broker),
            ["diskLimit"]   = PerNode("rabbitmq_disk_space_available_limit_bytes", broker),
            ["fds"]         = PerNode("rabbitmq_process_open_fds", broker),
            ["fdsMax"]      = PerNode("rabbitmq_process_max_fds", broker),
            ["sockets"]     = PerNode("rabbitmq_process_open_tcp_sockets", broker),
            ["socketsMax"]  = PerNode("rabbitmq_process_max_tcp_sockets", broker),
        };
    }

    /// <summary>
    /// The <c>rabbitmq_identity_info</c> series of one broker — the join partner that carries
    /// the cluster name.
    ///
    /// The endpoint is pinned because the plugin emits identity info once per scrape path: a
    /// ServiceMonitor that also scrapes <c>/metrics/detailed</c> (the operator's published one
    /// does) produces a second series per node, distinguished only by <c>rabbitmq_endpoint</c>.
    /// Left unpinned that doubles the node count and makes every join ambiguous, which Prometheus
    /// rejects outright with "multiple matches for labels".
    ///
    /// <para>The namespace is pinned because a broker's name is unique only within one: two
    /// brokers called <c>orders</c> in two namespaces — two tenants', perhaps — were summed into
    /// each other's panel.</para>
    ///
    /// <para>The label is added by whatever found the target: a ServiceMonitor, a PodMonitor and
    /// the annotation job all add it. An external broker may instead be scraped by a hand-written
    /// static job that adds none, so for those a series without the label still counts; one that
    /// carries another namespace never does.</para>
    /// </summary>
    public static string RabbitMQIdentity(RabbitMQBrokerIdentity broker)
    {
        string ns = broker.IsOperatorManaged ? $"=\"{broker.Namespace}\"" : $"=~\"{broker.Namespace}|\"";
        return $"rabbitmq_identity_info{{rabbitmq_cluster=\"{broker.Name}\",namespace{ns},rabbitmq_endpoint=\"aggregated\"}}";
    }

    /// <summary>
    /// Scopes a metric to one broker.
    ///
    /// The plugin labels almost nothing: every aggregated family is emitted with no labels at
    /// all, and <c>rabbitmq_cluster</c> exists only on <c>rabbitmq_identity_info</c> and
    /// <c>rabbitmq_build_info</c>. Writing <c>metric{rabbitmq_cluster="x"}</c> therefore matches
    /// no series and silently yields zero — so the cluster name has to be brought in by joining
    /// against identity info on the scrape target, which is what RabbitMQ's own dashboards do.
    ///
    /// <para>The namespace is part of "the scrape target": <c>instance</c> is a pod address, and
    /// Kubernetes hands a gone pod's address to the next one. Both sides come from the same
    /// scrape, so where one has no namespace neither does the other, and they still join.</para>
    /// </summary>
    public static string RabbitMQScoped(string expr, RabbitMQBrokerIdentity broker, string? extraJoinLabels = null)
    {
        string labels = string.IsNullOrEmpty(extraJoinLabels)
            ? "rabbitmq_cluster"
            : $"rabbitmq_cluster,{extraJoinLabels}";

        return $"{expr} * on(namespace, instance, job) group_left({labels}) {RabbitMQIdentity(broker)}";
    }

    private static string Agg(string metric, RabbitMQBrokerIdentity broker) =>
        $"sum({RabbitMQScoped(metric, broker)})";

    private static string Rate(string metric, RabbitMQBrokerIdentity broker) =>
        $"sum({RabbitMQScoped($"rate({metric}[5m])", broker)})";

    private static string PerNode(string metric, RabbitMQBrokerIdentity broker) =>
        RabbitMQScoped(metric, broker, "rabbitmq_node");

    /// <summary>
    /// Adds several counters' rates into one figure.
    ///
    /// Each term is defaulted with <c>or vector(0)</c> because vector arithmetic drops to an
    /// empty result when either side has no series: one counter a broker happens not to export
    /// would otherwise zero the whole sum and, with it, the warning that depends on it. The price
    /// is that these queries answer for a broker that does not exist, so they say nothing about
    /// whether it is scraped — see <see cref="RabbitMQMetricsSummary.HasData"/>.
    /// </summary>
    private static string RateSum(RabbitMQBrokerIdentity broker, params string[] metrics) =>
        string.Join(" + ", metrics.Select(m => $"({Rate(m, broker)} or vector(0))"));

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
        Guid tenantId, Guid clusterId, string instanceName, string keycloakNamespace,
        CancellationToken ct = default)
    {
        var (info, error) = await ResolveReachableAsync(tenantId, clusterId, ct);
        if (info is null) return KubernetesOperationResult<KeycloakMetricsSummary>.Failure(error!);

        return await WithServiceAsync<KeycloakMetricsSummary>(
            info.Cluster, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            (http, baseUrl, token) => QueryKeycloakMetricsAsync(http, baseUrl, instanceName, keycloakNamespace, token),
            $"Keycloak metrics for {instanceName}", ct);
    }

    /// <summary>
    /// Works out why a workload that should be exporting metrics produces none: whether a monitor
    /// selects it, and whether Prometheus reads that monitor. Shared by RabbitMQ and Keycloak.
    /// See <see cref="ScrapeDiagnostician"/> for what it says and why.
    /// </summary>
    public Task<KubernetesOperationResult<ScrapeDiagnosis>> DiagnoseServiceMonitorScrapeAsync(
        Guid tenantId, Guid clusterId, MonitoredWorkload workload, CancellationToken ct = default) =>
        DiagnoseScrapeAsync(tenantId, clusterId, workload, ct);

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

    /// <summary>
    /// <see cref="QueryManyAsync"/> for callers to whom a failed query is not "no data": every
    /// key's outcome carries either its results or why there are none. The Keycloak panel keeps the
    /// folding variant, where one unexported metric must not blank the rest.
    /// Cancellation by <paramref name="ct"/> propagates; it is the caller giving up, not an answer.
    /// </summary>
    public static async Task<Dictionary<string, InstantQueryOutcome>> QueryManyReportingAsync(
        HttpClient http, string baseUrl, IReadOnlyDictionary<string, string> queries, CancellationToken ct)
    {
        KeyValuePair<string, string>[] pairs = [.. queries];

        InstantQueryOutcome[] outcomes = await Task.WhenAll(
            pairs.Select(kv => InstantReportingAsync(http, baseUrl, kv.Value, ct)));

        Dictionary<string, InstantQueryOutcome> byKey = [];
        for (int i = 0; i < pairs.Length; i++)
            byKey[pairs[i].Key] = outcomes[i];

        return byKey;
    }

    private static async Task<InstantQueryOutcome> InstantReportingAsync(
        HttpClient http, string baseUrl, string query, CancellationToken ct)
    {
        try
        {
            string json = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q(query)}", ct);
            string? refused = PrometheusRefusal(json);
            return refused is null
                ? new InstantQueryOutcome(ParseInstantQueryResult(json), null)
                : new InstantQueryOutcome([], refused);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // Not our token: HttpClient's own timeout, which surfaces as a cancellation.
            return new InstantQueryOutcome([], "Prometheus did not answer in time.");
        }
        catch (Exception ex)
        {
            return new InstantQueryOutcome([], ex.Message);
        }
    }

    /// <summary>
    /// Why a 2xx body is not a result, or null when it is one. <see cref="ParseInstantQueryResult"/>
    /// answers an empty list for both <c>"status":"error"</c> and a body that is not JSON at all
    /// (a proxy's HTML page), which is exactly the confusion the reporting path exists to avoid.
    /// </summary>
    private static string? PrometheusRefusal(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out JsonElement status))
                return "Prometheus answered with something that is not a query result.";
            if (status.GetString() == "success") return null;
            return root.TryGetProperty("error", out JsonElement error) && error.GetString() is { Length: > 0 } message
                ? message
                : $"Prometheus answered with status \"{status.GetString()}\".";
        }
        catch (JsonException)
        {
            return "Prometheus answered with something that is not a query result.";
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
    /// <summary>The same derivation for a component that arrived through <c>ICatalogApi</c>.</summary>
    public static PrometheusConfig GetPrometheusConfig(
        EntKube.Contracts.Catalog.InstalledComponent component)
        => GetPrometheusConfig(
            component.Name, component.ReleaseName, component.Namespace, component.Configuration);

    public static PrometheusConfig GetPrometheusConfig(ClusterComponent component)
        => GetPrometheusConfig(
            component.Name, component.ReleaseName, component.Namespace, component.Configuration);

    private static PrometheusConfig GetPrometheusConfig(
        string componentName, string? componentRelease, string? componentNamespace, string? configuration)
    {
        // Derive defaults from component metadata so they work regardless of release name.
        string releaseName = componentRelease ?? componentName;
        string ns          = componentNamespace ?? "monitoring";

        PrometheusConfig derived = new()
        {
            Namespace               = ns,
            ServiceName             = $"{releaseName}-prometheus",
            AlertmanagerServiceName = $"{releaseName}-alertmanager",
        };

        if (string.IsNullOrWhiteSpace(configuration))
            return derived;

        try
        {
            PrometheusConfig? explicit_ = JsonSerializer.Deserialize<PrometheusConfig>(
                configuration,
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

    /// <summary>One cluster's share of an aggregate: which cluster, its name for messages, and the namespaces.</summary>
    private sealed record ClusterQuery(Guid ClusterId, string ClusterName, string NsRegex);

    /// <summary>Deployments grouped by cluster, each group reduced to its namespace alternation.</summary>
    private static List<ClusterQuery> ByCluster(IEnumerable<EntKube.Contracts.Delivery.DeploymentSummary> deployments)
        => [.. deployments
            .GroupBy(d => d.ClusterId)
            .Select(g => new ClusterQuery(g.Key, g.First().ClusterName, NsRegex(g)))];

    /// <summary>
    /// The namespace alternation for one cluster's deployments, escaped. Distinct, because several
    /// deployments of one app can share a namespace and a repeated alternative is a longer query
    /// for the same answer. Empty when none of them has a namespace — which
    /// <see cref="AggregateMetricsAsync"/> refuses rather than queries.
    /// </summary>
    private static string NsRegex(IEnumerable<EntKube.Contracts.Delivery.DeploymentSummary> deployments)
        => string.Join("|", deployments
            .Select(d => Regex.Escape(d.Namespace ?? ""))
            .Where(ns => ns.Length > 0)
            .Distinct());

    /// <summary>The single-number callers' view of an overview: no totals is a failure, never a zero.</summary>
    private static KubernetesOperationResult<DeploymentMetricsSummary> TotalsOrFailure(AppMetricsOverview overview)
        => overview.Totals is null
            ? KubernetesOperationResult<DeploymentMetricsSummary>.Failure(overview.Describe())
            : KubernetesOperationResult<DeploymentMetricsSummary>.Success(overview.Totals);

    /// <summary>
    /// Sums one metric set across several clusters, and says what happened to each one that did
    /// not contribute.
    ///
    /// <para>Takes cluster <em>ids</em> and resolves each through the seam, so a cluster this
    /// tenant does not own is skipped rather than queried — the tuple used to carry a kubeconfig,
    /// which made every producer of it a custodian.</para>
    ///
    /// <para><b>Each cluster ends in exactly one of three places.</b></para>
    /// <list type="bullet">
    /// <item><b>Answered</b> — its Prometheus answered all four queries: summed into
    /// <see cref="AppMetricsOverview.Totals"/> and listed in <see cref="AppMetricsOverview.Scopes"/>.
    /// That includes a cluster with <em>no</em> EntKube component whose default service
    /// (<see cref="PrometheusConfig"/>: <c>prometheus-kube-prometheus-prometheus:9090</c> in
    /// <c>monitoring</c>) answers — a hand-installed release EntKube never tracked.</item>
    /// <item><b>Without Prometheus</b> — it resolves, has no component, and nothing answers the
    /// probe of the default service — the endpoints lookup answers and no ready pod stands behind
    /// it (<see cref="NoReadyPodException"/>):
    /// <see cref="AppMetricsOverview.ClustersWithoutPrometheus"/>, the honest empty state.</item>
    /// <item><b>A problem</b> — it does not resolve for this tenant (or has no kubeconfig), its
    /// deployments carry no namespace, its registered Prometheus fails, or the default service
    /// passed the probe and then a query failed.</item>
    /// </list>
    ///
    /// <para>⚠️ <b>The component-less probe is load-bearing both ways.</b> Originally every
    /// component-less cluster was queried and a failure surfaced as a proxy error about a service
    /// nobody had installed. Then the query was dropped (<c>requireComponent: true</c>), and the
    /// customer tab and <see cref="GetAppMetricsAsync"/> stopped reading every cluster running a
    /// Prometheus EntKube did not install — those read as "not installed". The probe decides
    /// between the two, and only "no ready pod" means "not installed". A lookup that could not be
    /// made (403, an API server that does not answer, a timeout), an unhealthy pod, or a query
    /// that failed is a problem — reported as "not installed" it sent operators to install a
    /// Prometheus that may well be there.</para>
    ///
    /// <para>⚠️ <b>A successful zero is still impossible.</b> A group whose deployments have no
    /// namespace is refused rather than queried with <c>namespace=~""</c>, which matches no
    /// container series and answered 0 cores, 0 pods as success; and
    /// <see cref="AppMetricsOverview.Totals"/> is null unless at least one cluster answered.</para>
    /// </summary>
    private async Task<AppMetricsOverview> AggregateMetricsAsync(
        Guid tenantId,
        IEnumerable<ClusterQuery> clusterQueries,
        string logContext,
        CancellationToken ct)
    {
        double totalCpu = 0, totalMemBytes = 0, totalPods = 0, totalRestarts = 0;
        List<AppMetricsScope> answered = [];
        List<string> withoutPrometheus = [];
        List<string> problems = [];

        foreach (ClusterQuery q in clusterQueries)
        {
            if (q.NsRegex.Length == 0)
            {
                problems.Add($"{q.ClusterName}: no namespace is recorded for the deployments there, so there is nothing to scope a query to.");
                continue;
            }

            // Without the component requirement: a cluster that resolves but has no component is
            // probed on the default service below rather than written off here. Null now means
            // only that the cluster itself did not resolve — not this tenant's, or no kubeconfig.
            (ReachablePrometheus? reachable, string? error) =
                await ResolveReachableAsync(tenantId, q.ClusterId, ct, requireComponent: false);

            if (reachable is null)
            {
                problems.Add($"{q.ClusterName}: {error}");
                continue;
            }

            EntKube.Web.Services.Clusters.IClusterClient cluster = reachable.Cluster;
            PrometheusConfig cfg = reachable.Config;
            string nsRegex = q.NsRegex;

            // The probe's one "not installed" answer: the cluster's API answered the endpoints
            // lookup and no ready pod stands behind the default service (an absent namespace lists
            // as empty, so it lands here too). Any other failure — 403 on the lookup, an API server
            // that does not answer, a timeout, an unhealthy pod — is a problem with this cluster,
            // and reporting it as "Prometheus is not installed" sent operators to install one.
            bool nothingServing = false;

            KubernetesOperationResult<(double, double, double, double)> result =
                await WithServiceAsync<(double, double, double, double)>(
                    cluster, cfg.Namespace, cfg.ServiceName, cfg.ServicePort,
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
                    logContext, ct,
                    onFailure: ex => nothingServing = ex is NoReadyPodException);

            if (result.IsSuccess)
            {
                answered.Add(new AppMetricsScope(q.ClusterId, q.ClusterName, q.NsRegex,
                    ViaDefaultService: !reachable.Registered));
                totalCpu      += result.Data.Item1;
                totalMemBytes += result.Data.Item2;
                totalPods     += result.Data.Item3;
                totalRestarts += result.Data.Item4;
            }
            else if (!reachable.Registered && nothingServing)
            {
                // No component, and nothing answers where one would be: not installed. Not a
                // problem carrying a proxy error about a service nobody installed.
                withoutPrometheus.Add(q.ClusterName);
            }
            else
            {
                problems.Add($"{q.ClusterName}: {result.Error}");
            }
        }

        return new AppMetricsOverview
        {
            // Null unless something answered: a skipped cluster must not read as a measured zero.
            Totals = answered.Count == 0 ? null : new DeploymentMetricsSummary
            {
                Namespace    = "aggregated",
                CpuCores     = Math.Round(totalCpu, 4),
                MemoryMiB    = Math.Round(totalMemBytes / (1024 * 1024), 1),
                PodCount     = (int)totalPods,
                RestartCount = (int)totalRestarts,
                QueriedAt    = DateTime.UtcNow
            },
            Scopes = answered,
            ClustersWithoutPrometheus = withoutPrometheus,
            Problems = problems,
        };
    }

    // ──────── Internal Helpers ────────

    /// <summary>
    /// A prometheus this tenant may reach: the cluster as a seam client, and where on it to look.
    ///
    /// <para><b>There used to be two of these.</b> The other, <c>ResolvedPrometheusInfo</c>,
    /// carried a <c>string Kubeconfig</c> and was reached by cluster id alone — so a cluster id
    /// answered about any cluster in the installation. Converting all seventeen methods that used
    /// it at once is what I tried and abandoned: every signature threaded surfaced callers that
    /// were not counted, and the error count went up as often as down. The two resolutions lived
    /// side by side instead and the methods moved across in six steps, each compiling and tested.
    /// This is what is left.</para>
    /// </summary>
    /// <param name="Cluster">The cluster, as a seam client.</param>
    /// <param name="Config">Where on it Prometheus is, or would be.</param>
    /// <param name="Registered">
    /// Whether <paramref name="Config"/> came from a Prometheus component Catalog knows of. False
    /// only from <c>requireComponent: false</c> on a cluster without one, where the config is the
    /// default service name — a guess the caller has to probe before it means anything.
    /// </param>
    private sealed record ReachablePrometheus(
        EntKube.Web.Services.Clusters.IClusterClient Cluster, PrometheusConfig Config, bool Registered);

    /// <summary>
    /// The tenant-scoped resolution. The old one is <c>c.Id == clusterId</c> with no tenant
    /// predicate, which is why a cluster id alone answered about any cluster in the installation.
    /// </summary>
    /// <param name="requireComponent">
    /// Whether a registered prometheus component is required — "registered" meaning what
    /// <c>ICatalogApi.FindPrometheusAsync</c> answers, which includes a stack mid-upgrade or whose
    /// upgrade failed. The single-cluster callers say yes (the default): they answer a question
    /// about one cluster, and "No prometheus component found on this cluster." is the answer
    /// rather than an empty graph. Only <see cref="AggregateMetricsAsync"/> says no: a cluster
    /// without a component then resolves with the default <see cref="PrometheusConfig"/> and
    /// <c>Registered: false</c>, and the aggregate probes that service — it answers for a
    /// hand-installed release, and a failed probe is "not installed". Either way, a cluster that
    /// is not this tenant's, or has no kubeconfig, does not resolve.
    /// </param>
    private async Task<(ReachablePrometheus? Info, string? Error)> ResolveReachableAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct, bool requireComponent = true)
    {
        EntKube.Web.Services.Clusters.IClusterClient? cluster = await clusterClients.ForAsync(tenantId, clusterId, ct);

        if (cluster is null)
            return (null, "Cluster not found for this tenant, or it has no kubeconfig configured.");

        // Catalog's one answer to "does this cluster have Prometheus". This used to be the first
        // component whose NAME contained "prometheus" — any status, any chart — so a failed install
        // was queried as if it were running, and whichever prometheus-named component came back
        // first decided the service name.
        EntKube.Contracts.Catalog.InstalledComponent? prometheusComponent =
            await catalog.FindPrometheusAsync(tenantId, clusterId, ct);

        if (prometheusComponent is null)
        {
            return requireComponent
                ? (null, "No prometheus component found on this cluster.")
                : (new ReachablePrometheus(cluster, new PrometheusConfig(), Registered: false), null);
        }

        return (new ReachablePrometheus(cluster, GetPrometheusConfig(prometheusComponent), Registered: true), null);
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
    /// <summary>
    /// The proxy round-trip for a cluster reached through the seam. There was a second overload
    /// taking a raw <c>string kubeconfig</c>, for the methods not yet converted; it went with the
    /// last of them, and with it the client pool this service used to hold.
    /// </summary>
    private async Task<KubernetesOperationResult<T>> WithServiceAsync<T>(
        EntKube.Web.Services.Clusters.IClusterClient cluster,
        string ns,
        string svcName,
        int svcPort,
        Func<HttpClient, string, CancellationToken, Task<T>> action,
        string logContext,
        CancellationToken ct,
        bool probeHealthEndpoint = true,
        Action<Exception>? onFailure = null)
        => await WithServiceCoreAsync(
            cluster.GetPooledSdkClient(), ns, svcName, svcPort, action, logContext, ct,
            probeHealthEndpoint, onFailure);

    private async Task<KubernetesOperationResult<T>> WithServiceCoreAsync<T>(
        Kubernetes k8s,
        string ns,
        string svcName,
        int svcPort,
        Func<HttpClient, string, CancellationToken, Task<T>> action,
        string logContext,
        CancellationToken ct,
        bool probeHealthEndpoint,
        Action<Exception>? onFailure = null)
    {
        try
        {
            V1EndpointAddress? addr = await FindEndpointAddressAsync(k8s, ns, svcName, svcPort, ct);
            if (addr?.TargetRef is null)
                throw new NoReadyPodException(
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

            // The probe is a round-trip that tells us what the very next request is about
            // to tell us anyway. Callers that immediately query skip it: on a page built
            // from one tile per cluster it was a fixed WAN latency added to every tile,
            // buying nothing but a slightly tidier error message.
            if (probeHealthEndpoint)
            {
                await VerifyPrometheusConnectionAsync(k8s.HttpClient, baseUrl, ct);
            }

            T result = await action(k8s.HttpClient, baseUrl, ct);
            return KubernetesOperationResult<T>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prometheus query failed ({Context})", logContext);
            // The failure's kind, for the one caller that must tell "nothing serving here" from
            // "could not ask" — the message alone is the only thing the result carries.
            onFailure?.Invoke(ex);
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
    ///
    /// <para>Throws <see cref="UnreadableTargetListException"/> rather than answering with what it
    /// managed to read. It used to swallow every error, so a proxy's HTML page was "no targets" and
    /// one odd field cut the list short — and both then read as "nothing is down" and "nothing is
    /// scraped twice".</para>
    /// </summary>
    public static List<ScrapeTarget> ParseScrapeTargets(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success")
            {
                throw new UnreadableTargetListException(
                    root.TryGetProperty("error", out JsonElement error) && error.GetString() is { Length: > 0 } said
                        ? said
                        : "Prometheus did not answer with success.");
            }

            List<ScrapeTarget> results = [];
            foreach (JsonElement t in root.GetProperty("data").GetProperty("activeTargets").EnumerateArray())
            {
                ScrapeTarget target = new()
                {
                    // Without these two a target cannot be told apart from another one.
                    Pool      = t.GetProperty("scrapePool").GetString() ?? "",
                    ScrapeUrl = t.GetProperty("scrapeUrl").GetString() ?? "",
                    Health    = t.TryGetProperty("health",    out JsonElement h)  ? h.GetString()  ?? "" : "",
                    LastError = t.TryGetProperty("lastError", out JsonElement le) ? le.GetString() ?? "" : "",
                    LastScrapeDurationSeconds =
                        t.TryGetProperty("lastScrapeDuration", out JsonElement ld) && ld.ValueKind == JsonValueKind.Number
                            ? ld.GetDouble() : 0
                };

                if (t.TryGetProperty("lastScrape", out JsonElement ls) && ls.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ls.GetString(), out DateTimeOffset scraped))
                    target.LastScrape = scraped.UtcDateTime;

                if (t.TryGetProperty("labels", out JsonElement labels))
                    foreach (JsonProperty p in labels.EnumerateObject())
                        target.Labels[p.Name] = p.Value.GetString() ?? "";

                results.Add(target);
            }
            return results;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                                   or (InvalidOperationException and not UnreadableTargetListException))
        {
            throw new UnreadableTargetListException(ex.Message);
        }
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

    private static string Q(string promQuery) => Uri.EscapeDataString(promQuery);

    private static double ExtractScalarValue(string json)
    {
        List<PrometheusMetricResult> results = ParseInstantQueryResult(json);
        return results.Count > 0 ? results[0].Value : 0;
    }
}

/// <summary>
/// Prometheus answered, and its answer is not a list of scrape targets. Never the same as an empty
/// list: what is scraped is then unknown.
/// </summary>
public sealed class UnreadableTargetListException(string why)
    : InvalidOperationException($"The list of scrape targets could not be read: {why}");

/// <summary>
/// The endpoints lookup answered and no ready pod stands behind the service: nothing is serving
/// there. Its own type so the aggregate can tell this — "not installed" — apart from a lookup that
/// could not be made at all (403, network, timeout), which is a problem with the cluster.
/// </summary>
internal sealed class NoReadyPodException(string message) : InvalidOperationException(message);
