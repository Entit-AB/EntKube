using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EntKube.Web.Data;

/// <summary>
/// The main database context for EntKube, holding Identity tables and all
/// application entities. This is the base context — provider-specific
/// subclasses exist for PostgreSQL and SQL Server so EF Core can generate
/// provider-appropriate migrations independently.
/// </summary>
public class ApplicationDbContext(DbContextOptions options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<Environment> Environments => Set<Environment>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppEnvironment> AppEnvironments => Set<AppEnvironment>();
    public DbSet<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>();
    public DbSet<SecretVault> SecretVaults => Set<SecretVault>();
    public DbSet<VaultSecret> VaultSecrets => Set<VaultSecret>();
    public DbSet<VaultSecretVersion> VaultSecretVersions => Set<VaultSecretVersion>();
    public DbSet<ClusterComponent> ClusterComponents => Set<ClusterComponent>();
    public DbSet<ExternalRoute> ExternalRoutes => Set<ExternalRoute>();
    public DbSet<StorageLink> StorageLinks => Set<StorageLink>();
    public DbSet<OpenStackConnection> OpenStackConnections => Set<OpenStackConnection>();
    public DbSet<StorageBinding> StorageBindings => Set<StorageBinding>();
    public DbSet<AppDeployment> AppDeployments => Set<AppDeployment>();
    public DbSet<DeploymentManifest> DeploymentManifests => Set<DeploymentManifest>();
    public DbSet<DeploymentResource> DeploymentResources => Set<DeploymentResource>();
    public DbSet<CustomerAccess> CustomerAccesses => Set<CustomerAccess>();
    public DbSet<CnpgCluster> CnpgClusters => Set<CnpgCluster>();
    public DbSet<CnpgDatabase> CnpgDatabases => Set<CnpgDatabase>();
    public DbSet<CnpgBackup> CnpgBackups => Set<CnpgBackup>();
    public DbSet<MongoCluster> MongoClusters => Set<MongoCluster>();
    public DbSet<MongoDatabase> MongoDatabases => Set<MongoDatabase>();
    public DbSet<DatabaseBinding> DatabaseBindings => Set<DatabaseBinding>();
    public DbSet<MongoBackup> MongoBackups => Set<MongoBackup>();
    public DbSet<KeycloakComponentConfig> KeycloakComponentConfigs => Set<KeycloakComponentConfig>();
    public DbSet<KeycloakRealm> KeycloakRealms => Set<KeycloakRealm>();
    public DbSet<KeycloakTheme> KeycloakThemes => Set<KeycloakTheme>();
    public DbSet<KeycloakBackup> KeycloakBackups => Set<KeycloakBackup>();
    public DbSet<RegisteredPostgresInstance> RegisteredPostgresInstances => Set<RegisteredPostgresInstance>();
    public DbSet<RegisteredPostgresDatabase> RegisteredPostgresDatabases => Set<RegisteredPostgresDatabase>();
    public DbSet<RegisteredPostgresDump> RegisteredPostgresDumps => Set<RegisteredPostgresDump>();
    public DbSet<HarborComponentConfig> HarborComponentConfigs => Set<HarborComponentConfig>();
    public DbSet<HarborProject> HarborProjects => Set<HarborProject>();
    public DbSet<DockerRegistryCredential> DockerRegistryCredentials => Set<DockerRegistryCredential>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RabbitMQCluster> RabbitMQClusters => Set<RabbitMQCluster>();
    public DbSet<RabbitMQBackup> RabbitMQBackups => Set<RabbitMQBackup>();
    public DbSet<MessagingBinding> MessagingBindings => Set<MessagingBinding>();
    public DbSet<AlertIncident> AlertIncidents => Set<AlertIncident>();
    public DbSet<IncidentNote> IncidentNotes => Set<IncidentNote>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<DeploymentHealthSnapshot> DeploymentHealthSnapshots => Set<DeploymentHealthSnapshot>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<SlaTarget> SlaTargets => Set<SlaTarget>();
    public DbSet<ExternalRouteHealthHistory> ExternalRouteHealthHistories => Set<ExternalRouteHealthHistory>();
    public DbSet<VpnTunnel> VpnTunnels => Set<VpnTunnel>();
    public DbSet<VpnLocalEndpoint> VpnLocalEndpoints => Set<VpnLocalEndpoint>();
    public DbSet<VpnRemoteEndpoint> VpnRemoteEndpoints => Set<VpnRemoteEndpoint>();
    public DbSet<RedisCluster> RedisClusters => Set<RedisCluster>();
    public DbSet<CacheBinding> CacheBindings => Set<CacheBinding>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<GitKnownHost> GitKnownHosts => Set<GitKnownHost>();
    public DbSet<CustomerGitRepoPolicy> CustomerGitRepoPolicies => Set<CustomerGitRepoPolicy>();
    public DbSet<CustomerGitCredential> CustomerGitCredentials => Set<CustomerGitCredential>();
    public DbSet<AppQuota> AppQuotas => Set<AppQuota>();
    public DbSet<AppNetworkPolicy> AppNetworkPolicies => Set<AppNetworkPolicy>();
    public DbSet<AppRbacPolicy> AppRbacPolicies => Set<AppRbacPolicy>();
    public DbSet<AppRbacRule> AppRbacRules => Set<AppRbacRule>();
    public DbSet<AppRoute> AppRoutes => Set<AppRoute>();
    public DbSet<AppDeploymentRoute> AppDeploymentRoutes => Set<AppDeploymentRoute>();
    public DbSet<AppAllowedDatabase> AppAllowedDatabases => Set<AppAllowedDatabase>();
    public DbSet<AppAllowedCache> AppAllowedCaches => Set<AppAllowedCache>();
    public DbSet<AppAllowedStorage> AppAllowedStorages => Set<AppAllowedStorage>();
    public DbSet<OnCallSchedule> OnCallSchedules => Set<OnCallSchedule>();
    public DbSet<OnCallShift> OnCallShifts => Set<OnCallShift>();
    public DbSet<AlertRoutingRule> AlertRoutingRules => Set<AlertRoutingRule>();
    public DbSet<NotificationProviderConfig> NotificationProviderConfigs => Set<NotificationProviderConfig>();
    public DbSet<ClusterServer> ClusterServers => Set<ClusterServer>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Suppress the PendingModelChangesWarning during development.
        // The model may temporarily drift from the snapshot while iterating
        // on entity design — this prevents the app from failing to start.

        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Tenant — the slug must be unique across all tenants since it's
        // used as the URL-safe identifier in routes and API calls.

        builder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.Slug).IsUnique();
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.Slug).HasMaxLength(100).IsRequired();
        });

        // TenantRole — each role name must be unique within its tenant.
        // A tenant owns its roles; deleting a tenant cascades to its roles.

        builder.Entity<TenantRole>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
            entity.Property(r => r.Name).HasMaxLength(100).IsRequired();

            entity.HasOne(r => r.Tenant)
                .WithMany(t => t.Roles)
                .HasForeignKey(r => r.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TenantMembership — composite key on (UserId, TenantId) ensures
        // a user can only have one membership per tenant. The role FK tells
        // us what they can do in that tenant.

        builder.Entity<TenantMembership>(entity =>
        {
            entity.HasKey(m => new { m.UserId, m.TenantId });

            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Tenant)
                .WithMany(t => t.Memberships)
                .HasForeignKey(m => m.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Role)
                .WithMany(r => r.Memberships)
                .HasForeignKey(m => m.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Group — belongs to a tenant. Name should be unique within a tenant.

        builder.Entity<Group>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
            entity.Property(g => g.Name).HasMaxLength(200).IsRequired();

            entity.HasOne(g => g.Tenant)
                .WithMany(t => t.Groups)
                .HasForeignKey(g => g.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GroupMembership — composite key on (UserId, GroupId) prevents
        // a user from being added to the same group twice.

        builder.Entity<GroupMembership>(entity =>
        {
            entity.HasKey(gm => new { gm.UserId, gm.GroupId });

            entity.HasOne(gm => gm.User)
                .WithMany()
                .HasForeignKey(gm => gm.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(gm => gm.Group)
                .WithMany(g => g.Memberships)
                .HasForeignKey(gm => gm.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Environment — belongs to a tenant. Name must be unique within a tenant.

        builder.Entity<Environment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();

            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Environments)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Customer — belongs to a tenant. Name must be unique within a tenant.

        builder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();

            entity.HasOne(c => c.Tenant)
                .WithMany(t => t.Customers)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // App — belongs to a customer. Name must be unique within a customer.

        builder.Entity<App>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.CustomerId, a.Name }).IsUnique();
            entity.Property(a => a.Name).HasMaxLength(200).IsRequired();

            entity.HasOne(a => a.Customer)
                .WithMany(c => c.Apps)
                .HasForeignKey(a => a.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppEnvironment — many-to-many join between App and Environment.
        // Composite key prevents duplicate links.

        builder.Entity<AppEnvironment>(entity =>
        {
            entity.HasKey(ae => new { ae.AppId, ae.EnvironmentId });

            entity.HasOne(ae => ae.App)
                .WithMany(a => a.AppEnvironments)
                .HasForeignKey(ae => ae.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ae => ae.Environment)
                .WithMany(e => e.AppEnvironments)
                .HasForeignKey(ae => ae.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

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
            entity.Property(s => s.KubernetesSecretName).HasMaxLength(253);
            entity.Property(s => s.KubernetesNamespace).HasMaxLength(63);

            entity.HasIndex(s => new { s.VaultId, s.AppId, s.Name })
                .IsUnique()
                .HasFilter(null);

            entity.HasIndex(s => new { s.VaultId, s.ComponentId, s.Name })
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

            entity.HasOne(d => d.KubernetesCluster)
                .WithMany()
                .HasForeignKey(d => d.KubernetesClusterId)
                .OnDelete(DeleteBehavior.SetNull);
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

        // ExternalRoute — exposes a component via Gateway API HTTPRoute.
        // Hostname must be unique within a cluster (enforced via component→cluster).

        builder.Entity<ExternalRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Hostname).HasMaxLength(253).IsRequired();
            entity.Property(r => r.ServiceName).HasMaxLength(200);
            entity.Property(r => r.PathPrefix).HasMaxLength(200);
            entity.Property(r => r.ClusterIssuerName).HasMaxLength(200);
            entity.Property(r => r.GatewayName).HasMaxLength(200);
            entity.Property(r => r.GatewayNamespace).HasMaxLength(63);
            entity.Property(r => r.TlsMode).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(r => r.Component)
                .WithMany(c => c.ExternalRoutes)
                .HasForeignKey(r => r.ComponentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppDeployment — a deployable unit targeting a specific cluster and namespace.
        // Name must be unique within an app. Stores sync/health status like ArgoCD.

        builder.Entity<AppDeployment>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => new { d.AppId, d.Name }).IsUnique();
            entity.Property(d => d.Name).HasMaxLength(200).IsRequired();
            entity.Property(d => d.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(d => d.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.SyncStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.HealthStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.HelmRepoUrl).HasMaxLength(500);
            entity.Property(d => d.HelmChartName).HasMaxLength(200);
            entity.Property(d => d.HelmChartVersion).HasMaxLength(50);

            entity.HasOne(d => d.App)
                .WithMany(a => a.Deployments)
                .HasForeignKey(d => d.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Environment)
                .WithMany()
                .HasForeignKey(d => d.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(d => d.Cluster)
                .WithMany()
                .HasForeignKey(d => d.ClusterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // DeploymentManifest — an individual K8s YAML document within a deployment.
        // Applied in SortOrder sequence. Name is informational, not necessarily unique.

        builder.Entity<DeploymentManifest>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Kind).HasMaxLength(100).IsRequired();
            entity.Property(m => m.Name).HasMaxLength(253).IsRequired();

            entity.HasOne(m => m.Deployment)
                .WithMany(d => d.Manifests)
                .HasForeignKey(m => m.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DeploymentResource — a tracked live resource in the cluster (ArgoCD-style
        // resource tree). Resources form a parent-child hierarchy for tree rendering.

        builder.Entity<DeploymentResource>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Group).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Version).HasMaxLength(20).IsRequired();
            entity.Property(r => r.Kind).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(253).IsRequired();
            entity.Property(r => r.Namespace).HasMaxLength(63);
            entity.Property(r => r.SyncStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.HealthStatus).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(r => r.Deployment)
                .WithMany(d => d.Resources)
                .HasForeignKey(r => r.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.ParentResource)
                .WithMany(r => r.ChildResources)
                .HasForeignKey(r => r.ParentResourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // CustomerAccess — composite key on (UserId, CustomerId) ensures
        // a user gets exactly one access entry per customer. The Role enum
        // controls what they can do (Viewer, Operator, Admin).

        builder.Entity<CustomerAccess>(entity =>
        {
            entity.HasKey(ca => new { ca.UserId, ca.CustomerId });

            entity.Property(ca => ca.Role)
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.HasOne(ca => ca.User)
                .WithMany()
                .HasForeignKey(ca => ca.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ca => ca.Customer)
                .WithMany()
                .HasForeignKey(ca => ca.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // StorageBinding — connects a StorageLink to a workload (AppDeployment or
        // ClusterComponent). The platform syncs storage credentials to a K8s Secret
        // in the workload's namespace so pods can consume them.

        builder.Entity<StorageBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.StorageLink)
                .WithMany(s => s.StorageBindings)
                .HasForeignKey(b => b.StorageLinkId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.AppDeployment)
                .WithMany(d => d.StorageBindings)
                .HasForeignKey(b => b.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.Component)
                .WithMany(c => c.StorageBindings)
                .HasForeignKey(b => b.ComponentId)
                .OnDelete(DeleteBehavior.Cascade);
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

        // KeycloakComponentConfig — links a detected Keycloak ClusterComponent to its
        // backing CNPG database. DB credentials and the admin password are stored as
        // component vault secrets (ComponentId = ClusterComponentId) and synced to K8s.

        builder.Entity<KeycloakComponentConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.AdminUsername).HasMaxLength(100).IsRequired();
            entity.Property(c => c.AdminUrl).HasMaxLength(500);
            entity.Property(c => c.DisplayName).HasMaxLength(200);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ClusterComponent)
                .WithMany()
                .HasForeignKey(c => c.ClusterComponentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.CnpgDatabase)
                .WithMany()
                .HasForeignKey(c => c.CnpgDatabaseId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.RegisteredPostgresDatabase)
                .WithMany()
                .HasForeignKey(c => c.RegisteredPostgresDatabaseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // KeycloakTheme — a named CSS theme bundle for a Keycloak instance.
        // Name must be unique within the instance. CSS is stored separately in
        // the vault (keyed by theme Id). Multiple realms can reference the same theme.

        builder.Entity<KeycloakTheme>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.LoginTheme).HasMaxLength(100);
            entity.Property(t => t.AccountTheme).HasMaxLength(100);

            entity.HasIndex(t => new { t.KeycloakComponentConfigId, t.Name }).IsUnique();

            entity.HasOne(t => t.ComponentConfig)
                .WithMany()
                .HasForeignKey(t => t.KeycloakComponentConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // KeycloakRealm — a realm managed via a component config. RealmName must be
        // unique within a config (Keycloak enforces this globally per server).

        builder.Entity<KeycloakRealm>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.RealmName).HasMaxLength(100).IsRequired();
            entity.Property(r => r.DisplayName).HasMaxLength(200).IsRequired();

            entity.HasIndex(r => new { r.KeycloakComponentConfigId, r.RealmName }).IsUnique();

            entity.HasOne(r => r.ComponentConfig)
                .WithMany(c => c.Realms)
                .HasForeignKey(r => r.KeycloakComponentConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.LinkedApp)
                .WithMany()
                .HasForeignKey(r => r.LinkedAppId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.Theme)
                .WithMany(t => t.Realms)
                .HasForeignKey(r => r.KeycloakThemeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // KeycloakBackup — realm JSON snapshots stored in S3.

        builder.Entity<KeycloakBackup>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.ObjectKey).HasMaxLength(1024).IsRequired();
            entity.Property(b => b.RealmName).HasMaxLength(100).IsRequired();

            entity.HasOne(b => b.Realm)
                .WithMany(r => r.Backups)
                .HasForeignKey(b => b.KeycloakRealmId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.StorageLink)
                .WithMany()
                .HasForeignKey(b => b.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
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

        // DatabaseBinding — connects a managed database (CNPG, MongoDB, or registered
        // Postgres) to an AppDeployment. The platform syncs credentials into the app's
        // namespace automatically, including after password rotations.

        builder.Entity<DatabaseBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.CnpgDatabase)
                .WithMany(d => d.DatabaseBindings)
                .HasForeignKey(b => b.CnpgDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.MongoDatabase)
                .WithMany(d => d.DatabaseBindings)
                .HasForeignKey(b => b.MongoDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.RegisteredPostgresDatabase)
                .WithMany(d => d.DatabaseBindings)
                .HasForeignKey(b => b.RegisteredPostgresDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.AppDeployment)
                .WithMany(d => d.DatabaseBindings)
                .HasForeignKey(b => b.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // HarborComponentConfig — links an installed Harbor ClusterComponent to its
        // CNPG database and S3 storage link. One config per Harbor install.
        // Admin password and S3/DB credentials are stored in the vault (not here).

        builder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Action).HasMaxLength(100).IsRequired();
            entity.Property(a => a.ResourceKind).HasMaxLength(100).IsRequired();
            entity.Property(a => a.ResourceName).HasMaxLength(253);
            entity.Property(a => a.PerformedBy).HasMaxLength(256);
            entity.Property(a => a.Details).HasMaxLength(1000);

            entity.HasIndex(a => a.DeploymentId);
            entity.HasIndex(a => a.OccurredAt);

            entity.HasOne(a => a.Deployment)
                .WithMany()
                .HasForeignKey(a => a.DeploymentId)
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

        builder.Entity<CacheBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.RedisCluster)
                .WithMany()
                .HasForeignKey(b => b.RedisClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.AppDeployment)
                .WithMany(d => d.CacheBindings)
                .HasForeignKey(b => b.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.SeverityFilter).HasConversion<string>().HasMaxLength(30);

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

        builder.Entity<DeploymentHealthSnapshot>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.DeploymentId, s.SnapshotAt });
            entity.Property(s => s.HealthStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.SyncStatus).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(s => s.Deployment)
                .WithMany()
                .HasForeignKey(s => s.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<SlaTarget>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.TenantId, s.CustomerId, s.AppId }).IsUnique().HasFilter(null);

            entity.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Customer)
                .WithMany()
                .HasForeignKey(s => s.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.App)
                .WithMany()
                .HasForeignKey(s => s.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ExternalRouteHealthHistory>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.HasIndex(h => new { h.RouteId, h.CheckedAt });

            entity.HasOne(h => h.Route)
                .WithMany()
                .HasForeignKey(h => h.RouteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MessagingBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Vhost).HasMaxLength(200).IsRequired();
            entity.Property(b => b.QueueName).HasMaxLength(255);
            entity.Property(b => b.ExchangeName).HasMaxLength(255);
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.Cluster)
                .WithMany()
                .HasForeignKey(b => b.RabbitMQClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.AppDeployment)
                .WithMany()
                .HasForeignKey(b => b.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VpnTunnel — scoped to a tenant, name unique within a tenant.

        builder.Entity<VpnTunnel>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.TunnelType).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.IkeProposal).HasMaxLength(100).IsRequired();
            entity.Property(t => t.EspProposal).HasMaxLength(100).IsRequired();

            entity.HasOne(t => t.Tenant)
                .WithMany()
                .HasForeignKey(t => t.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VpnLocalEndpoint — a platform cluster participating in a VPN tunnel.

        builder.Entity<VpnLocalEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subnets).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.PublicIp).HasMaxLength(45);
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Tunnel)
                .WithMany(t => t.LocalEndpoints)
                .HasForeignKey(e => e.VpnTunnelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Cluster)
                .WithMany()
                .HasForeignKey(e => e.ClusterId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Component)
                .WithMany()
                .HasForeignKey(e => e.ComponentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // VpnRemoteEndpoint — an external site for SiteToSite tunnels. PSK/cert stored
        // as VaultSecret entries scoped to this endpoint via VpnRemoteEndpointId.

        builder.Entity<VpnRemoteEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PublicIp).HasMaxLength(45).IsRequired();
            entity.Property(e => e.Subnets).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.AuthMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.LocalId).HasMaxLength(500);
            entity.Property(e => e.RemoteId).HasMaxLength(500);

            entity.HasOne(e => e.Tunnel)
                .WithMany(t => t.RemoteEndpoints)
                .HasForeignKey(e => e.VpnTunnelId)
                .OnDelete(DeleteBehavior.Cascade);
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

        // CustomerGitRepoPolicy — URL allowlist per customer per environment (wildcard patterns).
        builder.Entity<CustomerGitRepoPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.CustomerId, p.EnvironmentId, p.UrlPattern }).IsUnique();
            entity.Property(p => p.UrlPattern).HasMaxLength(2000).IsRequired();

            entity.HasOne(p => p.Customer)
                .WithMany(c => c.GitRepoPolicies)
                .HasForeignKey(p => p.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Environment)
                .WithMany()
                .HasForeignKey(p => p.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // CustomerGitCredential — reusable credential sets per customer per environment.
        builder.Entity<CustomerGitCredential>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.CustomerId, c.EnvironmentId, c.Name }).IsUnique();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.AuthType).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.Username).HasMaxLength(300);
            entity.Property(c => c.UrlPattern).HasMaxLength(500);

            entity.HasOne(c => c.Customer)
                .WithMany(cu => cu.GitCredentials)
                .HasForeignKey(c => c.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Environment)
                .WithMany()
                .HasForeignKey(c => c.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // VaultSecret — CustomerGitCredential scoping (PAT / SSH key / password for a customer credential).
        builder.Entity<VaultSecret>()
            .HasOne(s => s.CustomerGitCredential)
            .WithMany()
            .HasForeignKey(s => s.CustomerGitCredentialId)
            .OnDelete(DeleteBehavior.Cascade);

        // App — add Namespace field constraint.
        builder.Entity<App>(entity =>
        {
            entity.Property(a => a.Namespace).HasMaxLength(63);
        });

        // AppQuota — one per app per environment. Cascades on app delete.
        builder.Entity<AppQuota>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.HasIndex(q => new { q.AppId, q.EnvironmentId }).IsUnique();
            entity.Property(q => q.CpuRequest).HasMaxLength(20);
            entity.Property(q => q.CpuLimit).HasMaxLength(20);
            entity.Property(q => q.MemoryRequest).HasMaxLength(20);
            entity.Property(q => q.MemoryLimit).HasMaxLength(20);

            entity.HasOne(q => q.App)
                .WithMany(a => a.Quotas)
                .HasForeignKey(q => q.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(q => q.Environment)
                .WithMany()
                .HasForeignKey(q => q.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AppNetworkPolicy — many per app per environment; name unique within (app, environment).
        builder.Entity<AppNetworkPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.AppId, p.EnvironmentId, p.Name }).IsUnique();
            entity.Property(p => p.Name).HasMaxLength(63).IsRequired();
            entity.Property(p => p.PolicyType).HasConversion<string>().HasMaxLength(30);
            entity.Property(p => p.AllowFromNamespace).HasMaxLength(63);

            entity.HasOne(p => p.App)
                .WithMany(a => a.NetworkPolicies)
                .HasForeignKey(p => p.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Environment)
                .WithMany()
                .HasForeignKey(p => p.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AppRbacPolicy — one per app per environment.
        builder.Entity<AppRbacPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.AppId, p.EnvironmentId }).IsUnique();
            entity.Property(p => p.ServiceAccountName).HasMaxLength(63).IsRequired();

            entity.HasOne(p => p.App)
                .WithMany(a => a.RbacPolicies)
                .HasForeignKey(p => p.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Environment)
                .WithMany()
                .HasForeignKey(p => p.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AppRbacRule — many per AppRbacPolicy.
        builder.Entity<AppRbacRule>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.ApiGroups).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Resources).HasMaxLength(500).IsRequired();
            entity.Property(r => r.Verbs).HasMaxLength(200).IsRequired();

            entity.HasOne(r => r.Policy)
                .WithMany(p => p.Rules)
                .HasForeignKey(r => r.AppRbacPolicyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GitRepository — tenant-scoped, name unique within a tenant.

        builder.Entity<GitRepository>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
            entity.Property(r => r.Name).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Url).HasMaxLength(2000).IsRequired();
            entity.Property(r => r.AuthType).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Username).HasMaxLength(300);
            entity.Property(r => r.DefaultBranch).HasMaxLength(200).HasDefaultValue("main");

            entity.HasOne(r => r.Tenant)
                .WithMany(t => t.GitRepositories)
                .HasForeignKey(r => r.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.CustomerGitCredential)
                .WithMany()
                .HasForeignKey(r => r.CustomerGitCredentialId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // GitKnownHost — trusted SSH host fingerprints, unique per (TenantId, Hostname).

        builder.Entity<GitKnownHost>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.HasIndex(h => new { h.TenantId, h.Hostname }).IsUnique();
            entity.Property(h => h.Hostname).HasMaxLength(253).IsRequired();
            entity.Property(h => h.Fingerprint).HasMaxLength(200).IsRequired();
            entity.Property(h => h.KeyType).HasMaxLength(50).IsRequired();

            entity.HasOne(h => h.Tenant)
                .WithMany(t => t.GitKnownHosts)
                .HasForeignKey(h => h.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppDeployment — git source FK and parent/child app-of-apps relationship.

        builder.Entity<AppDeployment>(entity =>
        {
            entity.Property(d => d.GitPath).HasMaxLength(500);
            entity.Property(d => d.GitRevision).HasMaxLength(200);
            entity.Property(d => d.GitLastSyncedCommit).HasMaxLength(40);

            entity.HasOne(d => d.GitRepository)
                .WithMany(r => r.Deployments)
                .HasForeignKey(d => d.GitRepositoryId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(d => d.ParentDeployment)
                .WithMany(d => d.ChildDeployments)
                .HasForeignKey(d => d.ParentDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppRoute — app-level hostname + TLS config. Cascades from App.

        builder.Entity<AppRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Hostname).HasMaxLength(253).IsRequired();
            entity.Property(r => r.TlsMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.ClusterIssuerName).HasMaxLength(200);

            entity.HasOne(r => r.App)
                .WithMany(a => a.Routes)
                .HasForeignKey(r => r.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppDeploymentRoute — per-deployment path + service target. Cascades from AppRoute.

        builder.Entity<AppDeploymentRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.PathPrefix).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ServiceName).HasMaxLength(200).IsRequired();
            entity.Property(r => r.GatewayName).HasMaxLength(200);
            entity.Property(r => r.GatewayNamespace).HasMaxLength(63);

            entity.HasOne(r => r.AppRoute)
                .WithMany(ar => ar.DeploymentRoutes)
                .HasForeignKey(r => r.AppRouteId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.AppDeployment)
                .WithMany(d => d.Routes)
                .HasForeignKey(r => r.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppAllowedDatabase — governance allowlist for which databases an app may bind to
        // in a given environment. Cascades when the app is deleted; restricted on environment.

        builder.Entity<AppAllowedDatabase>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.HasOne(a => a.App)
                .WithMany(app => app.AllowedDatabases)
                .HasForeignKey(a => a.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Environment)
                .WithMany()
                .HasForeignKey(a => a.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.CnpgDatabase)
                .WithMany()
                .HasForeignKey(a => a.CnpgDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.MongoDatabase)
                .WithMany()
                .HasForeignKey(a => a.MongoDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.RegisteredPostgresDatabase)
                .WithMany()
                .HasForeignKey(a => a.RegisteredPostgresDatabaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppAllowedCache — governance allowlist for which Redis clusters an app may bind to.

        builder.Entity<AppAllowedCache>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.HasOne(a => a.App)
                .WithMany(app => app.AllowedCaches)
                .HasForeignKey(a => a.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Environment)
                .WithMany()
                .HasForeignKey(a => a.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.RedisCluster)
                .WithMany()
                .HasForeignKey(a => a.RedisClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppAllowedStorage — governance allowlist for which StorageLinks an app may bind to.

        builder.Entity<AppAllowedStorage>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.HasOne(a => a.App)
                .WithMany(app => app.AllowedStorages)
                .HasForeignKey(a => a.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Environment)
                .WithMany()
                .HasForeignKey(a => a.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.StorageLink)
                .WithMany()
                .HasForeignKey(a => a.StorageLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OnCallSchedule — named rotation schedule owned by a tenant.

        builder.Entity<OnCallSchedule>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Description).HasMaxLength(500);

            entity.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(s => s.Shifts)
                .WithOne(sh => sh.Schedule)
                .HasForeignKey(sh => sh.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OnCallShift — a single time-boxed assignment within a schedule.

        builder.Entity<OnCallShift>(entity =>
        {
            entity.HasKey(sh => sh.Id);
            entity.HasIndex(sh => sh.ScheduleId);
            entity.HasIndex(sh => sh.StartsAt);
            entity.Property(sh => sh.AssigneeName).HasMaxLength(256).IsRequired();
            entity.Property(sh => sh.AssigneeEmail).HasMaxLength(256);
            entity.Property(sh => sh.Notes).HasMaxLength(1000);
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
            entity.HasIndex(c => c.ProviderType).IsUnique();
            entity.Property(c => c.ProviderType).HasConversion<string>().HasMaxLength(30);
            entity.Property(c => c.UpdatedByUserId).HasMaxLength(450);
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
    }
}

/// <summary>
/// PostgreSQL-specific context. Used only for generating and applying
/// PostgreSQL migrations. Shares the same model as the base context.
/// </summary>
public class PostgresApplicationDbContext(DbContextOptions<PostgresApplicationDbContext> options)
    : ApplicationDbContext(options)
{
}

/// <summary>
/// SQL Server-specific context. Used only for generating and applying
/// SQL Server migrations. Shares the same model as the base context.
/// </summary>
public class SqlServerApplicationDbContext(DbContextOptions<SqlServerApplicationDbContext> options)
    : ApplicationDbContext(options)
{
}
