namespace EntKube.Web.Services;

/// <summary>
/// What goes into an EntKube node image, and what turns one into a running Kubernetes node.
///
/// <para>Pure string building, deliberately: this is the content that decides whether a cluster
/// comes up, it is impossible to test against a real cloud cheaply, and the failures it produces
/// (a kubelet that will not start, a CNI that never gets scheduled) are diagnosed hours later from
/// a console log. Keeping it as functions over strings means the decisions in it can be asserted.</para>
///
/// <para>The same image serves both the cluster's own nodes and the ephemeral bootstrap cluster.
/// That is not a convenience — with k3s ruled out, the bootstrap node needs kubeadm, kubelet and
/// containerd exactly as a real node does, and baking them once is what lets both boot without
/// reaching the internet.</para>
/// </summary>
public static class NodeImageRecipe
{
    /// <summary>Written by the bake script when everything succeeded. Polled for over SSH.</summary>
    public const string ReadyMarker = "/run/entkube-image-ready";

    /// <summary>Written when it did not, so a failure is distinguishable from still-running.</summary>
    public const string FailedMarker = "/run/entkube-image-failed";

    /// <summary>Written by the bootstrap node once kubeadm, the untaint and the CNI are all done.</summary>
    public const string BootstrapReadyMarker = "/run/entkube-bootstrap-ready";

    /// <summary>Where kubeadm leaves the admin kubeconfig. Root-only, unlike k3s's.</summary>
    public const string AdminConfPath = "/etc/kubernetes/admin.conf";

    /// <summary>
    /// cloud-init for the temporary VM an image is baked on: container runtime, pinned Kubernetes
    /// packages, and the control-plane images pulled ahead of time so that a node booting from the
    /// result needs no registry.
    /// </summary>
    public static string BuildCloudInit(string kubernetesVersion)
    {
        string script = BakeScript(kubernetesVersion);
        return "#cloud-config\n"
             + "write_files:\n"
             + "  - path: /opt/entkube-bake.sh\n"
             + "    permissions: '0755'\n"
             + "    content: |\n"
             + Indent(script, 6)
             + "runcmd:\n"
             + "  - ['/bin/bash', '/opt/entkube-bake.sh']\n";
    }

    /// <summary>
    /// The bake itself. Everything is pinned and held: an unpinned install produces an image whose
    /// version is whatever the repository served that morning, which is not something two clusters
    /// can be built from and expected to match.
    /// </summary>
    public static string BakeScript(string kubernetesVersion)
    {
        string version = MachineImageNaming.Normalize(kubernetesVersion);
        string stream = MachineImageNaming.PackageRepoStream(version);
        string pin = MachineImageNaming.AptVersionPin(version);

        return $"""
            #!/bin/bash
            # Bakes an EntKube Kubernetes node image. Any failure leaves {FailedMarker} behind so the
            # builder can stop waiting and report, rather than timing out with nothing to say.
            set -euo pipefail
            trap 'echo "bake failed at line $LINENO" >&2; touch {FailedMarker}' ERR
            exec > >(tee -a /var/log/entkube-bake.log) 2>&1
            set -x

            export DEBIAN_FRONTEND=noninteractive

            # ── Kubelet refuses to run with swap on, and a node that silently swaps is worse ──
            swapoff -a
            sed -i '/\\sswap\\s/ s/^/#/' /etc/fstab

            # ── Bridged traffic has to be visible to iptables or Services simply do not work ──
            cat >/etc/modules-load.d/k8s.conf <<'EOF'
            overlay
            br_netfilter
            EOF
            modprobe overlay
            modprobe br_netfilter
            cat >/etc/sysctl.d/99-kubernetes.conf <<'EOF'
            net.bridge.bridge-nf-call-iptables  = 1
            net.bridge.bridge-nf-call-ip6tables = 1
            net.ipv4.ip_forward                 = 1
            EOF
            sysctl --system

            apt-get update
            apt-get install -y apt-transport-https ca-certificates curl gpg conntrack socat ethtool

            # ── containerd from Docker's repository: the distribution's is habitually too old for a
            #    current kubelet, and the failure mode is a CRI version mismatch at first boot ──
            install -m 0755 -d /etc/apt/keyrings
            curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
            chmod a+r /etc/apt/keyrings/docker.gpg
            echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
              >/etc/apt/sources.list.d/docker.list
            apt-get update
            apt-get install -y containerd.io

            # SystemdCgroup must match what kubelet uses, or pods start and are killed on the
            # first memory accounting the two disagree about.
            containerd config default >/etc/containerd/config.toml
            sed -i 's/SystemdCgroup = false/SystemdCgroup = true/' /etc/containerd/config.toml
            systemctl restart containerd
            systemctl enable containerd

            # ── Kubernetes packages, pinned to {version} and held ──
            curl -fsSL https://pkgs.k8s.io/core:/stable:/{stream}/deb/Release.key \
              | gpg --dearmor -o /etc/apt/keyrings/kubernetes-apt-keyring.gpg
            echo "deb [signed-by=/etc/apt/keyrings/kubernetes-apt-keyring.gpg] https://pkgs.k8s.io/core:/stable:/{stream}/deb/ /" \
              >/etc/apt/sources.list.d/kubernetes.list
            apt-get update
            apt-get install -y kubelet={pin} kubeadm={pin} kubectl={pin}
            apt-mark hold kubelet kubeadm kubectl containerd.io

            # Enabled but not started: kubelet crash-loops until kubeadm writes its configuration,
            # which is expected and is what makes the node join on first boot.
            systemctl enable kubelet

            # ── Pre-pull, so a node coming up needs no registry ──
            kubeadm config images pull --kubernetes-version {version}

            touch {ReadyMarker}
            """;
    }

    /// <summary>
    /// Strips the identity a snapshot must not carry: machine-id (duplicates break DHCP leases and
    /// systemd journals), host keys (every node presenting the same key), cloud-init's record of
    /// having already run, and the build's own authorized key.
    /// </summary>
    public static string GeneralizeScript(string sshUser) =>
        $"""
        set -eux
        cloud-init clean --logs --seed || true
        truncate -s 0 /etc/machine-id
        rm -f /var/lib/dbus/machine-id
        rm -f /etc/ssh/ssh_host_*
        rm -rf /var/lib/cloud/instances/*
        rm -f /root/.ssh/authorized_keys /home/{sshUser}/.ssh/authorized_keys
        apt-get clean
        rm -rf /var/lib/apt/lists/*
        sync
        """;

    /// <summary>
    /// cloud-init for the ephemeral management cluster: a single-node kubeadm control plane that is
    /// willing to run workloads and has a working network. Both of those are things k3s did for us
    /// and kubeadm does not — leave the taint on and the CAPI controllers never schedule; skip the
    /// CNI and <c>clusterctl init</c>'s cert-manager webhooks never answer, which surfaces as a
    /// timeout with nothing obviously wrong.
    /// </summary>
    public static string BootstrapCloudInit(string kubernetesVersion, string floatingIp, string podCidr, string cniManifestUrl)
    {
        string version = MachineImageNaming.Normalize(kubernetesVersion);
        string kubeadmApi = KubeadmApiVersion(version);

        string config = $"""
            apiVersion: {kubeadmApi}
            kind: InitConfiguration
            ---
            apiVersion: {kubeadmApi}
            kind: ClusterConfiguration
            kubernetesVersion: {version}
            networking:
              podSubnet: {podCidr}
            apiServer:
              certSANs:
                - {floatingIp}
                - 127.0.0.1
                - localhost
            """;

        // The floating IP is a certSAN because the kubeconfig we fetch names the node's private
        // address, which is unreachable from here; rewriting it to the floating IP only works if
        // the certificate covers it.
        string script = $"""
            #!/bin/bash
            set -euo pipefail
            trap 'echo "bootstrap failed at line $LINENO" >&2' ERR
            exec > >(tee -a /var/log/entkube-bootstrap.log) 2>&1
            set -x

            kubeadm init --config /etc/kubernetes/entkube-init.yaml
            export KUBECONFIG={AdminConfPath}

            # A single-node management cluster has nowhere else to put the CAPI controllers.
            kubectl taint nodes --all node-role.kubernetes.io/control-plane- || true

            kubectl apply -f {cniManifestUrl}
            kubectl wait --for=condition=Ready node --all --timeout=300s

            touch {BootstrapReadyMarker}
            """;

        return "#cloud-config\n"
             + "write_files:\n"
             + "  - path: /etc/kubernetes/entkube-init.yaml\n"
             + "    permissions: '0600'\n"
             + "    content: |\n"
             + Indent(config, 6)
             + "  - path: /opt/entkube-bootstrap.sh\n"
             + "    permissions: '0755'\n"
             + "    content: |\n"
             + Indent(script, 6)
             + "runcmd:\n"
             + "  - ['/bin/bash', '/opt/entkube-bootstrap.sh']\n";
    }

    /// <summary>
    /// kubeadm's configuration API moves: v1beta4 arrived in 1.31 and v1beta3 is on its way out.
    /// Choosing by version is what lets one image recipe serve a range of clusters.
    /// </summary>
    public static string KubeadmApiVersion(string kubernetesVersion)
    {
        string[] parts = MachineImageNaming.Normalize(kubernetesVersion).TrimStart('v').Split('.');
        if (parts.Length >= 2
            && int.TryParse(parts[0], out int major)
            && int.TryParse(parts[1], out int minor)
            && (major > 1 || minor >= 31))
        {
            return "kubeadm.k8s.io/v1beta4";
        }
        return "kubeadm.k8s.io/v1beta3";
    }

    private static string Indent(string text, int spaces)
    {
        string pad = new(' ', spaces);
        return string.Concat(text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => pad + l + "\n"));
    }
}
