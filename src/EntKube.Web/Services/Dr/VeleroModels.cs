namespace EntKube.Web.Services.Dr;

/// <summary>
/// Velero's terminal and in-flight backup phases.
///
/// PartiallyFailed is the one that matters most: Velero reports it as a completed
/// backup with a completion timestamp, so anything that only checks "did it finish"
/// treats a backup that silently skipped resources as a success. It is the phase most
/// likely to be discovered during a restore, which is the worst possible time.
/// </summary>
public enum VeleroPhase
{
    Completed = 0,
    PartiallyFailed = 1,
    Failed = 2,
    InProgress = 3,
    FailedValidation = 4,
    Deleting = 5,
    New = 6,
    Unknown = 7,
}

/// <summary>One Velero backup.</summary>
public sealed record VeleroBackup
{
    public required string Name { get; init; }
    public required VeleroPhase Phase { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Schedule that created it, when it was not taken by hand.</summary>
    public string? CreatedBySchedule { get; init; }

    public int Errors { get; init; }
    public int Warnings { get; init; }

    /// <summary>Namespaces the backup covers. Empty means every namespace.</summary>
    public IReadOnlyList<string> IncludedNamespaces { get; init; } = [];

    /// <summary>Storage location holding the data.</summary>
    public string? StorageLocation { get; init; }

    /// <summary>
    /// True only for a fully clean backup. PartiallyFailed is deliberately excluded:
    /// a backup that skipped resources is not one you can rely on to restore.
    /// </summary>
    public bool IsUsable => Phase == VeleroPhase.Completed && Errors == 0;
}

/// <summary>One Velero backup schedule.</summary>
public sealed record VeleroSchedule
{
    public required string Name { get; init; }
    public required string Cron { get; init; }
    public bool IsPaused { get; init; }
    public DateTime? LastBackupAt { get; init; }

    /// <summary>Retention for backups this schedule creates, as a Go duration ("720h0m0s").</summary>
    public string? Ttl { get; init; }

    public IReadOnlyList<string> IncludedNamespaces { get; init; } = [];

    /// <summary>True when the schedule also captures volume data, not just Kubernetes objects.</summary>
    public bool SnapshotVolumes { get; init; } = true;
}

/// <summary>One Velero restore, used as evidence that a restore has actually been exercised.</summary>
public sealed record VeleroRestore
{
    public required string Name { get; init; }
    public required string BackupName { get; init; }
    public required VeleroPhase Phase { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int Errors { get; init; }
}

/// <summary>A backup storage location and whether Velero can currently reach it.</summary>
public sealed record VeleroStorageLocation
{
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public string? Bucket { get; init; }

    /// <summary>Velero's own availability check — "Available" or "Unavailable".</summary>
    public string Phase { get; init; } = "Unknown";

    public DateTime? LastValidatedAt { get; init; }
    public bool IsDefault { get; init; }

    public bool IsAvailable => string.Equals(Phase, "Available", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything known about one cluster's disaster-recovery posture.</summary>
public sealed record ClusterDrStatus
{
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }

    /// <summary>False when Velero is not installed — every other field is then empty.</summary>
    public bool IsVeleroInstalled { get; init; }

    public IReadOnlyList<VeleroSchedule> Schedules { get; init; } = [];
    public IReadOnlyList<VeleroBackup> Backups { get; init; } = [];
    public IReadOnlyList<VeleroRestore> Restores { get; init; } = [];
    public IReadOnlyList<VeleroStorageLocation> StorageLocations { get; init; } = [];

    /// <summary>Why the status could not be read, when it could not be.</summary>
    public string? Error { get; init; }

    /// <summary>The newest backup that could actually be restored from.</summary>
    public VeleroBackup? LastUsableBackup =>
        Backups.Where(b => b.IsUsable)
               .OrderByDescending(b => b.CompletedAt ?? DateTime.MinValue)
               .FirstOrDefault();

    /// <summary>The newest backup of any outcome, used to tell "failing" from "never ran".</summary>
    public VeleroBackup? LastAttemptedBackup =>
        Backups.OrderByDescending(b => b.StartedAt ?? DateTime.MinValue).FirstOrDefault();

    /// <summary>The most recent successful restore — evidence the backups have been exercised.</summary>
    public VeleroRestore? LastSuccessfulRestore =>
        Restores.Where(r => r.Phase == VeleroPhase.Completed)
                .OrderByDescending(r => r.CompletedAt ?? DateTime.MinValue)
                .FirstOrDefault();
}
