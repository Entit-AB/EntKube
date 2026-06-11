namespace EntKube.Web.Data;

/// <summary>
/// RBAC configuration for an app: a ServiceAccount plus a Role/RoleBinding pair.
/// One record per app (1:1). When applied, creates:
///   - ServiceAccount (ServiceAccountName in the app's namespace)
///   - Role          (ServiceAccountName + "-role" with all Rules)
///   - RoleBinding   (ServiceAccountName + "-binding" → that Role)
/// </summary>
public class AppRbacPolicy
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }

    /// <summary>Which environment this RBAC policy applies to. One policy per app per environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// The Kubernetes ServiceAccount name, e.g. "billing-api".
    /// Must be a valid DNS label.
    /// </summary>
    public required string ServiceAccountName { get; set; }

    /// <summary>
    /// Whether pods should auto-mount the service account token.
    /// Defaults to false (least-privilege).
    /// </summary>
    public bool AutoMountToken { get; set; } = false;

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public ICollection<AppRbacRule> Rules { get; set; } = [];
}

/// <summary>
/// A single rule inside an AppRbacPolicy's Role, equivalent to one entry in
/// rules[] of a Kubernetes Role manifest.
/// </summary>
public class AppRbacRule
{
    public Guid Id { get; set; }
    public Guid AppRbacPolicyId { get; set; }

    /// <summary>API group, e.g. "" (core), "apps", "batch", or "*".</summary>
    public string ApiGroups { get; set; } = "";

    /// <summary>Comma-separated resource types, e.g. "pods,services" or "*".</summary>
    public required string Resources { get; set; }

    /// <summary>Comma-separated verbs, e.g. "get,list,watch" or "*".</summary>
    public required string Verbs { get; set; }

    // Navigation
    public AppRbacPolicy Policy { get; set; } = null!;
}
