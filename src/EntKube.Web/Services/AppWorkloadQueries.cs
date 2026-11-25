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
/// Whether a running pod mounts the claim, from kube-state-metrics' pod spec — not from the
/// kubelet's volume stats. The kubelet reports usage only for volumes whose driver measures it
/// (CSI drivers do; hostPath-backed ones such as local-path do not), so "no stats" is not "not
/// mounted". An unmounted claim has a requested size and nothing else.
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

    /// <summary>
    /// Whether the kubelet reported any usage for the claim. A mounted claim without it sits on
    /// storage that does not measure itself — its usage is unknown, which is not zero.
    /// </summary>
    public bool UsageReported => UsedBytes is not null || CapacityBytes is not null || AvailableBytes is not null;
}

/// <summary>
/// One range query's answer with its failure kept: <paramref name="Error"/> is null when
/// Prometheus answered, and only then does an empty <paramref name="Series"/> mean "no series".
/// </summary>
public sealed record RangeOutcome(IReadOnlyList<PrometheusTimeSeries> Series, string? Error)
{
    public bool Succeeded => Error is null;

    /// <summary>The first series' points, every step kept; empty when there is no series.</summary>
    public List<TimeSeriesDataPoint> FirstSeries => Series.Count > 0 ? Series[0].DataPoints : [];

    public static RangeOutcome From(KubernetesOperationResult<List<PrometheusTimeSeries>> result) =>
        result.IsSuccess
            ? new RangeOutcome(result.Data ?? [], null)
            : new RangeOutcome([], string.IsNullOrWhiteSpace(result.Error) ? "The query failed." : result.Error);
}

/// <summary>What the app panel's HTTP section is showing. See <see cref="AppWorkloadQueries.HttpMetrics"/>.</summary>
public enum HttpMetricsState
{
    /// <summary>Prometheus answered with request series: chart them.</summary>
    Charted,

    /// <summary>Prometheus answered, and holds no HTTP series for these namespaces: the install advice.</summary>
    NoSeries,

    /// <summary>The request query failed: nothing is known, and nothing is advised.</summary>
    Failed,
}

/// <summary>The HTTP section's content, decided by <see cref="AppWorkloadQueries.HttpMetrics"/>.</summary>
/// <param name="Error">Why the section could not be loaded, for <see cref="HttpMetricsState.Failed"/>.</param>
/// <param name="ErrorsError">Why the 5xx chart alone could not be loaded, when the rest could.</param>
/// <param name="P95Error">Why the p95 chart alone could not be loaded, when the rest could.</param>
public sealed record HttpMetricsView(
    HttpMetricsState State,
    string? Error,
    List<TimeSeriesDataPoint> Rate,
    List<TimeSeriesDataPoint> Errors,
    List<TimeSeriesDataPoint> P95Ms,
    string? ErrorsError,
    string? P95Error);

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
/// <c>kube_persistentvolumeclaim_resource_requests_storage_bytes</c>,
/// <c>kube_pod_spec_volumes_persistentvolumeclaims_info</c>, <c>kube_pod_status_phase</c> —
/// kube-state-metrics 2.20.0, the version that chart bundles.</item>
/// <item><c>kubelet_volume_stats_{used,capacity,available}_bytes</c> — the kubelet's own
/// <c>/metrics</c>, used by the chart's KubePersistentVolumeFillingUp alerts. Present only for
/// volumes whose driver measures usage: on kind's local-path (hostPath) volumes there are none
/// at all, verified on a live cluster.</item>
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
    public const string PvcMounted = "pvcMounted";
    public const string HttpRate = "httpRate";
    public const string HttpErrors = "httpErrors";
    public const string HttpP95 = "httpP95";

    /// <summary>The volume queries: instant, per claim, for the table rather than a chart.</summary>
    public static readonly IReadOnlyList<string> PvcKeys = [PvcUsed, PvcCapacity, PvcAvailable, PvcRequested, PvcMounted];

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
            // Claims a Running pod mounts. kube-state-metrics keeps reporting the spec of a Pending
            // or finished pod, so the phase join is what makes this "mounted" and not "named".
            [PvcMounted]   = "max by (namespace, persistentvolumeclaim) ("
                           + $"kube_pod_spec_volumes_persistentvolumeclaims_info{nsOnly}"
                           + $" * on (namespace, pod) group_left () (kube_pod_status_phase{{{ns},phase=\"Running\"}} == 1))",

            [HttpRate]     = $"sum(rate(http_server_request_duration_seconds_count{nsOnly}[{W}]))",
            [HttpErrors]   = $"sum(rate(http_server_request_duration_seconds_count{{{ns},http_response_status_code=~\"5..\"}}[{W}]))",
            [HttpP95]      = $"histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{nsOnly}[{W}])))",
        };
    }

    /// <summary>
    /// Joins the five per-claim vectors into table rows, one per claim.
    ///
    /// <para>The claim set is the union: the kubelet knows only mounted claims whose storage
    /// measures itself, and kube-state-metrics knows every claim. A claim is mounted when a running
    /// pod mounts it, or when the kubelet reports it at all; one with neither is unmounted — shown
    /// with its requested size rather than dropped, or worse, shown as 0 bytes used.</para>
    /// </summary>
    public static List<PvcUsageRow> PvcRows(
        IReadOnlyList<PrometheusMetricResult> used,
        IReadOnlyList<PrometheusMetricResult> capacity,
        IReadOnlyList<PrometheusMetricResult> available,
        IReadOnlyList<PrometheusMetricResult> requested,
        IReadOnlyList<PrometheusMetricResult> mountedBy)
    {
        Dictionary<(string, string), double> u = ByClaim(used), c = ByClaim(capacity),
            a = ByClaim(available), r = ByClaim(requested), m = ByClaim(mountedBy);

        return [.. u.Keys.Concat(c.Keys).Concat(a.Keys).Concat(r.Keys).Concat(m.Keys)
            .Distinct()
            .OrderBy(k => k.Item1, StringComparer.Ordinal)
            .ThenBy(k => k.Item2, StringComparer.Ordinal)
            .Select(k =>
            {
                bool mounted = m.ContainsKey(k) || u.ContainsKey(k) || c.ContainsKey(k) || a.ContainsKey(k);
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
    /// The volume table from the five volume queries' outcomes, or a failure naming the ones that
    /// did not answer. All or nothing, because the rows are a join: without the mounted query a
    /// claim on storage that reports no kubelet stats reads "not mounted", without the requested
    /// sizes an unmounted claim reads "—", and without all of them there are "no claims". See
    /// <see cref="PrometheusService.ReadPvcUsageAsync"/>.
    /// </summary>
    public static KubernetesOperationResult<List<PvcUsageRow>> PvcUsage(
        IReadOnlyDictionary<string, InstantQueryOutcome> outcomes)
    {
        List<(string Key, string Error)> failed =
            [.. PvcKeys
                .Select(k => (Key: k, Error: outcomes.TryGetValue(k, out InstantQueryOutcome? o)
                    ? o.Error
                    : "the query was not sent"))
                .Where(f => f.Error is not null)
                .Select(f => (f.Key, f.Error!))];

        if (failed.Count > 0)
        {
            string which = string.Join(", ", failed.Select(f => PvcQueryNames[f.Key]));
            string why = string.Join("; ", failed.Select(f => f.Error).Distinct());
            return KubernetesOperationResult<List<PvcUsageRow>>.Failure(
                $"Prometheus did not answer the volume queries for {which} ({why}). "
                + "The table is not shown, because without them it would mislabel the claims.");
        }

        return KubernetesOperationResult<List<PvcUsageRow>>.Success(PvcRows(
            outcomes[PvcUsed].Results, outcomes[PvcCapacity].Results, outcomes[PvcAvailable].Results,
            outcomes[PvcRequested].Results, outcomes[PvcMounted].Results));
    }

    private static readonly IReadOnlyDictionary<string, string> PvcQueryNames = new Dictionary<string, string>
    {
        [PvcUsed] = "used space",
        [PvcCapacity] = "capacity",
        [PvcAvailable] = "free space",
        [PvcRequested] = "requested size",
        [PvcMounted] = "which claims are mounted",
    };

    /// <summary>
    /// The points a chart can draw, for <c>MultiSeriesChart</c>, which places points by their
    /// timestamp — so dropping a step leaves a straight join across it, not a shifted time axis.
    /// <c>histogram_quantile</c> answers NaN for a step with no requests, and a NaN in an SVG
    /// coordinate breaks the whole line rather than leaving a gap.
    ///
    /// <para>⚠️ Not for <c>MetricChart</c>: that one places points by list index, so a dropped
    /// step squeezed every later point leftwards and the p95 chart stopped lining up with the
    /// Requests and 5xx charts beside it. MetricChart draws non-finite steps as gaps itself; see
    /// <see cref="HttpMetrics"/>, which hands it the p95 series with every step.</para>
    /// </summary>
    public static List<TimeSeriesDataPoint> Drawable(IEnumerable<TimeSeriesDataPoint> points) =>
        [.. points.Where(p => double.IsFinite(p.Value))];

    /// <summary>
    /// The 5xx series, or zeros on the request series' timestamps when there is none. A status
    /// code's series exists only once that code has been answered, so with requests flowing and no
    /// 5xx series the true rate is zero — not "insufficient data". Only for a 5xx query that
    /// <b>answered</b>; see <see cref="HttpMetrics"/>.
    /// </summary>
    public static List<TimeSeriesDataPoint> ErrorsOrZeros(
        List<TimeSeriesDataPoint> errors, List<TimeSeriesDataPoint> requests) =>
        errors.Count > 0 || requests.Count == 0
            ? errors
            : [.. requests.Select(p => new TimeSeriesDataPoint { Timestamp = p.Timestamp, Value = 0 })];

    /// <summary>
    /// What the HTTP section shows, from the outcomes of its three range queries.
    ///
    /// <para>⚠️ The panel used to fold a failed range query into an empty list, so a failure and
    /// an absence were the same value by the time they reached this decision. A failed 5xx query
    /// beside a working request query then became <see cref="ErrorsOrZeros"/>' measured flat
    /// 0/s line — "no errors" from a query that never answered; and a failed request query
    /// became "no HTTP series", whose advice is to install Prometheus and re-apply the eBPF agent,
    /// on a cluster where both are installed and Prometheus merely did not answer.</para>
    ///
    /// <list type="bullet">
    /// <item>The request query failed: <see cref="HttpMetricsState.Failed"/> with its reason —
    /// whether there is HTTP traffic at all is unknown.</item>
    /// <item>It answered with no series: <see cref="HttpMetricsState.NoSeries"/>, the install
    /// advice, whatever the other two did.</item>
    /// <item>Otherwise charted. The 5xx series is zero-filled only when its query answered; when
    /// it failed it is empty and <see cref="HttpMetricsView.ErrorsError"/> says why. The p95
    /// series keeps every step, NaN included — MetricChart draws those as gaps.</item>
    /// </list>
    /// </summary>
    public static HttpMetricsView HttpMetrics(RangeOutcome rate, RangeOutcome errors, RangeOutcome p95)
    {
        if (!rate.Succeeded)
            return new HttpMetricsView(HttpMetricsState.Failed, rate.Error, [], [], [], null, null);

        List<TimeSeriesDataPoint> requests = rate.FirstSeries;
        if (rate.Series.Count == 0)
            return new HttpMetricsView(HttpMetricsState.NoSeries, null, [], [], [], null, null);

        return new HttpMetricsView(
            HttpMetricsState.Charted, null,
            requests,
            errors.Succeeded ? ErrorsOrZeros(errors.FirstSeries, requests) : [],
            p95.Succeeded ? p95.FirstSeries : [],
            errors.Error,
            p95.Error);
    }

    /// <summary>
    /// The panel's warning banner: which charts could not be loaded, grouped by reason —
    /// "CPU total, Memory total: Prometheus did not answer in time." — or null when all answered.
    /// It used to keep only the first failure's reason and name no chart, so the reader could
    /// not tell an empty chart that failed from one that is truly empty.
    /// </summary>
    public static string? FailedCharts(IEnumerable<(string Chart, RangeOutcome Outcome)> charts)
    {
        List<string> groups =
            [.. charts
                .Where(c => !c.Outcome.Succeeded)
                .GroupBy(c => c.Outcome.Error!, StringComparer.Ordinal)
                .Select(g => $"{string.Join(", ", g.Select(c => c.Chart))}: {g.Key}")];

        return groups.Count == 0 ? null : string.Join("; ", groups);
    }

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
