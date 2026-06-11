namespace EntKube.Web.Data;

/// <summary>
/// Represents a user's membership in a tenant, including what role they hold.
/// This is the many-to-many join between Users and Tenants, enriched with
/// role context — a user might be an Administrator in one tenant and a Member
/// in another.
/// </summary>
public class TenantMembership
{
    public string UserId { get; set; } = null!;

    public Guid TenantId { get; set; }

    public Guid RoleId { get; set; }

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;
    public TenantRole Role { get; set; } = null!;
}
