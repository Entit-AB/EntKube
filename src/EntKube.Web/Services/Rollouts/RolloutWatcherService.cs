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
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

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
                await DecideAsync(rolloutId, now, dbFactory, rollouts, notifications, ct);
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
        IDbContextFactory<ApplicationDbContext> dbFactory, RolloutService rollouts,
        NotificationService notifications, CancellationToken ct)
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

            DeploymentRolloutStatus rollbackStatus = ok
                ? DeploymentRolloutStatus.RolledBack
                : DeploymentRolloutStatus.RollbackFailed;

            string rollbackVerdict = ok
                ? $"{verdict} — rolled back automatically. {output}"
                : $"{verdict} — ROLLBACK FAILED: {output}";

            await rollouts.CloseAsync(rolloutId, rollbackStatus, rollbackVerdict, signals, now, ct);
            await NotifyAsync(notifications, dbFactory, deployment, rollbackStatus, rollbackVerdict, ct);
            return;
        }

        if (outcome == DeploymentRolloutStatus.Alerted)
        {
            logger.LogWarning(
                "Rollout of deployment {DeploymentId} failed analysis (no rollback configured): {Verdict}",
                deployment.Id, verdict);
        }

        await rollouts.CloseAsync(rolloutId, outcome, verdict, signals, now, ct);
        await NotifyAsync(notifications, dbFactory, deployment, outcome, verdict, ct);
    }

    /// <summary>
    /// Which rollout outcomes are worth telling someone about, and how loudly.
    /// Null means stay quiet.
    ///
    /// Exposed and pure so the choice is testable — getting it wrong in either
    /// direction is costly: too quiet and an automatic rollback goes unnoticed, too
    /// loud and the channel is muted, which silences the rollback notification too.
    /// </summary>
    public static string? NotificationSeverityFor(DeploymentRolloutStatus outcome) => outcome switch
    {
        // The rollback itself failed: the bad release is still live AND the automatic
        // remedy did not work, so this needs a person now.
        DeploymentRolloutStatus.RollbackFailed => "critical",
        // Production was reverted without a human deciding to. Even though the outcome is
        // the safe one, nobody should discover it by reading a dashboard days later.
        DeploymentRolloutStatus.RolledBack => "critical",
        DeploymentRolloutStatus.Alerted => "warning",
        DeploymentRolloutStatus.Inconclusive => "info",
        // Promoted and Superseded stay silent. A channel that fires on every successful
        // deploy gets muted within a week, and the rollback notification goes with it.
        _ => null,
    };

    /// <summary>
    /// Tells someone what happened to a release.
    ///
    /// This is what makes the policy's "Alert" failure action mean anything: without it
    /// the setting sits in the UI next to "Roll back" implying somebody gets told, and
    /// all it does is write a log line nobody is watching at 3am. The same applies to an
    /// automatic rollback — production was just changed without a human, which is
    /// precisely the thing a human needs to hear about.
    ///
    /// A promoted or superseded release notifies nothing: a channel that fires on every
    /// successful deploy is muted within a week, and then the rollback notification is
    /// muted along with it.
    /// </summary>
    private async Task NotifyAsync(
        NotificationService notifications, IDbContextFactory<ApplicationDbContext> dbFactory,
        AppDeployment deployment, DeploymentRolloutStatus outcome, string verdict, CancellationToken ct)
    {
        string? severity = NotificationSeverityFor(outcome);

        if (severity is null)
        {
            return;
        }

        try
        {
            Guid tenantId;
            await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
            {
                tenantId = await db.Apps
                    .Where(a => a.Id == deployment.AppId)
                    .Select(a => a.Customer.TenantId)
                    .FirstAsync(ct);
            }

            string headline = outcome switch
            {
                DeploymentRolloutStatus.RollbackFailed => "Automatic rollback FAILED",
                DeploymentRolloutStatus.RolledBack => "Release rolled back automatically",
                DeploymentRolloutStatus.Alerted => "Release failed its analysis",
                _ => "Release could not be verified",
            };

            await notifications.DispatchDigestAsync(
                tenantId,
                $"{headline} — {deployment.Name}",
                $"Deployment: {deployment.Name}\nNamespace: {deployment.Namespace}\n\n{verdict}",
                severity, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The verdict is already recorded on the rollout, so a failed notification
            // loses the alert but never the fact of what happened.
            logger.LogWarning(ex, "Could not notify on rollout outcome for deployment {DeploymentId}", deployment.Id);
        }
    }
}
