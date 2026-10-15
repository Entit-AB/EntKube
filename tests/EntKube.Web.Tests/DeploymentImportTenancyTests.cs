using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Importing a live workload is a write into a tenant, and the tenant used to be whatever the
/// cluster row said.
///
/// <para><c>ImportAsync</c> opened with <c>Guid tenantId = cluster.TenantId;</c> — a label, not a
/// check. Whatever row the caller passed was imported into its own tenant, and every tenant-scoped
/// call underneath then compared that row to itself: the <see cref="VacuousTenantCheckTests"/>
/// shape, one level up from a predicate. The page has always had a tenant, so it is an input now.
/// </para>
/// </summary>
public class DeploymentImportTenancyTests : IDisposable
{
    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(RootKey);
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private KubernetesCluster ourCluster = null!;

    public DeploymentImportTenancyTests()
    {
        using ApplicationDbContext db = testDb.CreateContext();

        db.Tenants.Add(new Tenant { Id = ourTenant, Name = "ours", Slug = $"ours-{ourTenant:N}" });
        db.Tenants.Add(new Tenant { Id = theirTenant, Name = "theirs", Slug = $"theirs-{theirTenant:N}" });

        Guid environmentId = Guid.NewGuid();
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = ourTenant, Name = "prod" });

        ourCluster = new KubernetesCluster
        {
            Id = Guid.NewGuid(), TenantId = ourTenant, EnvironmentId = environmentId,
            Name = "ours-cluster", ApiServerUrl = "https://k8s.example.com",
        };
        db.KubernetesClusters.Add(ourCluster);
        db.SaveChanges();
    }

    public void Dispose()
    {
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private DeploymentImportService Build()
    {
        VaultService vault = testDb.CreateVaultService();
        EntKube.Web.Services.Clusters.ClusterClientFactory clusters = new(testDb.Factory, k8s.Object);

        Mock<IHttpClientFactory> innerHttp = new();
        innerHttp.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        EntKube.Web.Services.Agents.AgentRegistry agents =
            new(testDb.Factory, NullLogger<EntKube.Web.Services.Agents.AgentRegistry>.Instance);
        OpenStackHttpFactory httpFactory = new(innerHttp.Object, agents);
        ClusterEgressTunnel tunnel = new(NullLogger<ClusterEgressTunnel>.Instance);
        OpenStackKeystoneClient keystone = new(httpFactory, vault, tunnel, testDb.Factory);

        return new DeploymentImportService(
            testDb.Factory,
            new DeploymentService(testDb.Factory, new AuditService(testDb.Factory),
                new DeploymentStatusNotifier(), NullLogger<DeploymentService>.Instance),
            vault,
            new RegisteredPostgresService(testDb.Factory, vault, clusters,
                new StorageBrowserService(
                    new StorageLinkClientFactory(vault, testDb.Factory, httpFactory, keystone)),
                NullLogger<RegisteredPostgresService>.Instance),
            new DockerRegistryService(testDb.Factory, clusters, testDb.Encryption),
            new AppRouteService(testDb.Factory,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
                NullLogger<AppRouteService>.Instance),
            NullLogger<DeploymentImportService>.Instance);
    }

    private ImportRequest Request(string appName) => new()
    {
        AppName = appName,
        CustomerId = Guid.NewGuid(),
        EnvironmentId = ourCluster.EnvironmentId,
        Preview = new ImportPreview { ClusterId = ourCluster.Id },
    };

    /// <summary>
    /// The refusal. Nothing else in this method asks the question, so without it a cluster id from
    /// another tenant imported that tenant's workloads into the caller's own app list.
    /// </summary>
    [Fact]
    public async Task Importing_a_cluster_as_another_tenant_is_refused()
    {
        Func<Task> act = () => Build().ImportAsync(
            theirTenant, ourCluster, Request("stolen"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not this tenant's*");
    }

    /// <summary>
    /// And the owner is not refused — or the test above would pass against a method that refuses
    /// everyone. It gets as far as needing a customer, which is the next thing an import needs and
    /// proof that the guard let it through.
    /// </summary>
    [Fact]
    public async Task The_owner_passes_the_tenant_check()
    {
        Func<Task> act = () => Build().ImportAsync(
            ourTenant, ourCluster, Request("mine"));

        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().NotContain("not this tenant's");
    }
}
