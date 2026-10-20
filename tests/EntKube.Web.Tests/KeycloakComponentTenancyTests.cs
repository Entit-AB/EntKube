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
    /// <summary>
    /// Seeds a realm, an app deployment and a binding under <paramref name="tenantId"/>, and
    /// returns their ids. Lifted out of the Sync test so the three below can share it.
    /// </summary>
    private (Guid Realm, Guid Deployment, Guid Binding) SeedBinding(Guid tenantId, Guid componentId)
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), bindingId = Guid.NewGuid();
        Guid realmId = Guid.NewGuid(), deploymentId = Guid.NewGuid(), configId = Guid.NewGuid();

        KubernetesCluster cluster = db.KubernetesClusters.First(c => c.TenantId == tenantId);

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "C" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "app" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "deploy", Type = DeploymentType.Manual,
            EnvironmentId = cluster.EnvironmentId, ClusterId = cluster.Id, Namespace = "ns",
        });
        db.Set<KeycloakComponentConfig>().Add(new KeycloakComponentConfig
        {
            Id = configId, TenantId = tenantId, ClusterComponentId = componentId,
            AdminUrl = "https://keycloak.example",
        });
        db.Set<KeycloakRealm>().Add(new KeycloakRealm
        {
            Id = realmId, TenantId = tenantId, KeycloakComponentConfigId = configId,
            RealmName = "r", DisplayName = "R", LinkedAppId = appId,
        });
        db.Set<IdentityBinding>().Add(new IdentityBinding
        {
            Id = bindingId, KeycloakRealmId = realmId, AppDeploymentId = deploymentId,
            ClientId = "client", ClientUuid = "uuid", KubernetesSecretName = "oidc",
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return (realmId, deploymentId, bindingId);
    }

    /// <summary>
    /// ⚠️ <b>CreateIdentityBindingAsync took two ids, both naming rows, both from the
    /// caller</b> — so it wired a realm to a deployment without either being ours. The row it
    /// writes is what SyncIdentityBindingAsync follows to put an OIDC client secret into the
    /// deployment's namespace, so a binding across tenants is a client secret crossing with it.
    ///
    /// <para>Both halves are tested, because each is refused by its own guard: our realm with
    /// their deployment, and their realm with our deployment.</para>
    /// </summary>
    [Fact]
    public async Task An_identity_binding_cannot_be_created_across_tenants()
    {
        (Guid ourRealm, Guid ourDeployment, Guid _) = SeedBinding(ourTenant, ourComponent);
        (Guid theirRealm, Guid theirDeployment, Guid _) = SeedBinding(theirTenant, theirComponent);

        Func<Task> theirDeploy = () => sut.CreateIdentityBindingAsync(
            ourTenant, ourRealm, theirDeployment, "uuid", "client", "oidc");

        await theirDeploy.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Deployment not found, or it is not this tenant's.");

        Func<Task> theirRealmCall = () => sut.CreateIdentityBindingAsync(
            ourTenant, theirRealm, ourDeployment, "uuid", "client", "oidc");

        await theirRealmCall.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Realm not found, or it is not this tenant's.");

        // And ours still binds, or the two refusals say nothing.
        IdentityBinding ok = await sut.CreateIdentityBindingAsync(
            ourTenant, ourRealm, ourDeployment, "uuid", "client", "oidc");

        ok.KeycloakRealmId.Should().Be(ourRealm);
    }

    /// <summary>
    /// Removing a binding takes an application's OIDC configuration away, so by id alone this
    /// stopped somebody else's app authenticating.
    /// </summary>
    [Fact]
    public async Task An_identity_binding_cannot_be_removed_across_tenants()
    {
        (Guid _, Guid _, Guid theirBinding) = SeedBinding(theirTenant, theirComponent);

        Func<Task> act = () => sut.RemoveIdentityBindingAsync(ourTenant, theirBinding);

        // The exact message, for the reason the Sync test above gives.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Identity binding not found.");

        db.ChangeTracker.Clear();
        db.Set<IdentityBinding>().Any(b => b.Id == theirBinding).Should().BeTrue();
    }

    /// <summary>
    /// The theme extras are spliced into the Helm values a component is installed with, so a
    /// theme read across tenants is another tenant's login page on our install. KeycloakTheme
    /// carries its own TenantId, stamped where themes are created.
    /// </summary>
    [Fact]
    public async Task Theme_extras_are_not_read_across_tenants()
    {
        (Guid _, Guid _, Guid _) = SeedBinding(theirTenant, theirComponent);

        Guid theirConfigId = db.Set<KeycloakComponentConfig>()
            .First(c => c.TenantId == theirTenant).Id;

        db.Set<KeycloakTheme>().Add(new KeycloakTheme
        {
            Id = Guid.NewGuid(), TenantId = theirTenant,
            KeycloakComponentConfigId = theirConfigId,
            Name = "their-theme", LoginTheme = "their-theme",
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        // Asked as them it renders; asked as us their theme is not there to render.
        (await sut.BuildKeycloakThemeHelmExtrasAsync(theirTenant, theirComponent))
            .Should().Contain("their-theme");
        (await sut.BuildKeycloakThemeHelmExtrasAsync(ourTenant, theirComponent))
            .Should().BeEmpty();
    }
}
