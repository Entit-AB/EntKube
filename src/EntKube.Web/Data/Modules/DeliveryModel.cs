using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Delivery"/> module —
/// Apps, environments, deployments, rollouts, policy, git and service bindings.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class DeliveryModel
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
        builder.Entity<App>().ToTable("Apps");
        builder.Entity<AppAllowedCache>().ToTable("AppAllowedCaches");
        builder.Entity<AppAllowedDatabase>().ToTable("AppAllowedDatabases");
        builder.Entity<AppAllowedStorage>().ToTable("AppAllowedStorages");
        builder.Entity<AppDeployment>().ToTable("AppDeployments");
        builder.Entity<AppDeploymentRoute>().ToTable("AppDeploymentRoutes");
        builder.Entity<AppEnvironment>().ToTable("AppEnvironments");
        builder.Entity<AppQuota>().ToTable("AppQuotas");
        builder.Entity<AppRbacPolicy>().ToTable("AppRbacPolicies");
        builder.Entity<AppRbacRule>().ToTable("AppRbacRules");
        builder.Entity<AppServiceDependency>().ToTable("AppServiceDependencies");
        builder.Entity<AppServicePort>().ToTable("AppServicePorts");
        builder.Entity<CustomerEnvironment>().ToTable("CustomerEnvironments");
        builder.Entity<CustomerGitCredential>().ToTable("CustomerGitCredentials");
        builder.Entity<CustomerGitRepoPolicy>().ToTable("CustomerGitRepoPolicies");
        builder.Entity<DeploymentAppliedResource>().ToTable("DeploymentAppliedResources");
        builder.Entity<DeploymentHealthSnapshot>().ToTable("DeploymentHealthSnapshots");
        builder.Entity<DeploymentManifest>().ToTable("DeploymentManifests");
        builder.Entity<DeploymentResource>().ToTable("DeploymentResources");
        builder.Entity<DeploymentRollout>().ToTable("DeploymentRollouts");
        builder.Entity<Data.Environment>().ToTable("Environments");
        builder.Entity<ExternalDependency>().ToTable("ExternalDependencies");
        builder.Entity<GitKnownHost>().ToTable("GitKnownHosts");
        builder.Entity<GitRepository>().ToTable("GitRepositories");
        builder.Entity<KedaScaler>().ToTable("KedaScalers");
        builder.Entity<KyvernoPolicy>().ToTable("KyvernoPolicies");
        builder.Entity<RolloutPolicy>().ToTable("RolloutPolicies");
        builder.Entity<AppServiceDependency>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.AppId);

            entity.HasOne(d => d.Tenant)
                  .WithMany()
                  .HasForeignKey(d => d.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.App)
                  .WithMany()
                  .HasForeignKey(d => d.AppId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // RolloutPolicy — one per deployment; the unique index is what stops two policies
        // disagreeing about whether a release should be rolled back.

        builder.Entity<RolloutPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.DeploymentId).IsUnique();
            entity.Property(p => p.TelemetryServiceName).HasMaxLength(200);
            entity.Property(p => p.UpdatedBy).HasMaxLength(256);
            entity.HasOne(p => p.Deployment)
                  .WithMany()
                  .HasForeignKey(p => p.DeploymentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // DeploymentRollout — the watch history. Indexed on (DeploymentId, StartedAt) for
        // the per-deployment history view, and on Status so the background watcher can find
        // open rollouts without scanning the whole table.

        builder.Entity<DeploymentRollout>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.DeploymentId, r.StartedAt });
            entity.HasIndex(r => r.Status);
            entity.Property(r => r.TriggeredBy).HasMaxLength(256);
            entity.Property(r => r.Verdict).HasMaxLength(1000);
            entity.HasOne(r => r.Deployment)
                  .WithMany()
                  .HasForeignKey(r => r.DeploymentId)
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

        // CustomerEnvironment — many-to-many join between Customer and Environment.
        // Composite key prevents duplicate memberships. Environment is Restrict for the
        // same reason AppEnvironment is: two cascade paths from Tenant would otherwise
        // meet here and SQL Server rejects that.

        builder.Entity<CustomerEnvironment>(entity =>
        {
            entity.HasKey(ce => new { ce.CustomerId, ce.EnvironmentId });

            entity.HasOne(ce => ce.Customer)
                .WithMany(c => c.CustomerEnvironments)
                .HasForeignKey(ce => ce.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ce => ce.Environment)
                .WithMany(e => e.CustomerEnvironments)
                .HasForeignKey(ce => ce.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
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
            // Existing deployments predate management gating — default them to managed.
            entity.Property(d => d.IsManaged).HasDefaultValue(true);
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

        // DeploymentAppliedResource — the applied-manifest inventory for non-Helm
        // deployments; diffed on the next apply to prune removed resources.

        builder.Entity<DeploymentAppliedResource>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.DeploymentId);
            entity.Property(r => r.Group).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Version).HasMaxLength(20).IsRequired();
            entity.Property(r => r.Kind).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(253).IsRequired();
            entity.Property(r => r.Namespace).HasMaxLength(63);

            entity.HasOne(r => r.Deployment)
                .WithMany(d => d.AppliedResources)
                .HasForeignKey(r => r.DeploymentId)
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

        // IdentityBinding — connects a Keycloak OIDC client to an app deployment.

        builder.Entity<IdentityBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.ClientUuid).HasMaxLength(36).IsRequired();
            entity.Property(b => b.ClientId).HasMaxLength(255).IsRequired();
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.KeycloakRealm)
                .WithMany()
                .HasForeignKey(b => b.KeycloakRealmId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.AppDeployment)
                .WithMany()
                .HasForeignKey(b => b.AppDeploymentId)
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

        builder.Entity<KafkaBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.KafkaCluster)
                .WithMany()
                .HasForeignKey(b => b.KafkaClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.KafkaUser)
                .WithMany()
                .HasForeignKey(b => b.KafkaUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(b => b.AppDeployment)
                .WithMany()
                .HasForeignKey(b => b.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ElasticsearchBinding>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.KubernetesSecretName).HasMaxLength(253).IsRequired();

            entity.HasOne(b => b.ElasticsearchCluster)
                .WithMany()
                .HasForeignKey(b => b.ElasticsearchClusterId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict rather than cascade: deleting a user an app is still bound to would leave the
            // app holding a Secret whose credentials no longer work, and nothing saying why. The
            // service removes the bindings first, deliberately and visibly.
            entity.HasOne(b => b.ElasticsearchUser)
                .WithMany()
                .HasForeignKey(b => b.ElasticsearchUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(b => b.AppDeployment)
                .WithMany()
                .HasForeignKey(b => b.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(b => new { b.AppDeploymentId, b.KubernetesSecretName }).IsUnique();
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

        // AppDeploymentRoute — per-deployment path + service target. Cascades from AppRoute.

        builder.Entity<AppDeploymentRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.PathPrefix).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ServiceName).HasMaxLength(200).IsRequired();
            entity.Property(r => r.GatewayName).HasMaxLength(200);
            entity.Property(r => r.GatewayNamespace).HasMaxLength(63);
            entity.Property(r => r.SessionAffinity).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.SessionAffinityKey).HasMaxLength(200);

            entity.HasOne(r => r.AppRoute)
                .WithMany(ar => ar.DeploymentRoutes)
                .HasForeignKey(r => r.AppRouteId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.AppDeployment)
                .WithMany(d => d.Routes)
                .HasForeignKey(r => r.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Connectivity model (least-privilege graph) ──
        // All three are scoped per app + environment and cascade from App
        // (single cascade path), restricted on Environment — mirroring AppNetworkPolicy.

        // AppServicePort — a typed row per port an app exposes in an environment.
        builder.Entity<AppServicePort>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.AppId, p.EnvironmentId });
            entity.Property(p => p.ServiceName).HasMaxLength(253).IsRequired();
            entity.Property(p => p.Namespace).HasMaxLength(63);
            entity.Property(p => p.PortName).HasMaxLength(63);
            entity.Property(p => p.AppProtocol).HasMaxLength(30);
            entity.Property(p => p.Protocol).HasConversion<string>().HasMaxLength(10);
            entity.Property(p => p.Source).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(p => p.App)
                .WithMany(a => a.ServicePorts)
                .HasForeignKey(p => p.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Environment)
                .WithMany()
                .HasForeignKey(p => p.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ExternalDependency — an off-cluster egress target (FQDN + port).
        builder.Entity<ExternalDependency>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => new { d.AppId, d.EnvironmentId });
            entity.Property(d => d.Host).HasMaxLength(253).IsRequired();
            entity.Property(d => d.Protocol).HasConversion<string>().HasMaxLength(10);
            entity.Property(d => d.Source).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(d => d.App)
                .WithMany(a => a.ExternalDependencies)
                .HasForeignKey(d => d.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Environment)
                .WithMany()
                .HasForeignKey(d => d.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
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

        // KyvernoPolicy — Kyverno admission policy at tenant+environment scope.
        // Built-in types are singleton per (tenant, environment, type); Custom type allows multiples.

        builder.Entity<KyvernoPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.TenantId, p.EnvironmentId, p.PolicyType });
            entity.Property(p => p.PolicyType).HasConversion<string>().HasMaxLength(50).IsRequired();
            entity.Property(p => p.ValidationFailureAction).HasConversion<string>().HasMaxLength(10).IsRequired();
            entity.Property(p => p.Name).HasMaxLength(63);

            entity.HasOne(p => p.Tenant)
                .WithMany()
                .HasForeignKey(p => p.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Environment)
                .WithMany()
                .HasForeignKey(p => p.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // KedaScaler — autoscaler (KEDA ScaledObject / Custom YAML / native HPA) scoped per
        // (App, Environment). Name is unique within that scope and used as the Kubernetes
        // resource name.

        builder.Entity<KedaScaler>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.AppId, s.EnvironmentId, s.Name }).IsUnique();
            entity.HasIndex(s => new { s.TenantId, s.EnvironmentId });
            entity.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(63).IsRequired();
            entity.Property(s => s.ScaleTargetKind).HasMaxLength(63);

            entity.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.App)
                .WithMany()
                .HasForeignKey(s => s.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Environment)
                .WithMany()
                .HasForeignKey(s => s.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
