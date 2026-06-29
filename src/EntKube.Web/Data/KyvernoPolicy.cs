namespace EntKube.Web.Data;

public enum KyvernoPolicyType
{
    // Container security
    DisallowPrivilegedContainers  = 0,
    DisallowRootUser              = 1,
    RequireReadOnlyRootFilesystem = 2,
    DisallowPrivilegeEscalation   = 3,
    RequireSeccompProfile         = 12,

    // Workload isolation
    DisallowHostNetwork = 4,
    DisallowHostPID     = 5,
    DisallowHostPath    = 6,

    // Image control
    RestrictImageRegistries = 7,

    // Resource governance
    RequireResourceLimits   = 8,
    RequireResourceRequests = 9,

    // Metadata enforcement
    RequirePodLabels = 10,

    // Escape hatch
    Custom = 11
}

public enum KyvernoValidationFailureAction
{
    Audit,
    Enforce
}

/// <summary>
/// A Kyverno admission policy applied at tenant+environment scope.
/// When applied, the same policies are pushed to every app namespace the
/// tenant owns in that environment. Built-in types are singleton per
/// (tenant, environment, type); Custom allows multiple raw-YAML policies.
/// </summary>
public class KyvernoPolicy
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EnvironmentId { get; set; }

    public KyvernoPolicyType PolicyType { get; set; }

    /// <summary>Audit logs violations without blocking; Enforce blocks at admission.</summary>
    public KyvernoValidationFailureAction ValidationFailureAction { get; set; } = KyvernoValidationFailureAction.Audit;

    /// <summary>
    /// For Custom type: user-provided name used as the K8s resource name (max 63 chars, lowercase).
    /// Not used for built-in types — they derive their name from PolicyType.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// JSON configuration for parametrized built-in policies.
    /// RestrictImageRegistries: string[] of allowed registry prefixes.
    /// RequirePodLabels: string[] of required label keys.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>For Custom type: complete Kyverno Policy YAML document.</summary>
    public string? CustomYaml { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
