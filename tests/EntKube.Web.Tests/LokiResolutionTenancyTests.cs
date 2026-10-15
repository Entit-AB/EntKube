using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using EntKube.Web.Services.Agents;

namespace EntKube.Web.Tests;

/// <summary>
/// Who may be told about, and reach, a cluster's Loki.
///
/// <para>Every query method here resolved its cluster with <c>c.Id == clusterId</c> and no tenant
/// predicate, then read the credential off the row — so a cluster id alone answered about any
/// cluster in the installation, through a log viewer. The ids came from
/// <c>LogQueryService</c>, which carried a <c>tenantId</c> on all seven methods and had nowhere
/// to pass it.</para>
/// </summary>
public class LokiResolutionTenancyTests : IDisposable
{
    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(RootKey);
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourCluster, theirCluster, bareCluster;

    public LokiResolutionTenancyTests()
    {
        VaultService vault = testDb.CreateVaultService();
        using ApplicationDbContext db = testDb.CreateContext();

        db.Tenants.Add(new Tenant { Id = ourTenant, Name = "ours", Slug = $"ours-{ourTenant:N}" });
        db.Tenants.Add(new Tenant { Id = theirTenant, Name = "theirs", Slug = $"theirs-{theirTenant:N}" });

        ourCluster = SeedCluster(db, ourTenant, "ours", withLoki: true);
        theirCluster = SeedCluster(db, theirTenant, "theirs", withLoki: true);
        bareCluster = SeedCluster(db, ourTenant, "bare", withLoki: false);
        db.SaveChanges();

        testDb.SeedKubeconfigAsync(vault, ourTenant, ourCluster, TestKubeconfig.Valid).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, theirTenant, theirCluster, TestKubeconfig.Valid).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, ourTenant, bareCluster, TestKubeconfig.Valid).GetAwaiter().GetResult();
    }

    private static Guid SeedCluster(ApplicationDbContext db, Guid tenantId, string label, bool withLoki)
    {
        Guid environmentId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = label });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });

        if (withLoki)
        {
            db.ClusterComponents.Add(new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = clusterId, Name = "loki",
                HelmChartName = "loki", Namespace = "monitoring", ComponentType = "loki",
                Status = ComponentStatus.Installed,
            });
        }

        return clusterId;
    }

    public void Dispose()
    {
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private LokiService Build()
    {
        VaultService vault = testDb.CreateVaultService();
        Mock<IHttpClientFactory> innerHttp = new();
        innerHttp.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        AgentRegistry agents = new(testDb.Factory, NullLogger<AgentRegistry>.Instance);
        OpenStackHttpFactory httpFactory = new(innerHttp.Object, agents);
        ClusterEgressTunnel tunnel = new(NullLogger<ClusterEgressTunnel>.Instance);
        OpenStackKeystoneClient keystone = new(httpFactory, vault, tunnel, testDb.Factory);
        EntKube.Web.Services.Clusters.ClusterClientFactory clusters = new(testDb.Factory, k8s.Object);

        StorageService storageService = new(
            testDb.Factory, vault, new OpenStackS3Service(vault, httpFactory, keystone),
            keystone, new ClusterEgressRelay(NullLogger<ClusterEgressRelay>.Instance), tunnel,
            agents, k8s.Object, clusters,
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new StorageLinkClientFactory(vault, testDb.Factory, httpFactory, keystone));

        return new LokiService(
            new EntKube.Web.Modules.Api.DataServicesApi(testDb.Factory),
            NullLogger<LokiService>.Instance, vault, storageService,
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), clusters);
    }

    /// <summary>
    /// The owner sees Loki. Without this the refusals below would pass for any reason at all —
    /// a missing interceptor, an unseeded component — rather than for the tenant check.
    /// </summary>
    [Fact]
    public async Task The_owner_sees_an_installed_loki()
    {
        (await Build().IsAvailableAsync(ourTenant, ourCluster)).Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenants_cluster_reports_no_loki()
    {
        LokiService sut = Build();

        (await sut.IsAvailableAsync(ourTenant, theirCluster)).Should().BeFalse();
        (await sut.IsAvailableAsync(theirTenant, ourCluster)).Should().BeFalse();
    }

    /// <summary>
    /// Both halves of the resolution are scoped, not just the credential: a stranger must not be
    /// able to learn that a cluster runs Loki either, which is what the component read would tell
    /// them if only the <c>ForAsync</c> call had been converted.
    /// </summary>
    [Fact]
    public async Task Another_tenant_is_refused_before_any_query_is_attempted()
    {
        LokiService sut = Build();

        KubernetesOperationResult<List<string>> result =
            await sut.GetNamespacesAsync(theirTenant, ourCluster);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not this tenant's");
    }

    /// <summary>A cluster of this tenant's with no Loki installed is a different answer.</summary>
    [Fact]
    public async Task The_owners_cluster_without_loki_says_so()
    {
        KubernetesOperationResult<List<string>> result =
            await Build().GetNamespacesAsync(ourTenant, bareCluster);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("No Loki component");
        (await Build().IsAvailableAsync(ourTenant, bareCluster)).Should().BeFalse();
    }
}
