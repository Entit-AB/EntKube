namespace EntKube.Web.Data;

/// <summary>
/// Execution status of a single bootstrap step.
/// </summary>
public enum BootstrapStepStatus
{
    /// <summary>Not started yet.</summary>
    Pending,
    /// <summary>Currently installing/creating.</summary>
    Running,
    /// <summary>Completed successfully.</summary>
    Succeeded,
    /// <summary>Failed; the run halts here (unless the step was optional).</summary>
    Failed,
    /// <summary>Skipped (e.g. an optional step whose predecessor failed).</summary>
    Skipped
}

/// <summary>
/// A per-step snapshot + result within a <see cref="BootstrapRun"/>. Captures the
/// resolved step definition (blueprint values merged with bootstrap overrides) so
/// the run is reproducible and auditable independent of the source blueprint.
/// </summary>
public class BootstrapStepRun
{
    public Guid Id { get; set; }

    public Guid BootstrapRunId { get; set; }

    public int Order { get; set; }

    public BlueprintStepType StepType { get; set; }

    /// <summary>Catalog key or service kind (see <see cref="BlueprintStep.Key"/>).</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Namespace { get; set; }

    /// <summary>Blueprint parameters merged with any bootstrap-time overrides (JSON).</summary>
    public string? ResolvedParametersJson { get; set; }

    public BootstrapStepStatus Status { get; set; } = BootstrapStepStatus.Pending;

    /// <summary>Captured helm/kubectl output for display.</summary>
    public string? Output { get; set; }

    public string? Error { get; set; }

    /// <summary>For Component steps: the ClusterComponent created for this step.</summary>
    public Guid? CreatedComponentId { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    // Navigation
    public BootstrapRun Run { get; set; } = null!;
}
