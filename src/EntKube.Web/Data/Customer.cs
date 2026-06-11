namespace EntKube.Web.Data;

/// <summary>
/// A customer represents an end-client or account within a tenant. Tenants
/// may serve multiple customers, and this entity tracks that relationship.
/// </summary>
public class Customer
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The customer's display name. Must be unique within a tenant.
    /// </summary>
    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<App> Apps { get; set; } = [];
    public ICollection<CustomerGitRepoPolicy> GitRepoPolicies { get; set; } = [];
    public ICollection<CustomerGitCredential> GitCredentials { get; set; } = [];
}
