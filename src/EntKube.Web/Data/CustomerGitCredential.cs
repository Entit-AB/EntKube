namespace EntKube.Web.Data;

/// <summary>
/// A reusable git credential set scoped to a customer. One credential can cover
/// any number of repositories that match the customer's GitRepoPolicies.
/// Actual secret values (PAT, SSH key, password) are encrypted in the tenant
/// vault as VaultSecret rows with CustomerGitCredentialId set.
///
/// Supported auth types (same as GitRepository):
///   None            — public repos, no credentials needed
///   HttpsPat        — HTTPS with a Personal Access Token (vault: "PAT")
///   HttpsPassword   — HTTPS with username + password (vault: "PASSWORD"; username stored here)
///   SshKey          — SSH private key (vault: "SSH_PRIVATE_KEY")
/// </summary>
public class CustomerGitCredential
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    /// <summary>Human-friendly label, e.g. "GitHub PAT". Unique within a customer.</summary>
    public required string Name { get; set; }

    public GitAuthType AuthType { get; set; } = GitAuthType.None;

    /// <summary>
    /// For HttpsPassword auth: the username paired with the vault-stored password.
    /// Ignored for other auth types.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// The URL pattern this credential covers — same glob syntax as CustomerGitRepoPolicy
    /// (e.g. https://github.com/acme/*). When set, the sync service picks this credential
    /// automatically for repos whose URL matches. Null means "applies to any URL".
    /// </summary>
    public string? UrlPattern { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer Customer { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
