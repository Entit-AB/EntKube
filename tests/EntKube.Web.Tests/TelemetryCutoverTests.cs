using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Telemetry is held by the management plane again (docs/telemetry-central-again.md), which means the
/// cutover runs in reverse: a collector pointed at its cluster's own indexer has to be moved back, and the
/// read path has to keep sending reads to whichever store actually holds the data while that is in
/// progress.
///
/// Both halves move separately, and that is what makes it dangerous in either direction. WRITES move when
/// the collector is re-applied; READS follow the endpoint that re-apply records. Get the order wrong and
/// reads land on a store that is empty — and an empty store answers every query <i>successfully</i>, so
/// every log view, trace view and dashboard panel goes blank at once with nothing reporting an error,
/// which is indistinguishable from a quiet cluster.
///
/// These tests pin both halves: re-applying the collector moves it back and records where, and until it
/// has, reads stay where the data is.
/// </summary>
public class TelemetryCutoverTests
{
    private const string PublicIngest = "https://entkube.example.com";
    private const string PublicEndpoint = "https://entkube.example.com/ingest/otlp";
    private const string InClusterEndpoint = "http://entkube-telemetry-indexer.monitoring:8080/ingest/otlp";
    private static readonly byte[] TestRootKey =
        Convert.FromBase64String("dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    [Fact]
    public async Task A_collector_still_exporting_to_the_management_plane_keeps_the_reads_here()
    {
        // The exact state after installing the indexer and nothing else: the node exists, the collector
        // has not moved. Routing on the node's existence alone empties every telemetry view.
        Fixture f = Fixture.Build(collectorEndpoint: PublicEndpoint);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_collector_shipping_into_the_cluster_hands_the_reads_to_the_node()
    {
        // The legacy state now, but no less real: until that collector is re-applied it is still writing
        // into the cluster's node, so that is still where its logs and traces are.
        Fixture f = Fixture.Build(collectorEndpoint: InClusterEndpoint);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId)).Should().BeFalse();
    }

    [Fact]
    public async Task An_operator_chosen_destination_is_not_assumed_to_be_the_management_plane()
    {
        // Their own collector hop, or an indexer under a release name of their choosing. Wherever it is,
        // it is not this management plane's store, so reading from that store would find nothing.
        Fixture f = Fixture.Build(collectorEndpoint: "http://otel-hub.observability:4318/v1/logs");

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_placeholder_endpoint_counts_as_not_yet_moved()
    {
        // Nothing is arriving anywhere, so neither store has data — but the pre-cutover answer is the
        // honest one, and the install-time heal is what fixes the placeholder.
        Fixture f = Fixture.Build(collectorEndpoint: TelemetryIngestDefaults.EndpointPlaceholder);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId)).Should().BeTrue();
    }

    [Fact]
    public async Task No_collector_on_the_cluster_draws_no_conclusion()
    {
        // Something may be exporting straight to the node. Null, so the caller keeps its own default
        // rather than inheriting a guess dressed up as an answer.
        Fixture f = Fixture.Build(collectorEndpoint: null, withCollector: false);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId)).Should().BeNull();
    }

    [Fact]
    public async Task A_stranger_draws_no_conclusion_either()
    {
        // Same cluster, same collector, asked by a tenant it does not belong to. Catalog answers a
        // tenant about its own clusters, so there is no collector to find and the answer is the same
        // null as "no collector installed" — deliberately, because the only thing this question is
        // used for is picking a store to read from, and a stranger reads nothing out of either.
        Fixture f = Fixture.Build(collectorEndpoint: PublicEndpoint);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(Guid.NewGuid(), f.ClusterId))
            .Should().BeNull("the cluster is not this tenant's");

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId))
            .Should().BeTrue("and the owner's own answer is unaffected");
    }

    [Fact]
    public async Task Re_applying_a_collector_moves_it_back_and_records_where()
    {
        Fixture f = Fixture.Build(collectorEndpoint: InClusterEndpoint);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId))
            .Should().BeFalse("the cluster starts out holding its own telemetry");

        HelmCommand command = await f.Lifecycle.GetInstallCommandAsync(f.TenantId, f.CollectorId);

        // Rendered into this invocation, so the running collector actually moves...
        command.ValuesYaml.Should().Contain(PublicEndpoint);
        command.ValuesYaml.Should().NotContain(InClusterEndpoint);

        // ...and written back to the component, not merely rendered: the read path decides which store
        // holds the data by reading exactly this value, so a move it cannot see would send every log and
        // trace view to whichever store is empty.
        f.CollectorValues().Should().Contain(PublicEndpoint);
        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.TenantId, f.ClusterId)).Should().BeTrue();
    }

    [Fact]
    public async Task Re_applying_a_collector_leaves_an_operators_own_endpoint_alone()
    {
        Fixture f = Fixture.Build(collectorEndpoint: "http://otel-hub.observability:4318/v1/logs");

        HelmCommand command = await f.Lifecycle.GetInstallCommandAsync(f.TenantId, f.CollectorId);

        // Overwriting their destination would be worse than the stale address this heals: their telemetry
        // would silently start arriving somewhere they did not choose.
        command.ValuesYaml.Should().Contain("otel-hub.observability");
        f.CollectorValues().Should().Contain("otel-hub.observability");
    }

    [Fact]
    public async Task A_stranger_cannot_move_another_tenants_collector()
    {
        Fixture f = Fixture.Build(collectorEndpoint: InClusterEndpoint);

        // The move is a WRITE to the component, so the tenant guard on the way in is what stops a
        // stranger redirecting someone else's telemetry. Refused, and nothing is recorded.
        await FluentActions.Awaiting(() => f.Lifecycle.GetInstallCommandAsync(Guid.NewGuid(), f.CollectorId))
            .Should().ThrowAsync<InvalidOperationException>();

        f.CollectorValues().Should().Contain(InClusterEndpoint);
    }

    /// <summary>A cluster with an installed collector, an installed indexer, and a querier beside them.</summary>
    private sealed record Fixture(
        EntKubeTelemetryService Telemetry,
        ComponentLifecycleService Lifecycle,
        TestDbContextFactory Factory,
        Guid ClusterId,
        Guid IndexerId,
        Guid QuerierId,
        Guid CollectorId,
        Guid TenantId)
    {
        public string CollectorValues()
        {
            using ApplicationDbContext db = Factory.CreateDbContext();
            return db.ClusterComponents.First(c => c.Id == CollectorId).HelmValues ?? "";
        }

        public static Fixture Build(string? collectorEndpoint, bool withCollector = true)
        {
            SqliteConnection connection = new("DataSource=:memory:");
            connection.Open();
            using (ApplicationDbContext seed = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
            {
                seed.Database.EnsureCreated();
            }

            TestDbContextFactory factory = new(connection);
            Guid tenantId = Guid.NewGuid();
            Guid envId = Guid.NewGuid();
            Guid clusterId = Guid.NewGuid();
            Guid indexerId = Guid.NewGuid();
            Guid querierId = Guid.NewGuid();
            Guid collectorId = Guid.NewGuid();

            using (ApplicationDbContext db = factory.CreateDbContext())
            {
                db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
                db.Environments.Add(new Web.Data.Environment { Id = envId, TenantId = tenantId, Name = "prod" });
                db.KubernetesClusters.Add(new KubernetesCluster
                {
                    Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                    Name = "c1", ApiServerUrl = "https://k8s.example.com",
                });

                db.ClusterComponents.Add(new ClusterComponent
                {
                    Id = indexerId, ClusterId = clusterId, Name = EntKubeTelemetryService.IndexerKey,
                    ComponentType = "HelmChart", HelmChartName = EntKubeTelemetryService.ChartName,
                    ReleaseName = "entkube-telemetry", Namespace = "monitoring",
                    Status = ComponentStatus.Installed,
                });

                db.ClusterComponents.Add(new ClusterComponent
                {
                    Id = querierId, ClusterId = clusterId, Name = EntKubeTelemetryService.QuerierKey,
                    ComponentType = "HelmChart", HelmChartName = EntKubeTelemetryService.ChartName,
                    ReleaseName = "entkube-telemetry-query", Namespace = "monitoring",
                    Status = ComponentStatus.Installed,
                });

                if (withCollector)
                {
                    db.ClusterComponents.Add(new ClusterComponent
                    {
                        Id = collectorId, ClusterId = clusterId, Name = TelemetryIngestDefaults.CollectorKey,
                        ComponentType = "HelmChart", HelmChartName = "opentelemetry-collector",
                        ReleaseName = "otel-collector", Namespace = "monitoring",
                        Status = ComponentStatus.Installed,
                        HelmValues = "config:\n  exporters:\n    otlphttp/entkube:\n"
                                     + $"      endpoint: {collectorEndpoint}\n",
                    });
                }

                db.SaveChanges();
            }

            IConfiguration config = TestServices.TestConfiguration(PublicIngest);
            VaultService vault = new(factory, new VaultEncryptionService(TestRootKey), TestServices.NoClusterAccess);
            EntKubeTelemetryService telemetry = new(new EntKube.Web.Modules.Api.DataServicesApi(factory), vault, new IngestTokenService(config),
                new EntKube.Web.Modules.Api.CatalogApi(factory), config);

            return new Fixture(
                telemetry, TestServices.BuildLifecycle(factory, vault, PublicIngest),
                factory, clusterId, indexerId, querierId, collectorId, tenantId);
        }
    }
}

/// <summary>
/// The precedence in <see cref="TelemetryRoute"/>, which decides which store a cluster's logs and traces
/// are read from. Every one of these cases was reachable in production and three of them showed the user
/// an empty view with no error.
/// </summary>
public class TelemetryRouteTests
{
    private static Task<(bool InCluster, string Why)> Decide(
        bool present, bool nodeHasData, bool? mgmtStillReceives)
        => TelemetryRoute.DecideAsync(
            () => Task.FromResult(present),
            () => Task.FromResult(nodeHasData),
            () => Task.FromResult(mgmtStillReceives));

    [Fact]
    public async Task No_node_reads_the_management_plane()
    {
        (await Decide(present: false, nodeHasData: false, mgmtStillReceives: null))
            .InCluster.Should().BeFalse();
    }

    [Fact]
    public async Task A_node_holding_data_wins_over_anything_configuration_says()
    {
        // The case that matters most for clusters cut over by an earlier build: the repoint was rendered
        // into the helm invocation but never recorded, so the stored values still name the management
        // plane. The data is the fact; the stale configuration is not.
        (await Decide(present: true, nodeHasData: true, mgmtStillReceives: true))
            .InCluster.Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_node_whose_collector_has_not_moved_reads_the_management_plane()
    {
        // Exactly the state after installing the indexer and nothing else. Routing to the node here is
        // what blanks every telemetry surface with no error to explain it.
        (bool inCluster, string why) = await Decide(present: true, nodeHasData: false, mgmtStillReceives: true);

        inCluster.Should().BeFalse();
        why.Should().Contain("re-apply the collector", "the operator needs to be told how to finish");
    }

    [Fact]
    public async Task An_empty_node_that_ingest_points_at_keeps_the_reads()
    {
        // Cutover complete, nothing ingested yet. Empty either way — and the node is where the next batch
        // lands, so sending reads to the management plane would only make the emptiness outlast ingest.
        (await Decide(present: true, nodeHasData: false, mgmtStillReceives: false))
            .InCluster.Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_node_on_a_cluster_with_no_collector_keeps_the_reads()
    {
        // Null means "nothing to conclude" — something may export straight to the node — so the node,
        // which is the only telemetry component this cluster has, stays the destination.
        (await Decide(present: true, nodeHasData: false, mgmtStillReceives: null))
            .InCluster.Should().BeTrue();
    }
}
