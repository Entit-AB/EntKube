namespace EntKube.Web.Data;

/// <summary>
/// Tracks a Harbor project that EntKube is managing. Used to link a Harbor
/// project to a customer app so the customer can manage it through the portal.
///
/// The project itself lives in Harbor; this record stores the EntKube-side
/// association (tenant scope, optional app link) so the portal can surface it.
/// </summary>
public class HarborProject
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The Harbor instance this project belongs to.</summary>
    public Guid HarborComponentConfigId { get; set; }

    /// <summary>Harbor project name (used as the path segment in API calls and image references).</summary>
    public required string ProjectName { get; set; }

    /// <summary>
    /// When set, this project is linked to a customer app and the customer can
    /// manage their project (repositories, robot accounts) through the portal.
    /// </summary>
    public Guid? LinkedAppId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public HarborComponentConfig HarborComponentConfig { get; set; } = null!;
    public App? LinkedApp { get; set; }
}
