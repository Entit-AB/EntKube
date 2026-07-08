using EntKube.Web.Services.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Metrics now come from Prometheus (apps write there directly); <see cref="PromMetricsQueryBuilder"/>
/// encodes the OTel→Prometheus label conventions and counter/gauge handling. This pins that PromQL
/// construction — the part that would otherwise only be checkable against a live Prometheus.
/// </summary>
public sealed class PromMetricsQueryBuilderTests
{
    [Theory]
    [InlineData("http_server_requests_total", true)]
    [InlineData("process_cpu_seconds_total", true)]
    [InlineData("jvm_memory_used_bytes", false)]
    [InlineData("queue_depth", false)]
    public void IsCounter_DetectsTotalSuffix(string metric, bool expected)
        => PromMetricsQueryBuilder.IsCounter(metric).Should().Be(expected);

    [Fact]
    public void BuildSelector_EmptyScope_IsEmpty()
        => PromMetricsQueryBuilder.BuildSelector(null, null, null).Should().Be("");

    [Fact]
    public void BuildSelector_CombinesNamespacePodService()
    {
        string sel = PromMetricsQueryBuilder.BuildSelector(["prod", "staging"], "^(api|worker)-", "checkout");
        sel.Should().Be("{k8s_namespace_name=~\"prod|staging\",k8s_pod_name=~\"^(api|worker)-.*\",service_name=\"checkout\"}");
    }

    [Fact]
    public void BuildSeriesQuery_Counter_UsesRate()
        => PromMetricsQueryBuilder.BuildSeriesQuery("http_requests_total", "{service_name=\"x\"}", 300)
            .Should().Be("sum(rate(http_requests_total{service_name=\"x\"}[300s]))");

    [Fact]
    public void BuildSeriesQuery_Gauge_SumsRaw()
        => PromMetricsQueryBuilder.BuildSeriesQuery("queue_depth", "", 300)
            .Should().Be("sum(queue_depth)");

    [Fact]
    public void BuildSeriesByQuery_GroupsAndTopK()
        => PromMetricsQueryBuilder.BuildSeriesByQuery("http_requests_total", "{service_name=\"x\"}", "k8s_pod_name", 300, 24)
            .Should().Be("topk(24, sum by (k8s_pod_name) (rate(http_requests_total{service_name=\"x\"}[300s])))");

    [Theory]
    [InlineData("pod", "k8s_pod_name")]
    [InlineData("service", "service_name")]
    [InlineData("namespace", "k8s_namespace_name")]
    [InlineData("weird", "k8s_pod_name")]
    public void BreakdownLabel_MapsDimension(string dim, string label)
        => PromMetricsQueryBuilder.BreakdownLabel(dim).Should().Be(label);

    [Fact]
    public void RateWindow_IsAtLeastAMinute_AndScalesWithStep()
    {
        DateTime from = new(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        PromMetricsQueryBuilder.RateWindowSeconds(from, from.AddMinutes(5), 60).Should().BeGreaterThanOrEqualTo(60);
        PromMetricsQueryBuilder.RateWindowSeconds(from, from.AddHours(24), 60).Should().BeGreaterThan(60);
    }
}
