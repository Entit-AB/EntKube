namespace EntKube.Web.Data;

/// <summary>
/// A reusable, tenant-scoped recipe for standing up a cluster's platform: an
/// ordered list of components (Helm charts from the catalog) and services
/// (CNPG/Redis/RabbitMQ clusters) with their parameters. After a cluster is
/// registered, an operator can bootstrap it from a blueprint and a background
/// runner installs each step in order.
///
/// The <see cref="ProvisioningProvider"/> / <see cref="ProvisioningConfig"/>
/// fields are reserved for a future phase where a blueprint also provisions the
/// underlying cluster (e.g. via a cloud provider). They are unused today — a
/// null provider means "bootstrap targets an already-registered cluster".
/// </summary>
public class ClusterBlueprint
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Human-friendly name, unique within a tenant (e.g. "Standard Platform").</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    // ── Reserved for future cluster-provisioning scope (unused today) ──

    /// <summary>Future: provider used to create the cluster itself. Null = target an existing registered cluster.</summary>
    public string? ProvisioningProvider { get; set; }

    /// <summary>Future: provider-specific provisioning configuration (JSON).</summary>
    public string? ProvisioningConfig { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<BlueprintStep> Steps { get; set; } = [];

    /// <summary>Per-blueprint variables, each with a value per environment, referenced as ${Name} in step parameters.</summary>
    public ICollection<BlueprintVariable> Variables { get; set; } = [];
}
