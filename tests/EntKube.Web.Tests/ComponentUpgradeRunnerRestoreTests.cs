using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What an upgrade that failed leaves behind. The runner pins the target version before helm runs, so a
/// refused upgrade used to leave the component pinned to the version that was refused: Install then
/// repeated the refusal, with no way back from the page.
/// </summary>
public class ComponentUpgradeRunnerRestoreTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly ComponentLifecycleService lifecycle;
    private readonly Mock<IClusterClient> cluster = new();
    private readonly ComponentUpgradeRunner sut;
    private readonly Queue<string> revisions = new();

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid componentId = Guid.NewGuid();

    public ComponentUpgradeRunnerRestoreTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        lifecycle = TestServices.BuildLifecycle(testDb, new Mock<IKubernetesClientFactory>().Object);

        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = "prod", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "kyverno", ComponentType = "HelmChart",
            HelmChartName = "kyverno", HelmChartVersion = "3.8.1", ReleaseName = "kyverno", Namespace = "kyverno",
            Status = ComponentStatus.Installed, InstalledAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();

        cluster.Setup(c => c.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");
        cluster.Setup(c => c.GetJsonAsync("secrets", "kyverno", "owner=helm,name=kyverno", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => revisions.Dequeue());

        Mock<IClusterClientFactory> clients = new();
        clients.Setup(c => c.ForAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cluster.Object);

        // No orchestrator: calling it throws, which is an upgrade that failed before changing anything.
        sut = new ComponentUpgradeRunner(
            testDb.Factory, lifecycle, null!,
            new ReleaseVolumeGuard(testDb.Factory, clients.Object, TestServices.NewGate(), NullLogger<ReleaseVolumeGuard>.Instance),
            clients.Object, NullLogger<ComponentUpgradeRunner>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Revisions(params int[] versions) =>
        "{\"items\":[" + string.Join(",", versions.Select(v => "{\"metadata\":{\"labels\":{\"version\":\"" + v + "\"}}}")) + "]}";

    private Task<ClusterComponent> Stored() =>
        db.ClusterComponents.AsNoTracking().FirstAsync(c => c.Id == componentId);

    private Task<ComponentUpgradeOutcome> UpgradeAsync() => sut.UpgradeAsync(new ComponentUpgradeRequest
    {
        TenantId = tenantId, ComponentId = componentId, TargetChartVersion = "3.10.0", AllowScaleDown = true,
    });

    [Fact]
    public async Task An_upgrade_that_changed_nothing_leaves_the_component_as_it_was()
    {
        // Ten sorts before nine as text, and revisions are numbers.
        revisions.Enqueue(Revisions(9, 10));
        revisions.Enqueue(Revisions(9, 10));

        ComponentUpgradeOutcome outcome = await UpgradeAsync();

        outcome.Success.Should().BeFalse();
        outcome.AppliedVersion.Should().Be("3.8.1");
        outcome.Output.Should().Contain("still running 3.8.1");

        ClusterComponent stored = await Stored();
        stored.HelmChartVersion.Should().Be("3.8.1");
        stored.Status.Should().Be(ComponentStatus.Installed);
        stored.LastError.Should().NotBeNullOrEmpty("the operator still has to see why");
    }

    /// <summary>The release may be half-way to the new chart. Re-pinning the old one would make Apply a downgrade.</summary>
    [Fact]
    public async Task An_upgrade_that_reached_the_release_keeps_its_version()
    {
        revisions.Enqueue(Revisions(9, 10));
        revisions.Enqueue(Revisions(9, 10, 11));

        await UpgradeAsync();

        (await Stored()).HelmChartVersion.Should().Be("3.10.0");
    }

    [Fact]
    public async Task A_release_that_cannot_be_read_is_not_assumed_unchanged()
    {
        revisions.Enqueue("not json");
        revisions.Enqueue("not json");

        await UpgradeAsync();

        (await Stored()).HelmChartVersion.Should().Be("3.10.0");
    }

    [Fact]
    public async Task Restoring_returns_a_failed_component_to_the_version_it_runs()
    {
        await lifecycle.UpdateConfigurationAsync(tenantId, componentId, helmValues: null, chartVersion: "3.10.0");
        await lifecycle.MarkInstallResultAsync(tenantId, componentId, success: false, error: "refused");

        await lifecycle.RestoreAfterUnstartedUpgradeAsync(tenantId, componentId, "3.8.1", "refused");

        ClusterComponent stored = await Stored();
        (stored.Status, stored.HelmChartVersion, stored.LastError).Should().Be((ComponentStatus.Installed, "3.8.1", "refused"));

        Func<Task> stranger = () => lifecycle.RestoreAfterUnstartedUpgradeAsync(Guid.NewGuid(), componentId, "1", null);
        await stranger.Should().ThrowAsync<InvalidOperationException>();
    }
}
