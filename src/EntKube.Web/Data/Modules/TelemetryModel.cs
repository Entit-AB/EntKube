using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Telemetry"/> module —
/// Dashboards, alert rules, incidents and how notifications are delivered.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class TelemetryModel
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
        builder.Entity<AlertIncident>().ToTable("AlertIncidents");
        builder.Entity<AlertRoutingRule>().ToTable("AlertRoutingRules");
        builder.Entity<Dashboard>().ToTable("Dashboards");
        builder.Entity<IncidentNote>().ToTable("IncidentNotes");
        builder.Entity<NotificationChannel>().ToTable("NotificationChannels");
        builder.Entity<NotificationDelivery>().ToTable("NotificationDeliveries");
        builder.Entity<NotificationProviderConfig>().ToTable("NotificationProviderConfigs");
        builder.Entity<RumSite>().ToTable("RumSites");
        builder.Entity<TelemetryAlertRule>().ToTable("TelemetryAlertRules");
        builder.Entity<TelemetrySegment>().ToTable("TelemetrySegments");
        builder.Entity<TelemetryStorageSetting>().ToTable("TelemetryStorageSettings");

        builder.Entity<RumSite>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.PublicKey).IsUnique();
            entity.HasIndex(s => s.TenantId);
            // Indexed for the customer-portal site lookup (sites for a customer's apps). No FK/navigation,
            // matching ClusterId — the column is a soft association, not a cascade-owning relationship.
            entity.HasIndex(s => s.AppId);
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.Property(s => s.PublicKey).HasMaxLength(64).IsRequired();
            entity.Property(s => s.AllowedOrigins).HasMaxLength(4000);
        });

        builder.Entity<TelemetrySegment>(entity =>
        {
            entity.HasKey(s => s.Id);
            // Window-overlap pruning within a tenant: "TenantId = @t AND Signal = @s AND MaxTs >= @from
            // AND MinTs < @to". Leading TenantId + Signal + MaxTs seeks straight to the tenant's segments
            // that can hold rows in the requested time range.
            entity.HasIndex(s => new { s.TenantId, s.Signal, s.MaxTs, s.MinTs });
            entity.Property(s => s.Signal).HasMaxLength(20).IsRequired();
            entity.Property(s => s.ObjectKey).HasMaxLength(512).IsRequired();
        });

        builder.Entity<TelemetryStorageSetting>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.TenantId).IsUnique(); // one storage setting per tenant
        });

        builder.Entity<AlertIncident>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.HasIndex(i => new { i.ClusterId, i.Fingerprint }).IsUnique();
            entity.HasIndex(i => i.Status);
            entity.HasIndex(i => i.StartsAt);
            entity.Property(i => i.Fingerprint).HasMaxLength(100).IsRequired();
            entity.Property(i => i.AlertName).HasMaxLength(200).IsRequired();
            entity.Property(i => i.Severity).HasMaxLength(20).IsRequired();
            entity.Property(i => i.Summary).HasMaxLength(500);
            entity.Property(i => i.Description).HasMaxLength(2000);
            entity.Property(i => i.RunbookUrl).HasMaxLength(500);
            entity.Property(i => i.AcknowledgedBy).HasMaxLength(256);
            entity.Property(i => i.AssignedTo).HasMaxLength(256);
            entity.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(i => i.EscalatedAt);

            entity.HasOne(i => i.Cluster)
                .WithMany()
                .HasForeignKey(i => i.ClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<IncidentNote>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => n.IncidentId);
            entity.Property(n => n.Author).HasMaxLength(256).IsRequired();
            entity.Property(n => n.Content).HasMaxLength(2000).IsRequired();

            entity.HasOne(n => n.Incident)
                .WithMany(i => i.Notes)
                .HasForeignKey(n => n.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationChannel>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.CustomerId, c.Name }).IsUnique();
            entity.HasIndex(c => new { c.TenantId, c.CustomerId });
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.SeverityFilter).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.AcknowledgeFilter).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.FiringFilter).HasConversion<string>().HasMaxLength(30);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationDelivery>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => new { d.IncidentId, d.ChannelId, d.IsFiring });
            entity.Property(d => d.Error).HasMaxLength(1000);

            entity.HasOne(d => d.Incident)
                .WithMany(i => i.Deliveries)
                .HasForeignKey(d => d.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Channel)
                .WithMany(c => c.Deliveries)
                .HasForeignKey(d => d.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertRoutingRule — tenant-specific rules that map alert criteria to a notification channel.

        builder.Entity<AlertRoutingRule>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.TenantId);
            entity.HasIndex(r => r.ChannelId);
            entity.Property(r => r.Name).HasMaxLength(200).IsRequired();
            entity.Property(r => r.MatchAlertName).HasMaxLength(200);
            entity.Property(r => r.MatchNamespace).HasMaxLength(200);
            entity.Property(r => r.MatchSeverity).HasMaxLength(20);
            entity.Property(r => r.MatchLabelKey).HasMaxLength(100);
            entity.Property(r => r.MatchLabelValue).HasMaxLength(200);

            entity.HasOne(r => r.Tenant)
                .WithMany()
                .HasForeignKey(r => r.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.Channel)
                .WithMany()
                .HasForeignKey(r => r.ChannelId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            entity.HasOne(r => r.MatchCluster)
                .WithMany()
                .HasForeignKey(r => r.MatchClusterId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        builder.Entity<NotificationProviderConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.ProviderType }).IsUnique();
            entity.Property(c => c.ProviderType).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.UpdatedByUserId).HasMaxLength(450);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
