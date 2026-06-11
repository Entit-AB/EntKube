using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Singleton push bus for deployment status changes. Background services
/// (DeploymentSyncService, PortalDeploymentDetail) call Notify() after writing
/// a new SyncStatus/HealthStatus to the database. Blazor Server components
/// subscribe on mount so their status badges update immediately — no polling.
/// </summary>
public sealed class DeploymentStatusNotifier
{
    public event Action<Guid, SyncStatus, HealthStatus>? OnStatusChanged;

    public void Notify(Guid deploymentId, SyncStatus sync, HealthStatus health)
        => OnStatusChanged?.Invoke(deploymentId, sync, health);
}
