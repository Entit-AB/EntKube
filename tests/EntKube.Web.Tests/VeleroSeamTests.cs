using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Dr;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What Velero's DR paths ask of a cluster, now that they ask <see cref="IClusterClient"/>.
///
/// <para><b>What the conversion actually removed.</b> <c>VeleroService</c> had a private
/// <c>ResolveKubeconfigAsync</c> that ran <c>c.Id == clusterId &amp;&amp; c.TenantId == tenantId</c>
/// and returned the credential — the same query <c>ClusterClientFactory.ForAsync</c> runs, with
/// the same predicate, differing only in handing back a string instead of a client. Three write
/// paths called it and <c>GetStatusAsync</c> took a <c>string? kubeconfig</c> from a projection.
/// So this was custody, not authorization: the refusals below all held before.</para>
///
/// <para><b>Which is exactly why they are worth asserting.</b> A conversion whose point is "no
/// behaviour change" has nothing to show for itself unless the behaviour is pinned, and the way
/// this one could have gone wrong is by widening — <c>ForAsync(cluster.TenantId, cluster.Id)</c>
/// would have compiled, passed every existing test, and quietly dropped the tenant check. (See
/// <c>VacuousTenantCheckTests</c>: nineteen sites in the codebase did land that way.)</para>
/// </summary>
public class VeleroSeamTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly VeleroService sut;

    /// <summary>Every manifest applied, so a refusal can be told from a silent success.</summary>
    private readonly List<string> applied = [];

    private Guid ours, theirs, ourCluster, theirCluster;

    public VeleroSeamTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, CancellationToken _) => applied.Add(m))
            .ReturnsAsync("applied");

        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        sut = new VeleroService(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            null!,
            new EntKube.Web.Modules.Api.DataServicesApi(testDb.Factory),
            vault,
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<VeleroService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(bool theirsHasKubeconfig = true)
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();
        ourCluster = await SeedTenantAsync(ours, "ours", true);
        theirCluster = await SeedTenantAsync(theirs, "theirs", theirsHasKubeconfig);
    }

    private async Task<Guid> SeedTenantAsync(Guid tenantId, string label, bool withKubeconfig)
    {
        Guid clusterId = Guid.NewGuid(), envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }

        return clusterId;
    }

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_schedule_is_applied_to_our_own_cluster()
    {
        await SeedAsync();

        (bool ok, string message) = await sut.SaveScheduleAsync(
            ours, ourCluster, "nightly", "0 2 * * *", 30, null, true, null);

        ok.Should().BeTrue(message);
        applied.Should().ContainSingle()
            .Which.Should().Contain("kind: Schedule").And.Contain("nightly");
    }

    /// <summary>
    /// The refusal that must survive. A tenant asking for a backup schedule on a cluster that is
    /// not theirs gets nothing applied — and the assertion is on <c>applied</c>, not only on the
    /// returned flag, because a conversion that reached the cluster and then reported failure
    /// would still have written to someone else's cluster.
    /// </summary>
    [Fact]
    public async Task A_schedule_is_refused_on_another_tenants_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.SaveScheduleAsync(
            ours, theirCluster, "nightly", "0 2 * * *", 30, null, true, null);

        ok.Should().BeFalse();
        applied.Should().BeEmpty("nothing may reach a cluster this tenant does not own");
    }

    [Fact]
    public async Task A_one_off_backup_is_refused_on_another_tenants_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.CreateBackupAsync(ours, theirCluster, "adhoc", null, 7, null);

        ok.Should().BeFalse();
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_schedule_is_refused_on_another_tenants_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.DeleteScheduleAsync(ours, theirCluster, "nightly");

        ok.Should().BeFalse();
        k8s.Verify(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A cluster with no stored credential comes back as a status carrying the reason, not as a
    /// thrown exception and not as a silently absent row — one unreachable cluster must not remove
    /// itself from a fleet-wide DR report, because an absent cluster reads as "nothing to worry
    /// about here".
    /// </summary>
    [Fact]
    public async Task A_cluster_with_no_credential_is_reported_not_omitted()
    {
        await SeedAsync();

        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            // A second cluster for the same tenant, with nothing in the vault for it.
            write.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = Guid.NewGuid(), TenantId = ours,
                EnvironmentId = write.Set<Data.Environment>().First(e => e.TenantId == ours).Id,
                Name = "no-creds-cluster", ApiServerUrl = "https://k8s.example.com",
            });
            await write.SaveChangesAsync();
        }

        List<ClusterDrStatus> statuses = await sut.GetTenantStatusAsync(ours);

        statuses.Should().HaveCount(2);

        ClusterDrStatus unreachable = statuses.Single(s => s.ClusterName == "no-creds-cluster");
        unreachable.IsVeleroInstalled.Should().BeFalse();
        unreachable.Error.Should().NotBeNullOrWhiteSpace();
        unreachable.ClusterName.Should().Be("no-creds-cluster",
            "the name is passed in rather than read from the cluster, because when the seam "
            + "refuses there is no cluster to read it from");
    }

    /// <summary>
    /// And the report covers this tenant only. The sweep's cluster list is the thing that scopes
    /// it, so a conversion that resolved each cluster by id alone would still have produced a
    /// correct-looking report here — which is why the refusal tests above carry the weight.
    /// </summary>
    [Fact]
    public async Task The_fleet_report_covers_one_tenant()
    {
        await SeedAsync();

        (await sut.GetTenantStatusAsync(ours))
            .Should().ContainSingle().Which.ClusterName.Should().Be("ours-cluster");
    }
}
