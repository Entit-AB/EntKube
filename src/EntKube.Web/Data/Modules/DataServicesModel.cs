using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.DataServices"/> module —
/// Managed Postgres, Mongo, Redis, Kafka, RabbitMQ, Elastic, LDAP, Harbor and object storage.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class DataServicesModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.Entity<StorageBinding>().ToTable("StorageBindings");

        builder.Entity<ElasticsearchBinding>().ToTable("ElasticsearchBindings");
        builder.Entity<KafkaBinding>().ToTable("KafkaBindings");
        builder.Entity<MessagingBinding>().ToTable("MessagingBindings");
        builder.Entity<CacheBinding>().ToTable("CacheBindings");
        builder.Entity<DatabaseBinding>().ToTable("DatabaseBindings");
        // Table names, stated rather than inferred.
        //
        // EF derives a table name from the DbSet property on whichever context declares it,
        // so "AlertIncident" became "AlertIncidents" only because ApplicationDbContext spells
        // that property in the plural. The per-module contexts do not declare each other's
        // sets, which means that without this block each of them maps the same entity to a
        // different table — and a query crossing a module boundary would hit a table that is
        // not there. Naming them here makes the schema a property of the model instead of a
        // property of C# property naming.
        builder.Entity<CnpgBackup>().ToTable("CnpgBackups");
        builder.Entity<CnpgCluster>().ToTable("CnpgClusters");
        builder.Entity<CnpgDatabase>().ToTable("CnpgDatabases");
        builder.Entity<DockerRegistryCredential>().ToTable("DockerRegistryCredentials");
        builder.Entity<ElasticsearchCluster>().ToTable("ElasticsearchClusters");
        builder.Entity<ElasticsearchDataView>().ToTable("ElasticsearchDataViews");
        builder.Entity<ElasticsearchIlmPolicy>().ToTable("ElasticsearchIlmPolicies");
        builder.Entity<ElasticsearchIngestPipeline>().ToTable("ElasticsearchIngestPipelines");
        builder.Entity<ElasticsearchKibanaSpace>().ToTable("ElasticsearchKibanaSpaces");
        builder.Entity<ElasticsearchRemoteLink>().ToTable("ElasticsearchRemoteLinks");
        builder.Entity<ElasticsearchUser>().ToTable("ElasticsearchUsers");
        builder.Entity<HarborComponentConfig>().ToTable("HarborComponentConfigs");
        builder.Entity<HarborProject>().ToTable("HarborProjects");
        builder.Entity<KafkaCluster>().ToTable("KafkaClusters");
        builder.Entity<KafkaTopic>().ToTable("KafkaTopics");
        builder.Entity<KafkaUser>().ToTable("KafkaUsers");
        builder.Entity<MongoBackup>().ToTable("MongoBackups");
        builder.Entity<MongoCluster>().ToTable("MongoClusters");
        builder.Entity<MongoDatabase>().ToTable("MongoDatabases");
        builder.Entity<OpenLdapComponentConfig>().ToTable("OpenLdapComponentConfigs");
        builder.Entity<OpenLdapGroup>().ToTable("OpenLdapGroups");
        builder.Entity<OpenLdapGroupMember>().ToTable("OpenLdapGroupMembers");
        builder.Entity<OpenLdapOrganizationalUnit>().ToTable("OpenLdapOrganizationalUnits");
        builder.Entity<OpenLdapUser>().ToTable("OpenLdapUsers");
        builder.Entity<RabbitMQBackup>().ToTable("RabbitMQBackups");
        builder.Entity<RabbitMQCluster>().ToTable("RabbitMQClusters");
        builder.Entity<RedisCluster>().ToTable("RedisClusters");
        builder.Entity<RegisteredPostgresDatabase>().ToTable("RegisteredPostgresDatabases");
        builder.Entity<RegisteredPostgresDump>().ToTable("RegisteredPostgresDumps");
        builder.Entity<RegisteredPostgresInstance>().ToTable("RegisteredPostgresInstances");
        builder.Entity<StorageLink>().ToTable("StorageLinks");

        // DockerRegistryCredential — encrypted registry auth stored in the tenant vault.
        // Password is AES-256-GCM encrypted with the tenant DEK.

        builder.Entity<DockerRegistryCredential>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).HasMaxLength(200).IsRequired();
            entity.Property(d => d.Server).HasMaxLength(500).IsRequired();
            entity.Property(d => d.Username).HasMaxLength(300).IsRequired();
            entity.Property(d => d.Email).HasMaxLength(254);
            entity.Property(d => d.KubernetesSecretName).HasMaxLength(253);
            entity.Property(d => d.KubernetesNamespace).HasMaxLength(63);

            entity.HasOne(d => d.Vault)
                .WithMany()
                .HasForeignKey(d => d.VaultId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.App)
                .WithMany()
                .HasForeignKey(d => d.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            // Environment binding for app-scoped credentials. When the environment is deleted
            // the FK is set to null (the credential falls back to "shared") rather than cascading
            // the credential away. Mirrors VaultSecret.Environment.
            entity.HasOne(d => d.Environment)
                .WithMany()
                .HasForeignKey(d => d.EnvironmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(d => d.KubernetesCluster)
                .WithMany()
                .HasForeignKey(d => d.KubernetesClusterId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CnpgCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(63).IsRequired();
            entity.Property(c => c.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(c => c.PostgresVersion).HasMaxLength(10).IsRequired();
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.BackupSchedule).HasMaxLength(100);
            entity.Property(c => c.MaxBackups).HasDefaultValue(20);

            entity.HasIndex(c => new { c.KubernetesClusterId, c.Name, c.Namespace }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.KubernetesCluster)
                .WithMany()
                .HasForeignKey(c => c.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.StorageLink)
                .WithMany()
                .HasForeignKey(c => c.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CnpgDatabase>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).HasMaxLength(63).IsRequired();
            entity.Property(d => d.Owner).HasMaxLength(63).IsRequired();

            entity.HasIndex(d => new { d.CnpgClusterId, d.Name }).IsUnique();

            entity.HasOne(d => d.CnpgCluster)
                .WithMany(c => c.Databases)
                .HasForeignKey(d => d.CnpgClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CnpgBackup>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Name).HasMaxLength(253).IsRequired();

            entity.HasIndex(b => new { b.CnpgClusterId, b.Name }).IsUnique();

            entity.HasOne(b => b.CnpgCluster)
                .WithMany(c => c.Backups)
                .HasForeignKey(b => b.CnpgClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MongoCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(63).IsRequired();
            entity.Property(c => c.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(c => c.MongoVersion).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.BackupSchedule).HasMaxLength(100);
            entity.Property(c => c.MaxBackups).HasDefaultValue(20);

            entity.HasIndex(c => new { c.KubernetesClusterId, c.Name, c.Namespace }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.KubernetesCluster)
                .WithMany()
                .HasForeignKey(c => c.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.StorageLink)
                .WithMany()
                .HasForeignKey(c => c.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<MongoDatabase>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).HasMaxLength(63).IsRequired();

            entity.HasIndex(d => new { d.MongoClusterId, d.Name }).IsUnique();

            entity.HasOne(d => d.MongoCluster)
                .WithMany(c => c.Databases)
                .HasForeignKey(d => d.MongoClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MongoBackup>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Name).HasMaxLength(253).IsRequired();

            entity.HasIndex(b => new { b.MongoClusterId, b.Name }).IsUnique();

            entity.HasOne(b => b.MongoCluster)
                .WithMany(c => c.Backups)
                .HasForeignKey(b => b.MongoClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OpenLDAP — a managed directory attached to an installed ClusterComponent
        // (mirrors KeycloakComponentConfig). Admin/config passwords live in the vault;
        // the directory (OUs, users, groups) is authored here and seeded via Helm/LDIF.

        builder.Entity<OpenLdapComponentConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.BaseDn).HasMaxLength(400).IsRequired();
            entity.Property(c => c.Organization).HasMaxLength(200).IsRequired();
            entity.Property(c => c.AdminUsername).HasMaxLength(100).IsRequired();
            entity.Property(c => c.DisplayName).HasMaxLength(200);
            entity.Property(c => c.ClusterIssuer).HasMaxLength(200);
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageClass).HasMaxLength(100);
            entity.Property(c => c.TlsMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.PhpLdapAdminHostname).HasMaxLength(253);
            entity.Property(c => c.LtbPasswdHostname).HasMaxLength(253);
            entity.Property(c => c.PhpLdapAdminExposeMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.LtbPasswdExposeMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.PhpLdapAdminIngressClass).HasMaxLength(100);
            entity.Property(c => c.LtbPasswdIngressClass).HasMaxLength(100);
            entity.Property(c => c.WebUiClusterIssuer).HasMaxLength(200);
            entity.Property(c => c.PhpLdapAdminImage).HasMaxLength(300);
            entity.Property(c => c.LtbPasswdImage).HasMaxLength(300);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ClusterComponent)
                .WithMany()
                .HasForeignKey(c => c.ClusterComponentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OpenLdapOrganizationalUnit>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Name).HasMaxLength(100).IsRequired();
            entity.Property(o => o.Description).HasMaxLength(500);
            entity.HasIndex(o => new { o.ConfigId, o.Name }).IsUnique();

            entity.HasOne(o => o.Config)
                .WithMany(c => c.OrganizationalUnits)
                .HasForeignKey(o => o.ConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OpenLdapUser>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Uid).HasMaxLength(100).IsRequired();
            entity.Property(u => u.Cn).HasMaxLength(200).IsRequired();
            entity.Property(u => u.Sn).HasMaxLength(200);
            entity.Property(u => u.GivenName).HasMaxLength(200);
            entity.Property(u => u.Email).HasMaxLength(320);
            entity.Property(u => u.DisplayName).HasMaxLength(200);
            entity.Property(u => u.HomeDirectory).HasMaxLength(400);
            entity.Property(u => u.LoginShell).HasMaxLength(100);
            entity.Property(u => u.PasswordSsha).HasMaxLength(200);
            entity.HasIndex(u => new { u.ConfigId, u.Uid }).IsUnique();

            entity.HasOne(u => u.Config)
                .WithMany(c => c.Users)
                .HasForeignKey(u => u.ConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            // OU delete must not cascade-delete users; the seed builder falls back to the base DN.
            entity.HasOne(u => u.OrganizationalUnit)
                .WithMany()
                .HasForeignKey(u => u.OrganizationalUnitId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<OpenLdapGroup>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Cn).HasMaxLength(200).IsRequired();
            entity.Property(g => g.Description).HasMaxLength(500);
            entity.Property(g => g.GroupType).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(g => new { g.ConfigId, g.Cn }).IsUnique();

            entity.HasOne(g => g.Config)
                .WithMany(c => c.Groups)
                .HasForeignKey(g => g.ConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(g => g.OrganizationalUnit)
                .WithMany()
                .HasForeignKey(g => g.OrganizationalUnitId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<OpenLdapGroupMember>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.GroupId, m.UserId }).IsUnique();

            entity.HasOne(m => m.Group)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a user removes its memberships; use Restrict + explicit cleanup would
            // orphan; Cascade from both sides on the join needs one path to be NoAction to
            // avoid multiple-cascade-paths on SQL Server — the group path cascades, the user
            // path does not (the service deletes memberships before deleting a user).
            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // RegisteredPostgresInstance — a vanilla Postgres server inside a K8s cluster
        // that is not managed by CNPG. EntKube registers it to manage databases and
        // credentials, but does not own the server lifecycle.

        builder.Entity<RegisteredPostgresInstance>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Name).HasMaxLength(200).IsRequired();
            entity.Property(i => i.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(i => i.ServiceName).HasMaxLength(253).IsRequired();
            entity.Property(i => i.AdminPodName).HasMaxLength(253).IsRequired();
            entity.Property(i => i.AdminUsername).HasMaxLength(63).IsRequired();
            entity.Property(i => i.Notes).HasMaxLength(1000);

            entity.HasIndex(i => new { i.TenantId, i.Name }).IsUnique();

            entity.HasOne(i => i.Tenant)
                .WithMany()
                .HasForeignKey(i => i.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.KubernetesCluster)
                .WithMany()
                .HasForeignKey(i => i.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RegisteredPostgresDatabase>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).HasMaxLength(63).IsRequired();
            entity.Property(d => d.Owner).HasMaxLength(63).IsRequired();

            entity.HasIndex(d => new { d.RegisteredPostgresInstanceId, d.Name }).IsUnique();

            entity.HasOne(d => d.RegisteredPostgresInstance)
                .WithMany(i => i.Databases)
                .HasForeignKey(d => d.RegisteredPostgresInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RegisteredPostgresDump>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.S3Key).HasMaxLength(1024).IsRequired();

            entity.HasOne(d => d.RegisteredPostgresDatabase)
                .WithMany()
                .HasForeignKey(d => d.RegisteredPostgresDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.StorageLink)
                .WithMany()
                .HasForeignKey(d => d.StorageLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<HarborComponentConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.AdminUsername).HasMaxLength(100).IsRequired();
            entity.Property(c => c.RegistryUrl).HasMaxLength(500);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ClusterComponent)
                .WithMany()
                .HasForeignKey(c => c.ClusterComponentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.CnpgDatabase)
                .WithMany()
                .HasForeignKey(c => c.CnpgDatabaseId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.StorageLink)
                .WithMany()
                .HasForeignKey(c => c.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // HarborProject — tracks a Harbor project managed by EntKube.
        // Project name must be unique within a config (Harbor enforces this globally per server).
        // LinkedAppId links the project to a customer app for portal self-service.

        builder.Entity<HarborProject>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.ProjectName).HasMaxLength(255).IsRequired();

            entity.HasIndex(p => new { p.HarborComponentConfigId, p.ProjectName }).IsUnique();

            entity.HasOne(p => p.Tenant)
                .WithMany()
                .HasForeignKey(p => p.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.HarborComponentConfig)
                .WithMany()
                .HasForeignKey(p => p.HarborComponentConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.LinkedApp)
                .WithMany()
                .HasForeignKey(p => p.LinkedAppId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RabbitMQCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(63).IsRequired();
            entity.Property(c => c.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(c => c.RabbitMQVersion).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageClass).HasMaxLength(63);
            entity.Property(c => c.BackupSchedule).HasMaxLength(100);
            entity.Property(c => c.MaxBackups).HasDefaultValue(10);

            entity.HasIndex(c => new { c.KubernetesClusterId, c.Name, c.Namespace }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.KubernetesCluster)
                .WithMany()
                .HasForeignKey(c => c.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.StorageLink)
                .WithMany()
                .HasForeignKey(c => c.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RabbitMQBackup>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.ObjectKey).HasMaxLength(1024).IsRequired();
            entity.Property(b => b.ClusterName).HasMaxLength(63).IsRequired();

            entity.HasOne(b => b.Cluster)
                .WithMany(c => c.Backups)
                .HasForeignKey(b => b.RabbitMQClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.StorageLink)
                .WithMany()
                .HasForeignKey(b => b.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RedisCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(63).IsRequired();
            entity.Property(c => c.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(c => c.RedisVersion).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageClass).HasMaxLength(63);

            entity.HasIndex(c => new { c.KubernetesClusterId, c.Name, c.Namespace }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.KubernetesCluster)
                .WithMany()
                .HasForeignKey(c => c.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<KafkaCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(63).IsRequired();
            entity.Property(c => c.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(c => c.KafkaVersion).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageClass).HasMaxLength(63);
            entity.Property(c => c.CpuRequest).HasMaxLength(20);
            entity.Property(c => c.MemoryRequest).HasMaxLength(20);
            entity.Property(c => c.MemoryLimit).HasMaxLength(20);

            entity.HasIndex(c => new { c.KubernetesClusterId, c.Name, c.Namespace }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.KubernetesCluster)
                .WithMany()
                .HasForeignKey(c => c.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.Topics)
                .WithOne(t => t.KafkaCluster)
                .HasForeignKey(t => t.KafkaClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.Users)
                .WithOne(u => u.KafkaCluster)
                .HasForeignKey(u => u.KafkaClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<KafkaTopic>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).HasMaxLength(249).IsRequired();
            entity.HasIndex(t => new { t.KafkaClusterId, t.Name }).IsUnique();
        });

        builder.Entity<KafkaUser>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).HasMaxLength(63).IsRequired();
            entity.Property(u => u.ProducerTopics).HasMaxLength(1000);
            entity.Property(u => u.ConsumerTopics).HasMaxLength(1000);
            entity.Property(u => u.ConsumerGroup).HasMaxLength(255);
            entity.HasIndex(u => new { u.KafkaClusterId, u.Username }).IsUnique();
        });

        builder.Entity<ElasticsearchCluster>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(36).IsRequired();
            entity.Property(c => c.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(c => c.Version).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageClass).HasMaxLength(63);
            entity.Property(c => c.Health).HasMaxLength(20);

            foreach (string quantity in new[]
            {
                nameof(ElasticsearchCluster.MasterCpuRequest), nameof(ElasticsearchCluster.MasterMemory),
                nameof(ElasticsearchCluster.MasterStorageSize),
                nameof(ElasticsearchCluster.HotCpuRequest), nameof(ElasticsearchCluster.HotMemory),
                nameof(ElasticsearchCluster.HotStorageSize),
                nameof(ElasticsearchCluster.WarmCpuRequest), nameof(ElasticsearchCluster.WarmMemory),
                nameof(ElasticsearchCluster.WarmStorageSize),
                nameof(ElasticsearchCluster.ColdCpuRequest), nameof(ElasticsearchCluster.ColdMemory),
                nameof(ElasticsearchCluster.ColdStorageSize),
                nameof(ElasticsearchCluster.IngestCpuRequest), nameof(ElasticsearchCluster.IngestMemory),
                nameof(ElasticsearchCluster.IngestStorageSize),
                nameof(ElasticsearchCluster.KibanaCpuRequest), nameof(ElasticsearchCluster.KibanaMemory)
            })
            {
                entity.Property(quantity).HasMaxLength(20).IsRequired();
            }

            entity.Property(c => c.SnapshotBasePath).HasMaxLength(255);
            entity.Property(c => c.SnapshotScheduleCron).HasMaxLength(64).IsRequired();
            entity.Property(c => c.SnapshotLastSuccessName).HasMaxLength(255);
            entity.Property(c => c.SnapshotLastFailure).HasMaxLength(2000);

            entity.HasIndex(c => new { c.KubernetesClusterId, c.Name, c.Namespace }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.KubernetesCluster)
                .WithMany()
                .HasForeignKey(c => c.KubernetesClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            // SnapshotStorageLinkId is deliberately a soft reference, like RumSite.AppId: a real FK
            // between two tables that both cascade from Tenant is a second cascade path SQL Server
            // refuses, and the service already copes with a storage link that has gone away.

            entity.HasMany(c => c.IlmPolicies)
                .WithOne(p => p.ElasticsearchCluster)
                .HasForeignKey(p => p.ElasticsearchClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.Users)
                .WithOne(u => u.ElasticsearchCluster)
                .HasForeignKey(u => u.ElasticsearchClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.KibanaSpaces)
                .WithOne(s => s.ElasticsearchCluster)
                .HasForeignKey(s => s.ElasticsearchClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ElasticsearchDataView>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.SpaceId).HasMaxLength(63);
            entity.Property(v => v.Title).HasMaxLength(255).IsRequired();
            entity.Property(v => v.Name).HasMaxLength(255);
            entity.Property(v => v.TimeFieldName).HasMaxLength(255).IsRequired();
            entity.Property(v => v.LastError).HasMaxLength(2000);

            // A pattern can exist once per space: the same indices are a different data view in
            // each, and Kibana treats them as unrelated objects.
            entity.HasIndex(v => new { v.ElasticsearchClusterId, v.SpaceId, v.Title }).IsUnique();

            entity.HasOne(v => v.ElasticsearchCluster)
                .WithMany(c => c.DataViews)
                .HasForeignKey(v => v.ElasticsearchClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ElasticsearchIngestPipeline>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(128).IsRequired();
            entity.Property(p => p.Description).HasMaxLength(500);
            entity.Property(p => p.TimestampField).HasMaxLength(255);
            entity.Property(p => p.TimestampFormats).HasMaxLength(500).IsRequired();
            entity.Property(p => p.GrokField).HasMaxLength(255);
            entity.Property(p => p.GrokPattern).HasMaxLength(2000);
            entity.Property(p => p.RenameFields).HasMaxLength(1000);
            entity.Property(p => p.RemoveFields).HasMaxLength(1000);
            entity.Property(p => p.SetFields).HasMaxLength(1000);
            entity.Property(p => p.LastError).HasMaxLength(2000);
            entity.HasIndex(p => new { p.ElasticsearchClusterId, p.Name }).IsUnique();

            entity.HasOne(p => p.ElasticsearchCluster)
                .WithMany(c => c.IngestPipelines)
                .HasForeignKey(p => p.ElasticsearchClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ElasticsearchRemoteLink>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Alias).HasMaxLength(63).IsRequired();
            entity.Property(l => l.SearchIndexPatterns).HasMaxLength(500).IsRequired();
            entity.Property(l => l.LastError).HasMaxLength(2000);

            // An alias is how a query names the remote, so it has to be unique on the cluster doing
            // the searching — not globally.
            entity.HasIndex(l => new { l.LocalClusterId, l.Alias }).IsUnique();

            entity.HasOne(l => l.LocalCluster)
                .WithMany()
                .HasForeignKey(l => l.LocalClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not cascade: two cascade paths into the same table is what SQL Server
            // refuses outright, and deleting the cluster whose data is being searched should be a
            // deliberate act with the links removed first anyway.
            entity.HasOne(l => l.RemoteCluster)
                .WithMany()
                .HasForeignKey(l => l.RemoteClusterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ElasticsearchKibanaSpace>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.SpaceId).HasMaxLength(63).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(128).IsRequired();
            entity.Property(s => s.Description).HasMaxLength(500);
            entity.Property(s => s.LastError).HasMaxLength(2000);
            entity.HasIndex(s => new { s.ElasticsearchClusterId, s.SpaceId }).IsUnique();
        });

        builder.Entity<ElasticsearchUser>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).HasMaxLength(63).IsRequired();
            entity.Property(u => u.IndexPattern).HasMaxLength(255).IsRequired();
            entity.Property(u => u.KibanaSpaceId).HasMaxLength(63);
            entity.Property(u => u.LastError).HasMaxLength(2000);
            entity.HasIndex(u => new { u.ElasticsearchClusterId, u.Username }).IsUnique();
        });

        builder.Entity<ElasticsearchIlmPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(128).IsRequired();
            entity.Property(p => p.IndexPattern).HasMaxLength(255).IsRequired();
            entity.Property(p => p.LastError).HasMaxLength(2000);
            entity.Property(p => p.DefaultPipelineName).HasMaxLength(128);
            entity.HasIndex(p => new { p.ElasticsearchClusterId, p.Name }).IsUnique();
        });
    }
}
