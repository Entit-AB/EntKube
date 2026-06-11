namespace EntKube.Web.Data;

/// <summary>
/// A trusted SSH host fingerprint for a tenant. When a GitRepository with SshKey
/// auth connects for the first time, the server's fingerprint is recorded here
/// (auto-trust on first connect, like ArgoCD). Subsequent connections that present
/// a different fingerprint for the same hostname will fail.
///
/// Scoped to tenant rather than individual repositories so that multiple repos on
/// the same host (e.g. github.com) share one trust entry.
/// </summary>
public class GitKnownHost
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The hostname (e.g. "github.com", "dev.azure.com").
    /// </summary>
    public required string Hostname { get; set; }

    /// <summary>
    /// SHA-256 fingerprint of the host key, base64-encoded (e.g. "SHA256:nThbg6...").
    /// </summary>
    public required string Fingerprint { get; set; }

    /// <summary>
    /// The key type reported by the server (e.g. "ssh-rsa", "ecdsa-sha2-nistp256", "ssh-ed25519").
    /// </summary>
    public required string KeyType { get; set; }

    public DateTime TrustedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
