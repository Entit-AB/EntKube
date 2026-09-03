using System.Diagnostics;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Upgrades;

/// <summary>Whether a managed target still matches what EntKube last applied.</summary>
public enum DriftState
{
    /// <summary>Live cluster state differs from the desired manifest.</summary>
    Drifted = 0,
    /// <summary>Could not be determined — cluster unreachable, no kubeconfig, kubectl missing.</summary>
    Unknown = 1,
    /// <summary>Live cluster state matches the desired manifest.</summary>
    InSync = 2,
}

/// <summary>One managed deployment measured against its cluster.</summary>
public sealed record DriftResult
{
    public required Guid DeploymentId { get; init; }
    public required string DeploymentName { get; init; }
    public required string AppName { get; init; }
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }
    public required string Namespace { get; init; }
    public string? EnvironmentName { get; init; }
    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }

    public required DriftState State { get; init; }

    /// <summary>Unified diff of desired vs live, when drifted. Truncated for display.</summary>
    public string? DiffText { get; init; }

    /// <summary>Number of changed lines in the diff — a rough size signal for ranking.</summary>
    public int ChangedLines { get; init; }

    /// <summary>Why the state is <see cref="DriftState.Unknown"/>.</summary>
    public string? Note { get; init; }

    /// <summary>Removed/soon-removed Kubernetes APIs found in this deployment's manifests.</summary>
    public IReadOnlyList<DeprecatedApiUsage> DeprecatedApis { get; init; } = [];

    /// <summary>
    /// When this row was measured. Per-row rather than per-report because a row can be
    /// re-checked on its own after an operator acts, and showing it under the whole
    /// sweep's timestamp would misdate it.
    /// </summary>
    public DateTime CheckedAt { get; init; }
}

/// <summary>A tenant-wide drift sweep.</summary>
public sealed record DriftReport
{
    public required IReadOnlyList<DriftResult> Results { get; init; }
    public required DateTime GeneratedAt { get; init; }

    public int DriftedCount => Results.Count(r => r.State == DriftState.Drifted);
    public int UnknownCount => Results.Count(r => r.State == DriftState.Unknown);
    public int InSyncCount => Results.Count(r => r.State == DriftState.InSync);

    public IEnumerable<DeprecatedApiUsage> AllDeprecatedApis => Results.SelectMany(r => r.DeprecatedApis);
}

/// <summary>
/// Detects configuration drift: resources EntKube applied that someone has since
/// changed out-of-band (a `kubectl edit`, a console tweak, another controller).
///
/// Uses the same primitive the change gate already relies on — a server-side
/// `kubectl diff` of the desired manifest — so drift and the pre-apply preview
/// agree by construction. Desired state comes from
/// <see cref="DeploymentManifestComposer"/>, shared with the apply path, so a
/// clean apply always leaves a clean drift result.
///
/// Read-only: it never writes to a cluster. Re-converging is an ordinary apply
/// through the existing gated path.
/// </summary>
public class DriftDetectionService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<DriftDetectionService> logger)
{
    /// <summary>
    /// Bounds concurrent kubectl processes. Each diff is a subprocess doing a full
    /// server-side apply dry-run, so an unbounded fan-out over a large tenant would
    /// both exhaust local file handles and hammer every API server at once.
    /// </summary>
    private const int MaxConcurrentDiffs = 4;

    /// <summary>Diff output beyond this is truncated for display; the full diff is never stored.</summary>
    private const int MaxDiffChars = 20_000;

    private static readonly TimeSpan DiffTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Sweeps every managed manifest-based deployment in the tenant. Helm-based
    /// deployments are skipped: Helm owns their reconciliation and its own drift story,
    /// and a raw manifest diff against a rendered chart would report noise as drift.
    /// </summary>
    public Task<DriftReport> GetTenantDriftAsync(
        Guid tenantId, DateTime now, CancellationToken ct = default) =>
        SweepAsync(tenantId, onlyDeploymentId: null, now, ct);

    /// <summary>
    /// Re-checks a single deployment. Used after acting on drift, so the row an operator
    /// just re-applied shows its new state immediately rather than staying stale until the
    /// next two-hourly sweep — which would leave the UI claiming drift that no longer
    /// exists, and an operator re-applying something already converged.
    ///
    /// Returns null when the deployment is not a drift target: not this tenant's, not
    /// managed, or Helm-based.
    /// </summary>
    public async Task<DriftResult?> GetDeploymentDriftAsync(
        Guid tenantId, Guid deploymentId, DateTime now, CancellationToken ct = default)
    {
        DriftReport report = await SweepAsync(tenantId, deploymentId, now, ct);
        return report.Results.FirstOrDefault();
    }

    private async Task<DriftReport> SweepAsync(
        Guid tenantId, Guid? onlyDeploymentId, DateTime now, CancellationToken ct)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DeploymentType[] manifestTypes =
            [DeploymentType.Manual, DeploymentType.Yaml, DeploymentType.GitYaml];

        var targets = await db.AppDeployments
            .AsNoTracking()
            .Where(d => d.App.Customer.TenantId == tenantId
                     && d.IsManaged
                     && manifestTypes.Contains(d.Type)
                     && (onlyDeploymentId == null || d.Id == onlyDeploymentId))
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.Namespace,
                d.ClusterId,
                ClusterName = d.Cluster.Name,
                Kubeconfig = d.Cluster.Kubeconfig,
                AppName = d.App.Name,
                CustomerId = (Guid?)d.App.CustomerId,
                CustomerName = d.App.Customer.Name,
                EnvironmentName = d.Environment.Name,
            })
            .ToListAsync(ct);

        // Manifests are loaded up front in one query rather than per deployment: the sweep
        // already pays for a subprocess per target, and N+1 round-trips on top of that turns
        // a slow scan into an unusable one.
        HashSet<Guid> targetIds = [.. targets.Select(t => t.Id)];
        var manifestsByDeployment = (await db.DeploymentManifests
                .AsNoTracking()
                .Where(m => targetIds.Contains(m.DeploymentId))
                .OrderBy(m => m.SortOrder)
                .Select(m => new { m.DeploymentId, m.YamlContent })
                .ToListAsync(ct))
            .GroupBy(m => m.DeploymentId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.YamlContent).ToList());

        // Resolve each cluster's Kubernetes version once per sweep so the removed-API scan can
        // say "already broken" rather than only "will break". One extra kubectl call per
        // cluster is negligible next to one server-side dry-run per deployment.
        Dictionary<Guid, string?> minorByCluster = [];
        foreach (var cluster in targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Kubeconfig))
            .GroupBy(t => t.ClusterId))
        {
            minorByCluster[cluster.Key] = await GetServerMinorAsync(cluster.First().Kubeconfig!, ct);
        }

        using SemaphoreSlim limiter = new(MaxConcurrentDiffs);

        IEnumerable<Task<DriftResult>> work = targets.Select(async target =>
        {
            await limiter.WaitAsync(ct);
            try
            {
                DriftResult Base(DriftState state, string? note = null) => new()
                {
                    DeploymentId = target.Id,
                    DeploymentName = target.Name,
                    AppName = target.AppName,
                    ClusterId = target.ClusterId,
                    ClusterName = target.ClusterName,
                    Namespace = target.Namespace,
                    EnvironmentName = target.EnvironmentName,
                    CustomerId = target.CustomerId,
                    CustomerName = target.CustomerName,
                    State = state,
                    Note = note,
                    CheckedAt = now,
                };

                if (!manifestsByDeployment.TryGetValue(target.Id, out List<string>? bodies) || bodies.Count == 0)
                {
                    return Base(DriftState.Unknown, "No manifests are defined for this deployment.");
                }

                if (string.IsNullOrWhiteSpace(target.Kubeconfig))
                {
                    return Base(DriftState.Unknown, "Cluster has no kubeconfig configured.");
                }

                string desired = DeploymentManifestComposer.Combine(target.Namespace, bodies);
                IReadOnlyList<DeprecatedApiUsage> deprecated = DeprecatedApiScanner.Scan(
                    desired, minorByCluster.GetValueOrDefault(target.ClusterId));

                (DriftState state, string? diff, string? note) =
                    await DiffAsync(desired, target.Kubeconfig, target.Namespace, ct);

                return Base(state, note) with
                {
                    DiffText = diff,
                    ChangedLines = CountChangedLines(diff),
                    DeprecatedApis = deprecated,
                };
            }
            finally
            {
                limiter.Release();
            }
        });

        DriftResult[] results = await Task.WhenAll(work);

        List<DriftResult> ordered = [.. results
            .OrderBy(r => r.State)
            .ThenByDescending(r => r.ChangedLines)
            .ThenBy(r => r.AppName, StringComparer.OrdinalIgnoreCase)];

        logger.LogDebug(
            "Drift sweep for tenant {TenantId}: {Total} targets, {Drifted} drifted, {Unknown} unknown",
            tenantId, ordered.Count, ordered.Count(r => r.State == DriftState.Drifted),
            ordered.Count(r => r.State == DriftState.Unknown));

        return new DriftReport { Results = ordered, GeneratedAt = now };
    }

    /// <summary>
    /// Runs a server-side <c>kubectl diff</c>. Exit 0 = in sync, exit 1 = drift on stdout,
    /// anything else = the diff could not be computed — reported as Unknown rather than as
    /// "in sync", so a broken connection never reads as a clean bill of health.
    /// </summary>
    private async Task<(DriftState State, string? Diff, string? Note)> DiffAsync(
        string desired, string kubeconfig, string ns, CancellationToken ct)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-drift-{Guid.NewGuid()}.kubeconfig");
        string manifestPath = Path.Combine(Path.GetTempPath(), $"entkube-drift-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            await File.WriteAllTextAsync(manifestPath, desired, ct);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DiffTimeout);

            (int code, string stdout, string stderr) = await RunKubectlAsync(
                $"diff -f {manifestPath} --kubeconfig {kubeconfigPath} --namespace {ns} --server-side --force-conflicts",
                timeout.Token);

            return code switch
            {
                0 => (DriftState.InSync, null, null),
                1 => (DriftState.Drifted, Truncate(stdout), null),
                _ => (DriftState.Unknown, null,
                    string.IsNullOrWhiteSpace(stderr) ? "kubectl diff failed." : stderr.Trim()),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (DriftState.Unknown, null, $"Timed out after {DiffTimeout.TotalSeconds:N0}s.");
        }
        catch (Exception ex)
        {
            return (DriftState.Unknown, null, ex.Message);
        }
        finally
        {
            Delete(kubeconfigPath);
            Delete(manifestPath);
        }
    }

    /// <summary>
    /// Reads the cluster's server version as a "major.minor" string, or null when it can't be
    /// determined. Null is not an error: the removed-API scan then reports every removal as
    /// upcoming, which is the conservative direction.
    /// </summary>
    private async Task<string?> GetServerMinorAsync(string kubeconfig, CancellationToken ct)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-ver-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            (int code, string stdout, _) = await RunKubectlAsync(
                $"version -o json --kubeconfig {kubeconfigPath}", timeout.Token);

            if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("serverVersion", out System.Text.Json.JsonElement server)
                || !server.TryGetProperty("gitVersion", out System.Text.Json.JsonElement gitVersion))
            {
                return null;
            }

            SemVer? parsed = SemVer.Parse(StripDistroSuffix(gitVersion.GetString()));
            return parsed is null ? null : $"{parsed.Major}.{parsed.Minor}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read cluster version for the removed-API scan");
            return null;
        }
        finally
        {
            Delete(kubeconfigPath);
        }
    }

    /// <summary>Trims "+k3s1" / "-eks-1234" packaging markers from a reported server version.</summary>
    private static string? StripDistroSuffix(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        string work = version.Trim();
        int plus = work.IndexOf('+');
        if (plus >= 0) work = work[..plus];
        int dash = work.IndexOf('-');
        if (dash >= 0) work = work[..dash];
        return work;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunKubectlAsync(
        string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "kubectl",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected so stdin can be closed immediately. A kubeconfig whose user has no
            // usable credentials makes kubectl PROMPT for a username; with inherited stdin
            // that prompt never receives EOF in a server process and the diff hangs until
            // the timeout — once per deployment, four at a time.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["HOME"] = "/tmp";
        // kubectl diff shells out to a differ; without this it looks for $KUBECTL_EXTERNAL_DIFF
        // or `diff`, and on a minimal container image neither choice is guaranteed.
        psi.EnvironmentVariables["KUBECTL_EXTERNAL_DIFF"] = "diff -u -N";

        using Process process = new() { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync only stops awaiting — it does not stop the process. Without an
            // explicit kill, a timed-out diff keeps running against the cluster forever and
            // every sweep leaks another one.
            TryKill(process);
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process may have exited between the check and the kill; nothing to do.
        }
    }

    /// <summary>Counts added/removed lines in a unified diff, ignoring file headers.</summary>
    public static int CountChangedLines(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return 0;
        }

        int count = 0;
        foreach (string line in diff.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // "+++"/"---" are the file headers of each hunk, not content changes.
            if (line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '+' || line[0] == '-')
            {
                count++;
            }
        }

        return count;
    }

    private static string Truncate(string text) =>
        text.Length <= MaxDiffChars
            ? text
            : text[..MaxDiffChars] + "\n… diff truncated …";

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* temp file cleanup is best-effort */ }
    }
}
