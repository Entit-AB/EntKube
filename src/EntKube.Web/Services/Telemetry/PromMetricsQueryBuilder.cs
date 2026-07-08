using System.Text;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Builds the PromQL for the metrics explorer now that app metrics live in Prometheus (apps/collectors
/// write there directly; EntKube only visualizes). Pure and side-effect free so the query construction —
/// the part that encodes conventions — is unit-tested without a live Prometheus.
///
/// Label conventions follow the OpenTelemetry → Prometheus remote-write mapping (dots become underscores),
/// matching how the log/trace ingest already reads k8s attributes:
///   namespace → <c>k8s_namespace_name</c>, pod → <c>k8s_pod_name</c>, service → <c>service_name</c>.
/// A metric is treated as a counter (needing <c>rate()</c>) when its name ends in <c>_total</c>, the
/// Prometheus counter convention; everything else is summed as a gauge. Adjust here if a deployment uses
/// different conventions.
/// </summary>
public static class PromMetricsQueryBuilder
{
    public const string NamespaceLabel = "k8s_namespace_name";
    public const string PodLabel = "k8s_pod_name";
    public const string ServiceLabel = "service_name";

    public static bool IsCounter(string metricName) => metricName.EndsWith("_total", StringComparison.Ordinal);

    public static string BreakdownLabel(string dimension) => dimension switch
    {
        "pod" => PodLabel,
        "service" => ServiceLabel,
        "namespace" => NamespaceLabel,
        _ => PodLabel,
    };

    /// <summary>
    /// Builds a PromQL label selector <c>{k8s_namespace_name=~"a|b",k8s_pod_name=~"^(w).*",service_name="svc"}</c>
    /// from the explorer's namespace/pod/service scope. Returns "" when nothing is scoped.
    /// </summary>
    public static string BuildSelector(IReadOnlyList<string>? namespaces, string? podPattern, string? service)
    {
        var matchers = new List<string>();
        if (namespaces is { Count: > 0 })
            matchers.Add($"{NamespaceLabel}=~\"{Escape(string.Join("|", namespaces))}\"");
        if (!string.IsNullOrEmpty(podPattern))
            // Pod pattern is an anchored-start regex of workload prefixes ("^(w1|w2)-"); pods are
            // "<workload>-<hash>", so match the prefix. PromQL RE2 is whole-string, hence the trailing .*
            matchers.Add($"{PodLabel}=~\"{Escape(podPattern)}.*\"");
        if (!string.IsNullOrEmpty(service))
            matchers.Add($"{ServiceLabel}=\"{Escape(service)}\"");
        return matchers.Count == 0 ? "" : "{" + string.Join(",", matchers) + "}";
    }

    /// <summary>Total series query: <c>sum(rate(m{sel}[Xs]))</c> for counters, <c>sum(m{sel})</c> for gauges.</summary>
    public static string BuildSeriesQuery(string metric, string selector, int rateWindowSeconds)
        => IsCounter(metric)
            ? $"sum(rate({metric}{selector}[{rateWindowSeconds}s]))"
            : $"sum({metric}{selector})";

    /// <summary>Broken-down query: <c>topk(N, sum by (label)(rate(m{sel}[Xs])))</c> / gauge variant.</summary>
    public static string BuildSeriesByQuery(string metric, string selector, string breakdownLabel, int rateWindowSeconds, int maxSeries)
    {
        string inner = IsCounter(metric)
            ? $"rate({metric}{selector}[{rateWindowSeconds}s])"
            : $"{metric}{selector}";
        return $"topk({maxSeries}, sum by ({breakdownLabel}) ({inner}))";
    }

    /// <summary>rate() window in seconds: a few scrape intervals worth, scaled to the query step, min 60s.</summary>
    public static int RateWindowSeconds(DateTime from, DateTime to, int buckets)
    {
        double stepSecs = Math.Max(15, (to - from).TotalSeconds / Math.Max(1, buckets));
        return (int)Math.Max(60, stepSecs * 4);
    }

    private static string Escape(string value) =>
        new StringBuilder(value).Replace("\\", "\\\\").Replace("\"", "\\\"").ToString();
}
