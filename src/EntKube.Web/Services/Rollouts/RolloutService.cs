using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
    IDbContextFactory<DeliveryDbContext> dbFactory,
    PrometheusService prometheus,
    EntKube.Web.Services.Telemetry.ITelemetryBackends telemetry,
    ErrorBudgetService errorBudgets,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients,
    ILogger<RolloutService> logger) : IRolloutStarter
{
    /// <summary>
    /// Opens a watch for a deployment that was just applied, if it has an enabled policy.
    /// Returns null when there is no policy — most deployments have none, and that is fine.
    /// </summary>
    public async Task<DeploymentRollout?> OpenAsync(
        Guid deploymentId, string? triggeredBy, DateTime now, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

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
        Guid tenantId, AppDeployment deployment, RolloutPolicy policy, DateTime windowStart,
        DateTime now, CancellationToken ct = default)
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
            await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);
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
        //
        // ⚠️ Over the analysis window, NOT GetDeploymentMetricsAsync's RestartCount. That one is
        // an instant sum of kube_pod_container_status_restarts_total, which is a CUMULATIVE
        // counter — every restart every container in the namespace has ever had. The dashboards
        // want that figure and it is right for them; a rollout verdict is a rate question.
        //
        // Read as a lifetime total it made the restart check permanently unsatisfiable: with
        // MaxRestarts defaulting to 3, any namespace that had ever accumulated four container
        // restarts breached on EVERY subsequent rollout, however healthy the new version was —
        // and RolloutWatcherService rolls back automatically on a breach, so deploys into a
        // mature namespace were undone unattended and the verdict read "1247 container restarts
        // (limit 3)", which is absurd on its face but internally consistent.
        //
        // This method is handed windowStart and now, and already uses them for the error-rate and
        // latency signals. The restart signal was the one that did not.
        try
        {
            KubernetesOperationResult<int> inWindow = await prometheus.GetRestartsInWindowAsync(
                tenantId, deployment.Id, now - windowStart, ct);

            if (inWindow.IsSuccess)
            {
                restarts = inWindow.Data;
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
                // The analysis reads the deployment's own telemetry as the tenant whose rollout this
                // is — the same tenant already supplied for the metrics above and the cluster client below.
                KubernetesOperationResult<List<RedBucket>> red = await telemetry.TracesFor(tenantId)
                    .GetServiceRedAsync(
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
                // The tenant the watcher is acting as, not one read back off the deployment:
                // the caller selected this rollout under that tenant, so re-deriving it here
                // would only ask the same row to vouch for itself.
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
    /// Rolls a workload back with <c>kubectl rollout undo</c>, naming the revision it wants.
    ///
    /// <para>Undo rather than re-applying an older manifest: undo asks Kubernetes to restore the
    /// revision it actually recorded, which is the state that was genuinely running.
    /// Reconstructing that from EntKube's own history would be a guess at what the cluster
    /// had, and a wrong guess during an incident is the worst possible time to find out.</para>
    ///
    /// <para>⭐ <b>The undo is pinned, because a relative one is only right by luck.</b> The seam
    /// used to run <c>rollout undo &lt;workload&gt; -n &lt;ns&gt;</c> with no
    /// <c>--to-revision</c>, which means "one step back from wherever you are at the moment this
    /// runs". That is correct only if nothing else has moved the workload since the watch opened.
    /// An operator undoing by hand while the watcher judges, a retry after a partial failure, or
    /// any second undo from any source moves it <em>again</em> — off the good revision and back on
    /// to the bad one — and reports success. The judgement claim stops two EntKube instances
    /// doing it; it cannot stop a human, a retry, or anything outside this process. See
    /// <c>KubernetesClientFactory.PlanNamespaceUndo</c> for the pinning and for the refusal that
    /// leaves an already-rolled-back workload alone.</para>
    ///
    /// <para>⚠️ <b>The revision this watch started from is not recorded anywhere.</b>
    /// <see cref="DeploymentRollout"/> holds <c>StartedAt</c>, <c>DecideAt</c>, <c>TriggeredBy</c>,
    /// the verdict and the signals — no revision number, and <see cref="OpenAsync"/> reads no
    /// cluster state at all, so there was never a moment at which one was observed. Capturing it
    /// would be a schema change plus a cluster read on the apply path. Until then the seam pins
    /// "the revision below the one running now", read at rollback time, and <b>refuses</b> rather
    /// than inventing a number when it cannot establish one. Do not read a pinned rollback as a
    /// guarantee that the workload returns to precisely what this watch saw go out.</para>
    /// </summary>
    public async Task<(bool Success, string Output)> RollbackAsync(
        Guid tenantId, AppDeployment deployment, CancellationToken ct = default)
    {
        try
        {
            EntKube.Web.Services.Clusters.IClusterClient? cluster =
                await clusterClients.ForAsync(tenantId, deployment.ClusterId, ct);

            if (cluster is null)
            {
                return (false,
                    "The deployment's cluster is not this tenant's, or has no kubeconfig configured.");
            }

            // The namespace is the unit: a release is a set of workloads, and rolling back only
            // one of them would leave the namespace in a state that never existed. The seam owns
            // the credential and the acknowledgment; what belongs here is the decision to undo.
            EntKube.Web.Services.Clusters.WorkloadUndoResult undone = await cluster.UndoDeploymentsAsync(
                deployment.Namespace,
                $"Roll back {deployment.Name} in {deployment.Namespace} after a failed release", ct);

            return (undone.Success, undone.Output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Claims a rollout for judgement, returning true only to the caller that won it.
    ///
    /// <para>⭐ <b>This is the whole multi-instance fix.</b> Judging a rollout is not a read:
    /// it ends in <c>kubectl rollout undo</c> against production. Two EntKube instances both
    /// poll on a one-minute tick, so both find the same rollout <c>Watching</c> and due, both
    /// measure the same failing window, and both undo. The second undo rolls <em>forward</em>
    /// again — <c>rollout undo</c> with no <c>--to-revision</c> moved to the previous revision,
    /// and after the first undo the previous revision is the bad release. The automatic
    /// rollback therefore cancelled itself out, the bad release stayed live, and both
    /// instances wrote a verdict saying it had been rolled back. That verdict is the worst
    /// part: it is a record of something that did not happen.</para>
    ///
    /// <para>⚠️ The undo is no longer relative — see <see cref="RollbackAsync"/> — so a second one
    /// is now refused at the seam rather than rolling forward. The claim is still load-bearing and
    /// is the wrong thing to remove: without it two instances still both measure, both decide, and
    /// both write a verdict, and the seam's refusal would make the loser's verdict read "already
    /// rolled back" for a rollback it did not perform.</para>
    ///
    /// <para>The claim is a compare-and-set on <see cref="DeploymentRollout.DecideAt"/>, modelled
    /// directly on <c>CostLedgerWriter</c>'s per-cluster period cursor — the one other claim in
    /// this codebase, which exists for the same reason (never do the costly thing twice) and in
    /// the same shape: <c>Where(unchanged-since-I-read-it).ExecuteUpdateAsync(advance it)</c>,
    /// with the loser taking <c>0</c> rows back and doing nothing at all.</para>
    ///
    /// <para>⚠️ This is a claim, not a lock. Nothing blocks, nothing is held, and losing is not
    /// an error — a caller that loses has simply had its work done for it. There is no leader
    /// election, no advisory lock and no lease table anywhere in EntKube, and this does not
    /// introduce one.</para>
    ///
    /// <para>⚠️ <paramref name="decideAt"/> must be the value the caller itself read, not a
    /// recomputed one. That is what makes the update a compare-and-set rather than a plain
    /// write: if another instance moved the deadline between the read and here, no row matches
    /// and we lose.</para>
    ///
    /// <para><paramref name="claimUntil"/> doubles as the re-arm. The deadline moves into the
    /// future, so the winner's siblings stop seeing the rollout as due, and if the winner dies
    /// mid-judgement the rollout becomes due again once the claim expires rather than being
    /// stranded in <c>Watching</c> forever. ⚠️ A consequence worth knowing: a judgement that
    /// crashes repeatedly pushes <c>DecideAt</c> forward each time, so the watcher's "too
    /// overdue to judge" horizon is measured from the last attempt, not from the original
    /// deadline. That is the direction we want to fail in — a retried verdict beats an
    /// abandoned one — but it does mean <c>DecideAt</c> is not a historical record of when the
    /// window first closed.</para>
    /// </summary>
    public async Task<bool> TryClaimForJudgementAsync(
        Guid rolloutId, DateTime decideAt, DateTime claimUntil, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        int claimed = await db.DeploymentRollouts
            .Where(r => r.Id == rolloutId
                && r.Status == DeploymentRolloutStatus.Watching
                && r.DecideAt == decideAt)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DecideAt, claimUntil), ct);

        return claimed > 0;
    }

    /// <summary>
    /// Records the verdict and closes the rollout. Returns true if this call is the one that
    /// closed it, false if it was already closed by someone else.
    ///
    /// <para>The write is a compare-and-set on <c>Status == Watching</c>. It used to be a
    /// read-modify-write with nothing between the two halves:</para>
    /// <code>
    /// DeploymentRollout? rollout = await db.DeploymentRollouts.FirstOrDefaultAsync(...);
    /// if (rollout is null) return;
    /// rollout.Status = status;          // ← two instances both get here
    /// await db.SaveChangesAsync(ct);    // ← and the later one wins
    /// </code>
    ///
    /// <para>With the judgement claimed this is belt and braces against a second watcher, but
    /// it is load-bearing against <see cref="OpenAsync"/>: a new apply landing mid-analysis
    /// marks the open watch <c>Superseded</c>, and without the guard the watcher would then
    /// overwrite that with a verdict about a release no longer running. Losing here is correct
    /// — whoever closed it first closed it for a reason.</para>
    /// </summary>
    public async Task<bool> CloseAsync(
        Guid rolloutId, DeploymentRolloutStatus status, string verdict, RolloutSignals signals,
        DateTime now, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Computed out here rather than inside the expression tree: SetProperty takes the value
        // as a constant to parameterize, not as something to translate into SQL.
        string trimmed = verdict.Length > 1000 ? verdict[..1000] : verdict;
        string signalsJson = JsonSerializer.Serialize(signals);

        int closed = await db.DeploymentRollouts
            .Where(r => r.Id == rolloutId && r.Status == DeploymentRolloutStatus.Watching)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.FinishedAt, (DateTime?)now)
                .SetProperty(r => r.Verdict, trimmed)
                .SetProperty(r => r.SignalsJson, signalsJson), ct);

        return closed > 0;
    }
}
