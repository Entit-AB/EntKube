using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Rollouts;

/// <summary>
/// Closes rollout watches whose analysis window has expired: measures, judges, and
/// acts — promoting, alerting, or rolling back.
///
/// The rollback path runs here, in a background scope, on purpose. The cluster change
/// gate is scoped and only blocks where an interactive acknowledgment sink is
/// registered, so a background scope passes straight through. That is the behaviour we
/// want: an automatic rollback that waited for a human to click a dialog would be
/// useless at 3am, which is exactly when it matters.
/// </summary>
public class RolloutWatcherService(
    IServiceScopeFactory scopeFactory,
    ILogger<RolloutWatcherService> logger) : BackgroundService
{
    /// <summary>
    /// Polled rather than scheduled per rollout: analysis windows are minutes long, so a
    /// one-minute tick decides within a minute of the deadline without holding timers for
    /// releases that may be superseded before they ever expire.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A watch older than this is abandoned rather than judged. If the process was down
    /// for hours, the window's traffic is long gone and any verdict would be about a
    /// period nobody observed.
    /// </summary>
    private static readonly TimeSpan MaxOverdue = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Rollout watcher cycle failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;

        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var rollouts = scope.ServiceProvider.GetRequiredService<RolloutService>();

        List<Guid> due;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            due = await db.DeploymentRollouts
                .Where(r => r.Status == DeploymentRolloutStatus.Watching && r.DecideAt <= now)
                .Select(r => r.Id)
                .ToListAsync(ct);
        }

        foreach (Guid rolloutId in due)
        {
            try
            {
                await DecideAsync(rolloutId, now, dbFactory, rollouts, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not decide rollout {RolloutId}", rolloutId);
            }
        }
    }

    private async Task DecideAsync(
        Guid rolloutId, DateTime now,
        IDbContextFactory<ApplicationDbContext> dbFactory, RolloutService rollouts, CancellationToken ct)
    {
        AppDeployment? deployment;
        RolloutPolicy? policy;
        DateTime startedAt;
        DateTime decideAt;

        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            DeploymentRollout? rollout = await db.DeploymentRollouts
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == rolloutId, ct);

            if (rollout is null || rollout.Status != DeploymentRolloutStatus.Watching)
            {
                return;
            }

            startedAt = rollout.StartedAt;
            decideAt = rollout.DecideAt;

            deployment = await db.AppDeployments
                .Include(d => d.Cluster)
                .FirstOrDefaultAsync(d => d.Id == rollout.DeploymentId, ct);

            policy = await db.RolloutPolicies
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.DeploymentId == rollout.DeploymentId, ct);
        }

        if (deployment is null || policy is null)
        {
            await rollouts.CloseAsync(rolloutId, DeploymentRolloutStatus.Superseded,
                "The deployment or its rollout policy no longer exists.", new RolloutSignals(), now, ct);
            return;
        }

        if (now - decideAt > MaxOverdue)
        {
            await rollouts.CloseAsync(rolloutId, DeploymentRolloutStatus.Inconclusive,
                "The analysis window expired while EntKube was not running, so the release was never observed.",
                new RolloutSignals(), now, ct);
            return;
        }

        // Measure from the end of warm-up, not from the apply: pods restarting during the
        // rollout itself would otherwise be counted against the new release.
        DateTime windowStart = startedAt.AddMinutes(policy.WarmupMinutes);

        RolloutSignals signals = await rollouts.MeasureAsync(deployment, policy, windowStart, now, ct);
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(signals, policy);
        DeploymentRolloutStatus outcome = RolloutAnalysis.Decide(judgement, policy);

        string verdict = judgement.Summary;

        if (outcome == DeploymentRolloutStatus.RolledBack)
        {
            if (string.IsNullOrWhiteSpace(deployment.Cluster?.Kubeconfig))
            {
                await rollouts.CloseAsync(rolloutId, DeploymentRolloutStatus.RollbackFailed,
                    $"{verdict} — rollback could not run: the cluster has no kubeconfig.", signals, now, ct);
                return;
            }

            logger.LogWarning(
                "Rolling back deployment {DeploymentId} automatically: {Verdict}", deployment.Id, verdict);

            (bool ok, string output) = await rollouts.RollbackAsync(deployment, deployment.Cluster.Kubeconfig, ct);

            await rollouts.CloseAsync(
                rolloutId,
                ok ? DeploymentRolloutStatus.RolledBack : DeploymentRolloutStatus.RollbackFailed,
                ok ? $"{verdict} — rolled back automatically. {output}"
                   : $"{verdict} — ROLLBACK FAILED: {output}",
                signals, now, ct);
            return;
        }

        if (outcome == DeploymentRolloutStatus.Alerted)
        {
            logger.LogWarning(
                "Rollout of deployment {DeploymentId} failed analysis (no rollback configured): {Verdict}",
                deployment.Id, verdict);
        }

        await rollouts.CloseAsync(rolloutId, outcome, verdict, signals, now, ct);
    }
}
