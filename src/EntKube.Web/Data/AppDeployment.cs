namespace EntKube.Web.Data;

/// <summary>
/// An app deployment represents a deployable unit targeting a specific cluster
/// and namespace. Think of it like an ArgoCD Application — it describes what
/// should be deployed, where, and tracks whether the live state matches.
///
/// A deployment can be defined three ways:
/// - Manual: structured form (containers, services, PVCs) → generated manifests
/// - Yaml: raw K8s YAML pasted or uploaded
/// - HelmChart: a Helm chart with repo, name, version, and dynamic values
///
/// Regardless of type, the platform tracks the deployment's sync and health
/// status by comparing desired state to live cluster state.
/// </summary>
public class AppDeployment
{
    public Guid Id { get; set; }

    public Guid AppId { get; set; }

    /// <summary>
    /// A human-friendly name for this deployment (e.g. "billing-api-prod").
    /// Must be unique within an app.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// How this deployment is defined — Manual form, raw YAML, or Helm chart.
    /// </summary>
    public DeploymentType Type { get; set; }

    // ── Target ──

    /// <summary>
    /// The environment this deployment targets (e.g. Production).
    /// </summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// The cluster within that environment where resources will be applied.
    /// </summary>
    public Guid ClusterId { get; set; }

    /// <summary>
    /// The Kubernetes namespace for all resources in this deployment.
    /// </summary>
    public required string Namespace { get; set; }

    // ── Status (ArgoCD-style) ──

    public SyncStatus SyncStatus { get; set; } = SyncStatus.Unknown;
    public HealthStatus HealthStatus { get; set; } = HealthStatus.Unknown;
    public string? StatusMessage { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// When false the deployment is observed only: EntKube tracks live state and shows
    /// drift but never applies manifests to the cluster — leaving ownership to whatever
    /// created the workload (commonly ArgoCD or Flux). Imported deployments start
    /// unmanaged; enabling management refreshes the manifests from the live cluster
    /// (adopting ArgoCD/Flux's current spec) before EntKube will apply. Defaults to true
    /// so deployments created in EntKube manage themselves as before.
    /// </summary>
    public bool IsManaged { get; set; } = true;

    // ── Helm-specific fields (only used when Type == HelmChart) ──

    /// <summary>
    /// The Helm chart repository URL (e.g. "https://charts.bitnami.com/bitnami").
    /// </summary>
    public string? HelmRepoUrl { get; set; }

    /// <summary>
    /// The chart name within the repository (e.g. "postgresql").
    /// </summary>
    public string? HelmChartName { get; set; }

    /// <summary>
    /// The chart version to deploy (e.g. "15.5.0").
    /// </summary>
    public string? HelmChartVersion { get; set; }

    /// <summary>
    /// The Helm values as YAML text. These override the chart's default values.
    /// Stored as free-form YAML so any chart's values can be represented.
    /// </summary>
    public string? HelmValues { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Git source fields (only used when Type is GitYaml, GitHelm, or GitAppOfApps) ──

    /// <summary>
    /// The URL of the Git repository to sync from. Used for policy-credential-based
    /// git access (no GitRepository record required). Takes precedence over GitRepositoryId.
    /// </summary>
    public string? GitUrl { get; set; }

    /// <summary>
    /// Legacy: FK to a registered GitRepository. Superseded by GitUrl for new deployments.
    /// </summary>
    public Guid? GitRepositoryId { get; set; }

    /// <summary>
    /// Path within the repository to the manifest directory, Helm chart root,
    /// or app-of-apps directory. Use "." for the repo root.
    /// </summary>
    public string? GitPath { get; set; }

    /// <summary>
    /// Branch, tag, or commit SHA to check out. Defaults to the repository's
    /// DefaultBranch when null.
    /// </summary>
    public string? GitRevision { get; set; }

    /// <summary>
    /// SHA of the commit that was last successfully synced from Git.
    /// </summary>
    public string? GitLastSyncedCommit { get; set; }

    /// <summary>
    /// When the last successful Git sync completed.
    /// </summary>
    public DateTime? GitLastSyncedAt { get; set; }

    /// <summary>
    /// When true the background GitSyncService will poll this deployment
    /// and sync whenever a new commit is detected.
    /// </summary>
    public bool GitAutoSync { get; set; } = true;

    /// <summary>
    /// For GitAppOfApps child deployments: the parent deployment that manages
    /// this deployment's lifecycle. Null for top-level deployments.
    /// </summary>
    public Guid? ParentDeploymentId { get; set; }

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public KubernetesCluster Cluster { get; set; } = null!;
    public GitRepository? GitRepository { get; set; }
    public AppDeployment? ParentDeployment { get; set; }
    public ICollection<AppDeployment> ChildDeployments { get; set; } = [];
    public ICollection<DeploymentManifest> Manifests { get; set; } = [];
    public ICollection<DeploymentResource> Resources { get; set; } = [];
    public ICollection<DeploymentAppliedResource> AppliedResources { get; set; } = [];
    public ICollection<StorageBinding> StorageBindings { get; set; } = [];
    public ICollection<DatabaseBinding> DatabaseBindings { get; set; } = [];
    public ICollection<CacheBinding> CacheBindings { get; set; } = [];
    public ICollection<AppDeploymentRoute> Routes { get; set; } = [];
}
