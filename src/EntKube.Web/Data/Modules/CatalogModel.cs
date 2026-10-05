using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Catalog"/> module —
/// Installed components, end-of-life notices and CA trust distribution.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class CatalogModel
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
        builder.Entity<CaTrustBundle>().ToTable("CaTrustBundles");
        builder.Entity<CaTrustBundleSource>().ToTable("CaTrustBundleSources");
        builder.Entity<CertificateDistribution>().ToTable("CertificateDistributions");
        builder.Entity<ClusterComponent>().ToTable("ClusterComponents");
        builder.Entity<EndOfLifeNotice>().ToTable("EndOfLifeNotices");

        builder.Entity<EndOfLifeNotice>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => new { n.AppId, n.UpgradedAt });

            entity.HasOne(n => n.Tenant)
                  .WithMany()
                  .HasForeignKey(n => n.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(n => n.App)
                  .WithMany()
                  .HasForeignKey(n => n.AppId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ClusterComponent — a deployable unit (Helm chart, operator, etc.) on a cluster.
        // Name must be unique within a cluster.

        builder.Entity<ClusterComponent>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.ClusterId, c.Name }).IsUnique();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.ComponentType).HasMaxLength(50).IsRequired();

            entity.HasOne(c => c.Cluster)
                .WithMany(k => k.Components)
                .HasForeignKey(c => c.ClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
