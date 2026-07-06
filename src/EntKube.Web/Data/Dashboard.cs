namespace EntKube.Web.Data;

/// <summary>
/// A user-composed dashboard: a named, tenant-scoped set of panels over the native telemetry signals
/// (metrics / traces / logs). Panels are stored as JSON (<see cref="PanelsJson"/>) so the layout can
/// evolve without a schema change; the cluster and time range are chosen at view time.
/// </summary>
public class Dashboard
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>Serialized list of DashboardPanel — the panel configs and their grid widths.</summary>
    public string PanelsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
