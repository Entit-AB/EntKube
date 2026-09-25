namespace EntKube.Web.Data.Backup;

public class BackupBundle
{
    // Version 8 added the inbound ticket bridge: the customers' own ticketing systems and
    // what each of our tickets mirrors there.
    // Version 7 added the envelope recipients kept beside the claimed ones.
    // Version 6 added each customer's own support addresses.
    // Version 5 added the customer mail-domain register.
    // Version 4 added the platform configuration a coverage test found missing: API
    // tokens, egress agents, cost rates, SSO group mappings, rollout policies, the
    // client CAs and mesh mTLS policy, the Stalwart mail configuration, and the
    // backup rows that index an object in storage.
    // Version 3 added application management and support: the agreement's annexes, the
    // ticket store and its clocks, worked time, the knowledge base, and the support
    // mailbox with its triage rules.
    // Version 2 added the full set of configuration entities (routing, connectivity,
    // Kafka, governance, blueprints, CA trust, observability config, secret history, …).
    // Earlier bundles are still accepted on import — their missing lists deserialize
    // to empty collections.
    /// <summary>
    /// The version this build writes. Import accepts anything up to it and refuses
    /// anything beyond — a newer bundle may carry tables this build cannot place, and
    /// restoring half of one is worse than refusing it.
    ///
    /// <para>Bumping this is the <em>only</em> edit a new version needs. It used to be a
    /// literal here and a second literal in the import's guard, which is how a bundle
    /// this very code wrote came to be rejected by it.</para>
    /// </summary>
    public const int CurrentVersion = 8;

    public int Version { get; set; } = CurrentVersion;
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

    // What a person decided about an advisor finding — acknowledged, snoozed, assigned,
    // annotated. The findings themselves are recomputed on every read; these are not.
    public List<AdvisorFindingState> AdvisorFindingStates { get; set; } = [];

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

    // Application management and support (the förvaltningsavtal's annexes, the work done
    // under them, and the record of how it was reported). Migrating a server without
    // these would lose what was agreed and every SLA timestamp §14.6 makes evidence.
    public List<ApplicationContract> ApplicationContracts { get; set; } = [];
    public List<ApplicationServiceLevel> ApplicationServiceLevels { get; set; } = [];
    public List<PortfolioAgreement> PortfolioAgreements { get; set; } = [];
    public List<ContractContact> ContractContacts { get; set; } = [];
    public List<PriceList> PriceLists { get; set; } = [];
    public List<PriceListEntry> PriceListEntries { get; set; } = [];
    public List<Subconsultant> Subconsultants { get; set; } = [];

    public List<Ticket> Tickets { get; set; } = [];
    public List<TicketEvent> TicketEvents { get; set; } = [];
    public List<TicketPause> TicketPauses { get; set; } = [];
    public List<TicketAffectedApp> TicketAffectedApps { get; set; } = [];
    public List<TimeEntry> TimeEntries { get; set; } = [];
    public List<WorkAuthorisation> WorkAuthorisations { get; set; } = [];

    public List<AppKnowledgeProfile> AppKnowledgeProfiles { get; set; } = [];
    public List<KnowledgeSection> KnowledgeSections { get; set; } = [];
    public List<KnowledgeRevision> KnowledgeRevisions { get; set; } = [];
    public List<AppServiceDependency> AppServiceDependencies { get; set; } = [];
    public List<EndOfLifeNotice> EndOfLifeNotices { get; set; } = [];

    // The mailbox's own IMAP password rides along in VaultSecrets, like every other
    // credential; this is only the connection settings.
    public List<SupportMailbox> SupportMailboxes { get; set; } = [];
    // Which domains belong to which customer. Losing it does not lose a message, but every
    // sender who is not individually named stops being recognised — support mail quietly
    // starts arriving unplaced and somebody has to work out why.
    public List<CustomerEmailDomain> CustomerEmailDomains { get; set; } = [];
    public List<CustomerSupportAddress> CustomerSupportAddresses { get; set; } = [];
    public List<MailTriageRule> MailTriageRules { get; set; } = [];
    public List<InboundMailMessage> InboundMailMessages { get; set; } = [];
    public List<MailSuggestion> MailSuggestions { get; set; } = [];

    // The customers' own ticketing systems. The connection's shared secret rides along as
    // its hash, which is all there ever was — a restored connection therefore keeps working
    // without anybody having to reissue credentials into somebody else's estate.
    public List<TicketBridgeConnection> TicketBridgeConnections { get; set; } = [];
    // What each ticket mirrors in the customer's system. Losing it would not lose a ticket,
    // but every future delivery about one would open a second — the link is the only thing
    // that recognises a resend.
    public List<ExternalTicketLink> ExternalTicketLinks { get; set; } = [];

    // Platform configuration that nothing live recreates. Added when a coverage test was
    // written and found them missing; see BackupCoverageTests for what is still not here.
    public List<ApiToken> ApiTokens { get; set; } = [];
    public List<EgressAgent> EgressAgents { get; set; } = [];
    public List<ClusterCostRate> ClusterCostRates { get; set; } = [];
    public List<ExternalGroupMapping> ExternalGroupMappings { get; set; } = [];
    public List<RolloutPolicy> RolloutPolicies { get; set; } = [];

    // Client certificate authorities and mesh policy. An mTLS setup that has to be
    // rebuilt by hand is one where every partner's client certificate stops working.
    public List<ClientCaBundle> ClientCaBundles { get; set; } = [];
    public List<ClientCaCertificate> ClientCaCertificates { get; set; } = [];
    public List<MeshMtlsPolicy> MeshMtlsPolicies { get; set; } = [];
    public List<OutboundMtlsCredential> OutboundMtlsCredentials { get; set; } = [];

    // The Stalwart mail stack's declarative configuration — domains and accounts are
    // authored here and replayed onto the server, so this is the only copy.
    public List<StalwartComponentConfig> StalwartComponentConfigs { get; set; } = [];
    public List<StalwartMailDomain> StalwartMailDomains { get; set; } = [];
    public List<StalwartMailAccount> StalwartMailAccounts { get; set; } = [];

    // Backups whose row is the only index of an object in storage. The file survives a
    // migration either way; without the row, nothing knows it is there.
    public List<RegisteredPostgresDump> RegisteredPostgresDumps { get; set; } = [];
    public List<RabbitMQBackup> RabbitMQBackups { get; set; } = [];
    public List<KeycloakBackup> KeycloakBackups { get; set; } = [];

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
    // These four were missing, so a restore silently unhooked every secret that used
    // them: a cluster's kubeconfig, a Kafka cluster's credentials, an app secret's
    // environment scoping, and the support mailbox's password. Added late, hence the
    // position — the record is positional and the earlier fields cannot move.
    Guid? KafkaClusterId,
    Guid? OwnerClusterId,
    Guid? EnvironmentId,
    Guid? SupportMailboxId,
    VaultSecretType SecretType,
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
