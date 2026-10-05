using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Cost"/> module —
/// Rates, usage snapshots, the ledger and chargeback.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class CostModel
{
    internal static void Configure(ModelBuilder builder)
    {
        // Table names, stated rather than inferred.
        //
        // EF derives a table name from the DbSet property on whichever context declares it,
        // so "AlertIncident" became "AlertIncidents" only because ApplicationDbContext spells
        // that property in the plural. The per-module contexts do not declare each other's
        // sets, which means that without this block each of them maps the same entity to a
        // different table — and a query crossing a module boundary would hit a table that is
        // not there. Naming them here makes the schema a property of the model instead of a
        // property of C# property naming.
        builder.Entity<ClusterCostRate>().ToTable("ClusterCostRates");
        builder.Entity<CostLedgerCoverage>().ToTable("CostLedgerCoverages");
        builder.Entity<CostLedgerCursor>().ToTable("CostLedgerCursors");
        builder.Entity<CostLedgerEntry>().ToTable("CostLedgerEntries");
        builder.Entity<PriceList>().ToTable("PriceLists");
        builder.Entity<PriceListEntry>().ToTable("PriceListEntries");
        builder.Entity<ResourceUsageSnapshot>().ToTable("ResourceUsageSnapshots");

        // ClusterCostRate — at most one price sheet per cluster, so the unique index is
        // what prevents two rates silently disagreeing about what a core costs.

        builder.Entity<ClusterCostRate>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.ClusterId).IsUnique();
            entity.Property(r => r.Currency).HasMaxLength(3).IsRequired();
            entity.Property(r => r.UpdatedBy).HasMaxLength(256);
            // Money: an explicit precision rather than the provider default, which for
            // SQL Server would silently round to 2 decimals — useless for a per-core-hour
            // rate that is routinely fractions of a cent.
            entity.Property(r => r.CpuCoreHourCost).HasPrecision(18, 6);
            entity.Property(r => r.MemoryGiBHourCost).HasPrecision(18, 6);
            entity.Property(r => r.StorageGiBMonthCost).HasPrecision(18, 6);
            entity.Property(r => r.ClusterMonthlyOverhead).HasPrecision(18, 2);
            // Monthly amounts, so two decimals is the right precision — but stated
            // explicitly rather than left to a provider default, which is how the hourly
            // rates would have silently become decimal(18,2) on SQL Server and rounded to
            // zero. Same reasoning, opposite answer.
            entity.Property(r => r.LoadBalancerMonthlyCost).HasPrecision(18, 2);
            entity.Property(r => r.PublicIpMonthlyCost).HasPrecision(18, 2);
            entity.HasOne(r => r.Cluster)
                  .WithMany()
                  .HasForeignKey(r => r.ClusterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Cost ledger — what was actually incurred, day by day, as opposed to the run
        // rate. Three tables: the accrued cost itself, how much of each day was actually
        // measured, and the high-water mark that stops an hour being billed twice.

        builder.Entity<CostLedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);

            // One row per namespace per day. This index is what makes the hourly accrual
            // an upsert rather than an append — without it a sweep that ran twice would
            // add a second row for the same day and double that day's cost.
            entity.HasIndex(e => new { e.ClusterId, e.Namespace, e.Day }).IsUnique();

            // The shape every history query reads in: a tenant's rows over a date range.
            entity.HasIndex(e => new { e.TenantId, e.Day });

            entity.Property(e => e.ClusterName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Namespace).HasMaxLength(253).IsRequired();
            entity.Property(e => e.CustomerName).HasMaxLength(256);
            entity.Property(e => e.AppName).HasMaxLength(256);
            entity.Property(e => e.EnvironmentName).HasMaxLength(256);
            entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();

            // Six decimals, not two. These are hourly accruals: a small namespace incurs
            // fractions of a cent an hour, and rounding on the way in would store zero
            // for most of the fleet and produce a monthly total made entirely of the
            // largest workloads. Rounding to money happens when a figure is displayed.
            entity.Property(e => e.Hours).HasPrecision(18, 6);
            entity.Property(e => e.CpuCost).HasPrecision(18, 6);
            entity.Property(e => e.MemoryCost).HasPrecision(18, 6);
            entity.Property(e => e.StorageCost).HasPrecision(18, 6);
            entity.Property(e => e.NetworkCost).HasPrecision(18, 6);
            entity.Property(e => e.SharedCost).HasPrecision(18, 6);

            // Tenant is the only relationship. Cluster, customer and app are held as bare
            // ids with their names copied in, so that deleting any of them leaves the
            // record of what it cost intact — a ledger that forgets what it charged for
            // when the thing is removed is not a ledger. A purged tenant is the deliberate
            // exception: it is meant to leave nothing at all.
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PriceList>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.TenantId, p.CustomerId, p.EffectiveFrom });

            entity.HasOne(p => p.Tenant)
                  .WithMany()
                  .HasForeignKey(p => p.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Customer)
                  .WithMany()
                  .HasForeignKey(p => p.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PriceListEntry>(entity =>
        {
            entity.HasKey(e => e.Id);

            // One amount per key within a list — a second row for the same fee would make
            // the price ambiguous, which is the one thing a price list may not be.
            entity.HasIndex(e => new { e.PriceListId, e.Kind, e.Key }).IsUnique();

            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Hours).HasPrecision(18, 2);

            entity.HasOne(e => e.PriceList)
                  .WithMany(p => p.Entries)
                  .HasForeignKey(e => e.PriceListId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CostLedgerCoverage>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.ClusterId, c.Day }).IsUnique();
            entity.HasIndex(c => new { c.TenantId, c.Day });
            entity.Property(c => c.ClusterName).HasMaxLength(256).IsRequired();
            entity.Property(c => c.CoveredHours).HasPrecision(18, 6);
            entity.Property(c => c.GapHours).HasPrecision(18, 6);

            entity.HasOne(c => c.Tenant)
                  .WithMany()
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CostLedgerCursor>(entity =>
        {
            entity.HasKey(c => c.Id);

            // One cursor per cluster: two would mean two writers each believing they were
            // the only one, which is precisely the double-billing this prevents.
            entity.HasIndex(c => c.ClusterId).IsUnique();

            entity.HasOne(c => c.Cluster)
                  .WithMany()
                  .HasForeignKey(c => c.ClusterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ResourceUsageSnapshot>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Namespace).HasMaxLength(253).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(253).IsRequired();
            entity.HasIndex(s => new { s.ClusterId, s.Kind, s.SnapshotAt });
            entity.HasOne<KubernetesCluster>().WithMany().HasForeignKey(s => s.ClusterId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
