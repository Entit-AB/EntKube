namespace EntKube.Web.Services.Dr;

/// <summary>A single thing wrong with a cluster's disaster-recovery posture.</summary>
public sealed record DrGap
{
    /// <summary>Stable key so the advisor can track and snooze the finding.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required DrSeverity Severity { get; init; }
    public string? Remediation { get; init; }
}

public enum DrSeverity
{
    /// <summary>There is no usable backup. A cluster loss right now is unrecoverable.</summary>
    Critical = 0,
    /// <summary>Backups exist but something is degrading them.</summary>
    Warning = 1,
    /// <summary>Worth fixing, no immediate exposure.</summary>
    Info = 2,
}

/// <summary>
/// Judges a cluster's disaster-recovery posture.
///
/// Pure, so the rules that decide whether someone is told their cluster is
/// unrecoverable can be checked without a cluster.
///
/// The judgement that shapes this: <b>a backup that exists is not a backup that
/// works</b>. Velero reports PartiallyFailed backups with a completion timestamp, so
/// anything that only asks "did it finish" treats a backup that silently skipped
/// resources as a success — and that gets discovered during a restore, which is the
/// worst possible moment. Only a clean, error-free Completed backup counts here.
/// </summary>
public static class DrReadiness
{
    /// <summary>
    /// A cluster with no successful backup in this long is treated as unprotected.
    /// Set against a daily schedule with room for one missed run, so a single transient
    /// failure does not raise a critical finding.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);

    /// <summary>
    /// Restores are expected to be exercised at least this often. Untested backups have
    /// a long history of turning out not to restore.
    /// </summary>
    public static readonly TimeSpan RestoreDrillInterval = TimeSpan.FromDays(180);

    public static IReadOnlyList<DrGap> Evaluate(ClusterDrStatus status, DateTime now)
    {
        List<DrGap> gaps = [];

        // A cluster without Velero is not "failing DR" — it is outside the feature's
        // scope entirely, and reporting every such cluster would drown the real gaps.
        if (!status.IsVeleroInstalled)
        {
            return gaps;
        }

        string scope = status.ClusterName;

        // ── Is there anything to restore from at all? ──
        VeleroBackup? usable = status.LastUsableBackup;

        if (usable is null)
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-no-backup:{status.ClusterId}",
                Title = $"“{scope}” has no usable backup",
                Detail = status.LastAttemptedBackup is null
                    ? "Velero is installed but has never completed a backup."
                    : $"The most recent backup finished as {status.LastAttemptedBackup.Phase}. "
                      + "A partially-failed backup has skipped resources and cannot be relied on to restore.",
                Severity = DrSeverity.Critical,
                Remediation = status.Schedules.Count == 0
                    ? "Create a backup schedule, then verify the first backup completes cleanly."
                    : "Investigate why backups are not completing cleanly.",
            });
        }
        else if (usable.CompletedAt is DateTime completed && now - completed > StaleAfter)
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-stale-backup:{status.ClusterId}",
                Title = $"“{scope}” has no recent backup",
                Detail = $"The newest usable backup completed {(int)(now - completed).TotalHours} hours ago. "
                    + "Everything changed since then would be lost.",
                Severity = DrSeverity.Critical,
                Remediation = "Check that the schedule is running and the storage location is reachable.",
            });
        }

        // ── Is anything scheduled, or is protection down to someone remembering? ──
        if (status.Schedules.Count == 0)
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-no-schedule:{status.ClusterId}",
                Title = $"“{scope}” has no backup schedule",
                Detail = "Velero is installed but nothing is scheduled, so backups only happen "
                       + "when someone remembers to take one.",
                Severity = DrSeverity.Warning,
                Remediation = "Add a schedule covering the namespaces that carry state.",
            });
        }

        foreach (VeleroSchedule paused in status.Schedules.Where(s => s.IsPaused))
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-schedule-paused:{status.ClusterId}:{paused.Name}",
                Title = $"Backup schedule “{paused.Name}” is paused on “{scope}”",
                Detail = "A paused schedule still appears in the list but takes no backups.",
                Severity = DrSeverity.Warning,
                Remediation = "Resume the schedule, or delete it if it is genuinely not wanted.",
            });
        }

        // A schedule that skips volumes captures Kubernetes objects but not the data in
        // them — a restore brings back empty PVCs, which looks like success until someone
        // opens the application.
        foreach (VeleroSchedule noVolumes in status.Schedules.Where(s => !s.SnapshotVolumes))
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-schedule-no-volumes:{status.ClusterId}:{noVolumes.Name}",
                Title = $"Schedule “{noVolumes.Name}” on “{scope}” does not capture volume data",
                Detail = "It backs up Kubernetes objects but not persistent volume contents, so a "
                       + "restore would bring back empty volumes.",
                Severity = DrSeverity.Warning,
                Remediation = "Enable volume snapshots, unless volume data is backed up another way.",
            });
        }

        // ── Can Velero still reach where the backups live? ──
        foreach (VeleroStorageLocation location in status.StorageLocations.Where(l => !l.IsAvailable))
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-storage-unavailable:{status.ClusterId}:{location.Name}",
                Title = $"Backup storage “{location.Name}” is unavailable on “{scope}”",
                Detail = $"Velero reports the location as {location.Phase}. New backups will fail and "
                       + "existing ones may not be restorable.",
                Severity = DrSeverity.Critical,
                Remediation = "Check the bucket, its credentials and network reachability from the cluster.",
            });
        }

        // ── Has a restore ever been proven to work? ──
        VeleroRestore? restore = status.LastSuccessfulRestore;
        bool neverRestored = restore is null;
        bool restoreStale = restore?.CompletedAt is DateTime restoredAt
                            && now - restoredAt > RestoreDrillInterval;

        if (usable is not null && (neverRestored || restoreStale))
        {
            gaps.Add(new DrGap
            {
                Key = $"dr-untested:{status.ClusterId}",
                Title = $"Backups on “{scope}” have never been restore-tested",
                Detail = neverRestored
                    ? "Velero has no record of a successful restore. A backup that has never been "
                      + "restored is a hypothesis, not a recovery plan."
                    : $"The last successful restore was {(int)(now - restore!.CompletedAt!.Value).TotalDays} days ago.",
                Severity = DrSeverity.Info,
                Remediation = "Restore a backup into a scratch namespace or a throwaway cluster and confirm it comes back.",
            });
        }

        return gaps;
    }
}
