using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// These manifests are what a cluster is made of, and the failure they replace was silent: the
/// stock clusterctl template emits one MachineDeployment, so a config with two worker pools
/// produced one pool carrying the first pool's flavor and the sum of both counts. Nothing errored.
/// You found out by counting nodes.
/// </summary>
public class CapiManifestBuilderTests
{
    private static OpenStackProvisioningConfig Config(Action<OpenStackProvisioningConfig>? tweak = null)
    {
        OpenStackProvisioningConfig config = new()
        {
            OpenStackConnectionId = Guid.NewGuid(),
            ClusterName = "prod-eu-1",
            KubernetesVersion = "v1.31.4",
            NodeImageName = "entkube-k8s-v1.31.4-202609101430",
            ControlPlaneCount = 3,
            ControlPlaneFlavor = "b.4c8gb",
            ExternalNetworkId = "ext-net-123",
            BootstrapFlavor = "b.2c4gb",
            BootstrapNetworkId = "tenant-net-456",
            BaseImageName = "ubuntu-22.04",
            WorkerPools =
            [
                new WorkerPool { Name = "general", Count = 3, Flavor = "b.4c8gb" },
                new WorkerPool { Name = "memory", Count = 2, Flavor = "b.8c32gb", DiskGb = 200 }
            ]
        };
        tweak?.Invoke(config);
        return config;
    }

    private static ClusterManifestInputs Inputs(ApiEndpointStrategy endpoint = ApiEndpointStrategy.Octavia) => new()
    {
        NodeImageName = "entkube-k8s-v1.31.4-202609101430",
        CloudSecretName = "openstack-cloud-config",
        CloudName = "openstack",
        ApiEndpoint = endpoint
    };

    private static List<YamlMappingNode> Documents(string manifest)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(manifest));
        return stream.Documents.Select(d => (YamlMappingNode)d.RootNode).ToList();
    }

    private static IEnumerable<YamlMappingNode> OfKind(string manifest, string kind) =>
        Documents(manifest).Where(d => d.Children.TryGetValue("kind", out YamlNode? k) && k.ToString() == kind);

    private static string Name(YamlMappingNode doc) =>
        ((YamlMappingNode)doc["metadata"])["name"].ToString();

    // ── The reason this exists ──

    [Fact]
    public void Every_worker_pool_becomes_its_own_machine_deployment()
    {
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        List<YamlMappingNode> deployments = OfKind(manifest, "MachineDeployment").ToList();

        deployments.Should().HaveCount(2);
        deployments.Select(Name).Should().BeEquivalentTo("prod-eu-1-general", "prod-eu-1-memory");
    }

    [Fact]
    public void Each_pool_keeps_its_own_flavor_and_count()
    {
        // The old path gave both pools the first one's flavor and one combined replica count.
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        YamlMappingNode memory = OfKind(manifest, "MachineDeployment").Single(d => Name(d) == "prod-eu-1-memory");
        ((YamlMappingNode)memory["spec"])["replicas"].ToString().Should().Be("2");

        YamlMappingNode memoryTemplate = OfKind(manifest, "OpenStackMachineTemplate").Single(d => Name(d) == "prod-eu-1-memory");
        YamlMappingNode spec = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)memoryTemplate["spec"])["template"])["spec"];
        spec["flavor"].ToString().Should().Be("b.8c32gb");

        YamlMappingNode general = OfKind(manifest, "OpenStackMachineTemplate").Single(d => Name(d) == "prod-eu-1-general");
        YamlMappingNode generalSpec = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)general["spec"])["template"])["spec"];
        generalSpec["flavor"].ToString().Should().Be("b.4c8gb");
    }

    [Fact]
    public void A_pool_with_a_disk_size_gets_a_root_volume_and_one_without_does_not()
    {
        // Asking for a root volume the operator did not want moves them off the flavor's own disk
        // and onto Cinder, which is a different performance and failure story.
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        // Navigated, so a rootVolume that landed at the wrong depth cannot pass as present.
        YamlMappingNode memory = OfKind(manifest, "OpenStackMachineTemplate").Single(d => Name(d) == "prod-eu-1-memory");
        YamlMappingNode memorySpec = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)memory["spec"])["template"])["spec"];
        ((YamlMappingNode)memorySpec["rootVolume"])["sizeGiB"].ToString().Should().Be("200");

        YamlMappingNode general = OfKind(manifest, "OpenStackMachineTemplate").Single(d => Name(d) == "prod-eu-1-general");
        YamlMappingNode spec = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)general["spec"])["template"])["spec"];
        spec.Children.Should().NotContainKey(new YamlScalarNode("rootVolume"));
    }

    [Fact]
    public void Pool_names_must_be_unique_because_they_become_resource_names()
    {
        // Two pools called the same thing would silently collapse into one — the exact failure
        // this phase exists to remove, reintroduced by a typo.
        OpenStackProvisioningConfig config = Config(c => c.WorkerPools =
        [
            new WorkerPool { Name = "general", Count = 3, Flavor = "b.4c8gb" },
            new WorkerPool { Name = "General", Count = 2, Flavor = "b.8c32gb" }
        ]);

        Action build = () => CapiManifestBuilder.Build(config, Inputs());

        build.Should().Throw<InvalidOperationException>().WithMessage("*unique*");
    }

    // ── Control plane ──

    [Fact]
    public void The_control_plane_rolls_one_machine_at_a_time()
    {
        // etcd loses quorum if two of three go at once, and a rollout that takes the cluster down
        // is indistinguishable from an outage while it happens.
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        YamlMappingNode kcp = OfKind(manifest, "KubeadmControlPlane").Single();
        YamlMappingNode rollout = (YamlMappingNode)((YamlMappingNode)kcp["spec"])["rolloutStrategy"];
        ((YamlMappingNode)rollout["rollingUpdate"])["maxSurge"].ToString().Should().Be("1");
    }

    [Fact]
    public void An_even_control_plane_count_is_refused()
    {
        // Four members tolerate the same single failure three do, and cost more to lose.
        Action build = () => CapiManifestBuilder.Build(Config(c => c.ControlPlaneCount = 2), Inputs());

        build.Should().Throw<InvalidOperationException>().WithMessage("*quorum*");
    }

    [Fact]
    public void Every_node_defers_to_the_cloud_controller_manager()
    {
        // Without cloud-provider: external the kubelet registers its own addresses and the CCM
        // never gets to correct them, which surfaces later as load balancers pointed at nothing.
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        manifest.Split("cloud-provider: external").Length.Should().BeGreaterThan(3);
    }

    // ── Cloud shape ──

    [Fact]
    public void Octavia_gets_a_load_balancer_in_front_of_the_control_plane()
    {
        string manifest = CapiManifestBuilder.Build(Config(), Inputs(ApiEndpointStrategy.Octavia));

        YamlMappingNode cluster = OfKind(manifest, "OpenStackCluster").Single();
        YamlMappingNode lb = (YamlMappingNode)((YamlMappingNode)cluster["spec"])["apiServerLoadBalancer"];
        lb["enabled"].ToString().Should().Be("true");
    }

    [Fact]
    public void A_cloud_without_octavia_falls_back_to_a_floating_ip()
    {
        string manifest = CapiManifestBuilder.Build(Config(), Inputs(ApiEndpointStrategy.FloatingIp));

        YamlMappingNode cluster = OfKind(manifest, "OpenStackCluster").Single();
        YamlMappingNode lb = (YamlMappingNode)((YamlMappingNode)cluster["spec"])["apiServerLoadBalancer"];
        lb["enabled"].ToString().Should().Be("false");
    }

    [Fact]
    public void With_no_network_chosen_capo_is_asked_to_create_one()
    {
        // The from-nothing path. The DNS servers matter: nodes on a subnet CAPO just created have
        // none until something says so, and a node that cannot resolve anything looks like a
        // registry problem.
        string manifest = CapiManifestBuilder.Build(Config(c => c.NodeNetworkId = null), Inputs());

        manifest.Should().Contain("managedSubnets");
        manifest.Should().Contain("dnsNameservers: [1.1.1.1, 8.8.8.8]");
    }

    [Fact]
    public void An_existing_network_is_used_as_given()
    {
        string manifest = CapiManifestBuilder.Build(Config(c => c.NodeNetworkId = "tenant-net-456"), Inputs());

        manifest.Should().Contain("id: tenant-net-456");
        manifest.Should().NotContain("managedSubnets");
    }

    // ── Autoscaling and health ──

    [Fact]
    public void An_autoscaled_pool_carries_its_bounds_as_annotations()
    {
        // The cluster-autoscaler reads these from the MachineDeployment. They used to be patched on
        // after the pivot, which is a step a resumed run could skip — leaving an autoscaler with
        // nothing to scale and no error anywhere.
        OpenStackProvisioningConfig config = Config(c => c.WorkerPools =
        [
            new WorkerPool { Name = "general", Count = 3, Flavor = "b.4c8gb", MinCount = 2, MaxCount = 10 }
        ]);

        string manifest = CapiManifestBuilder.Build(config, Inputs());

        manifest.Should().Contain("cluster-api-autoscaler-node-group-min-size: \"2\"");
        manifest.Should().Contain("cluster-api-autoscaler-node-group-max-size: \"10\"");
    }

    [Fact]
    public void A_fixed_pool_carries_no_autoscaler_annotations()
    {
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        manifest.Should().NotContain("cluster-api-autoscaler-node-group");
    }

    [Fact]
    public void Unhealthy_nodes_are_replaced_but_never_en_masse()
    {
        // maxUnhealthy is the difference between repairing a dead node and rebuilding the cluster
        // during a cloud-wide wobble.
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        YamlMappingNode check = OfKind(manifest, "MachineHealthCheck").Single();
        ((YamlMappingNode)check["spec"])["maxUnhealthy"].ToString().Should().Be("40%");
    }

    // ── Shape of the whole ──

    [Fact]
    public void The_manifest_is_valid_yaml_with_every_document_it_needs()
    {
        string manifest = CapiManifestBuilder.Build(Config(), Inputs());

        List<string> kinds = Documents(manifest).Select(d => d["kind"].ToString()).ToList();

        kinds.Should().Contain("Cluster");
        kinds.Should().Contain("OpenStackCluster");
        kinds.Should().Contain("KubeadmControlPlane");
        kinds.Should().Contain("MachineHealthCheck");
        // Per pool: a machine template, a deployment and a kubeadm config template.
        kinds.Count(k => k == "OpenStackMachineTemplate").Should().Be(3);  // 2 pools + control plane
        kinds.Count(k => k == "KubeadmConfigTemplate").Should().Be(2);
    }

    [Fact]
    public void The_hostname_placeholder_reaches_the_cluster_untouched()
    {
        // kubeadm's own templating, which must arrive as literal braces rather than be interpolated
        // away on the way out of C#.
        CapiManifestBuilder.Build(Config(), Inputs())
            .Should().Contain("name: '{{ local_hostname }}'");
    }

    [Fact]
    public void Pool_labels_and_taints_are_applied_at_registration()
    {
        // Not afterwards: a taint added once the node is Ready is a taint the scheduler was free to
        // ignore for a minute, which for a pool that exists to keep workloads off it is the minute
        // that matters.
        OpenStackProvisioningConfig config = Config(c => c.WorkerPools =
        [
            new WorkerPool
            {
                Name = "gpu",
                Count = 2,
                Flavor = "g.8c32gb",
                Labels = new Dictionary<string, string> { ["workload"] = "gpu" },
                Taints = [new NodeTaint { Key = "nvidia.com/gpu", Value = "true", Effect = "NoSchedule" }]
            }
        ]);

        string manifest = CapiManifestBuilder.Build(config, Inputs());

        // Navigated rather than string-matched: indentation is semantic here, and a label written
        // one level out is a sibling of kubeletExtraArgs that kubeadm ignores in silence.
        YamlMappingNode template = OfKind(manifest, "KubeadmConfigTemplate").Single();
        YamlMappingNode registration = (YamlMappingNode)
            ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)template["spec"])["template"])["spec"])["joinConfiguration"];
        YamlMappingNode node = (YamlMappingNode)registration["nodeRegistration"];

        ((YamlMappingNode)node["kubeletExtraArgs"])["node-labels"].ToString().Should().Be("workload=gpu");

        YamlSequenceNode taints = (YamlSequenceNode)node["taints"];
        YamlMappingNode taint = (YamlMappingNode)taints.Children.Single();
        taint["key"].ToString().Should().Be("nvidia.com/gpu");
        taint["effect"].ToString().Should().Be("NoSchedule");
    }

    [Fact]
    public void A_pool_can_lag_the_control_plane_during_an_upgrade()
    {
        // Which is the whole shape of a Kubernetes upgrade: control plane first, pools one at a
        // time behind it.
        OpenStackProvisioningConfig config = Config(c => c.WorkerPools =
        [
            new WorkerPool { Name = "general", Count = 3, Flavor = "b.4c8gb", KubernetesVersion = "v1.30.5" }
        ]);

        string manifest = CapiManifestBuilder.Build(config, Inputs());

        YamlMappingNode kcp = OfKind(manifest, "KubeadmControlPlane").Single();
        ((YamlMappingNode)kcp["spec"])["version"].ToString().Should().Be("v1.31.4");
        manifest.Should().Contain("version: v1.30.5");
    }
}
