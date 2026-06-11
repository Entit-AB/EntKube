using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// CRUD operations for GitRepository records and credential validation.
/// Credential storage always goes through VaultService so values are
/// encrypted with the tenant DEK before being persisted.
/// </summary>
public class GitRepositoryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    GitOperationsService gitOps)
{
    // ── Listing & retrieval ──────────────────────────────────────────────────────

    public async Task<List<GitRepository>> GetRepositoriesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.GitRepositories
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<GitRepository?> GetRepositoryAsync(
        Guid tenantId, Guid repoId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.GitRepositories
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == repoId, ct);
    }

    // ── Create / update ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new Git repository registration. After calling this, store
    /// credentials with <see cref="SetPatAsync"/>, <see cref="SetPasswordAsync"/>,
    /// or <see cref="SetSshKeyAsync"/> as appropriate for the auth type.
    /// </summary>
    public async Task<GitRepository> CreateRepositoryAsync(
        Guid tenantId, string name, string url,
        GitAuthType authType, string? username = null, string defaultBranch = "main",
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        GitRepository repo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Url = url,
            AuthType = authType,
            Username = username,
            DefaultBranch = defaultBranch
        };

        db.GitRepositories.Add(repo);
        await db.SaveChangesAsync(ct);
        return repo;
    }

    public async Task<bool> UpdateRepositoryAsync(
        Guid tenantId, Guid repoId,
        string name, string url, GitAuthType authType,
        string? username, string defaultBranch,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        GitRepository? repo = await db.GitRepositories
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == repoId, ct);

        if (repo is null) return false;

        repo.Name = name;
        repo.Url = url;
        repo.AuthType = authType;
        repo.Username = username;
        repo.DefaultBranch = defaultBranch;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteRepositoryAsync(
        Guid tenantId, Guid repoId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        GitRepository? repo = await db.GitRepositories
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == repoId, ct);

        if (repo is null) return false;

        db.GitRepositories.Remove(repo);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Credential management (all go through vault) ─────────────────────────────

    /// <summary>Store a Personal Access Token for HTTPS auth.</summary>
    public Task SetPatAsync(Guid tenantId, Guid repoId, string pat, CancellationToken ct = default)
        => vault.SetGitRepositorySecretAsync(tenantId, repoId, "PAT", pat, ct);

    /// <summary>Store a password for HTTPS username+password auth.</summary>
    public Task SetPasswordAsync(Guid tenantId, Guid repoId, string password, CancellationToken ct = default)
        => vault.SetGitRepositorySecretAsync(tenantId, repoId, "PASSWORD", password, ct);

    /// <summary>Store an SSH private key (PEM format).</summary>
    public Task SetSshKeyAsync(Guid tenantId, Guid repoId, string privateKeyPem, CancellationToken ct = default)
        => vault.SetGitRepositorySecretAsync(tenantId, repoId, "SSH_PRIVATE_KEY", privateKeyPem, ct);

    // ── Connection validation ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to connect to the repository and fetch the HEAD commit SHA.
    /// Returns (success, commitSha|errorMessage).
    /// </summary>
    public async Task<(bool Success, string Message)> ValidateConnectionAsync(
        Guid tenantId, Guid repoId, CancellationToken ct = default)
    {
        GitRepository? repo = await GetRepositoryAsync(tenantId, repoId, ct);

        if (repo is null)
            return (false, "Repository not found.");

        string revision = repo.DefaultBranch;

        try
        {
            string? sha = await gitOps.GetHeadCommitAsync(repo, revision, ct: ct);

            if (sha is null)
                return (false, "Could not reach repository or resolve branch.");

            return (true, $"Connected. HEAD ({revision}): {sha[..Math.Min(7, sha.Length)]}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Known hosts management ───────────────────────────────────────────────────

    public async Task<List<GitKnownHost>> GetKnownHostsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.GitKnownHosts
            .Where(h => h.TenantId == tenantId)
            .OrderBy(h => h.Hostname)
            .ToListAsync(ct);
    }

    public async Task<bool> RemoveKnownHostAsync(
        Guid tenantId, Guid knownHostId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        GitKnownHost? host = await db.GitKnownHosts
            .FirstOrDefaultAsync(h => h.TenantId == tenantId && h.Id == knownHostId, ct);

        if (host is null) return false;

        db.GitKnownHosts.Remove(host);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
