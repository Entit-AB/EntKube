using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Three of the six sweeps <see cref="SweepTenancyRatchetTests"/> listed as not yet acting as each
/// tenant (docs/decomposition.md §4.0.1.2), now doing so.
///
/// <para><b>Two of them had a real stake and one did not</b>, which is worth separating rather than
/// claiming a uniform win:</para>
/// <list type="bullet">
///   <item><c>SupportMailPoller</c> took the tenant off each mailbox row and then <b>took mail in
///   as</b> that tenant. It read as converted because its loop variable was called
///   <c>tenantId</c> — the name of a loop variable says nothing about where its value came
///   from.</item>
///   <item><c>SecretExpiryNotificationService</c> took the tenant off each config row and then
///   <b>notified as</b> that tenant, so a mis-stamped row would send one tenant's expiring-secret
///   list to another's channels.</item>
///   <item><c>ExternalRouteHealthService</c> makes no tenant-scoped decision at all: it probes a
///   hostname over HTTPS and writes history on the row it probed. That conversion buys a cycle per
///   tenant — isolation for the worker extraction — and is listed here as such.</item>
/// </list>
///
/// <para>Two more joined them, and the same split holds. <c>ObservedSecretRefreshService</c> reads
/// <b>live Kubernetes Secrets</b> with a cluster credential, so the tenant it acts as decides whose
/// cluster is read — safety. <c>UptimeTrackingService</c> writes a health row per deployment and
/// authorizes nothing — isolation. The predicates for both are asserted here; what the conversions
/// bought is recorded in <see cref="SweepTenancyRatchetTests"/>.</para>
/// </summary>
public sealed class SweepBacklogTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();

    public SweepBacklogTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Database.EnsureCreated();
        }
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();

        foreach ((Guid tenantId, string label) in new[] { (ours, "ours"), (theirs, "theirs") })
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public void A_mail_poll_sees_only_the_asking_tenants_mailboxes()
    {
        db.Set<SupportMailbox>().Add(NewMailbox(ours, "ours@example.com"));
        db.Set<SupportMailbox>().Add(NewMailbox(theirs, "theirs@example.com"));
        db.SaveChanges();

        using SupportDbContext support = ((IDbContextFactory<SupportDbContext>)factory).CreateDbContext();

        SupportMailPoller.PollableFor(support, ours).Select(m => m.Username).Should().Equal(["ours@example.com"]);
        SupportMailPoller.PollableFor(support, theirs).Select(m => m.Username).Should().Equal(["theirs@example.com"]);
    }

    /// <summary>
    /// The status rules are this sweep's own policy and must survive the tenant filter: a disabled
    /// mailbox, or one that has failed too often, is not polled for anybody.
    /// </summary>
    [Fact]
    public void A_mail_poll_still_skips_a_disabled_or_backed_off_mailbox()
    {
        // One mailbox per tenant is a unique constraint, so the two states take a tenant each.
        SupportMailbox disabled = NewMailbox(ours, "disabled@example.com");
        disabled.IsEnabled = false;

        SupportMailbox backedOff = NewMailbox(theirs, "failing@example.com");
        backedOff.ConsecutiveFailures = SupportMailboxService.FailuresBeforeBackingOff;

        db.Set<SupportMailbox>().AddRange(disabled, backedOff);
        db.SaveChanges();

        using SupportDbContext support = ((IDbContextFactory<SupportDbContext>)factory).CreateDbContext();

        SupportMailPoller.PollableFor(support, ours).Should().BeEmpty("it is disabled");
        SupportMailPoller.PollableFor(support, theirs).Should().BeEmpty("it has failed too often");
    }

    [Fact]
    public void A_secret_expiry_scan_sees_only_the_asking_tenants_scopes()
    {
        db.Set<SecretExpiryNotificationConfig>().Add(new SecretExpiryNotificationConfig
        {
            Id = Guid.NewGuid(), TenantId = ours, IsEnabled = true,
        });
        db.Set<SecretExpiryNotificationConfig>().Add(new SecretExpiryNotificationConfig
        {
            Id = Guid.NewGuid(), TenantId = theirs, IsEnabled = true,
        });
        // Same tenant, switched off.
        db.Set<SecretExpiryNotificationConfig>().Add(new SecretExpiryNotificationConfig
        {
            Id = Guid.NewGuid(), TenantId = ours, IsEnabled = false,
        });
        db.SaveChanges();

        using SecretsDbContext secrets = ((IDbContextFactory<SecretsDbContext>)factory).CreateDbContext();

        SecretExpiryNotificationService.EnabledScopesFor(secrets, ours).Should().ContainSingle()
            .Which.TenantId.Should().Be(ours);
        SecretExpiryNotificationService.EnabledScopesFor(secrets, theirs).Should().ContainSingle()
            .Which.TenantId.Should().Be(theirs);
    }

    /// <summary>
    /// A route carries no tenant of its own — it reaches one through its component's cluster — so
    /// this is the predicate most likely to be written the short way and be wrong.
    ///
    /// <para>The predicate used to do that walk itself (<c>r.Component.Cluster.TenantId</c>), which
    /// was correct and was also a two-hop read into Catalog's table and then Fleet's. The tenant
    /// check now comes from Catalog's tenant-first lookup, so this test asks the way the sweep
    /// asks: components for the tenant, then that tenant's routes. The property under test is
    /// unchanged — one tenant's hostnames, never the other's.</para>
    /// </summary>
    [Fact]
    public async Task A_route_health_sweep_reaches_a_tenants_routes_through_the_cluster()
    {
        Seed(ours, "ours.example.com");
        Seed(theirs, "theirs.example.com");

        using ConnectivityDbContext connectivity =
            ((IDbContextFactory<ConnectivityDbContext>)factory).CreateDbContext();
        EntKube.Contracts.Catalog.ICatalogApi catalog = new EntKube.Web.Modules.Api.CatalogApi(factory);

        ExternalRouteHealthService.RoutesFor(connectivity, await ComponentIdsOf(ours))
            .Select(r => r.Hostname).Should().Equal(["ours.example.com"]);
        ExternalRouteHealthService.RoutesFor(connectivity, await ComponentIdsOf(theirs))
            .Select(r => r.Hostname).Should().Equal(["theirs.example.com"]);

        async Task<List<Guid>> ComponentIdsOf(Guid tenantId) =>
            [.. (await catalog.GetComponentsForTenantAsync(tenantId)).Select(c => c.Id)];

        void Seed(Guid tenantId, string hostname)
        {
            Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid(), componentId = Guid.NewGuid();

            db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = hostname });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                Name = hostname, ApiServerUrl = "https://k8s.example.com",
            });
            db.ClusterComponents.Add(new ClusterComponent
            {
                Id = componentId, ClusterId = clusterId, Name = "traefik",
                ComponentType = "Ingress", Status = ComponentStatus.Installed,
            });
            db.Set<ExternalRoute>().Add(new ExternalRoute
            {
                Id = Guid.NewGuid(), ComponentId = componentId, Hostname = hostname,
            });
            db.SaveChanges();
        }
    }

    /// <summary>
    /// The observed set is the sweep's work list, and the sweep reaches a cluster for every entry
    /// on it. Drawn installation-wide, the refresher read each secret from the cluster the row
    /// named, with the credential resolved from that row's own tenant — so the work list was also
    /// the authorization.
    /// </summary>
    [Fact]
    public void The_observed_secret_set_is_drawn_per_tenant()
    {
        Guid oursSecret = SeedObservedSecret(ours, "ours-app-tls");
        Guid theirsSecret = SeedObservedSecret(theirs, "theirs-app-tls");

        using SecretsDbContext secrets = ((IDbContextFactory<SecretsDbContext>)factory).CreateDbContext();

        VaultService.ObservedAppSecretsFor(secrets, ours).Select(s => s.Id)
            .Should().Equal([oursSecret]);
        VaultService.ObservedAppSecretsFor(secrets, theirs).Select(s => s.Id)
            .Should().Equal([theirsSecret]);
    }

    /// <summary>
    /// The sweep's own policy has to survive the tenant filter: a secret whose sync is ENABLED is
    /// owned by EntKube and must not be refreshed from the cluster for anybody — refreshing it
    /// would overwrite the vault copy with whatever is live, which is the opposite direction.
    /// </summary>
    [Fact]
    public void An_owned_secret_is_not_observed_for_anybody()
    {
        Guid id = SeedObservedSecret(ours, "taken-over", syncEnabled: true);

        using SecretsDbContext secrets = ((IDbContextFactory<SecretsDbContext>)factory).CreateDbContext();

        VaultService.ObservedAppSecretsFor(secrets, ours).Select(s => s.Id)
            .Should().NotContain(id, "enabling sync is how an operator takes ownership");
    }

    /// <summary>
    /// Uptime snapshots authorize nothing, so this asserts the isolation rather than a boundary:
    /// each tenant's cycle sees its own deployments, and tenancy reaches a deployment the long way
    /// round — through its app's customer.
    /// </summary>
    [Fact]
    public void An_uptime_sweep_snapshots_only_the_asking_tenants_deployments()
    {
        Guid oursDeployment = SeedDeployment(ours, "ours-api");
        Guid theirsDeployment = SeedDeployment(theirs, "theirs-api");

        UptimeTrackingService.DeploymentsFor(db, ours).Select(d => d.Id)
            .Should().Equal([oursDeployment]);
        UptimeTrackingService.DeploymentsFor(db, theirs).Select(d => d.Id)
            .Should().Equal([theirsDeployment]);
    }

    private Guid SeedObservedSecret(Guid tenantId, string name, bool syncEnabled = false)
    {
        SecretVault vault = db.Set<SecretVault>().FirstOrDefault(v => v.TenantId == tenantId)
            ?? AddVault(tenantId);

        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = name });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = name });
        db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = name });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = name, ApiServerUrl = "https://k8s.example.com",
        });

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(), VaultId = vault.Id, Name = name,
            EncryptedValue = [1], Nonce = [2],
            AppId = appId,
            SyncToKubernetes = syncEnabled,
            KubernetesClusterId = clusterId,
            KubernetesSecretName = name,
            KubernetesNamespace = "prod",
        };
        db.Set<VaultSecret>().Add(secret);
        db.SaveChanges();
        return secret.Id;
    }

    private SecretVault AddVault(Guid tenantId)
    {
        SecretVault vault = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            EncryptedDataKey = [1], Nonce = [2],
        };
        db.Set<SecretVault>().Add(vault);
        db.SaveChanges();
        return vault;
    }

    private Guid SeedDeployment(Guid tenantId, string name)
    {
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        Guid deploymentId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = name });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = name });
        db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = name });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = name, ApiServerUrl = "https://k8s.example.com",
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = name,
            EnvironmentId = envId, ClusterId = clusterId, Namespace = name,
        });
        db.SaveChanges();
        return deploymentId;
    }

    private static SupportMailbox NewMailbox(Guid tenantId, string username) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, IsEnabled = true,
        Host = "mail.example.com", Username = username,
    };
}
