namespace EntKube.Web.Data;

/// <summary>
/// Whether a maintenance window owes the agreement's notice period.
/// </summary>
public enum MaintenanceKind
{
    /// <summary>
    /// Arranged in advance, and therefore owed notice. The default, because a window
    /// recorded without anyone saying which kind it is has not earned the exemption.
    /// </summary>
    Planned = 0,

    /// <summary>
    /// Something had to be done at once. No notice is owed beforehand — but the window is
    /// still recorded, so the downtime is still excluded from availability and the
    /// customer can still see it happened.
    /// </summary>
    Emergency = 1,
}

public class MaintenanceWindow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ClusterId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the agreement's notice period applies to this window.</summary>
    public MaintenanceKind Kind { get; set; } = MaintenanceKind.Planned;

    /// <summary>
    /// When the customer was told, if that is not when the window was recorded here.
    ///
    /// <para>Null is the common case and means "when it was created" — the only
    /// announcement the system can vouch for. It is fillable because the announcement is
    /// often a mail sent before anyone opens EntKube, and scoring that window as no notice
    /// at all would be wrong in the direction that matters.</para>
    /// </summary>
    public DateTime? AnnouncedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster? Cluster { get; set; }
}
