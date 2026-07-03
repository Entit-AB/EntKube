namespace EntKube.Web.Data;

/// <summary>
/// A tenant represents an organization or workspace in EntKube. Every resource
/// (clusters, services, monitoring) is scoped to a tenant. Users interact with
/// the platform through their tenant memberships.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-friendly display name for the tenant (e.g. "Acme Corp").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// URL-safe unique identifier used in routes and API calls (e.g. "acme-corp").
    /// Must be unique across all tenants.
    /// </summary>
    public required string Slug { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties — a tenant owns its roles, memberships, groups, environments, customers, clusters, and vault.
    public ICollection<TenantRole> Roles { get; set; } = [];
    public ICollection<TenantMembership> Memberships { get; set; } = [];
    public ICollection<Group> Groups { get; set; } = [];
    public ICollection<Environment> Environments { get; set; } = [];
    public ICollection<Customer> Customers { get; set; } = [];
    public ICollection<KubernetesCluster> KubernetesClusters { get; set; } = [];
    public ICollection<ClusterBlueprint> Blueprints { get; set; } = [];
    public ICollection<GitRepository> GitRepositories { get; set; } = [];
    public ICollection<GitKnownHost> GitKnownHosts { get; set; } = [];
    public SecretVault? Vault { get; set; }
}
