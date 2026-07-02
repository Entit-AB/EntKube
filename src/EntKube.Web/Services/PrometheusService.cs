using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
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
/// A single data point in a time series — a timestamp/value pair.
/// </summary>
public class TimeSeriesDataPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
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
/// </summary>
public class RabbitMQMetricsSummary
{
    public string ClusterName { get; set; } = "";
    public int Nodes { get; set; }
    public long TotalMessages { get; set; }
    public long ReadyMessages { get; set; }
    public long UnackedMessages { get; set; }
    public int Connections { get; set; }
    public int Channels { get; set; }
    public double PublishRatePerSec { get; set; }
    public double DeliverRatePerSec { get; set; }
    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Metrics for a CloudNativePG cluster scraped from Prometheus.
/// </summary>
public class CnpgMetricsSummary
{
    public string ClusterName { get; set; } = "";
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

        return await WithServiceAsync<List<PrometheusTimeSeries>>(
            info.Kubeconfig, info.Config.Namespace, info.Config.ServiceName, info.Config.ServicePort,
            async (http, baseUrl, token) =>
            {
                string json = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query_range?query={encodedQuery}&start={start}&end={end}&step={step}", token);
                return ParseRangeQueryResult(json);
            },
            $"range query for {clusterId}", ct);
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
                string clusterLabel = $"{{cluster_name=\"{cnpgClusterName}\"}}";

                string lagJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"max(cnpg_pg_replication_lag{clusterLabel})")}",
                    token);
                string backendsJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"sum(cnpg_backends_total{clusterLabel})")}",
                    token);
                string queriesJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"sum(cnpg_pg_stat_activity_count{clusterLabel}{{state=\"active\"}})")}",
                    token);
                string sizesJson = await http.GetStringAsync(
                    $"{baseUrl}/api/v1/query?query={Q($"cnpg_pg_database_size_bytes{clusterLabel}{{datname!=\"\"}}")}",
                    token);

                List<PrometheusMetricResult> lagResults = ParseInstantQueryResult(lagJson);
                List<PrometheusMetricResult> backendResults = ParseInstantQueryResult(backendsJson);
                List<PrometheusMetricResult> queryResults = ParseInstantQueryResult(queriesJson);
                List<PrometheusMetricResult> sizeResults = ParseInstantQueryResult(sizesJson);

                return new CnpgMetricsSummary
                {
                    ClusterName = cnpgClusterName,
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
                string label = $"{{rabbitmq_cluster=\"{rabbitMQName}\"}}";

                string readyJson    = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rabbitmq_identity_info{label})")}", token);
                string msgJson      = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rabbitmq_queue_messages{label})")}", token);
                string readyMsgJson = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rabbitmq_queue_messages_ready{label})")}", token);
                string unackJson    = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rabbitmq_queue_messages_unacknowledged{label})")}", token);
                string connJson     = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rabbitmq_connections{label})")}", token);
                string chanJson     = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rabbitmq_channels{label})")}", token);
                string pubRateJson  = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rate(rabbitmq_queue_messages_published_total{label}[5m]))")}", token);
                string conRateJson  = await http.GetStringAsync($"{baseUrl}/api/v1/query?query={Q($"sum(rate(rabbitmq_queue_messages_delivered_total{label}[5m]))")}", token);

                return new RabbitMQMetricsSummary
                {
                    ClusterName        = rabbitMQName,
                    Nodes              = (int)ExtractScalarValue(readyJson),
                    TotalMessages      = (long)ExtractScalarValue(msgJson),
                    ReadyMessages      = (long)ExtractScalarValue(readyMsgJson),
                    UnackedMessages    = (long)ExtractScalarValue(unackJson),
                    Connections        = (int)ExtractScalarValue(connJson),
                    Channels           = (int)ExtractScalarValue(chanJson),
                    PublishRatePerSec  = Math.Round(ExtractScalarValue(pubRateJson), 2),
                    DeliverRatePerSec  = Math.Round(ExtractScalarValue(conRateJson), 2)
                };
            },
            $"RabbitMQ metrics for {rabbitMQName}", ct);
    }

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
            using Kubernetes k8s = CreateK8sClient(kubeconfig);

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

    private static Kubernetes CreateK8sClient(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }

    private static string Q(string promQuery) => Uri.EscapeDataString(promQuery);

    private static double ExtractScalarValue(string json)
    {
        List<PrometheusMetricResult> results = ParseInstantQueryResult(json);
        return results.Count > 0 ? results[0].Value : 0;
    }
}
