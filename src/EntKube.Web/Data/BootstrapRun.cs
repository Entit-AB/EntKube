namespace EntKube.Web.Data;

/// <summary>
/// Overall status of a bootstrap run.
/// </summary>
public enum BootstrapRunStatus
{
    /// <summary>Created and waiting for the background runner to pick it up.</summary>
    Queued,
    /// <summary>The runner is currently executing steps.</summary>
    Running,
    /// <summary>All steps completed successfully.</summary>
    Succeeded,
    /// <summary>A required step failed; the run is halted and can be resumed.</summary>
    Failed,
    /// <summary>Cancelled by an operator.</summary>
    Cancelled
}

/// <summary>
/// One execution of a <see cref="ClusterBlueprint"/> against a registered cluster.
/// Steps are snapshotted into <see cref="BootstrapStepRun"/> rows at start time so
/// later edits to the blueprint don't rewrite run history. A background
/// runner (BootstrapRunnerService) processes queued/resumable runs.
/// </summary>
public class BootstrapRun
{
    public Guid Id { get; set; }

    public Guid ClusterId { get; set; }

    public Guid BlueprintId { get; set; }

    /// <summary>Snapshot of the blueprint name at run time, for display.</summary>
    public required string BlueprintName { get; set; }

    public BootstrapRunStatus Status { get; set; } = BootstrapRunStatus.Queued;

    /// <summary>Order of the step currently executing (or last executed).</summary>
    public int CurrentStepOrder { get; set; }

    public string? TriggeredBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    // Navigation
    public KubernetesCluster Cluster { get; set; } = null!;
    public ICollection<BootstrapStepRun> StepRuns { get; set; } = [];
}
