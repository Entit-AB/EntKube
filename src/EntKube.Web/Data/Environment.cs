namespace EntKube.Web.Data;

/// <summary>
/// An environment represents a deployment stage within a tenant (e.g. "Development",
/// "Staging", "Production"). Clusters, services, and other resources are scoped
/// to an environment within a tenant.
/// </summary>
public class Environment
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// Human-friendly name for the environment (e.g. "Production").
    /// Must be unique within a tenant.
    /// </summary>
    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<AppEnvironment> AppEnvironments { get; set; } = [];
    public ICollection<KubernetesCluster> KubernetesClusters { get; set; } = [];
}
