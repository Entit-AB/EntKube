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
///
/// <para>⚠️ <b>It kept happening.</b> <c>ComponentLifecycleService</c> had <em>six</em> of them at
/// once (#270) — <c>GetInstallCommandAsync</c>, <c>InstallSubchartsAsync</c>,
/// <c>UninstallSubchartsAsync</c>, <c>SyncComponentSecretsAsync</c>, <c>ExecuteHelmAsync</c>,
/// <c>ApplyExternalRoutesAsync</c> — and <c>VpnService.AddRemoteEndpointAsync</c> had it with the
/// tenant in seventh position, used only to file a PSK in the vault (#268).
/// <c>HarborService.LinkProjectToAppAsync</c> is the same shape again and the reason this file
/// gained a third test: there the tenant was used only to <em>stamp</em> a new row, so neither of
/// the two ids it was handed was ever checked.
///
/// <para>The common thread is that a tenant reaching the vault, the cluster seam or an INSERT
/// looks exactly like a tenant reaching the predicate at a glance. Only the query says which.
/// <c>VacuousTenantCheckTests</c> catches the related tautology — a tenant read off the row being
/// authorized — but not this one, and an attempt to ratchet it generally was measured three ways
/// and abandoned: every variant was dominated by legitimate code.</para></para>
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

    /// <summary>
    /// <c>SyncIdentityBindingAsync</c> loaded its binding by id alone. Here the tenant was
    /// genuinely load-bearing twice further down — <c>GetClientSecretAsync</c> asks the vault as
    /// this tenant, and <c>ForAsync</c> refuses a cluster that is not theirs — so this was
    /// redundant rather than exploitable. It is scoped anyway: <c>IdentityBinding</c> has no
    /// <c>TenantId</c>, and a method should not depend on what happens after it for its own
    /// authorization.
    /// </summary>
    [Fact]
    public async Task An_identity_binding_is_not_reachable_across_tenants()
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), bindingId = Guid.NewGuid();
        Guid realmId = Guid.NewGuid(), deploymentId = Guid.NewGuid();

        KubernetesCluster theirCluster = db.KubernetesClusters.First(c => c.TenantId == theirTenant);

        db.Customers.Add(new Customer { Id = customerId, TenantId = theirTenant, Name = "Theirs" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "their-app" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "their-deploy", Type = DeploymentType.Manual,
            EnvironmentId = theirCluster.EnvironmentId, ClusterId = theirCluster.Id,
            Namespace = "their-ns",
        });
        Guid configId = Guid.NewGuid();
        db.Set<KeycloakComponentConfig>().Add(new KeycloakComponentConfig
        {
            Id = configId, TenantId = theirTenant, ClusterComponentId = theirComponent,
            AdminUrl = "https://keycloak.theirs.example",
        });
        db.Set<KeycloakRealm>().Add(new KeycloakRealm
        {
            Id = realmId, TenantId = theirTenant, KeycloakComponentConfigId = configId,
            RealmName = "theirs", DisplayName = "Theirs", LinkedAppId = appId,
        });
        db.Set<IdentityBinding>().Add(new IdentityBinding
        {
            Id = bindingId, KeycloakRealmId = realmId, AppDeploymentId = deploymentId,
            ClientId = "their-client", ClientUuid = "uuid", KubernetesSecretName = "oidc",
        });
        db.SaveChanges();

        Func<Task> act = () => sut.SyncIdentityBindingAsync(ourTenant, bindingId);

        // ⚠️ The exact message, not a wildcard. "*not found*" passed with the predicate deleted,
        // because the binding was then found and the NEXT guard — no client secret for a realm
        // that is not ours — threw a message that also matched. An assertion loose enough to
        // accept a downstream refusal is not testing this one.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Identity binding not found.",
                "the binding must read as absent here, not be resolved and then refused later "
                + "for a different reason");
    }
}
