using EntKube.Telemetry;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Metrics now come from Prometheus (apps write there directly); <see cref="PromMetricsQueryBuilder"/>
/// encodes the label conventions and counter/gauge handling. This pins that PromQL construction —
/// the part that would otherwise only be checkable against a live Prometheus.
///
/// <para>⚠️ <b>Two spellings, one scope.</b> The explorer used to filter only on the OpenTelemetry
/// labels (<c>k8s_namespace_name</c>, <c>k8s_pod_name</c>), so series from annotated pods — which
/// carry <c>namespace</c>/<c>pod</c> — never matched in the customer portal, where a namespace filter
/// is always present. Each test of the fallback is paired with the tenant-scoping half: every branch
/// must carry every matcher, or the second spelling would widen what a customer sees.</para>
/// </summary>
public sealed class PromMetricsQueryBuilderTests
{
    [Theory]
    [InlineData("http_server_requests_total", true)]
    [InlineData("process_cpu_seconds_total", true)]
    [InlineData("http_server_request_duration_seconds_bucket", true)]
    [InlineData("jvm_memory_used_bytes", false)]
    [InlineData("queue_depth", false)]
    // The name alone cannot say: a histogram's _count is a counter, agroal_active_count is a gauge.
    [InlineData("http_server_request_duration_seconds_count", false)]
    [InlineData("agroal_active_count", false)]
    public void IsCounter_DecidesWhatTheNameAloneCanDecide(string metric, bool expected)
        => PromMetricsQueryBuilder.IsCounter(metric).Should().Be(expected);

    /// <summary>The rollup backend and the PromQL one must rate the same names, or a graph changes when routing flips.</summary>
    [Theory]
    [InlineData("a_total")]
    [InlineData("a_bucket")]
    [InlineData("a_count")]
    [InlineData("a_sum")]
    [InlineData("a_bytes")]
    public void IsCounter_AgreesWithTheRollupBackend(string metric)
        => PromMetricsQueryBuilder.IsCounter(metric).Should().Be(MetricSeriesBuilder.IsCounter(metric));

    [Theory]
    [InlineData("http_server_request_duration_seconds_count", "http_server_request_duration_seconds")]
    [InlineData("rpc_latency_sum", "rpc_latency")]
    [InlineData("requests_total", null)]
    [InlineData("_count", null)]
    public void HistogramFamily_StripsCountAndSum(string metric, string? family)
        => PromMetricsQueryBuilder.HistogramFamily(metric).Should().Be(family);

    [Fact]
    public void BuildSelectors_EmptyScope_IsNoSelectorAtAll()
        => PromMetricsQueryBuilder.BuildSelectors(null, null, null).Should().BeEmpty();

    [Fact]
    public void BuildSelectors_OneSelectorPerSpelling_EachWithTheWholeScope()
    {
        IReadOnlyList<string> sel = PromMetricsQueryBuilder.BuildSelectors(["prod", "staging"], "^(api|worker)-", "checkout");

        sel.Should().Equal(
            "{k8s_namespace_name=~\"prod|staging\",k8s_pod_name=~\"^(api|worker)-.*\",service_name=\"checkout\"}",
            "{namespace=~\"prod|staging\",pod=~\"^(api|worker)-.*\",app_kubernetes_io_name=\"checkout\"}");
    }

    /// <summary>
    /// The tenant half: in the portal the namespace list IS the customer's boundary, so no branch
    /// of the union may exist without a namespace matcher.
    /// </summary>
    [Fact]
    public void BuildSelectors_EveryBranchIsNamespaceScoped()
    {
        IReadOnlyList<string> sel = PromMetricsQueryBuilder.BuildSelectors(["cust-a"], null, null);

        sel.Should().HaveCount(2);
        sel.Should().OnlyContain(s => ScopedToCustA(s));
        sel.Should().Equal("{k8s_namespace_name=~\"cust-a\"}", "{namespace=~\"cust-a\"}");
    }

    /// <summary>Whether a selector carries a namespace matcher for cust-a, in either spelling.</summary>
    private static bool ScopedToCustA(string selector) =>
        System.Text.RegularExpressions.Regex.IsMatch(selector, "[{,](k8s_namespace_name|namespace)=~\"cust-a\"[,}]");

    [Fact]
    public void BuildSeriesQuery_Scoped_UnionsBothSpellingsInsideTheSum()
        => PromMetricsQueryBuilder.BuildSeriesQuery(
                "app_jobs_total", PromMetricsQueryBuilder.BuildSelectors(["cust-a"], null, null), 300, counter: true)
            .Should().Be("sum(rate(app_jobs_total{k8s_namespace_name=~\"cust-a\"}[300s]) or rate(app_jobs_total{namespace=~\"cust-a\"}[300s]))");

    [Fact]
    public void BuildSeriesQuery_Gauge_SumsRaw()
        => PromMetricsQueryBuilder.BuildSeriesQuery(
                "queue_depth", PromMetricsQueryBuilder.BuildSelectors(["cust-a"], null, null), 300, counter: false)
            .Should().Be("sum(queue_depth{k8s_namespace_name=~\"cust-a\"} or queue_depth{namespace=~\"cust-a\"})");

    [Fact]
    public void BuildSeriesQuery_Unscoped_HasNoSelector()
        => PromMetricsQueryBuilder.BuildSeriesQuery("queue_depth", [], 300, counter: false)
            .Should().Be("sum(queue_depth)");

    [Fact]
    public void BuildSeriesByQuery_GroupsByBothSpellingsOfTheLabel()
        => PromMetricsQueryBuilder.BuildSeriesByQuery(
                "http_requests_total", PromMetricsQueryBuilder.BuildSelectors(null, null, "x"), "pod", 300, 24, counter: true)
            .Should().Be("topk(24, sum by (k8s_pod_name, pod) (rate(http_requests_total{service_name=\"x\"}[300s])"
                         + " or rate(http_requests_total{app_kubernetes_io_name=\"x\"}[300s])))");

    [Theory]
    [InlineData("pod", "k8s_pod_name", "pod")]
    [InlineData("service", "service_name", "app_kubernetes_io_name")]
    [InlineData("namespace", "k8s_namespace_name", "namespace")]
    [InlineData("weird", "k8s_pod_name", "pod")]
    public void BreakdownLabels_MapsDimension(string dim, string otel, string scraped)
    {
        PromMetricsQueryBuilder.BreakdownLabels(dim).Should().Be((otel, scraped));
        PromMetricsQueryBuilder.BreakdownLabel(dim).Should().Be(otel);
    }

    private static PrometheusTimeSeries Series(Dictionary<string, string> labels, params (int Min, double V)[] points)
    {
        DateTime t0 = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        return new PrometheusTimeSeries
        {
            Labels = labels,
            DataPoints = [.. points.Select(p => new TimeSeriesDataPoint { Timestamp = t0.AddMinutes(p.Min), Value = p.V })],
        };
    }

    [Fact]
    public void CollapseBreakdown_NamesEachSeriesByWhicheverSpellingItCarries()
    {
        List<MetricSeriesGroup> groups = PromMetricsQueryBuilder.CollapseBreakdown(
        [
            Series(new() { ["k8s_pod_name"] = "otel-pod" }, (0, 1)),
            Series(new() { ["pod"] = "scraped-pod" }, (0, 2)),
            Series(new(), (0, 3)),
        ], "pod");

        groups.Select(g => g.Name).Should().Equal("otel-pod", "scraped-pod", "(unlabeled)");
    }

    [Fact]
    public void CollapseBreakdown_OnePodReportedByBothRoads_IsOneLine_Summed()
    {
        List<MetricSeriesGroup> groups = PromMetricsQueryBuilder.CollapseBreakdown(
        [
            Series(new() { ["k8s_pod_name"] = "api-1" }, (0, 1), (1, 2)),
            Series(new() { ["pod"] = "api-1" }, (0, 10), (1, 20)),
        ], "pod");

        groups.Should().ContainSingle();
        groups[0].Points.Select(p => p.Value).Should().Equal(11, 22);
    }

    [Fact]
    public void FamilyMemberSelectors_AskForBucketsOrQuantiles_WithinTheScope()
        => PromMetricsQueryBuilder.FamilyMemberSelectors("rpc_latency", ["cust-a"], null, null).Should().Equal(
            "{__name__=\"rpc_latency_bucket\",k8s_namespace_name=~\"cust-a\"}",
            "{__name__=\"rpc_latency\",quantile!=\"\",k8s_namespace_name=~\"cust-a\"}",
            "{__name__=\"rpc_latency_bucket\",namespace=~\"cust-a\"}",
            "{__name__=\"rpc_latency\",quantile!=\"\",namespace=~\"cust-a\"}");

    [Fact]
    public void FamilyMemberSelectors_Unscoped_StillNamesTheFamily()
        => PromMetricsQueryBuilder.FamilyMemberSelectors("rpc_latency", null, null, null).Should().Equal(
            "{__name__=\"rpc_latency_bucket\"}",
            "{__name__=\"rpc_latency\",quantile!=\"\"}");

    // ── Counter detection that needs Prometheus: decided with a lambda for the lookup ──

    private static Func<IReadOnlyList<string>, Task<KubernetesOperationResult<List<string>>>> Lookup(
        List<IReadOnlyList<string>> asked, KubernetesOperationResult<List<string>> answer)
        => selectors => { asked.Add(selectors); return Task.FromResult(answer); };

    [Fact]
    public async Task IsCounterAsync_HistogramCount_IsRated()
    {
        List<IReadOnlyList<string>> asked = [];
        bool counter = await PromMetricsService.IsCounterAsync(
            "http_server_request_duration_seconds_count", ["cust-a"], null, null,
            Lookup(asked, KubernetesOperationResult<List<string>>.Success(["http_server_request_duration_seconds_bucket"])));

        counter.Should().BeTrue();
        asked.Should().ContainSingle().Which.Should().OnlyContain(
            s => ScopedToCustA(s), "the check sees only what the explorer may see");
    }

    [Fact]
    public async Task IsCounterAsync_GaugeEndingInCount_IsNotRated()
        => (await PromMetricsService.IsCounterAsync(
                "agroal_active_count", null, null, null,
                Lookup([], KubernetesOperationResult<List<string>>.Success([]))))
            .Should().BeFalse("no _bucket and no quantiles: a gauge that happens to end in _count");

    [Fact]
    public async Task IsCounterAsync_TotalAndBucket_NeedNoLookup()
    {
        List<IReadOnlyList<string>> asked = [];
        (await PromMetricsService.IsCounterAsync("x_total", null, null, null,
            Lookup(asked, KubernetesOperationResult<List<string>>.Failure("unreachable")))).Should().BeTrue();
        (await PromMetricsService.IsCounterAsync("x_bucket", null, null, null,
            Lookup(asked, KubernetesOperationResult<List<string>>.Failure("unreachable")))).Should().BeTrue();
        asked.Should().BeEmpty();
    }

    [Fact]
    public void RateWindow_IsAtLeastAMinute_AndScalesWithStep()
    {
        DateTime from = new(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        PromMetricsQueryBuilder.RateWindowSeconds(from, from.AddMinutes(5), 60).Should().BeGreaterThanOrEqualTo(60);
        PromMetricsQueryBuilder.RateWindowSeconds(from, from.AddHours(24), 60).Should().BeGreaterThan(60);
    }
}
