using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Fleet"/> module —
/// Clusters, nodes, cloud connections, blueprints and bootstrap runs.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class FleetModel
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
        builder.Entity<BlueprintRollout>().ToTable("BlueprintRollouts");
        builder.Entity<BlueprintRolloutTarget>().ToTable("BlueprintRolloutTargets");
        builder.Entity<BlueprintStep>().ToTable("BlueprintSteps");
        builder.Entity<BlueprintVariable>().ToTable("BlueprintVariables");
        builder.Entity<BlueprintVariableValue>().ToTable("BlueprintVariableValues");
        builder.Entity<BootstrapRun>().ToTable("BootstrapRuns");
        builder.Entity<BootstrapStepRun>().ToTable("BootstrapStepRuns");
        builder.Entity<ClusterBlueprint>().ToTable("ClusterBlueprints");
        builder.Entity<ClusterServer>().ToTable("ClusterServers");
        builder.Entity<EgressAgent>().ToTable("EgressAgents");
        builder.Entity<KubernetesCluster>().ToTable("KubernetesClusters");
        builder.Entity<MaintenanceWindow>().ToTable("MaintenanceWindows");
        builder.Entity<OpenStackConnection>().ToTable("OpenStackConnections");

        // KubernetesCluster — belongs to a tenant and an environment.
        // Name must be unique within a tenant.

        builder.Entity<KubernetesCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.ApiServerUrl).HasMaxLength(500).IsRequired();

            entity.HasOne(c => c.Tenant)
                .WithMany(t => t.KubernetesClusters)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Environment)
                .WithMany(e => e.KubernetesClusters)
                .HasForeignKey(c => c.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MaintenanceWindow>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.HasIndex(w => new { w.TenantId, w.StartsAt });
            entity.HasIndex(w => w.StartsAt);
            entity.Property(w => w.Title).HasMaxLength(200).IsRequired();
            entity.Property(w => w.Description).HasMaxLength(1000);
            entity.Property(w => w.CreatedBy).HasMaxLength(256).IsRequired();

            entity.HasOne(w => w.Tenant)
                .WithMany()
                .HasForeignKey(w => w.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(w => w.Cluster)
                .WithMany()
                .HasForeignKey(w => w.ClusterId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ClusterServer — inventory record for physical/VM nodes behind a K8s cluster.
        // Name (DisplayName) must be unique within a cluster.

        builder.Entity<ClusterServer>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.ClusterId, s.DisplayName }).IsUnique();
            entity.Property(s => s.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(s => s.NodeName).HasMaxLength(253);
            entity.Property(s => s.IpAddress).HasMaxLength(45);
            entity.Property(s => s.ManagementIpAddress).HasMaxLength(45);
            entity.Property(s => s.Provider).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.OsDistribution).HasMaxLength(200);
            entity.Property(s => s.Location).HasMaxLength(200);
            entity.Property(s => s.SshUser).HasMaxLength(100);
            entity.Property(s => s.JumpHost).HasMaxLength(500);
            entity.Property(s => s.Notes).HasMaxLength(2000);

            entity.HasOne(s => s.Cluster)
                .WithMany(c => c.Servers)
                .HasForeignKey(s => s.ClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ClusterBlueprint — a tenant-scoped bootstrap recipe. Name unique per tenant.
        // Deleting a tenant cascades to its blueprints and their steps.

        builder.Entity<ClusterBlueprint>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();
            entity.Property(b => b.Name).HasMaxLength(200).IsRequired();
            entity.Property(b => b.Description).HasMaxLength(2000);
            entity.Property(b => b.ProvisioningProvider).HasMaxLength(100);

            entity.HasOne(b => b.Tenant)
                .WithMany(t => t.Blueprints)
                .HasForeignKey(b => b.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlueprintStep — ordered items within a blueprint; cascade from blueprint.

        builder.Entity<BlueprintStep>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.BlueprintId, s.Order });
            entity.Property(s => s.StepType).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Key).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Namespace).HasMaxLength(253);

            entity.HasOne(s => s.Blueprint)
                .WithMany(b => b.Steps)
                .HasForeignKey(s => s.BlueprintId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BootstrapRun — one execution of a blueprint against a cluster.
        // Cascade from cluster so removing a cluster clears its run history.

        builder.Entity<BootstrapRun>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.ClusterId);
            entity.Property(r => r.BlueprintName).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Mode).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.TriggeredBy).HasMaxLength(256);

            entity.HasOne(r => r.Cluster)
                .WithMany()
                .HasForeignKey(r => r.ClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BootstrapStepRun — per-step snapshot + result; cascade from run.

        builder.Entity<BootstrapStepRun>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.BootstrapRunId, s.Order });
            entity.Property(s => s.StepType).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Key).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Namespace).HasMaxLength(253);

            entity.HasOne(s => s.Run)
                .WithMany(r => r.StepRuns)
                .HasForeignKey(s => s.BootstrapRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlueprintRollout — a staged push of a blueprint to its bootstrapped clusters.
        // Cascade from blueprint so deleting a blueprint clears its rollout history.

        builder.Entity<BlueprintRollout>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.BlueprintId);
            entity.Property(r => r.BlueprintName).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.TriggeredBy).HasMaxLength(256);

            entity.HasOne(r => r.Blueprint)
                .WithMany()
                .HasForeignKey(r => r.BlueprintId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlueprintRolloutTarget — one cluster within a rollout; cascade from rollout.

        builder.Entity<BlueprintRolloutTarget>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.RolloutId, t.Order });
            entity.Property(t => t.ClusterName).HasMaxLength(200).IsRequired();
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(t => t.Rollout)
                .WithMany(r => r.Targets)
                .HasForeignKey(t => t.RolloutId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlueprintVariable — a named per-blueprint variable (${Name}); cascade from blueprint.
        // Name unique within a blueprint.

        builder.Entity<BlueprintVariable>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => new { v.BlueprintId, v.Name }).IsUnique();
            entity.Property(v => v.Name).HasMaxLength(200).IsRequired();
            entity.Property(v => v.Description).HasMaxLength(2000);
            entity.Property(v => v.DefaultValue).HasMaxLength(2000);

            entity.HasOne(v => v.Blueprint)
                .WithMany(b => b.Variables)
                .HasForeignKey(v => v.BlueprintId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlueprintVariableValue — a variable's value for one environment; cascade from variable.
        // One value per (variable, environment).

        builder.Entity<BlueprintVariableValue>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => new { v.VariableId, v.EnvironmentId }).IsUnique();
            entity.Property(v => v.Value).HasMaxLength(2000).IsRequired();

            entity.HasOne(v => v.Variable)
                .WithMany(v => v.Values)
                .HasForeignKey(v => v.VariableId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
