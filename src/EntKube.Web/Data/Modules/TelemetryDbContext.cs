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
    public DbSet<FeedbackItem> FeedbackItems => Set<FeedbackItem>();
    public DbSet<FeedbackScreenshot> FeedbackScreenshots => Set<FeedbackScreenshot>();
    public DbSet<SavedLogQuery> SavedLogQueries => Set<SavedLogQuery>();
    public DbSet<SyntheticCheck> SyntheticChecks => Set<SyntheticCheck>();
    public DbSet<SyntheticCheckResult> SyntheticCheckResults => Set<SyntheticCheckResult>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<AlertIncident> AlertIncidents => Set<AlertIncident>();
    public DbSet<IncidentNote> IncidentNotes => Set<IncidentNote>();
    public DbSet<MetricRollup> MetricRollups => Set<MetricRollup>();
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

    /// <summary>
    /// Environment, read-only: this module may look one up, never write it.
    ///
    /// <para>The fifth hub, and it was missing. <b>24 entities across 7 modules carry an
    /// <c>EnvironmentId</c></b> — Fleet's clusters, Secrets' vault secrets, Identity's JIT
    /// grants, DataServices' storage links, Catalog's certificate distributions, Connectivity's
    /// network policies — which is the same concentration that earned Tenant, Customer, App and
    /// KubernetesCluster their place here. The entity's own doc has said so since it was written:
    /// "Clusters, services, and other resources are scoped to an environment within a tenant."
    /// It is a tenant-scoped dimension row carrying an id, a tenant and a name.</para>
    ///
    /// <para>It was reachable only from <c>DeliveryDbContext</c>, which owns the table because
    /// <c>AppEnvironment</c> and <c>CustomerEnvironment</c> live beside it. That ownership is
    /// right and is unchanged; what was wrong was that every other module had to hold the
    /// all-seeing context to ask which stage a row belonged to.</para>
    /// </summary>
    public IQueryable<Data.Environment> Environments => Set<Data.Environment>().AsNoTracking();
}
