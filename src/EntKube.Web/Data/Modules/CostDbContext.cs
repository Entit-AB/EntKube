using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Cost module's view of the database — rates, usage snapshots, the ledger and chargeback.
///
/// <para>Exposes only the 7 tables Cost owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class CostDbContext(DbContextOptions<CostDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ClusterCostRate> ClusterCostRates => Set<ClusterCostRate>();
    public DbSet<CostLedgerEntry> CostLedgerEntries => Set<CostLedgerEntry>();
    public DbSet<CostLedgerCursor> CostLedgerCursors => Set<CostLedgerCursor>();
    public DbSet<CostLedgerCoverage> CostLedgerCoverages => Set<CostLedgerCoverage>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListEntry> PriceListEntries => Set<PriceListEntry>();
    public DbSet<ResourceUsageSnapshot> ResourceUsageSnapshots => Set<ResourceUsageSnapshot>();

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
