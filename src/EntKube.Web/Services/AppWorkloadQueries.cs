using System.Text;

namespace EntKube.Web.Services;

/// <summary>
/// One cluster's share of an app environment's metrics: where to ask, and which namespaces to ask
/// about. <paramref name="NsRegex"/> is an escaped alternation (<c>billing|billing\-jobs</c>), never
/// empty — an empty one is refused before a query is built, see <see cref="AppWorkloadQueries.Build"/>.
/// </summary>
public sealed record AppMetricsScope(Guid ClusterId, string ClusterName, string NsRegex);

/// <summary>One persistent volume claim of an app, as the PVC table shows it.</summary>
/// <param name="Mounted">
/// Whether the kubelet reports the volume. Volume stats exist only while a pod mounts the claim;
/// an unmounted claim has a requested size and nothing else.
/// </param>
public sealed record PvcUsageRow(
    string Namespace,
    string Claim,
    double? UsedBytes,
    double? CapacityBytes,
    double? AvailableBytes,
    double? RequestedBytes,
    bool Mounted)
{
    /// <summary>Used as a share of capacity, or null when either is unknown.</summary>
    public double? UsedPercent =>
        UsedBytes is double used && CapacityBytes is double cap && cap > 0 ? used / cap * 100 : null;
}

/// <summary>
/// The PromQL behind every app's basic metrics: CPU, memory, restarts, volume usage and HTTP
/// traffic — what an app gets with nothing to configure on a cluster that runs Prometheus.
///
/// <para>Separated from the queries' execution so the metric names are assertable, the pattern of
/// <see cref="PrometheusService.BuildKeycloakQueries"/>: a misspelt metric name is not an error
/// in PromQL, it is an empty vector, and an empty chart looks exactly like an idle app.</para>
///
/// <para><b>Where each name comes from</b>, checked against the sources EntKube installs:</para>
/// <list type="bullet">
/// <item><c>container_cpu_usage_seconds_total</c>, <c>container_memory_working_set_bytes</c> —
/// the kubelet's cAdvisor endpoint, scraped by kube-prometheus-stack 93.9.0, whose own recording
/// rules filter them with <c>image!=""</c> as these do. <c>container!=""</c> drops the pod-level
/// cgroup series, which would otherwise count every container twice.</item>
/// <item><c>kube_pod_container_status_restarts_total</c>,
/// <c>kube_persistentvolumeclaim_resource_requests_storage_bytes</c> — kube-state-metrics 2.20.0,
/// the version that chart bundles.</item>
/// <item><c>kubelet_volume_stats_{used,capacity,available}_bytes</c> — the kubelet's own
/// <c>/metrics</c>, used by the chart's KubePersistentVolumeFillingUp alerts.</item>
/// <item><c>http_server_request_duration_seconds</c> (<c>_count</c>, <c>_bucket</c>) with
/// <c>http_response_status_code</c> — OBI v1.0.0, the eBPF agent: the OTel
/// <c>http.server.request.duration</c> histogram translated with unit suffixes. Its series carry
/// <c>namespace</c> and <c>pod</c> because the agent's export relabels them so.</item>
/// </list>
///
/// <para>The range queries use <see cref="PromRateWindow.Token"/> rather than a fixed window, as
/// <see cref="PrometheusService.GetMetricRangeAsync"/> expects, so a 24-hour chart does not sample
/// one five-minute window per fifteen-minute step.</para>
/// </summary>
public static class AppWorkloadQueries
{
    public const string Cpu = "cpu";
    public const string CpuByPod = "cpuByPod";
    public const string Memory = "memory";
    public const string MemoryByPod = "memoryByPod";
    public const string Restarts = "restarts";
    public const string PvcUsed = "pvcUsed";
    public const string PvcCapacity = "pvcCapacity";
    public const string PvcAvailable = "pvcAvailable";
    public const string PvcRequested = "pvcRequested";
    public const string HttpRate = "httpRate";
    public const string HttpErrors = "httpErrors";
    public const string HttpP95 = "httpP95";

    /// <summary>The volume queries: instant, per claim, for the table rather than a chart.</summary>
    public static readonly IReadOnlyList<string> PvcKeys = [PvcUsed, PvcCapacity, PvcAvailable, PvcRequested];

    /// <summary>
    /// Every query for one cluster's namespaces.
    /// </summary>
    /// <param name="nsRegex">The escaped namespace alternation, as <see cref="AppMetricsScope.NsRegex"/>.</param>
    /// <exception cref="ArgumentException">
    /// When <paramref name="nsRegex"/> is empty. <c>namespace=~""</c> is not "no namespaces": it
    /// matches every series that has no namespace label at all, and for the volume and HTTP
    /// queries that is an empty vector — a successful zero that reads as an idle app.
    /// </exception>
    public static IReadOnlyDictionary<string, string> Build(string nsRegex)
    {
        if (string.IsNullOrWhiteSpace(nsRegex))
            throw new ArgumentException(
                "No namespace to scope the queries to; an empty match would read as an idle app.",
                nameof(nsRegex));

        string ns = $"namespace=~\"{Literal(nsRegex)}\"";
        string containers = $"{{{ns},container!=\"\",image!=\"\"}}";
        string nsOnly = $"{{{ns}}}";
        const string W = PromRateWindow.Token;

        // `max`, not `sum`: a ReadWriteMany volume is reported by the kubelet of every node that
        // mounts it, so summing would count one volume once per node.
        static string PerClaim(string metric, string sel) =>
            $"max by (namespace, persistentvolumeclaim) ({metric}{sel})";

        return new Dictionary<string, string>
        {
            [Cpu]          = $"sum(rate(container_cpu_usage_seconds_total{containers}[{W}]))",
            [CpuByPod]     = $"sum by (pod) (rate(container_cpu_usage_seconds_total{containers}[{W}]))",
            [Memory]       = $"sum(container_memory_working_set_bytes{containers})",
            [MemoryByPod]  = $"sum by (pod) (container_memory_working_set_bytes{containers})",
            // The counter is cumulative, so an instant sum is every restart ever; increase() over
            // the window is what a chart of "restarting now" needs.
            [Restarts]     = $"sum(increase(kube_pod_container_status_restarts_total{nsOnly}[{W}]))",

            [PvcUsed]      = PerClaim("kubelet_volume_stats_used_bytes", nsOnly),
            [PvcCapacity]  = PerClaim("kubelet_volume_stats_capacity_bytes", nsOnly),
            [PvcAvailable] = PerClaim("kubelet_volume_stats_available_bytes", nsOnly),
            // Known for every bound or pending claim, mounted or not — the only size an
            // unmounted claim has.
            [PvcRequested] = PerClaim("kube_persistentvolumeclaim_resource_requests_storage_bytes", nsOnly),

            [HttpRate]     = $"sum(rate(http_server_request_duration_seconds_count{nsOnly}[{W}]))",
            [HttpErrors]   = $"sum(rate(http_server_request_duration_seconds_count{{{ns},http_response_status_code=~\"5..\"}}[{W}]))",
            [HttpP95]      = $"histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{nsOnly}[{W}])))",
        };
    }

    /// <summary>
    /// Joins the four per-claim vectors into table rows, one per claim.
    ///
    /// <para>The claim set is the union: the kubelet knows only mounted claims and
    /// kube-state-metrics knows every claim, so a claim with a requested size and no volume stats
    /// is an unmounted one — shown with its requested size rather than dropped, or worse, shown as
    /// 0 bytes used.</para>
    /// </summary>
    public static List<PvcUsageRow> PvcRows(
        IReadOnlyList<PrometheusMetricResult> used,
        IReadOnlyList<PrometheusMetricResult> capacity,
        IReadOnlyList<PrometheusMetricResult> available,
        IReadOnlyList<PrometheusMetricResult> requested)
    {
        Dictionary<(string, string), double> u = ByClaim(used), c = ByClaim(capacity),
            a = ByClaim(available), r = ByClaim(requested);

        return [.. u.Keys.Concat(c.Keys).Concat(a.Keys).Concat(r.Keys)
            .Distinct()
            .OrderBy(k => k.Item1, StringComparer.Ordinal)
            .ThenBy(k => k.Item2, StringComparer.Ordinal)
            .Select(k =>
            {
                bool mounted = u.ContainsKey(k) || c.ContainsKey(k) || a.ContainsKey(k);
                return new PvcUsageRow(
                    k.Item1, k.Item2,
                    UsedBytes:      u.TryGetValue(k, out double uv) ? uv : null,
                    CapacityBytes:  c.TryGetValue(k, out double cv) ? cv : null,
                    AvailableBytes: a.TryGetValue(k, out double av) ? av : null,
                    RequestedBytes: r.TryGetValue(k, out double rv) ? rv : null,
                    Mounted: mounted);
            })];
    }

    /// <summary>
    /// The points a chart can draw. <c>histogram_quantile</c> answers NaN for a step with no
    /// requests, and a NaN in an SVG coordinate breaks the whole line rather than leaving a gap.
    /// </summary>
    public static List<TimeSeriesDataPoint> Drawable(IEnumerable<TimeSeriesDataPoint> points) =>
        [.. points.Where(p => double.IsFinite(p.Value))];

    /// <summary>
    /// The 5xx series, or zeros on the request series' timestamps when there is none. A status
    /// code's series exists only once that code has been answered, so with requests flowing and no
    /// 5xx series the true rate is zero — not "insufficient data".
    /// </summary>
    public static List<TimeSeriesDataPoint> ErrorsOrZeros(
        List<TimeSeriesDataPoint> errors, List<TimeSeriesDataPoint> requests) =>
        errors.Count > 0 || requests.Count == 0
            ? errors
            : [.. requests.Select(p => new TimeSeriesDataPoint { Timestamp = p.Timestamp, Value = 0 })];

    /// <summary>A byte count for the volume table, in binary units: <c>512 MiB</c>, <c>1.5 GiB</c>.</summary>
    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = Math.Max(0, bytes);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} B" : $"{v:0.#} {units[i]}";
    }

    private static Dictionary<(string, string), double> ByClaim(IEnumerable<PrometheusMetricResult> results)
    {
        Dictionary<(string, string), double> map = [];
        foreach (PrometheusMetricResult r in results)
        {
            if (!r.Labels.TryGetValue("persistentvolumeclaim", out string? claim) || string.IsNullOrEmpty(claim))
                continue;
            r.Labels.TryGetValue("namespace", out string? ns);
            map[(ns ?? "", claim)] = r.Value;
        }
        return map;
    }

    /// <summary>A PromQL double-quoted string literal's body: backslashes and quotes escaped.</summary>
    private static string Literal(string value) =>
        new StringBuilder(value).Replace("\\", "\\\\").Replace("\"", "\\\"").ToString();
}
