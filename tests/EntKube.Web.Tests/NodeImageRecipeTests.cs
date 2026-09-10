using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The image recipe and the kubeadm bootstrap are the content that decides whether a cluster comes
/// up at all, and they cannot be tried cheaply — a mistake costs a VM boot and surfaces as a
/// kubelet that will not start, diagnosed from a console log an hour later. So the decisions in
/// them are asserted here instead.
/// </summary>
public class NodeImageRecipeTests
{
    // ── Version handling ──

    [Theory]
    [InlineData("1.31.4", "v1.31.4")]
    [InlineData("v1.31.4", "v1.31.4")]
    [InlineData("  v1.31.4  ", "v1.31.4")]
    [InlineData("v1.30.2+k3s1", "v1.30.2")]
    public void Versions_normalize_to_one_form(string input, string expected)
    {
        // A lookup for "the image serving 1.31.4" must not depend on how the version was typed.
        MachineImageNaming.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void The_package_repository_is_addressed_by_minor_not_patch()
    {
        // pkgs.k8s.io publishes one repository per minor. Asking for v1.31.4 yields a 404 and an
        // install that fails halfway through the bake.
        MachineImageNaming.PackageRepoStream("v1.31.4").Should().Be("v1.31");
    }

    [Fact]
    public void Packages_are_pinned_to_the_requested_patch()
    {
        MachineImageNaming.AptVersionPin("v1.31.4").Should().Be("1.31.4-*");
    }

    [Fact]
    public void Two_builds_of_one_version_get_distinct_names()
    {
        // Glance allows duplicate names, and two images called the same thing is how you end up
        // unable to say which one a cluster is running.
        string first = MachineImageNaming.ImageName("v1.31.4", new DateTime(2026, 9, 10, 14, 30, 0, DateTimeKind.Utc));
        string second = MachineImageNaming.ImageName("v1.31.4", new DateTime(2026, 9, 10, 15, 45, 0, DateTimeKind.Utc));

        first.Should().NotBe(second);
        first.Should().Contain("v1.31.4");
    }

    [Fact]
    public void The_version_travels_as_metadata_so_it_survives_a_rename()
    {
        Dictionary<string, string> props = MachineImageNaming.Properties("1.31.4", "ubuntu-22.04", DateTime.UtcNow);

        props[MachineImageNaming.VersionProperty].Should().Be("v1.31.4");
        props[MachineImageNaming.BaseImageProperty].Should().Be("ubuntu-22.04");
        props.Should().ContainKey(MachineImageNaming.BuiltAtProperty);
    }

    // ── The bake ──

    [Fact]
    public void The_bake_pins_every_kubernetes_package_and_holds_it()
    {
        string script = NodeImageRecipe.BakeScript("v1.31.4");

        script.Should().Contain("kubelet=1.31.4-*");
        script.Should().Contain("kubeadm=1.31.4-*");
        script.Should().Contain("kubectl=1.31.4-*");
        // Without the hold, an unattended-upgrade on a running node moves kubelet out from under
        // a control plane it is supposed to match.
        script.Should().Contain("apt-mark hold kubelet kubeadm kubectl");
    }

    [Fact]
    public void The_bake_prepares_the_things_a_kubelet_refuses_to_start_without()
    {
        string script = NodeImageRecipe.BakeScript("v1.31.4");

        script.Should().Contain("swapoff -a");
        script.Should().Contain("br_netfilter");
        script.Should().Contain("net.bridge.bridge-nf-call-iptables  = 1");
        script.Should().Contain("net.ipv4.ip_forward                 = 1");
    }

    [Fact]
    public void Containerd_uses_the_systemd_cgroup_driver()
    {
        // Kubelet defaults to systemd. A containerd on cgroupfs starts pods and then has them
        // killed on the first memory accounting the two disagree about — which looks like a
        // workload problem, not a configuration one.
        NodeImageRecipe.BakeScript("v1.31.4")
            .Should().Contain("s/SystemdCgroup = false/SystemdCgroup = true/");
    }

    [Fact]
    public void The_bake_prepulls_control_plane_images()
    {
        // The point of baking: a node that boots needs no registry.
        NodeImageRecipe.BakeScript("v1.31.4")
            .Should().Contain("kubeadm config images pull --kubernetes-version v1.31.4");
    }

    [Fact]
    public void A_failed_bake_leaves_a_marker_rather_than_hanging()
    {
        string script = NodeImageRecipe.BakeScript("v1.31.4");

        // Without this the builder cannot tell "still installing" from "broken" until it times
        // out, by which point the useful output has scrolled away.
        script.Should().Contain($"touch {NodeImageRecipe.FailedMarker}");
        script.Should().Contain($"touch {NodeImageRecipe.ReadyMarker}");
        script.Should().Contain("trap");
    }

    [Fact]
    public void Generalizing_strips_the_identity_a_snapshot_must_not_carry()
    {
        string script = NodeImageRecipe.GeneralizeScript("ubuntu");

        script.Should().Contain("/etc/machine-id");      // duplicates break DHCP leases and journals
        script.Should().Contain("rm -f /etc/ssh/ssh_host_*"); // every node presenting the same host key
        script.Should().Contain("cloud-init clean");     // or cloud-init believes it has already run
        script.Should().Contain("/home/ubuntu/.ssh/authorized_keys"); // the build key must not ship
    }

    [Fact]
    public void The_bake_cloud_init_is_a_cloud_config_document_carrying_the_script()
    {
        string cloudInit = NodeImageRecipe.BuildCloudInit("v1.31.4");

        cloudInit.Should().StartWith("#cloud-config\n");
        cloudInit.Should().Contain("/opt/entkube-bake.sh");
        // The script body must be indented under content:, or cloud-init silently writes nothing.
        cloudInit.Should().Contain("      #!/bin/bash");
    }

    // ── The kubeadm bootstrap ──

    [Fact]
    public void The_bootstrap_untaints_itself_or_nothing_ever_schedules()
    {
        // k3s ran workloads on its single node. kubeadm taints the control plane, and CAPI's
        // controllers then sit Pending forever with no obvious cause.
        NodeImageRecipe.BootstrapCloudInit("v1.31.4", "203.0.113.10", "192.168.0.0/16", "https://cni")
            .Should().Contain("kubectl taint nodes --all node-role.kubernetes.io/control-plane-");
    }

    [Fact]
    public void The_bootstrap_installs_a_cni_before_declaring_itself_ready()
    {
        string cloudInit = NodeImageRecipe.BootstrapCloudInit("v1.31.4", "203.0.113.10", "192.168.0.0/16", "https://cni/calico.yaml");

        cloudInit.Should().Contain("kubectl apply -f https://cni/calico.yaml");

        // Order matters and is the whole reason the marker exists: clusterctl init installs
        // cert-manager, whose webhooks must answer, which needs a pod network.
        int cni = cloudInit.IndexOf("kubectl apply -f https://cni/calico.yaml", StringComparison.Ordinal);
        int ready = cloudInit.IndexOf(NodeImageRecipe.BootstrapReadyMarker, StringComparison.Ordinal);
        cni.Should().BeLessThan(ready);
    }

    [Fact]
    public void The_floating_ip_is_a_certificate_san()
    {
        // The fetched kubeconfig names the node's private address and gets rewritten to the
        // floating IP. That only works if the certificate covers it.
        NodeImageRecipe.BootstrapCloudInit("v1.31.4", "203.0.113.10", "192.168.0.0/16", "https://cni")
            .Should().Contain("- 203.0.113.10");
    }

    [Fact]
    public void The_bootstrap_pod_cidr_matches_the_cluster_it_is_told_about()
    {
        // A podSubnet that disagrees with the CNI manifest produces a cluster whose pods cannot
        // route, which looks like a CNI bug rather than a two-line mismatch.
        NodeImageRecipe.BootstrapCloudInit("v1.31.4", "203.0.113.10", "10.244.0.0/16", "https://cni")
            .Should().Contain("podSubnet: 10.244.0.0/16");
    }

    [Theory]
    [InlineData("v1.31.4", "kubeadm.k8s.io/v1beta4")]
    [InlineData("v1.32.0", "kubeadm.k8s.io/v1beta4")]
    [InlineData("v1.30.5", "kubeadm.k8s.io/v1beta3")]
    [InlineData("v1.29.0", "kubeadm.k8s.io/v1beta3")]
    public void The_kubeadm_config_api_follows_the_kubernetes_version(string version, string expected)
    {
        // v1beta4 arrived in 1.31. Sending it to an older kubeadm fails at parse, before anything
        // useful is logged.
        NodeImageRecipe.KubeadmApiVersion(version).Should().Be(expected);
    }

    [Fact]
    public void Nothing_in_the_bootstrap_reaches_for_k3s()
    {
        string cloudInit = NodeImageRecipe.BootstrapCloudInit("v1.31.4", "203.0.113.10", "192.168.0.0/16", "https://cni");

        cloudInit.Should().NotContain("k3s");
        cloudInit.Should().Contain("kubeadm init");
    }
}

/// <summary>
/// Pointing a fetched kubeconfig at an address we can actually reach. The k3s version of this was
/// a blanket string replace, which is fine until a certificate happens to contain the same bytes.
/// </summary>
public class KubeconfigServerRewriteTests
{
    private const string Kubeconfig = """
        apiVersion: v1
        clusters:
        - cluster:
            certificate-authority-data: LS0tLTEyNy4wLjAuMS0tLQ==
            server: https://10.0.0.5:6443
          name: kubernetes
        contexts:
        - context:
            cluster: kubernetes
        """;

    [Fact]
    public void The_server_is_repointed_at_the_reachable_address()
    {
        string rewritten = ClusterProvisioningService.RewriteServerAddress(Kubeconfig, "203.0.113.10");

        rewritten.Should().Contain("server: https://203.0.113.10:6443");
        rewritten.Should().NotContain("10.0.0.5");
    }

    [Fact]
    public void Nothing_but_the_server_line_is_touched()
    {
        // A blanket replace of "127.0.0.1" would corrupt base64 certificate data that decodes to
        // something else entirely — and the failure appears as an unreadable CA, far from here.
        string rewritten = ClusterProvisioningService.RewriteServerAddress(Kubeconfig, "203.0.113.10");

        rewritten.Should().Contain("certificate-authority-data: LS0tLTEyNy4wLjAuMS0tLQ==");
        rewritten.Should().Contain("name: kubernetes");
    }

    [Fact]
    public void Indentation_survives_so_the_document_stays_valid()
    {
        string rewritten = ClusterProvisioningService.RewriteServerAddress(Kubeconfig, "203.0.113.10");

        rewritten.Should().Contain("    server: https://203.0.113.10:6443");
    }
}
