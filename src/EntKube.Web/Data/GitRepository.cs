namespace EntKube.Web.Data;

/// <summary>
/// A registered Git repository credential set, scoped to a tenant.
/// Acts like ArgoCD's Repository — a reusable auth configuration that multiple
/// deployments can reference. Credentials are stored encrypted in the tenant vault
/// (VaultSecret rows with GitRepositoryId set), never in this table.
///
/// Supported auth types:
/// - None: public repo, no credentials
/// - HttpsPat: HTTPS with a single PAT (stored as vault secret "PAT")
/// - HttpsPassword: HTTPS with username + password (stored as vault secret "PASSWORD"; username stored here)
/// - SshKey: SSH private key (stored as vault secret "SSH_PRIVATE_KEY")
/// </summary>
public class GitRepository
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// Human-friendly label for this repository (e.g. "prod-gitops-repo").
    /// Must be unique within the tenant.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The clone URL. HTTPS (https://...) or SSH (git@github.com:...) depending on AuthType.
    /// </summary>
    public required string Url { get; set; }

    public GitAuthType AuthType { get; set; } = GitAuthType.None;

    /// <summary>
    /// For HttpsPassword auth: the username to use alongside the vault-stored password.
    /// For Azure DevOps PAT auth this is often just the string "x-auth-token" or empty.
    /// Not used for SshKey or HttpsPat auth.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// The default branch/revision to check out when a deployment does not specify one.
    /// </summary>
    public string DefaultBranch { get; set; } = "main";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When set, this repository was created on behalf of a customer and uses the
    /// customer's stored credential for authentication rather than its own vault entries.
    /// </summary>
    public Guid? CustomerGitCredentialId { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public CustomerGitCredential? CustomerGitCredential { get; set; }
    public ICollection<AppDeployment> Deployments { get; set; } = [];
    public ICollection<GitKnownHost> KnownHosts { get; set; } = [];
}
