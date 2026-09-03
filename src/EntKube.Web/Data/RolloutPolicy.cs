namespace EntKube.Web.Data;

/// <summary>What EntKube does when a rollout's analysis says the new version is failing.</summary>
public enum RolloutFailureAction
{
    /// <summary>Raise an incident and leave the deployment alone. The safe default.</summary>
    Alert = 0,
    /// <summary>Roll the workload back to its previous revision automatically.</summary>
    Rollback = 1,
}

/// <summary>What to do when no signal could be measured at all.</summary>
public enum RolloutInconclusiveAction
{
    /// <summary>Treat an unmeasurable rollout as fine and close it. </summary>
    Promote = 0,
    /// <summary>Leave the rollout open and tell someone. Nothing is rolled back on no evidence.</summary>
    Hold = 1,
}

/// <summary>
/// Per-deployment rules for watching a release after it is applied.
///
/// Thresholds are all nullable: a null threshold means "do not judge on this signal",
/// which is different from a threshold that could not be measured. That distinction is
/// the core of the analysis — see <c>RolloutAnalysis</c>.
/// </summary>
public class RolloutPolicy
{
    public Guid Id { get; set; }

    public Guid DeploymentId { get; set; }

    /// <summary>When false, applying this deployment opens no rollout watch.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// How long to watch after the apply before deciding. Long enough for the new pods to
    /// take real traffic; too short and every rollout is judged on its warm-up.
    /// </summary>
    public int AnalysisWindowMinutes { get; set; } = 10;

    /// <summary>
    /// Grace period after the apply before any sample counts. Pods are still starting and
    /// the old ones still terminating, so early samples describe the rollout, not the release.
    /// </summary>
    public int WarmupMinutes { get; set; } = 2;

    /// <summary>Fail if the HTTP/span error rate over the window exceeds this percentage. Null = do not check.</summary>
    public double? MaxErrorRatePercent { get; set; } = 5.0;

    /// <summary>Fail if p95 latency exceeds this many milliseconds. Null = do not check.</summary>
    public double? MaxLatencyP95Ms { get; set; }

    /// <summary>Fail if containers restart more than this many times during the window. Null = do not check.</summary>
    public int? MaxRestarts { get; set; } = 3;

    /// <summary>
    /// Fail if the ready fraction of replicas drops below this. Null = do not check.
    /// The one signal that is always available, since it needs no instrumentation.
    /// </summary>
    public double? MinReadyFraction { get; set; } = 1.0;

    /// <summary>
    /// Fail if the deployment's error budget is burning faster than this multiple of the
    /// sustainable rate. Null = do not check. Requires an SLA target on the deployment.
    /// </summary>
    public double? MaxErrorBudgetBurnRate { get; set; }

    public RolloutFailureAction OnFailure { get; set; } = RolloutFailureAction.Alert;

    public RolloutInconclusiveAction OnInconclusive { get; set; } = RolloutInconclusiveAction.Promote;

    /// <summary>
    /// Name of the service in the trace store carrying this deployment's spans. When null,
    /// error-rate and latency checks cannot run and are reported as unavailable.
    /// </summary>
    public string? TelemetryServiceName { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    // Navigation
    public AppDeployment Deployment { get; set; } = null!;
}

/// <summary>Lifecycle of one watched release.</summary>
public enum DeploymentRolloutStatus
{
    /// <summary>Applied; inside the analysis window.</summary>
    Watching = 0,
    /// <summary>Analysis passed. The release stands.</summary>
    Promoted = 1,
    /// <summary>Analysis failed and the workload was rolled back.</summary>
    RolledBack = 2,
    /// <summary>Analysis failed and the policy said to alert rather than roll back.</summary>
    Alerted = 3,
    /// <summary>Nothing could be measured and the policy said to hold.</summary>
    Inconclusive = 4,
    /// <summary>The rollback itself failed — needs a human now.</summary>
    RollbackFailed = 5,
    /// <summary>Superseded by a newer apply before the window closed.</summary>
    Superseded = 6,
}

/// <summary>
/// One watched release of a deployment: opened when EntKube applies it, closed when the
/// analysis window expires and a verdict is reached.
/// </summary>
public class DeploymentRollout
{
    public Guid Id { get; set; }

    public Guid DeploymentId { get; set; }

    public DeploymentRolloutStatus Status { get; set; } = DeploymentRolloutStatus.Watching;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the analysis window closes and a verdict must be reached.</summary>
    public DateTime DecideAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Who or what triggered the apply — a username, or "api-token:CI pipeline".</summary>
    public string? TriggeredBy { get; set; }

    /// <summary>Operator-facing explanation of the verdict.</summary>
    public string? Verdict { get; set; }

    /// <summary>JSON snapshot of the measured signals, for the rollout history view.</summary>
    public string? SignalsJson { get; set; }

    // Navigation
    public AppDeployment Deployment { get; set; } = null!;
}
