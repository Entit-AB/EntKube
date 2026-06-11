namespace EntKube.Web.Data;

/// <summary>
/// A group organizes users within a tenant. Groups enable bulk permission
/// assignment and logical team structure (e.g. "Engineering", "On-Call").
/// A group belongs to exactly one tenant.
/// </summary>
public class Group
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<GroupMembership> Memberships { get; set; } = [];
}
