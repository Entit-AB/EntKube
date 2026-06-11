namespace EntKube.Web.Data;

public class MaintenanceWindow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ClusterId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster? Cluster { get; set; }
}
