using System.Net;
using System.Text;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.Delivery;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.ScrapeMonitors;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The cluster is read in full or not at all. Every list here is one whose emptiness would be acted
/// on: no monitors means create them all, no pods means it is not running, no targets means nothing
/// else scrapes it.
/// </summary>
public sealed class LiveScrapeStateReaderTests
{
    private const string Monitoring = "/apis/monitoring.coreos.com/v1";
    private const string ProxiedTargets = "/api/v1/namespaces/monitoring/pods/prom-0:9090/proxy/api/v1/targets";

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly DateTimeOffset readAt = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>What the API server answers, by path, and by page where it has a second one.</summary>
    private readonly Dictionary<string, Func<HttpResponseMessage>> answers = new(StringComparer.Ordinal);
    private readonly List<string> asked = [];

    public LiveScrapeStateReaderTests()
    {
        Answer($"{Monitoring}/prometheuses", List("""
            {"metadata":{"name":"kps-prometheus","namespace":"monitoring","labels":{"release":"kps"}},"spec":{"podMonitorSelector":{}}}
            """));
        Answer($"{Monitoring}/servicemonitors", List("""
            {"metadata":{"name":"events-metrics","namespace":"queues","labels":{"a":"b"},"annotations":{"c":"d"}},"spec":{"selector":{}}}
            """));
        Answer($"{Monitoring}/podmonitors", List("""{"metadata":{"name":"orders","namespace":"databases"},"spec":{}}"""));
        Answer("/api/v1/namespaces/monitoring/endpoints/kps-prometheus", """
            {"apiVersion":"v1","kind":"Endpoints","metadata":{"name":"kps-prometheus"},
             "subsets":[{"addresses":[{"ip":"10.0.0.9","targetRef":{"kind":"Pod","name":"prom-0","namespace":"monitoring"}}],
                         "ports":[{"port":9090}]}]}
            """);
        Answer(ProxiedTargets, Targets("""
            {"scrapePool":"podMonitor/databases/orders/0","scrapeUrl":"http://10.0.0.5:9187/metrics","health":"up"}
            """));
        Answer("/api/v1/namespaces", List(
            """{"metadata":{"name":"databases","labels":{"kubernetes.io/metadata.name":"databases"}},"status":{"phase":"Active"}}""",
            """{"metadata":{"name":"leaving","labels":{}},"status":{"phase":"Terminating"}}"""));
        Answer("/api/v1/namespaces/databases/pods", List("""{"metadata":{"name":"orders-1"},"status":{"phase":"Running","podIP":"10.0.0.5"}}"""));
        Answer("/api/v1/namespaces/databases/services", List("""{"metadata":{"name":"orders-rw"},"spec":{"ports":[{"port":5432,"targetPort":"pg"}]}}"""));
        Answer("/apis/networking.k8s.io/v1/namespaces/databases/networkpolicies", List("""{"metadata":{"name":"deny"},"spec":{"podSelector":{}}}"""));
    }

    // ── What is scraped ───────────────────────────────────────────────────────

    [Fact]
    public async Task Everything_that_says_what_is_scraped_is_read()
    {
        LiveMonitoring? live = await ReadMonitoringAsync();

        LivePrometheus prometheus = live!.Prometheuses.Should().ContainSingle().Subject;
        (prometheus.Namespace, prometheus.Name, prometheus.Labels["release"]).Should().Be(("monitoring", "kps-prometheus", "kps"));
        prometheus.MonitorSelector(MonitorKind.PodMonitor).Should().NotBeNull();
        prometheus.MonitorSelector(MonitorKind.ServiceMonitor).Should().BeNull();

        live.Monitors.Select(m => m.Identity.ToString())
            .Should().Equal("ServiceMonitor queues/events-metrics", "PodMonitor databases/orders");
        live.Monitors[0].Labels["a"].Should().Be("b");
        live.Monitors[0].Annotations["c"].Should().Be("d");

        ActiveTarget target = live.Targets.Should().ContainSingle().Subject;
        target.Should().Be(new ActiveTarget("podMonitor/databases/orders/0", "10.0.0.5", 9187, "/metrics", Up: true));
        target.Monitor.Should().Be(new MonitorIdentity(MonitorKind.PodMonitor, "databases", "orders"));
    }

    [Theory]
    [InlineData("prometheuses")]
    [InlineData("servicemonitors")]
    [InlineData("podmonitors")]
    public async Task A_cluster_without_the_monitor_kinds_has_no_monitors_and_that_is_no_failure(string plural)
    {
        answers.Remove($"{Monitoring}/{plural}");

        (await ReadMonitoringAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("prometheuses", HttpStatusCode.Forbidden)]
    [InlineData("servicemonitors", HttpStatusCode.Forbidden)]
    [InlineData("podmonitors", HttpStatusCode.Forbidden)]
    [InlineData("podmonitors", HttpStatusCode.InternalServerError)]
    [InlineData("podmonitors", HttpStatusCode.ServiceUnavailable)]
    public async Task A_list_that_is_refused_is_not_an_empty_one(string plural, HttpStatusCode status)
    {
        Fail($"{Monitoring}/{plural}", status);

        (await FluentActions.Awaiting(ReadMonitoringAsync).Should().ThrowAsync<HttpOperationException>())
            .Which.Response.StatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"items":null}""")]
    [InlineData("""{"items":["a"]}""")]
    [InlineData("""{"items":[{"metadata":{"namespace":"x"},"spec":{}}]}""")]
    [InlineData("""{"items":[{"metadata":{"name":"x","namespace":"x"}}]}""")]
    public async Task A_list_that_cannot_be_read_is_not_an_empty_one(string body)
    {
        Answer($"{Monitoring}/podmonitors", body);

        await FluentActions.Awaiting(ReadMonitoringAsync).Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public async Task Every_page_is_read_to_the_last()
    {
        Answer($"{Monitoring}/podmonitors", """
            {"metadata":{"continue":"more"},"items":[{"metadata":{"name":"first","namespace":"a"},"spec":{}}]}
            """);
        Answer($"{Monitoring}/podmonitors?continue=more", """
            {"metadata":{"continue":""},"items":[{"metadata":{"name":"second","namespace":"a"},"spec":{}}]}
            """);

        LiveMonitoring? live = await ReadMonitoringAsync();

        live!.Monitors.Where(m => m.Identity.Kind == MonitorKind.PodMonitor).Select(m => m.Identity.Name)
            .Should().Equal("first", "second");
    }

    [Fact]
    public async Task A_second_page_that_fails_is_not_the_end_of_the_list()
    {
        Answer($"{Monitoring}/podmonitors", """{"metadata":{"continue":"more"},"items":[]}""");
        Fail($"{Monitoring}/podmonitors?continue=more", HttpStatusCode.Gone);

        await FluentActions.Awaiting(ReadMonitoringAsync).Should().ThrowAsync<HttpOperationException>();
    }

    [Fact]
    public async Task A_monitor_says_when_it_was_made_and_the_answer_when_it_was_read()
    {
        Answer($"{Monitoring}/podmonitors", List(
            """{"metadata":{"name":"orders","namespace":"databases","creationTimestamp":"2026-11-26T11:50:00Z"},"spec":{}}"""));

        LiveMonitoring live = (await ReadMonitoringAsync())!;

        live.Monitors.Single(m => m.Identity.Name == "orders").Created
            .Should().Be(new DateTimeOffset(2026, 11, 26, 11, 50, 0, TimeSpan.Zero));
        live.Monitors.Single(m => m.Identity.Name == "events-metrics").Created.Should().BeNull();
        live.ReadAt.Should().Be(readAt, "the age of what was read is measured from when it was read");
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("12")]
    public async Task A_creation_time_that_is_not_a_time_is_not_a_monitor_of_no_age(string created)
    {
        Answer($"{Monitoring}/podmonitors", List(
            $$$"""{"metadata":{"name":"orders","namespace":"databases","creationTimestamp":{{{created}}}},"spec":{}}"""));

        await FluentActions.Awaiting(ReadMonitoringAsync).Should().ThrowAsync<FormatException>();
    }

    // ── What Prometheus says ──────────────────────────────────────────────────

    [Fact]
    public async Task A_Prometheus_that_does_not_answer_is_not_one_that_scrapes_nothing()
    {
        Fail(ProxiedTargets, HttpStatusCode.ServiceUnavailable);

        await FluentActions.Awaiting(ReadMonitoringAsync).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("Prometheus could not be asked what it scrapes*");
    }

    [Theory]
    [InlineData("<html>")]
    [InlineData("""{"status":"error","data":{"activeTargets":[]}}""")]
    [InlineData("""{"status":"success","data":{}}""")]
    [InlineData("""{"status":"success","data":{"activeTargets":[{"scrapeUrl":"http://10.0.0.5:9187/metrics"}]}}""")]
    [InlineData("""{"status":"success","data":{"activeTargets":[{"scrapePool":"p","scrapeUrl":"not a url"}]}}""")]
    public async Task A_list_of_targets_that_cannot_be_read_is_not_an_empty_one(string body)
    {
        Answer(ProxiedTargets, body);

        await FluentActions.Awaiting(ReadMonitoringAsync).Should().ThrowAsync<FormatException>();
    }

    [Theory]
    [InlineData("https://10.0.0.5:9187/metrics?x=1", "10.0.0.5", 9187, "/metrics", "?x=1")]
    [InlineData("http://10.0.0.5/stats/prometheus", "10.0.0.5", 80, "/stats/prometheus", "")]
    [InlineData("http://[fd00::5]:9187/metrics", "fd00::5", 9187, "/metrics", "")]
    public void A_target_is_where_its_address_says(string url, string host, int port, string path, string query)
    {
        ActiveTarget target = LiveScrapeStateReader.ReadTargets(Targets(
            $$$"""{"scrapePool":"some-job","scrapeUrl":"{{{url}}}","health":"down"}""")).Single();

        target.Should().Be(new ActiveTarget("some-job", host, port, path, Up: false, query));
        target.Monitor.Should().BeNull("a job that is not an operator's monitor has none behind it");
    }

    // ── What runs ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task What_runs_is_read_in_the_namespaces_that_matter()
    {
        LiveWorkloads live = await ReadWorkloadsAsync("databases", "databases", "nowhere", "leaving");

        live.NamespaceLabels.Keys.Should().Equal("databases");
        LiveNamespace databases = live.Namespaces.Should().ContainSingle().Subject.Value;
        databases.Pods.Single().Metadata.Name.Should().Be("orders-1");
        databases.Services.Single().Spec.Ports.Single().TargetPort.Value.Should().Be("pg");
        databases.NetworkPolicies.Single().Metadata.Name.Should().Be("deny");
        asked.Should().NotContain(p => p.Contains("nowhere") || p.Contains("leaving"),
            "a namespace that is gone or going holds nothing to write to");
    }

    [Theory]
    [InlineData("/api/v1/namespaces")]
    [InlineData("/api/v1/namespaces/databases/pods")]
    [InlineData("/api/v1/namespaces/databases/services")]
    [InlineData("/apis/networking.k8s.io/v1/namespaces/databases/networkpolicies")]
    public async Task What_runs_is_not_nothing_because_it_may_not_be_seen(string path)
    {
        Fail(path, HttpStatusCode.Forbidden);

        await FluentActions.Awaiting(() => ReadWorkloadsAsync("databases")).Should().ThrowAsync<HttpOperationException>();
    }

    [Fact]
    public async Task Every_page_of_pods_is_read()
    {
        Answer("/api/v1/namespaces/databases/pods", """{"metadata":{"continue":"more"},"items":[{"metadata":{"name":"a"}}]}""");
        Answer("/api/v1/namespaces/databases/pods?continue=more", List("""{"metadata":{"name":"b"}}"""));

        LiveWorkloads live = await ReadWorkloadsAsync("databases");

        live.Namespaces["databases"].Pods.Select(p => p.Metadata.Name).Should().Equal("a", "b");
    }

    // ── The API server ────────────────────────────────────────────────────────

    private Task<LiveMonitoring?> ReadMonitoringAsync()
        => Reader().ReadMonitoringAsync(tenantId, Cluster(), CancellationToken.None);

    private Task<LiveWorkloads> ReadWorkloadsAsync(params string[] namespaces)
        => Reader().ReadWorkloadsAsync(Cluster(), namespaces, CancellationToken.None);

    private LiveScrapeStateReader Reader()
    {
        Mock<ICatalogApi> catalog = new();
        catalog.Setup(c => c.FindPrometheusAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstalledComponent(
                Guid.NewGuid(), tenantId, clusterId, "prod", Guid.NewGuid(), "kube-prometheus-stack", "HelmChart",
                ComponentStatus.Installed, "monitoring", "kps", PrometheusPresence.HelmChartName,
                null, null, null, null, DateTime.UtcNow));

        Mock<IClusterClientFactory> clusters = new();
        clusters.Setup(c => c.ForAsync(tenantId, clusterId, It.IsAny<CancellationToken>())).ReturnsAsync(Cluster());

        return new LiveScrapeStateReader(new PrometheusService(
            Mock.Of<IDeliveryApi>(), clusters.Object, catalog.Object, new PromQueryCache(),
            NullLogger<PrometheusService>.Instance), new StoppedClock(readAt));
    }

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private IClusterClient Cluster()
    {
        Mock<IClusterClient> client = new(MockBehavior.Strict);
        client.SetupGet(c => c.ClusterId).Returns(clusterId);
        client.Setup(c => c.GetPooledSdkClient()).Returns(() => new Kubernetes(
            new KubernetesClientConfiguration { Host = "https://k8s.example.com" }, new ApiServer(this)));
        return client.Object;
    }

    private void Answer(string path, string body)
        => answers[path] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private void Fail(string path, HttpStatusCode status)
        => answers[path] = () => new HttpResponseMessage(status) { Content = new StringContent("{}") };

    private static string List(params string[] items)
        => $$$"""{"metadata":{},"items":[{{{string.Join(",", items)}}}]}""";

    private static string Targets(params string[] targets)
        => $$$"""{"status":"success","data":{"activeTargets":[{{{string.Join(",", targets)}}}]}}""";

    /// <summary>The last handler of the SDK client's chain, so nothing leaves the process.</summary>
    private sealed class ApiServer(LiveScrapeStateReaderTests test) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri uri = request.RequestUri!;
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            string? page = System.Web.HttpUtility.ParseQueryString(uri.Query)["continue"];

            HttpResponseMessage response;
            lock (test.asked)
            {
                test.asked.Add(path);
                response = test.answers.TryGetValue(page is null ? path : $"{path}?continue={page}",
                    out Func<HttpResponseMessage>? answer)
                    ? answer()
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            // The SDK builds its exception from the response's request, which a real handler sets.
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
