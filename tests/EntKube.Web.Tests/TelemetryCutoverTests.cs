using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// The cutover from management-plane telemetry to a cluster's own indexer has two halves that move
/// separately, and that is what makes it dangerous. Installing the indexer moves READS onto it at once,
/// because the read path finds it simply by existing. It does not move WRITES: the collector keeps
/// exporting to the management plane until it is itself re-applied.
///
/// In between, the node is empty and the data is somewhere else — and an empty node answers every query
/// <i>successfully</i>. So every log view, every trace view, every dashboard panel goes blank at once with
/// nothing anywhere reporting an error, which is indistinguishable from a quiet cluster.
///
/// These tests pin both halves: the install moves the collector, and until it has, reads stay where the
/// data is.
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

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.ClusterId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_collector_shipping_into_the_cluster_hands_the_reads_to_the_node()
    {
        Fixture f = Fixture.Build(collectorEndpoint: InClusterEndpoint);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.ClusterId)).Should().BeFalse();
    }

    [Fact]
    public async Task An_operator_chosen_destination_is_not_assumed_to_be_the_management_plane()
    {
        // Their own collector hop, or an indexer under a release name of their choosing. Wherever it is,
        // it is not this management plane's store, so reading from that store would find nothing.
        Fixture f = Fixture.Build(collectorEndpoint: "http://otel-hub.observability:4318/v1/logs");

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.ClusterId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_placeholder_endpoint_counts_as_not_yet_moved()
    {
        // Nothing is arriving anywhere, so neither store has data — but the pre-cutover answer is the
        // honest one, and the install-time heal is what fixes the placeholder.
        Fixture f = Fixture.Build(collectorEndpoint: TelemetryIngestDefaults.EndpointPlaceholder);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.ClusterId)).Should().BeTrue();
    }

    [Fact]
    public async Task No_collector_on_the_cluster_draws_no_conclusion()
    {
        // Something may be exporting straight to the node. Null, so the caller keeps its own default
        // rather than inheriting a guess dressed up as an answer.
        Fixture f = Fixture.Build(collectorEndpoint: null, withCollector: false);

        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.ClusterId)).Should().BeNull();
    }

    [Fact]
    public async Task Installing_the_indexer_repoints_the_collector_and_records_it()
    {
        Fixture f = Fixture.Build(collectorEndpoint: PublicEndpoint);

        Guid? toReapply = await f.Lifecycle.RepointCollectorToInClusterAsync(f.IndexerId);

        toReapply.Should().Be(f.CollectorId, "the collector is what has to be re-applied to move ingest");

        // Written back to the component, not merely rendered into one helm invocation: the read path
        // decides which store holds the data by reading exactly this value.
        f.CollectorValues().Should().Contain(InClusterEndpoint);
        (await f.Telemetry.ManagementPlaneStillReceivesAsync(f.ClusterId)).Should().BeFalse();
    }

    [Fact]
    public async Task Installing_the_indexer_leaves_an_operators_own_endpoint_alone()
    {
        Fixture f = Fixture.Build(collectorEndpoint: "http://otel-hub.observability:4318/v1/logs");

        (await f.Lifecycle.RepointCollectorToInClusterAsync(f.IndexerId))
            .Should().BeNull("re-applying their collector to overwrite their destination would be worse "
                             + "than the bug this fixes");

        f.CollectorValues().Should().Contain("otel-hub.observability");
    }

    [Fact]
    public async Task Installing_something_else_does_not_touch_the_collector()
    {
        Fixture f = Fixture.Build(collectorEndpoint: PublicEndpoint);

        (await f.Lifecycle.RepointCollectorToInClusterAsync(f.QuerierId)).Should().BeNull();
        f.CollectorValues().Should().Contain(PublicEndpoint);
    }

    /// <summary>A cluster with an installed collector, an installed indexer, and a querier beside them.</summary>
    private sealed record Fixture(
        EntKubeTelemetryService Telemetry,
        ComponentLifecycleService Lifecycle,
        TestDbContextFactory Factory,
        Guid ClusterId,
        Guid IndexerId,
        Guid QuerierId,
        Guid CollectorId)
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
            VaultService vault = new(factory, new VaultEncryptionService(TestRootKey));
            EntKubeTelemetryService telemetry = new(factory, vault, new IngestTokenService(config), config);

            return new Fixture(
                telemetry, TestServices.BuildLifecycle(factory, vault, PublicIngest),
                factory, clusterId, indexerId, querierId, collectorId);
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
