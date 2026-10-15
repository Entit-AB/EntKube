namespace EntKube.Contracts.Delivery;

/// <summary>How a deployment is delivered.</summary>
/// <summary>
/// How a deployment is defined.
///
/// <para><b>These mirror Delivery's own members exactly, in order, on purpose.</b> The first
/// version of this enum was a coarser vocabulary — <c>Yaml, Helm, Git, Manual</c> — and the
/// implementation mapped onto it with <c>(DeploymentType)(int)d.Type</c>. Nothing lined up: the
/// entity's <c>Manual</c> is 0 and arrived as <c>Yaml</c>, its <c>HelmChart</c> arrived as
/// <c>Git</c>, and <c>GitHelm</c> and <c>GitAppOfApps</c> had no member to land on at all, so they
/// produced an enum value outside the declared range. It went unnoticed because this contract had
/// no consumer that read the field.</para>
///
/// <para>A coarser public vocabulary would have been defensible, but it has to be an explicit
/// mapping rather than a cast, and collapsing <c>GitHelm</c> into either "Helm" or "Git" discards
/// something true. Mirroring is the only version that cannot invent a value, and
/// <c>ModuleApiTests</c> keeps the two declarations in step.</para>
/// </summary>
public enum DeploymentType
{
    /// <summary>Built from a form; the platform generates the manifests.</summary>
    Manual,

    /// <summary>Raw YAML manifests supplied by the operator.</summary>
    Yaml,

    /// <summary>A Helm chart from a repository.</summary>
    HelmChart,

    /// <summary>Raw YAML manifests sourced from a git repository.</summary>
    GitYaml,

    /// <summary>A Helm chart sourced from a git repository.</summary>
    GitHelm,

    /// <summary>An app-of-apps tree sourced from a git repository.</summary>
    GitAppOfApps,
}

/// <summary>Whether what is running matches what was asked for.</summary>
public enum SyncStatus
{
    Unknown,
    Synced,
    OutOfSync,
    Syncing,
    Failed,
}

/// <summary>
/// Whether what is running is well. Mirrors Delivery's members in order — <c>Suspended</c> was
/// missing here, so a suspended deployment had no member to map onto.
/// </summary>
public enum HealthStatus
{
    Unknown,
    Healthy,
    Progressing,
    Degraded,
    Missing,
    Suspended,
}

/// <summary>
/// A customer application, as everyone outside Delivery sees it.
/// </summary>
/// <param name="Id">The app's id.</param>
/// <param name="TenantId">Owning tenant, by way of its customer.</param>
/// <param name="CustomerId">The customer it belongs to.</param>
/// <param name="CustomerName">That customer's name, to save a second lookup.</param>
/// <param name="Name">The app's name.</param>
/// <param name="Namespace">Its default namespace, when it has one.</param>
public sealed record AppSummary(
    Guid Id,
    Guid TenantId,
    Guid CustomerId,
    string CustomerName,
    string Name,
    string? Namespace);

/// <summary>
/// One deployment of an app into one environment on one cluster.
///
/// <para>Carries the app and cluster identities inline for the same reason
/// <see cref="Catalog.InstalledComponent"/> does: of the cross-module reads, 20 were
/// <c>.Include(d =&gt; d.Cluster)</c> and 8 <c>.Include(d =&gt; d.App)</c>. A deployment on its
/// own answers almost nothing.</para>
/// </summary>
/// <param name="Id">The deployment's id.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="AppId">The app being deployed.</param>
/// <param name="AppName">Its name, to save a second lookup.</param>
/// <param name="EnvironmentId">Which environment.</param>
/// <param name="EnvironmentName">
/// That environment's name, carried inline for the same reason <see cref="AppName"/> is: a caller
/// grouping deployments by environment — a cost report, a report by stage — would otherwise need
/// Delivery's <c>Environments</c> table, which is the dependency this contract exists to remove.
/// Added when the first consumer was written, because a contract missing the one field its callers
/// read cannot be adopted by them: that is how <c>InstalledComponent.Configuration</c> was missed,
/// and how an interface stays unused.
/// </param>
/// <param name="ClusterId">Which cluster.</param>
/// <param name="Name">The deployment's name.</param>
/// <param name="Type">How it is delivered.</param>
/// <param name="Namespace">Where it lands.</param>
/// <param name="SyncStatus">Whether it matches what was asked for.</param>
/// <param name="HealthStatus">Whether it is well.</param>
/// <param name="StatusMessage">Any detail behind those two.</param>
/// <param name="LastSyncedAt">When it last synced.</param>
/// <param name="IsManaged">Whether EntKube owns its lifecycle.</param>
public sealed record DeploymentSummary(
    Guid Id,
    Guid TenantId,
    Guid AppId,
    string AppName,
    Guid EnvironmentId,
    string EnvironmentName,
    Guid ClusterId,
    string Name,
    DeploymentType Type,
    string? Namespace,
    SyncStatus SyncStatus,
    HealthStatus HealthStatus,
    string? StatusMessage,
    DateTime? LastSyncedAt,
    bool IsManaged);

/// <summary>
/// What Delivery offers the rest of EntKube.
///
/// <para>Read-only: 22 services across other modules read <c>AppDeployment</c> and none of
/// them writes it. Deploying, syncing and rolling back are Delivery's own work.</para>
/// </summary>
public interface IDeliveryApi
{
    /// <summary>One app, or null when it is not this tenant's.</summary>
    Task<AppSummary?> GetAppAsync(Guid tenantId, Guid appId, CancellationToken ct = default);

    /// <summary>Every app the tenant has.</summary>
    Task<IReadOnlyList<AppSummary>> GetAppsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>One deployment, or null when it is not this tenant's.</summary>
    Task<DeploymentSummary?> GetDeploymentAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default);

    /// <summary>Every deployment in the tenant.</summary>
    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>What one app has running in one environment — the most common question asked of this table.</summary>
    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForAppEnvironmentAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default);

    /// <summary>Everything deployed to one cluster.</summary>
    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default);
}
