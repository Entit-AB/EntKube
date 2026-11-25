using System.Globalization;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The PromQL behind every app's basic metrics. A misspelt metric or label is not a PromQL error —
/// it is an empty vector, and an empty chart looks exactly like an idle app — so the names are
/// pinned here exactly, against the sources they were checked in: kube-prometheus-stack 93.9.0
/// (cAdvisor and kubelet series, its recording rules' <c>image!=""</c>), kube-state-metrics 2.20.0
/// (the version that chart bundles) and OBI v1.0.0 (<c>http.server.request.duration</c> translated
/// with unit suffixes, <c>http_response_status_code</c>).
/// </summary>
public sealed class AppWorkloadQueriesTests
{
    private const string Ns = "billing|billing\\-jobs";
    private static readonly IReadOnlyDictionary<string, string> Q = AppWorkloadQueries.Build("billing|billing-jobs");

    [Fact]
    public void Cpu_RatesTheCadvisorCounter_OverTheStepSizedWindow()
    {
        Q[AppWorkloadQueries.Cpu].Should().Be(
            "sum(rate(container_cpu_usage_seconds_total{namespace=~\"billing|billing-jobs\",container!=\"\",image!=\"\"}[$__rate]))");
        Q[AppWorkloadQueries.CpuByPod].Should().Be(
            "sum by (pod) (rate(container_cpu_usage_seconds_total{namespace=~\"billing|billing-jobs\",container!=\"\",image!=\"\"}[$__rate]))");
    }

    [Fact]
    public void Memory_IsTheWorkingSet_WithTheSameContainerFilters()
    {
        Q[AppWorkloadQueries.Memory].Should().Be(
            "sum(container_memory_working_set_bytes{namespace=~\"billing|billing-jobs\",container!=\"\",image!=\"\"})");
        Q[AppWorkloadQueries.MemoryByPod].Should().Be(
            "sum by (pod) (container_memory_working_set_bytes{namespace=~\"billing|billing-jobs\",container!=\"\",image!=\"\"})");
    }

    [Fact]
    public void Restarts_AreAnIncreaseOfTheKsmCounter_NotItsLifetimeTotal()
        => Q[AppWorkloadQueries.Restarts].Should().Be(
            "sum(increase(kube_pod_container_status_restarts_total{namespace=~\"billing|billing-jobs\"}[$__rate]))");

    /// <summary>
    /// <c>max</c> per claim, not <c>sum</c>: a ReadWriteMany volume is reported by the kubelet of
    /// every node that mounts it, and a sum would count it once per node.
    /// </summary>
    [Theory]
    [InlineData(AppWorkloadQueries.PvcUsed, "kubelet_volume_stats_used_bytes")]
    [InlineData(AppWorkloadQueries.PvcCapacity, "kubelet_volume_stats_capacity_bytes")]
    [InlineData(AppWorkloadQueries.PvcAvailable, "kubelet_volume_stats_available_bytes")]
    [InlineData(AppWorkloadQueries.PvcRequested, "kube_persistentvolumeclaim_resource_requests_storage_bytes")]
    public void VolumeQueries_TakeTheMaxPerClaim(string key, string metric)
        => Q[key].Should().Be(
            $"max by (namespace, persistentvolumeclaim) ({metric}{{namespace=~\"billing|billing-jobs\"}})");

    /// <summary>
    /// Mounted is read from the pod spec, joined to Running pods: kube-state-metrics keeps a
    /// Pending or finished pod's spec, and the kubelet's volume stats exist only for storage that
    /// measures itself. Run as written against kube-prometheus-stack 93.9.0 on kind, it named the
    /// mounted claim and not the unmounted one.
    /// </summary>
    [Fact]
    public void Mounted_IsThePodSpecOfRunningPods()
        => Q[AppWorkloadQueries.PvcMounted].Should().Be(
            "max by (namespace, persistentvolumeclaim) (kube_pod_spec_volumes_persistentvolumeclaims_info{namespace=~\"billing|billing-jobs\"}"
            + " * on (namespace, pod) group_left () (kube_pod_status_phase{namespace=~\"billing|billing-jobs\",phase=\"Running\"} == 1))");

    [Fact]
    public void Http_ComesFromObisDurationHistogram()
    {
        Q[AppWorkloadQueries.HttpRate].Should().Be(
            "sum(rate(http_server_request_duration_seconds_count{namespace=~\"billing|billing-jobs\"}[$__rate]))");
        Q[AppWorkloadQueries.HttpErrors].Should().Be(
            "sum(rate(http_server_request_duration_seconds_count{namespace=~\"billing|billing-jobs\",http_response_status_code=~\"5..\"}[$__rate]))");
        Q[AppWorkloadQueries.HttpP95].Should().Be(
            "histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{namespace=~\"billing|billing-jobs\"}[$__rate])))");
    }

    /// <summary>
    /// Every range query carries the token, so <c>GetMetricRangeAsync</c> sizes the window from the
    /// step; a fixed <c>[5m]</c> would sample one five-minute window per fifteen-minute step on a
    /// 24-hour chart.
    /// </summary>
    [Fact]
    public void EveryRateCarriesTheWindowToken_AndNoFixedWindow()
    {
        foreach (string key in new[] { AppWorkloadQueries.Cpu, AppWorkloadQueries.CpuByPod, AppWorkloadQueries.Restarts,
                     AppWorkloadQueries.HttpRate, AppWorkloadQueries.HttpErrors, AppWorkloadQueries.HttpP95 })
        {
            Q[key].Should().Contain("[$__rate]", key);
            Q[key].Should().NotContain("[5m]", key);
        }
    }

    /// <summary>
    /// <c>namespace=~""</c> is not "no namespaces": it matches every series without a namespace
    /// label, which for these queries is an empty vector — a successful zero.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Build_RefusesAnEmptyNamespaceMatch(string nsRegex)
    {
        Action act = () => AppWorkloadQueries.Build(nsRegex);
        act.Should().Throw<ArgumentException>().WithMessage("*No namespace*");
    }

    /// <summary>The alternation arrives regex-escaped; inside a PromQL string a backslash must be doubled.</summary>
    [Fact]
    public void Build_EscapesBackslashesForThePromqlString()
        => AppWorkloadQueries.Build(Ns)[AppWorkloadQueries.Restarts].Should().Contain(
            "namespace=~\"billing|billing\\\\-jobs\"");

    // ── The volume table ─────────────────────────────────────────

    private static PrometheusMetricResult Claim(string ns, string claim, double value) => new()
    {
        Labels = new() { ["namespace"] = ns, ["persistentvolumeclaim"] = claim },
        Value = value,
    };

    [Fact]
    public void PvcRows_JoinsTheFourVectorsPerClaim()
    {
        List<PvcUsageRow> rows = AppWorkloadQueries.PvcRows(
            used: [Claim("billing", "data", 25)],
            capacity: [Claim("billing", "data", 100)],
            available: [Claim("billing", "data", 75)],
            requested: [Claim("billing", "data", 100)],
            mountedBy: [Claim("billing", "data", 1)]);

        rows.Should().ContainSingle();
        PvcUsageRow r = rows[0];
        r.Should().BeEquivalentTo(new PvcUsageRow("billing", "data", 25, 100, 75, 100, Mounted: true));
        r.UsedPercent.Should().Be(25);
    }

    /// <summary>
    /// A claim no running pod mounts and the kubelet does not report is unmounted: it keeps its
    /// requested size, and nothing about it is 0.
    /// </summary>
    [Fact]
    public void PvcRows_AnUnmountedClaim_KeepsItsRequestedSize_AndNoUsage()
    {
        List<PvcUsageRow> rows = AppWorkloadQueries.PvcRows(
            used: [Claim("billing", "data", 25)],
            capacity: [Claim("billing", "data", 100)],
            available: [Claim("billing", "data", 75)],
            requested: [Claim("billing", "data", 100), Claim("billing", "archive", 500)],
            mountedBy: [Claim("billing", "data", 1)]);

        PvcUsageRow archive = rows.Single(r => r.Claim == "archive");
        archive.Mounted.Should().BeFalse();
        archive.RequestedBytes.Should().Be(500);
        archive.UsedBytes.Should().BeNull("an unmounted claim has no usage, which is not zero usage");
        archive.UsedPercent.Should().BeNull();

        rows.Single(r => r.Claim == "data").Mounted.Should().BeTrue();
    }

    /// <summary>
    /// Seen on a live cluster: kind's local-path volumes are hostPath, and the kubelet publishes
    /// no volume stats for them at all. A claim a running pod mounts is mounted whatever the
    /// kubelet says; when it was inferred from the stats, every such claim read "not mounted".
    /// </summary>
    [Fact]
    public void PvcRows_AMountedClaimWithoutVolumeStats_IsMounted_WithUsageUnknown()
    {
        List<PvcUsageRow> rows = AppWorkloadQueries.PvcRows(
            used: [], capacity: [], available: [],
            requested: [Claim("shop", "shop-data", 1024), Claim("shop", "shop-unmounted", 2048)],
            mountedBy: [Claim("shop", "shop-data", 1)]);

        PvcUsageRow data = rows.Single(r => r.Claim == "shop-data");
        data.Mounted.Should().BeTrue();
        data.UsageReported.Should().BeFalse();
        data.UsedBytes.Should().BeNull("unknown usage is not zero usage");

        rows.Single(r => r.Claim == "shop-unmounted").Mounted.Should().BeFalse();
    }

    /// <summary>The stats alone still prove a mount, should the kube-state-metrics series be missing.</summary>
    [Fact]
    public void PvcRows_VolumeStatsAlone_MeanMounted()
        => AppWorkloadQueries.PvcRows([Claim("a", "data", 1)], [], [], [], [])
            .Single().Should().Match<PvcUsageRow>(r => r.Mounted && r.UsageReported);

    [Fact]
    public void PvcRows_SameClaimNameInTwoNamespaces_IsTwoRows()
    {
        List<PvcUsageRow> rows = AppWorkloadQueries.PvcRows(
            [Claim("a", "data", 1), Claim("b", "data", 2)], [], [], [], []);

        rows.Select(r => (r.Namespace, r.Claim)).Should().Equal(("a", "data"), ("b", "data"));
    }

    // ── Chart hygiene ────────────────────────────────────────────

    [Fact]
    public void Drawable_DropsTheNaNStepsHistogramQuantileReturns()
    {
        DateTime t = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        List<TimeSeriesDataPoint> points =
        [
            new() { Timestamp = t, Value = 0.12 },
            new() { Timestamp = t.AddMinutes(1), Value = double.NaN },
            new() { Timestamp = t.AddMinutes(2), Value = double.PositiveInfinity },
            new() { Timestamp = t.AddMinutes(3), Value = 0.2 },
        ];

        AppWorkloadQueries.Drawable(points).Select(p => p.Value).Should().Equal(0.12, 0.2);
    }

    [Fact]
    public void ErrorsOrZeros_WithRequestsAndNo5xxSeries_IsZero_NotMissing()
    {
        DateTime t = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        List<TimeSeriesDataPoint> requests = [new() { Timestamp = t, Value = 3 }, new() { Timestamp = t.AddMinutes(1), Value = 4 }];

        List<TimeSeriesDataPoint> errors = AppWorkloadQueries.ErrorsOrZeros([], requests);

        errors.Select(p => p.Value).Should().Equal(0, 0);
        errors.Select(p => p.Timestamp).Should().Equal(t, t.AddMinutes(1));
    }

    [Fact]
    public void ErrorsOrZeros_NoRequestsEither_StaysEmpty()
        => AppWorkloadQueries.ErrorsOrZeros([], []).Should().BeEmpty(
            "with no HTTP series at all there is nothing to call zero");

    // ── Volume table: all five queries or no table ───────────────

    private static Dictionary<string, InstantQueryOutcome> AllVolumeQueriesAnswered() => new()
    {
        [AppWorkloadQueries.PvcUsed]      = new([Claim("shop", "data", 256)], null),
        [AppWorkloadQueries.PvcCapacity]  = new([Claim("shop", "data", 1024)], null),
        [AppWorkloadQueries.PvcAvailable] = new([Claim("shop", "data", 768)], null),
        [AppWorkloadQueries.PvcRequested] = new([Claim("shop", "data", 1024), Claim("shop", "local", 2048)], null),
        [AppWorkloadQueries.PvcMounted]   = new([Claim("shop", "data", 1), Claim("shop", "local", 1)], null),
    };

    [Fact]
    public void The_volume_table_is_the_join_of_the_five_queries_when_all_of_them_answered()
    {
        KubernetesOperationResult<List<PvcUsageRow>> result = AppWorkloadQueries.PvcUsage(AllVolumeQueriesAnswered());

        result.IsSuccess.Should().BeTrue();
        result.Data!.Select(r => (r.Claim, r.Mounted, r.RequestedBytes)).Should().Equal(
            ("data", true, 1024d), ("local", true, 2048d));
    }

    /// <summary>
    /// ⚠️ The case that mislabelled: claim "local" sits on local-path storage, which reports no
    /// kubelet stats, so only the mounted query knows a pod mounts it. With that query failing
    /// and folded into "no series", the table said "not mounted" about a claim in use.
    /// </summary>
    [Fact]
    public void A_failed_mounted_query_fails_the_table_rather_than_calling_claims_unmounted()
    {
        Dictionary<string, InstantQueryOutcome> outcomes = AllVolumeQueriesAnswered();
        outcomes[AppWorkloadQueries.PvcMounted] = new([], "Response status code does not indicate success: 503 (Service Unavailable).");

        KubernetesOperationResult<List<PvcUsageRow>> result = AppWorkloadQueries.PvcUsage(outcomes);

        result.IsSuccess.Should().BeFalse("a table missing one of its joined vectors states things nobody measured");
        result.Error.Should().Contain("which claims are mounted").And.Contain("503 (Service Unavailable)");
        result.Error.Should().NotContain("used space", "only the query that failed is named");
    }

    [Fact]
    public void Every_failed_volume_query_is_named_and_a_shared_reason_is_said_once()
    {
        Dictionary<string, InstantQueryOutcome> outcomes = AllVolumeQueriesAnswered();
        foreach (string key in AppWorkloadQueries.PvcKeys)
            outcomes[key] = new([], "Prometheus did not answer in time.");

        string error = AppWorkloadQueries.PvcUsage(outcomes).Error!;

        error.Should().Contain("used space").And.Contain("capacity").And.Contain("free space")
            .And.Contain("requested size").And.Contain("which claims are mounted");
        error.Split("did not answer in time").Should().HaveCount(2, "five identical reasons read as one");
    }

    // ── HTTP section: a failed query is not an absent series ─────

    private static readonly DateTime T0 = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private static RangeOutcome Answered(params double[] values) => new(
        values.Length == 0 ? [] :
        [new PrometheusTimeSeries
        {
            DataPoints = [.. values.Select((v, i) => new TimeSeriesDataPoint { Timestamp = T0.AddMinutes(i), Value = v })],
        }],
        null);

    private static RangeOutcome Failed(string why) => new([], why);

    /// <summary>
    /// ⚠️ With requests flowing, an empty 5xx answer is zero errors — a status code's series
    /// exists only once it has been answered. An empty list from a 5xx query that FAILED was the
    /// same value by then, and drew a measured flat 0/s line from a query that never answered.
    /// </summary>
    [Fact]
    public void A_failed_5xx_query_beside_working_requests_is_not_drawn_as_zero_errors()
    {
        HttpMetricsView view = AppWorkloadQueries.HttpMetrics(
            rate: Answered(3, 4), errors: Failed("query timed out"), p95: Answered(12, 15));

        view.State.Should().Be(HttpMetricsState.Charted);
        view.Errors.Should().BeEmpty("no line at all, rather than a line of zeros nobody measured");
        view.ErrorsError.Should().Be("query timed out");
        view.Rate.Select(p => p.Value).Should().Equal(3, 4);
    }

    [Fact]
    public void An_answered_5xx_query_with_no_series_is_still_zero_errors()
    {
        HttpMetricsView view = AppWorkloadQueries.HttpMetrics(Answered(3, 4), Answered(), Answered(12, 15));

        view.Errors.Select(p => p.Value).Should().Equal(0, 0);
        view.ErrorsError.Should().BeNull();
    }

    /// <summary>
    /// ⚠️ A failed request query used to read as "no HTTP series", whose advice is to install
    /// Prometheus and apply the eBPF agent again — on a cluster where both are running and
    /// Prometheus merely did not answer.
    /// </summary>
    [Fact]
    public void A_failed_request_query_is_a_failure_not_a_cluster_without_the_agent()
    {
        HttpMetricsView view = AppWorkloadQueries.HttpMetrics(
            rate: Failed("503 (Service Unavailable)"), errors: Answered(), p95: Answered());

        view.State.Should().Be(HttpMetricsState.Failed);
        view.Error.Should().Be("503 (Service Unavailable)");
    }

    [Fact]
    public void An_answered_request_query_with_no_series_is_the_install_advice()
        => AppWorkloadQueries.HttpMetrics(Answered(), Failed("whatever"), Failed("whatever"))
            .State.Should().Be(HttpMetricsState.NoSeries,
                "Prometheus answered and holds no HTTP series: that is what the advice is for");

    /// <summary>
    /// The p95 chart is placed by step index beside the Requests and 5xx charts, so it must get
    /// every step — the NaN of a step without requests included; MetricChart draws it as a gap.
    /// </summary>
    [Fact]
    public void The_p95_series_keeps_every_step_including_the_ones_without_requests()
    {
        HttpMetricsView view = AppWorkloadQueries.HttpMetrics(
            Answered(3, 0, 4), Answered(), Answered(12, double.NaN, 15));

        view.P95Ms.Should().HaveCount(3);
        view.P95Ms.Select(p => p.Timestamp).Should().Equal(T0, T0.AddMinutes(1), T0.AddMinutes(2));
        double.IsNaN(view.P95Ms[1].Value).Should().BeTrue();
    }

    [Fact]
    public void A_failed_p95_query_is_said_beside_working_requests()
    {
        HttpMetricsView view = AppWorkloadQueries.HttpMetrics(Answered(3, 4), Answered(), Failed("bad_data"));

        view.State.Should().Be(HttpMetricsState.Charted);
        view.P95Ms.Should().BeEmpty();
        view.P95Error.Should().Be("bad_data");
    }

    [Fact]
    public void The_banner_names_each_failed_chart_grouped_by_reason()
    {
        string? banner = AppWorkloadQueries.FailedCharts(
        [
            ("CPU total", Failed("timeout")),
            ("Memory total", Answered(1, 2)),
            ("Container restarts", Failed("timeout")),
            ("5xx errors", Failed("503")),
        ]);

        banner.Should().Be("CPU total, Container restarts: timeout; 5xx errors: 503");
    }

    [Fact]
    public void The_banner_is_absent_when_every_chart_answered_even_with_no_series()
        => AppWorkloadQueries.FailedCharts([("CPU total", Answered()), ("Requests", Answered(1))])
            .Should().BeNull();

    [Fact]
    public void A_failed_range_result_without_a_message_still_counts_as_failed()
    {
        RangeOutcome outcome = RangeOutcome.From(KubernetesOperationResult<List<PrometheusTimeSeries>>.Failure(""));

        outcome.Succeeded.Should().BeFalse("an empty message must not turn a failure into an answer");
        outcome.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FormatBytes_UsesBinaryUnits()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            AppWorkloadQueries.FormatBytes(512).Should().Be("512 B");
            AppWorkloadQueries.FormatBytes(512 * 1024 * 1024d).Should().Be("512 MiB");
            AppWorkloadQueries.FormatBytes(1.5 * 1024 * 1024 * 1024).Should().Be("1.5 GiB");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
