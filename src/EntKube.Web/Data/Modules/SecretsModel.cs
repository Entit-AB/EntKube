using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Secrets"/> module —
/// The vault and the secrets in it.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class SecretsModel
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
        builder.Entity<SecretExpiryNotification>().ToTable("SecretExpiryNotifications");
        builder.Entity<SecretExpiryNotificationConfig>().ToTable("SecretExpiryNotificationConfigs");
        builder.Entity<SecretVault>().ToTable("SecretVaults");
        builder.Entity<VaultSecret>().ToTable("VaultSecrets");
        builder.Entity<VaultSecretVersion>().ToTable("VaultSecretVersions");

        // SecretVault — one vault per tenant. Stores the sealed Data Encryption Key.
        // A tenant can have at most one vault (1:1 relationship).

        builder.Entity<SecretVault>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => v.TenantId).IsUnique();

            entity.HasOne(v => v.Tenant)
                .WithOne(t => t.Vault)
                .HasForeignKey<SecretVault>(v => v.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VaultSecret — an individual encrypted secret. Scoped to either an App
        // or a ClusterComponent. Name must be unique within its scope.

        builder.Entity<VaultSecret>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            // Stored as an int; existing rows default to Opaque (0).
            entity.Property(s => s.SecretType).HasDefaultValue(VaultSecretType.Opaque);
            entity.Property(s => s.KubernetesSecretName).HasMaxLength(253);
            entity.Property(s => s.KubernetesNamespace).HasMaxLength(63);

            // App secrets are unique per (app, environment, name): a "shared"
            // secret (null EnvironmentId) and an environment-bound secret can
            // reuse the same name, and each environment has its own namespace.
            entity.HasIndex(s => new { s.VaultId, s.AppId, s.EnvironmentId, s.Name })
                .IsUnique()
                .HasFilter(null);

            entity.HasIndex(s => new { s.VaultId, s.ComponentId, s.Name })
                .IsUnique()
                .HasFilter(null);

            // A cluster's kubeconfig is unique per (cluster, name).
            entity.HasIndex(s => new { s.VaultId, s.OwnerClusterId, s.Name })
                .IsUnique()
                .HasFilter(null);

            entity.HasOne(s => s.Vault)
                .WithMany(v => v.Secrets)
                .HasForeignKey(s => s.VaultId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.App)
                .WithMany(a => a.Secrets)
                .HasForeignKey(s => s.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            // Environment binding for app-scoped secrets. When the environment is
            // deleted the FK is set to null (the secret falls back to "shared")
            // rather than cascading the secret away.
            entity.HasOne(s => s.Environment)
                .WithMany()
                .HasForeignKey(s => s.EnvironmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(s => s.Component)
                .WithMany(c => c.Secrets)
                .HasForeignKey(s => s.ComponentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.CnpgCluster)
                .WithMany()
                .HasForeignKey(s => s.CnpgClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.CnpgDatabase)
                .WithMany()
                .HasForeignKey(s => s.CnpgDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.MongoDatabase)
                .WithMany()
                .HasForeignKey(s => s.MongoDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.MongoCluster)
                .WithMany()
                .HasForeignKey(s => s.MongoClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.RegisteredPostgresDatabase)
                .WithMany()
                .HasForeignKey(s => s.RegisteredPostgresDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            // App-scoped secrets can optionally specify a target K8s cluster for sync.
            // When the cluster is deleted the FK is set to null (secrets are not removed).
            entity.HasOne(s => s.KubernetesCluster)
                .WithMany()
                .HasForeignKey(s => s.KubernetesClusterId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(s => s.RabbitMQCluster)
                .WithMany()
                .HasForeignKey(s => s.RabbitMQClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.RedisCluster)
                .WithMany()
                .HasForeignKey(s => s.RedisClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.KafkaCluster)
                .WithMany()
                .HasForeignKey(s => s.KafkaClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            // A kubeconfig secret is owned by a cluster. Deleting the cluster cascades
            // its kubeconfig secret away. Distinct from KubernetesClusterId, which is the
            // (sync-target) cluster for app secrets. KubernetesCluster.KubeconfigSecretId is
            // an intentionally unmapped scalar pointer back to this secret — the ownership
            // relationship (and its cascade) is modelled solely here to avoid a two-way FK cycle.
            entity.HasOne(s => s.OwnerCluster)
                .WithMany()
                .HasForeignKey(s => s.OwnerClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            // The support mailbox's IMAP password. Deleting the mailbox takes the
            // credential with it rather than leaving it in the vault unreferenced.
            entity.HasOne<SupportMailbox>()
                .WithMany()
                .HasForeignKey(s => s.SupportMailboxId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(s => s.Versions)
                .WithOne(v => v.Secret)
                .HasForeignKey(v => v.SecretId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VaultSecretVersion — immutable historical snapshots of a secret's value.
        // At most 10 versions are retained per secret (pruned on write).

        builder.Entity<VaultSecretVersion>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.CreatedBy).HasMaxLength(254);

            entity.HasIndex(v => new { v.SecretId, v.VersionNumber });
        });

        // VaultSecret — VpnRemoteEndpoint scoping (PSK/cert for a remote site).
        // Registering this here keeps the VaultSecret entity config in one place
        // even though this relationship is declared in the VpnRemoteEndpoint section.
        builder.Entity<VaultSecret>()
            .HasOne(s => s.VpnRemoteEndpoint)
            .WithMany(e => e.Secrets)
            .HasForeignKey(s => s.VpnRemoteEndpointId)
            .OnDelete(DeleteBehavior.Cascade);

        // VaultSecret — GitRepository scoping (PAT / SSH key / password for a git repo).
        builder.Entity<VaultSecret>()
            .HasOne(s => s.GitRepository)
            .WithMany()
            .HasForeignKey(s => s.GitRepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // VaultSecret — CustomerGitCredential scoping (PAT / SSH key / password for a customer credential).
        builder.Entity<VaultSecret>()
            .HasOne(s => s.CustomerGitCredential)
            .WithMany()
            .HasForeignKey(s => s.CustomerGitCredentialId)
            .OnDelete(DeleteBehavior.Cascade);

        // SecretExpiryNotificationConfig — per-tenant settings for expiring-secret
        // notifications (one row per tenant).

        builder.Entity<SecretExpiryNotificationConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.CustomerId }).IsUnique();
            entity.Property(c => c.ThresholdDaysCsv).HasMaxLength(200).IsRequired();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SecretExpiryNotification — sent-record / dedupe / history for expiring-secret
        // notices. SecretId is not an FK so history survives secret deletion.

        builder.Entity<SecretExpiryNotification>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => new { n.SecretId, n.ThresholdDays, n.ExpiresAt });
            entity.HasIndex(n => new { n.TenantId, n.CustomerId, n.SentAt });
            entity.Property(n => n.SecretName).HasMaxLength(256).IsRequired();
            entity.Property(n => n.Error).HasMaxLength(1000);

            entity.HasOne(n => n.Tenant)
                .WithMany()
                .HasForeignKey(n => n.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
