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
            requested: [Claim("billing", "data", 100)]);

        rows.Should().ContainSingle();
        PvcUsageRow r = rows[0];
        r.Should().BeEquivalentTo(new PvcUsageRow("billing", "data", 25, 100, 75, 100, Mounted: true));
        r.UsedPercent.Should().Be(25);
    }

    /// <summary>
    /// The kubelet reports a volume only while a pod mounts it. A claim kube-state-metrics knows and
    /// the kubelet does not is unmounted: it keeps its requested size, and nothing about it is 0.
    /// </summary>
    [Fact]
    public void PvcRows_AnUnmountedClaim_KeepsItsRequestedSize_AndNoUsage()
    {
        List<PvcUsageRow> rows = AppWorkloadQueries.PvcRows(
            used: [Claim("billing", "data", 25)],
            capacity: [Claim("billing", "data", 100)],
            available: [Claim("billing", "data", 75)],
            requested: [Claim("billing", "data", 100), Claim("billing", "archive", 500)]);

        PvcUsageRow archive = rows.Single(r => r.Claim == "archive");
        archive.Mounted.Should().BeFalse();
        archive.RequestedBytes.Should().Be(500);
        archive.UsedBytes.Should().BeNull("an unmounted claim has no usage, which is not zero usage");
        archive.UsedPercent.Should().BeNull();

        rows.Single(r => r.Claim == "data").Mounted.Should().BeTrue();
    }

    [Fact]
    public void PvcRows_SameClaimNameInTwoNamespaces_IsTwoRows()
    {
        List<PvcUsageRow> rows = AppWorkloadQueries.PvcRows(
            [Claim("a", "data", 1), Claim("b", "data", 2)], [], [], []);

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
