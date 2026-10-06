using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Reaching a cluster without being handed its credentials.
///
/// <para>The point of <see cref="IClusterClientFactory"/> is that the kubeconfig is resolved
/// in one place — inside Fleet — rather than by each of the 508 callers that currently load a
/// cluster row to take the credential out of it. These check the two things that makes it
/// safe: that the resolution is tenant-scoped, and that the client really does carry the
/// credential through to the underlying call so nothing is silently not applied.</para>
/// </summary>
public class ClusterClientFactoryTests : IDisposable
{
    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(RootKey);
    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourCluster, theirCluster, bareCluster;

    public ClusterClientFactoryTests()
    {
        VaultService vault = testDb.CreateVaultService();

        using ApplicationDbContext db = testDb.CreateContext();

        // Added once, before any Seed call: an Any() check would query the database and miss
        // a tenant that is only pending in the change tracker.
        db.Tenants.Add(new Tenant { Id = ourTenant, Name = "ours", Slug = $"ours-{ourTenant:N}" });
        db.Tenants.Add(new Tenant { Id = theirTenant, Name = "theirs", Slug = $"theirs-{theirTenant:N}" });

        ourCluster = Seed(db, ourTenant, "ours");
        theirCluster = Seed(db, theirTenant, "theirs");
        bareCluster = Seed(db, ourTenant, "bare");
        db.SaveChanges();

        testDb.SeedKubeconfigAsync(vault, ourTenant, ourCluster, TestKubeconfig.Valid).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, theirTenant, theirCluster, TestKubeconfig.Valid).GetAwaiter().GetResult();
        // bareCluster deliberately gets none.
    }

    private static Guid Seed(ApplicationDbContext db, Guid tenantId, string label)
    {
        Guid environmentId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = label });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });

        return clusterId;
    }

    public void Dispose()
    {
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private (IClusterClientFactory Factory, Mock<IKubernetesClientFactory> Inner) Build()
    {
        Mock<IKubernetesClientFactory> inner = new();
        return (new ClusterClientFactory(testDb.Factory, inner.Object), inner);
    }

    [Fact]
    public async Task A_cluster_of_this_tenant_resolves()
    {
        (IClusterClientFactory factory, _) = Build();

        IClusterClient? client = await factory.ForAsync(ourTenant, ourCluster);

        client.Should().NotBeNull();
        client!.ClusterId.Should().Be(ourCluster);
        client.ClusterName.Should().Be("ours-cluster");
    }

    /// <summary>
    /// The whole reason to centralise this. A caller that passed the wrong tenant used to get
    /// a cluster anyway, because the filter was its own to remember.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_does_not_resolve()
    {
        (IClusterClientFactory factory, _) = Build();

        (await factory.ForAsync(ourTenant, theirCluster)).Should().BeNull();
        (await factory.ForAsync(theirTenant, ourCluster)).Should().BeNull();
    }

    /// <summary>
    /// A cluster with no stored kubeconfig is null here rather than a <c>.Kubeconfig!</c> that
    /// throws somewhere further in, naming nothing useful.
    /// </summary>
    [Fact]
    public async Task A_cluster_with_no_kubeconfig_does_not_resolve()
    {
        (IClusterClientFactory factory, _) = Build();

        (await factory.ForAsync(ourTenant, bareCluster)).Should().BeNull();
    }

    /// <summary>
    /// The typed-SDK capability is what lets the ten services that build their own
    /// <c>Kubernetes</c> client stop handling a credential. If it produced a client that could
    /// not actually reach anything, those conversions would look fine and fail in production.
    /// </summary>
    [Fact]
    public async Task A_client_can_hand_out_a_configured_sdk_client()
    {
        (IClusterClientFactory factory, _) = Build();

        IClusterClient cluster = (await factory.ForAsync(ourTenant, ourCluster))!;

        using k8s.Kubernetes sdk = cluster.CreateSdkClient();

        sdk.Should().NotBeNull();
        sdk.BaseUri.Should().NotBeNull(
            "a client built from the seeded kubeconfig should be pointed at that cluster's API server");
        sdk.BaseUri!.ToString().Should().StartWith("https://k8s.example.com",
            "the server in TestKubeconfig.Valid — proof the credential was read, not merely held");
    }

    [Fact]
    public async Task The_resolved_credential_reaches_the_underlying_call()
    {
        (IClusterClientFactory factory, Mock<IKubernetesClientFactory> inner) = Build();

        IClusterClient client = (await factory.ForAsync(ourTenant, ourCluster))!;
        await client.ApplyManifestAsync("kind: Namespace", CancellationToken.None);
        await client.EnsureNamespaceAsync("demo", CancellationToken.None);

        // The credential the caller never saw is the one the cluster is reached with.
        inner.Verify(i => i.ApplyManifestAsync("kind: Namespace", TestKubeconfig.Valid, It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(i => i.EnsureNamespaceAsync("demo", TestKubeconfig.Valid, It.IsAny<CancellationToken>()), Times.Once);
    }
}
