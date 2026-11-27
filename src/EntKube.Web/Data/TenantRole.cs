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

    /// <summary>
    /// Which version of the permission rules this role's grants were written for.
    ///
    /// <para>Before role permissions were enforced, a member could do everything in the tenant
    /// whatever their role said. Roles from that time are at 0, and are carried across once
    /// so that nobody loses what they could already do (see
    /// <see cref="Services.TenantRoleService.CarryRolesAcrossAsync"/>). The database column defaults
    /// to 0 for exactly that reason.</para>
    ///
    /// <para>A role made in code starts at the current version, and so with only what it is
    /// explicitly granted. A creator that forgets about this field gets the least privilege,
    /// not a quiet grant of Manage at the next restart.</para>
    /// </summary>
    public int PermissionSchema { get; set; } = CurrentPermissionSchema;

    /// <summary>The version of the permission rules this build enforces.</summary>
    public const int CurrentPermissionSchema = 1;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<TenantMembership> Memberships { get; set; } = [];
}
