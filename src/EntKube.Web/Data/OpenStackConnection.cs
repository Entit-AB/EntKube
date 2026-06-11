namespace EntKube.Web.Data;

/// <summary>
/// An OpenStack connection stores the authentication details needed to interact
/// with an OpenStack cloud (e.g. Cleura/City Cloud). This enables the platform
/// to manage S3 buckets, credentials, and other resources via the OpenStack API.
///
/// Credentials (password, application credential secret) are stored encrypted
/// in the vault — only metadata lives here.
/// </summary>
public class OpenStackConnection
{
    public Guid Id { get; set; }

    /// <summary>The tenant that owns this connection.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Human-friendly name (e.g. "Cleura Production", "City Cloud Dev").</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Keystone authentication URL.
    /// Example: "https://identity.c2.citycloud.com:5000/v3"
    /// </summary>
    public required string AuthUrl { get; set; }

    /// <summary>
    /// The OpenStack region (e.g. "Kna1", "Sto2", "Fra1").
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// The OpenStack project/tenant name.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// The OpenStack project/tenant ID.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// The user domain name (typically "Default" or the company domain).
    /// </summary>
    public string? UserDomainName { get; set; }

    /// <summary>
    /// The project domain name (typically "Default").
    /// </summary>
    public string? ProjectDomainName { get; set; }

    /// <summary>
    /// The OpenStack username for authentication.
    /// The password is stored in the vault under this connection's ID.
    /// </summary>
    public string? Username { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<StorageLink> StorageLinks { get; set; } = [];
}
