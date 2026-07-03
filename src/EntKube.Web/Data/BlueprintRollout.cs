namespace EntKube.Web.Data;

/// <summary>
/// Status of a staged blueprint-update rollout.
/// </summary>
public enum RolloutStatus
{
    /// <summary>Targets remain to be promoted (a target may be running, or awaiting manual promotion).</summary>
    InProgress,
    /// <summary>All targets succeeded.</summary>
    Completed,
    /// <summary>A target failed; the rollout is halted and can be retried or cancelled.</summary>
    Failed,
    /// <summary>Cancelled by an operator; remaining targets are skipped.</summary>
    Cancelled
}

/// <summary>
/// Status of a single cluster within a rollout.
/// </summary>
public enum RolloutTargetStatus
{
    /// <summary>Not started yet.</summary>
    Pending,
    /// <summary>Its update run is queued or executing.</summary>
    Running,
    /// <summary>Its update run succeeded.</summary>
    Succeeded,
    /// <summary>Its update run failed.</summary>
    Failed,
    /// <summary>Skipped (e.g. rollout cancelled before reaching it).</summary>
    Skipped
}

/// <summary>
/// A staged push of a blueprint's current state to every cluster that was
/// bootstrapped from it. Clusters are updated <b>one at a time</b> (in
/// <see cref="BlueprintRolloutTarget.Order"/> order): each target runs an
/// update-mode <see cref="BootstrapRun"/> that reconciles the cluster to the
/// latest blueprint. On success the rollout either auto-advances to the next
/// target (<see cref="AutoAdvance"/>) or waits for the operator to promote it —
/// a CI/CD-style gate. A failed target halts the rollout.
/// </summary>
public class BlueprintRollout
{
    public Guid Id { get; set; }

    public Guid BlueprintId { get; set; }

    /// <summary>Snapshot of the blueprint name at rollout time, for display.</summary>
    public required string BlueprintName { get; set; }

    public RolloutStatus Status { get; set; } = RolloutStatus.InProgress;

    /// <summary>When true, the next target starts automatically after the previous succeeds.</summary>
    public bool AutoAdvance { get; set; }

    public string? TriggeredBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    // Navigation
    public ClusterBlueprint Blueprint { get; set; } = null!;
    public ICollection<BlueprintRolloutTarget> Targets { get; set; } = [];
}

/// <summary>
/// One cluster within a <see cref="BlueprintRollout"/>. Executing a target creates
/// an update-mode <see cref="BootstrapRun"/> (referenced by <see cref="BootstrapRunId"/>)
/// whose outcome drives this target's status.
/// </summary>
public class BlueprintRolloutTarget
{
    public Guid Id { get; set; }

    public Guid RolloutId { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>Snapshot of the cluster name at rollout time, for display.</summary>
    public required string ClusterName { get; set; }

    /// <summary>Zero-based promotion order.</summary>
    public int Order { get; set; }

    public RolloutTargetStatus Status { get; set; } = RolloutTargetStatus.Pending;

    /// <summary>The update run created when this target was started.</summary>
    public Guid? BootstrapRunId { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    // Navigation
    public BlueprintRollout Rollout { get; set; } = null!;
}
