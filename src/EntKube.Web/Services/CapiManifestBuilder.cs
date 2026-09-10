using System.Text;

namespace EntKube.Web.Services;

/// <summary>
/// Authors the Cluster API manifests for a cluster, instead of asking
/// <c>clusterctl generate cluster</c> for them.
///
/// <para><b>Why not the stock template.</b> It emits exactly one MachineDeployment. Everything
/// else follows from that: <c>BuildEnv</c> had to collapse every worker pool into a single
/// <c>WORKER_MACHINE_COUNT</c> carrying the first pool's flavor, so a cluster configured with a
/// small general pool and a large memory pool got neither — it got one pool of the wrong size.
/// Pools that differ in flavor, disk, availability zone, labels or taints cannot be expressed
/// through it at all, and none of that is a gap upstream intends to close: the template is a
/// starting point to copy, which is what this is.</para>
///
/// <para>It also removes a dependency worth losing. <c>clusterctl generate</c> fetches its
/// templates over the network at run time, which is exactly the thing that does not work in a
/// customer network that cannot reach GitHub.</para>
/// </summary>
public static class CapiManifestBuilder
{
    /// <summary>
    /// kubeadm's own templating for the node's hostname, passed through untouched. It has to reach
    /// the cluster as literal braces, and a raw interpolated string cannot express those — so it
    /// arrives through an interpolation hole instead of being escaped.
    /// </summary>
    private const string LocalHostname = "'{{ local_hostname }}'";

    /// <summary>Namespace the cluster's CAPI objects live in on the management plane.</summary>
    public const string Namespace = "default";

    /// <summary>
    /// The complete set of documents for a cluster: the Cluster and its OpenStackCluster, the
    /// control plane and its machine template, and one MachineDeployment + OpenStackMachineTemplate
    /// per worker pool. Applied as one document to whichever management plane is in play.
    /// </summary>
    public static string Build(OpenStackProvisioningConfig config, ClusterManifestInputs inputs)
    {
        IReadOnlyList<string> errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Cannot author CAPI manifests: " + string.Join("; ", errors));
        }

        StringBuilder sb = new();

        Append(sb, ClusterDocument(config));
        Append(sb, OpenStackClusterDocument(config, inputs));
        Append(sb, ControlPlaneTemplateDocument(config, inputs));
        Append(sb, ControlPlaneDocument(config));

        foreach (WorkerPool pool in config.WorkerPools)
        {
            Append(sb, WorkerTemplateDocument(config, inputs, pool));
            Append(sb, MachineDeploymentDocument(config, pool));
        }

        // One health check per cluster, matching every worker. Nodes fail, and a cluster that does
        // not replace them silently shrinks until something that mattered was on the last one.
        Append(sb, MachineHealthCheckDocument(config));

        return sb.ToString();
    }

    // ──────── Cluster ────────

    private static string ClusterDocument(OpenStackProvisioningConfig config) => $"""
        apiVersion: cluster.x-k8s.io/v1beta1
        kind: Cluster
        metadata:
          name: {config.ClusterName}
          namespace: {Namespace}
        spec:
          clusterNetwork:
            pods:
              cidrBlocks: [{config.PodCidr}]
            services:
              cidrBlocks: [{config.ServiceCidr}]
          infrastructureRef:
            apiVersion: infrastructure.cluster.x-k8s.io/v1beta1
            kind: OpenStackCluster
            name: {config.ClusterName}
          controlPlaneRef:
            apiVersion: controlplane.cluster.x-k8s.io/v1beta1
            kind: KubeadmControlPlane
            name: {config.ClusterName}-control-plane
        """;

    private static string OpenStackClusterDocument(OpenStackProvisioningConfig config, ClusterManifestInputs inputs)
    {
        // Composed line by line rather than from raw literals. Indentation is semantic in YAML, and
        // in a raw string it is set by where the closing delimiter sits — which put this whole block
        // two levels too deep and made apiServerLoadBalancer a child of the security-group settings.
        List<string> lines =
        [
            "apiVersion: infrastructure.cluster.x-k8s.io/v1beta1",
            "kind: OpenStackCluster",
            "metadata:",
            $"  name: {config.ClusterName}",
            $"  namespace: {Namespace}",
            "spec:",
            "  identityRef:",
            $"    name: {inputs.CloudSecretName}",
            $"    cloudName: {inputs.CloudName}",
            "  externalNetwork:",
            $"    id: {config.ExternalNetworkId}",
            "  managedSecurityGroups:",
            "    allowAllInClusterTraffic: false",
        ];

        // Octavia puts a load balancer in front of every control-plane node, which is the only
        // arrangement where losing one of them is survivable. Where the cloud has no Octavia the
        // endpoint rides a floating IP instead, and an HA control plane there is a promise we
        // cannot keep — so the caller is told at discovery, not here.
        if (inputs.ApiEndpoint == ApiEndpointStrategy.Octavia)
        {
            lines.Add("  apiServerLoadBalancer:");
            lines.Add("    enabled: true");
        }
        else
        {
            lines.Add("  apiServerLoadBalancer:");
            lines.Add("    enabled: false");
            lines.Add("  disableAPIServerFloatingIP: false");
        }

        if (!string.IsNullOrWhiteSpace(config.NodeNetworkId))
        {
            lines.Add("  network:");
            lines.Add($"    id: {config.NodeNetworkId}");
        }
        else
        {
            // No network given: CAPO creates a router, network and subnet of its own. That is the
            // from-nothing path, and the DNS servers matter because nodes on a subnet that was just
            // created have none until something says so — which presents as a registry failure.
            lines.Add("  managedSubnets:");
            lines.Add($"    - cidr: {inputs.ManagedSubnetCidr}");
            lines.Add($"      dnsNameservers: [{FormatList(config.DnsNameservers)}]");
        }

        return string.Join("\n", lines);
    }

    // ──────── Control plane ────────

    private static string ControlPlaneTemplateDocument(OpenStackProvisioningConfig config, ClusterManifestInputs inputs) => $"""
        apiVersion: infrastructure.cluster.x-k8s.io/v1beta1
        kind: OpenStackMachineTemplate
        metadata:
          name: {config.ClusterName}-control-plane
          namespace: {Namespace}
        spec:
          template:
            spec:
              flavor: {config.ControlPlaneFlavor}
              image:
                filter:
                  name: {inputs.NodeImageName}
              sshKeyName: {config.ClusterName}-key{RootVolume(inputs.ControlPlaneDiskGb)}
        """;

    private static string ControlPlaneDocument(OpenStackProvisioningConfig config) => $"""
        apiVersion: controlplane.cluster.x-k8s.io/v1beta1
        kind: KubeadmControlPlane
        metadata:
          name: {config.ClusterName}-control-plane
          namespace: {Namespace}
        spec:
          replicas: {config.ControlPlaneCount}
          version: {MachineImageNaming.Normalize(config.KubernetesVersion)}
          machineTemplate:
            infrastructureRef:
              apiVersion: infrastructure.cluster.x-k8s.io/v1beta1
              kind: OpenStackMachineTemplate
              name: {config.ClusterName}-control-plane
          kubeadmConfigSpec:
            clusterConfiguration:
              apiServer:
                extraArgs:
                  cloud-provider: external
              controllerManager:
                extraArgs:
                  cloud-provider: external
            initConfiguration:
              nodeRegistration:
                name: {LocalHostname}
                kubeletExtraArgs:
                  # The cloud controller manager owns node addresses and lifecycle. Without this
                  # the kubelet registers itself and the CCM never gets to correct it.
                  cloud-provider: external
            joinConfiguration:
              nodeRegistration:
                name: {LocalHostname}
                kubeletExtraArgs:
                  cloud-provider: external
          rolloutStrategy:
            type: RollingUpdate
            rollingUpdate:
              # One at a time: etcd loses quorum if two members of three go at once.
              maxSurge: 1
        """;

    // ──────── Worker pools ────────

    private static string WorkerTemplateDocument(
        OpenStackProvisioningConfig config, ClusterManifestInputs inputs, WorkerPool pool)
    {
        string name = PoolResourceName(config, pool);
        StringBuilder sb = new();

        sb.Append($"""
            apiVersion: infrastructure.cluster.x-k8s.io/v1beta1
            kind: OpenStackMachineTemplate
            metadata:
              name: {name}
              namespace: {Namespace}
            spec:
              template:
                spec:
                  flavor: {pool.Flavor}
                  image:
                    filter:
                      name: {inputs.NodeImageName}
                  sshKeyName: {config.ClusterName}-key{RootVolume(pool.DiskGb)}
            """);

        return sb.ToString();
    }

    private static string MachineDeploymentDocument(OpenStackProvisioningConfig config, WorkerPool pool)
    {
        string name = PoolResourceName(config, pool);
        string version = MachineImageNaming.Normalize(pool.KubernetesVersion ?? config.KubernetesVersion);
        string zone = pool.FailureDomain ?? config.FailureDomain ?? "nova";

        List<string> lines =
        [
            "apiVersion: cluster.x-k8s.io/v1beta1",
            "kind: MachineDeployment",
            "metadata:",
            $"  name: {name}",
            $"  namespace: {Namespace}",
        ];

        // Autoscaler bounds live as annotations on the MachineDeployment; the cluster-autoscaler
        // reads them to size the group. Written here rather than patched on after the pivot, which
        // is a step a resumed run could skip — leaving an autoscaler with nothing to scale.
        if (pool.Autoscale)
        {
            lines.Add("  annotations:");
            lines.Add($"    cluster.x-k8s.io/cluster-api-autoscaler-node-group-min-size: \"{pool.MinCount}\"");
            lines.Add($"    cluster.x-k8s.io/cluster-api-autoscaler-node-group-max-size: \"{pool.MaxCount}\"");
        }

        lines.AddRange(
        [
            "spec:",
            $"  clusterName: {config.ClusterName}",
            $"  replicas: {pool.Count}",
            "  selector:",
            "    matchLabels:",
            $"      cluster.x-k8s.io/deployment-name: {name}",
            "  template:",
            "    metadata:",
            "      labels:",
            $"        cluster.x-k8s.io/deployment-name: {name}",
            "    spec:",
            $"      clusterName: {config.ClusterName}",
            $"      version: {version}",
            $"      failureDomain: {zone}",
            "      infrastructureRef:",
            "        apiVersion: infrastructure.cluster.x-k8s.io/v1beta1",
            "        kind: OpenStackMachineTemplate",
            $"        name: {name}",
            "      bootstrap:",
            "        configRef:",
            "          apiVersion: bootstrap.cluster.x-k8s.io/v1beta1",
            "          kind: KubeadmConfigTemplate",
            $"          name: {name}",
            "---",
            "apiVersion: bootstrap.cluster.x-k8s.io/v1beta1",
            "kind: KubeadmConfigTemplate",
            "metadata:",
            $"  name: {name}",
            $"  namespace: {Namespace}",
            "spec:",
            "  template:",
            "    spec:",
            "      joinConfiguration:",
            "        nodeRegistration:",
            $"          name: {LocalHostname}",
            "          kubeletExtraArgs:",
            "            cloud-provider: external",
        ]);

        // Labels and taints go on at registration rather than afterwards. A taint added once the
        // node is Ready is a taint the scheduler was free to ignore for a minute — which, for a
        // pool that exists to keep workloads off it, is the minute that matters.
        if (pool.Labels.Count > 0)
        {
            lines.Add($"            node-labels: {string.Join(",", pool.Labels.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        if (pool.Taints.Count > 0)
        {
            lines.Add("          taints:");
            foreach (NodeTaint taint in pool.Taints)
            {
                lines.Add($"            - key: {taint.Key}");
                lines.Add($"              value: {taint.Value}");
                lines.Add($"              effect: {taint.Effect}");
            }
        }

        return string.Join("\n", lines);
    }

    // ──────── Health ────────

    private static string MachineHealthCheckDocument(OpenStackProvisioningConfig config) => $"""
        apiVersion: cluster.x-k8s.io/v1beta1
        kind: MachineHealthCheck
        metadata:
          name: {config.ClusterName}-workers
          namespace: {Namespace}
        spec:
          clusterName: {config.ClusterName}
          selector:
            matchLabels:
              cluster.x-k8s.io/cluster-name: {config.ClusterName}
          # Conservative on purpose: replacing a node that was merely slow to report costs more
          # than waiting does, and a cloud with a wobbly metadata service can otherwise produce a
          # cluster that endlessly rebuilds itself.
          unhealthyConditions:
            - type: Ready
              status: Unknown
              timeout: 300s
            - type: Ready
              status: "False"
              timeout: 300s
          # Never remediate past this, or a control-plane-wide outage turns into a mass rebuild.
          maxUnhealthy: 40%
        """;

    // ──────── Shared bits ────────

    /// <summary>
    /// The resource name for a pool. Deliberately <c>{cluster}-{pool}</c>: the machine deployments
    /// are addressed by this from day-2 operations and from the autoscaler's annotations, so it has
    /// to be derivable rather than remembered.
    /// </summary>
    public static string PoolResourceName(OpenStackProvisioningConfig config, WorkerPool pool) =>
        $"{config.ClusterName}-{pool.Name}";

    private static string RootVolume(int diskGb) =>
        diskGb > 0
            ? $"""

                    rootVolume:
                      sizeGiB: {diskGb}
              """.TrimEnd()
            : "";

    private static string FormatList(string commaSeparated) =>
        string.Join(", ", commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void Append(StringBuilder sb, string document)
    {
        if (sb.Length > 0)
        {
            sb.Append("---\n");
        }
        sb.Append(document.TrimEnd('\n'));
        sb.Append('\n');
    }
}

/// <summary>
/// The facts about a particular cloud and build that the spec does not carry: which image was
/// baked, how the API server is reached here, and what the CAPO identity secret is called.
/// </summary>
public sealed class ClusterManifestInputs
{
    /// <summary>The node image, resolved from the Kubernetes version by the image builder.</summary>
    public required string NodeImageName { get; init; }

    /// <summary>Secret holding clouds.yaml, which CAPO authenticates with.</summary>
    public required string CloudSecretName { get; init; }

    /// <summary>Cloud entry within that clouds.yaml.</summary>
    public required string CloudName { get; init; }

    /// <summary>Octavia where the cloud has it, a floating IP where it does not.</summary>
    public required ApiEndpointStrategy ApiEndpoint { get; init; }

    /// <summary>Root disk for control-plane machines; 0 leaves the flavor's own disk.</summary>
    public int ControlPlaneDiskGb { get; init; }

    /// <summary>Subnet CAPO creates when no existing network was chosen.</summary>
    public string ManagedSubnetCidr { get; init; } = "10.6.0.0/24";
}
