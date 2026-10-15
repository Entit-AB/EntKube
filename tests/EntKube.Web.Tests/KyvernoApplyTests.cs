using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What applying Kyverno policies sends to a cluster, and whose cluster it is willing to send to.
///
/// <para><b>Why this is worth pinning.</b> Both entry points used to take a
/// <c>KubernetesCluster</c> and read <c>cluster.Kubeconfig</c> off the row they were handed, so the
/// caller's choice of entity <em>was</em> the authorization. These are admission policies: aimed at
/// the wrong cluster they either enforce one tenant's deny-list on another's workloads, or remove a
/// ClusterPolicy that was the only thing covering them. Deployment tests cover the rendering;
/// nothing covered who the apply acts as.</para>
/// </summary>
public class KyvernoApplyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly KyvernoPolicyService sut;

    private readonly List<(string Manifest, string Namespace, string? Summary)> applied = [];
    private readonly List<(string Kind, string Name, string Namespace)> deleted = [];

    public KyvernoApplyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string ns, string _, string? s, CancellationToken _) => applied.Add((m, ns, s)))
            .ReturnsAsync("policy.kyverno.io/require-labels configured\n");

        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string kind, string name, string ns, string _, CancellationToken _)
                => deleted.Add((kind, name, ns)))
            .Returns(Task.CompletedTask);

        sut = new KyvernoPolicyService(
            testDb.Factory,
            k8s.Object,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new NoAckGate(),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<KyvernoPolicyService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid tenantId;
    private Guid environmentId;

    private async Task<KubernetesCluster> SeedClusterAsync(bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        environmentId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "production",
        });
        KubernetesCluster cluster = new()
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }

        return cluster;
    }

    private static List<KyvernoPolicy> OnePolicy() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            PolicyType = KyvernoPolicyType.DisallowPrivilegedContainers,
            ValidationFailureAction = KyvernoValidationFailureAction.Audit,
        },
    ];

    [Fact]
    public async Task The_policies_land_in_the_apps_namespace()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeTrue(output);

        (string manifest, string ns, string? summary) = applied.Should().ContainSingle().Subject;

        ns.Should().Be("billing-prod");
        summary.Should().Be("Apply Kyverno policies to billing-prod");
        manifest.Should().Contain("kind: Policy");
    }

    /// <summary>
    /// The case the old signature could not express. The cluster belongs to somebody else, and the
    /// caller names it anyway — which is what a deployment row pointing at a foreign cluster would
    /// do. Nothing may reach that cluster.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_is_refused_and_nothing_is_applied()
    {
        KubernetesCluster theirs = await SeedClusterAsync();
        Guid stranger = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"stranger-{stranger:N}" });
        await db.SaveChangesAsync();
        await vault.InitializeVaultAsync(stranger);

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), stranger, theirs.Id, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        applied.Should().BeEmpty("an admission policy applied to a cluster the asker does not own "
            + "enforces their rules on somebody else's workloads");
    }

    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_refused_without_a_cluster_call()
    {
        KubernetesCluster cluster = await SeedClusterAsync(withKubeconfig: false);

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("no kubeconfig");
        applied.Should().BeEmpty();
    }

    /// <summary>
    /// With nothing enabled the cluster-scoped policy must be <em>removed</em>, not left behind —
    /// and the removal is as tenant-scoped as the apply. A stranger must not be able to delete the
    /// ClusterPolicy that is covering this tenant's namespaces.
    /// </summary>
    [Fact]
    public async Task Reconcile_removes_the_cluster_policy_when_nothing_is_enabled()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        (bool success, _) = await sut.ReconcileClusterRbacPolicyAsync(tenantId, cluster.Id);

        success.Should().BeTrue();
        (string kind, string name, string ns) = deleted.Should().ContainSingle().Subject;
        kind.Should().Be("clusterpolicy");
        ns.Should().BeEmpty("a ClusterPolicy is cluster-scoped");
        name.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reconcile_for_another_tenants_cluster_deletes_nothing()
    {
        KubernetesCluster theirs = await SeedClusterAsync();
        Guid stranger = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"stranger-{stranger:N}" });
        await db.SaveChangesAsync();

        (bool success, string output) = await sut.ReconcileClusterRbacPolicyAsync(stranger, theirs.Id);

        success.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        deleted.Should().BeEmpty("removing a ClusterPolicy uncovers every namespace it protected");
    }

    /// <summary>A gate that asks nobody, for tests that are not about the gate.</summary>
    private sealed class NoAckGate : EntKube.Web.Services.ClusterChanges.IClusterChangeGate
    {
        public Task AcknowledgeAsync(
            EntKube.Web.Services.ClusterChanges.PlannedClusterChange change,
            CancellationToken ct = default) => Task.CompletedTask;

        public IDisposable RegisterSink(EntKube.Web.Services.ClusterChanges.IClusterChangeAckSink sink)
            => new Nothing();

        public IDisposable DeclareUnattended(string reason) => new Nothing();

        public IDisposable DeclareConsequence(string reason) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
