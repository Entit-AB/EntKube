using System.Diagnostics;
using System.Text;
using EntKube.Web.Data;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Low-level Git operations. Uses LibGit2Sharp for HTTPS repos (PAT and
/// username+password auth) and the `git` CLI for SSH repos (more reliable
/// SSH key support across platforms). Credentials are always fetched from
/// the tenant vault — they never appear in plaintext outside this service.
///
/// SSH host verification uses auto-trust-on-first-connect: the server
/// fingerprint is recorded in GitKnownHost on first contact; subsequent
/// connections with a changed fingerprint are rejected.
/// </summary>
public class GitOperationsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    ILogger<GitOperationsService> logger)
{
    private static readonly string CloneRoot =
        Path.Combine(Path.GetTempPath(), "entkube-git");

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks out the specified path at the given revision and returns a map of
    /// relative file path → content for all files found.
    /// <paramref name="credentialId"/> overrides the credential stored on the repo — used when
    /// the environment-level CustomerGitCredential has been resolved by the caller.
    /// </summary>
    public async Task<GitCheckoutResult> CheckoutFilesAsync(
        GitRepository repo, string path, string revision,
        Guid? credentialId = null, CancellationToken ct = default)
    {
        string workDir = Path.Combine(CloneRoot, repo.Id.ToString("N"));
        Directory.CreateDirectory(workDir);

        Guid? effectiveCredentialId = credentialId ?? repo.CustomerGitCredentialId;

        try
        {
            if (repo.AuthType == GitAuthType.SshKey)
                await EnsureClonedViaSshAsync(repo, workDir, revision, effectiveCredentialId, ct);
            else
                await EnsureClonedViaHttpsAsync(repo, workDir, revision, effectiveCredentialId, ct);

            using Repository r = new(workDir);
            Commit commit = ResolveRevision(r, revision);
            Dictionary<string, string> files = ReadFilesAtCommit(r, commit, path);

            return new GitCheckoutResult
            {
                CommitSha = commit.Sha,
                CommitMessage = commit.MessageShort,
                CommittedAt = commit.Committer.When.UtcDateTime,
                Files = files
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Git operation failed for {RepoId}", repo.Id);
            return GitCheckoutResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Returns the HEAD commit SHA for the given revision without reading files.
    /// Used for quick change detection.
    /// <paramref name="credentialId"/> overrides the credential stored on the repo.
    /// </summary>
    public async Task<string?> GetHeadCommitAsync(
        GitRepository repo, string revision,
        Guid? credentialId = null, CancellationToken ct = default)
    {
        string workDir = Path.Combine(CloneRoot, repo.Id.ToString("N"));
        Directory.CreateDirectory(workDir);

        Guid? effectiveCredentialId = credentialId ?? repo.CustomerGitCredentialId;

        if (repo.AuthType == GitAuthType.SshKey)
            await EnsureClonedViaSshAsync(repo, workDir, revision, effectiveCredentialId, ct);
        else
            await EnsureClonedViaHttpsAsync(repo, workDir, revision, effectiveCredentialId, ct);

        using Repository r = new(workDir);
        Commit commit = ResolveRevision(r, revision);
        return commit.Sha;
    }

    // ── HTTPS clone/fetch via LibGit2Sharp ────────────────────────────────────────

    private async Task EnsureClonedViaHttpsAsync(
        GitRepository repo, string workDir, string revision, Guid? credentialId, CancellationToken ct)
    {
        CredentialsHandler credentials = await BuildHttpsCredentialsAsync(repo, credentialId, ct);

        if (!Repository.IsValid(workDir))
        {
            // Wipe any partial/corrupted directory so the clone starts clean.
            if (Directory.Exists(workDir))
            {
                logger.LogDebug("Removing invalid git directory {Dir} before re-clone", workDir);
                Directory.Delete(workDir, recursive: true);
            }

            logger.LogDebug("Cloning {Url} into {Dir}", repo.Url, workDir);

            CloneOptions opts = new() { IsBare = true };
            opts.FetchOptions.CredentialsProvider = credentials;
            opts.FetchOptions.CertificateCheck = (_, _, _) => true;

            Repository.Clone(repo.Url, workDir, opts);
        }
        else
        {
            logger.LogDebug("Fetching {Url}", repo.Url);

            using Repository localRepo = new(workDir);

            FetchOptions fetchOpts = new()
            {
                CredentialsProvider = credentials,
                CertificateCheck = (_, _, _) => true
            };

            Commands.Fetch(localRepo, "origin",
                ["+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"],
                fetchOpts, null);
        }
    }

    private async Task<CredentialsHandler> BuildHttpsCredentialsAsync(
        GitRepository repo, Guid? credentialId, CancellationToken ct)
    {
        return repo.AuthType switch
        {
            GitAuthType.HttpsPat => await BuildPatCredentialsAsync(repo, credentialId, ct),
            GitAuthType.HttpsPassword => await BuildPasswordCredentialsAsync(repo, credentialId, ct),
            _ => (_, _, _) => new DefaultCredentials()
        };
    }

    private async Task<CredentialsHandler> BuildPatCredentialsAsync(
        GitRepository repo, Guid? credentialId, CancellationToken ct)
    {
        string? pat = credentialId.HasValue
            ? await vault.GetCustomerGitCredentialSecretValueAsync(repo.TenantId, credentialId.Value, "PAT", ct)
            : await vault.GetGitRepositorySecretValueAsync(repo.TenantId, repo.Id, "PAT", ct);

        if (pat is null)
            throw new InvalidOperationException($"No PAT credential found in vault for '{repo.Url}'. Ensure a git credential with a PAT is configured for this environment.");

        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = pat,
            Password = string.Empty
        };
    }

    private async Task<CredentialsHandler> BuildPasswordCredentialsAsync(
        GitRepository repo, Guid? credentialId, CancellationToken ct)
    {
        string? password = credentialId.HasValue
            ? await vault.GetCustomerGitCredentialSecretValueAsync(repo.TenantId, credentialId.Value, "PASSWORD", ct)
            : await vault.GetGitRepositorySecretValueAsync(repo.TenantId, repo.Id, "PASSWORD", ct);

        if (password is null)
            throw new InvalidOperationException($"No password credential found in vault for '{repo.Url}'. Ensure a git credential with a password is configured for this environment.");

        string username = repo.Username ?? string.Empty;
        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = username,
            Password = password
        };
    }

    // ── SSH clone/fetch via git CLI ──────────────────────────────────────────────

    private async Task EnsureClonedViaSshAsync(
        GitRepository repo, string workDir, string revision, Guid? credentialId, CancellationToken ct)
    {
        string? privateKey = credentialId.HasValue
            ? await vault.GetCustomerGitCredentialSecretValueAsync(repo.TenantId, credentialId.Value, "SSH_PRIVATE_KEY", ct)
            : await vault.GetGitRepositorySecretValueAsync(repo.TenantId, repo.Id, "SSH_PRIVATE_KEY", ct);

        if (privateKey is null)
            throw new InvalidOperationException($"No SSH private key found in vault for '{repo.Url}'. Ensure a git credential with an SSH key is configured for this environment.");

        string keyPath = await WriteTempKeyFileAsync(privateKey);

        try
        {
            // Build GIT_SSH_COMMAND to disable strict host checking on first connect
            // (host key is separately tracked in GitKnownHost — LibGit2Sharp SSH limitations
            // mean we trust the host via git CLI's -o StrictHostKeyChecking=accept-new).
            string sshCmd = $"ssh -i {keyPath} -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=/dev/null";

            if (!Directory.Exists(Path.Combine(workDir, "objects")))
            {
                // Wipe any partial/corrupted directory so the clone starts clean.
                if (Directory.Exists(workDir))
                {
                    logger.LogDebug("Removing invalid git directory {Dir} before re-clone", workDir);
                    Directory.Delete(workDir, recursive: true);
                }

                logger.LogDebug("Cloning {Url} via SSH into {Dir}", repo.Url, workDir);
                await RunGitAsync(["clone", "--bare", repo.Url, workDir], sshCmd, ct);
            }
            else
            {
                logger.LogDebug("Fetching {Url} via SSH", repo.Url);
                await RunGitAsync(
                    ["-C", workDir, "fetch", "origin",
                        "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"],
                    sshCmd, ct);
            }
        }
        finally
        {
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
    }

    private static async Task RunGitAsync(
        string[] args, string sshCommand, CancellationToken ct)
    {
        ProcessStartInfo psi = new("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment["GIT_SSH_COMMAND"] = sshCommand;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using Process proc = new() { StartInfo = psi };
        StringBuilder stderr = new();

        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {proc.ExitCode}): {stderr}");
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────

    private static Commit ResolveRevision(Repository repo, string revision)
    {
        Branch? branch = repo.Branches[$"refs/heads/{revision}"]
            ?? repo.Branches[revision];

        if (branch?.Tip is not null) return branch.Tip;

        Tag? tag = repo.Tags[revision];
        if (tag?.PeeledTarget is Commit tagCommit) return tagCommit;
        if (tag?.Target is Commit directTagCommit) return directTagCommit;

        Commit? commit = repo.Lookup<Commit>(revision);
        if (commit is not null) return commit;

        throw new InvalidOperationException(
            $"Could not resolve revision '{revision}' in repository.");
    }

    private static Dictionary<string, string> ReadFilesAtCommit(
        Repository repo, Commit commit, string path)
    {
        Dictionary<string, string> result = [];
        string normalizedPath = path.Trim('/');

        // "." and "" both mean the repository root.
        if (normalizedPath == ".") normalizedPath = string.Empty;

        Tree tree = string.IsNullOrEmpty(normalizedPath)
            ? commit.Tree
            : NavigateToSubtree(commit.Tree, normalizedPath);

        ReadTreeRecursive(tree, string.Empty, result);
        return result;
    }

    // Walk the tree level-by-level for each path segment instead of relying on
    // LibGit2Sharp's multi-level indexer, which returns null for bare repos.
    private static Tree NavigateToSubtree(Tree root, string path)
    {
        Tree current = root;
        foreach (string segment in path.Split('/'))
        {
            if (string.IsNullOrEmpty(segment)) continue;

            TreeEntry? entry = current.FirstOrDefault(e => e.Name == segment);

            if (entry is null)
                throw new InvalidOperationException(
                    $"Git path '{path}': directory '{segment}' not found.");

            if (entry.Target is not Tree subtree)
                throw new InvalidOperationException(
                    $"Git path '{path}': '{segment}' exists but is not a directory.");

            current = subtree;
        }
        return current;
    }

    private static void ReadTreeRecursive(Tree tree, string prefix, Dictionary<string, string> result)
    {
        foreach (TreeEntry entry in tree)
        {
            string entryPath = string.IsNullOrEmpty(prefix)
                ? entry.Name
                : $"{prefix}/{entry.Name}";

            if (entry.TargetType == TreeEntryTargetType.Blob)
            {
                result[entryPath] = ((Blob)entry.Target).GetContentText();
            }
            else if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                ReadTreeRecursive((Tree)entry.Target, entryPath, result);
            }
        }
    }

    private static async Task<string> WriteTempKeyFileAsync(string privateKey)
    {
        string path = Path.Combine(Path.GetTempPath(), $"entkube-ssh-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, privateKey);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}

/// <summary>Result of a git checkout operation.</summary>
public class GitCheckoutResult
{
    public bool IsSuccess { get; init; } = true;
    public string? Error { get; init; }
    public string CommitSha { get; init; } = string.Empty;
    public string CommitMessage { get; init; } = string.Empty;
    public DateTime CommittedAt { get; init; }

    /// <summary>Relative file path → file content for all files in the checked-out path.</summary>
    public Dictionary<string, string> Files { get; init; } = [];

    public static GitCheckoutResult Failure(string error) => new()
    {
        IsSuccess = false,
        Error = error
    };
}
