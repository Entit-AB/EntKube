using EntKube.Contracts.Catalog;
using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DataStatus = EntKube.Web.Data.ComponentStatus;
using ContractStatus = EntKube.Contracts.Catalog.ComponentStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// Catalog's one answer to "does this cluster have Prometheus".
///
/// <para>It was answered three ways. <c>PrometheusService</c> took the first component whose
/// <em>name</em> contained "prometheus" — any status, any chart — while the alert and monitoring
/// pages asked for the chart and the Installed status. Now it decides whether OBI renders a
/// ServiceMonitor, which fails the whole release on a cluster without the Prometheus Operator's
/// CRDs, so a wrong "yes" is no longer an empty graph but an install that cannot complete.</para>
/// </summary>
public class PrometheusPresenceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly CatalogApi catalog;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid otherClusterId = Guid.NewGuid();
    private readonly Guid strangersClusterId = Guid.NewGuid();

    public PrometheusPresenceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        catalog = new CatalogApi(new TestDbContextFactory(connection));

        foreach ((Guid tenant, Guid[] clusters) in new[]
                 {
                     (tenantId, new[] { clusterId, otherClusterId }),
                     (strangerId, new[] { strangersClusterId }),
                 })
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = $"t-{tenant:N}", Slug = $"t-{tenant:N}" });
            Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant, Name = "prod" };
            db.Environments.Add(env);
            foreach (Guid cluster in clusters)
            {
                db.KubernetesClusters.Add(new KubernetesCluster
                {
                    Id = cluster, TenantId = tenant, EnvironmentId = env.Id,
                    Name = $"c-{cluster:N}", ApiServerUrl = "https://k8s.example",
                });
            }
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ClusterComponent Add(Guid cluster, DataStatus status,
        string chart = "kube-prometheus-stack", string name = "kube-prometheus-stack")
    {
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = cluster, Name = name, ComponentType = "HelmChart",
            HelmChartName = chart, ReleaseName = "prom", Namespace = "monitoring", Status = status,
        };
        db.ClusterComponents.Add(component);
        db.SaveChanges();
        return component;
    }

    [Fact]
    public async Task An_installed_kube_prometheus_stack_is_the_answer()
    {
        ClusterComponent prometheus = Add(clusterId, DataStatus.Installed);

        InstalledComponent? found = await catalog.FindPrometheusAsync(tenantId, clusterId);

        found.Should().NotBeNull();
        found!.Id.Should().Be(prometheus.Id);
        found.ReleaseName.Should().Be("prom", "PrometheusService derives the service name from it");
    }

    [Theory]
    [InlineData(DataStatus.NotInstalled)]
    [InlineData(DataStatus.Installing)]
    [InlineData(DataStatus.Failed)]
    [InlineData(DataStatus.Uninstalling)]
    public async Task A_prometheus_that_is_not_installed_is_no_prometheus(DataStatus status)
    {
        Add(clusterId, status);

        (await catalog.FindPrometheusAsync(tenantId, clusterId)).Should().BeNull(
            "a failed or half-removed install has no Prometheus Operator CRDs to promise — a "
            + "ServiceMonitor rendered on its word fails the release");
    }

    [Fact]
    public async Task Another_tenants_cluster_reads_as_having_none()
    {
        Add(strangersClusterId, DataStatus.Installed);

        (await catalog.FindPrometheusAsync(tenantId, strangersClusterId)).Should().BeNull(
            "the cluster id is the stranger's; asked as this tenant it is not there");
        (await catalog.FindPrometheusAsync(strangerId, strangersClusterId)).Should().NotBeNull(
            "and asked as its owner it is — the null above is the tenant scope, not a broken query");
    }

    [Fact]
    public async Task Another_cluster_of_the_same_tenant_does_not_count()
    {
        Add(otherClusterId, DataStatus.Installed);

        (await catalog.FindPrometheusAsync(tenantId, clusterId)).Should().BeNull();
    }

    [Fact]
    public async Task A_component_merely_named_like_prometheus_is_not_it()
    {
        // What PrometheusService's old `Name.Contains("prometheus")` accepted.
        Add(clusterId, DataStatus.Installed, chart: "prometheus-blackbox-exporter", name: "prometheus-blackbox");

        (await catalog.FindPrometheusAsync(tenantId, clusterId)).Should().BeNull(
            "the chart decides; a name with the word in it is an exporter, an adapter, anything");
    }

    /// <summary>
    /// The query and the in-memory rule the sweeps use are two spellings of one predicate; this asks
    /// both the same question for every status, so they cannot drift apart unnoticed.
    /// </summary>
    [Fact]
    public async Task The_query_and_the_in_memory_rule_agree_for_every_status()
    {
        foreach (DataStatus status in Enum.GetValues<DataStatus>())
        {
            db.ClusterComponents.RemoveRange(db.ClusterComponents);
            db.SaveChanges();
            Add(clusterId, status);

            bool byQuery = await catalog.FindPrometheusAsync(tenantId, clusterId) is not null;
            bool byRule = (await catalog.GetComponentsForClusterAsync(tenantId, clusterId))
                .Any(PrometheusPresence.Answers);

            byRule.Should().Be(byQuery, $"both must answer the same for {status}");
            byQuery.Should().Be(status == DataStatus.Installed);
        }
    }

    [Fact]
    public void The_rule_reads_the_chart_and_the_status()
    {
        InstalledComponent Component(string chart, ContractStatus status) => new(
            Guid.NewGuid(), tenantId, clusterId, "c", Guid.NewGuid(), "anything", "HelmChart", status,
            "monitoring", "prom", chart, null, null, null, null, null);

        PrometheusPresence.Answers(Component("kube-prometheus-stack", ContractStatus.Installed)).Should().BeTrue();
        PrometheusPresence.Answers(Component("kube-prometheus-stack", ContractStatus.Failed)).Should().BeFalse();
        PrometheusPresence.Answers(Component("prometheus", ContractStatus.Installed)).Should().BeFalse();
    }
}
