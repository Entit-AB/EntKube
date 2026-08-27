namespace EntKube.Web.Data.Backup;

public class BackupBundle
{
    // Version 2 added the full set of configuration entities (routing, connectivity,
    // Kafka, governance, blueprints, CA trust, observability config, secret history, …).
    // Version 1 bundles are still accepted on import — their missing lists deserialize
    // to empty collections.
    public int Version { get; set; } = 2;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";

    // Identity
    public List<UserRecord> Users { get; set; } = [];
    public List<RoleRecord> Roles { get; set; } = [];
    public List<UserRoleRecord> UserRoles { get; set; } = [];

    // Tenant structure
    public List<Tenant> Tenants { get; set; } = [];
    public List<TenantRole> TenantRoles { get; set; } = [];
    public List<TenantMembership> TenantMemberships { get; set; } = [];
    public List<Group> Groups { get; set; } = [];
    public List<GroupMembership> GroupMemberships { get; set; } = [];
    public List<Environment> Environments { get; set; } = [];
    public List<Customer> Customers { get; set; } = [];
    public List<CustomerEnvironment> CustomerEnvironments { get; set; } = [];
    public List<CustomerAccess> CustomerAccesses { get; set; } = [];
    public List<App> Apps { get; set; } = [];
    public List<AppEnvironment> AppEnvironments { get; set; } = [];

    // Infrastructure
    public List<KubernetesCluster> KubernetesClusters { get; set; } = [];
    public List<ClusterComponent> ClusterComponents { get; set; } = [];
    public List<ExternalRoute> ExternalRoutes { get; set; } = [];
    public List<OpenStackConnection> OpenStackConnections { get; set; } = [];
    public List<StorageLink> StorageLinks { get; set; } = [];

    // App governance
    public List<AppNetworkPolicy> AppNetworkPolicies { get; set; } = [];
    public List<AppQuota> AppQuotas { get; set; } = [];
    public List<AppRbacPolicy> AppRbacPolicies { get; set; } = [];
    public List<AppRbacRule> AppRbacRules { get; set; } = [];
    public List<KyvernoPolicy> KyvernoPolicies { get; set; } = [];
    public List<KedaScaler> KedaScalers { get; set; } = [];

    // App routing & connectivity (least-privilege graph + ingress/L4 routes)
    public List<AppRoute> AppRoutes { get; set; } = [];
    public List<AppDeploymentRoute> AppDeploymentRoutes { get; set; } = [];
    public List<AppL4Route> AppL4Routes { get; set; } = [];
    public List<AppServicePort> AppServicePorts { get; set; } = [];
    public List<ConnectivityRule> ConnectivityRules { get; set; } = [];
    public List<ExternalDependency> ExternalDependencies { get; set; } = [];
    public List<AppAllowedDatabase> AppAllowedDatabases { get; set; } = [];
    public List<AppAllowedCache> AppAllowedCaches { get; set; } = [];
    public List<AppAllowedStorage> AppAllowedStorages { get; set; } = [];

    // Deployments
    public List<AppDeployment> AppDeployments { get; set; } = [];
    public List<DeploymentManifest> DeploymentManifests { get; set; } = [];
    public List<StorageBinding> StorageBindings { get; set; } = [];

    // Databases
    public List<CnpgCluster> CnpgClusters { get; set; } = [];
    public List<CnpgDatabase> CnpgDatabases { get; set; } = [];
    public List<MongoCluster> MongoClusters { get; set; } = [];
    public List<MongoDatabase> MongoDatabases { get; set; } = [];
    public List<RabbitMQCluster> RabbitMQClusters { get; set; } = [];
    public List<RegisteredPostgresInstance> RegisteredPostgresInstances { get; set; } = [];
    public List<RegisteredPostgresDatabase> RegisteredPostgresDatabases { get; set; } = [];
    public List<DatabaseBinding> DatabaseBindings { get; set; } = [];
    public List<MessagingBinding> MessagingBindings { get; set; } = [];

    // Git sync
    public List<GitRepository> GitRepositories { get; set; } = [];
    public List<GitKnownHost> GitKnownHosts { get; set; } = [];

    // Customer git credentials & policies
    public List<CustomerGitCredential> CustomerGitCredentials { get; set; } = [];
    public List<CustomerGitRepoPolicy> CustomerGitRepoPolicies { get; set; } = [];

    // Cache
    public List<RedisCluster> RedisClusters { get; set; } = [];
    public List<CacheBinding> CacheBindings { get; set; } = [];

    // Streaming (self-hosted Strimzi Kafka)
    public List<KafkaCluster> KafkaClusters { get; set; } = [];
    public List<KafkaTopic> KafkaTopics { get; set; } = [];
    public List<KafkaUser> KafkaUsers { get; set; } = [];
    public List<KafkaBinding> KafkaBindings { get; set; } = [];

    // VPN
    public List<VpnTunnel> VpnTunnels { get; set; } = [];
    public List<VpnLocalEndpoint> VpnLocalEndpoints { get; set; } = [];
    public List<VpnRemoteEndpoint> VpnRemoteEndpoints { get; set; } = [];

    // Identity / Auth management
    public List<KeycloakComponentConfig> KeycloakComponentConfigs { get; set; } = [];
    public List<KeycloakTheme> KeycloakThemes { get; set; } = [];
    public List<KeycloakRealm> KeycloakRealms { get; set; } = [];

    // Container registry
    public List<HarborComponentConfig> HarborComponentConfigs { get; set; } = [];
    public List<HarborProject> HarborProjects { get; set; } = [];

    // Alerting & SLA
    public List<NotificationChannel> NotificationChannels { get; set; } = [];
    public List<SlaTarget> SlaTargets { get; set; } = [];
    public List<MaintenanceWindow> MaintenanceWindows { get; set; } = [];
    public List<AlertRoutingRule> AlertRoutingRules { get; set; } = [];
    public List<OnCallSchedule> OnCallSchedules { get; set; } = [];
    public List<OnCallShift> OnCallShifts { get; set; } = [];

    // Observability config (dashboards, telemetry alert rules, RUM sites, storage target, digests)
    public List<Dashboard> Dashboards { get; set; } = [];
    public List<RumSite> RumSites { get; set; } = [];
    public List<TelemetryAlertRule> TelemetryAlertRules { get; set; } = [];
    public List<TelemetryStorageSetting> TelemetryStorageSettings { get; set; } = [];
    public List<AdvisorDigestConfig> AdvisorDigestConfigs { get; set; } = [];

    // Notification & secret-expiry provider config
    // NotificationProviderConfig is a GLOBAL singleton set (no TenantId) — see wipe handling on restore.
    public List<NotificationProviderConfig> NotificationProviderConfigs { get; set; } = [];
    public List<SecretExpiryNotificationConfig> SecretExpiryNotificationConfigs { get; set; } = [];

    // Server inventory & identity bindings
    public List<ClusterServer> ClusterServers { get; set; } = [];
    public List<IdentityBinding> IdentityBindings { get; set; } = [];

    // Cluster blueprints (ordered recipes + variables)
    public List<ClusterBlueprint> ClusterBlueprints { get; set; } = [];
    public List<BlueprintStep> BlueprintSteps { get; set; } = [];
    public List<BlueprintVariable> BlueprintVariables { get; set; } = [];
    public List<BlueprintVariableValue> BlueprintVariableValues { get; set; } = [];

    // CA & trust management
    public List<CaTrustBundle> CaTrustBundles { get; set; } = [];
    public List<CaTrustBundleSource> CaTrustBundleSources { get; set; } = [];
    public List<CertificateDistribution> CertificateDistributions { get; set; } = [];

    // OpenLDAP directory (config + declaratively-authored entries; PasswordSsha is a hash, not plaintext)
    public List<OpenLdapComponentConfig> OpenLdapComponentConfigs { get; set; } = [];
    public List<OpenLdapOrganizationalUnit> OpenLdapOrganizationalUnits { get; set; } = [];
    public List<OpenLdapUser> OpenLdapUsers { get; set; } = [];
    public List<OpenLdapGroup> OpenLdapGroups { get; set; } = [];
    public List<OpenLdapGroupMember> OpenLdapGroupMembers { get; set; } = [];

    // Secrets — stored as decrypted plaintext in the bundle.
    // On restore, fresh DEKs are generated and secrets are re-encrypted with the
    // new server's root key. The bundle itself is therefore sensitive at rest.
    public List<VaultRecord> SecretVaults { get; set; } = [];
    public List<VaultSecretRecord> VaultSecrets { get; set; } = [];
    public List<VaultSecretVersionRecord> VaultSecretVersions { get; set; } = [];
    public List<DockerCredentialRecord> DockerCredentials { get; set; } = [];
}

public record UserRecord(
    string Id,
    string? UserName,
    string? Email,
    string? PasswordHash,
    bool EmailConfirmed,
    string? NormalizedUserName,
    string? NormalizedEmail,
    string? SecurityStamp,
    string? ConcurrencyStamp,
    string? PhoneNumber,
    bool PhoneNumberConfirmed,
    bool TwoFactorEnabled,
    DateTimeOffset? LockoutEnd,
    bool LockoutEnabled,
    int AccessFailedCount);

public record RoleRecord(string Id, string? Name, string? NormalizedName, string? ConcurrencyStamp);

public record UserRoleRecord(string UserId, string RoleId);

public class VaultRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record VaultSecretRecord(
    Guid Id,
    Guid VaultId,
    string Name,
    string PlaintextValue,
    Guid? AppId,
    Guid? ComponentId,
    Guid? StorageLinkId,
    Guid? OpenStackConnectionId,
    Guid? CnpgClusterId,
    Guid? CnpgDatabaseId,
    Guid? MongoDatabaseId,
    Guid? MongoClusterId,
    Guid? RegisteredPostgresDatabaseId,
    Guid? RabbitMQClusterId,
    Guid? RedisClusterId,
    Guid? VpnRemoteEndpointId,
    Guid? GitRepositoryId,
    Guid? CustomerGitCredentialId,
    bool SyncToKubernetes,
    Guid? KubernetesClusterId,
    string? KubernetesSecretName,
    string? KubernetesNamespace,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// A historical secret value. Like VaultSecretRecord, the encrypted blob is decrypted
// to plaintext on export and re-encrypted with the destination vault's fresh DEK on
// restore (the version's original ciphertext is sealed under the source server's key).
public record VaultSecretVersionRecord(
    Guid Id,
    Guid SecretId,
    int VersionNumber,
    string PlaintextValue,
    string? CreatedBy,
    DateTime CreatedAt);

public record DockerCredentialRecord(
    Guid Id,
    Guid VaultId,
    string Name,
    DockerRegistryType RegistryType,
    string Server,
    string Username,
    string PlaintextPassword,
    string? Email,
    Guid? AppId,
    Guid? KubernetesClusterId,
    string? KubernetesSecretName,
    string? KubernetesNamespace,
    DateTime CreatedAt,
    DateTime UpdatedAt);
