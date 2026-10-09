using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EntKube.Web.Tests;

/// <summary>
/// Two Keycloak lookups that answered about any component in the installation.
///
/// <para><b>Found by migrating, not by looking for it.</b> <c>ICatalogApi</c> takes the tenant as a
/// parameter rather than leaving it to a filter the caller remembers, so moving these two methods
/// onto the contract required a tenant — and both already had one. The queries were
/// <c>c.Id == clusterComponentId</c> with no tenant predicate at all, in methods whose signature
/// reads <c>(Guid tenantId, Guid clusterComponentId, …)</c>. The argument was there and unused.</para>
///
/// <para>This is the third time this programme has turned up that shape (<c>RedisService</c> #123,
/// <c>WorkloadService</c> #135), which is why it is worth a test rather than a changelog line.</para>
/// </summary>
public class KeycloakComponentTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;
    private readonly KeycloakService sut;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourComponent, theirComponent;

    public KeycloakComponentTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        ourComponent = Seed(ourTenant, "ours");
        theirComponent = Seed(theirTenant, "theirs");
        db.SaveChanges();

        sut = TestServices.BuildKeycloak(factory, new VaultService(factory, new VaultEncryptionService(
            Convert.FromBase64String(TestServices.TestRootKeyBase64)), TestServices.NoClusterAccess));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid Seed(Guid tenantId, string label)
    {
        Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid(), componentId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = label });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "keycloak",
            ComponentType = "Helm", Namespace = "keycloak", Status = Data.ComponentStatus.Installed,
        });

        return componentId;
    }

    [Fact]
    public async Task Databases_for_a_component_are_refused_across_tenants()
    {
        // Sanity: our own component answers, so a refusal below is about the tenant and not about
        // the component being unreachable.
        (await sut.GetDatabasesForComponentAsync(ourTenant, ourComponent)).Should().BeEmpty();

        Func<Task> act = () => sut.GetDatabasesForComponentAsync(ourTenant, theirComponent);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a component belonging to another tenant must read as absent");
    }

    [Fact]
    public async Task Registered_postgres_databases_are_refused_across_tenants()
    {
        (await sut.GetRegisteredPostgresDatabasesForComponentAsync(ourTenant, ourComponent)).Should().BeEmpty();

        Func<Task> act = () => sut.GetRegisteredPostgresDatabasesForComponentAsync(ourTenant, theirComponent);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
