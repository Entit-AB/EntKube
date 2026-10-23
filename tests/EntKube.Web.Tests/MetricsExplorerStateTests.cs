using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Two things <see cref="MetricsExplorer"/> got wrong about state it did not own.
///
/// <para><b>Defect 1 — the service filter outlived its cluster.</b> The per-cluster reset in
/// <c>OnParametersSetAsync</c> cleared the series, the chart flag and the selected metric but not
/// <c>selectedService</c>, while the <c>services</c> list was replaced wholesale. The
/// <c>&lt;select&gt;</c> therefore re-rendered with the new cluster's options and <i>no</i>
/// selection — a Blazor select only takes its selection from options rendered in the same batch —
/// so the box read as "All services" while the field still held cluster A's service name, and
/// <c>Load()</c> put it into the PromQL as <c>service_name="&lt;cluster A's service&gt;"</c>.
/// TenantMetricsTab mounts this component with no <c>@key</c>, so the instance really is reused
/// across a switch of the Cluster dropdown and this reset is the whole boundary between the two
/// clusters. The operator saw "No data points for this metric in the selected range" on a cluster
/// that was emitting it fine — or, if cluster B happened to run a service of the same name, a chart
/// silently narrowed to that one service with nothing on screen to say so.</para>
///
/// <para><b>Defect 2 — an outage read as an un-instrumented cluster.</b> Availability came from
/// <c>IMetricsQuery.HasDataAsync</c>, a bool: a missing prometheus component, a cluster with no
/// kubeconfig, no ready Prometheus pod, a 401/403 on the API-server proxy and a WAN timeout all
/// arrived as the same <c>false</c> as a healthy, empty Prometheus. The screen then said "no
/// metrics for this cluster yet" and told the reader to install a collector — advice that was also
/// wrong for this backend, which reads the cluster's own Prometheus. Every other Prometheus
/// consumer in the UI renders <c>KubernetesOperationResult.Error</c>, including <c>Load()</c> one
/// method further down the same file.</para>
///
/// <para>⚠️ Both failures are states that <i>look</i> like a working screen, so each test here
/// asserts the opposite state too: the service filter is first proven to reach the query at all,
/// and the empty-state banner is proven to still appear when Prometheus genuinely answers with
/// nothing. Without those halves, a component that rendered nothing would pass.</para>
/// </summary>
public class MetricsExplorerStateTests : BunitContext
{
    private const string Metric = "http_requests_total";

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterA = Guid.NewGuid();
    private readonly Guid clusterB = Guid.NewGuid();

    private static KubernetesOperationResult<List<string>> Ok(params string[] values)
        => KubernetesOperationResult<List<string>>.Success([.. values]);

    private IRenderedComponent<MetricsExplorer> RenderFor(Guid clusterId)
        => Render<MetricsExplorer>(ps => ps
            .Add(p => p.TenantId, tenantId)
            .Add(p => p.ClusterId, clusterId));

    /// <summary>Index into the four filter selects: metric, service, break-down, time range.</summary>
    private static void Choose(IRenderedComponent<MetricsExplorer> cut, int selectIndex, string value)
    {
        IReadOnlyList<AngleSharp.Dom.IElement> selects = cut.FindAll("select");
        selects.Count.Should().Be(4, "metric, service, break-down and time range");
        selects[selectIndex].Change(value);
    }

    [Fact]
    public void Switching_cluster_drops_the_previous_clusters_service_filter()
    {
        FakeMetricsQuery fake = new();
        fake.Names[clusterA] = Ok(Metric);
        fake.Names[clusterB] = Ok(Metric);
        fake.ServiceNames[clusterA] = Ok("checkout");
        fake.ServiceNames[clusterB] = Ok("billing");
        Services.AddSingleton<IMetricsQuery>(fake);

        IRenderedComponent<MetricsExplorer> cut = RenderFor(clusterA);
        cut.WaitForState(() => cut.Markup.Contains("checkout"), TimeSpan.FromSeconds(10));

        // The positive half: a chosen service does reach the query, so the null below is the reset
        // working and not the plumbing being absent.
        Choose(cut, 0, Metric);
        Choose(cut, 1, "checkout");
        cut.Find("button").Click();
        cut.WaitForAssertion(
            () => fake.LastServiceFilter.Should().Be("checkout"),
            TimeSpan.FromSeconds(10));

        cut.Render(ps => ps
            .Add(p => p.TenantId, tenantId)
            .Add(p => p.ClusterId, clusterB));
        cut.WaitForState(() => cut.Markup.Contains("billing"), TimeSpan.FromSeconds(10));
        cut.Markup.Should().NotContain(
            "checkout", "cluster B's dropdown offers cluster B's services");

        fake.LastServiceFilter = "not-asked";
        Choose(cut, 0, Metric);
        cut.Find("button").Click();
        cut.WaitForAssertion(
            () => fake.LastServiceFilter.Should().BeNull(
                "after the switch the Service box reads 'All services', so the query must be "
                + "unfiltered — it used to carry cluster A's 'checkout' into cluster B's PromQL"),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_failing_prometheus_is_reported_as_a_failure()
    {
        const string error = "No prometheus component found on this cluster.";

        FakeMetricsQuery fake = new();
        fake.Names[clusterA] = KubernetesOperationResult<List<string>>.Failure(error);
        fake.ServiceNames[clusterA] = KubernetesOperationResult<List<string>>.Failure(error);
        Services.AddSingleton<IMetricsQuery>(fake);

        IRenderedComponent<MetricsExplorer> cut = RenderFor(clusterA);

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain(
                error, "the reason the backend gave is the only thing that tells an operator to go "
                + "and look at Prometheus rather than at their own apps"),
            TimeSpan.FromSeconds(10));

        cut.Markup.Should().NotContain(
            "No metrics for this cluster yet",
            "that banner is a statement about the cluster, and nothing was learned about the cluster");
    }

    [Fact]
    public void An_answering_but_empty_prometheus_still_gets_the_empty_state()
    {
        FakeMetricsQuery fake = new();
        fake.Names[clusterA] = Ok();
        fake.ServiceNames[clusterA] = Ok();
        Services.AddSingleton<IMetricsQuery>(fake);

        IRenderedComponent<MetricsExplorer> cut = RenderFor(clusterA);

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain(
                "No metrics for this cluster yet",
                "Prometheus answered and knew no metric names — this is the one reading the empty "
                + "state is entitled to make"),
            TimeSpan.FromSeconds(10));

        cut.Markup.Should().NotContain(
            "Could not read this cluster's metrics",
            "nothing failed, so there is no failure to report");
    }

    /// <summary>
    /// A stand-in for <see cref="PromMetricsService"/> that answers per cluster and records the
    /// service filter it was asked for. <c>HasDataAsync</c> is implemented honestly — it collapses
    /// a failure into <c>false</c> exactly as the real one must, since the bool has no room for a
    /// reason. That is what makes the failure test above meaningful: a component that gated on it
    /// would see <c>false</c> and show the empty state.
    /// </summary>
    private sealed class FakeMetricsQuery : IMetricsQuery
    {
        public Dictionary<Guid, KubernetesOperationResult<List<string>>> Names { get; } = [];
        public Dictionary<Guid, KubernetesOperationResult<List<string>>> ServiceNames { get; } = [];

        /// <summary>The <c>service</c> argument of the last range query. Null = unfiltered.</summary>
        public string? LastServiceFilter { get; set; } = "not-asked";

        public Task<bool> HasDataAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
        {
            KubernetesOperationResult<List<string>> names = Names[clusterId];
            return Task.FromResult(names.IsSuccess && names.Data is { Count: > 0 });
        }

        public Task<KubernetesOperationResult<List<string>>> GetMetricNamesAsync(
            Guid tenantId, Guid clusterId, CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
            => Task.FromResult(Names[clusterId]);

        public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
            Guid tenantId, Guid clusterId, CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
            => Task.FromResult(ServiceNames[clusterId]);

        public Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
            Guid tenantId, Guid clusterId, string name, string? service, DateTime from, DateTime to,
            int buckets = 60, CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        {
            LastServiceFilter = service;
            return Task.FromResult(KubernetesOperationResult<List<TimeSeriesDataPoint>>.Success([]));
        }

        public Task<KubernetesOperationResult<List<MetricSeriesGroup>>> GetSeriesByAsync(
            Guid tenantId, Guid clusterId, string name, string? service, string dimension,
            DateTime from, DateTime to, int buckets = 60, int maxSeries = 24,
            CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        {
            LastServiceFilter = service;
            return Task.FromResult(KubernetesOperationResult<List<MetricSeriesGroup>>.Success([]));
        }
    }
}
