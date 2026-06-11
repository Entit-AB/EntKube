namespace EntKube.Web.Data;

/// <summary>
/// A role that exists within a specific tenant's scope. Roles define what a
/// user can do within that tenant. Every tenant gets at least an "Administrator"
/// role by default.
/// </summary>
public class TenantRole
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The role name (e.g. "Administrator", "Member", "Viewer").
    /// Must be unique within a tenant.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// JSON-serialized Dictionary&lt;TenantFeature, AccessLevel&gt; — permissions per feature.
    /// Null means "no explicit permissions defined" (treat as all None).
    /// </summary>
    public string? PermissionsJson { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<TenantMembership> Memberships { get; set; } = [];
}
