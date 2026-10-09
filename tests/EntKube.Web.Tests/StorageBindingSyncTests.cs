using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Agents;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Pushing a storage binding's credentials into a deployment's namespace.
///
/// <para><b>Why this one.</b> It is the only one of <c>StorageService</c>'s three conversions whose
/// behaviour a test can see — the other two reach the cluster through the Kubernetes SDK, which
/// means a real connection or nothing. It is also the one that turned out to have a tenant hole:
/// the binding was fetched by id alone, and the <c>tenantId</c> argument was used only for the
/// vault reads, so a binding id from another tenant resolved and had a Secret written into its
/// deployment's namespace.</para>
///
/// <para>Asking the seam for the credential closes that on its own — <c>ForAsync</c> returns null
/// for "not this tenant's" — but the message would then be about a missing kubeconfig rather than
/// about whose binding it is, so the query is scoped as well and this pins both.</para>
/// </summary>
public class StorageBindingSyncTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly StorageService sut;

    private readonly List<string> applied = [];
    private readonly List<string> namespaces = [];

    private Guid ours, theirs, ourBindingId, theirBindingId;

    public StorageBindingSyncTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, CancellationToken _) => applied.Add(m))
            .ReturnsAsync("applied");

        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string ns, string _, CancellationToken _) => namespaces.Add(ns))
            .Returns(Task.CompletedTask);

        Mock<IHttpClientFactory> http = new();
        AgentRegistry agents = new(testDb.Factory, NullLogger<AgentRegistry>.Instance);
        OpenStackHttpFactory httpFactory = new(http.Object, agents);
        ClusterEgressRelay relay = new(k8s.Object, NullLogger<ClusterEgressRelay>.Instance);
        ClusterEgressTunnel tunnel = new(NullLogger<ClusterEgressTunnel>.Instance);
        OpenStackKeystoneClient keystone = new(httpFactory, vault, tunnel, testDb.Factory);
        OpenStackS3Service s3 = new(vault, httpFactory, keystone);
        StorageLinkClientFactory clients = new(vault, testDb.Factory, httpFactory, keystone);

        sut = new StorageService(
            testDb.Factory, vault, s3, keystone, relay, tunnel, agents, k8s.Object,
            new ClusterClientFactory(testDb.Factory, k8s.Object), clients);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Two tenants, each with a cluster, a deployment and a storage binding on it.</summary>
    private async Task SeedAsync()
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();

        ourBindingId = await SeedTenantAsync(ours, "ours");
        theirBindingId = await SeedTenantAsync(theirs, "theirs");
    }

    private async Task<Guid> SeedTenantAsync(Guid tenantId, string label, bool withKubeconfig = true)
    {
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();
        Guid deploymentId = Guid.NewGuid();
        Guid linkId = Guid.NewGuid();
        Guid bindingId = Guid.NewGuid();

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

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label} customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"{label}-app" });
        await db.SaveChangesAsync();
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = $"{label}-deploy",
            Type = DeploymentType.Manual, ClusterId = clusterId, EnvironmentId = envId,
            Namespace = $"{label}-ns",
        });
        await db.SaveChangesAsync();

        db.Set<StorageLink>().Add(new StorageLink
        {
            Id = linkId, TenantId = tenantId, EnvironmentId = envId, Name = $"{label}-bucket",
            Provider = StorageProvider.MinIO,
            Endpoint = "http://minio.minio.svc.cluster.local:9000",
            BucketName = $"{label}-bucket",
        });
        await db.SaveChangesAsync();

        db.Set<StorageBinding>().Add(new StorageBinding
        {
            Id = bindingId, StorageLinkId = linkId, AppDeploymentId = deploymentId,
            KubernetesSecretName = $"{label}-storage",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
        await vault.SetStorageLinkSecretAsync(tenantId, linkId, "ACCESS_KEY", $"AK-{label}", default);
        await vault.SetStorageLinkSecretAsync(tenantId, linkId, "SECRET_KEY", $"SK-{label}", default);

        return bindingId;
    }

    [Fact]
    public async Task A_binding_syncs_its_credentials_into_its_own_namespace()
    {
        await SeedAsync();

        await sut.SyncStorageBindingAsync(ours, ourBindingId);

        namespaces.Should().ContainSingle().Which.Should().Be("ours-ns");

        string manifest = applied.Should().ContainSingle().Subject;
        manifest.Should().Contain("name: ours-storage").And.Contain("namespace: ours-ns");

        // The values are base64 in the manifest, which is what makes an eyeball check useless and
        // this assertion worth making: the wrong tenant's keys would look identical.
        manifest.Should().Contain(Convert.ToBase64String("AK-ours"u8.ToArray()));
        manifest.Should().Contain(Convert.ToBase64String("SK-ours"u8.ToArray()));
    }

    /// <summary>
    /// The hole this conversion closed. A Secret written into another tenant's namespace is not
    /// only a disclosure of our credentials to them — it is a write into a cluster we were never
    /// entitled to touch.
    /// </summary>
    [Fact]
    public async Task Another_tenants_binding_is_refused_and_nothing_reaches_a_cluster()
    {
        await SeedAsync();

        Func<Task> act = () => sut.SyncStorageBindingAsync(ours, theirBindingId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binding not found*");

        applied.Should().BeEmpty();
        namespaces.Should().BeEmpty("not even the namespace may be created");
    }

    [Fact]
    public async Task The_owning_tenant_can_still_sync_the_same_binding()
    {
        await SeedAsync();

        await sut.SyncStorageBindingAsync(theirs, theirBindingId);

        applied.Should().ContainSingle().Which.Should().Contain("namespace: theirs-ns");
    }

    /// <summary>
    /// A cluster whose kubeconfig has not been stored. The seam returns null for this and for
    /// "not this tenant's" alike, so the refusal has to name the one the operator can act on.
    /// </summary>
    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_refused_before_the_namespace()
    {
        await SeedAsync();

        Guid bare = Guid.NewGuid();
        Guid bareBindingId = await SeedTenantAsync(bare, "bare", withKubeconfig: false);

        Func<Task> act = () => sut.SyncStorageBindingAsync(bare, bareBindingId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no kubeconfig*");

        namespaces.Should().BeEmpty();
        applied.Should().BeEmpty();
    }
}
