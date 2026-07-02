using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Verifies that a cluster's kubeconfig — now stored encrypted in the vault rather than as a
/// plaintext column — is transparently repopulated onto <see cref="KubernetesCluster.Kubeconfig"/>
/// when the cluster is materialized, via <see cref="KubeconfigMaterializationInterceptor"/> and
/// <see cref="KubeconfigResolver"/>. This is what lets the many consumers that read
/// <c>cluster.Kubeconfig</c> keep working unchanged.
/// </summary>
public class KubeconfigResolverTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private const string SampleKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: prod
          cluster:
            server: https://k8s.example.com
        contexts:
        - name: prod
          context:
            cluster: prod
            user: admin
        users:
        - name: admin
          user:
            token: secret-token
        """;

    // A named shared-cache in-memory database so that multiple independent connections see the
    // same data (mirroring production, where the resolver opens its own connection separate from
    // the one materializing the cluster). The keep-alive connection keeps the DB from being torn
    // down while no other connection is open. The name is unique per test instance so concurrent
    // tests don't share the same global in-memory database.
    private readonly string ConnectionString =
        $"DataSource=file:kubeconfig-resolver-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection keepAlive;
    private readonly ApplicationDbContext db;
    private readonly VaultEncryptionService encryption;
    private readonly KubeconfigResolver resolver;
    private readonly KubeconfigMaterializationInterceptor interceptor;
    private readonly VaultService vault;

    /// <summary>A factory that opens a fresh connection per context, as production does.</summary>
    private sealed class PerConnectionDbContextFactory(string connectionString) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            return new ApplicationDbContext(options);
        }
    }

    public KubeconfigResolverTests()
    {
        keepAlive = new SqliteConnection(ConnectionString);
        keepAlive.Open();

        db = new PerConnectionDbContextFactory(ConnectionString).CreateDbContext();
        db.Database.EnsureCreated();

        encryption = new VaultEncryptionService(TestRootKey);
        IDbContextFactory<ApplicationDbContext> dbFactory = new PerConnectionDbContextFactory(ConnectionString);

        ServiceProvider sp = new ServiceCollection()
            .AddSingleton(dbFactory)
            .BuildServiceProvider();

        resolver = new KubeconfigResolver(sp, encryption);
        interceptor = new KubeconfigMaterializationInterceptor(resolver);
        vault = new VaultService(dbFactory, encryption, resolver);
    }

    public void Dispose()
    {
        db.Dispose();
        keepAlive.Dispose();
    }

    private ApplicationDbContext CreateInterceptedContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(ConnectionString)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new ApplicationDbContext(options);
    }

    private (Guid TenantId, Guid ClusterId) SeedCluster()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "production" };
        db.Environments.Add(env);
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com",
        };
        db.KubernetesClusters.Add(cluster);
        db.SaveChanges();
        return (tenant.Id, cluster.Id);
    }

    [Fact]
    public async Task Materialization_PopulatesKubeconfigFromVault()
    {
        (Guid tenantId, Guid clusterId) = SeedCluster();

        await vault.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle { ConfigYaml = SampleKubeconfig, ContextName = "prod" }, "admin");

        // Loading through an intercepted context repopulates the [NotMapped] Kubeconfig property.
        using ApplicationDbContext ctx = CreateInterceptedContext();
        KubernetesCluster loaded = await ctx.KubernetesClusters.FirstAsync(c => c.Id == clusterId);

        loaded.KubeconfigSecretId.Should().NotBeNull();
        loaded.Kubeconfig.Should().Be(SampleKubeconfig);
    }

    [Fact]
    public async Task Materialization_ReflectsUpdatedKubeconfigAfterInvalidation()
    {
        (Guid tenantId, Guid clusterId) = SeedCluster();

        await vault.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle { ConfigYaml = SampleKubeconfig }, "admin");

        // Warm the resolver cache.
        using (ApplicationDbContext ctx = CreateInterceptedContext())
        {
            KubernetesCluster first = await ctx.KubernetesClusters.FirstAsync(c => c.Id == clusterId);
            first.Kubeconfig.Should().Contain("secret-token");
        }

        // Update — SetClusterKubeconfigAsync invalidates the cache.
        string updated = SampleKubeconfig.Replace("secret-token", "rotated-token");
        await vault.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle { ConfigYaml = updated }, "admin");

        using (ApplicationDbContext ctx = CreateInterceptedContext())
        {
            KubernetesCluster second = await ctx.KubernetesClusters.FirstAsync(c => c.Id == clusterId);
            second.Kubeconfig.Should().Contain("rotated-token");
        }
    }

    [Fact]
    public async Task Materialization_LeavesKubeconfigNullWhenNoSecretLinked()
    {
        (_, Guid clusterId) = SeedCluster();

        using ApplicationDbContext ctx = CreateInterceptedContext();
        KubernetesCluster loaded = await ctx.KubernetesClusters.FirstAsync(c => c.Id == clusterId);

        loaded.KubeconfigSecretId.Should().BeNull();
        loaded.Kubeconfig.Should().BeNull();
    }

    // The Kubeconfig property is [NotMapped], so it cannot appear in a server-side query predicate
    // or projection — EF throws "Translation of member 'Kubeconfig' ... failed" at runtime (it stops
    // background services). Consumers must instead filter on the mapped KubeconfigSecretId column and
    // read the resolved value only after materialization. These guard that contract.

    [Fact]
    public async Task Query_FilterByKubeconfigSecretId_Translates()
    {
        (Guid tenantId, Guid clusterId) = SeedCluster();
        await vault.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle { ConfigYaml = SampleKubeconfig }, "admin");

        using ApplicationDbContext ctx = CreateInterceptedContext();

        // Mirrors the fixed DeploymentSyncService/Prometheus/Kyverno/RabbitMQ predicates.
        List<KubernetesCluster> withKubeconfig = await ctx.KubernetesClusters
            .Where(c => c.KubeconfigSecretId != null)
            .ToListAsync();

        withKubeconfig.Should().ContainSingle(c => c.Id == clusterId);
        // The materialized entity still has its kubeconfig resolved from the vault.
        withKubeconfig.Single(c => c.Id == clusterId).Kubeconfig.Should().Be(SampleKubeconfig);
    }

    [Fact]
    public async Task Query_UsingUnmappedKubeconfigInPredicate_Throws()
    {
        SeedCluster();
        using ApplicationDbContext ctx = CreateInterceptedContext();

        // Documents why consumers must not reference the unmapped property in a query.
        Func<Task> act = () => ctx.KubernetesClusters.Where(c => c.Kubeconfig != null).ToListAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Query_ProjectClusterEntity_ResolvesKubeconfig()
    {
        (Guid tenantId, Guid clusterId) = SeedCluster();
        await vault.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle { ConfigYaml = SampleKubeconfig }, "admin");

        using ApplicationDbContext ctx = CreateInterceptedContext();

        // Mirrors the fixed StorageBrowserService projection: project the entity (not the unmapped
        // column) so the interceptor resolves the kubeconfig on materialization.
        KubernetesCluster? projected = await ctx.KubernetesClusters
            .Where(c => c.Id == clusterId)
            .Select(c => c)
            .FirstOrDefaultAsync();

        projected!.Kubeconfig.Should().Be(SampleKubeconfig);
    }
}
