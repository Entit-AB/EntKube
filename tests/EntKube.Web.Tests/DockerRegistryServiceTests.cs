using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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

    public DockerRegistryServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();
        sut = new DockerRegistryService(testDb.Factory, testDb.Encryption);
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

        KubernetesOperationResult<string> result = await sut.SyncToKubernetesAsync(cred.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("different environment");
    }
}
