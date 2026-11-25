using System.Net;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What the RabbitMQ panel may say about a broker it did or did not hear from: whether it is
/// scraped at all, whose series they are, and what a figure nobody exports looks like.
/// </summary>
public sealed class RabbitMQScrapeTests
{
    private static readonly RabbitMQBrokerIdentity Orders = new("orders", "shop", IsOperatorManaged: true);

    private readonly ScrapedClusterFake cluster = new();

    private async Task<RabbitMQMetricsSummary> Read(RabbitMQBrokerIdentity? broker = null)
    {
        KubernetesOperationResult<RabbitMQMetricsSummary> result =
            await cluster.Prometheus.GetRabbitMQMetricsAsync(cluster.TenantId, cluster.ClusterId, broker ?? Orders);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Data!;
    }

    /// <summary>
    /// What Prometheus says about a broker it has never scraped: nothing — except to the queries
    /// ending in <c>or vector(0)</c>, which answer zero for anything.
    /// </summary>
    private static (HttpStatusCode, string) NeverScraped(string query) =>
        query.Contains("or vector(0)") ? ScrapedClusterFake.Series("0") : ScrapedClusterFake.NoSeries;

    private static bool IsNodeCount(string query) =>
        query.StartsWith("sum(rabbitmq_identity_info", StringComparison.Ordinal);

    // ──────── Scraped or not ────────

    /// <summary>
    /// ⭐ The unscraped broker. Three queries answered zero for it, "some query answered" was the
    /// rule, so it was shown as scraped, idle and "Healthy" — and the button that would have had it
    /// scraped, which shows only when it is not, could not be reached.
    /// </summary>
    [Fact]
    public async Task A_broker_prometheus_never_scraped_has_no_data()
    {
        cluster.Query = NeverScraped;

        (await Read()).HasData.Should().BeFalse();

        cluster.Queries.Should().Contain(q => q.Contains("or vector(0)"),
            "if no query answers for an unscraped broker any more, this test proves nothing");
    }

    [Fact]
    public async Task A_scraped_broker_that_does_nothing_has_data()
    {
        cluster.Query = q => IsNodeCount(q) ? ScrapedClusterFake.Series("1") : NeverScraped(q);

        RabbitMQMetricsSummary idle = await Read();

        idle.HasData.Should().BeTrue("it reports its identity, idle or not");
        idle.Nodes.Should().Be(1);
        idle.PublishRatePerSec.Should().Be(0);
        idle.UnansweredQueries.Should().BeNull();
    }

    [Fact]
    public async Task When_the_identity_query_fails_the_broker_is_not_called_unscraped()
    {
        cluster.Query = q => IsNodeCount(q) ? (HttpStatusCode.ServiceUnavailable, "busy") : NeverScraped(q);

        KubernetesOperationResult<RabbitMQMetricsSummary> result =
            await cluster.Prometheus.GetRabbitMQMetricsAsync(cluster.TenantId, cluster.ClusterId, Orders);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("could not be read").And.Contain("503");
    }

    /// <summary>
    /// A target scraped twice under one job makes every join ambiguous: the identity still counts
    /// and every other query is refused. That was a panel of zeros with a "Healthy" badge.
    /// </summary>
    [Fact]
    public async Task Queries_prometheus_refuses_are_reported_and_not_shown_as_zeros_alone()
    {
        cluster.Query = q => IsNodeCount(q)
            ? ScrapedClusterFake.Series("2")
            : (HttpStatusCode.OK, """{"status":"error","errorType":"execution","error":"multiple matches for labels"}""");

        RabbitMQMetricsSummary summary = await Read();

        summary.HasData.Should().BeTrue();
        summary.UnansweredQueries.Should().Contain("multiple matches for labels");
    }

    // ──────── Sockets ────────

    [Fact]
    public async Task A_node_that_exports_no_socket_gauges_reports_no_sockets_rather_than_zero_of_zero()
    {
        cluster.Query = q => IsNodeCount(q) ? ScrapedClusterFake.Series("1")
            : q.Contains("rabbitmq_process_open_fds") ? ScrapedClusterFake.Series("40", "\"rabbitmq_node\":\"rabbit@orders-0\"")
            : NeverScraped(q);

        RabbitMQNodeMetrics node = (await Read()).NodeMetrics.Should().ContainSingle().Subject;

        node.OpenFds.Should().Be(40);
        node.ReportsSockets.Should().BeFalse();
        node.OpenSockets.Should().BeNull();
        node.MaxSockets.Should().BeNull();
        node.SocketUsedPercent.Should().Be(0);
    }

    [Fact]
    public async Task A_node_that_does_export_its_socket_gauges_reports_them()
    {
        cluster.Query = q => IsNodeCount(q) ? ScrapedClusterFake.Series("1")
            : q.Contains("rabbitmq_process_open_tcp_sockets") ? ScrapedClusterFake.Series("90", "\"rabbitmq_node\":\"rabbit@orders-0\"")
            : q.Contains("rabbitmq_process_max_tcp_sockets") ? ScrapedClusterFake.Series("100", "\"rabbitmq_node\":\"rabbit@orders-0\"")
            : NeverScraped(q);

        RabbitMQNodeMetrics node = (await Read()).NodeMetrics.Should().ContainSingle().Subject;

        node.ReportsSockets.Should().BeTrue();
        node.SocketUsedPercent.Should().Be(90);
        node.NearLimit.Should().BeTrue();
    }

    // ──────── Whose series ────────

    [Fact]
    public void Two_brokers_of_one_name_in_two_namespaces_never_share_a_series()
    {
        foreach ((string key, string query) in PrometheusService.BuildRabbitMQQueries(Orders))
            query.Should().Contain("namespace=\"shop\"", $"query '{key}' must not read another namespace's broker");

        foreach ((string key, string query) in PrometheusService.BuildRabbitMQQueries(Orders with { Namespace = "billing" }))
            query.Should().Contain("namespace=\"billing\"").And.NotContain("shop", $"query '{key}'");
    }

    [Fact]
    public void The_join_is_made_on_the_namespace_too()
    {
        foreach ((string key, string query) in PrometheusService.BuildRabbitMQQueries(Orders).Where(q => q.Key != "nodes"))
            query.Should().Contain("on(namespace, instance, job)", $"query '{key}'");
    }

    /// <summary>
    /// A broker EntKube did not install may be scraped by a hand-written static job, which adds no
    /// namespace label. <c>=~"shop|"</c> matches the namespace and the absent label, and nothing else.
    /// </summary>
    [Fact]
    public void An_external_broker_is_found_with_its_namespace_or_without_any_but_never_with_another()
    {
        PrometheusService.RabbitMQIdentity(Orders with { IsOperatorManaged = false })
            .Should().Be("rabbitmq_identity_info{rabbitmq_cluster=\"orders\",namespace=~\"shop|\",rabbitmq_endpoint=\"aggregated\"}");
    }

    [Fact]
    public void An_operator_managed_broker_is_found_with_its_namespace_only()
    {
        PrometheusService.RabbitMQIdentity(Orders)
            .Should().Be("rabbitmq_identity_info{rabbitmq_cluster=\"orders\",namespace=\"shop\",rabbitmq_endpoint=\"aggregated\"}");
    }
}
