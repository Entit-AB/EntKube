namespace EntKube.Web.Data;

/// <summary>
/// Lifecycle status of a cluster component. Tracks whether the component
/// has been installed to the target cluster and its current operational state.
/// </summary>
public enum ComponentStatus
{
    /// <summary>Component is registered but not yet installed on the cluster.</summary>
    NotInstalled,
    /// <summary>Helm install/upgrade is in progress.</summary>
    Installing,
    /// <summary>Component is installed and running on the cluster.</summary>
    Installed,
    /// <summary>Last install/upgrade attempt failed.</summary>
    Failed,
    /// <summary>Helm uninstall is in progress.</summary>
    Uninstalling
}

/// <summary>
/// A component deployed to a Kubernetes cluster. Components represent Helm charts,
/// deployments, or other installable units managed by the platform. Each component
/// can have secrets in the vault that are integrated with its Helm values or
/// environment configuration during deployment.
/// </summary>
public class ClusterComponent
{
    public Guid Id { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>
    /// Human-friendly name (e.g. "minio", "cnpg-cluster", "keycloak").
    /// Must be unique within a cluster.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The type of component. Will be refined later with a proper enum/value set.
    /// Examples: "HelmChart", "Deployment", "StatefulSet", "Operator".
    /// </summary>
    public required string ComponentType { get; set; }

    /// <summary>
    /// Optional JSON configuration specific to this component type.
    /// For kube-prometheus-stack: {"namespace","serviceName","servicePort"}.
    /// </summary>
    public string? Configuration { get; set; }

    // ── Lifecycle fields ──

    /// <summary>Current lifecycle status of this component on the cluster.</summary>
    public ComponentStatus Status { get; set; } = ComponentStatus.NotInstalled;

    /// <summary>Target namespace where the component is installed.</summary>
    public string? Namespace { get; set; }

    /// <summary>Helm repository URL (e.g. "https://prometheus-community.github.io/helm-charts").</summary>
    public string? HelmRepoUrl { get; set; }

    /// <summary>Helm chart name (e.g. "kube-prometheus-stack").</summary>
    public string? HelmChartName { get; set; }

    /// <summary>Helm chart version (e.g. "65.1.0"). Null means latest.</summary>
    public string? HelmChartVersion { get; set; }

    /// <summary>Helm release name on the cluster. Defaults to component Name if not set.</summary>
    public string? ReleaseName { get; set; }

    /// <summary>Custom Helm values in YAML format, applied with --values during install/upgrade.</summary>
    public string? HelmValues { get; set; }

    /// <summary>Error message from the last failed operation, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>When the component was last installed or upgraded.</summary>
    public DateTime? InstalledAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KubernetesCluster Cluster { get; set; } = null!;
    public ICollection<VaultSecret> Secrets { get; set; } = [];
    public ICollection<ExternalRoute> ExternalRoutes { get; set; } = [];
    public ICollection<StorageBinding> StorageBindings { get; set; } = [];
}
