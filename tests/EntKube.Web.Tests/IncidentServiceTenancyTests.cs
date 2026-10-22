using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Every <c>IncidentService</c> entry point that takes an incident id refuses another tenant's.
///
/// <para><b>This is the service's first test file.</b> It has 25 public methods and backs the
/// operator incident queue, the fleet-wide advisor and two customer-facing portal pages, and
/// twelve of those methods took an id with no tenant.</para>
///
/// <para>⚠️ <c>AlertIncident</c> carries no <c>TenantId</c> — only a <c>ClusterId</c> — so the
/// scope travels <c>i.Cluster.TenantId</c>, which is what the already-correct
/// <c>GetIncidentsForTenantAsync</c> has always done. The missing column is the likeliest reason
/// these never gained the parameter: there was nowhere on the row to put it. That is the third
/// entity this session where the schema predicted which methods stayed unscoped
/// (<c>DatabaseBinding</c> and <c>StorageBinding</c> were the others).</para>
///
/// <para>Each case seeds an incident that <em>would</em> be returned or written if the predicate
/// were missing, and asserts the owner's call first — an empty result proves refusal only when a
/// row exists.</para>
/// </summary>
public class IncidentServiceTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly IncidentService sut;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourCluster, theirCluster, ourIncident, theirIncident;

    public IncidentServiceTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        TestDbContextFactory factory = new(connection);
        // GetUptimeAsync reads Delivery's health snapshots, and now asks Delivery's contract for
        // the aggregate rather than walking s.Deployment.App.Customer.TenantId itself. The real
        // implementation is used, not a stub, because the predicate under test now lives in it.
        sut = new IncidentService(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory));

        ourCluster = Seed(ourTenant, "ours");
        theirCluster = Seed(theirTenant, "theirs");
        ourIncident = Incident(ourCluster, "ours-down");
        theirIncident = Incident(theirCluster, "theirs-down");
        db.SaveChanges();
    }

    private Guid Seed(Guid tenantId, string slug)
    {
        Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = slug, Slug = slug });
        db.Environments.Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{slug}-1", ApiServerUrl = $"https://{slug}.example",
        });
        return clusterId;
    }

    private Guid Incident(Guid clusterId, string name)
    {
        Guid id = Guid.NewGuid();
        db.AlertIncidents.Add(new AlertIncident
        {
            Id = id,
            ClusterId = clusterId,
            Fingerprint = name,
            AlertName = name,
            Severity = "critical",
            StartsAt = DateTime.UtcNow.AddHours(-1),
            Status = IncidentStatus.Active,
        });
        return id;
    }

    [Fact]
    public async Task A_stranger_cannot_read_another_tenants_incident()
    {
        (await sut.GetByIdAsync(ourTenant, ourIncident)).Should().NotBeNull();
        (await sut.GetByIdAsync(theirTenant, ourIncident)).Should().BeNull(
            "the row exists, so this is a refusal and not an absence");
    }

    [Fact]
    public async Task A_stranger_cannot_acknowledge_resolve_or_assign_another_tenants_incident()
    {
        await sut.AcknowledgeAsync(theirTenant, ourIncident, "them");
        await sut.ResolveAsync(theirTenant, ourIncident, "them");
        await sut.AssignAsync(theirTenant, ourIncident, "them");

        db.ChangeTracker.Clear();
        AlertIncident after = db.AlertIncidents.Single(i => i.Id == ourIncident);
        after.Status.Should().Be(IncidentStatus.Active, "none of the three may land");
        after.AcknowledgedBy.Should().BeNull();

        // The owner still can.
        await sut.AcknowledgeAsync(ourTenant, ourIncident, "us");
        db.ChangeTracker.Clear();
        db.AlertIncidents.Single(i => i.Id == ourIncident).AcknowledgedBy.Should().Be("us");
    }

    /// <summary>
    /// ⚠️ A bulk call applies the tenant clause to the <b>set</b>. A batch carrying a mix must
    /// act on this tenant's incidents and leave the rest alone — not refuse wholesale, and
    /// certainly not act on all of them.
    /// </summary>
    [Fact]
    public async Task A_bulk_acknowledge_touches_only_this_tenants_incidents()
    {
        await sut.BulkAcknowledgeAsync(ourTenant, [ourIncident, theirIncident], "us");

        db.ChangeTracker.Clear();
        db.AlertIncidents.Single(i => i.Id == ourIncident).Status
            .Should().Be(IncidentStatus.Acknowledged, "ours is in the batch and is ours");
        db.AlertIncidents.Single(i => i.Id == theirIncident).Status
            .Should().Be(IncidentStatus.Active, "theirs was in the same batch and must be untouched");
    }

    /// <summary>
    /// <c>AddNoteAsync</c> writes without reading — it inserted the note for whatever id it was
    /// handed — so only the stored notes can say it was refused.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_annotate_another_tenants_incident()
    {
        await sut.AddNoteAsync(theirTenant, ourIncident, "them", "injected");

        db.ChangeTracker.Clear();
        db.IncidentNotes.Where(n => n.IncidentId == ourIncident)
            .Should().NotContain(n => n.Content == "injected");

        await sut.AddNoteAsync(ourTenant, ourIncident, "us", "legitimate");
        db.ChangeTracker.Clear();
        db.IncidentNotes.Where(n => n.IncidentId == ourIncident)
            .Should().Contain(n => n.Content == "legitimate", "the owner can annotate");
    }

    [Fact]
    public async Task A_stranger_cannot_list_incidents_for_another_tenants_deployments()
    {
        // The method takes ENTITIES and derives its cluster set from them, so the deployment is
        // constructed rather than read — which is exactly the trust this predicate removes.
        List<AppDeployment> deployments =
        [
            new()
            {
                Id = Guid.NewGuid(), AppId = Guid.NewGuid(), ClusterId = ourCluster,
                EnvironmentId = Guid.NewGuid(), Name = "ours", Namespace = "",
            },
        ];

        (await sut.GetIncidentsForDeploymentsAsync(ourTenant, deployments))
            .Should().ContainSingle("the owner sees its own incident");

        (await sut.GetIncidentsForDeploymentsAsync(theirTenant, deployments))
            .Should().BeEmpty("a caller-supplied deployment list is not an entitlement");
    }

    /// <summary>
    /// <c>GetUptimeAsync</c> refuses another tenant's deployment.
    ///
    /// <para><b>This test did not exist, and the predicate it covers has just moved.</b> The method
    /// used to apply the scope itself by walking <c>s.Deployment.App.Customer.TenantId</c> — three
    /// navigations out of Telemetry and across two other modules — and now asks
    /// <c>IDeliveryApi.GetHealthRatiosAsync</c>, which applies the same scope on Delivery's own
    /// side. Nothing proved the predicate was there before the move, so nothing would have noticed
    /// if the move had dropped it.</para>
    ///
    /// <para>The samples are seeded so the owner gets a real figure: an empty answer proves a
    /// refusal only when there is something to refuse.</para>
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_read_another_tenants_uptime()
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), deploymentId = Guid.NewGuid();
        Guid envId = db.KubernetesClusters.Single(c => c.Id == ourCluster).EnvironmentId;

        db.Customers.Add(new Customer { Id = customerId, TenantId = ourTenant, Name = "ours" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "ours-app" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, ClusterId = ourCluster, EnvironmentId = envId,
            Name = "ours-deploy", Namespace = "ours",
        });

        // Three healthy, one degraded — a mixed fixture, so a miscount shows as the wrong
        // percentage rather than as a missing one.
        foreach (HealthStatus status in new[]
        {
            HealthStatus.Healthy, HealthStatus.Healthy, HealthStatus.Healthy, HealthStatus.Degraded,
        })
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(), DeploymentId = deploymentId, HealthStatus = status,
                SnapshotAt = DateTime.UtcNow.AddMinutes(-5),
            });
        }
        await db.SaveChangesAsync();

        (await sut.GetUptimeAsync(ourTenant, deploymentId, 30)).Percent
            .Should().Be(75, "three of four samples were healthy");

        (await sut.GetUptimeAsync(theirTenant, deploymentId, 30)).Percent
            .Should().BeNull("the deployment exists and has samples, so this is a refusal");
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
