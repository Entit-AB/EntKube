using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the scope the customer portal confines a customer to. The portal shows
/// databases, message brokers and caches through this service alone, so anything it
/// leaves out is unreachable from the portal — these tests pin the boundary.
/// </summary>
public class PortalServiceScopeServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly PortalServiceScopeService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid otherEnvironmentId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid otherAppId = Guid.NewGuid();
    private readonly Guid k8sClusterId = Guid.NewGuid();

    public PortalServiceScopeServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();

        sut = new PortalServiceScopeService(new TestDbContextFactory(connection));

        Seed();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Seed ──

    private void Seed()
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = "testco" });
        db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Rival", Slug = "rival" });

        Guid customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });

        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = "prod" });
        db.Environments.Add(new Data.Environment { Id = otherEnvironmentId, TenantId = tenantId, Name = "test" });

        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "shop" });
        db.Apps.Add(new App { Id = otherAppId, CustomerId = customerId, Name = "crm" });

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = k8sClusterId,
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Name = "k8s-1",
            ApiServerUrl = "https://k8s-1.example",
        });

        db.SaveChanges();
    }

    private AppDeployment AddDeployment(Guid forAppId, Guid forEnvironmentId, string name)
    {
        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = forAppId,
            EnvironmentId = forEnvironmentId,
            ClusterId = k8sClusterId,
            Name = name,
            Namespace = name,
            Type = DeploymentType.Yaml,
        };
        db.AppDeployments.Add(deployment);
        db.SaveChanges();
        return deployment;
    }

    private CnpgCluster AddCnpgCluster(string name, Guid? forTenantId = null)
    {
        CnpgCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = forTenantId ?? tenantId,
            KubernetesClusterId = k8sClusterId,
            Name = name,
            Namespace = "databases",
            PostgresVersion = "16",
            StorageSize = "10Gi",
        };
        db.CnpgClusters.Add(cluster);
        db.SaveChanges();
        return cluster;
    }

    private CnpgDatabase AddCnpgDatabase(CnpgCluster cluster, string name)
    {
        CnpgDatabase database = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cluster.Id,
            Name = name,
            Owner = $"{name}_owner",
            Status = CnpgDatabaseStatus.Ready,
        };
        db.CnpgDatabases.Add(database);
        db.SaveChanges();
        return database;
    }

    private void Bind(AppDeployment deployment, CnpgDatabase database, string secretName = "app-db")
    {
        db.DatabaseBindings.Add(new DatabaseBinding
        {
            Id = Guid.NewGuid(),
            AppDeploymentId = deployment.Id,
            CnpgDatabaseId = database.Id,
            KubernetesSecretName = secretName,
        });
        db.SaveChanges();
    }

    private RabbitMQCluster AddRabbitCluster(string name)
    {
        RabbitMQCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = k8sClusterId,
            Name = name,
            Namespace = "messaging",
            RabbitMQVersion = "3.13",
            StorageSize = "10Gi",
        };
        db.RabbitMQClusters.Add(cluster);
        db.SaveChanges();
        return cluster;
    }

    private void BindMessaging(AppDeployment deployment, RabbitMQCluster cluster, string vhost)
    {
        db.MessagingBindings.Add(new MessagingBinding
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RabbitMQClusterId = cluster.Id,
            AppDeploymentId = deployment.Id,
            Vhost = vhost,
            KubernetesSecretName = "rabbit-creds",
        });
        db.SaveChanges();
    }

    private RedisCluster AddRedisCluster(string name)
    {
        RedisCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = k8sClusterId,
            Name = name,
            Namespace = "cache",
            RedisVersion = "7.2",
            StorageSize = "5Gi",
        };
        db.RedisClusters.Add(cluster);
        db.SaveChanges();
        return cluster;
    }

    // ── Databases ──

    [Fact]
    public async Task DatabaseScope_IsEmpty_WhenNothingIsBoundOrAllowed()
    {
        AddCnpgDatabase(AddCnpgCluster("shared-pg"), "someone_else");

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        scope.IsEmpty.Should().BeTrue();
        scope.CnpgDatabases.Should().BeEmpty();
    }

    [Fact]
    public async Task DatabaseScope_IncludesBoundDatabaseAndItsServer()
    {
        AppDeployment deployment = AddDeployment(appId, environmentId, "shop-api");
        CnpgCluster cluster = AddCnpgCluster("shared-pg");
        CnpgDatabase mine = AddCnpgDatabase(cluster, "shop");
        Bind(deployment, mine);

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        scope.CnpgDatabases.Should().ContainSingle(d => d.Id == mine.Id);
        scope.CnpgClusters.Should().ContainSingle(c => c.Id == cluster.Id);
        scope.AllowsCnpgCluster(cluster.Id).Should().BeTrue();
        scope.AllowsCnpgDatabase(mine.Id).Should().BeTrue();
    }

    [Fact]
    public async Task DatabaseScope_HidesOtherCustomersDatabasesOnTheSameServer()
    {
        AppDeployment deployment = AddDeployment(appId, environmentId, "shop-api");
        CnpgCluster cluster = AddCnpgCluster("shared-pg");
        CnpgDatabase mine = AddCnpgDatabase(cluster, "shop");
        CnpgDatabase theirs = AddCnpgDatabase(cluster, "rival");
        Bind(deployment, mine);

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        // The server is in scope because a database on it is bound — but only that
        // database comes with it. A neighbour's database on the same cluster stays hidden.
        scope.CnpgDatabases.Should().ContainSingle();
        scope.AllowsCnpgDatabase(theirs.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DatabaseScope_ExcludesBindingsFromAnotherEnvironment()
    {
        AppDeployment testDeployment = AddDeployment(appId, otherEnvironmentId, "shop-api-test");
        CnpgDatabase testDb = AddCnpgDatabase(AddCnpgCluster("test-pg"), "shop_test");
        Bind(testDeployment, testDb);

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        scope.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task DatabaseScope_ExcludesAnotherAppsBindings()
    {
        AppDeployment otherDeployment = AddDeployment(otherAppId, environmentId, "crm-api");
        CnpgDatabase crmDb = AddCnpgDatabase(AddCnpgCluster("crm-pg"), "crm");
        Bind(otherDeployment, crmDb);

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        scope.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task DatabaseScope_ExcludesServersBelongingToAnotherTenant()
    {
        AppDeployment deployment = AddDeployment(appId, environmentId, "shop-api");
        CnpgCluster foreignCluster = AddCnpgCluster("rival-pg", otherTenantId);
        CnpgDatabase foreignDb = AddCnpgDatabase(foreignCluster, "rival");
        Bind(deployment, foreignDb);

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        scope.IsEmpty.Should().BeTrue();
        scope.AllowsCnpgDatabase(foreignDb.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DatabaseScope_IncludesAllowListedDatabaseWithNoBinding()
    {
        CnpgCluster cluster = AddCnpgCluster("shared-pg");
        CnpgDatabase granted = AddCnpgDatabase(cluster, "shop");

        await sut.GrantDatabaseAsync(appId, environmentId, cnpgDatabaseId: granted.Id);
        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        // Nothing is bound yet, but the grant is what lets a freshly created database
        // stay visible until the customer connects it to a deployment.
        scope.AllowsCnpgDatabase(granted.Id).Should().BeTrue();
        scope.AllowsCnpgCluster(cluster.Id).Should().BeTrue();
        scope.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task GrantDatabase_IsIdempotent()
    {
        CnpgDatabase granted = AddCnpgDatabase(AddCnpgCluster("shared-pg"), "shop");

        await sut.GrantDatabaseAsync(appId, environmentId, cnpgDatabaseId: granted.Id);
        await sut.GrantDatabaseAsync(appId, environmentId, cnpgDatabaseId: granted.Id);

        db.AppAllowedDatabases.Count(a => a.CnpgDatabaseId == granted.Id).Should().Be(1);
    }

    [Fact]
    public async Task DatabaseScope_ListsOnlyThisAppsDeploymentsInThisEnvironment()
    {
        AppDeployment mine = AddDeployment(appId, environmentId, "shop-api");
        AddDeployment(appId, otherEnvironmentId, "shop-api-test");
        AddDeployment(otherAppId, environmentId, "crm-api");

        PortalDatabaseScope scope = await sut.GetDatabaseScopeAsync(tenantId, appId, environmentId);

        scope.Deployments.Should().ContainSingle(d => d.Id == mine.Id);
        scope.AllowsDeployment(mine.Id).Should().BeTrue();
    }

    // ── Messaging ──

    [Fact]
    public async Task MessagingScope_IsEmpty_WhenNoVhostIsBound()
    {
        AddDeployment(appId, environmentId, "shop-api");
        AddRabbitCluster("shared-rabbit");

        PortalMessagingScope scope = await sut.GetMessagingScopeAsync(tenantId, appId, environmentId);

        scope.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task MessagingScope_ContainsOnlyTheVhostsTheAppIsBoundTo()
    {
        AppDeployment deployment = AddDeployment(appId, environmentId, "shop-api");
        AppDeployment otherDeployment = AddDeployment(otherAppId, environmentId, "crm-api");
        RabbitMQCluster cluster = AddRabbitCluster("shared-rabbit");

        BindMessaging(deployment, cluster, "/shop");
        BindMessaging(otherDeployment, cluster, "/crm");

        PortalMessagingScope scope = await sut.GetMessagingScopeAsync(tenantId, appId, environmentId);

        scope.Vhosts.Should().ContainSingle();
        scope.Vhosts[0].Vhost.Should().Be("/shop");
        scope.AllowsVhost(cluster.Id, "/shop").Should().BeTrue();
        // The neighbour's vhost on the same broker is not reachable, so topology
        // created from the portal can never land in it.
        scope.AllowsVhost(cluster.Id, "/crm").Should().BeFalse();
    }

    [Fact]
    public async Task MessagingScope_GroupsSeveralBindingsOnOneVhost()
    {
        AppDeployment api = AddDeployment(appId, environmentId, "shop-api");
        AppDeployment worker = AddDeployment(appId, environmentId, "shop-worker");
        RabbitMQCluster cluster = AddRabbitCluster("shared-rabbit");

        BindMessaging(api, cluster, "/shop");
        BindMessaging(worker, cluster, "/shop");

        PortalMessagingScope scope = await sut.GetMessagingScopeAsync(tenantId, appId, environmentId);

        scope.Vhosts.Should().ContainSingle();
        scope.Vhosts[0].Bindings.Should().HaveCount(2);
    }

    // ── Cache ──

    [Fact]
    public async Task CacheScope_ContainsOnlyTheClustersBoundToThisApp()
    {
        AppDeployment deployment = AddDeployment(appId, environmentId, "shop-api");
        AppDeployment otherDeployment = AddDeployment(otherAppId, environmentId, "crm-api");
        RedisCluster mine = AddRedisCluster("shop-cache");
        RedisCluster theirs = AddRedisCluster("crm-cache");

        db.CacheBindings.Add(new CacheBinding
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RedisClusterId = mine.Id,
            AppDeploymentId = deployment.Id,
            KubernetesSecretName = "redis",
        });
        db.CacheBindings.Add(new CacheBinding
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RedisClusterId = theirs.Id,
            AppDeploymentId = otherDeployment.Id,
            KubernetesSecretName = "redis",
        });
        db.SaveChanges();

        PortalCacheScope scope = await sut.GetCacheScopeAsync(tenantId, appId, environmentId);

        scope.Clusters.Should().ContainSingle(c => c.Id == mine.Id);
    }

    [Fact]
    public async Task CacheScope_IncludesAllowListedClusterWithNoBinding()
    {
        RedisCluster cluster = AddRedisCluster("shop-cache");
        db.AppAllowedCaches.Add(new AppAllowedCache
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = environmentId,
            RedisClusterId = cluster.Id,
        });
        db.SaveChanges();

        PortalCacheScope scope = await sut.GetCacheScopeAsync(tenantId, appId, environmentId);

        scope.Clusters.Should().ContainSingle(c => c.Id == cluster.Id);
        scope.Bindings.Should().BeEmpty();
    }
}
