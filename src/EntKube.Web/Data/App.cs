namespace EntKube.Web.Data;

/// <summary>
/// An app represents a software application owned by a customer. Apps can be
/// deployed to one or many environments via the AppEnvironment join entity.
/// </summary>
public class App
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>
    /// The application name. Must be unique within a customer.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The primary Kubernetes namespace for this app. Used as the target for
    /// ResourceQuota, NetworkPolicy, and RBAC resources. Set by tenant admins;
    /// read-only in the customer portal. Null means no namespace has been assigned.
    /// </summary>
    public string? Namespace { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer Customer { get; set; } = null!;
    public ICollection<AppEnvironment> AppEnvironments { get; set; } = [];
    public ICollection<VaultSecret> Secrets { get; set; } = [];
    public ICollection<AppDeployment> Deployments { get; set; } = [];
    public ICollection<AppQuota> Quotas { get; set; } = [];
    public ICollection<AppNetworkPolicy> NetworkPolicies { get; set; } = [];
    public ICollection<AppRbacPolicy> RbacPolicies { get; set; } = [];
    public ICollection<AppRoute> Routes { get; set; } = [];
    public ICollection<AppAllowedDatabase> AllowedDatabases { get; set; } = [];
    public ICollection<AppAllowedCache> AllowedCaches { get; set; } = [];
    public ICollection<AppAllowedStorage> AllowedStorages { get; set; } = [];

    // Connectivity model — least-privilege graph (exposed ports, edges, egress).
    public ICollection<AppServicePort> ServicePorts { get; set; } = [];
    public ICollection<ConnectivityRule> ConnectivityRules { get; set; } = [];
    public ICollection<ExternalDependency> ExternalDependencies { get; set; } = [];
}
