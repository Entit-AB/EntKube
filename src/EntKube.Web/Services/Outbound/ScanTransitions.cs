using EntKube.Web.Services.Dr;
using EntKube.Web.Services.Upgrades;

namespace EntKube.Web.Services.Outbound;

/// <summary>
/// What a sweep should tell a tenant about, given what it found this time and what it found last
/// time. Pure, so the rule can be read and tested without a cluster, a database or a channel.
///
/// <para><b>Why the comparison exists at all.</b> Drift and DR gaps are <em>states</em>, not events:
/// a component drifted an hour ago is still drifted now, and a cluster with no backups will have no
/// backups on the next sweep too. Notifying on the state means a channel that fires every few
/// minutes for as long as the problem lasts, which is how an operator learns to mute the channel —
/// and the rollback alert gets muted with it. Both are already in the Advisor and its daily digest;
/// what a channel adds is the moment something <em>became</em> true.</para>
///
/// <para><b>Nothing is said on the first sweep.</b> The caches are per-process, so the first sweep
/// after a restart has no previous state to compare against and everything already wrong would read
/// as new. A restart is not an event that happened to a cluster, and announcing one as though it
/// were would make every deploy of EntKube itself page somebody.</para>
///
/// <para><b>Nothing is said when a problem clears.</b> "Drift resolved" is information the digest
/// carries and an interruption nobody needs — and a channel that speaks on both edges is twice as
/// easy to mute. The precedent is #5's rollout outcomes, where promoted and superseded stay
/// silent.</para>
/// </summary>
public static class ScanTransitions
{
    /// <summary>
    /// Deployments that drifted between two sweeps, as (name, cluster) pairs for the message.
    /// Empty when there is nothing to say — including on the first sweep.
    /// </summary>
    public static IReadOnlyList<DriftResult> NewlyDrifted(DriftReport? previous, DriftReport current)
    {
        if (previous is null) return [];

        HashSet<Guid> wasDrifted =
        [
            .. previous.Results.Where(r => r.State == DriftState.Drifted).Select(r => r.DeploymentId)
        ];

        return
        [
            .. current.Results
                .Where(r => r.State == DriftState.Drifted && !wasDrifted.Contains(r.DeploymentId))
                .OrderBy(r => r.ClusterName, StringComparer.Ordinal)
                .ThenBy(r => r.DeploymentName, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// DR gaps that opened between two sweeps, keyed by <see cref="DrGap.Key"/> — the same identity
    /// the Advisor uses, so a gap cannot be "new" here and old there.
    ///
    /// <para><c>Info</c> gaps are left out on purpose: they are advice ("you have never exercised a
    /// restore"), not something that just broke, and they are what the digest is for.</para>
    /// </summary>
    public static IReadOnlyList<(ClusterDrStatus Cluster, DrGap Gap)> NewlyOpenedGaps(
        IReadOnlyList<ClusterDrStatus>? previous,
        IReadOnlyList<ClusterDrStatus> current,
        DateTime now)
    {
        if (previous is null) return [];

        HashSet<string> before =
        [
            .. previous.SelectMany(s => DrReadiness.Evaluate(s, now)).Select(g => g.Key)
        ];

        return
        [
            .. current
                .SelectMany(s => DrReadiness.Evaluate(s, now).Select(g => (Cluster: s, Gap: g)))
                .Where(x => x.Gap.Severity != DrSeverity.Info && !before.Contains(x.Gap.Key))
                .OrderBy(x => x.Cluster.ClusterName, StringComparer.Ordinal)
                .ThenBy(x => x.Gap.Key, StringComparer.Ordinal)
        ];
    }

    /// <summary>One message for a tenant's newly drifted deployments, or null when there are none.</summary>
    public static (string Title, string Body, string Severity)? DriftMessage(
        IReadOnlyList<DriftResult> newlyDrifted)
    {
        if (newlyDrifted.Count == 0) return null;

        string title = newlyDrifted.Count == 1
            ? $"Drift detected — {newlyDrifted[0].DeploymentName}"
            : $"Drift detected on {newlyDrifted.Count} deployments";

        string body = string.Join("\n", newlyDrifted.Select(r =>
            $"• {r.DeploymentName} ({r.AppName}) in {r.Namespace} on {r.ClusterName}"));

        // Warning, not critical: a drifted component is still serving. It means the cluster no
        // longer matches what EntKube would apply, which is a thing to look at today and not a
        // thing to wake someone for.
        return (title, body + "\n\nLive state no longer matches the desired manifest.", "warning");
    }

    /// <summary>One message for a tenant's newly opened DR gaps, or null when there are none.</summary>
    public static (string Title, string Body, string Severity)? DrMessage(
        IReadOnlyList<(ClusterDrStatus Cluster, DrGap Gap)> opened)
    {
        if (opened.Count == 0) return null;

        bool critical = opened.Any(x => x.Gap.Severity == DrSeverity.Critical);

        string title = opened.Count == 1
            ? $"Backup gap — {opened[0].Cluster.ClusterName}: {opened[0].Gap.Title}"
            : $"{opened.Count} backup gaps opened";

        string body = string.Join("\n\n", opened.Select(x =>
            $"• {x.Cluster.ClusterName}: {x.Gap.Title}\n  {x.Gap.Detail}"
            + (x.Gap.Remediation is null ? "" : $"\n  → {x.Gap.Remediation}")));

        // A cluster with nothing to restore from is the one DR state worth interrupting someone
        // for: the exposure is total and every hour of it is more data that cannot come back. The
        // Advisor already draws this line with AdvisorHorizon.Today; this follows it.
        return (title, body, critical ? "critical" : "warning");
    }
}
