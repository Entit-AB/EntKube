using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Jit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose cluster a JIT grant is provisioned on.
///
/// <para><b>This is the check that was missing entirely.</b> The provisioner loaded its cluster by
/// id alone — there was no tenant anywhere in the file — so a grant row naming any cluster in the
/// installation was provisioned on it. For the one feature whose purpose is handing out cluster
/// access, and whose teardown <em>removes</em> access, that is the worst place for it to be
/// missing.</para>
///
/// <para>The reaper's own tests use a fake provisioner, so they cannot see this: the refusal lives
/// in the real one. Hence a separate file.</para>
/// </summary>
public class JitProvisionerCustodyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly KubernetesJitProvisioner sut;

    private readonly List<string> applied = [];
    private readonly List<(string Kind, string Name)> deleted = [];

    private Guid tenantId;
    private Guid clusterId;

    public JitProvisionerCustodyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, string _2, string? _3, CancellationToken _4) => applied.Add(m))
            .ReturnsAsync("serviceaccount/jit created");

        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string kind, string name, string _, string _2, CancellationToken _3)
                => deleted.Add((kind, name)))
            .Returns(Task.CompletedTask);

        k8s.Setup(x => x.RequestServiceAccountTokenAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceAccountToken("bound-token", DateTime.UtcNow.AddHours(1)));

        sut = new KubernetesJitProvisioner(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new NoAckGate(),
            new VaultEncryptionService(TestRootKey),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<KubernetesJitProvisioner>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<JitGrant> SeedGrantAsync()
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = $"acme-{tenantId:N}" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Acme" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "primary", ApiServerUrl = "https://c:6443",
        });
        db.Users.Add(new ApplicationUser
        {
            Id = "customer@acme.example", UserName = "customer@acme.example",
        });
        await db.SaveChangesAsync();

        JitGrant grant = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId, CustomerId = customerId, AppId = appId,
            EnvironmentId = envId, KubernetesClusterId = clusterId,
            Namespace = "acme-billing",
            UserId = "customer@acme.example",
            Reason = "debugging",
            RequestedBy = "customer@acme.example",
            ApprovedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Level = JitAccessLevel.Observe,
        };
        db.Set<JitGrant>().Add(grant);
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        return grant;
    }

    [Fact]
    public async Task A_grant_is_minted_on_its_own_tenants_cluster()
    {
        JitGrant grant = await SeedGrantAsync();

        MintedCredential credential = await sut.MintAsync(tenantId, grant, TimeSpan.FromHours(1));

        credential.Plaintext.Should().StartWith(KubernetesJitProvisioner.TokenPrefix);
        credential.TokenExpiresAt.Should().NotBeNull(
            "the API server's own expiry is recorded, not the requested window");
        applied.Should().ContainSingle().Which.Should().Contain("ServiceAccount");
    }

    /// <summary>
    /// Minting as a tenant that does not own the cluster must reach no cluster at all. A grant row
    /// naming a foreign cluster is exactly how this would happen.
    /// </summary>
    [Fact]
    public async Task Minting_on_another_tenants_cluster_is_refused()
    {
        JitGrant grant = await SeedGrantAsync();
        Guid stranger = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.MintAsync(stranger, grant, TimeSpan.FromHours(1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");

        applied.Should().BeEmpty("RBAC granting cluster access must not be applied for a stranger");
    }

    /// <summary>
    /// Teardown is the dangerous direction: it deletes the RoleBinding that is the access. A
    /// stranger must not be able to remove another tenant's grant objects.
    /// </summary>
    [Fact]
    public async Task Tearing_down_another_tenants_grant_deletes_nothing()
    {
        JitGrant grant = await SeedGrantAsync();
        Guid stranger = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        await sut.TearDownAsync(stranger, grant);

        deleted.Should().BeEmpty("deleting the RoleBinding is what removes the access");
    }

    [Fact]
    public async Task Tearing_down_its_own_grant_removes_the_binding_first()
    {
        JitGrant grant = await SeedGrantAsync();

        await sut.TearDownAsync(tenantId, grant);

        deleted.Select(d => d.Kind).Should().Equal(["rolebinding", "role", "serviceaccount"],
            "the binding is what grants, so it goes first — the rest is tidying");
    }

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
