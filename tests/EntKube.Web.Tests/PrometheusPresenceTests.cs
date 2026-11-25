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

    private static readonly DateTime RanBefore = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private ClusterComponent Add(Guid cluster, DataStatus status,
        string chart = "kube-prometheus-stack", string name = "kube-prometheus-stack",
        DateTime? installedAt = null)
    {
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = cluster, Name = name, ComponentType = "HelmChart",
            HelmChartName = chart, ReleaseName = "prom", Namespace = "monitoring", Status = status,
            InstalledAt = installedAt,
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
    public async Task A_prometheus_that_has_never_been_installed_is_no_prometheus(DataStatus status)
    {
        Add(clusterId, status, installedAt: null);

        (await catalog.FindPrometheusAsync(tenantId, clusterId)).Should().BeNull(
            "a first install that is running or failed, or a half-removed one, has no Prometheus "
            + "Operator CRDs to promise — a ServiceMonitor rendered on its word fails the release");
    }

    /// <summary>
    /// An upgrade of a running stack holds the status at Installing for the whole <c>helm --wait</c>,
    /// and a failed upgrade leaves it Failed — while helm keeps the previous revision serving. Asking
    /// for Installed alone made every upgrade an outage of every metrics view, and an OBI re-apply
    /// in that window switched its metrics off.
    /// </summary>
    [Theory]
    [InlineData(DataStatus.Installing)]
    [InlineData(DataStatus.Failed)]
    public async Task A_prometheus_being_upgraded_or_whose_upgrade_failed_is_still_the_answer(DataStatus status)
    {
        ClusterComponent prometheus = Add(clusterId, status, installedAt: RanBefore);

        InstalledComponent? found = await catalog.FindPrometheusAsync(tenantId, clusterId);

        found.Should().NotBeNull("InstalledAt says this release ran, and helm keeps its last revision serving");
        found!.Id.Should().Be(prometheus.Id);
    }

    [Theory]
    [InlineData(DataStatus.NotInstalled)]
    [InlineData(DataStatus.Uninstalling)]
    public async Task A_prometheus_being_removed_is_no_prometheus_even_though_it_once_ran(DataStatus status)
    {
        Add(clusterId, status, installedAt: RanBefore);

        (await catalog.FindPrometheusAsync(tenantId, clusterId)).Should().BeNull(
            "the release is going or gone; that it ran once says nothing about its CRDs now");
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
    /// The query and the in-memory rule the sweeps use are two spellings of one predicate (the
    /// query compares the entity's enum, which the contract cannot see); this asks both the same
    /// question for every status, with and without <c>InstalledAt</c>, so they cannot drift apart
    /// unnoticed — and pins both to the decided table, so they cannot drift together either.
    /// </summary>
    [Fact]
    public async Task The_query_and_the_in_memory_rule_agree_for_every_status_and_install_history()
    {
        foreach (DataStatus status in Enum.GetValues<DataStatus>())
        {
            foreach (DateTime? installedAt in new DateTime?[] { null, RanBefore })
            {
                db.ClusterComponents.RemoveRange(db.ClusterComponents);
                db.SaveChanges();
                Add(clusterId, status, installedAt: installedAt);

                bool byQuery = await catalog.FindPrometheusAsync(tenantId, clusterId) is not null;
                bool byRule = (await catalog.GetComponentsForClusterAsync(tenantId, clusterId))
                    .Any(PrometheusPresence.Answers);

                // Spelled as a table on purpose, not by calling either rule.
                bool expected = status switch
                {
                    DataStatus.Installed => true,
                    DataStatus.Installing or DataStatus.Failed => installedAt is not null,
                    _ => false,
                };

                string when = $"{status} with InstalledAt {(installedAt is null ? "null" : "set")}";
                byRule.Should().Be(byQuery, $"both must answer the same for {when}");
                byQuery.Should().Be(expected, $"the decided rule for {when}");
            }
        }
    }

    [Fact]
    public void The_rule_reads_the_chart_and_the_status()
    {
        InstalledComponent Component(string chart, ContractStatus status, DateTime? installedAt = null) => new(
            Guid.NewGuid(), tenantId, clusterId, "c", Guid.NewGuid(), "anything", "HelmChart", status,
            "monitoring", "prom", chart, null, null, null, null, installedAt);

        PrometheusPresence.Answers(Component("kube-prometheus-stack", ContractStatus.Installed)).Should().BeTrue();
        PrometheusPresence.Answers(Component("kube-prometheus-stack", ContractStatus.Failed)).Should().BeFalse(
            "a failed FIRST install never served");
        PrometheusPresence.Answers(Component("kube-prometheus-stack", ContractStatus.Failed, RanBefore)).Should().BeTrue(
            "a failed upgrade leaves the previous revision serving");
        PrometheusPresence.Answers(Component("prometheus", ContractStatus.Installed)).Should().BeFalse();
        PrometheusPresence.Answers(Component("prometheus", ContractStatus.Installing, RanBefore)).Should().BeFalse(
            "the chart still decides");
    }
}

/// <summary>
/// The cost the presence rule has at apply time: whether OBI's RED metrics stay on. Re-applying
/// OBI while kube-prometheus-stack was being upgraded (Installing) or after a failed upgrade
/// (Failed) asked "Installed?", heard no, and rendered the metrics OFF — on a cluster whose
/// Prometheus Operator and its CRDs had never gone anywhere.
/// </summary>
public class PrometheusPresenceAtApplyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly EntKube.Web.Services.ComponentLifecycleService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public PrometheusPresenceAtApplyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"t-{tenantId:N}", Slug = $"t-{tenantId:N}" });
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "prod" };
        db.Environments.Add(env);
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = env.Id,
            Name = $"c-{clusterId:N}", ApiServerUrl = "https://k8s.example",
        });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        EntKube.Web.Services.VaultService vault = new(factory,
            new EntKube.Web.Services.VaultEncryptionService(Convert.FromBase64String(TestServices.TestRootKeyBase64)),
            TestServices.NoClusterAccess);
        sut = TestServices.BuildLifecycle(factory, vault);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(DataStatus.Installing)]
    [InlineData(DataStatus.Failed)]
    public async Task Re_applying_obi_while_prometheus_is_upgraded_or_after_a_failed_upgrade_keeps_its_metrics_on(
        DataStatus prometheusStatus)
    {
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "kube-prometheus-stack", ComponentType = "HelmChart",
            HelmChartName = "kube-prometheus-stack", Namespace = "monitoring", Status = prometheusStatus,
            InstalledAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        ClusterComponent obi = new()
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "otel-ebpf", ComponentType = "HelmChart",
            HelmChartName = "opentelemetry-ebpf-instrumentation", ReleaseName = "otel-ebpf",
            Namespace = "monitoring",
            HelmValues = EntKube.Web.Services.ComponentCatalog.GetByKey("otel-ebpf")!.DefaultValues,
        };
        db.ClusterComponents.Add(obi);
        db.SaveChanges();

        string values = (await sut.GetInstallCommandAsync(tenantId, obi.Id)).ValuesYaml!;

        EntKube.Web.Services.YamlFormMerger.ExtractValue(values, "serviceMonitor.enabled").Should().Be("true",
            "the release ran before (InstalledAt) and helm keeps its last revision — and its CRDs — serving");
        EntKube.Web.Services.YamlFormMerger.ExtractValue(values, "config.data.prometheus_export.port").Should().Be("9090");
    }
}
