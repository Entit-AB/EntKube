using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Delivery module's view of the database — apps, environments, deployments, rollouts, policy, git and service bindings.
///
/// <para>Exposes only the 34 tables Delivery owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : ModuleDbContext(options)
{
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppEnvironment> AppEnvironments => Set<AppEnvironment>();
    public DbSet<Data.Environment> Environments => Set<Data.Environment>();
    public DbSet<CustomerEnvironment> CustomerEnvironments => Set<CustomerEnvironment>();
    public DbSet<AppDeployment> AppDeployments => Set<AppDeployment>();
    public DbSet<AppDeploymentRoute> AppDeploymentRoutes => Set<AppDeploymentRoute>();
    public DbSet<AppQuota> AppQuotas => Set<AppQuota>();
    public DbSet<AppRbacPolicy> AppRbacPolicies => Set<AppRbacPolicy>();
    public DbSet<AppRbacRule> AppRbacRules => Set<AppRbacRule>();
    public DbSet<AppAllowedCache> AppAllowedCaches => Set<AppAllowedCache>();
    public DbSet<AppAllowedDatabase> AppAllowedDatabases => Set<AppAllowedDatabase>();
    public DbSet<AppAllowedStorage> AppAllowedStorages => Set<AppAllowedStorage>();
    public DbSet<AppServiceDependency> AppServiceDependencies => Set<AppServiceDependency>();
    public DbSet<AppServicePort> AppServicePorts => Set<AppServicePort>();
    public DbSet<ExternalDependency> ExternalDependencies => Set<ExternalDependency>();
    public DbSet<DeploymentManifest> DeploymentManifests => Set<DeploymentManifest>();
    public DbSet<DeploymentResource> DeploymentResources => Set<DeploymentResource>();
    public DbSet<DeploymentAppliedResource> DeploymentAppliedResources => Set<DeploymentAppliedResource>();
    public DbSet<DeploymentHealthSnapshot> DeploymentHealthSnapshots => Set<DeploymentHealthSnapshot>();
    public DbSet<DeploymentRollout> DeploymentRollouts => Set<DeploymentRollout>();
    public DbSet<RolloutPolicy> RolloutPolicies => Set<RolloutPolicy>();
    public DbSet<KedaScaler> KedaScalers => Set<KedaScaler>();
    public DbSet<KyvernoPolicy> KyvernoPolicies => Set<KyvernoPolicy>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<GitKnownHost> GitKnownHosts => Set<GitKnownHost>();
    public DbSet<CustomerGitCredential> CustomerGitCredentials => Set<CustomerGitCredential>();
    public DbSet<CustomerGitRepoPolicy> CustomerGitRepoPolicies => Set<CustomerGitRepoPolicy>();
    public DbSet<CacheBinding> CacheBindings => Set<CacheBinding>();
    public DbSet<DatabaseBinding> DatabaseBindings => Set<DatabaseBinding>();
    public DbSet<ElasticsearchBinding> ElasticsearchBindings => Set<ElasticsearchBinding>();
    public DbSet<KafkaBinding> KafkaBindings => Set<KafkaBinding>();
    public DbSet<MessagingBinding> MessagingBindings => Set<MessagingBinding>();
    public DbSet<StorageBinding> StorageBindings => Set<StorageBinding>();
    public DbSet<IdentityBinding> IdentityBindings => Set<IdentityBinding>();

    // ---- Hubs owned by other modules -------------------------------------------------
    // The measurement in docs/decomposition.md §4.0 found 70 of 164 cross-module foreign
    // keys point at Tenant or Customer, and another 40 at App or KubernetesCluster. Making
    // those four readable here is what stops the boundary being merely annoying. They are
    // IQueryable rather than DbSet on purpose: readable, not writable, and not trackable.

    /// <summary>Tenant, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Tenant> Tenants => Set<Tenant>().AsNoTracking();

    /// <summary>Customer, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Customer> Customers => Set<Customer>().AsNoTracking();

    /// <summary>KubernetesCluster, read-only: this module may look one up, never write it.</summary>
    public IQueryable<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>().AsNoTracking();
}
