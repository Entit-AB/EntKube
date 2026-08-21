namespace EntKube.Web.Data;

/// <summary>
/// Maps a group from the identity provider onto access inside EntKube.
///
/// Access granted this way is <em>derived</em>, not stored as an independent fact: it is
/// recomputed from the provider's claims on every SSO login. That is what makes
/// deprovisioning work — removing someone from a group in the IdP removes their EntKube
/// access at their next login, without anyone remembering to do it here too.
/// </summary>
public class ExternalGroupMapping
{
    public Guid Id { get; set; }

    /// <summary>
    /// The group value as the identity provider emits it. For Entra this is usually a
    /// group object id rather than a display name; for Keycloak or Okta it is often a
    /// path or name. Stored verbatim and compared exactly — normalising it would silently
    /// match groups the operator did not intend.
    /// </summary>
    public required string ExternalGroup { get; set; }

    /// <summary>Tenant the group grants access to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Role members of the group receive in that tenant.</summary>
    public Guid RoleId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public TenantRole Role { get; set; } = null!;
}
