using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Two histories that were readable by id alone.
///
/// <para>Both of these are <b>reads</b>, and neither row carries a tenant of its own — which is
/// why they outlasted the obvious targets. An <c>AuditEvent</c> hangs off a deployment and an
/// <c>ExternalRouteHealthHistory</c> hangs off a route, and in both cases the owning table
/// belongs to a different module, so the tenant could not be reached by adding a column to the
/// predicate. It had to be asked for.</para>
///
/// <para>What they give away is a record over time. The audit trail names what was done, to
/// which resource, by whom — restarts, scales, helm installs, with the operator's address in
/// <c>PerformedBy</c>, so it is an operations diary and a staff list at once. The uptime history
/// says when somebody else's hostname was down and for how long.</para>
/// </summary>
public class AuditAndUptimeTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public AuditAndUptimeTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
        db.SaveChanges();

        factory = new TestDbContextFactory(connection);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private AuditService Audit() =>
        new(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory));

    /// <summary>Seeds a deployment owned by <paramref name="owner"/> and one audit event on it.</summary>
    private Guid SeedDeploymentWithAudit(Guid owner)
    {
        string tag = $"{Guid.NewGuid():N}"[..8];
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid();
        Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid(), deploymentId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = owner, Name = $"Kund-{tag}" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "journal" });
        db.Environments.Add(new Data.Environment
        {
            Id = envId, TenantId = owner, Name = $"production-{tag}",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = owner, EnvironmentId = envId,
            Name = $"prod-{tag}", ApiServerUrl = "https://k8s.example.invalid",
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, EnvironmentId = envId, ClusterId = clusterId,
            Name = "journal", Type = DeploymentType.Yaml, Namespace = "journal",
        });
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Action = "RestartWorkload",
            ResourceKind = "Deployment",
            ResourceName = "journal-api",
            Details = "Restarted after the 02:00 incident.",
            PerformedBy = "lakare@region.example",
            OccurredAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        return deploymentId;
    }

    [Fact]
    public async Task Another_tenants_audit_trail_is_not_returned()
    {
        Guid theirs = SeedDeploymentWithAudit(otherTenantId);

        List<AuditEvent> events = await Audit().GetDeploymentEventsAsync(tenantId, theirs);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Our_own_audit_trail_is_still_returned()
    {
        Guid ours = SeedDeploymentWithAudit(tenantId);

        List<AuditEvent> events = await Audit().GetDeploymentEventsAsync(tenantId, ours);

        events.Should().ContainSingle()
            .Which.PerformedBy.Should().Be("lakare@region.example");
    }

    /// <summary>
    /// A deployment that does not exist at all must look the same as one that is not ours — the
    /// contract answers null to both, and the empty list is what the caller renders.
    /// </summary>
    [Fact]
    public async Task An_unknown_deployment_returns_no_events()
    {
        (await Audit().GetDeploymentEventsAsync(tenantId, Guid.NewGuid())).Should().BeEmpty();
    }

    // ── Route uptime ──────────────────────────────────────────────────────────

    private ExternalRouteService Routes() =>
        new(factory, new EntKube.Web.Modules.Api.CatalogApi(factory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExternalRouteService>.Instance);

    /// <summary>Seeds a route owned by <paramref name="owner"/> with two health samples.</summary>
    private Guid SeedRouteWithHistory(Guid owner)
    {
        string tag = $"{Guid.NewGuid():N}"[..8];
        Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid();
        Guid componentId = Guid.NewGuid(), routeId = Guid.NewGuid();

        db.Environments.Add(new Data.Environment
        {
            Id = envId, TenantId = owner, Name = $"production-{tag}",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = owner, EnvironmentId = envId,
            Name = $"prod-{tag}", ApiServerUrl = "https://k8s.example.invalid",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = $"harbor-{tag}",
            ComponentType = "HelmChart", Namespace = "harbor",
            Status = ComponentStatus.Installed,
        });
        db.Set<ExternalRoute>().Add(new ExternalRoute
        {
            Id = routeId, ComponentId = componentId,
            Hostname = $"registry-{tag}.example", ServiceName = "harbor-core", ServicePort = 80,
        });
        db.Set<ExternalRouteHealthHistory>().AddRange(
            new ExternalRouteHealthHistory
            {
                Id = Guid.NewGuid(), RouteId = routeId, IsReachable = true,
                StatusCode = 200, ResponseMs = 120, CheckedAt = DateTime.UtcNow.AddHours(-2),
            },
            new ExternalRouteHealthHistory
            {
                Id = Guid.NewGuid(), RouteId = routeId, IsReachable = false,
                StatusCode = 503, ResponseMs = 4000, CheckedAt = DateTime.UtcNow.AddHours(-1),
            });
        db.SaveChanges();

        return routeId;
    }

    [Fact]
    public async Task Another_tenants_route_uptime_is_refused()
    {
        Guid theirs = SeedRouteWithHistory(otherTenantId);

        Func<Task> act = () => Routes().GetRouteUptimeAsync(tenantId, theirs);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Route not found.");
    }

    [Fact]
    public async Task Our_own_route_uptime_is_still_returned()
    {
        Guid ours = SeedRouteWithHistory(tenantId);

        RouteUptimeSummary summary = await Routes().GetRouteUptimeAsync(tenantId, ours);

        summary.History.Should().HaveCount(2);
        summary.UptimePercent.Should().Be(50);
    }
}
