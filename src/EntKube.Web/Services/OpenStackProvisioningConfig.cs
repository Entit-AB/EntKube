using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntKube.Web.Services;

/// <summary>
/// A pool of identically-sized worker nodes in a provisioned cluster.
/// </summary>
public sealed class WorkerPool
{
    public string Name { get; set; } = "md-0";
    public int Count { get; set; } = 3;

    /// <summary>OpenStack flavor name for the workers in this pool (e.g. "b.4c8gb").</summary>
    public string Flavor { get; set; } = "";

    /// <summary>
    /// When set (together with <see cref="MaxCount"/>), the pool's MachineDeployment is annotated
    /// for cluster-autoscaler so node count scales between <see cref="MinCount"/>..<see cref="MaxCount"/>.
    /// Null on both means a fixed-size pool (no autoscaling).
    /// </summary>
    public int? MinCount { get; set; }

    /// <summary>Upper bound of nodes when autoscaling is enabled (see <see cref="MinCount"/>).</summary>
    public int? MaxCount { get; set; }

    /// <summary>True when this pool is configured to autoscale (both bounds set and Max ≥ Min ≥ 0).</summary>
    [JsonIgnore]
    public bool Autoscale => MinCount is int min && MaxCount is int max && min >= 0 && max >= min && max > 0;

    /// <summary>Root disk in GiB. 0 uses the flavor's own disk, which is what most flavors provide.</summary>
    public int DiskGb { get; set; }

    /// <summary>
    /// Availability zone for this pool's nodes. Per-pool rather than per-cluster, because spreading
    /// pools across zones is how a cluster survives one of them — and because some flavors only
    /// exist in some zones.
    /// </summary>
    public string? FailureDomain { get; set; }

    /// <summary>
    /// Kubernetes version for this pool's nodes. Null follows the control plane. It only differs
    /// during an upgrade, where the control plane goes first and pools follow one at a time.
    /// </summary>
    public string? KubernetesVersion { get; set; }

    /// <summary>Node labels, applied at registration so a scheduler never sees the node without them.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>Node taints, applied at registration for the same reason.</summary>
    public List<NodeTaint> Taints { get; set; } = [];
}

/// <summary>A node taint. Effect is Kubernetes' own spelling: NoSchedule, PreferNoSchedule, NoExecute.</summary>
public sealed class NodeTaint
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Effect { get; set; } = "NoSchedule";
}

/// <summary>
/// Strongly-typed provisioning configuration serialized to
/// <see cref="Data.ClusterBlueprint.ProvisioningConfig"/> as JSON when a blueprint
/// provisions its own OpenStack cluster (Cluster API + CAPO). Describes the target
/// cluster shape; the ephemeral bootstrap VM parameters; and the network/image
/// inputs the CAPO template consumes.
/// </summary>
public sealed class OpenStackProvisioningConfig
{
    /// <summary>The OpenStack connection (Keystone auth) used to create the cluster.</summary>
    public Guid OpenStackConnectionId { get; set; }

    /// <summary>Name of the target cluster (also the CAPI Cluster name; must be DNS-1123).</summary>
    public string ClusterName { get; set; } = "";

    /// <summary>Kubernetes version for the target nodes, e.g. "v1.31.4".</summary>
    public string KubernetesVersion { get; set; } = "v1.31.4";

    // ── Target cluster nodes ──

    /// <summary>
    /// Glance image the control-plane and worker nodes boot from. Left empty, an image for
    /// <see cref="KubernetesVersion"/> is found or baked from <see cref="BaseImageName"/> — which is
    /// what makes "give me a v1.31.4 cluster" answerable without anyone knowing an image name.
    /// </summary>
    public string NodeImageName { get; set; } = "";

    /// <summary>
    /// The stock Ubuntu cloud image a node image is baked from, when one has to be built. Not the
    /// image nodes boot: this one has no Kubernetes on it yet.
    /// </summary>
    public string BaseImageName { get; set; } = "";

    public int ControlPlaneCount { get; set; } = 3;
    public string ControlPlaneFlavor { get; set; } = "";

    /// <summary>
    /// Root disk in GiB for control-plane nodes. 0 uses the flavor's own disk. Worth setting: etcd
    /// is the thing that fills a control-plane disk, and a full one takes the cluster with it.
    /// </summary>
    public int ControlPlaneDiskGb { get; set; }

    public List<WorkerPool> WorkerPools { get; set; } = [];

    // ── Networking ──

    /// <summary>External (public) network ID used for floating IPs and the API server load balancer.</summary>
    public string ExternalNetworkId { get; set; } = "";

    /// <summary>Existing tenant network ID to attach nodes to. Null → CAPO creates a managed network.</summary>
    public string? NodeNetworkId { get; set; }

    public string PodCidr { get; set; } = "192.168.0.0/16";
    public string ServiceCidr { get; set; } = "10.96.0.0/12";

    /// <summary>DNS nameservers for the managed network (comma-separated), e.g. "1.1.1.1,8.8.8.8".</summary>
    public string DnsNameservers { get; set; } = "1.1.1.1,8.8.8.8";

    /// <summary>OpenStack availability zone / failure domain for the nodes (optional).</summary>
    public string? FailureDomain { get; set; }

    /// <summary>CNI to install once nodes are up (drives which catalog component is auto-appended).</summary>
    public string Cni { get; set; } = "cilium";

    // ── Ephemeral bootstrap VM ──

    /// <summary>
    /// Glance image for the throwaway bootstrap VM. Left empty it follows the node image, which is
    /// the intended arrangement: the bootstrap node runs kubeadm exactly as a cluster node does, so
    /// the same baked image serves both and neither has to install a distribution at boot.
    /// </summary>
    public string BootstrapImageName { get; set; } = "";

    /// <summary>Flavor for the throwaway bootstrap VM (a small 2c/4gb flavor is plenty).</summary>
    public string BootstrapFlavor { get; set; } = "";

    /// <summary>Tenant network the bootstrap VM attaches to (must reach the OpenStack + internet).</summary>
    public string BootstrapNetworkId { get; set; } = "";

    /// <summary>Default login user of the bootstrap image (Ubuntu images use "ubuntu").</summary>
    public string BootstrapSshUser { get; set; } = "ubuntu";

    public int TotalWorkerCount => WorkerPools.Sum(p => p.Count);

    /// <summary>
    /// The image the bootstrap VM boots. Falls back to the node image, which is the arrangement
    /// this is designed around: one baked image serves the cluster's nodes and the throwaway
    /// kubeadm control plane that creates them.
    /// </summary>
    public string EffectiveBootstrapImageName =>
        string.IsNullOrWhiteSpace(BootstrapImageName) ? NodeImageName : BootstrapImageName;

    // ── (De)serialization ──

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static OpenStackProvisioningConfig FromJson(string json) =>
        JsonSerializer.Deserialize<OpenStackProvisioningConfig>(json, JsonOptions)
        ?? throw new InvalidOperationException("ProvisioningConfig JSON was empty or invalid.");

    /// <summary>
    /// Validates the config, returning a human-readable error per missing/invalid field.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (OpenStackConnectionId == Guid.Empty) errors.Add("An OpenStack connection is required.");
        if (string.IsNullOrWhiteSpace(ClusterName)) errors.Add("Cluster name is required.");
        if (string.IsNullOrWhiteSpace(NodeImageName) && string.IsNullOrWhiteSpace(BaseImageName))
            errors.Add("Either a node image, or a base image to bake one from, is required.");
        if (string.IsNullOrWhiteSpace(ControlPlaneFlavor)) errors.Add("Control-plane flavor is required.");
        if (ControlPlaneCount < 1) errors.Add("At least one control-plane node is required.");
        if (WorkerPools.Count == 0) errors.Add("At least one worker pool is required.");
        if (WorkerPools.Any(p => string.IsNullOrWhiteSpace(p.Flavor))) errors.Add("Every worker pool needs a flavor.");
        if (WorkerPools.Any(p => string.IsNullOrWhiteSpace(p.Name))) errors.Add("Every worker pool needs a name.");
        // Pool names become CAPI resource names, so duplicates would silently collapse two pools
        // into one — which is the failure this whole phase exists to remove.
        if (WorkerPools.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != WorkerPools.Count)
            errors.Add("Worker pool names must be unique.");
        if (ControlPlaneCount % 2 == 0)
            errors.Add("Control-plane count must be odd — etcd needs a majority to keep quorum.");
        foreach (WorkerPool p in WorkerPools.Where(p => p.MinCount is not null || p.MaxCount is not null))
        {
            if (p.MinCount is null || p.MaxCount is null)
                errors.Add($"Worker pool '{p.Name}': autoscaling needs both a min and a max node count.");
            else if (p.MinCount < 0 || p.MaxCount < p.MinCount || p.MaxCount < 1)
                errors.Add($"Worker pool '{p.Name}': autoscale bounds must satisfy 0 ≤ min ≤ max and max ≥ 1.");
        }
        if (string.IsNullOrWhiteSpace(ExternalNetworkId)) errors.Add("External network ID is required.");
        // Not required: an empty bootstrap image follows the node image, which is the norm.
        if (string.IsNullOrWhiteSpace(EffectiveBootstrapImageName)) errors.Add("Bootstrap VM image name is required.");
        if (string.IsNullOrWhiteSpace(BootstrapFlavor)) errors.Add("Bootstrap VM flavor is required.");
        if (string.IsNullOrWhiteSpace(BootstrapNetworkId)) errors.Add("Bootstrap VM network ID is required.");
        return errors;
    }
}

/// <summary>
/// Builds the inputs the CAPO <c>clusterctl generate cluster</c> template consumes:
/// a <c>clouds.yaml</c> (used both by CAPO's identityRef and the in-cluster
/// cloud-config) and the <c>OPENSTACK_*</c> environment variables.
/// </summary>
public static class CapiTemplateInputs
{
    public const string CloudName = "openstack";

    /// <summary>
    /// Secret holding clouds.yaml on the management plane, which the OpenStackCluster's identityRef
    /// names. CAPO reads its credentials from here rather than from the environment, which is what
    /// lets the cluster keep reconciling after the pivot.
    /// </summary>
    public const string CloudSecretName = "openstack-cloud-config";

    /// <summary>
    /// Renders a clouds.yaml for the given connection using an application credential
    /// (preferred: revocable, no password on the node).
    /// </summary>
    public static string BuildCloudsYaml(Data.OpenStackConnection connection, ApplicationCredential appCred)
    {
        string authUrl = OpenStackKeystoneClient.NormalizeV3(connection.AuthUrl);
        StringBuilder sb = new();
        sb.AppendLine("clouds:");
        sb.AppendLine($"  {CloudName}:");
        sb.AppendLine("    auth_type: v3applicationcredential");
        sb.AppendLine("    auth:");
        sb.AppendLine($"      auth_url: {authUrl}");
        sb.AppendLine($"      application_credential_id: {appCred.Id}");
        sb.AppendLine($"      application_credential_secret: {appCred.Secret}");
        if (!string.IsNullOrWhiteSpace(connection.Region))
            sb.AppendLine($"    region_name: {connection.Region}");
        sb.AppendLine("    interface: public");
        sb.AppendLine("    identity_api_version: 3");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the INI-format <c>cloud.conf</c> consumed by the in-cluster
    /// cloud-controller-manager and Cinder CSI (application-credential auth).
    /// </summary>
    public static string BuildCloudConf(
        Data.OpenStackConnection connection, ApplicationCredential appCred, OpenStackProvisioningConfig config)
    {
        string authUrl = OpenStackKeystoneClient.NormalizeV3(connection.AuthUrl);
        StringBuilder sb = new();
        sb.AppendLine("[Global]");
        sb.AppendLine($"auth-url={authUrl}");
        sb.AppendLine($"application-credential-id={appCred.Id}");
        sb.AppendLine($"application-credential-secret={appCred.Secret}");
        if (!string.IsNullOrWhiteSpace(connection.Region))
            sb.AppendLine($"region={connection.Region}");
        sb.AppendLine();
        sb.AppendLine("[LoadBalancer]");
        // Lets Service type=LoadBalancer allocate Octavia LBs with floating IPs.
        sb.AppendLine($"floating-network-id={config.ExternalNetworkId}");
        sb.AppendLine();
        sb.AppendLine("[BlockStorage]");
        sb.AppendLine("bs-version=v3");
        sb.AppendLine("ignore-volume-az=true");
        return sb.ToString();
    }

}
