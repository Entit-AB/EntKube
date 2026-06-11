namespace EntKube.Web.Data;

public class DeploymentHealthSnapshot
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public HealthStatus HealthStatus { get; set; }
    public SyncStatus SyncStatus { get; set; }
    public int? ReadyReplicas { get; set; }
    public int? TotalReplicas { get; set; }
    public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;

    public AppDeployment Deployment { get; set; } = null!;
}
