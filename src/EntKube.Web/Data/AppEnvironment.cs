namespace EntKube.Web.Data;

/// <summary>
/// Join entity linking an app to an environment. This represents the
/// many-to-many relationship: an app can be deployed to multiple environments,
/// and an environment can host multiple apps.
/// </summary>
public class AppEnvironment
{
    public Guid AppId { get; set; }

    public Guid EnvironmentId { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Locked namespace for this app in this environment. When set, deployments
    /// must use this namespace — customers cannot override it.
    /// </summary>
    public string? Namespace { get; set; }

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
