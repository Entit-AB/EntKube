using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Three background sweeps that used to read the whole installation and take the tenant off each
/// row they found, now asking per tenant (docs/decomposition.md §4.0.1.2).
///
/// <para><b>Why the derived tenant mattered in each.</b> It was never just a label:</para>
/// <list type="bullet">
///   <item><c>AlertEscalationService</c> re-notified through the tenant read off the incident's
///   cluster, so the escalation was <em>sent as</em> that tenant.</item>
///   <item><c>KeycloakBackupSchedulerService</c> passed it into <c>CreateBackupAsync</c>, which is
///   what resolves the realm's cluster credential and its storage link.</item>
///   <item><c>BootstrapRunnerService</c> passed it to the component registrar and the install
///   orchestrator — the tenant a whole cluster bootstrap acts as.</item>
/// </list>
///
/// <para>Each selection is now a named static query, which is what makes this assertable at all:
/// the previous shape returned every row in the installation and would satisfy any test that only
/// counted what came back for one tenant. Here two tenants each own a row that qualifies, and each
/// must see exactly their own — the second half being the one that fails if a predicate is simply
/// missing rather than wrong.</para>
/// </summary>
public sealed class SweepTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantA = Guid.NewGuid();
    private readonly Guid tenantB = Guid.NewGuid();
    private readonly Guid clusterA = Guid.NewGuid();
    private readonly Guid clusterB = Guid.NewGuid();

    /// <summary>Per tenant: the Keycloak component config its realms hang off, and a place to put a backup.</summary>
    private readonly Dictionary<Guid, (Guid ConfigId, Guid StorageLinkId)> keycloak = [];

    public SweepTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Database.EnsureCreated();
        }
        factory = new TestDbContextFactory(connection);

        using ApplicationDbContext db = factory.CreateDbContext();
        foreach ((Guid tenantId, Guid clusterId, string name) in
                 new[] { (tenantA, clusterA, "a"), (tenantB, clusterB, "b") })
        {
            Guid envId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"{name}-{tenantId:N}" });
            db.Environments.Add(new Web.Data.Environment { Id = envId, TenantId = tenantId, Name = "prod" });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                Name = $"cluster-{name}", ApiServerUrl = "https://k8s.example.com",
            });

            Guid configId = Guid.NewGuid(), storageLinkId = Guid.NewGuid();
            keycloak[tenantId] = (configId, storageLinkId);
            db.Set<KeycloakComponentConfig>().Add(new KeycloakComponentConfig
            {
                Id = configId, TenantId = tenantId, DisplayName = $"keycloak-{name}",
            });
            db.Set<StorageLink>().Add(new StorageLink
            {
                Id = storageLinkId, TenantId = tenantId, EnvironmentId = envId,
                Name = $"{name}-bucket", Provider = StorageProvider.MinIO,
                Endpoint = "http://minio.minio.svc.cluster.local:9000", BucketName = $"{name}-bucket",
            });
        }
        db.SaveChanges();
    }

    public void Dispose() => connection.Dispose();

    // ── the alert escalation sweep ───────────────────────────────────────────────────

    [Fact]
    public void An_escalation_sweep_sees_only_the_asking_tenants_unacknowledged_incidents()
    {
        DateTime now = DateTime.UtcNow;
        using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.AlertIncidents.Add(NewIncident(clusterA, now));
            db.AlertIncidents.Add(NewIncident(clusterB, now));
            db.SaveChanges();
        }

        using TelemetryDbContext telemetry = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();

        AlertEscalationService.DueFor(telemetry, tenantA, now.AddMinutes(-15))
            .Select(i => i.ClusterId).Should().Equal([clusterA]);
        AlertEscalationService.DueFor(telemetry, tenantB, now.AddMinutes(-15))
            .Select(i => i.ClusterId).Should().Equal([clusterB]);
    }

    [Fact]
    public void An_acknowledged_or_already_escalated_incident_is_not_a_candidate()
    {
        DateTime now = DateTime.UtcNow;
        using (ApplicationDbContext db = factory.CreateDbContext())
        {
            AlertIncident acknowledged = NewIncident(clusterA, now);
            acknowledged.Status = IncidentStatus.Acknowledged;
            AlertIncident escalated = NewIncident(clusterA, now);
            escalated.EscalatedAt = now;
            AlertIncident tooRecent = NewIncident(clusterA, now);
            tooRecent.StartsAt = now;           // inside the threshold
            db.AlertIncidents.AddRange(acknowledged, escalated, tooRecent);
            db.SaveChanges();
        }

        using TelemetryDbContext telemetry = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();

        AlertEscalationService.DueFor(telemetry, tenantA, now.AddMinutes(-15))
            .Should().BeEmpty("the tenant filter must not be the only thing the predicate still does");
    }

    // ── the Keycloak backup scheduler ────────────────────────────────────────────────

    [Fact]
    public void A_backup_scheduler_sees_only_the_asking_tenants_scheduled_realms()
    {
        using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.KeycloakRealms.Add(NewRealm(tenantA, "a-realm", "0 2 * * *", keycloak[tenantA].StorageLinkId));
            db.KeycloakRealms.Add(NewRealm(tenantB, "b-realm", "0 3 * * *", keycloak[tenantB].StorageLinkId));
            // Same tenant, but nothing to schedule or nowhere to put it.
            db.KeycloakRealms.Add(NewRealm(tenantA, "a-unscheduled", null, keycloak[tenantA].StorageLinkId));
            db.KeycloakRealms.Add(NewRealm(tenantA, "a-no-storage", "0 2 * * *", null));
            db.SaveChanges();
        }

        using IdentityDbContext identity = ((IDbContextFactory<IdentityDbContext>)factory).CreateDbContext();

        KeycloakBackupSchedulerService.ScheduledFor(identity, tenantA)
            .Select(r => r.RealmName).Should().Equal(["a-realm"]);
        KeycloakBackupSchedulerService.ScheduledFor(identity, tenantB)
            .Select(r => r.RealmName).Should().Equal(["b-realm"]);
    }

    // ── the bootstrap queue ──────────────────────────────────────────────────────────

    [Fact]
    public void The_bootstrap_queue_is_read_per_tenant_oldest_first()
    {
        DateTime now = DateTime.UtcNow;
        using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.BootstrapRuns.Add(NewRun(clusterA, "second", now));
            db.BootstrapRuns.Add(NewRun(clusterA, "first", now.AddMinutes(-5)));
            db.BootstrapRuns.Add(NewRun(clusterB, "theirs", now.AddMinutes(-10)));
            BootstrapRun running = NewRun(clusterA, "already-running", now.AddMinutes(-20));
            running.Status = BootstrapRunStatus.Running;
            db.BootstrapRuns.Add(running);
            db.SaveChanges();
        }

        using ApplicationDbContext app = factory.CreateDbContext();

        BootstrapRunnerService.QueuedFor(app, tenantA)
            .Select(r => r.BlueprintName).Should().Equal(["first", "second"]);
        BootstrapRunnerService.QueuedFor(app, tenantB)
            .Select(r => r.BlueprintName).Should().Equal(["theirs"],
                "the other tenant's queue is not this tenant's work, however old it is");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static AlertIncident NewIncident(Guid clusterId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), ClusterId = clusterId,
        Fingerprint = $"fp:{Guid.NewGuid():N}", AlertName = "HighErrorRate", Severity = "critical",
        Status = IncidentStatus.Active, StartsAt = now.AddHours(-1),
    };

    private KeycloakRealm NewRealm(Guid tenantId, string name, string? schedule, Guid? storageLinkId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, KeycloakComponentConfigId = keycloak[tenantId].ConfigId,
        RealmName = name, DisplayName = name,
        BackupSchedule = schedule, StorageLinkId = storageLinkId,
    };

    private static BootstrapRun NewRun(Guid clusterId, string blueprintName, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(), ClusterId = clusterId, BlueprintId = Guid.NewGuid(),
        BlueprintName = blueprintName, Status = BootstrapRunStatus.Queued, CreatedAt = createdAt,
    };
}
