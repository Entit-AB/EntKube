using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// How the monitors are carried out: in what order a cluster is read, what stops a write, and that
/// nothing is written when there is nothing to write. The decisions themselves are
/// <see cref="ScrapeMonitorPlannerTests"/>.
/// </summary>
public sealed class ScrapeMonitorServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    private readonly ScrapeWorld world = new();

    /// <summary>Everything that was asked or changed, in order.</summary>
    private readonly List<string> log = [];

    private readonly Mock<ICatalogApi> catalog = new(MockBehavior.Strict);
    private readonly Mock<IDataServicesApi> dataServices = new(MockBehavior.Strict);
    private readonly Mock<IClusterClientFactory> clusterClients = new(MockBehavior.Strict);
    private readonly Mock<ILiveScrapeStateReader> reader = new(MockBehavior.Strict);
    private readonly Mock<IClusterClient> cluster = new(MockBehavior.Strict);
    private readonly Mock<IClusterChangeGate> gate = new();
    private readonly MovableClock clock = new();
    private readonly ScrapeMonitorBackoff backoff;

    private InstalledComponent? prometheus;
    private (string Manifest, string Summary)? applied;
    private (string Manifest, string Summary)? deleted;

    public ScrapeMonitorServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using ApplicationDbContext db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = world.TenantId, Name = "ours", Slug = "ours" });
        db.SaveChanges();

        backoff = new ScrapeMonitorBackoff(clock);
        prometheus = world.InstalledComponent(PrometheusPresence.HelmChartName, "monitoring", ScrapeWorld.Release);

        catalog.Setup(c => c.FindPrometheusAsync(world.TenantId, world.ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => prometheus);
        catalog.Setup(c => c.GetComponentsForTenantAsync(world.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => prometheus is null ? [] : [prometheus]);
        catalog.Setup(c => c.GetComponentsForClusterAsync(world.TenantId, world.ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { log.Add("components"); return [.. world.Components]; });

        dataServices.Setup(d => d.GetManagedServicesForTenantAsync(world.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { log.Add("data services"); return [.. world.Services]; });

        clusterClients.Setup(c => c.ForAsync(world.TenantId, world.ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cluster.Object);

        reader.Setup(r => r.ReadMonitoringAsync(world.TenantId, cluster.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { log.Add("monitors"); return world.Monitoring(); });
        reader.Setup(r => r.ReadWorkloadsAsync(cluster.Object, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { log.Add("workloads"); return world.Workloads(); });

        cluster.SetupGet(c => c.ClusterName).Returns("prod-eu");
        cluster.SetupGet(c => c.ClusterId).Returns(world.ClusterId);
        cluster.Setup(c => c.ApplyManifestInNamespaceAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string manifest, string _, string summary, CancellationToken _) =>
            {
                log.Add("apply");
                applied = (manifest, summary);
                world.Apply(world.Plan() with { Prune = [] });
                return "";
            });
        cluster.Setup(c => c.DeleteManifestSetAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string manifest, string _, string summary, CancellationToken _) =>
            {
                log.Add("delete");
                deleted = (manifest, summary);
                world.Monitors.RemoveAll(m => manifest.Contains($"\"{m.Identity.Name}\""));
                return "";
            });
    }

    public void Dispose() => connection.Dispose();

    private ScrapeMonitorService Service(IClusterChangeGate? through = null) => new(
        factory, catalog.Object, dataServices.Object, clusterClients.Object, reader.Object,
        through ?? gate.Object, NullLogger<ScrapeMonitorService>.Instance);

    private Task ReconcileAsync() => Service().ReconcileAllAsync(backoff);

    /// <summary>A database that runs and has no monitor.</summary>
    private void ADatabaseWithoutAMonitor()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
    }

    /// <summary>A monitor of ours whose database is gone.</summary>
    private void AnOrphan() => world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-old");

    private void NothingWasWritten() => log.Should().NotContain("apply").And.NotContain("delete");

    // ── The order ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A database created between the two reads is in the second and not the first. Read the other
    /// way round, its monitor would be live and its row missing, and it would be deleted.
    /// </summary>
    [Fact]
    public async Task The_cluster_is_read_before_the_database_is()
    {
        ADatabaseWithoutAMonitor();

        await ReconcileAsync();

        log.Should().Equal("monitors", "components", "data services", "workloads", "apply");
    }

    [Fact]
    public async Task Only_the_namespaces_something_is_to_be_found_in_are_read()
    {
        ADatabaseWithoutAMonitor();
        world.DataService(ManagedServiceKind.MongoDb, "documents", "archive");
        IReadOnlyCollection<string>? asked = null;
        reader.Setup(r => r.ReadWorkloadsAsync(cluster.Object, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IClusterClient _, IReadOnlyCollection<string> namespaces, CancellationToken _) =>
            {
                asked = namespaces;
                return world.Workloads();
            });

        await ReconcileAsync();

        asked!.Distinct().Should().BeEquivalentTo("monitoring", "databases");
    }

    // ── What is written ───────────────────────────────────────────────────────

    [Fact]
    public async Task What_differs_is_one_apply_and_what_goes_is_one_delete_before_it()
    {
        ADatabaseWithoutAMonitor();
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");
        AnOrphan();
        world.OurMonitor(MonitorKind.ServiceMonitor, "queues", "entkube-other-old");

        await ReconcileAsync();

        log.Skip(4).Should().Equal("delete", "apply");
        applied!.Value.Manifest.Should().Contain("entkube-cnpg-orders").And.Contain("events-metrics").And.NotContain("-old");
        deleted!.Value.Manifest.Should().Contain("entkube-cnpg-old").And.Contain("entkube-other-old").And.NotContain("orders");
    }

    /// <summary>The audit row has no field that says which cluster, so the summary has to.</summary>
    [Fact]
    public async Task The_summary_names_the_cluster_and_the_monitors()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();

        await ReconcileAsync();

        const string said = "Prometheus monitors on cluster prod-eu: apply databases/entkube-cnpg-orders; remove databases/entkube-cnpg-old";
        applied!.Value.Summary.Should().Be(said);
        deleted!.Value.Summary.Should().Be(said);
    }

    [Fact]
    public async Task A_long_list_is_cut_short_and_counted()
    {
        for (int i = 0; i < 12; i++)
        {
            world.DataService(ManagedServiceKind.Postgres, "databases", $"db{i:00}");
            world.CnpgPod($"db{i:00}");
        }

        await ReconcileAsync();

        applied!.Value.Summary.Should().Contain("entkube-cnpg-db09 and 2 more").And.NotContain("db10");
    }

    /// <summary>
    /// Every unattended call through the seam is an audit row nothing deletes: 20 clusters every five
    /// minutes would be two million of them a year.
    /// </summary>
    [Fact]
    public async Task A_cluster_where_everything_is_in_order_is_not_written_to()
    {
        ADatabaseWithoutAMonitor();
        await ReconcileAsync();
        log.Clear();

        await ReconcileAsync();

        log.Should().Equal("monitors", "components", "data services", "workloads");
    }

    [Fact]
    public async Task A_pass_that_only_removes_makes_no_apply()
    {
        AnOrphan();

        await ReconcileAsync();

        log.Skip(4).Should().Equal("delete");
    }

    [Fact]
    public async Task Only_the_monitor_that_differs_is_applied()
    {
        ADatabaseWithoutAMonitor();
        await ReconcileAsync();
        world.DataService(ManagedServiceKind.Postgres, "databases", "billing");
        world.CnpgPod("billing");

        await ReconcileAsync();

        applied!.Value.Manifest.Should().Contain("entkube-cnpg-billing").And.NotContain("orders");
    }

    [Fact]
    public async Task A_monitor_that_has_to_be_made_again_is_deleted_first()
    {
        ADatabaseWithoutAMonitor();
        await ReconcileAsync();
        world.Monitors.Single().Spec["selector"]!["matchLabels"]!["team"] = "payments";
        log.Clear();

        await ReconcileAsync();

        log.Skip(4).Should().Equal("delete", "apply");
        deleted!.Value.Manifest.Should().Contain("entkube-cnpg-orders");
        deleted.Value.Summary.Should().NotContain("remove");
    }

    [Fact]
    public async Task A_delete_that_fails_does_not_keep_the_rest_from_being_applied()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();
        cluster.Setup(c => c.DeleteManifestSetAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("refused"));

        KubernetesOperationResult<ScrapeMonitorPlan> result = await Service().ApplyAsync(world.TenantId, world.ClusterId);

        applied.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("could not be removed").And.Contain("refused");
    }

    // ── What stops a write ────────────────────────────────────────────────────

    [Fact]
    public async Task A_cluster_without_Prometheus_is_not_even_read()
    {
        prometheus = null;
        ADatabaseWithoutAMonitor();
        AnOrphan();

        await ReconcileAsync();
        (await Service().PlanAsync(world.TenantId, world.ClusterId)).Data!.Scope.Should().Be(MonitorPlanScope.NoPrometheus);

        log.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cluster_without_the_monitor_kinds_is_left_alone()
    {
        ADatabaseWithoutAMonitor();
        reader.Setup(r => r.ReadMonitoringAsync(world.TenantId, cluster.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LiveMonitoring?)null);

        await ReconcileAsync();
        (await Service().PlanAsync(world.TenantId, world.ClusterId)).Data!.Scope.Should().Be(MonitorPlanScope.NoMonitorKinds);

        log.Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_of_monitors_that_fails_writes_nothing()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();
        reader.Setup(r => r.ReadMonitoringAsync(world.TenantId, cluster.Object, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("forbidden"));

        await ReconcileAsync();

        NothingWasWritten();
        log.Should().BeEmpty("there is nothing to ask the database for");
    }

    /// <summary>Services and NetworkPolicies among them: a kubeconfig may list pods and not those.</summary>
    [Fact]
    public async Task A_list_of_what_runs_that_fails_writes_nothing()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();
        reader.Setup(r => r.ReadWorkloadsAsync(cluster.Object, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new k8s.Autorest.HttpOperationException("networkpolicies is forbidden"));

        await ReconcileAsync();

        NothingWasWritten();
    }

    [Fact]
    public async Task A_list_of_data_services_that_fails_prunes_nothing()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();
        dataServices.Setup(d => d.GetManagedServicesForTenantAsync(world.TenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the kafka table is locked"));

        await ReconcileAsync();

        NothingWasWritten();
    }

    [Fact]
    public async Task A_list_of_components_that_fails_prunes_nothing()
    {
        AnOrphan();
        catalog.Setup(c => c.GetComponentsForClusterAsync(world.TenantId, world.ClusterId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog"));

        await ReconcileAsync();

        NothingWasWritten();
    }

    [Fact]
    public async Task Another_clusters_databases_are_not_this_ones()
    {
        ADatabaseWithoutAMonitor();
        world.Services[0] = world.Services[0] with { ClusterId = Guid.NewGuid() };

        await ReconcileAsync();

        NothingWasWritten();
    }

    [Fact]
    public async Task A_cluster_with_no_kubeconfig_is_a_failure_and_not_an_empty_plan()
    {
        clusterClients.Setup(c => c.ForAsync(world.TenantId, world.ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IClusterClient?)null);

        (await Service().PlanAsync(world.TenantId, world.ClusterId)).Error.Should().Contain("no kubeconfig");
    }

    // ── Tenants ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_tenant_failing_does_not_end_the_pass_for_the_next()
    {
        // Seen first or second, the other one is reconciled.
        Guid broken = Guid.NewGuid();
        using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.Tenants.Add(new Tenant { Id = broken, Name = "broken", Slug = "broken" });
            db.SaveChanges();
        }
        catalog.Setup(c => c.GetComponentsForTenantAsync(broken, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no"));
        ADatabaseWithoutAMonitor();

        await ReconcileAsync();

        log.Should().Contain("apply");
        catalog.Verify(c => c.GetComponentsForTenantAsync(broken, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task One_cluster_failing_does_not_end_the_pass_for_the_tenants_next()
    {
        Guid unreachable = Guid.NewGuid();
        InstalledComponent theirs = prometheus! with { ClusterId = unreachable };
        catalog.Setup(c => c.GetComponentsForTenantAsync(world.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([theirs, prometheus]);
        catalog.Setup(c => c.FindPrometheusAsync(world.TenantId, unreachable, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no"));
        ADatabaseWithoutAMonitor();

        await ReconcileAsync();

        log.Should().Contain("apply");
    }

    [Fact]
    public async Task A_pass_that_is_told_to_stop_stops()
    {
        using CancellationTokenSource stopping = new();
        reader.Setup(r => r.ReadMonitoringAsync(world.TenantId, cluster.Object, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, IClusterClient _, CancellationToken ct) =>
            {
                await stopping.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return world.Monitoring();
            });

        await FluentActions.Awaiting(() => Service().ReconcileAllAsync(backoff, stopping.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Backing off ───────────────────────────────────────────────────────────

    private void EveryApplyFails()
    {
        cluster.Setup(c => c.ApplyManifestInNamespaceAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("admission webhook denied the request"));
    }

    private void AppliedTimes(int times) => cluster.Verify(c => c.ApplyManifestInNamespaceAsync(
        It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    [Fact]
    public async Task A_cluster_that_refuses_is_asked_again_after_five_minutes_then_ten()
    {
        ADatabaseWithoutAMonitor();
        EveryApplyFails();

        await ReconcileAsync();
        AppliedTimes(1);

        clock.Pass(TimeSpan.FromMinutes(4));
        await ReconcileAsync();
        AppliedTimes(1);

        clock.Pass(TimeSpan.FromMinutes(1));
        await ReconcileAsync();
        AppliedTimes(2);

        clock.Pass(TimeSpan.FromMinutes(9));
        await ReconcileAsync();
        AppliedTimes(2);

        clock.Pass(TimeSpan.FromMinutes(1));
        await ReconcileAsync();
        AppliedTimes(3);
    }

    [Fact]
    public async Task A_different_set_of_changes_is_tried_at_once()
    {
        ADatabaseWithoutAMonitor();
        EveryApplyFails();
        await ReconcileAsync();

        world.DataService(ManagedServiceKind.Postgres, "databases", "billing");
        world.CnpgPod("billing");
        await ReconcileAsync();

        AppliedTimes(2);
    }

    [Fact]
    public void Waiting_never_exceeds_six_hours_and_a_success_forgets_it()
    {
        Guid id = Guid.NewGuid();
        for (int i = 0; i < 20; i++) backoff.Failed(id, "same");

        clock.Pass(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        backoff.MayAttempt(id, "same").Should().BeFalse();
        clock.Pass(TimeSpan.FromSeconds(1));
        backoff.MayAttempt(id, "same").Should().BeTrue();

        backoff.Failed(id, "same");
        backoff.Succeeded(id);
        backoff.MayAttempt(id, "same").Should().BeTrue();
        backoff.Failed(id, "same");
        clock.Pass(TimeSpan.FromMinutes(5));
        backoff.MayAttempt(id, "same").Should().BeTrue("a success starts the waiting over");
    }

    [Fact]
    public void One_clusters_waiting_is_not_anothers()
    {
        backoff.Failed(Guid.NewGuid(), "same");

        backoff.MayAttempt(Guid.NewGuid(), "same").Should().BeTrue();
    }

    [Fact]
    public async Task A_pass_with_nothing_to_write_forgets_a_past_refusal()
    {
        backoff.Failed(world.ClusterId, "before");

        await ReconcileAsync();

        backoff.MayAttempt(world.ClusterId, "before").Should().BeTrue();
    }

    // ── Pressing the button ───────────────────────────────────────────────────

    /// <summary>The operator chose one thing, so the two writes are one question.</summary>
    [Fact]
    public async Task Applying_by_hand_is_one_operation_opened_before_the_first_write()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();
        gate.Setup(g => g.BeginOperation(It.IsAny<string>())).Returns(() =>
        {
            log.Add("operation");
            return Mock.Of<IDisposable>();
        });

        KubernetesOperationResult<ScrapeMonitorPlan> result = await Service().ApplyAsync(world.TenantId, world.ClusterId);

        log.IndexOf("operation").Should().Be(0);
        log.Should().ContainInOrder("delete", "apply");
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data!.HasChanges.Should().BeFalse("the plan returned is the one after the apply");
        result.Data.Rows.Single().Change.Should().Be(MonitorChange.None);
    }

    [Fact]
    public async Task Declining_the_dialog_ends_it()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();
        cluster.Setup(c => c.DeleteManifestSetAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Cluster change cancelled by operator"));

        KubernetesOperationResult<ScrapeMonitorPlan> result = await Service().ApplyAsync(world.TenantId, world.ClusterId);

        result.Error.Should().Be("Cancelled. Nothing was changed.");
        applied.Should().BeNull("the answer was about the whole of it");
    }

    [Fact]
    public async Task Looking_at_the_plan_changes_nothing()
    {
        ADatabaseWithoutAMonitor();
        AnOrphan();

        KubernetesOperationResult<ScrapeMonitorPlan> result = await Service().PlanAsync(world.TenantId, world.ClusterId);

        result.Data!.HasChanges.Should().BeTrue();
        NothingWasWritten();
    }

    [Fact]
    public async Task A_cluster_that_takes_too_long_says_so()
    {
        reader.Setup(r => r.ReadMonitoringAsync(world.TenantId, cluster.Object, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());

        (await Service().PlanAsync(world.TenantId, world.ClusterId)).Error.Should().Be("The cluster did not answer in time.");
    }

    /// <summary>
    /// Through the real gate: the sweep's scope is unattended, so a pass leaves one record for each
    /// of its two writes, and a pass with nothing to do leaves none.
    /// </summary>
    [Fact]
    public async Task What_the_audit_trail_gets_is_a_row_for_each_write_and_none_for_a_quiet_pass()
    {
        Mock<IClusterChangeRecorder> recorder = new();
        ClusterChangeGate real = new(
            new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance, recorder.Object);
        using IDisposable unattended = real.DeclareUnattended("scrape-monitor-reconcile");
        Acknowledged(real);
        ADatabaseWithoutAMonitor();
        AnOrphan();

        await Service(real).ReconcileAllAsync(backoff);
        recorder.Invocations.Should().HaveCount(2);

        await Service(real).ReconcileAllAsync(backoff);
        recorder.Invocations.Should().HaveCount(2);
    }

    /// <summary>The seam as the real factory has it: each write asks the gate first.</summary>
    private void Acknowledged(IClusterChangeGate through)
    {
        PlannedClusterChange Change(ChangeVerb verb, string manifest, string? summary) => new()
        {
            Verb = verb, Manifest = manifest, Kubeconfig = "", Summary = summary,
        };

        cluster.Setup(c => c.ApplyManifestInNamespaceAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string _, string summary, CancellationToken ct) =>
            {
                await through.AcknowledgeAsync(Change(ChangeVerb.Apply, manifest, summary), ct);
                world.Apply(world.Plan() with { Prune = [] });
                return "";
            });
        cluster.Setup(c => c.DeleteManifestSetAsync(It.IsAny<string>(), "", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string _, string summary, CancellationToken ct) =>
            {
                await through.AcknowledgeAsync(Change(ChangeVerb.Delete, manifest, summary), ct);
                world.Monitors.RemoveAll(m => manifest.Contains($"\"{m.Identity.Name}\""));
                return "";
            });
    }

    /// <summary>A clock that moves when it is told to.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Pass(TimeSpan time) => now += time;
    }
}
