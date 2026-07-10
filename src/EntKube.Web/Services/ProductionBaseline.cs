namespace EntKube.Web.Services;

/// <summary>
/// One catalog component installed as part of a production baseline. The release name and
/// namespace are resolved from the <see cref="ComponentCatalog"/> entry (falling back to the
/// key) unless explicitly overridden, so the baseline stays in step with the catalog.
/// </summary>
/// <param name="CatalogKey">The <see cref="CatalogEntry.Key"/> to install.</param>
/// <param name="ReleaseName">Override for the Helm release name; null → the catalog default.</param>
/// <param name="Namespace">Override for the target namespace; null → the catalog default.</param>
/// <param name="Parameters">Form-field values to seed the step with; null → catalog defaults.</param>
public sealed record BaselineStep(
    string CatalogKey,
    string? ReleaseName = null,
    string? Namespace = null,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    private CatalogEntry Entry =>
        ComponentCatalog.GetByKey(CatalogKey)
        ?? throw new InvalidOperationException($"Baseline references unknown catalog component '{CatalogKey}'.");

    /// <summary>Resolved Helm release name (override → catalog default → key).</summary>
    public string ResolvedReleaseName => ReleaseName ?? Entry.DefaultReleaseName ?? CatalogKey;

    /// <summary>Resolved target namespace (override → catalog default).</summary>
    public string ResolvedNamespace => Namespace ?? Entry.DefaultNamespace;
}

/// <summary>
/// A togglable capability a user can include in a new cluster's production baseline. Each maps
/// to one or more ordered <see cref="BaselineStep"/>s installed after the cluster is provisioned.
/// </summary>
/// <param name="Key">Stable identifier persisted in the wizard selection.</param>
/// <param name="Label">Short UI label.</param>
/// <param name="Description">One-line explanation shown in the baseline checklist.</param>
/// <param name="Icon">Bootstrap icon class.</param>
/// <param name="Steps">Components this capability installs, in install order.</param>
/// <param name="RequiresStorage">True when this capability needs an object store (CubeFS) present.</param>
public sealed record BaselineCapability(
    string Key,
    string Label,
    string Description,
    string Icon,
    IReadOnlyList<BaselineStep> Steps,
    bool RequiresStorage = false);

/// <summary>
/// The curated "production baseline" a New Cluster is bootstrapped with after provisioning.
///
/// Deliberately excludes the CNI + cloud-controller-manager + Cinder CSI: those are auto-prepended
/// to every provisioning run by <see cref="ClusterBlueprintService"/> (provision → ccm → cinder-csi),
/// so a baseline never lists them. Capabilities are ordered here in the order they should install
/// (cert-manager before ingress, storage before things that want a StorageClass, backups/autoscaling
/// last); <see cref="BuildSteps"/> preserves that order.
/// </summary>
public static class ProductionBaseline
{
    /// <summary>Capability key for node autoscaling — the wizard also gates it on worker-pool bounds.</summary>
    public const string NodeAutoscalingKey = "node-autoscaling";

    /// <summary>Capability key for cluster backups — gated on an object store being present.</summary>
    public const string BackupsKey = "backups";

    public static IReadOnlyList<BaselineCapability> Capabilities { get; } =
    [
        new BaselineCapability(
            "ingress-tls",
            "Ingress + TLS",
            "Traefik (Gateway API) ingress and cert-manager for automatic TLS certificates.",
            "bi-door-open",
            [
                new BaselineStep("cert-manager"),
                new BaselineStep("traefik"),
            ]),

        new BaselineCapability(
            "storage",
            "Object & shared storage",
            "CubeFS for S3-compatible buckets and shared (RWX) volumes, layered on the Cinder block store.",
            "bi-hdd-stack",
            [
                new BaselineStep("cubefs"),
            ]),

        new BaselineCapability(
            "observability",
            "Observability",
            "Kube Prometheus Stack: metrics, Grafana dashboards, and Alertmanager.",
            "bi-graph-up",
            [
                new BaselineStep("kube-prometheus-stack"),
            ]),

        new BaselineCapability(
            BackupsKey,
            "Backups",
            "Velero cluster backup/restore to an S3 target (pairs with CubeFS object storage).",
            "bi-archive",
            [
                new BaselineStep("velero"),
            ],
            RequiresStorage: true),

        new BaselineCapability(
            NodeAutoscalingKey,
            "Node autoscaling",
            "Cluster Autoscaler scales worker nodes to match demand, using each pool's min/max bounds.",
            "bi-arrows-expand",
            [
                new BaselineStep("cluster-autoscaler"),
            ]),
    ];

    /// <summary>The capability keys enabled by default when the wizard first renders.</summary>
    public static IReadOnlyList<string> DefaultSelectedKeys { get; } =
        Capabilities.Select(c => c.Key).ToList();

    /// <summary>
    /// Flattens the chosen capabilities into an ordered list of steps, preserving the
    /// capability declaration order (which is the intended install order). Unknown keys are ignored.
    /// </summary>
    public static IReadOnlyList<BaselineStep> BuildSteps(IEnumerable<string> selectedCapabilityKeys)
    {
        HashSet<string> selected = new(selectedCapabilityKeys, StringComparer.OrdinalIgnoreCase);
        return Capabilities
            .Where(c => selected.Contains(c.Key))
            .SelectMany(c => c.Steps)
            .ToList();
    }
}
