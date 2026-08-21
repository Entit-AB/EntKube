using System.Diagnostics;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Rollouts;

/// <summary>
/// The narrow slice of rollout handling the apply path needs: "a release just went out,
/// start watching it if it is configured to be watched".
///
/// Separated from the full <see cref="RolloutService"/> so the apply path does not depend
/// on the measurement and rollback machinery — it has no use for either, and coupling to
/// them would drag Prometheus, the trace store and the error-budget service into every
/// caller (and every test) of a plain deployment apply.
/// </summary>
public interface IRolloutStarter
{
    /// <summary>Opens a watch for a just-applied deployment, or returns null if it has no policy.</summary>
    Task<DeploymentRollout?> OpenAsync(
        Guid deploymentId, string? triggeredBy, DateTime now, CancellationToken ct = default);
}

/// <summary>A starter that does nothing — for tests and for hosts that disable rollout watching.</summary>
public sealed class NoOpRolloutStarter : IRolloutStarter
{
    public Task<DeploymentRollout?> OpenAsync(
        Guid deploymentId, string? triggeredBy, DateTime now, CancellationToken ct = default) =>
        Task.FromResult<DeploymentRollout?>(null);
}

/// <summary>
/// Opens, measures and closes rollout watches.
///
/// The watch is deliberately decoupled from the apply that starts it: the apply returns
/// as soon as kubectl does, and the verdict arrives minutes later from the background
/// watcher. Blocking a sync — or an API call, or a CI job — for a ten-minute analysis
/// window would make the feature unusable in exactly the pipelines it exists to protect.
/// </summary>
public class RolloutService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PrometheusService prometheus,
    ITraceQueryService traces,
    ErrorBudgetService errorBudgets,
    ILogger<RolloutService> logger) : IRolloutStarter
{
    /// <summary>
    /// Opens a watch for a deployment that was just applied, if it has an enabled policy.
    /// Returns null when there is no policy — most deployments have none, and that is fine.
    /// </summary>
    public async Task<DeploymentRollout?> OpenAsync(
        Guid deploymentId, string? triggeredBy, DateTime now, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        RolloutPolicy? policy = await db.RolloutPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.DeploymentId == deploymentId, ct);

        if (policy is null || !policy.IsEnabled)
        {
            return null;
        }

        // A new apply supersedes any watch still open on the same deployment: judging the
        // old release on traffic now served by the new one would be meaningless.
        List<DeploymentRollout> open = await db.DeploymentRollouts
            .Where(r => r.DeploymentId == deploymentId && r.Status == DeploymentRolloutStatus.Watching)
            .ToListAsync(ct);

        foreach (DeploymentRollout previous in open)
        {
            previous.Status = DeploymentRolloutStatus.Superseded;
            previous.FinishedAt = now;
            previous.Verdict = "A newer release was applied before this window closed.";
        }

        DeploymentRollout rollout = new()
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Status = DeploymentRolloutStatus.Watching,
            StartedAt = now,
            // Warm-up is inside the window: pods are still starting and old ones still
            // terminating, so early samples describe the rollout, not the release.
            DecideAt = now.AddMinutes(policy.WarmupMinutes + policy.AnalysisWindowMinutes),
            TriggeredBy = triggeredBy,
        };

        db.DeploymentRollouts.Add(rollout);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Rollout watch {RolloutId} opened for deployment {DeploymentId}, deciding at {DecideAt:o}",
            rollout.Id, deploymentId, rollout.DecideAt);

        return rollout;
    }

    /// <summary>Gathers whatever signals are available for a deployment over the analysis window.</summary>
    public async Task<RolloutSignals> MeasureAsync(
        AppDeployment deployment, RolloutPolicy policy, DateTime windowStart, DateTime now,
        CancellationToken ct = default)
    {
        double? readyFraction = null;
        int? restarts = null;
        long? requestCount = null;
        long? errorCount = null;
        double? latencyP95 = null;
        double? burnRate = null;

        // ── Readiness, from the health snapshots already being collected ──
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
            var snapshot = await db.DeploymentHealthSnapshots
                .AsNoTracking()
                .Where(s => s.DeploymentId == deployment.Id && s.SnapshotAt >= windowStart)
                .OrderByDescending(s => s.SnapshotAt)
                .Select(s => new { s.ReadyReplicas, s.TotalReplicas })
                .FirstOrDefaultAsync(ct);

            // A zero desired-replica count is a scaled-down deployment, not a 0% ready one.
            if (snapshot is { TotalReplicas: > 0, ReadyReplicas: not null })
            {
                readyFraction = (double)snapshot.ReadyReplicas.Value / snapshot.TotalReplicas.Value;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Readiness unavailable for rollout of deployment {DeploymentId}", deployment.Id);
        }

        // ── Restarts, from Prometheus ──
        try
        {
            KubernetesOperationResult<DeploymentMetricsSummary> metrics =
                await prometheus.GetDeploymentMetricsAsync(deployment.Id, ct);

            if (metrics.IsSuccess && metrics.Data is not null)
            {
                restarts = metrics.Data.RestartCount;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Restart count unavailable for deployment {DeploymentId}", deployment.Id);
        }

        // ── Error rate and latency, from EntKube's own trace store ──
        if (!string.IsNullOrWhiteSpace(policy.TelemetryServiceName))
        {
            try
            {
                KubernetesOperationResult<List<RedBucket>> red = await traces.GetServiceRedAsync(
                    deployment.ClusterId, policy.TelemetryServiceName, windowStart, now,
                    buckets: 12, ct);

                if (red.IsSuccess && red.Data is { Count: > 0 })
                {
                    requestCount = red.Data.Sum(b => b.Count);
                    errorCount = red.Data.Sum(b => b.Errors);

                    // Weight p95 by request volume: an idle bucket's p95 says as much about
                    // the release as a busy one only if you pretend they carry equal traffic.
                    List<RedBucket> withTraffic = [.. red.Data.Where(b => b.Count > 0)];
                    if (withTraffic.Count > 0)
                    {
                        latencyP95 = withTraffic.Sum(b => b.P95Ms * b.Count) / withTraffic.Sum(b => b.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Trace signals unavailable for deployment {DeploymentId}", deployment.Id);
            }
        }

        // ── Error-budget burn ──
        if (policy.MaxErrorBudgetBurnRate is not null)
        {
            try
            {
                await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
                Guid tenantId = await db.Apps
                    .Where(a => a.Id == deployment.AppId)
                    .Select(a => a.Customer.TenantId)
                    .FirstAsync(ct);

                List<ErrorBudgetStatus> budgets = await errorBudgets.GetTenantErrorBudgetsAsync(tenantId, ct);
                burnRate = budgets.FirstOrDefault(b => b.DeploymentId == deployment.Id)?.BurnRateRecent;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error budget unavailable for deployment {DeploymentId}", deployment.Id);
            }
        }

        return new RolloutSignals
        {
            ReadyFraction = readyFraction,
            Restarts = restarts,
            RequestCount = requestCount,
            ErrorCount = errorCount,
            LatencyP95Ms = latencyP95,
            ErrorBudgetBurnRate = burnRate,
        };
    }

    /// <summary>
    /// Rolls a workload back to its previous revision with <c>kubectl rollout undo</c>.
    ///
    /// Undo rather than re-applying an older manifest: undo asks Kubernetes to restore the
    /// revision it actually recorded, which is the state that was genuinely running.
    /// Reconstructing that from EntKube's own history would be a guess at what the cluster
    /// had, and a wrong guess during an incident is the worst possible time to find out.
    /// </summary>
    public async Task<(bool Success, string Output)> RollbackAsync(
        AppDeployment deployment, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-rollback-{Guid.NewGuid()}.kubeconfig");

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            // Every Deployment in the namespace that EntKube applied for this release is
            // undone: a release is a set of workloads, and rolling back only one of them
            // would leave the namespace in a state that never existed.
            (int listCode, string listOut, string listErr) = await RunKubectlAsync(
                $"get deployments -n {deployment.Namespace} --kubeconfig {kubeconfigPath} -o name", ct);

            if (listCode != 0)
            {
                return (false, $"Could not list workloads to roll back: {listErr.Trim()}");
            }

            string[] workloads = listOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (workloads.Length == 0)
            {
                return (false, $"No Deployments found in namespace {deployment.Namespace}.");
            }

            List<string> results = [];
            bool allSucceeded = true;

            foreach (string workload in workloads)
            {
                (int code, string output, string error) = await RunKubectlAsync(
                    $"rollout undo {workload} -n {deployment.Namespace} --kubeconfig {kubeconfigPath}", ct);

                results.Add($"{workload}: {(code == 0 ? output.Trim() : error.Trim())}");
                if (code != 0)
                {
                    allSucceeded = false;
                }
            }

            return (allSucceeded, string.Join('\n', results));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(kubeconfigPath); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>Records the verdict and closes the rollout.</summary>
    public async Task CloseAsync(
        Guid rolloutId, DeploymentRolloutStatus status, string verdict, RolloutSignals signals,
        DateTime now, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DeploymentRollout? rollout = await db.DeploymentRollouts.FirstOrDefaultAsync(r => r.Id == rolloutId, ct);
        if (rollout is null)
        {
            return;
        }

        rollout.Status = status;
        rollout.FinishedAt = now;
        rollout.Verdict = verdict.Length > 1000 ? verdict[..1000] : verdict;
        rollout.SignalsJson = JsonSerializer.Serialize(signals);

        await db.SaveChangesAsync(ct);
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
            // Closed immediately: a kubeconfig without usable credentials makes kubectl
            // prompt, and an inherited stdin never delivers the EOF that ends the prompt.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["HOME"] = "/tmp";

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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }
}
