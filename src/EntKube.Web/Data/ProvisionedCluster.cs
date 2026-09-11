namespace EntKube.Web.Data;

/// <summary>What the operator wants this cluster to be doing.</summary>
public enum ProvisionedClusterState
{
    /// <summary>Reconciled towards the spec.</summary>
    Running,

    /// <summary>Left alone. Nothing is applied, nothing is repaired — an escape hatch for
    /// investigating a cluster without a controller changing it underneath you.</summary>
    Paused,

    /// <summary>On its way out. Set before teardown so a crash mid-delete resumes rather than
    /// reconciles the cluster back into existence.</summary>
    Deleting
}

/// <summary>Where the API server lives, which the cloud decides rather than the operator.</summary>
public enum ClusterApiEndpoint
{
    /// <summary>An Octavia load balancer across the control-plane nodes.</summary>
    Octavia,

    /// <summary>A floating IP, for clouds with no load-balancer service.</summary>
    FloatingIp
}

public enum ClusterNetworkMode
{
    /// <summary>CAPO creates the router, network and subnet. The from-nothing path.</summary>
    Managed,

    /// <summary>Nodes attach to a network that already exists.</summary>
    Existing
}

/// <summary>
/// A Kubernetes cluster EntKube creates and operates on OpenStack: the spec, as asked for.
///
/// <para><b>Why this is a row rather than JSON on a blueprint.</b> A blueprint describes how to
/// build something once. This describes what a cluster is supposed to be, continuously — and day-2
/// is editing it. Node pools have to be addressable to be scaled, versions have to be comparable
/// to be upgraded, and a reconciler needs somewhere to record what it last saw. None of that works
/// against a serialized blob that only the provisioning run ever read.</para>
///
/// <para>The spec is intent; the cluster's own Cluster API objects are fact. EntKube keeps no
/// second copy of the facts — the difference between the two is exactly what the reconciler acts
/// on, and a cached copy of reality is a copy that can be wrong.</para>
/// </summary>
public class ProvisionedCluster
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The registered cluster this became. Null until the control plane is up and its kubeconfig
    /// has been stored — a cluster being built is not yet a cluster anything can be deployed to.
    /// </summary>
    public Guid? KubernetesClusterId { get; set; }

    /// <summary>The cloud it lives on.</summary>
    public Guid OpenStackConnectionId { get; set; }

    /// <summary>DNS-1123 name, also the CAPI Cluster name and the prefix of every resource.</summary>
    public required string Name { get; set; }

    /// <summary>The environment the registered cluster belongs to once it exists.</summary>
    public Guid EnvironmentId { get; set; }

    // ── Versions and images ──

    public string KubernetesVersion { get; set; } = "v1.31.4";

    /// <summary>
    /// The baked node image. Empty means "find or build one for <see cref="KubernetesVersion"/>",
    /// which is the normal case — an operator asks for a version, not for an image name.
    /// </summary>
    public string NodeImageName { get; set; } = "";

    /// <summary>The stock cloud image a node image is baked from when one has to be built.</summary>
    public string BaseImageName { get; set; } = "";

    // ── Control plane ──

    /// <summary>Odd, and 3 by default: one node is not a control plane you can lose a node from.</summary>
    public int ControlPlaneCount { get; set; } = 3;

    public string ControlPlaneFlavor { get; set; } = "";

    /// <summary>Root disk in GiB; 0 uses the flavor's own. etcd is what fills a control-plane disk.</summary>
    public int ControlPlaneDiskGb { get; set; }

    public ClusterApiEndpoint ApiEndpoint { get; set; } = ClusterApiEndpoint.Octavia;

    // ── Networking ──

    public ClusterNetworkMode NetworkMode { get; set; } = ClusterNetworkMode.Managed;

    public string? NodeNetworkId { get; set; }

    public string ExternalNetworkId { get; set; } = "";

    public string PodCidr { get; set; } = "192.168.0.0/16";

    public string ServiceCidr { get; set; } = "10.96.0.0/12";

    public string DnsNameservers { get; set; } = "1.1.1.1,8.8.8.8";

    /// <summary>Default availability zone for machines that do not name one themselves.</summary>
    public string? FailureDomain { get; set; }

    // ── Bootstrap ──

    /// <summary>Flavor for the ephemeral kubeadm bootstrap VM, and for the image build VM.</summary>
    public string BootstrapFlavor { get; set; } = "";

    /// <summary>Network the ephemeral VMs attach to. Needs outbound internet.</summary>
    public string BootstrapNetworkId { get; set; } = "";

    public string BootstrapSshUser { get; set; } = "ubuntu";

    // ── Intent and observation ──

    public ProvisionedClusterState DesiredState { get; set; } = ProvisionedClusterState.Running;

    /// <summary>
    /// Bumped on every change to the spec or its pools. The reconciler records which generation it
    /// last acted on, so "has this been applied yet" is answerable without diffing everything.
    /// </summary>
    public int Generation { get; set; } = 1;

    /// <summary>The generation the reconciler last brought the cluster to.</summary>
    public int ObservedGeneration { get; set; }

    public DateTime? LastReconciledAt { get; set; }

    /// <summary>
    /// The last observed state, as JSON: control-plane readiness and version, per-pool replica
    /// counts, machines in trouble, rollouts in progress. A cache for the UI, never an input to a
    /// decision — decisions read the cluster.
    /// </summary>
    public string? ObservedStateJson { get; set; }

    /// <summary>Why the cluster is not what was asked for, when it is not.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<ProvisionedWorkerPool> WorkerPools { get; set; } = [];
}

/// <summary>
/// One group of identically-shaped worker nodes. A row rather than a list element because pools are
/// what day-2 operates on: scaled, reshaped, upgraded and removed individually, and named in the
/// CAPI resources that carry them.
/// </summary>
public class ProvisionedWorkerPool
{
    public Guid Id { get; set; }

    public Guid ProvisionedClusterId { get; set; }

    /// <summary>
    /// Unique within the cluster, and part of the MachineDeployment's name — so it is addressable
    /// from a day-2 operation rather than positional.
    /// </summary>
    public required string Name { get; set; }

    public string Flavor { get; set; } = "";

    /// <summary>Root disk in GiB; 0 uses the flavor's own disk.</summary>
    public int DiskGb { get; set; }

    /// <summary>Availability zone. Per-pool, because some flavors only exist in some zones.</summary>
    public string? FailureDomain { get; set; }

    /// <summary>Fixed size, and the starting size when autoscaling.</summary>
    public int Count { get; set; } = 3;

    /// <summary>Set together with <see cref="MaxCount"/> to autoscale; null on both means fixed.</summary>
    public int? MinCount { get; set; }

    public int? MaxCount { get; set; }

    /// <summary>
    /// Null follows the control plane. It differs only during an upgrade, where the control plane
    /// goes first and pools follow one at a time.
    /// </summary>
    public string? KubernetesVersion { get; set; }

    /// <summary>Node labels as JSON, applied at registration.</summary>
    public string? LabelsJson { get; set; }

    /// <summary>Node taints as JSON, applied at registration.</summary>
    public string? TaintsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ProvisionedCluster Cluster { get; set; } = null!;
}
