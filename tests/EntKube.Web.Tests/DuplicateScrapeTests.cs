using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Endpoints more than one job scrapes, and what the scrape-targets panel says about them — and
/// about a target list it could not read, which is neither "no duplicates" nor "no targets".
/// </summary>
public sealed class DuplicateScrapeTests : BunitContext
{
    private static ScrapeTarget Target(string pool, string url) => new() { Pool = pool, ScrapeUrl = url, Health = "up" };

    // ──────── The detector ────────

    [Fact]
    public void One_endpoint_in_two_pools_is_a_duplicate()
    {
        DuplicateScrape d = DuplicateScrapes.Find(
        [
            Target("podMonitor/data/shop/0", "http://10.0.0.5:9187/metrics"),
            Target("podMonitor/data/entkube-cnpg-shop/0", "http://10.0.0.5:9187/metrics"),
            Target("podMonitor/data/shop/0", "http://10.0.0.6:9187/metrics"),
        ]).Should().ContainSingle().Subject;

        d.Url.Should().Be("http://10.0.0.5:9187/metrics");
        d.Pools.Should().Equal("podMonitor/data/entkube-cnpg-shop/0", "podMonitor/data/shop/0");
    }

    [Fact]
    public void Two_ports_of_one_pod_are_different_endpoints()
    {
        DuplicateScrapes.Find(
        [
            Target("a", "http://10.0.0.5:9187/metrics"),
            Target("b", "http://10.0.0.5:8080/metrics"),
        ]).Should().BeEmpty();
    }

    /// <summary>
    /// ⭐ The planner's rule (ScrapedEndpoints.ServesItsOwnMetrics): one port serves one set of series,
    /// whatever the path. RabbitMQ's /metrics/per-object serves its /metrics again, per object, so a job on
    /// each doubles every sum the same as two jobs on one path.
    /// </summary>
    [Fact]
    public void Two_paths_of_one_port_are_one_endpoint()
    {
        DuplicateScrape d = DuplicateScrapes.Find(
        [
            Target("serviceMonitor/queues/events-metrics/0", "http://10.0.0.7:15692/metrics"),
            Target("rabbitmq-per-object", "http://10.0.0.7:15692/metrics/per-object"),
        ]).Should().ContainSingle().Subject;

        d.Scrapes.Should().Equal(
            new EndpointScrape("rabbitmq-per-object", null, "/metrics/per-object"),
            new EndpointScrape("serviceMonitor/queues/events-metrics/0", null, "/metrics"));
        d.Scrapes[0].ToString().Should().Contain("/metrics/per-object", "the path is what tells a person where to look");
    }

    /// <summary>
    /// What asks the port for something else is not its own series: an exporter's probe, a query that
    /// picks families, and the kubelet's separate sets beside its /metrics. kube-prometheus-stack scrapes
    /// the kubelet on /metrics, /metrics/cadvisor and /metrics/probes of every node.
    /// </summary>
    [Fact]
    public void A_path_that_asks_for_something_else_is_its_own_endpoint()
    {
        DuplicateScrapes.Find(
        [
            Target("serviceMonitor/kube-system/kubelet/0", "https://10.0.1.1:10250/metrics"),
            Target("serviceMonitor/kube-system/kubelet/1", "https://10.0.1.1:10250/metrics/cadvisor"),
            Target("serviceMonitor/kube-system/kubelet/2", "https://10.0.1.1:10250/metrics/probes"),
            Target("serviceMonitor/kube-system/kubelet/3", "https://10.0.1.1:10250/metrics/resource"),
            Target("serviceMonitor/kube-system/kubelet/4", "https://10.0.1.1:10250/metrics/slis"),
            Target("exporter", "http://10.0.0.9:9115/metrics"),
            Target("probe/a", "http://10.0.0.9:9115/probe?target=a"),
            Target("rabbitmq-detailed", "http://10.0.0.7:15692/metrics/detailed?family=queue_coarse_metrics"),
            Target("rabbitmq", "http://10.0.0.7:15692/metrics"),
        ]).Should().BeEmpty();
    }

    [Fact]
    public void Two_scrapes_that_ask_for_the_same_something_else_are_still_a_duplicate()
    {
        DuplicateScrapes.Find(
        [
            Target("serviceMonitor/kube-system/kubelet/1", "https://10.0.1.1:10250/metrics/cadvisor"),
            Target("cadvisor-by-hand", "https://10.0.1.1:10250/metrics/cadvisor"),
        ]).Should().ContainSingle();
    }

    [Fact]
    public void The_same_endpoint_by_another_scheme_is_still_the_same_endpoint()
    {
        DuplicateScrapes.Find(
            [Target("a", "http://10.0.0.5:9187/metrics"), Target("b", "https://10.0.0.5:9187/metrics")])
            .Should().ContainSingle();
    }

    /// <summary>A blackbox exporter serves every probe on one path; the target is in the query string.</summary>
    [Fact]
    public void Two_probes_through_one_exporter_are_not_duplicates()
    {
        DuplicateScrapes.Find(
        [
            Target("probe/a", "http://blackbox:9115/probe?module=http&target=https%3A%2F%2Fa.example"),
            Target("probe/b", "http://blackbox:9115/probe?module=http&target=https%3A%2F%2Fb.example"),
        ]).Should().BeEmpty();
    }

    private static ScrapeTarget Target(string pool, string url, string job) =>
        new() { Pool = pool, ScrapeUrl = url, Health = "up", Labels = new() { ["job"] = job } };

    /// <summary>
    /// ⭐ One ServiceMonitor that reaches a pod port through two Services: one pool, two targets, one
    /// job label per Service. Prometheus merges targets whose labels are all the same, so two that
    /// it lists are two scrapes, and every sum over them doubles.
    /// </summary>
    [Fact]
    public void One_endpoint_twice_in_one_pool_is_a_duplicate()
    {
        DuplicateScrape d = DuplicateScrapes.Find(
        [
            Target("serviceMonitor/caches/redis/0", "http://10.0.0.5:9121/metrics", "sessions-leader"),
            Target("serviceMonitor/caches/redis/0", "http://10.0.0.5:9121/metrics", "sessions-leader-metrics"),
            Target("serviceMonitor/caches/redis/0", "http://10.0.0.6:9121/metrics", "sessions-leader"),
        ]).Should().ContainSingle().Subject;

        d.Url.Should().Be("http://10.0.0.5:9121/metrics");
        d.Scrapes.Should().Equal(
            new EndpointScrape("serviceMonitor/caches/redis/0", "sessions-leader"),
            new EndpointScrape("serviceMonitor/caches/redis/0", "sessions-leader-metrics"));
        d.Pools.Should().Equal("serviceMonitor/caches/redis/0");
    }

    /// <summary>A pool whose job label is the pool's own name is named once: the annotation job is.</summary>
    [Fact]
    public void A_scrape_is_named_by_its_pool_and_by_its_job_only_where_the_two_differ()
    {
        new EndpointScrape("kubernetes-pods-annotated", "kubernetes-pods-annotated").ToString().Should().Be("kubernetes-pods-annotated");
        new EndpointScrape("podMonitor/data/shop/0", null).ToString().Should().Be("podMonitor/data/shop/0");
        new EndpointScrape("serviceMonitor/mq/a/0", "orders").ToString().Should().Be("serviceMonitor/mq/a/0 (job orders)");
    }

    // ──────── Reading the list ────────

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"status":"error","error":"too busy"}""")]
    [InlineData("""{"status":"success","data":{}}""")]
    [InlineData("""{"status":"success","data":null}""")]
    [InlineData("""{"status":"success","data":{"activeTargets":[{"scrapePool":"a","scrapeUrl":"http://a/metrics"},{"scrapePool":"b"}]}}""")]
    public void A_target_list_that_cannot_be_read_is_an_error_and_never_a_shorter_list(string body)
    {
        Action read = () => PrometheusService.ParseScrapeTargets(body);

        read.Should().Throw<UnreadableTargetListException>().WithMessage("The list of scrape targets could not be read*");
    }

    [Fact]
    public void A_target_list_is_read_whole_even_when_a_field_nobody_decides_by_is_odd()
    {
        PrometheusService.ParseScrapeTargets("""
            {"status":"success","data":{"activeTargets":[
              {"scrapePool":"a","scrapeUrl":"http://a/metrics","health":"up","lastScrapeDuration":"soon"},
              {"scrapePool":"b","scrapeUrl":"http://b/metrics","health":"down","lastScrapeDuration":0.5,"labels":{"job":"b"}}]}}
            """).Select(t => (t.Pool, t.Health)).Should().Equal(("a", "up"), ("b", "down"));
    }

    // ──────── The panel ────────

    private readonly ScrapedClusterFake cluster = new();

    private IRenderedComponent<ScrapeTargetsPanel> Panel()
    {
        Services.AddSingleton(cluster.Prometheus);
        IRenderedComponent<ScrapeTargetsPanel> cut = Render<ScrapeTargetsPanel>(ps => ps
            .Add(p => p.TenantId, cluster.TenantId).Add(p => p.ClusterId, cluster.ClusterId));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading scrape targets"));
        return cut;
    }

    private static string Active(params (string Pool, string Url)[] targets) =>
        Active([.. targets.Select(t => (t.Pool, t.Url, t.Pool))]);

    private static string Active(params (string Pool, string Url, string Job)[] targets) =>
        "{\"status\":\"success\",\"data\":{\"activeTargets\":["
        + string.Join(",", targets.Select(t => $"{{\"scrapePool\":\"{t.Pool}\",\"scrapeUrl\":\"{t.Url}\",\"health\":\"up\",\"labels\":{{\"job\":\"{t.Job}\"}}}}"))
        + "]}}";

    [Fact]
    public void The_panel_names_the_doubled_endpoint_and_both_jobs()
    {
        cluster.Targets = Active(
            ("serviceMonitor/mq/orders-metrics/0", "http://10.0.0.7:15692/metrics"),
            ("kubernetes-pods-annotated", "http://10.0.0.7:15692/metrics"));

        string banner = Panel().Find("[data-duplicates]").TextContent;

        banner.Should().Contain("1 endpoint is scraped by more than one job")
            .And.Contain("http://10.0.0.7:15692/metrics")
            .And.Contain("serviceMonitor/mq/orders-metrics/0").And.Contain("kubernetes-pods-annotated");
    }

    /// <summary>Each scrape is named by its pool and its job: in one pool, the job is what tells them apart.</summary>
    [Fact]
    public void The_panel_names_both_jobs_of_an_endpoint_doubled_inside_one_pool()
    {
        cluster.Targets = Active(
            ("serviceMonitor/caches/redis/0", "http://10.0.0.5:9121/metrics", "sessions-leader"),
            ("serviceMonitor/caches/redis/0", "http://10.0.0.5:9121/metrics", "sessions-leader-metrics"));

        string banner = Panel().Find("[data-duplicates]").TextContent;

        banner.Should().Contain("1 endpoint is scraped by more than one job")
            .And.Contain("serviceMonitor/caches/redis/0 (job sessions-leader)")
            .And.Contain("serviceMonitor/caches/redis/0 (job sessions-leader-metrics)");
    }

    [Fact]
    public void The_panel_shows_no_banner_when_every_endpoint_has_one_job()
    {
        cluster.Targets = Active(("a", "http://10.0.0.7:15692/metrics"), ("b", "http://10.0.0.8:15692/metrics"));

        IRenderedComponent<ScrapeTargetsPanel> cut = Panel();

        cut.FindAll("[data-duplicates]").Should().BeEmpty();
        cut.Markup.Should().Contain("2 of 2 targets", "the list was read, so the missing banner means something");
    }

    [Fact]
    public void Searching_for_one_job_does_not_hide_the_banner()
    {
        cluster.Targets = Active(("a", "http://10.0.0.7:15692/metrics"), ("b", "http://10.0.0.7:15692/metrics"));
        IRenderedComponent<ScrapeTargetsPanel> cut = Panel();

        cut.Find("input.form-control").Input("a");

        cut.FindAll("[data-duplicates]").Should().ContainSingle();
    }

    /// <summary>⭐ The list that could not be read: an error, and no word about duplicates or counts.</summary>
    [Fact]
    public void A_list_the_panel_cannot_read_is_shown_as_an_error_and_not_as_no_targets()
    {
        cluster.Targets = "<html>Bad gateway</html>";

        IRenderedComponent<ScrapeTargetsPanel> cut = Panel();

        cut.Find(".alert-warning").TextContent.Should().Contain("could not be read");
        cut.Markup.Should().NotContain("0 of 0 targets").And.NotContain("no active scrape targets");
    }

    /// <summary>Unhandled, this reaches the Blazor dispatcher and takes the whole page down.</summary>
    [Fact]
    public void A_cluster_that_cannot_even_be_resolved_is_shown_as_an_error()
    {
        cluster.VaultIsDown = true;

        Panel().Find(".alert-warning").TextContent.Should().Contain("the vault is sealed");
    }

    [Fact]
    public void A_list_that_is_really_empty_says_so()
    {
        Panel().Find("[data-empty='targets']").TextContent.Should().Contain("no active scrape targets");
    }
}
