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
    /// </summary>
    [Fact]
    public void A_route_health_sweep_reaches_a_tenants_routes_through_the_cluster()
    {
        Seed(ours, "ours.example.com");
        Seed(theirs, "theirs.example.com");

        using ConnectivityDbContext connectivity =
            ((IDbContextFactory<ConnectivityDbContext>)factory).CreateDbContext();

        ExternalRouteHealthService.RoutesFor(connectivity, ours)
            .Select(r => r.Hostname).Should().Equal(["ours.example.com"]);
        ExternalRouteHealthService.RoutesFor(connectivity, theirs)
            .Select(r => r.Hostname).Should().Equal(["theirs.example.com"]);

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

    private static SupportMailbox NewMailbox(Guid tenantId, string username) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, IsEnabled = true,
        Host = "mail.example.com", Username = username,
    };
}
