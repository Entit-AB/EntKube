namespace EntKube.Contracts.Delivery;

/// <summary>How a deployment is delivered.</summary>
public enum DeploymentType
{
    Yaml,
    Helm,
    Git,
    Manual,
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

/// <summary>Whether what is running is well.</summary>
public enum HealthStatus
{
    Unknown,
    Healthy,
    Progressing,
    Degraded,
    Missing,
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
