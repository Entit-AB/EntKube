using EntKube.Web.Data;

namespace EntKube.Web.Modules;

/// <summary>
/// Which module owns which table. One entry per entity in the model — no defaults, no
/// fallback module, because a silent default is how an unowned table drifts.
///
/// <para><b>This is the source of truth for the decomposition.</b> `ModuleBoundaryTests`
/// holds it to the EF model in both directions: an entity missing here fails the build, and
/// so does an entry naming a type the model no longer has. Adding a table therefore forces
/// the question "whose is this?" at the moment it is cheapest to answer.</para>
///
/// <para><b>Owning a table is not the same as being the only one to read it.</b> Plenty of
/// cross-module references exist today and some are permanent — every tenant-owned table
/// points at <see cref="Tenant"/>, which Identity owns. The test measures those edges and
/// ratchets them rather than pretending they are zero.</para>
/// </summary>
public static partial class ModuleMap
{
    /// <summary>Every entity in the model, and the module that owns it.</summary>
    public static readonly IReadOnlyDictionary<Type, Module> Entities = new Dictionary<Type, Module>
    {
        // ---- Identity -----------------------------------------------------------------
        [typeof(ApplicationUser)] = Module.Identity,
        [typeof(Tenant)] = Module.Identity,
        [typeof(TenantMembership)] = Module.Identity,
        [typeof(TenantRole)] = Module.Identity,
        [typeof(Customer)] = Module.Identity,
        [typeof(CustomerAccess)] = Module.Identity,
        [typeof(CustomerEmailDomain)] = Module.Identity,
        [typeof(Group)] = Module.Identity,
        [typeof(GroupMembership)] = Module.Identity,
        [typeof(ExternalGroupMapping)] = Module.Identity,
        [typeof(ApiToken)] = Module.Identity,
        [typeof(JitGrant)] = Module.Identity,
        [typeof(AuditEvent)] = Module.Identity,
        [typeof(KeycloakRealm)] = Module.Identity,
        [typeof(KeycloakTheme)] = Module.Identity,
        [typeof(KeycloakBackup)] = Module.Identity,
        [typeof(KeycloakComponentConfig)] = Module.Identity,

        // ---- Fleet --------------------------------------------------------------------
        [typeof(KubernetesCluster)] = Module.Fleet,
        [typeof(ClusterServer)] = Module.Fleet,
        [typeof(OpenStackConnection)] = Module.Fleet,
        [typeof(EgressAgent)] = Module.Fleet,
        [typeof(MaintenanceWindow)] = Module.Fleet,
        [typeof(ClusterBlueprint)] = Module.Fleet,
        [typeof(BlueprintStep)] = Module.Fleet,
        [typeof(BlueprintVariable)] = Module.Fleet,
        [typeof(BlueprintVariableValue)] = Module.Fleet,
        [typeof(BlueprintRollout)] = Module.Fleet,
        [typeof(BlueprintRolloutTarget)] = Module.Fleet,
        [typeof(BootstrapRun)] = Module.Fleet,
        [typeof(BootstrapStepRun)] = Module.Fleet,

        // ---- Catalog ------------------------------------------------------------------
        [typeof(ClusterComponent)] = Module.Catalog,
        [typeof(EndOfLifeNotice)] = Module.Catalog,
        [typeof(CaTrustBundle)] = Module.Catalog,
        [typeof(CaTrustBundleSource)] = Module.Catalog,
        [typeof(CertificateDistribution)] = Module.Catalog,

        // ---- DataServices -------------------------------------------------------------
        [typeof(CnpgCluster)] = Module.DataServices,
        [typeof(CnpgDatabase)] = Module.DataServices,
        [typeof(CnpgBackup)] = Module.DataServices,
        [typeof(MongoCluster)] = Module.DataServices,
        [typeof(MongoDatabase)] = Module.DataServices,
        [typeof(MongoBackup)] = Module.DataServices,
        [typeof(RedisCluster)] = Module.DataServices,
        [typeof(KafkaCluster)] = Module.DataServices,
        [typeof(KafkaTopic)] = Module.DataServices,
        [typeof(KafkaUser)] = Module.DataServices,
        [typeof(RabbitMQCluster)] = Module.DataServices,
        [typeof(RabbitMQBackup)] = Module.DataServices,
        [typeof(ElasticsearchCluster)] = Module.DataServices,
        [typeof(ElasticsearchUser)] = Module.DataServices,
        [typeof(ElasticsearchDataView)] = Module.DataServices,
        [typeof(ElasticsearchIlmPolicy)] = Module.DataServices,
        [typeof(ElasticsearchIngestPipeline)] = Module.DataServices,
        [typeof(ElasticsearchKibanaSpace)] = Module.DataServices,
        [typeof(ElasticsearchRemoteLink)] = Module.DataServices,
        [typeof(OpenLdapComponentConfig)] = Module.DataServices,
        [typeof(OpenLdapUser)] = Module.DataServices,
        [typeof(OpenLdapGroup)] = Module.DataServices,
        [typeof(OpenLdapGroupMember)] = Module.DataServices,
        [typeof(OpenLdapOrganizationalUnit)] = Module.DataServices,
        [typeof(HarborComponentConfig)] = Module.DataServices,
        [typeof(HarborProject)] = Module.DataServices,
        [typeof(RegisteredPostgresInstance)] = Module.DataServices,
        [typeof(RegisteredPostgresDatabase)] = Module.DataServices,
        [typeof(RegisteredPostgresDump)] = Module.DataServices,
        [typeof(StorageLink)] = Module.DataServices,
        [typeof(DockerRegistryCredential)] = Module.DataServices,

        // ---- Mail ---------------------------------------------------------------------
        [typeof(StalwartComponentConfig)] = Module.Mail,
        [typeof(StalwartMailDomain)] = Module.Mail,
        [typeof(StalwartMailAccount)] = Module.Mail,

        // ---- Delivery -----------------------------------------------------------------
        [typeof(App)] = Module.Delivery,
        [typeof(AppEnvironment)] = Module.Delivery,
        [typeof(Data.Environment)] = Module.Delivery,
        [typeof(CustomerEnvironment)] = Module.Delivery,
        [typeof(AppDeployment)] = Module.Delivery,
        [typeof(AppDeploymentRoute)] = Module.Delivery,
        [typeof(AppQuota)] = Module.Delivery,
        [typeof(AppRbacPolicy)] = Module.Delivery,
        [typeof(AppRbacRule)] = Module.Delivery,
        [typeof(AppAllowedCache)] = Module.Delivery,
        [typeof(AppAllowedDatabase)] = Module.Delivery,
        [typeof(AppAllowedStorage)] = Module.Delivery,
        [typeof(AppServiceDependency)] = Module.Delivery,
        [typeof(AppServicePort)] = Module.Delivery,
        [typeof(ExternalDependency)] = Module.Delivery,
        [typeof(DeploymentManifest)] = Module.Delivery,
        [typeof(DeploymentResource)] = Module.Delivery,
        [typeof(DeploymentAppliedResource)] = Module.Delivery,
        [typeof(DeploymentHealthSnapshot)] = Module.Delivery,
        [typeof(DeploymentRollout)] = Module.Delivery,
        [typeof(RolloutPolicy)] = Module.Delivery,
        [typeof(KedaScaler)] = Module.Delivery,
        [typeof(KyvernoPolicy)] = Module.Delivery,
        [typeof(GitRepository)] = Module.Delivery,
        [typeof(GitKnownHost)] = Module.Delivery,
        [typeof(CustomerGitCredential)] = Module.Delivery,
        [typeof(CustomerGitRepoPolicy)] = Module.Delivery,
        [typeof(CacheBinding)] = Module.Delivery,
        [typeof(DatabaseBinding)] = Module.Delivery,
        [typeof(ElasticsearchBinding)] = Module.Delivery,
        [typeof(KafkaBinding)] = Module.Delivery,
        [typeof(MessagingBinding)] = Module.Delivery,
        [typeof(StorageBinding)] = Module.Delivery,
        [typeof(IdentityBinding)] = Module.Delivery,

        // ---- Connectivity -------------------------------------------------------------
        [typeof(AppRoute)] = Module.Connectivity,
        [typeof(AppL4Route)] = Module.Connectivity,
        [typeof(AppNetworkPolicy)] = Module.Connectivity,
        [typeof(ConnectivityRule)] = Module.Connectivity,
        [typeof(ExternalRoute)] = Module.Connectivity,
        [typeof(ExternalRouteHealthHistory)] = Module.Connectivity,
        [typeof(MeshMtlsPolicy)] = Module.Connectivity,
        [typeof(OutboundMtlsCredential)] = Module.Connectivity,
        [typeof(ClientCaBundle)] = Module.Connectivity,
        [typeof(ClientCaCertificate)] = Module.Connectivity,
        [typeof(VpnTunnel)] = Module.Connectivity,
        [typeof(VpnLocalEndpoint)] = Module.Connectivity,
        [typeof(VpnRemoteEndpoint)] = Module.Connectivity,

        // ---- Secrets ------------------------------------------------------------------
        [typeof(SecretVault)] = Module.Secrets,
        [typeof(VaultSecret)] = Module.Secrets,
        [typeof(VaultSecretVersion)] = Module.Secrets,
        [typeof(SecretExpiryNotification)] = Module.Secrets,
        [typeof(SecretExpiryNotificationConfig)] = Module.Secrets,

        // ---- Telemetry ----------------------------------------------------------------
        [typeof(TelemetryAlertRule)] = Module.Telemetry,
        [typeof(TelemetrySegment)] = Module.Telemetry,
        [typeof(TelemetryStorageSetting)] = Module.Telemetry,
        [typeof(RumSite)] = Module.Telemetry,
        [typeof(Dashboard)] = Module.Telemetry,
        [typeof(AlertIncident)] = Module.Telemetry,
        [typeof(IncidentNote)] = Module.Telemetry,
        [typeof(AlertRoutingRule)] = Module.Telemetry,
        [typeof(NotificationChannel)] = Module.Telemetry,
        [typeof(NotificationDelivery)] = Module.Telemetry,
        [typeof(NotificationProviderConfig)] = Module.Telemetry,

        // ---- Cost ---------------------------------------------------------------------
        [typeof(ClusterCostRate)] = Module.Cost,
        [typeof(CostLedgerEntry)] = Module.Cost,
        [typeof(CostLedgerCursor)] = Module.Cost,
        [typeof(CostLedgerCoverage)] = Module.Cost,
        [typeof(PriceList)] = Module.Cost,
        [typeof(PriceListEntry)] = Module.Cost,
        [typeof(ResourceUsageSnapshot)] = Module.Cost,

        // ---- Support ------------------------------------------------------------------
        [typeof(Ticket)] = Module.Support,
        [typeof(TicketEvent)] = Module.Support,
        [typeof(TicketPause)] = Module.Support,
        [typeof(TicketAffectedApp)] = Module.Support,
        [typeof(TicketBridgeConnection)] = Module.Support,
        [typeof(ExternalTicketLink)] = Module.Support,
        [typeof(SupportMailbox)] = Module.Support,
        [typeof(SupportDuty)] = Module.Support,
        [typeof(CustomerSupportAddress)] = Module.Support,
        [typeof(InboundMailMessage)] = Module.Support,
        [typeof(MailTriageRule)] = Module.Support,
        [typeof(MailSuggestion)] = Module.Support,
        [typeof(TimeEntry)] = Module.Support,
        [typeof(WorkAuthorisation)] = Module.Support,
        [typeof(ApplicationContract)] = Module.Support,
        [typeof(ApplicationServiceLevel)] = Module.Support,
        [typeof(ContractContact)] = Module.Support,
        [typeof(PortfolioAgreement)] = Module.Support,
        [typeof(SlaTarget)] = Module.Support,
        [typeof(Subconsultant)] = Module.Support,
        [typeof(OnCallSchedule)] = Module.Support,
        [typeof(OnCallShift)] = Module.Support,
        [typeof(KnowledgeSection)] = Module.Support,
        [typeof(KnowledgeRevision)] = Module.Support,
        [typeof(AppKnowledgeProfile)] = Module.Support,

        // ---- Advisor ------------------------------------------------------------------
        [typeof(AdvisorDigestConfig)] = Module.Advisor,
        [typeof(AdvisorFindingState)] = Module.Advisor,
    };

    /// <summary>The module owning <paramref name="entity"/>, or null when it is unassigned.</summary>
    public static Module? Owner(Type entity) => Entities.TryGetValue(entity, out Module m) ? m : null;
}
