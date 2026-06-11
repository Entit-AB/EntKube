namespace EntKube.Web.Data;

/// <summary>
/// An immutable record of an action taken by a user or the system on a deployment
/// resource. Written on every destructive or significant operation — restart, scale,
/// delete, apply, install — so operators have a full activity timeline.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; }

    /// <summary>The deployment the action was taken on, if applicable.</summary>
    public Guid? DeploymentId { get; set; }

    /// <summary>
    /// The identity that performed the action. Contains the user's email/name
    /// from the authentication state. Null for system-initiated actions.
    /// </summary>
    public string? PerformedBy { get; set; }

    /// <summary>Short verb describing what happened, e.g. "RestartWorkload", "ScalePod", "HelmInstall".</summary>
    public required string Action { get; set; }

    /// <summary>The Kubernetes resource kind the action targeted, e.g. "Deployment", "Pod".</summary>
    public required string ResourceKind { get; set; }

    /// <summary>The Kubernetes resource name that was affected.</summary>
    public string? ResourceName { get; set; }

    /// <summary>Extra context: replica count for scale, chart version for install, etc.</summary>
    public string? Details { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AppDeployment? Deployment { get; set; }
}
