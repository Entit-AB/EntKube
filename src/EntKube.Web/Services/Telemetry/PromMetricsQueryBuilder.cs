using System.Text;
using EntKube.Telemetry;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Builds the PromQL for the metrics explorer now that app metrics live in Prometheus (apps/collectors
/// write there directly; EntKube only visualizes). Pure and side-effect free so the query construction —
/// the part that encodes conventions — is unit-tested without a live Prometheus.
///
/// <para><b>Two spellings of the same scope.</b> A series reaches Prometheus by one of two roads, and
/// each names the namespace, pod and service differently:</para>
/// <list type="bullet">
/// <item><b>OpenTelemetry</b> (OTLP → Prometheus, dots become underscores), as the log/trace ingest
/// already reads k8s attributes: <c>k8s_namespace_name</c>, <c>k8s_pod_name</c>,
/// <c>service_name</c>.</item>
/// <item><b>Scraped</b> — pods annotated <c>prometheus.io/scrape</c>, kube-state-metrics, the
/// kubelet: <c>namespace</c>, <c>pod</c>, and the <c>app_kubernetes_io_name</c> the annotation
/// scrape job copies from the pod's labels.</item>
/// </list>
/// <para>⚠️ The explorer used to filter on the OpenTelemetry spelling only, so in the customer portal —
/// where a namespace filter is always present, because it is the customer's boundary — no scraped
/// series ever matched. A scope is now one selector per spelling, joined with <c>or</c> inside the
/// aggregation. <c>or</c> keeps a series once when both selectors match it (same label set), and
/// <b>every</b> branch carries every scope matcher, so neither spelling widens what a customer
/// sees: each branch is the same scope, written in its own labels.</para>
///
/// <para>A metric is a counter (needing <c>rate()</c>) when its name ends in <c>_total</c> or is a
/// histogram's <c>_bucket</c> — see <see cref="IsCounter"/>. <c>_count</c> and <c>_sum</c> are
/// counters only when they belong to a histogram or summary, which the name alone cannot say:
/// plenty of gauges end in <c>_count</c>. <see cref="HistogramFamily"/>,
/// <see cref="FamilyMemberSelectors"/> and <see cref="ProvesCumulativeFamily"/> are how the caller
/// asks Prometheus — a <c>_bucket</c>, a <c>quantile</c>, or the <c>_sum</c>/<c>_count</c> pair
/// in scope.</para>
/// </summary>
public static class PromMetricsQueryBuilder
{
    public const string NamespaceLabel = "k8s_namespace_name";
    public const string PodLabel = "k8s_pod_name";
    public const string ServiceLabel = "service_name";

    /// <summary>The scraped spelling of <see cref="NamespaceLabel"/>.</summary>
    public const string ScrapedNamespaceLabel = "namespace";
    /// <summary>The scraped spelling of <see cref="PodLabel"/>.</summary>
    public const string ScrapedPodLabel = "pod";
    /// <summary>The scraped stand-in for <see cref="ServiceLabel"/>: the pod's app.kubernetes.io/name.</summary>
    public const string ScrapedServiceLabel = "app_kubernetes_io_name";

    /// <summary>
    /// Whether the name alone says counter: <c>_total</c>, or a histogram's <c>_bucket</c>. The
    /// same rule as the rollup backend's <see cref="MetricSeriesBuilder.IsCounter"/>, by
    /// delegation, so the two cannot drift apart.
    /// </summary>
    public static bool IsCounter(string metricName) => MetricSeriesBuilder.IsCounter(metricName);

    /// <summary>
    /// For a name ending in <c>_count</c> or <c>_sum</c>, the family it would belong to if it is a
    /// histogram's or summary's — <c>http_server_duration_seconds</c> for
    /// <c>http_server_duration_seconds_count</c>. Null for every other name.
    /// </summary>
    public static string? HistogramFamily(string metricName)
    {
        foreach (string suffix in new[] { "_count", "_sum" })
        {
            if (metricName.Length > suffix.Length && metricName.EndsWith(suffix, StringComparison.Ordinal))
                return metricName[..^suffix.Length];
        }
        return null;
    }

    /// <summary>
    /// Selectors for every series that could prove <paramref name="family"/> is a histogram or a
    /// summary, within the explorer's scope — so the check sees only what the explorer itself may
    /// see. Per scope spelling: the <c>_bucket</c> series, the bare name with a <c>quantile</c>,
    /// and the <c>_sum</c> and <c>_count</c> pair. One label-values lookup answers all of them;
    /// <see cref="ProvesCumulativeFamily"/> reads the names it returns.
    ///
    /// <para>⚠️ <b>Buckets and quantiles alone miss the commonest summary.</b> A summary with no
    /// quantile objectives — the default in Python's prometheus_client, client_golang v1+ and
    /// prometheus-net — exposes only <c>foo_count</c> and <c>foo_sum</c>. Asking for buckets or
    /// quantiles found neither, so its <c>_count</c> was summed as a gauge and charted as a line
    /// that only ever grows. The <c>_sum</c>/<c>_count</c> pair is what every histogram and every
    /// summary has in common, and what a gauge that merely ends in <c>_count</c>
    /// (<c>agroal_active_count</c>) does not.</para>
    /// </summary>
    public static IReadOnlyList<string> FamilyMemberSelectors(
        string family, IReadOnlyList<string>? namespaces, string? podPattern, string? service)
    {
        List<List<string>> scopes = ScopeMatchers(namespaces, podPattern, service);
        if (scopes.Count == 0) scopes = [[]];

        string f = Escape(family);
        List<string> selectors = [];
        foreach (List<string> scope in scopes)
        {
            selectors.Add(Wrap([$"__name__=\"{f}_bucket\"", .. scope]));
            selectors.Add(Wrap([$"__name__=\"{f}\"", "quantile!=\"\"", .. scope]));
            selectors.Add(Wrap([$"__name__=\"{f}_sum\"", .. scope]));
            selectors.Add(Wrap([$"__name__=\"{f}_count\"", .. scope]));
        }
        return selectors;
    }

    /// <summary>
    /// Whether the metric names a <see cref="FamilyMemberSelectors"/> lookup returned prove
    /// <paramref name="family"/> is a histogram or a summary — whose <c>_count</c> and <c>_sum</c>
    /// are both cumulative counters: a <c>_bucket</c> series (histogram), the bare name (only the
    /// <c>quantile</c> selector can return it: a summary with objectives), or <b>both</b>
    /// <c>_sum</c> and <c>_count</c> (either kind, including a quantile-less summary).
    ///
    /// <para>The names are read, not counted: the lookup asks for the family's own <c>_count</c>
    /// and <c>_sum</c>, so it always returns the very metric being charted, and "anything came
    /// back" would rate every gauge ending in <c>_count</c>. One of the pair is not enough — that
    /// one is the metric itself.</para>
    /// </summary>
    public static bool ProvesCumulativeFamily(string family, IReadOnlyCollection<string> names) =>
        names.Contains($"{family}_bucket")
        || names.Contains(family)
        || (names.Contains($"{family}_sum") && names.Contains($"{family}_count"));

    public static string BreakdownLabel(string dimension) => BreakdownLabels(dimension).Otel;

    /// <summary>The label a breakdown groups by, in both spellings.</summary>
    public static (string Otel, string Scraped) BreakdownLabels(string dimension) => dimension switch
    {
        "pod" => (PodLabel, ScrapedPodLabel),
        "service" => (ServiceLabel, ScrapedServiceLabel),
        "namespace" => (NamespaceLabel, ScrapedNamespaceLabel),
        _ => (PodLabel, ScrapedPodLabel),
    };

    /// <summary>
    /// The explorer's namespace/pod/service scope as one selector per label spelling —
    /// <c>{k8s_namespace_name=~"a|b",k8s_pod_name=~"^(w).*"}</c> and
    /// <c>{namespace=~"a|b",pod=~"^(w).*"}</c>. Empty when nothing is scoped.
    /// </summary>
    public static IReadOnlyList<string> BuildSelectors(IReadOnlyList<string>? namespaces, string? podPattern, string? service)
        => [.. ScopeMatchers(namespaces, podPattern, service).Select(Wrap)];

    /// <summary>
    /// Total series query: <c>sum(rate(m{a}[Xs]) or rate(m{b}[Xs]))</c> for counters,
    /// <c>sum(m{a} or m{b})</c> for gauges, <c>sum(m)</c> unscoped.
    /// </summary>
    public static string BuildSeriesQuery(string metric, IReadOnlyList<string> selectors, int rateWindowSeconds, bool counter)
        => $"sum({Union(metric, selectors, rateWindowSeconds, counter)})";

    /// <summary>
    /// Broken-down query: <c>topk(N, sum by (otel, scraped) (…))</c>. Grouped by both spellings of
    /// the label, so a series of either kind keeps its name; <see cref="CollapseBreakdown"/> then
    /// reads whichever one it has.
    /// </summary>
    public static string BuildSeriesByQuery(
        string metric, IReadOnlyList<string> selectors, string dimension, int rateWindowSeconds, int maxSeries, bool counter)
    {
        (string otel, string scraped) = BreakdownLabels(dimension);
        return $"topk({maxSeries}, sum by ({otel}, {scraped}) ({Union(metric, selectors, rateWindowSeconds, counter)}))";
    }

    /// <summary>
    /// The breakdown's series named by whichever spelling of the label they carry, OpenTelemetry's
    /// first. Two series that resolve to the same name — the same pod reported by both roads with
    /// different extra labels — are summed point by point rather than drawn as two lines of one
    /// name.
    /// </summary>
    public static List<MetricSeriesGroup> CollapseBreakdown(IEnumerable<PrometheusTimeSeries> series, string dimension)
    {
        (string otel, string scraped) = BreakdownLabels(dimension);
        List<MetricSeriesGroup> groups = [];

        foreach (IGrouping<string, PrometheusTimeSeries> byName in series.GroupBy(s => NameOf(s, otel, scraped)))
        {
            List<TimeSeriesDataPoint> points = byName.Count() == 1
                ? byName.First().DataPoints
                : [.. byName.SelectMany(s => s.DataPoints)
                    .GroupBy(p => p.Timestamp)
                    .OrderBy(g => g.Key)
                    .Select(g => new TimeSeriesDataPoint { Timestamp = g.Key, Value = g.Sum(p => p.Value) })];
            groups.Add(new MetricSeriesGroup(byName.Key, points));
        }
        return groups;
    }

    /// <summary>rate() window in seconds: a few scrape intervals worth, scaled to the query step, min 60s.</summary>
    public static int RateWindowSeconds(DateTime from, DateTime to, int buckets)
    {
        double stepSecs = Math.Max(15, (to - from).TotalSeconds / Math.Max(1, buckets));
        return (int)Math.Max(60, stepSecs * 4);
    }

    private static string NameOf(PrometheusTimeSeries s, string otel, string scraped)
        => s.Labels.TryGetValue(otel, out string? a) && !string.IsNullOrEmpty(a) ? a
         : s.Labels.TryGetValue(scraped, out string? b) && !string.IsNullOrEmpty(b) ? b
         : "(unlabeled)";

    private static string Union(string metric, IReadOnlyList<string> selectors, int rateWindowSeconds, bool counter)
    {
        string One(string selector) => counter
            ? $"rate({metric}{selector}[{rateWindowSeconds}s])"
            : $"{metric}{selector}";

        return selectors.Count == 0 ? One("") : string.Join(" or ", selectors.Select(One));
    }

    /// <summary>
    /// The scope's matchers, one list per spelling, each list carrying every matcher the scope
    /// asks for. Empty when nothing is scoped.
    /// </summary>
    private static List<List<string>> ScopeMatchers(IReadOnlyList<string>? namespaces, string? podPattern, string? service)
    {
        List<string> otel = [], scraped = [];

        if (namespaces is { Count: > 0 })
        {
            string ns = Escape(string.Join("|", namespaces));
            otel.Add($"{NamespaceLabel}=~\"{ns}\"");
            scraped.Add($"{ScrapedNamespaceLabel}=~\"{ns}\"");
        }
        if (!string.IsNullOrEmpty(podPattern))
        {
            // Pod pattern is an anchored-start regex of workload prefixes ("^(w1|w2)-"); pods are
            // "<workload>-<hash>", so match the prefix. PromQL RE2 is whole-string, hence the trailing .*
            string pod = Escape(podPattern) + ".*";
            otel.Add($"{PodLabel}=~\"{pod}\"");
            scraped.Add($"{ScrapedPodLabel}=~\"{pod}\"");
        }
        if (!string.IsNullOrEmpty(service))
        {
            string svc = Escape(service);
            otel.Add($"{ServiceLabel}=\"{svc}\"");
            scraped.Add($"{ScrapedServiceLabel}=\"{svc}\"");
        }

        return otel.Count == 0 ? [] : [otel, scraped];
    }

    private static string Wrap(IEnumerable<string> matchers) => "{" + string.Join(",", matchers) + "}";

    private static string Escape(string value) =>
        new StringBuilder(value).Replace("\\", "\\\\").Replace("\"", "\\\"").ToString();
}
