using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The DataServices module's view of the database — managed Postgres, Mongo, Redis, Kafka, RabbitMQ, Elastic, LDAP, Harbor and object storage.
///
/// <para>Exposes only the 31 tables DataServices owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class DataServicesDbContext(DbContextOptions<DataServicesDbContext> options) : ModuleDbContext(options)
{
    public DbSet<StorageBinding> StorageBindings => Set<StorageBinding>();
    public DbSet<ElasticsearchBinding> ElasticsearchBindings => Set<ElasticsearchBinding>();
    public DbSet<KafkaBinding> KafkaBindings => Set<KafkaBinding>();
    public DbSet<MessagingBinding> MessagingBindings => Set<MessagingBinding>();
    public DbSet<CacheBinding> CacheBindings => Set<CacheBinding>();
    public DbSet<DatabaseBinding> DatabaseBindings => Set<DatabaseBinding>();
    public DbSet<CnpgCluster> CnpgClusters => Set<CnpgCluster>();
    public DbSet<CnpgDatabase> CnpgDatabases => Set<CnpgDatabase>();
    public DbSet<CnpgBackup> CnpgBackups => Set<CnpgBackup>();
    public DbSet<MongoCluster> MongoClusters => Set<MongoCluster>();
    public DbSet<MongoDatabase> MongoDatabases => Set<MongoDatabase>();
    public DbSet<MongoBackup> MongoBackups => Set<MongoBackup>();
    public DbSet<RedisCluster> RedisClusters => Set<RedisCluster>();
    public DbSet<KafkaCluster> KafkaClusters => Set<KafkaCluster>();
    public DbSet<KafkaTopic> KafkaTopics => Set<KafkaTopic>();
    public DbSet<KafkaUser> KafkaUsers => Set<KafkaUser>();
    public DbSet<RabbitMQCluster> RabbitMQClusters => Set<RabbitMQCluster>();
    public DbSet<RabbitMQBackup> RabbitMQBackups => Set<RabbitMQBackup>();
    public DbSet<ElasticsearchCluster> ElasticsearchClusters => Set<ElasticsearchCluster>();
    public DbSet<ElasticsearchUser> ElasticsearchUsers => Set<ElasticsearchUser>();
    public DbSet<ElasticsearchDataView> ElasticsearchDataViews => Set<ElasticsearchDataView>();
    public DbSet<ElasticsearchIlmPolicy> ElasticsearchIlmPolicies => Set<ElasticsearchIlmPolicy>();
    public DbSet<ElasticsearchIngestPipeline> ElasticsearchIngestPipelines => Set<ElasticsearchIngestPipeline>();
    public DbSet<ElasticsearchKibanaSpace> ElasticsearchKibanaSpaces => Set<ElasticsearchKibanaSpace>();
    public DbSet<ElasticsearchRemoteLink> ElasticsearchRemoteLinks => Set<ElasticsearchRemoteLink>();
    public DbSet<OpenLdapComponentConfig> OpenLdapComponentConfigs => Set<OpenLdapComponentConfig>();
    public DbSet<OpenLdapUser> OpenLdapUsers => Set<OpenLdapUser>();
    public DbSet<OpenLdapGroup> OpenLdapGroups => Set<OpenLdapGroup>();
    public DbSet<OpenLdapGroupMember> OpenLdapGroupMembers => Set<OpenLdapGroupMember>();
    public DbSet<OpenLdapOrganizationalUnit> OpenLdapOrganizationalUnits => Set<OpenLdapOrganizationalUnit>();
    public DbSet<HarborComponentConfig> HarborComponentConfigs => Set<HarborComponentConfig>();
    public DbSet<HarborProject> HarborProjects => Set<HarborProject>();
    public DbSet<RegisteredPostgresInstance> RegisteredPostgresInstances => Set<RegisteredPostgresInstance>();
    public DbSet<RegisteredPostgresDatabase> RegisteredPostgresDatabases => Set<RegisteredPostgresDatabase>();
    public DbSet<RegisteredPostgresDump> RegisteredPostgresDumps => Set<RegisteredPostgresDump>();
    public DbSet<StorageLink> StorageLinks => Set<StorageLink>();
    public DbSet<DockerRegistryCredential> DockerRegistryCredentials => Set<DockerRegistryCredential>();

    // ---- Hubs owned by other modules -------------------------------------------------
    // The measurement in docs/decomposition.md §4.0 found 70 of 164 cross-module foreign
    // keys point at Tenant or Customer, and another 40 at App or KubernetesCluster. Making
    // those four readable here is what stops the boundary being merely annoying. They are
    // IQueryable rather than DbSet on purpose: readable, not writable, and not trackable.

    /// <summary>Tenant, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Tenant> Tenants => Set<Tenant>().AsNoTracking();

    /// <summary>Customer, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Customer> Customers => Set<Customer>().AsNoTracking();

    /// <summary>App, read-only: this module may look one up, never write it.</summary>
    public IQueryable<App> Apps => Set<App>().AsNoTracking();

    /// <summary>KubernetesCluster, read-only: this module may look one up, never write it.</summary>
    public IQueryable<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>().AsNoTracking();

    /// <summary>
    /// Environment, read-only: this module may look one up, never write it.
    ///
    /// <para>The fifth hub, and it was missing. <b>24 entities across 7 modules carry an
    /// <c>EnvironmentId</c></b> — Fleet's clusters, Secrets' vault secrets, Identity's JIT
    /// grants, DataServices' storage links, Catalog's certificate distributions, Connectivity's
    /// network policies — which is the same concentration that earned Tenant, Customer, App and
    /// KubernetesCluster their place here. The entity's own doc has said so since it was written:
    /// "Clusters, services, and other resources are scoped to an environment within a tenant."
    /// It is a tenant-scoped dimension row carrying an id, a tenant and a name.</para>
    ///
    /// <para>It was reachable only from <c>DeliveryDbContext</c>, which owns the table because
    /// <c>AppEnvironment</c> and <c>CustomerEnvironment</c> live beside it. That ownership is
    /// right and is unchanged; what was wrong was that every other module had to hold the
    /// all-seeing context to ask which stage a row belonged to.</para>
    /// </summary>
    public IQueryable<Data.Environment> Environments => Set<Data.Environment>().AsNoTracking();
}
