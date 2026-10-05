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
    public DbSet<CustomerEnvironment> CustomerEnvironments => Set<CustomerEnvironment>();
    public DbSet<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>();
    public DbSet<EgressAgent> EgressAgents => Set<EgressAgent>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<JitGrant> JitGrants => Set<JitGrant>();
    public DbSet<ClusterCostRate> ClusterCostRates => Set<ClusterCostRate>();
    public DbSet<ApplicationContract> ApplicationContracts => Set<ApplicationContract>();
    public DbSet<ApplicationServiceLevel> ApplicationServiceLevels => Set<ApplicationServiceLevel>();
    public DbSet<PortfolioAgreement> PortfolioAgreements => Set<PortfolioAgreement>();
    public DbSet<ContractContact> ContractContacts => Set<ContractContact>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListEntry> PriceListEntries => Set<PriceListEntry>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<TicketPause> TicketPauses => Set<TicketPause>();
    public DbSet<TicketAffectedApp> TicketAffectedApps => Set<TicketAffectedApp>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<WorkAuthorisation> WorkAuthorisations => Set<WorkAuthorisation>();
    public DbSet<KnowledgeSection> KnowledgeSections => Set<KnowledgeSection>();
    public DbSet<KnowledgeRevision> KnowledgeRevisions => Set<KnowledgeRevision>();
    public DbSet<AppKnowledgeProfile> AppKnowledgeProfiles => Set<AppKnowledgeProfile>();
    public DbSet<AppServiceDependency> AppServiceDependencies => Set<AppServiceDependency>();
    public DbSet<EndOfLifeNotice> EndOfLifeNotices => Set<EndOfLifeNotice>();
    public DbSet<Subconsultant> Subconsultants => Set<Subconsultant>();
    public DbSet<InboundMailMessage> InboundMailMessages => Set<InboundMailMessage>();
    public DbSet<MailSuggestion> MailSuggestions => Set<MailSuggestion>();
    public DbSet<MailTriageRule> MailTriageRules => Set<MailTriageRule>();
    public DbSet<SupportMailbox> SupportMailboxes => Set<SupportMailbox>();

    public DbSet<TicketBridgeConnection> TicketBridgeConnections => Set<TicketBridgeConnection>();

    public DbSet<ExternalTicketLink> ExternalTicketLinks => Set<ExternalTicketLink>();
    public DbSet<CustomerEmailDomain> CustomerEmailDomains => Set<CustomerEmailDomain>();
    public DbSet<CustomerSupportAddress> CustomerSupportAddresses => Set<CustomerSupportAddress>();
    public DbSet<CostLedgerEntry> CostLedgerEntries => Set<CostLedgerEntry>();
    public DbSet<CostLedgerCoverage> CostLedgerCoverages => Set<CostLedgerCoverage>();
    public DbSet<CostLedgerCursor> CostLedgerCursors => Set<CostLedgerCursor>();
    public DbSet<ExternalGroupMapping> ExternalGroupMappings => Set<ExternalGroupMapping>();
    public DbSet<RolloutPolicy> RolloutPolicies => Set<RolloutPolicy>();
    public DbSet<DeploymentRollout> DeploymentRollouts => Set<DeploymentRollout>();
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
    public DbSet<DeploymentAppliedResource> DeploymentAppliedResources => Set<DeploymentAppliedResource>();
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
    public DbSet<AdvisorFindingState> AdvisorFindingStates => Set<AdvisorFindingState>();
    public DbSet<ResourceUsageSnapshot> ResourceUsageSnapshots => Set<ResourceUsageSnapshot>();
    public DbSet<AdvisorDigestConfig> AdvisorDigestConfigs => Set<AdvisorDigestConfig>();
    public DbSet<ExternalRouteHealthHistory> ExternalRouteHealthHistories => Set<ExternalRouteHealthHistory>();
    public DbSet<VpnTunnel> VpnTunnels => Set<VpnTunnel>();
    public DbSet<VpnLocalEndpoint> VpnLocalEndpoints => Set<VpnLocalEndpoint>();
    public DbSet<VpnRemoteEndpoint> VpnRemoteEndpoints => Set<VpnRemoteEndpoint>();
    public DbSet<RedisCluster> RedisClusters => Set<RedisCluster>();
    public DbSet<CacheBinding> CacheBindings => Set<CacheBinding>();
    public DbSet<KafkaCluster> KafkaClusters => Set<KafkaCluster>();
    public DbSet<KafkaTopic> KafkaTopics => Set<KafkaTopic>();
    public DbSet<KafkaUser> KafkaUsers => Set<KafkaUser>();
    public DbSet<KafkaBinding> KafkaBindings => Set<KafkaBinding>();
    public DbSet<ElasticsearchCluster> ElasticsearchClusters => Set<ElasticsearchCluster>();
    public DbSet<ElasticsearchIlmPolicy> ElasticsearchIlmPolicies => Set<ElasticsearchIlmPolicy>();
    public DbSet<ElasticsearchUser> ElasticsearchUsers => Set<ElasticsearchUser>();
    public DbSet<ElasticsearchBinding> ElasticsearchBindings => Set<ElasticsearchBinding>();
    public DbSet<ElasticsearchKibanaSpace> ElasticsearchKibanaSpaces => Set<ElasticsearchKibanaSpace>();
    public DbSet<ElasticsearchRemoteLink> ElasticsearchRemoteLinks => Set<ElasticsearchRemoteLink>();
    public DbSet<ElasticsearchIngestPipeline> ElasticsearchIngestPipelines => Set<ElasticsearchIngestPipeline>();
    public DbSet<ElasticsearchDataView> ElasticsearchDataViews => Set<ElasticsearchDataView>();
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
    public DbSet<AppL4Route> AppL4Routes => Set<AppL4Route>();
    public DbSet<AppAllowedDatabase> AppAllowedDatabases => Set<AppAllowedDatabase>();
    public DbSet<AppAllowedCache> AppAllowedCaches => Set<AppAllowedCache>();
    public DbSet<AppAllowedStorage> AppAllowedStorages => Set<AppAllowedStorage>();
    public DbSet<AppServicePort> AppServicePorts => Set<AppServicePort>();
    public DbSet<ConnectivityRule> ConnectivityRules => Set<ConnectivityRule>();
    public DbSet<ExternalDependency> ExternalDependencies => Set<ExternalDependency>();
    public DbSet<KyvernoPolicy> KyvernoPolicies => Set<KyvernoPolicy>();
    public DbSet<KedaScaler> KedaScalers => Set<KedaScaler>();
    public DbSet<OnCallSchedule> OnCallSchedules => Set<OnCallSchedule>();
    public DbSet<OnCallShift> OnCallShifts => Set<OnCallShift>();
    public DbSet<AlertRoutingRule> AlertRoutingRules => Set<AlertRoutingRule>();
    public DbSet<TelemetryAlertRule> TelemetryAlertRules => Set<TelemetryAlertRule>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<RumSite> RumSites => Set<RumSite>();
    public DbSet<TelemetrySegment> TelemetrySegments => Set<TelemetrySegment>();
    public DbSet<TelemetryStorageSetting> TelemetryStorageSettings => Set<TelemetryStorageSetting>();
    public DbSet<NotificationProviderConfig> NotificationProviderConfigs => Set<NotificationProviderConfig>();

    public DbSet<SupportDuty> SupportDuties => Set<SupportDuty>();
    public DbSet<SecretExpiryNotificationConfig> SecretExpiryNotificationConfigs => Set<SecretExpiryNotificationConfig>();
    public DbSet<SecretExpiryNotification> SecretExpiryNotifications => Set<SecretExpiryNotification>();
    public DbSet<ClusterServer> ClusterServers => Set<ClusterServer>();
    public DbSet<IdentityBinding> IdentityBindings => Set<IdentityBinding>();
    public DbSet<ClusterBlueprint> ClusterBlueprints => Set<ClusterBlueprint>();
    public DbSet<BlueprintStep> BlueprintSteps => Set<BlueprintStep>();
    public DbSet<BootstrapRun> BootstrapRuns => Set<BootstrapRun>();
    public DbSet<BootstrapStepRun> BootstrapStepRuns => Set<BootstrapStepRun>();
    public DbSet<BlueprintRollout> BlueprintRollouts => Set<BlueprintRollout>();
    public DbSet<BlueprintRolloutTarget> BlueprintRolloutTargets => Set<BlueprintRolloutTarget>();
    public DbSet<BlueprintVariable> BlueprintVariables => Set<BlueprintVariable>();
    public DbSet<BlueprintVariableValue> BlueprintVariableValues => Set<BlueprintVariableValue>();
    public DbSet<MeshMtlsPolicy> MeshMtlsPolicies => Set<MeshMtlsPolicy>();
    public DbSet<OutboundMtlsCredential> OutboundMtlsCredentials => Set<OutboundMtlsCredential>();
    public DbSet<ClientCaBundle> ClientCaBundles => Set<ClientCaBundle>();
    public DbSet<ClientCaCertificate> ClientCaCertificates => Set<ClientCaCertificate>();
    public DbSet<CaTrustBundle> CaTrustBundles => Set<CaTrustBundle>();
    public DbSet<CaTrustBundleSource> CaTrustBundleSources => Set<CaTrustBundleSource>();
    public DbSet<CertificateDistribution> CertificateDistributions => Set<CertificateDistribution>();
    public DbSet<OpenLdapComponentConfig> OpenLdapComponentConfigs => Set<OpenLdapComponentConfig>();
    public DbSet<OpenLdapOrganizationalUnit> OpenLdapOrganizationalUnits => Set<OpenLdapOrganizationalUnit>();
    public DbSet<OpenLdapUser> OpenLdapUsers => Set<OpenLdapUser>();
    public DbSet<OpenLdapGroup> OpenLdapGroups => Set<OpenLdapGroup>();
    public DbSet<OpenLdapGroupMember> OpenLdapGroupMembers => Set<OpenLdapGroupMember>();
    public DbSet<StalwartComponentConfig> StalwartComponentConfigs => Set<StalwartComponentConfig>();
    public DbSet<StalwartMailDomain> StalwartMailDomains => Set<StalwartMailDomain>();
    public DbSet<StalwartMailAccount> StalwartMailAccounts => Set<StalwartMailAccount>();

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

        // The model itself lives in one file per module; see EntKubeModel. This context
        // exposes all of it, because it is still what migrations are generated from and
        // what services that have not yet moved to a module context use.
        Modules.EntKubeModel.ConfigureAll(builder);
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
