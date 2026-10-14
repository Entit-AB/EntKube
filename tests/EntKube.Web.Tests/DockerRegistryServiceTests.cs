using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for DockerRegistryService environment scoping — registry credentials are scoped by
/// app + environment (null environment = shared), like app secrets, including the cross-environment
/// sync guard. Uses the intercepting test DB so the cluster kubeconfig resolves from the vault.
/// </summary>
public class DockerRegistryServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly DockerRegistryService sut;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    /// <summary>Namespaces the sync ensured, in order.</summary>
    private readonly List<string> namespacesEnsured = [];

    /// <summary>Secrets the sync replaced, as (name, namespace, manifest).</summary>
    private readonly List<(string Name, string Ns, string Manifest)> secretsReplaced = [];

    public DockerRegistryServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(f => f.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ns, _, _) => namespacesEnsured.Add(ns))
            .Returns(Task.CompletedTask);

        k8s.Setup(f => f.ReplaceSecretAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string?, CancellationToken>(
                (name, ns, manifest, _, _, _) => secretsReplaced.Add((name, ns, manifest)))
            .ReturnsAsync("secret/registry-creds replaced");
        // The real factory over the intercepting test database: the credential reaches the cluster
        // the way production resolves it, and a cross-tenant ask refuses here rather than in a mock.
        sut = new DockerRegistryService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            testDb.Encryption);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    private async Task<(Tenant tenant, App app, Data.Environment prod, Data.Environment test)> SeedAsync()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Co", Slug = "co" };
        db.Tenants.Add(tenant);
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Cust" };
        db.Customers.Add(customer);
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "App" };
        db.Apps.Add(app);
        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        Data.Environment test = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "test" };
        db.Set<Data.Environment>().AddRange(prod, test);
        await db.SaveChangesAsync();
        await vault.InitializeVaultAsync(tenant.Id);
        return (tenant, app, prod, test);
    }

    private Task<DockerRegistryCredential> CreateAsync(Guid tenantId, Guid? appId, string name, Guid? envId) =>
        sut.CreateAsync(tenantId, appId, name, DockerRegistryType.Generic,
            "registry.example.com", "user", "pass", email: null, environmentId: envId);

    [Fact]
    public async Task CreateAsync_AppScoped_StoresEnvironmentBinding()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-acr", prod.Id);

        cred.AppId.Should().Be(app.Id);
        cred.EnvironmentId.Should().Be(prod.Id);
    }

    [Fact]
    public async Task CreateAsync_TenantWide_IgnoresEnvironment()
    {
        (Tenant tenant, _, Data.Environment prod, _) = await SeedAsync();

        // No app → an environment binding makes no sense and is dropped.
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, appId: null, "shared", prod.Id);

        cred.AppId.Should().BeNull();
        cred.EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithEnvironmentFilter_ReturnsSharedAndThatEnvironmentOnly()
    {
        (Tenant tenant, App app, Data.Environment prod, Data.Environment test) = await SeedAsync();
        await CreateAsync(tenant.Id, app.Id, "shared-cred", null);
        await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await CreateAsync(tenant.Id, app.Id, "test-cred", test.Id);

        List<DockerRegistryCredential> visibleInProd = await sut.GetAsync(tenant.Id, app.Id, prod.Id);

        visibleInProd.Select(c => c.Name).Should().BeEquivalentTo("shared-cred", "prod-cred");
        visibleInProd.Should().NotContain(c => c.Name == "test-cred");
    }

    [Fact]
    public async Task ChangeScopeAsync_BindsAndUnbinds()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "cred", null);

        await sut.ChangeScopeAsync(cred.Id, prod.Id);
        (await db.Set<DockerRegistryCredential>().AsNoTracking().FirstAsync(c => c.Id == cred.Id))
            .EnvironmentId.Should().Be(prod.Id);

        await sut.ChangeScopeAsync(cred.Id, null);
        (await db.Set<DockerRegistryCredential>().AsNoTracking().FirstAsync(c => c.Id == cred.Id))
            .EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task ChangeScopeAsync_NoOpForTenantWide()
    {
        (Tenant tenant, _, Data.Environment prod, _) = await SeedAsync();
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, appId: null, "shared", null);

        await sut.ChangeScopeAsync(cred.Id, prod.Id);

        (await db.Set<DockerRegistryCredential>().AsNoTracking().FirstAsync(c => c.Id == cred.Id))
            .EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task SyncToKubernetesAsync_EnvBoundCredToOtherEnvironmentCluster_Fails()
    {
        (Tenant tenant, App app, Data.Environment prod, Data.Environment test) = await SeedAsync();

        // A cluster in the TEST environment, with a kubeconfig stored in the vault.
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = test.Id,
            Name = "test-cluster",
            ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        // A credential bound to PROD, configured to sync into the TEST cluster.
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(cred.Id, cluster.Id, "registry-creds", "app");

        KubernetesOperationResult<string> result = await sut.SyncToKubernetesAsync(tenant.Id, cred.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("different environment");

        secretsReplaced.Should().BeEmpty("the guard runs before anything reaches the cluster");
    }

    /// <summary>
    /// The sync writes a registry password into a namespace, so it is exactly the kind of change
    /// that must not happen without the gate seeing it. Through the seam it is
    /// <c>EnsureNamespaceAsync</c> + <c>ReplaceSecretAsync</c>; it used to be <c>kubectl apply -f</c>
    /// run as its own process with <c>--kubeconfig</c>, which the gate cannot see at all.
    /// </summary>
    [Fact]
    public async Task SyncToKubernetesAsync_WritesTheSecretThroughTheSeam()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
            Name = "prod-cluster", ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(cred.Id, cluster.Id, "registry-creds", "app");

        (await sut.SyncToKubernetesAsync(tenant.Id, cred.Id)).IsSuccess.Should().BeTrue();

        namespacesEnsured.Should().Equal(["app"]);
        secretsReplaced.Should().ContainSingle()
            .Which.Should().Match<(string Name, string Ns, string Manifest)>(s =>
                s.Name == "registry-creds" && s.Ns == "app"
                && s.Manifest.Contains("kubernetes.io/dockerconfigjson")
                && !s.Manifest.Contains("kind: Namespace"));
    }

    /// <summary>
    /// And another tenant cannot use it to write into a cluster that is not theirs: the seam
    /// refuses, so nothing is ensured and nothing is written.
    /// </summary>
    [Fact]
    public async Task SyncToKubernetesAsync_RefusesAnotherTenantsCluster()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
            Name = "prod-cluster", ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(cred.Id, cluster.Id, "registry-creds", "app");

        KubernetesOperationResult<string> result =
            await sut.SyncToKubernetesAsync(Guid.NewGuid(), cred.Id);

        result.IsSuccess.Should().BeFalse();
        secretsReplaced.Should().BeEmpty("nothing should reach anyone's cluster");
        namespacesEnsured.Should().BeEmpty();
    }
}
