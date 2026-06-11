using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Background service that periodically pulls Git repositories and reconciles
/// AppDeployment manifests/Helm values from the latest commit.
///
/// For each deployment with a GitRepositoryId and GitAutoSync=true:
/// 1. Fetches the HEAD commit SHA for the configured revision.
/// 2. If the commit matches GitLastSyncedCommit, skips (already current).
/// 3. Otherwise reads files from the repo and applies changes:
///    - GitYaml: replaces DeploymentManifest rows with parsed YAML documents.
///    - GitHelm: updates HelmValues.
///    - GitAppOfApps: calls AppOfAppsService to reconcile child deployments.
/// 4. Updates GitLastSyncedCommit and GitLastSyncedAt.
///
/// Runs every 3 minutes by default. A deployment can also be synced on-demand
/// via <see cref="SyncDeploymentAsync"/> (called from the UI or webhook).
/// </summary>
public class GitSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<GitSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InterDeploymentDelay = TimeSpan.FromMilliseconds(300);

    // Deployments can be queued for immediate sync (e.g. webhook or UI button).
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _syncQueue = new();

    public void EnqueueSync(Guid deploymentId) => _syncQueue.Enqueue(deploymentId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Drain the immediate queue first.
            while (_syncQueue.TryDequeue(out Guid queued))
            {
                try { await SyncDeploymentAsync(queued, stoppingToken); }
                catch (Exception ex) { logger.LogWarning(ex, "GitSyncService: queue sync failed for {Id}", queued); }
            }

            await SyncAllAutoSyncDeploymentsAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task SyncAllAutoSyncDeploymentsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        List<Guid> deploymentIds;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            DeploymentType[] gitTypes =
            [
                DeploymentType.GitYaml,
                DeploymentType.GitHelm,
                DeploymentType.GitAppOfApps
            ];

            deploymentIds = await db.AppDeployments
                .Where(d => (d.GitUrl != null || d.GitRepositoryId != null)
                    && d.GitAutoSync
                    && gitTypes.Contains(d.Type))
                .Select(d => d.Id)
                .ToListAsync(ct);
        }

        logger.LogDebug("GitSyncService: checking {Count} git-backed deployments", deploymentIds.Count);

        foreach (Guid id in deploymentIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await SyncDeploymentAsync(id, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GitSyncService: unhandled error syncing {Id}", id);
            }

            await Task.Delay(InterDeploymentDelay, ct);
        }
    }

    /// <summary>
    /// Syncs a single deployment from its Git source. Safe to call directly from
    /// the UI or webhook handler for on-demand sync.
    /// </summary>
    public async Task<GitSyncResult> SyncDeploymentAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var gitOps = scope.ServiceProvider.GetRequiredService<GitOperationsService>();
        var gitService = scope.ServiceProvider.GetRequiredService<CustomerGitService>();
        var appOfApps = scope.ServiceProvider.GetRequiredService<AppOfAppsService>();

        AppDeployment? deployment;
        GitRepository repo;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            deployment = await db.AppDeployments
                .Include(d => d.GitRepository)
                .Include(d => d.App)
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        }

        if (deployment is null)
            return GitSyncResult.Failure("Deployment not found.");

        // Resolve the effective repo: URL-based deployments build a virtual GitRepository
        // from the matching customer credential; legacy deployments use the stored GitRepository.
        Guid? credentialId;
        if (deployment.GitUrl is not null)
        {
            CustomerGitCredential? cred = await gitService.FindMatchingCredentialAsync(
                deployment.App.CustomerId, deployment.EnvironmentId, deployment.GitUrl, ct);
            credentialId = cred?.Id;
            repo = new GitRepository
            {
                Id = deployment.Id,
                TenantId = cred?.TenantId ?? Guid.Empty,
                Name = deployment.Name,
                Url = deployment.GitUrl,
                AuthType = cred?.AuthType ?? GitAuthType.None,
                Username = cred?.Username,
                DefaultBranch = "main"
            };
        }
        else if (deployment.GitRepository is not null)
        {
            repo = deployment.GitRepository;
            CustomerGitCredential? envCredential = await gitService.FindMatchingCredentialAsync(
                deployment.App.CustomerId, deployment.EnvironmentId, repo.Url, ct);
            credentialId = envCredential?.Id;
        }
        else
        {
            return GitSyncResult.Failure("Deployment has no Git repository configured.");
        }

        // Namespace governance check — block sync if the deployment targets a locked namespace it isn't in.
        using (ApplicationDbContext dbNs = dbFactory.CreateDbContext())
        {
            AppEnvironment? ae = await dbNs.AppEnvironments
                .FirstOrDefaultAsync(e =>
                    e.AppId == deployment.AppId &&
                    e.EnvironmentId == deployment.EnvironmentId, ct);

            if (ae?.Namespace is { Length: > 0 } locked &&
                !string.Equals(deployment.Namespace.Trim(), locked, StringComparison.OrdinalIgnoreCase))
            {
                return GitSyncResult.Failure(
                    $"Namespace governance violation: deployment targets '{deployment.Namespace}' " +
                    $"but governance policy locks this environment to '{locked}'.");
            }
        }

        string revision = deployment.GitRevision ?? repo.DefaultBranch;
        string path = deployment.GitPath ?? ".";

        // Quick HEAD check — skip checkout if already up to date.
        string headSha;
        try
        {
            string? sha = await gitOps.GetHeadCommitAsync(repo, revision, credentialId, ct);
            if (sha is null)
                return GitSyncResult.Failure("Could not fetch HEAD commit from repository.");
            headSha = sha;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GitSyncService: HEAD fetch failed for deployment {Id}", deploymentId);
            return GitSyncResult.Failure($"Git error: {ex.Message}");
        }

        if (headSha == deployment.GitLastSyncedCommit)
            return new GitSyncResult { AlreadyCurrent = true, CommitSha = headSha };

        // Full checkout.
        GitCheckoutResult checkout = await gitOps.CheckoutFilesAsync(repo, path, revision, credentialId, ct);

        if (!checkout.IsSuccess)
            return GitSyncResult.Failure(checkout.Error ?? "Checkout failed.");

        // Apply changes based on deployment type.
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            // Re-fetch to get a tracked entity.
            AppDeployment tracked = await db.AppDeployments
                .Include(d => d.Manifests)
                .FirstAsync(d => d.Id == deploymentId, ct);

            switch (tracked.Type)
            {
                case DeploymentType.GitYaml:
                    await ApplyYamlManifestsAsync(db, tracked, checkout.Files, ct);
                    break;

                case DeploymentType.GitHelm:
                    ApplyHelmValues(tracked, checkout.Files, path);
                    break;

                case DeploymentType.GitAppOfApps:
                    await appOfApps.ReconcileAsync(tracked, checkout.Files, ct);
                    break;
            }

            tracked.GitLastSyncedCommit = checkout.CommitSha;
            tracked.GitLastSyncedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "GitSyncService: synced deployment {Name} to commit {Sha}",
            deployment.Name, checkout.CommitSha[..Math.Min(7, checkout.CommitSha.Length)]);

        // Push the updated manifests/values to the cluster so the cluster stays
        // in sync with Git. Without this step the DB is updated but the cluster
        // keeps running the old spec.
        var k8sOps = scope.ServiceProvider.GetRequiredService<KubernetesOperationsService>();
        string? applyOutput = null;
        bool applySuccess = true;

        try
        {
            switch (deployment.Type)
            {
                case DeploymentType.GitYaml:
                {
                    KubernetesOperationResult<string> applyResult =
                        await k8sOps.ApplyYamlDeploymentAsync(deploymentId, "git-sync", ct);
                    applySuccess = applyResult.IsSuccess;
                    applyOutput = applyResult.IsSuccess ? applyResult.Data : applyResult.Error;
                    if (!applyResult.IsSuccess)
                        logger.LogWarning("GitSyncService: cluster apply failed for {Name}: {Error}",
                            deployment.Name, applyResult.Error);
                    break;
                }
                case DeploymentType.GitHelm:
                {
                    KubernetesOperationResult<string> helmResult =
                        await k8sOps.HelmInstallOrUpgradeAsync(deploymentId, ct: ct);
                    applySuccess = helmResult.IsSuccess;
                    applyOutput = helmResult.IsSuccess ? helmResult.Data : helmResult.Error;
                    if (!helmResult.IsSuccess)
                        logger.LogWarning("GitSyncService: helm upgrade failed for {Name}: {Error}",
                            deployment.Name, helmResult.Error);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            applySuccess = false;
            applyOutput = ex.Message;
            logger.LogWarning(ex, "GitSyncService: cluster apply threw for {Name}", deployment.Name);
        }

        return new GitSyncResult
        {
            CommitSha = checkout.CommitSha,
            CommitMessage = checkout.CommitMessage,
            CommittedAt = checkout.CommittedAt,
            ApplySuccess = applySuccess,
            ApplyOutput = applyOutput
        };
    }

    // ── Apply helpers ────────────────────────────────────────────────────────────

    private static async Task ApplyYamlManifestsAsync(
        ApplicationDbContext db,
        AppDeployment deployment,
        Dictionary<string, string> files,
        CancellationToken ct)
    {
        // Remove existing manifests and replace with the current set from Git.
        db.DeploymentManifests.RemoveRange(deployment.Manifests);

        List<(string Path, string Content)> yamlFiles = files
            .Where(f => f.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.Key.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key)
            .Select(f => (f.Key, f.Value))
            .ToList();

        int order = 0;

        foreach ((string filePath, string content) in yamlFiles)
        {
            // Split multi-document YAML files.
            foreach (string doc in content.Split("\n---", StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = doc.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Extract kind and name for labelling — best-effort parsing.
                (string kind, string name) = ExtractKindAndName(trimmed);

                db.DeploymentManifests.Add(new DeploymentManifest
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deployment.Id,
                    Name = name,
                    Kind = kind,
                    YamlContent = trimmed,
                    SortOrder = order++,
                    SourceFile = filePath
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static void ApplyHelmValues(
        AppDeployment deployment,
        Dictionary<string, string> files,
        string chartPath)
    {
        // Look for values.yaml in the chart path.
        string key = string.IsNullOrEmpty(chartPath) || chartPath == "."
            ? "values.yaml"
            : $"{chartPath.TrimEnd('/')}/values.yaml";

        if (files.TryGetValue(key, out string? values))
        {
            deployment.HelmValues = values;
        }
    }

    private static (string Kind, string Name) ExtractKindAndName(string yaml)
    {
        string kind = "Unknown";
        string name = "unnamed";

        foreach (string line in yaml.Split('\n'))
        {
            if (line.StartsWith("kind:", StringComparison.Ordinal))
                kind = line["kind:".Length..].Trim();
            else if (line.TrimStart().StartsWith("name:", StringComparison.Ordinal)
                && name == "unnamed")
                name = line.Split(':')[1].Trim().Trim('"');
        }

        return (kind, name);
    }
}

public class GitSyncResult
{
    public bool IsSuccess { get; init; } = true;
    public bool AlreadyCurrent { get; init; }
    public string? Error { get; init; }
    public string CommitSha { get; init; } = string.Empty;
    public string CommitMessage { get; init; } = string.Empty;
    public DateTime CommittedAt { get; init; }
    public bool ApplySuccess { get; init; } = true;
    public string? ApplyOutput { get; init; }

    public static GitSyncResult Failure(string error) => new()
    {
        IsSuccess = false,
        Error = error
    };
}
