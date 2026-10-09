using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// What a component id from the wrong tenant now gets.
///
/// <para><b>Why behaviour and not only the sweep.</b>
/// <see cref="ComponentTenantScopingTests"/> is a text sweep: it can see that a predicate is
/// written and cannot see whether it does anything. These drive the services with a real id
/// belonging to another tenant — which is the thing an operator, a stale bookmark or a crafted
/// form post would actually send — and assert the answer is a refusal rather than another
/// tenant's component.</para>
///
/// <para><b>Two of the four fixes are not reachable this way, and that is worth stating rather
/// than papering over.</b> <c>StalwartService.StoreManifestAsync</c> is private and every caller
/// resolves the component through a tenant-scoped lookup first, so a wrong tenant returns before
/// reaching it — its own predicate is defence in depth against a future caller, not a path a test
/// can exercise today. <c>HeadscaleService.EnsureExternalRouteAfterInstallAsync</c> is left
/// unscoped on purpose; the reason is on the method.</para>
/// </summary>
public class ComponentTenantReachTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();

    /// <summary>A component belonging to the other tenant — the id under test throughout.</summary>
    private Guid theirComponentId;

    /// <summary>The cluster that component is on, so a colliding AppRoute can be put beside it.</summary>
    private Guid theirClusterId;

    private Guid theirEnvironmentId;

    public ComponentTenantReachTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        dbFactory = new TestDbContextFactory(connection);

        db.Tenants.Add(new Tenant { Id = ours, Name = "Ours", Slug = $"ours-{ours:N}" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Theirs", Slug = $"theirs-{theirs:N}" });

        (theirComponentId, theirClusterId, theirEnvironmentId) =
            SeedComponent(theirs, "their-cluster", "stalwart");
        SeedComponent(ours, "our-cluster", "traefik");

        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private (Guid ComponentId, Guid ClusterId, Guid EnvironmentId) SeedComponent(
        Guid tenantId, string clusterName, string componentName)
    {
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid componentId = Guid.NewGuid();

        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = clusterName,
            ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = componentName,
            ComponentType = "HelmChart", Namespace = componentName,
            ReleaseName = componentName, Status = ComponentStatus.Installed,
        });

        return (componentId, clusterId, envId);
    }

    private ExternalRouteService Routes() =>
        new(dbFactory, NullLogger<ExternalRouteService>.Instance);

    // ════════════════════════════════════════════════════════════════
    //  ExternalRouteService
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Exposing a component means creating a public hostname that serves it. Doing that to another
    /// tenant's component is the worst thing on this list, which is why it is first.
    /// </summary>
    [Fact]
    public async Task A_route_cannot_be_added_to_another_tenants_component()
    {
        Func<Task> act = () => Routes().AddRouteAsync(ours, theirComponentId, new ExternalRouteRequest
        {
            Hostname = "stolen.example.com",
            ServiceName = "stalwart",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");

        db.ExternalRoutes.Should().BeEmpty("nothing may be written for a component we cannot see");
    }

    [Fact]
    public async Task A_route_can_still_be_added_to_our_own_component()
    {
        Guid ourComponentId = db.ClusterComponents
            .Include(c => c.Cluster)
            .First(c => c.Cluster.TenantId == ours).Id;

        ExternalRoute route = await Routes().AddRouteAsync(ours, ourComponentId,
            new ExternalRouteRequest
            {
                Hostname = "ours.example.com",
                ServiceName = "traefik",
                ServicePort = 80,
                TlsMode = TlsMode.ClusterIssuer,
                ClusterIssuerName = "letsencrypt",
            });

        route.Hostname.Should().Be("ours.example.com");
    }

    /// <summary>
    /// The shadowing check answers "which of this component's hostnames are also claimed by an
    /// app on the same cluster" — so unscoped it reports another tenant's hostnames back into our
    /// UI, which is a disclosure rather than a mutation. It returns an empty set rather than
    /// throwing, matching how it already treats a component that does not exist.
    /// </summary>
    /// <summary>
    /// A real collision on the other tenant's cluster: one of their component's hostnames also
    /// claimed by one of their apps. Seeded rather than asserted, because an empty answer proves
    /// nothing on its own — with nothing to find, this method returns an empty set whether it is
    /// scoped or not, and the first version of this test passed against the unscoped code.
    /// </summary>
    private async Task<string> GivenACollisionOnTheirClusterAsync()
    {
        const string collision = "mail.theirs.example.com";

        db.ExternalRoutes.Add(new ExternalRoute
        {
            Id = Guid.NewGuid(),
            ComponentId = theirComponentId,
            Hostname = collision,
            ServiceName = "stalwart",
            ServicePort = 80,
            PathPrefix = "/",
            TlsMode = TlsMode.ClusterIssuer,
        });

        // Saved in stages: as one batch EF orders the inserts so that a foreign key is not yet
        // satisfied when its row lands, and SQLite rejects the whole thing.
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();
        Guid deploymentId = Guid.NewGuid();
        Guid appRouteId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = theirs, Name = "Their Customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "their-app" });
        await db.SaveChangesAsync();
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "their-deploy",
            Type = DeploymentType.Manual, ClusterId = theirClusterId,
            EnvironmentId = theirEnvironmentId, Namespace = "their-ns",
        });
        await db.SaveChangesAsync();
        db.Set<AppRoute>().Add(new AppRoute
        {
            Id = appRouteId, AppId = appId, Hostname = collision, IsEnabled = true,
        });
        await db.SaveChangesAsync();
        db.Set<AppDeploymentRoute>().Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(), AppRouteId = appRouteId, AppDeploymentId = deploymentId,
            PathPrefix = "/", ServiceName = "their-svc", ServicePort = 80, IsEnabled = true,
        });
        await db.SaveChangesAsync();

        return collision;
    }

    [Fact]
    public async Task Shadowed_hostnames_are_not_reported_for_another_tenants_component()
    {
        await GivenACollisionOnTheirClusterAsync();

        IReadOnlySet<string> shadowed = await Routes().ShadowedHostnamesAsync(ours, theirComponentId);

        shadowed.Should().BeEmpty(
            "unscoped this reports that hostname, which is the other tenant's app and its cluster "
            + "appearing in our UI");
    }

    /// <summary>
    /// The same collision, seen by the tenant it belongs to — so the empty answer above is the
    /// scoping and not the method having stopped working.
    /// </summary>
    [Fact]
    public async Task Shadowed_hostnames_are_still_reported_to_the_tenant_that_owns_them()
    {
        string collision = await GivenACollisionOnTheirClusterAsync();

        IReadOnlySet<string> shadowed = await Routes()
            .ShadowedHostnamesAsync(theirs, theirComponentId);

        shadowed.Should().BeEquivalentTo([collision]);
    }

    // ════════════════════════════════════════════════════════════════
    //  SupportMailboxService
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// A mailbox row naming another tenant's mail server. Before the predicate this resolved, and
    /// resolving is what produces the host and the credential to connect with — so the next step
    /// was an IMAP session against a server this tenant does not own, reading its mail into this
    /// tenant's triage queue.
    ///
    /// <para>It fails as a reported state rather than an exception, because that is what
    /// <c>TestAsync</c> is for: the configuration screen shows the operator why.</para>
    /// </summary>
    [Fact]
    public async Task A_mailbox_cannot_resolve_against_another_tenants_mail_server()
    {
        db.Set<SupportMailbox>().Add(new SupportMailbox
        {
            Id = Guid.NewGuid(),
            TenantId = ours,
            StalwartComponentId = theirComponentId,
            StalwartAccountId = Guid.NewGuid(),
            Host = "",
            Username = "",
            IsEnabled = true,
        });
        await db.SaveChangesAsync();

        SupportMailboxService mailboxes = new(
            dbFactory,
            new VaultService(dbFactory, new VaultEncryptionService(TestRootKey)),
            null!, null!, null!,
            NullLogger<SupportMailboxService>.Instance);

        MailPollResult result = await mailboxes.TestAsync(ours);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("no longer exists",
            "the refusal has to read as a configuration problem the operator can fix, not as a "
            + "connection failure they will retry");
    }
}
