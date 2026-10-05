using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Telemetry module's view of the database — dashboards, alert rules, incidents and notification delivery.
///
/// <para>Exposes only the 11 tables Telemetry owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : ModuleDbContext(options)
{
    public DbSet<TelemetryAlertRule> TelemetryAlertRules => Set<TelemetryAlertRule>();
    public DbSet<TelemetrySegment> TelemetrySegments => Set<TelemetrySegment>();
    public DbSet<TelemetryStorageSetting> TelemetryStorageSettings => Set<TelemetryStorageSetting>();
    public DbSet<RumSite> RumSites => Set<RumSite>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<AlertIncident> AlertIncidents => Set<AlertIncident>();
    public DbSet<IncidentNote> IncidentNotes => Set<IncidentNote>();
    public DbSet<AlertRoutingRule> AlertRoutingRules => Set<AlertRoutingRule>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationProviderConfig> NotificationProviderConfigs => Set<NotificationProviderConfig>();

    // ---- Hubs owned by other modules -------------------------------------------------
    // The measurement in docs/decomposition.md §4.0 found 70 of 164 cross-module foreign
    // keys point at Tenant or Customer, and another 40 at App or KubernetesCluster. Making
    // those four readable here is what stops the boundary being merely annoying. They are
    // IQueryable rather than DbSet on purpose: readable, not writable, and not trackable.

    /// <summary>Tenant, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Tenant> Tenants => Set<Tenant>().AsNoTracking();

    /// <summary>Customer, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Customer> Customers => Set<Customer>().AsNoTracking();

    /// <summary>App, read-only: this module may look one up, never write it.</summary>
    public IQueryable<App> Apps => Set<App>().AsNoTracking();

    /// <summary>KubernetesCluster, read-only: this module may look one up, never write it.</summary>
    public IQueryable<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>().AsNoTracking();
}
