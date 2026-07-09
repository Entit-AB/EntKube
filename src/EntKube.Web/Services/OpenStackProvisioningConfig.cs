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

    /// <summary>Glance image the control-plane/worker nodes boot from (kubeadm-ready, cloud-init).</summary>
    public string NodeImageName { get; set; } = "";

    public int ControlPlaneCount { get; set; } = 1;
    public string ControlPlaneFlavor { get; set; } = "";

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

    /// <summary>Glance image for the throwaway k3s bootstrap VM (Ubuntu 22.04+ with cloud-init).</summary>
    public string BootstrapImageName { get; set; } = "";

    /// <summary>Flavor for the throwaway bootstrap VM (a small 2c/4gb flavor is plenty).</summary>
    public string BootstrapFlavor { get; set; } = "";

    /// <summary>Tenant network the bootstrap VM attaches to (must reach the OpenStack + internet).</summary>
    public string BootstrapNetworkId { get; set; } = "";

    /// <summary>Default login user of the bootstrap image (Ubuntu images use "ubuntu").</summary>
    public string BootstrapSshUser { get; set; } = "ubuntu";

    public int TotalWorkerCount => WorkerPools.Sum(p => p.Count);

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
        if (string.IsNullOrWhiteSpace(NodeImageName)) errors.Add("Node image name is required.");
        if (string.IsNullOrWhiteSpace(ControlPlaneFlavor)) errors.Add("Control-plane flavor is required.");
        if (ControlPlaneCount < 1) errors.Add("At least one control-plane node is required.");
        if (WorkerPools.Count == 0) errors.Add("At least one worker pool is required.");
        if (WorkerPools.Any(p => string.IsNullOrWhiteSpace(p.Flavor))) errors.Add("Every worker pool needs a flavor.");
        if (string.IsNullOrWhiteSpace(ExternalNetworkId)) errors.Add("External network ID is required.");
        if (string.IsNullOrWhiteSpace(BootstrapImageName)) errors.Add("Bootstrap VM image name is required.");
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

    /// <summary>
    /// The <c>OPENSTACK_*</c> / cluster env vars consumed by <c>clusterctl generate cluster</c>.
    /// </summary>
    public static Dictionary<string, string> BuildEnv(
        OpenStackProvisioningConfig config, string cloudsYaml)
    {
        string cloudsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudsYaml));
        return new Dictionary<string, string>
        {
            ["CLUSTER_NAME"] = config.ClusterName,
            ["KUBERNETES_VERSION"] = config.KubernetesVersion,
            ["CONTROL_PLANE_MACHINE_COUNT"] = config.ControlPlaneCount.ToString(),
            ["WORKER_MACHINE_COUNT"] = config.TotalWorkerCount.ToString(),
            ["OPENSTACK_CLOUD"] = CloudName,
            ["OPENSTACK_CLOUD_YAML_B64"] = cloudsB64,
            ["OPENSTACK_CLOUD_CACERT_B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("\n")),
            ["OPENSTACK_CONTROL_PLANE_MACHINE_FLAVOR"] = config.ControlPlaneFlavor,
            ["OPENSTACK_NODE_MACHINE_FLAVOR"] = config.WorkerPools.FirstOrDefault()?.Flavor ?? config.ControlPlaneFlavor,
            ["OPENSTACK_IMAGE_NAME"] = config.NodeImageName,
            ["OPENSTACK_EXTERNAL_NETWORK_ID"] = config.ExternalNetworkId,
            ["OPENSTACK_DNS_NAMESERVERS"] = config.DnsNameservers,
            ["OPENSTACK_FAILURE_DOMAIN"] = config.FailureDomain ?? "nova",
            ["OPENSTACK_SSH_KEY_NAME"] = $"{config.ClusterName}-key",
        };
    }
}
