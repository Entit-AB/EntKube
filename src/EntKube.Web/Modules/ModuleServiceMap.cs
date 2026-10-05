using EntKube.Web.Authorization;
using EntKube.Web.Client.Pages;
using EntKube.Web.Components.Account;
using EntKube.Web.Components;
using EntKube.Web.Data.Modules;
using EntKube.Web.Data;
using EntKube.Web.Modules.Composition;
using EntKube.Web.Services.Agents;
using EntKube.Web.Services.Telemetry;
using EntKube.Web.Services.Tickets.Bridge;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace EntKube.Web.Modules;

/// <summary>
/// Which module owns which service, the counterpart to <see cref="ModuleMap.Entities"/>.
///
/// <para>Keyed on the <em>implementation</em> type, because that is the thing a module owns;
/// an interface may well be the contract another module is allowed to depend on later.</para>
///
/// <para><c>ModuleCompositionTests</c> holds this to the services actually registered, so a
/// new service has to be given an owner — or named as platform wiring — before it builds.</para>
/// </summary>
public static partial class ModuleMap
{
    /// <summary>Every module-owned service, and the module that owns it.</summary>
    public static readonly IReadOnlyDictionary<Type, Module> Services = new Dictionary<Type, Module>
    {
        // ---- Identity --------------------------------------------------------------
        [typeof(EntKube.Web.Services.PublicApi.ApiTokenService)] = Module.Identity,
        [typeof(AuditService)] = Module.Identity,
        [typeof(CustomerAccessService)] = Module.Identity,
        [typeof(EntKube.Web.Services.Sso.ExternalGroupSync)] = Module.Identity,
        [typeof(EntKube.Web.Services.Jit.JitAccessService)] = Module.Identity,
        [typeof(EntKube.Web.Services.Jit.JitGrantReaperService)] = Module.Identity,
        [typeof(EntKube.Web.Services.Jit.JitProxyService)] = Module.Identity,
        [typeof(EntKube.Web.Services.Jit.JitUpstreamClientPool)] = Module.Identity,
        [typeof(KeycloakBackupSchedulerService)] = Module.Identity,
        [typeof(KeycloakService)] = Module.Identity,
        [typeof(EntKube.Web.Services.Scim.ScimUserService)] = Module.Identity,
        [typeof(TenantRoleService)] = Module.Identity,
        [typeof(TenantService)] = Module.Identity,
        [typeof(UserAccessService)] = Module.Identity,
        [typeof(UserManagementService)] = Module.Identity,

        // ---- Fleet -----------------------------------------------------------------
        [typeof(AgentRegistry)] = Module.Fleet,
        [typeof(BlueprintFromClusterService)] = Module.Fleet,
        [typeof(BootstrapRunnerService)] = Module.Fleet,
        [typeof(ClusterBlueprintService)] = Module.Fleet,
        [typeof(EntKube.Web.Services.ClusterChanges.ClusterChangeGate)] = Module.Fleet,
        [typeof(ClusterEgressRelay)] = Module.Fleet,
        [typeof(ClusterEgressTunnel)] = Module.Fleet,
        [typeof(ClusterProvisioningService)] = Module.Fleet,
        [typeof(ClusterTenantResolver)] = Module.Fleet,
        [typeof(EntKube.Web.Services.Dr.DrScanCache)] = Module.Fleet,
        [typeof(EntKube.Web.Services.Dr.DrScanService)] = Module.Fleet,
        [typeof(KubernetesClientFactory)] = Module.Fleet,
        [typeof(KubernetesOperationsService)] = Module.Fleet,
        [typeof(KubernetesProxyClientPool)] = Module.Fleet,
        [typeof(NodeManagementService)] = Module.Fleet,
        [typeof(OpenStackComputeService)] = Module.Fleet,
        [typeof(OpenStackHttpFactory)] = Module.Fleet,
        [typeof(OpenStackInventoryService)] = Module.Fleet,
        [typeof(OpenStackKeystoneClient)] = Module.Fleet,
        [typeof(RemediationService)] = Module.Fleet,
        [typeof(EntKube.Web.Services.Dr.VeleroService)] = Module.Fleet,
        [typeof(WorkloadService)] = Module.Fleet,

        // ---- Catalog ---------------------------------------------------------------
        [typeof(CatalogComponentRegistrar)] = Module.Catalog,
        [typeof(CertificateDistributionReconcileService)] = Module.Catalog,
        [typeof(CertificateDistributionService)] = Module.Catalog,
        [typeof(ComponentInstallOrchestrator)] = Module.Catalog,
        [typeof(ComponentLifecycleService)] = Module.Catalog,
        [typeof(ComponentScanService)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.ComponentUpgradeRunner)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.ComponentUpgradeService)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Adoption.DriftAdoptionService)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.DriftDetectionService)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.DriftScanCache)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.DriftScanService)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.HelmRepoIndexClient)] = Module.Catalog,
        [typeof(EntKube.Web.Services.Upgrades.ReleaseVolumeGuard)] = Module.Catalog,
        [typeof(EntKube.Web.Services.SupplyChain.SupplyChainScanCache)] = Module.Catalog,
        [typeof(EntKube.Web.Services.SupplyChain.SupplyChainScanService)] = Module.Catalog,
        [typeof(EntKube.Web.Services.SupplyChain.SupplyChainService)] = Module.Catalog,
        [typeof(TrustBundleService)] = Module.Catalog,

        // ---- DataServices ----------------------------------------------------------
        [typeof(CnpgService)] = Module.DataServices,
        [typeof(DatabaseService)] = Module.DataServices,
        [typeof(DockerRegistryService)] = Module.DataServices,
        [typeof(ElasticsearchService)] = Module.DataServices,
        [typeof(HarborService)] = Module.DataServices,
        [typeof(KafkaService)] = Module.DataServices,
        [typeof(MessagingStatusPollingService)] = Module.DataServices,
        [typeof(MongoService)] = Module.DataServices,
        [typeof(OpenLdapService)] = Module.DataServices,
        [typeof(OpenStackS3Service)] = Module.DataServices,
        [typeof(RabbitMQService)] = Module.DataServices,
        [typeof(RedisService)] = Module.DataServices,
        [typeof(RegisteredPostgresService)] = Module.DataServices,
        [typeof(SearchStatusPollingService)] = Module.DataServices,
        [typeof(StorageBrowserService)] = Module.DataServices,
        [typeof(StorageLinkClientFactory)] = Module.DataServices,
        [typeof(StorageService)] = Module.DataServices,

        // ---- Mail ------------------------------------------------------------------
        [typeof(EntKube.Web.Services.Mail.MailDnsCheck)] = Module.Mail,
        [typeof(EntKube.Web.Services.Mail.StalwartDnsService)] = Module.Mail,
        [typeof(StalwartService)] = Module.Mail,

        // ---- Delivery --------------------------------------------------------------
        [typeof(AppGovernanceService)] = Module.Delivery,
        [typeof(AppOfAppsService)] = Module.Delivery,
        [typeof(CustomerGitService)] = Module.Delivery,
        [typeof(DeploymentImportService)] = Module.Delivery,
        [typeof(DeploymentService)] = Module.Delivery,
        [typeof(DeploymentStatusNotifier)] = Module.Delivery,
        [typeof(DeploymentSyncService)] = Module.Delivery,
        [typeof(GitOperationsService)] = Module.Delivery,
        [typeof(GitRepositoryService)] = Module.Delivery,
        [typeof(GitSyncService)] = Module.Delivery,
        [typeof(GitWebhookService)] = Module.Delivery,
        [typeof(KedaScalerService)] = Module.Delivery,
        [typeof(KyvernoPolicyService)] = Module.Delivery,
        [typeof(PortalServiceScopeService)] = Module.Delivery,
        [typeof(EntKube.Web.Services.Rollouts.RolloutService)] = Module.Delivery,
        [typeof(EntKube.Web.Services.Rollouts.RolloutWatcherService)] = Module.Delivery,

        // ---- Connectivity ----------------------------------------------------------
        [typeof(AppL4RouteHealthService)] = Module.Connectivity,
        [typeof(AppL4RouteService)] = Module.Connectivity,
        [typeof(AppRouteService)] = Module.Connectivity,
        [typeof(ConnectivityGraphService)] = Module.Connectivity,
        [typeof(ExternalRouteHealthService)] = Module.Connectivity,
        [typeof(ExternalRouteService)] = Module.Connectivity,
        [typeof(HeadscaleCertSyncService)] = Module.Connectivity,
        [typeof(HeadscaleService)] = Module.Connectivity,
        [typeof(IngressDashboardService)] = Module.Connectivity,
        [typeof(MeshMtlsService)] = Module.Connectivity,
        [typeof(MtlsService)] = Module.Connectivity,
        [typeof(OutboundMtlsService)] = Module.Connectivity,
        [typeof(TailscaleService)] = Module.Connectivity,
        [typeof(VpnService)] = Module.Connectivity,

        // ---- Secrets ---------------------------------------------------------------
        [typeof(ObservedSecretRefreshService)] = Module.Secrets,
        [typeof(SecretExpiryNotificationService)] = Module.Secrets,
        [typeof(SecretExpiryService)] = Module.Secrets,
        [typeof(VaultService)] = Module.Secrets,

        // ---- Telemetry -------------------------------------------------------------
        [typeof(AlertEscalationService)] = Module.Telemetry,
        [typeof(AlertRoutingService)] = Module.Telemetry,
        [typeof(AlertSyncService)] = Module.Telemetry,
        [typeof(EntKube.Web.Services.Tickets.AlertTicketBridge)] = Module.Telemetry,
        [typeof(ClusterRoutedLogBackend)] = Module.Telemetry,
        [typeof(ClusterRoutedTraceService)] = Module.Telemetry,
        [typeof(CustomerNotificationService)] = Module.Telemetry,
        [typeof(DashboardService)] = Module.Telemetry,
        [typeof(EfSegmentCatalog)] = Module.Telemetry,
        [typeof(EntKubeTelemetryService)] = Module.Telemetry,
        [typeof(ErrorBudgetService)] = Module.Telemetry,
        [typeof(IncidentCorrelationService)] = Module.Telemetry,
        [typeof(IncidentDispatcher)] = Module.Telemetry,
        [typeof(IncidentService)] = Module.Telemetry,
        [typeof(IngestRateLimiter)] = Module.Telemetry,
        [typeof(IngestTokenService)] = Module.Telemetry,
        [typeof(LogQueryService)] = Module.Telemetry,
        [typeof(LokiService)] = Module.Telemetry,
        [typeof(MimirService)] = Module.Telemetry,
        [typeof(NodeLogBackend)] = Module.Telemetry,
        [typeof(NodeTraceService)] = Module.Telemetry,
        [typeof(NotificationProviderConfigService)] = Module.Telemetry,
        [typeof(NotificationService)] = Module.Telemetry,
        [typeof(PromMetricsService)] = Module.Telemetry,
        [typeof(PrometheusService)] = Module.Telemetry,
        [typeof(RumSiteService)] = Module.Telemetry,
        [typeof(SegmentLogService)] = Module.Telemetry,
        [typeof(SegmentRumService)] = Module.Telemetry,
        [typeof(SegmentTelemetryStore)] = Module.Telemetry,
        [typeof(SegmentTraceService)] = Module.Telemetry,
        [typeof(StormSuppressionService)] = Module.Telemetry,
        [typeof(TelemetryAlertEvaluator)] = Module.Telemetry,
        [typeof(TelemetryAlertRuleService)] = Module.Telemetry,
        [typeof(TelemetryNodeClient)] = Module.Telemetry,
        [typeof(TelemetryStorageSettingService)] = Module.Telemetry,
        [typeof(TempoService)] = Module.Telemetry,
        [typeof(TenantBlobStoreFactory)] = Module.Telemetry,
        [typeof(UptimeTrackingService)] = Module.Telemetry,

        // ---- Cost ------------------------------------------------------------------
        [typeof(EntKube.Web.Services.Cost.CostLedgerService)] = Module.Cost,
        [typeof(EntKube.Web.Services.Cost.CostLedgerWriter)] = Module.Cost,
        [typeof(EntKube.Web.Services.Cost.CostRateService)] = Module.Cost,
        [typeof(EntKube.Web.Services.Cost.CostReportService)] = Module.Cost,
        [typeof(EntKube.Web.Services.Cost.CostScanCache)] = Module.Cost,
        [typeof(EntKube.Web.Services.Cost.CostScanService)] = Module.Cost,
        [typeof(ResourceUsageCollectorService)] = Module.Cost,

        // ---- Support ---------------------------------------------------------------
        [typeof(EntKube.Web.Services.Contracts.ContractService)] = Module.Support,
        [typeof(JiraAdapter)] = Module.Support,
        [typeof(EntKube.Web.Services.Knowledge.KnowledgeService)] = Module.Support,
        [typeof(EntKube.Web.Services.Mail.MailTriageRuleService)] = Module.Support,
        [typeof(EntKube.Web.Services.Mail.MailboxTokenProvider)] = Module.Support,
        [typeof(EntKube.Web.Services.Reporting.MonthlyReportService)] = Module.Support,
        [typeof(OnCallService)] = Module.Support,
        [typeof(ServiceNowAdapter)] = Module.Support,
        [typeof(EntKube.Web.Services.Mail.SmtpSettingsResolver)] = Module.Support,
        [typeof(EntKube.Web.Services.Support.SupportDutyService)] = Module.Support,
        [typeof(EntKube.Web.Services.Mail.SupportMailPoller)] = Module.Support,
        [typeof(EntKube.Web.Services.Mail.SupportMailService)] = Module.Support,
        [typeof(EntKube.Web.Services.Mail.SupportMailboxService)] = Module.Support,
        [typeof(TicketBridgeAdmin)] = Module.Support,
        [typeof(TicketBridgeService)] = Module.Support,
        [typeof(EntKube.Web.Services.Tickets.TicketNotifier)] = Module.Support,
        [typeof(EntKube.Web.Services.Tickets.TicketService)] = Module.Support,
        [typeof(EntKube.Web.Services.Time.TimeService)] = Module.Support,

        // ---- Advisor ---------------------------------------------------------------
        [typeof(AdvisorDigestConfigService)] = Module.Advisor,
        [typeof(AdvisorScanService)] = Module.Advisor,
        [typeof(AdvisorStateService)] = Module.Advisor,
        [typeof(OperationsAdvisorService)] = Module.Advisor,
    };

    /// <summary>
    /// Cross-cutting wiring that belongs to no business module: presence tracking, the
    /// current actor, toasts, kubeconfig materialisation, the backup bundle. These stay
    /// registered in <c>Program.cs</c> rather than in a module's composition file.
    /// </summary>
    public static readonly IReadOnlySet<Type> PlatformServices = new HashSet<Type>
    {
        typeof(BackupService),
        typeof(EntKube.Web.Services.CurrentActor),
        typeof(HasTenantAccessHandler),
        typeof(IdentityRedirectManager),
        typeof(IdentityRevalidatingAuthenticationStateProvider),
        typeof(InMemoryPresenceTracker),
        typeof(KubeconfigMaterializationInterceptor),
        typeof(KubeconfigResolver),
        typeof(PresenceCircuitHandler),
        typeof(PresenceHeartbeatService),
        typeof(RedisPresenceTracker),
        typeof(ToastService),
    };

    /// <summary>The module owning <paramref name="service"/>, or null when it is platform wiring or unmapped.</summary>
    public static Module? ServiceOwner(Type service) => Services.TryGetValue(service, out Module m) ? m : null;
}
