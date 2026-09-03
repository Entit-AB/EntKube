namespace EntKube.Web.Data;

public enum KedaScalerKind
{
    /// <summary>Autoscale an existing workload (Deployment/StatefulSet/…) via a structured form.</summary>
    ScaledObject = 0,

    /// <summary>Raw KEDA YAML authored by the user — a ScaledObject or a ScaledJob.</summary>
    Custom = 1,

    /// <summary>
    /// A native autoscaling/v2 HorizontalPodAutoscaler on CPU and/or memory utilization.
    /// Needs only metrics-server — no KEDA controller on the cluster.
    /// </summary>
    Hpa = 2
}

/// <summary>
/// An autoscaler defined for an app in a specific environment. When applied, EntKube renders
/// a keda.sh/v1alpha1 ScaledObject, an autoscaling/v2 HorizontalPodAutoscaler, or (for Custom)
/// the user's raw YAML — e.g. a ScaledJob — into the app's namespace on every cluster the app
/// is deployed to in that environment.
///
/// The type predates native HPA support and keeps its name (and table) so both autoscaler
/// families share one CRUD/apply/copy pipeline; <see cref="Kind"/> decides what is rendered.
/// Only the KEDA kinds require the KEDA component on the target cluster (ComponentCatalog "keda").
///
/// Scoped per (App, Environment). Name is unique within that scope and is used as the
/// Kubernetes resource name.
/// </summary>
public class KedaScaler
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AppId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>Kubernetes resource name (max 63 chars, lowercase).</summary>
    public required string Name { get; set; }

    public KedaScalerKind Kind { get; set; } = KedaScalerKind.ScaledObject;

    // ── ScaledObject structured fields (Kind == ScaledObject) ──

    /// <summary>Name of the workload to scale (the scaleTargetRef name).</summary>
    public string? ScaleTargetName { get; set; }

    /// <summary>Kind of the workload to scale — Deployment, StatefulSet, etc.</summary>
    public string ScaleTargetKind { get; set; } = "Deployment";

    /// <summary>Minimum replica count. Null uses the KEDA default (0).</summary>
    public int? MinReplicaCount { get; set; }

    /// <summary>Maximum replica count. Null uses the KEDA default (100).</summary>
    public int? MaxReplicaCount { get; set; }

    /// <summary>How often (seconds) KEDA polls the triggers. Null uses the KEDA default (30).</summary>
    public int? PollingInterval { get; set; }

    /// <summary>How long (seconds) to wait after the last active trigger before scaling down. Null uses the KEDA default (300).</summary>
    public int? CooldownPeriod { get; set; }

    /// <summary>
    /// The KEDA triggers block as a YAML list (each item begins with "- type:").
    /// Spliced under spec.triggers — covers all KEDA scaler types (cpu, memory,
    /// prometheus, kafka, rabbitmq, azure-queue, cron, …).
    /// </summary>
    public string? TriggersYaml { get; set; }

    // ── HPA structured fields (Kind == Hpa) ──
    // Reuses ScaleTargetKind/ScaleTargetName and Min/MaxReplicaCount above; an HPA's
    // minReplicas must be at least 1 (scale-to-zero is a KEDA/alpha-gate capability).

    /// <summary>Target average CPU utilization, as a percentage of the pod's CPU request. Null disables the CPU metric.</summary>
    public int? TargetCpuUtilization { get; set; }

    /// <summary>Target average memory utilization, as a percentage of the pod's memory request. Null disables the memory metric.</summary>
    public int? TargetMemoryUtilization { get; set; }

    /// <summary>
    /// Optional spec.behavior block as YAML (scaleUp/scaleDown stabilization windows and
    /// policies). Spliced verbatim under spec.behavior; omitted when blank.
    /// </summary>
    public string? BehaviorYaml { get; set; }

    // ── Custom (Kind == Custom) ──

    /// <summary>Complete KEDA YAML document (ScaledObject or ScaledJob).</summary>
    public string? CustomYaml { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
