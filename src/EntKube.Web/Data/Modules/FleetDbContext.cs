using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Fleet module's view of the database — clusters, nodes, cloud connections, blueprints and bootstrap runs.
///
/// <para>Exposes only the 13 tables Fleet owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class FleetDbContext(DbContextOptions<FleetDbContext> options) : ModuleDbContext(options)
{
    public DbSet<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>();
    public DbSet<ClusterServer> ClusterServers => Set<ClusterServer>();
    public DbSet<OpenStackConnection> OpenStackConnections => Set<OpenStackConnection>();
    public DbSet<EgressAgent> EgressAgents => Set<EgressAgent>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<ClusterBlueprint> ClusterBlueprints => Set<ClusterBlueprint>();
    public DbSet<BlueprintStep> BlueprintSteps => Set<BlueprintStep>();
    public DbSet<BlueprintVariable> BlueprintVariables => Set<BlueprintVariable>();
    public DbSet<BlueprintVariableValue> BlueprintVariableValues => Set<BlueprintVariableValue>();
    public DbSet<BlueprintRollout> BlueprintRollouts => Set<BlueprintRollout>();
    public DbSet<BlueprintRolloutTarget> BlueprintRolloutTargets => Set<BlueprintRolloutTarget>();
    public DbSet<BootstrapRun> BootstrapRuns => Set<BootstrapRun>();
    public DbSet<BootstrapStepRun> BootstrapStepRuns => Set<BootstrapStepRun>();

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
}
