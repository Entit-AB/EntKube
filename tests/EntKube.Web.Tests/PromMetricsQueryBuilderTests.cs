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
    public void FamilyMemberSelectors_AskForBucketsQuantilesAndTheSumCountPair_WithinTheScope()
        => PromMetricsQueryBuilder.FamilyMemberSelectors("rpc_latency", ["cust-a"], null, null).Should().Equal(
            "{__name__=\"rpc_latency_bucket\",k8s_namespace_name=~\"cust-a\"}",
            "{__name__=\"rpc_latency\",quantile!=\"\",k8s_namespace_name=~\"cust-a\"}",
            "{__name__=\"rpc_latency_sum\",k8s_namespace_name=~\"cust-a\"}",
            "{__name__=\"rpc_latency_count\",k8s_namespace_name=~\"cust-a\"}",
            "{__name__=\"rpc_latency_bucket\",namespace=~\"cust-a\"}",
            "{__name__=\"rpc_latency\",quantile!=\"\",namespace=~\"cust-a\"}",
            "{__name__=\"rpc_latency_sum\",namespace=~\"cust-a\"}",
            "{__name__=\"rpc_latency_count\",namespace=~\"cust-a\"}");

    [Fact]
    public void FamilyMemberSelectors_Unscoped_StillNamesTheFamily()
        => PromMetricsQueryBuilder.FamilyMemberSelectors("rpc_latency", null, null, null).Should().Equal(
            "{__name__=\"rpc_latency_bucket\"}",
            "{__name__=\"rpc_latency\",quantile!=\"\"}",
            "{__name__=\"rpc_latency_sum\"}",
            "{__name__=\"rpc_latency_count\"}");

    // ── Counter detection that needs Prometheus: decided with a lambda for the lookup ──

    /// <summary>One series a fake Prometheus holds: its name and labels.</summary>
    private static Dictionary<string, string> S(string name, string ns, params (string K, string V)[] extra)
    {
        Dictionary<string, string> labels = new() { ["__name__"] = name, ["namespace"] = ns };
        foreach ((string k, string v) in extra) labels[k] = v;
        return labels;
    }

    /// <summary>
    /// A label-values lookup for <c>__name__</c> that answers the way Prometheus does: the names
    /// of the <paramref name="series"/> matching ANY of the selectors it is handed, and every name
    /// when handed none. The answer depends on the selectors, so asking about the wrong family,
    /// or without a <c>__name__</c> matcher at all, gets the answer Prometheus would give to THAT
    /// question — a canned answer would hand back the right names to any question.
    /// </summary>
    private static Func<IReadOnlyList<string>, Task<KubernetesOperationResult<List<string>>>> Prometheus(
        List<IReadOnlyList<string>> asked, params Dictionary<string, string>[] series)
        => selectors =>
        {
            asked.Add(selectors);
            List<string> names = [.. series
                .Where(s => selectors.Count == 0 || selectors.Any(sel => Matches(sel, s)))
                .Select(s => s["__name__"]).Distinct().Order(StringComparer.Ordinal)];
            return Task.FromResult(KubernetesOperationResult<List<string>>.Success(names));
        };

    /// <summary>
    /// The three matchers the builder writes — <c>=</c>, <c>!=</c>, <c>=~</c> — with PromQL's
    /// semantics: a missing label is the empty string, and a regex must match the whole value.
    /// </summary>
    private static bool Matches(string selector, Dictionary<string, string> labels) =>
        System.Text.RegularExpressions.Regex.Matches(selector, "(\\w+)(=~|!=|=)\"((?:[^\"\\\\]|\\\\.)*)\"")
            .All(m =>
            {
                string actual = labels.GetValueOrDefault(m.Groups[1].Value, "");
                string want = m.Groups[3].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                return m.Groups[2].Value switch
                {
                    "=" => actual == want,
                    "!=" => actual != want,
                    _ => System.Text.RegularExpressions.Regex.IsMatch(actual, $"^(?:{want})$"),
                };
            });

    [Fact]
    public async Task IsCounterAsync_HistogramCount_IsRated_AndTheLookupAsksAboutItsFamilyInScope()
    {
        List<IReadOnlyList<string>> asked = [];
        bool counter = await PromMetricsService.IsCounterAsync(
            "http_server_request_duration_seconds_count", ["cust-a"], null, null,
            Prometheus(asked,
                S("http_server_request_duration_seconds_bucket", "cust-a", ("le", "0.5")),
                S("http_server_request_duration_seconds_sum", "cust-a"),
                S("http_server_request_duration_seconds_count", "cust-a")));

        counter.Should().BeTrue();
        // Which family, and in which scope: the lookup for X_count asks about X — not X_count, whose
        // "_bucket" no histogram has — and every selector names it, or each one would match every
        // series in the namespace.
        asked.Should().ContainSingle().Which.Should().Equal(
            PromMetricsQueryBuilder.FamilyMemberSelectors(
                "http_server_request_duration_seconds", ["cust-a"], null, null));
        asked[0].Should().OnlyContain(s => ScopedToCustA(s), "the check sees only what the explorer may see");
    }

    [Fact]
    public async Task IsCounterAsync_HistogramSum_IsRated_ByTheSameFamilyLookup()
    {
        List<IReadOnlyList<string>> asked = [];
        bool counter = await PromMetricsService.IsCounterAsync(
            "rpc_latency_sum", ["cust-a"], null, null,
            Prometheus(asked,
                S("rpc_latency_bucket", "cust-a", ("le", "1")),
                S("rpc_latency_sum", "cust-a"),
                S("rpc_latency_count", "cust-a")));

        counter.Should().BeTrue();
        asked.Should().ContainSingle().Which.Should().Equal(
            PromMetricsQueryBuilder.FamilyMemberSelectors("rpc_latency", ["cust-a"], null, null));
    }

    /// <summary>
    /// A summary without quantile objectives — the default in Python's prometheus_client,
    /// client_golang v1+ and prometheus-net — exposes only <c>_count</c> and <c>_sum</c>. Its
    /// <c>_count</c> is cumulative; charted as a gauge it is a line that only ever grows.
    /// </summary>
    [Theory]
    [InlineData("job_duration_seconds_count")]
    [InlineData("job_duration_seconds_sum")]
    public async Task IsCounterAsync_QuantilelessSummary_IsRated(string metric)
    {
        List<IReadOnlyList<string>> asked = [];
        bool counter = await PromMetricsService.IsCounterAsync(
            metric, ["cust-a"], null, null,
            Prometheus(asked,
                S("job_duration_seconds_sum", "cust-a"),
                S("job_duration_seconds_count", "cust-a")));

        counter.Should().BeTrue("a _sum beside a _count is a summary (or a histogram): both are counters");
        asked.Should().ContainSingle().Which.Should().Equal(
            PromMetricsQueryBuilder.FamilyMemberSelectors("job_duration_seconds", ["cust-a"], null, null));
    }

    [Fact]
    public async Task IsCounterAsync_SummaryWithQuantiles_IsRated()
        => (await PromMetricsService.IsCounterAsync(
                "rpc_wait_seconds_count", ["cust-a"], null, null,
                Prometheus([],
                    S("rpc_wait_seconds", "cust-a", ("quantile", "0.99")),
                    S("rpc_wait_seconds_count", "cust-a"))))
            .Should().BeTrue("a quantile series of the family proves a summary on its own");

    [Fact]
    public async Task IsCounterAsync_GaugeEndingInCount_WithNoSumBeside_IsNotRated()
    {
        List<IReadOnlyList<string>> asked = [];
        bool counter = await PromMetricsService.IsCounterAsync(
            "agroal_active_count", ["cust-a"], null, null,
            Prometheus(asked,
                // The gauge itself, which the family lookup always finds — it asks for _count —
                // and a real histogram in the same namespace, which a lookup without a __name__
                // matcher would also find.
                S("agroal_active_count", "cust-a"),
                S("http_server_request_duration_seconds_bucket", "cust-a", ("le", "0.5")),
                S("http_server_request_duration_seconds_sum", "cust-a"),
                S("http_server_request_duration_seconds_count", "cust-a")));

        counter.Should().BeFalse("no _bucket, no quantiles, no _sum: a gauge that happens to end in _count");
        asked.Should().ContainSingle().Which.Should().Equal(
            PromMetricsQueryBuilder.FamilyMemberSelectors("agroal_active", ["cust-a"], null, null));
    }

    /// <summary>
    /// The mirror of the gauge above: a gauge that ends in <c>_sum</c> has itself in scope, which
    /// the lookup always finds. Only the pair proves a family, so one side alone must not.
    /// </summary>
    [Fact]
    public async Task IsCounterAsync_GaugeEndingInSum_WithNoCountBeside_IsNotRated()
        => (await PromMetricsService.IsCounterAsync(
                "cache_entries_sum", ["cust-a"], null, null,
                Prometheus([], S("cache_entries_sum", "cust-a"))))
            .Should().BeFalse("no _bucket, no quantiles, no _count: a gauge that happens to end in _sum");

    /// <summary>
    /// The pair has to be in the explorer's scope: another customer's summary of the same name
    /// says nothing about this customer's gauge.
    /// </summary>
    [Fact]
    public async Task IsCounterAsync_SumBesideTheCountOnlyInAnotherNamespace_IsNotRated()
        => (await PromMetricsService.IsCounterAsync(
                "pool_count", ["cust-a"], null, null,
                Prometheus([],
                    S("pool_count", "cust-a"),
                    S("pool_sum", "cust-b"),
                    S("pool_count", "cust-b"))))
            .Should().BeFalse("cust-b's pool_sum is outside what the cust-a explorer may see");

    [Fact]
    public async Task IsCounterAsync_TotalAndBucket_NeedNoLookup()
    {
        List<IReadOnlyList<string>> asked = [];
        (await PromMetricsService.IsCounterAsync("x_total", null, null, null,
            Unreachable(asked))).Should().BeTrue();
        (await PromMetricsService.IsCounterAsync("x_bucket", null, null, null,
            Unreachable(asked))).Should().BeTrue();
        asked.Should().BeEmpty();
    }

    [Fact]
    public async Task IsCounterAsync_LookupFails_AnswersGauge()
        => (await PromMetricsService.IsCounterAsync("rpc_latency_count", null, null, null, Unreachable([])))
            .Should().BeFalse("a failed lookup answers what every name got before the check existed");

    private static Func<IReadOnlyList<string>, Task<KubernetesOperationResult<List<string>>>> Unreachable(
        List<IReadOnlyList<string>> asked)
        => selectors =>
        {
            asked.Add(selectors);
            return Task.FromResult(KubernetesOperationResult<List<string>>.Failure("unreachable"));
        };

    [Fact]
    public void RateWindow_IsAtLeastAMinute_AndScalesWithStep()
    {
        DateTime from = new(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        PromMetricsQueryBuilder.RateWindowSeconds(from, from.AddMinutes(5), 60).Should().BeGreaterThanOrEqualTo(60);
        PromMetricsQueryBuilder.RateWindowSeconds(from, from.AddHours(24), 60).Should().BeGreaterThan(60);
    }
}
