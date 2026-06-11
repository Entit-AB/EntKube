using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// A Kubernetes cluster belongs to a tenant and is associated with an environment.
/// One environment can have many clusters (e.g. a production environment might
/// span multiple clusters for redundancy or regional distribution).
/// </summary>
public class ClusterEntityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public ClusterEntityTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Cluster_CanBeCreated_WithTenantAndEnvironment()
    {
        // A cluster is registered under a tenant and placed into an environment.
        // It has a name and an API server endpoint for connectivity.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-eu-west-1",
            ApiServerUrl = "https://k8s.prod-eu.example.com:6443"
        };

        context.KubernetesClusters.Add(cluster);
        await context.SaveChangesAsync();

        KubernetesCluster? retrieved = await context.KubernetesClusters
            .Include(c => c.Tenant)
            .Include(c => c.Environment)
            .FirstOrDefaultAsync(c => c.Name == "prod-eu-west-1");

        retrieved.Should().NotBeNull();
        retrieved!.Tenant.Name.Should().Be("Acme");
        retrieved.Environment.Name.Should().Be("Production");
        retrieved.ApiServerUrl.Should().Be("https://k8s.prod-eu.example.com:6443");
    }

    [Fact]
    public async Task Cluster_NameMustBeUniqueWithinTenant()
    {
        // Two clusters in the same tenant cannot share the same name.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.Add(env);

        context.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = env.Id,
            Name = "prod-01", ApiServerUrl = "https://a.example.com:6443"
        });

        await context.SaveChangesAsync();

        context.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = env.Id,
            Name = "prod-01", ApiServerUrl = "https://b.example.com:6443"
        });

        Func<Task> act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Environment_CanHaveMultipleClusters()
    {
        // A production environment might span multiple clusters for
        // redundancy or regional distribution.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.Add(prod);

        context.KubernetesClusters.AddRange(
            new KubernetesCluster
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
                Name = "prod-eu-west-1", ApiServerUrl = "https://eu-west.example.com:6443"
            },
            new KubernetesCluster
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
                Name = "prod-us-east-1", ApiServerUrl = "https://us-east.example.com:6443"
            }
        );

        await context.SaveChangesAsync();

        List<KubernetesCluster> clusters = await context.KubernetesClusters
            .Where(c => c.EnvironmentId == prod.Id)
            .ToListAsync();

        clusters.Should().HaveCount(2);
    }

    [Fact]
    public async Task Tenant_CanHaveClustersAcrossEnvironments()
    {
        // A tenant has clusters spread across different environments.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Data.Environment dev = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Development" };
        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.AddRange(dev, prod);

        context.KubernetesClusters.AddRange(
            new KubernetesCluster
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = dev.Id,
                Name = "dev-01", ApiServerUrl = "https://dev.example.com:6443"
            },
            new KubernetesCluster
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
                Name = "prod-01", ApiServerUrl = "https://prod.example.com:6443"
            }
        );

        await context.SaveChangesAsync();

        List<KubernetesCluster> clusters = await context.KubernetesClusters
            .Where(c => c.TenantId == tenant.Id)
            .ToListAsync();

        clusters.Should().HaveCount(2);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
