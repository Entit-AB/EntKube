namespace EntKube.Web.Data;

/// <summary>
/// How a deployment is defined — whether through manual form entry,
/// raw YAML manifests, or a Helm chart with dynamic values.
/// </summary>
public enum DeploymentType
{
    /// <summary>
    /// Built via form: containers, services, PVCs, env vars, volume mounts.
    /// The platform generates K8s manifests from the structured definition.
    /// </summary>
    Manual,

    /// <summary>
    /// Raw YAML manifests pasted or uploaded by the user.
    /// Supports Deployment, Service, PVC, ConfigMap, etc.
    /// </summary>
    Yaml,

    /// <summary>
    /// A Helm chart from any repository. The user provides the repo URL,
    /// chart name, version, and dynamic values.
    /// </summary>
    HelmChart,

    /// <summary>
    /// Raw YAML manifests sourced from a Git repository. The platform fetches
    /// the YAML files at the specified path and applies them to the cluster.
    /// </summary>
    GitYaml,

    /// <summary>
    /// A Helm chart sourced from a Git repository (the chart lives in the repo,
    /// not in a chart museum). The chart directory is at GitPath.
    /// </summary>
    GitHelm,

    /// <summary>
    /// App-of-apps sourced from a Git repository. The platform reads ArgoCD
    /// Application CRD YAML files at GitPath and creates or reconciles child
    /// AppDeployment rows automatically.
    /// </summary>
    GitAppOfApps
}

/// <summary>
/// The synchronization status of a deployment, modeled after ArgoCD.
/// Tells us whether the desired state matches the live cluster state.
/// </summary>
public enum SyncStatus
{
    Unknown,
    Synced,
    OutOfSync,
    Syncing,
    Failed
}

/// <summary>
/// The health status of a deployment's resources, modeled after ArgoCD.
/// Tells us whether the workload is actually running correctly.
/// </summary>
public enum HealthStatus
{
    Unknown,
    Healthy,
    Progressing,
    Degraded,
    Missing,
    Suspended
}
