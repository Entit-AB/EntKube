using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Advisor"/> module —
/// The cross-cutting findings feed.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class AdvisorModel
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
        builder.Entity<AdvisorDigestConfig>().ToTable("AdvisorDigestConfigs");
        builder.Entity<AdvisorFindingState>().ToTable("AdvisorFindingStates");

        builder.Entity<AdvisorFindingState>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.FindingKey).HasMaxLength(256).IsRequired();
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.AcknowledgedBy).HasMaxLength(256);
            entity.Property(s => s.AssignedTo).HasMaxLength(256);
            entity.Property(s => s.Note).HasMaxLength(1024);
            entity.HasIndex(s => new { s.TenantId, s.FindingKey }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AdvisorDigestConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Frequency).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.WeeklyDay).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(c => c.TenantId).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
