using System.Text;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages per-customer git repo URL allowlist policies and reusable git
/// credential sets. Credential secrets are stored encrypted via VaultService.
/// </summary>
public class CustomerGitService(
    IDbContextFactory<DeliveryDbContext> dbFactory,
    VaultService vault)
{
    /// <summary>
    /// Whether this customer and this environment are both the tenant's. Used by the writes that
    /// name a pair — a policy or a credential is written against one, and a customer of ours with
    /// an environment of theirs is as wrong as the reverse.
    /// </summary>
    private async Task<bool> PairBelongsToTenantAsync(
        Guid tenantId, Guid customerId, Guid environmentId, CancellationToken ct)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct)
            && await db.Environments.AnyAsync(e => e.Id == environmentId && e.TenantId == tenantId, ct);
    }

    // ── Repo policies ────────────────────────────────────────────────────────────

    /// <param name="tenantId">
    /// Whose customer. A repo policy is the allow-list a deployment's git URL is checked against,
    /// so reading another tenant's tells you which repositories — and which hosts and orgs — they
    /// deploy from. <c>CustomerGitRepoPolicy</c> has no <c>TenantId</c>, so the scope travels
    /// through the customer.
    /// </param>
    public async Task<List<CustomerGitRepoPolicy>> GetRepoPoliciesAsync(
        Guid tenantId, Guid customerId, Guid environmentId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.CustomerGitRepoPolicies
            .Where(p => p.CustomerId == customerId && p.EnvironmentId == environmentId
                     && p.Customer.TenantId == tenantId)
            .OrderBy(p => p.UrlPattern)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose customer. Widening somebody else's allow-list is the write that matters here: a
    /// pattern added to their policy is a repository their deployments will then accept.
    /// </param>
    public async Task<CustomerGitRepoPolicy> AddRepoPolicyAsync(
        Guid tenantId, Guid customerId, Guid environmentId, string urlPattern,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, customerId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The customer or the environment does not belong to this tenant.");
        }

        using DeliveryDbContext db = dbFactory.CreateDbContext();

        CustomerGitRepoPolicy policy = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            EnvironmentId = environmentId,
            UrlPattern = urlPattern.Trim()
        };

        db.CustomerGitRepoPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return policy;
    }

    /// <param name="tenantId">
    /// Whose customer. Deleting an allow-list entry narrows what their deployments may pull, so
    /// this is a denial of service against their next sync as much as it is a deletion.
    /// </param>
    public async Task<bool> DeleteRepoPolicyAsync(
        Guid tenantId, Guid customerId, Guid policyId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        CustomerGitRepoPolicy? policy = await db.CustomerGitRepoPolicies
            .FirstOrDefaultAsync(
                p => p.CustomerId == customerId && p.Id == policyId
                  && p.Customer.TenantId == tenantId, ct);

        if (policy is null) return false;

        db.CustomerGitRepoPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="url"/> matches at least one policy pattern.
    /// '*' matches any characters except '/'; '**' matches across '/'.
    /// </summary>
    public static bool MatchesAnyPolicy(string url, IEnumerable<CustomerGitRepoPolicy> policies)
        => policies.Any(p => MatchesPattern(url, p.UrlPattern));

    public static bool MatchesPattern(string url, string pattern)
    {
        // Convert the glob pattern to a regex so matching is handled by the
        // battle-tested .NET regex engine rather than a hand-rolled walker.
        //   '**'  → matches any sequence of characters, including '/'
        //   '*'   → matches any sequence of characters within a single path segment (no '/')
        //   other → escaped and matched literally
        StringBuilder rx = new("^");
        int i = 0;
        while (i < pattern.Length)
        {
            if (i + 1 < pattern.Length && pattern[i] == '*' && pattern[i + 1] == '*')
            {
                rx.Append(".*");
                i += 2;
            }
            else if (pattern[i] == '*')
            {
                rx.Append("[^/]*");
                i++;
            }
            else
            {
                rx.Append(Regex.Escape(pattern[i].ToString()));
                i++;
            }
        }
        rx.Append('$');
        return Regex.IsMatch(url, rx.ToString(), RegexOptions.IgnoreCase);
    }

    // ── Repository auto-provisioning ────────────────────────────────────────────

    /// <summary>
    /// Finds an existing <see cref="GitRepository"/> that was provisioned for the given
    /// customer credential and URL, or creates one backed by that credential.
    /// The repository reuses the credential's auth type and fetches secrets from the
    /// customer credential vault entries at sync time — no secret copying needed.
    /// </summary>
    public async Task<GitRepository> FindOrCreateRepositoryForCustomerAsync(
        Guid tenantId,
        Guid customerId,
        string url,
        Guid credentialId,
        CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        // Normal case: repo already linked to this credential.
        GitRepository? existing = await db.GitRepositories
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.CustomerGitCredentialId == credentialId &&
                r.Url == url, ct);

        if (existing is not null) return existing;

        CustomerGitCredential? cred = await db.CustomerGitCredentials
            .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.Id == credentialId, ct);

        if (cred is null)
            throw new InvalidOperationException("Customer git credential not found.");

        // Recovery case: repo exists but credential was deleted (SetNull). Re-link rather
        // than creating a duplicate so existing deployment FKs stay valid.
        GitRepository? orphaned = await db.GitRepositories
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.CustomerGitCredentialId == null &&
                r.Url == url, ct);

        if (orphaned is not null)
        {
            orphaned.CustomerGitCredentialId = credentialId;
            orphaned.AuthType = cred.AuthType;
            orphaned.Username = cred.Username;
            await db.SaveChangesAsync(ct);
            return orphaned;
        }

        // Derive a short name from the URL path (e.g. "github.com/org/repo").
        string name = DeriveRepoName(tenantId, url, db);

        GitRepository repo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Url = url,
            AuthType = cred.AuthType,
            Username = cred.Username,
            CustomerGitCredentialId = credentialId
        };

        db.GitRepositories.Add(repo);
        await db.SaveChangesAsync(ct);
        return repo;
    }

    private static string DeriveRepoName(Guid tenantId, string url, DeliveryDbContext db)
    {
        // Strip scheme and trailing .git to get a human-readable label.
        string path = url
            .TrimEnd('/')
            .Replace("https://", "")
            .Replace("http://", "")
            .Replace("git@", "")
            .TrimEnd(".git".ToCharArray());

        // Keep it short and unique within the tenant.
        string candidate = path.Length > 120 ? path[^120..] : path;
        string name = candidate;
        int suffix = 2;
        while (db.GitRepositories.Any(r => r.TenantId == tenantId && r.Name == name))
            name = $"{candidate} ({suffix++})";

        return name;
    }

    // ── Credentials ──────────────────────────────────────────────────────────────

    /// <param name="tenantId">
    /// Whose customer. A credential row names the auth type, the username and the URL pattern —
    /// the secret itself is in the vault, but the row is the key to it, and <c>GetPatAsync</c> and
    /// its siblings take an id. <c>CustomerGitCredential</c> carries a <c>TenantId</c> column, so
    /// this is a direct comparison rather than a traversal.
    /// </param>
    public async Task<List<CustomerGitCredential>> GetCredentialsAsync(
        Guid tenantId, Guid customerId, Guid environmentId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.CustomerGitCredentials
            .Where(c => c.CustomerId == customerId && c.EnvironmentId == environmentId
                     && c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose customer. ⚠️ This one already took a <c>tenantId</c> — and <em>wrote</em> it onto the
    /// row without ever checking that the customer or the environment were that tenant's. A
    /// tenant id that is only ever stamped is not a check: the row would claim one tenant while
    /// hanging off another's customer, and every later query that trusts the column would serve
    /// it to the wrong side.
    /// </param>
    public async Task<CustomerGitCredential> CreateCredentialAsync(
        Guid customerId, Guid environmentId, Guid tenantId, string name,
        GitAuthType authType, string? username = null, string? urlPattern = null,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, customerId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The customer or the environment does not belong to this tenant.");
        }

        using DeliveryDbContext db = dbFactory.CreateDbContext();

        CustomerGitCredential credential = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            EnvironmentId = environmentId,
            TenantId = tenantId,
            Name = name.Trim(),
            AuthType = authType,
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            UrlPattern = string.IsNullOrWhiteSpace(urlPattern) ? null : urlPattern.Trim()
        };

        db.CustomerGitCredentials.Add(credential);
        await db.SaveChangesAsync(ct);
        return credential;
    }

    /// <param name="tenantId">
    /// Whose credential. The <c>UrlPattern</c> is editable here, and that is what decides which
    /// repository a stored secret is sent to — so an edit can redirect somebody else's PAT at a
    /// host of the editor's choosing without ever reading it.
    /// </param>
    public async Task<bool> UpdateCredentialAsync(
        Guid tenantId, Guid customerId, Guid credentialId,
        string name, GitAuthType authType, string? username, string? urlPattern,
        CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        CustomerGitCredential? credential = await db.CustomerGitCredentials
            .FirstOrDefaultAsync(
                c => c.CustomerId == customerId && c.Id == credentialId
                  && c.TenantId == tenantId, ct);

        if (credential is null) return false;

        credential.Name = name.Trim();
        credential.AuthType = authType;
        credential.Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        credential.UrlPattern = string.IsNullOrWhiteSpace(urlPattern) ? null : urlPattern.Trim();

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Finds the best-matching credential for a given repo URL in the customer's environment.
    /// Prefers credentials whose UrlPattern matches the URL; falls back to credentials with no
    /// pattern set. Returns null if no credentials exist.
    /// </summary>
    /// <param name="tenantId">
    /// Whose customer. This is the one the sync path calls, and what it returns is handed to git
    /// as the credential to authenticate with — so an unscoped match would make one tenant's
    /// stored PAT the one a stranger's deployment pulls with.
    /// </param>
    public async Task<CustomerGitCredential?> FindMatchingCredentialAsync(
        Guid tenantId, Guid customerId, Guid environmentId, string url,
        CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        List<CustomerGitCredential> creds = await db.CustomerGitCredentials
            .Where(c => c.CustomerId == customerId && c.EnvironmentId == environmentId
                     && c.TenantId == tenantId)
            .ToListAsync(ct);

        return creds.FirstOrDefault(c => c.UrlPattern is not null && MatchesPattern(url, c.UrlPattern))
            ?? creds.FirstOrDefault(c => c.UrlPattern is null)
            ?? creds.FirstOrDefault();
    }

    /// <param name="tenantId">Whose credential.</param>
    public async Task<bool> DeleteCredentialAsync(
        Guid tenantId, Guid customerId, Guid credentialId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        CustomerGitCredential? credential = await db.CustomerGitCredentials
            .FirstOrDefaultAsync(
                c => c.CustomerId == customerId && c.Id == credentialId
                  && c.TenantId == tenantId, ct);

        if (credential is null) return false;

        db.CustomerGitCredentials.Remove(credential);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Credential secrets (stored encrypted in vault) ───────────────────────────

    public Task SetPatAsync(Guid tenantId, Guid credentialId, string pat, CancellationToken ct = default)
        => vault.SetCustomerGitCredentialSecretAsync(tenantId, credentialId, "PAT", pat, ct);

    public Task SetPasswordAsync(Guid tenantId, Guid credentialId, string password, CancellationToken ct = default)
        => vault.SetCustomerGitCredentialSecretAsync(tenantId, credentialId, "PASSWORD", password, ct);

    public Task SetSshKeyAsync(Guid tenantId, Guid credentialId, string privateKeyPem, CancellationToken ct = default)
        => vault.SetCustomerGitCredentialSecretAsync(tenantId, credentialId, "SSH_PRIVATE_KEY", privateKeyPem, ct);

    public Task<string?> GetPatAsync(Guid tenantId, Guid credentialId, CancellationToken ct = default)
        => vault.GetCustomerGitCredentialSecretValueAsync(tenantId, credentialId, "PAT", ct);

    public Task<string?> GetPasswordAsync(Guid tenantId, Guid credentialId, CancellationToken ct = default)
        => vault.GetCustomerGitCredentialSecretValueAsync(tenantId, credentialId, "PASSWORD", ct);

    public Task<string?> GetSshKeyAsync(Guid tenantId, Guid credentialId, CancellationToken ct = default)
        => vault.GetCustomerGitCredentialSecretValueAsync(tenantId, credentialId, "SSH_PRIVATE_KEY", ct);
}
