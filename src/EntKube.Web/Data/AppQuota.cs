namespace EntKube.Web.Data;

/// <summary>
/// Resource quota limits for an app's namespace, applied as a Kubernetes
/// ResourceQuota. One record per app (1:1). Null fields are omitted from the
/// generated manifest so only the desired constraints are enforced.
/// </summary>
public class AppQuota
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }

    // CPU
    public string? CpuRequest { get; set; }   // e.g. "500m"
    public string? CpuLimit { get; set; }     // e.g. "2"

    // Memory
    public string? MemoryRequest { get; set; } // e.g. "256Mi"
    public string? MemoryLimit { get; set; }   // e.g. "1Gi"

    // Count quotas
    public int? MaxPods { get; set; }
    public int? MaxPvcs { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Which environment this quota applies to. One quota per app per environment.</summary>
    public Guid EnvironmentId { get; set; }

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
