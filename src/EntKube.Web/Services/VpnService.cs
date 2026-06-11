using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services;

/// <summary>
/// Manages VPN tunnels: data lifecycle, swanctl config generation, K8s config sync,
/// and connection status polling. Two tunnel types are supported:
///
///   SiteToSite  — StrongSwan gateway pod on one or more platform clusters,
///                 connecting to external (customer) sites via IKEv2/IPsec.
///
///   ClusterMesh — Submariner broker + gateways across multiple platform clusters,
///                 enabling encrypted pod-to-pod and service cross-cluster connectivity.
/// </summary>
public class VpnService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    ComponentLifecycleService lifecycleService)
{
    // ── Tunnel CRUD ─────────────────────────────────────────────────────────────

    public async Task<VpnTunnel> CreateTunnelAsync(
        Guid tenantId, string name, VpnTunnelType type, string? description = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VpnTunnel tunnel = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            TunnelType = type,
            Status = VpnTunnelStatus.Draft
        };

        db.VpnTunnels.Add(tunnel);
        await db.SaveChangesAsync(ct);
        return tunnel;
    }

    public async Task<VpnTunnel?> GetTunnelWithDetailsAsync(Guid tunnelId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.VpnTunnels
            .Include(t => t.LocalEndpoints).ThenInclude(e => e.Cluster)
            .Include(t => t.LocalEndpoints).ThenInclude(e => e.Component)
            .Include(t => t.RemoteEndpoints)
            .FirstOrDefaultAsync(t => t.Id == tunnelId, ct);
    }

    public async Task<List<VpnTunnel>> GetTunnelsForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.VpnTunnels
            .Where(t => t.TenantId == tenantId)
            .Include(t => t.LocalEndpoints).ThenInclude(e => e.Cluster)
            .Include(t => t.RemoteEndpoints)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task UpdateCryptoSettingsAsync(
        Guid tunnelId, int ikeVersion, string ikeProposal, string espProposal,
        int dpdDelay, int dpdTimeout, int ikeLifetime = 86400, int childLifetime = 3600,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnTunnel tunnel = await db.VpnTunnels.FindAsync([tunnelId], ct)
            ?? throw new InvalidOperationException($"Tunnel {tunnelId} not found.");

        tunnel.IkeVersion = ikeVersion;
        tunnel.IkeProposal = ikeProposal;
        tunnel.EspProposal = espProposal;
        tunnel.DpdDelay = dpdDelay;
        tunnel.DpdTimeout = dpdTimeout;
        tunnel.IkeLifetime = ikeLifetime;
        tunnel.ChildLifetime = childLifetime;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateTunnelStatusAsync(Guid tunnelId, VpnTunnelStatus status, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnTunnel tunnel = await db.VpnTunnels.FindAsync([tunnelId], ct)
            ?? throw new InvalidOperationException($"Tunnel {tunnelId} not found.");
        tunnel.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteTunnelAsync(Guid tunnelId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnTunnel tunnel = await db.VpnTunnels.FindAsync([tunnelId], ct)
            ?? throw new InvalidOperationException($"Tunnel {tunnelId} not found.");
        db.VpnTunnels.Remove(tunnel);
        await db.SaveChangesAsync(ct);
    }

    // ── Local endpoints ──────────────────────────────────────────────────────────

    public async Task<VpnLocalEndpoint> AddLocalEndpointAsync(
        Guid tunnelId, Guid clusterId, string[] subnets, VpnEndpointRole role,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnLocalEndpoint ep = new()
        {
            Id = Guid.NewGuid(),
            VpnTunnelId = tunnelId,
            ClusterId = clusterId,
            Subnets = JsonSerializer.Serialize(subnets),
            Role = role,
            Status = VpnEndpointStatus.Pending
        };
        db.VpnLocalEndpoints.Add(ep);
        await db.SaveChangesAsync(ct);
        return ep;
    }

    public async Task UpdateLocalEndpointAsync(
        Guid endpointId, string[] subnets, string? publicIp, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnLocalEndpoint ep = await db.VpnLocalEndpoints.FindAsync([endpointId], ct)
            ?? throw new InvalidOperationException($"Local endpoint {endpointId} not found.");
        ep.Subnets = JsonSerializer.Serialize(subnets);
        if (publicIp is not null) ep.PublicIp = publicIp;
        await db.SaveChangesAsync(ct);
    }

    public async Task LinkComponentAsync(Guid endpointId, Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnLocalEndpoint ep = await db.VpnLocalEndpoints.FindAsync([endpointId], ct)
            ?? throw new InvalidOperationException($"Local endpoint {endpointId} not found.");
        ep.ComponentId = componentId;
        ep.Status = VpnEndpointStatus.Ready;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveLocalEndpointAsync(Guid endpointId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnLocalEndpoint ep = await db.VpnLocalEndpoints.FindAsync([endpointId], ct)
            ?? throw new InvalidOperationException($"Local endpoint {endpointId} not found.");
        db.VpnLocalEndpoints.Remove(ep);
        await db.SaveChangesAsync(ct);
    }

    // ── Remote endpoints (SiteToSite only) ──────────────────────────────────────

    public async Task<VpnRemoteEndpoint> AddRemoteEndpointAsync(
        Guid tunnelId, string name, string publicIp, string[] subnets,
        VpnAuthMode authMode, string psk, Guid tenantId,
        string? localId = null, string? remoteId = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VpnRemoteEndpoint ep = new()
        {
            Id = Guid.NewGuid(),
            VpnTunnelId = tunnelId,
            Name = name,
            PublicIp = publicIp,
            Subnets = JsonSerializer.Serialize(subnets),
            AuthMode = authMode,
            LocalId = localId,
            RemoteId = remoteId,
            Status = VpnConnectionStatus.Unknown
        };

        db.VpnRemoteEndpoints.Add(ep);
        await db.SaveChangesAsync(ct);

        await vaultService.SetVpnRemoteEndpointSecretAsync(tenantId, ep.Id, "PSK", psk, ct);

        return ep;
    }

    public async Task UpdateRemoteEndpointAsync(
        Guid endpointId, string publicIp, string[] subnets, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnRemoteEndpoint ep = await db.VpnRemoteEndpoints.FindAsync([endpointId], ct)
            ?? throw new InvalidOperationException($"Remote endpoint {endpointId} not found.");
        ep.PublicIp = publicIp;
        ep.Subnets = JsonSerializer.Serialize(subnets);
        await db.SaveChangesAsync(ct);
    }

    public async Task RotatePskAsync(Guid endpointId, string newPsk, Guid tenantId, CancellationToken ct = default)
    {
        await vaultService.SetVpnRemoteEndpointSecretAsync(tenantId, endpointId, "PSK", newPsk, ct);
    }

    public async Task RemoveRemoteEndpointAsync(Guid endpointId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        VpnRemoteEndpoint ep = await db.VpnRemoteEndpoints.FindAsync([endpointId], ct)
            ?? throw new InvalidOperationException($"Remote endpoint {endpointId} not found.");
        db.VpnRemoteEndpoints.Remove(ep);
        await db.SaveChangesAsync(ct);
    }

    // ── Config generation ────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a swanctl.conf for the given tunnel, containing one connection
    /// block per remote endpoint.
    /// </summary>
    public async Task<string> GenerateSwanctlConfigAsync(Guid tunnelId, CancellationToken ct = default)
    {
        VpnTunnel tunnel = await GetTunnelWithDetailsAsync(tunnelId, ct)
            ?? throw new InvalidOperationException($"Tunnel {tunnelId} not found.");

        StringBuilder sb = new();
        sb.AppendLine("connections {");

        VpnLocalEndpoint? gateway = tunnel.LocalEndpoints.FirstOrDefault(e => e.Role == VpnEndpointRole.Gateway);
        string localIp = gateway?.PublicIp ?? "%any";
        string[] localSubnets = gateway is not null
            ? (JsonSerializer.Deserialize<string[]>(gateway.Subnets) ?? [])
            : [];

        foreach (VpnRemoteEndpoint remote in tunnel.RemoteEndpoints)
        {
            string connName = SanitizeConnectionName(remote.Name);
            string[] remoteSubnets = JsonSerializer.Deserialize<string[]>(remote.Subnets) ?? [];

            string localId = !string.IsNullOrWhiteSpace(remote.LocalId) ? remote.LocalId : localIp;
            string remoteId = !string.IsNullOrWhiteSpace(remote.RemoteId) ? remote.RemoteId : remote.PublicIp;

            sb.AppendLine($"    {connName} {{");
            sb.AppendLine($"        version = {tunnel.IkeVersion}");
            sb.AppendLine($"        proposals = {tunnel.IkeProposal}");
            sb.AppendLine($"        rekey_time = {tunnel.IkeLifetime}s");
            sb.AppendLine($"        dpd_delay = {tunnel.DpdDelay}s");
            sb.AppendLine($"        dpd_timeout = {tunnel.DpdTimeout}s");
            sb.AppendLine($"        local_addrs = {localIp}");
            sb.AppendLine($"        remote_addrs = {remote.PublicIp}");
            sb.AppendLine($"        local {{");
            sb.AppendLine($"            auth = psk");
            sb.AppendLine($"            id = {localId}");
            sb.AppendLine($"            subnets = {string.Join(",", localSubnets)}");
            sb.AppendLine($"        }}");
            sb.AppendLine($"        remote {{");
            sb.AppendLine($"            auth = psk");
            sb.AppendLine($"            id = {remoteId}");
            sb.AppendLine($"            subnets = {string.Join(",", remoteSubnets)}");
            sb.AppendLine($"        }}");
            sb.AppendLine($"        children {{");
            sb.AppendLine($"            {connName} {{");
            sb.AppendLine($"                esp_proposals = {tunnel.EspProposal}");
            sb.AppendLine($"                rekey_time = {tunnel.ChildLifetime}s");
            sb.AppendLine($"                start_action = trap");
            sb.AppendLine($"                dpd_action = restart");
            sb.AppendLine($"            }}");
            sb.AppendLine($"        }}");
            sb.AppendLine($"    }}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Generates the swanctl secrets section (PSKs) for the tunnel.
    /// PSK values are decrypted from the vault at call time.
    /// </summary>
    public async Task<string> GenerateSwanctlSecretsAsync(Guid tunnelId, Guid tenantId, CancellationToken ct = default)
    {
        VpnTunnel tunnel = await GetTunnelWithDetailsAsync(tunnelId, ct)
            ?? throw new InvalidOperationException($"Tunnel {tunnelId} not found.");

        StringBuilder sb = new();
        sb.AppendLine("secrets {");

        foreach (VpnRemoteEndpoint remote in tunnel.RemoteEndpoints)
        {
            string? psk = await vaultService.GetVpnRemoteEndpointSecretValueAsync(tenantId, remote.Id, "PSK", ct);
            if (psk is null) continue;

            sb.AppendLine($"    ike-{SanitizeConnectionName(remote.Name)} {{");
            sb.AppendLine($"        id = {remote.PublicIp}");
            sb.AppendLine($"        secret = \"{psk}\"");
            sb.AppendLine($"    }}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Applies the swanctl config and secrets as a ConfigMap and opaque Secret on the
    /// cluster associated with the given local endpoint, using kubectl apply.
    /// </summary>
    public async Task<HelmExecutionResult> SyncConfigToClusterAsync(
        Guid localEndpointId, Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VpnLocalEndpoint ep = await db.VpnLocalEndpoints
            .Include(e => e.Cluster)
            .FirstOrDefaultAsync(e => e.Id == localEndpointId, ct)
            ?? throw new InvalidOperationException($"Local endpoint {localEndpointId} not found.");

        string config = await GenerateSwanctlConfigAsync(ep.VpnTunnelId, ct);
        string secrets = await GenerateSwanctlSecretsAsync(ep.VpnTunnelId, tenantId, ct);

        string secretsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secrets));

        string manifest = BuildSyncManifest(config, secretsB64);

        return await lifecycleService.ApplyRawManifestAsync(ep.Cluster, manifest, delete: false, ct);
    }

    // ── CIDR auto-detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Tries to detect the pod and service CIDRs for a cluster by querying its
    /// Kubernetes API. Uses kubeadm-config and node specs as sources.
    /// Returns null values for either CIDR if detection fails — the operator
    /// can always fill them in manually.
    /// </summary>
    public async Task<(string? PodCidr, string? ServiceCidr)> DetectClusterCidrsAsync(
        KubernetesCluster cluster, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (null, null);

        string tempKubeconfig = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, cluster.Kubeconfig, ct);

            string? podCidr = null;
            string? serviceCidr = null;

            // ── Source 1: kube-controller-manager static pod command (most authoritative) ──
            // The --cluster-cidr flag is the actual configured pod CIDR, not a per-node value.
            try
            {
                string args = await RunKubectlOutputAsync(
                    $"get pod -n kube-system -l component=kube-controller-manager -o jsonpath={{.items[0].spec.containers[0].command}} --kubeconfig={tempKubeconfig}", ct);
                if (!string.IsNullOrWhiteSpace(args))
                {
                    Match m = Regex.Match(args, @"--cluster-cidr[=""'\s]+([0-9a-f.:/ ,]+?)(?:[""'\s]|$)");
                    if (m.Success) podCidr = m.Groups[1].Value.Trim('"', '\'', ' ', ',');
                }
            }
            catch { }

            // ── Source 2: kube-apiserver static pod command for service CIDR ──
            try
            {
                string args = await RunKubectlOutputAsync(
                    $"get pod -n kube-system -l component=kube-apiserver -o jsonpath={{.items[0].spec.containers[0].command}} --kubeconfig={tempKubeconfig}", ct);
                if (!string.IsNullOrWhiteSpace(args))
                {
                    Match m = Regex.Match(args, @"--service-cluster-ip-range[=""'\s]+([0-9a-f.:/ ,]+?)(?:[""'\s]|$)");
                    if (m.Success) serviceCidr = m.Groups[1].Value.Trim('"', '\'', ' ', ',');
                }
            }
            catch { }

            // ── Source 3: kubeadm-config ConfigMap (kubeadm clusters) ──
            // Contains explicit networking.podSubnet and networking.serviceSubnet.
            if (podCidr is null || serviceCidr is null)
            {
                try
                {
                    string yaml = await RunKubectlOutputAsync(
                        $"get cm kubeadm-config -n kube-system -o jsonpath={{.data.ClusterConfiguration}} --kubeconfig={tempKubeconfig}", ct);
                    if (!string.IsNullOrWhiteSpace(yaml))
                    {
                        if (podCidr is null)
                        {
                            Match m = Regex.Match(yaml, @"podSubnet:\s*([0-9a-f.:/ ]+)");
                            if (m.Success) podCidr = m.Groups[1].Value.Trim();
                        }
                        if (serviceCidr is null)
                        {
                            Match m = Regex.Match(yaml, @"serviceSubnet:\s*([0-9a-f.:/ ]+)");
                            if (m.Success) serviceCidr = m.Groups[1].Value.Trim();
                        }
                    }
                }
                catch { }
            }

            // ── Source 4: kube-proxy ConfigMap clusterCIDR (fallback for pod CIDR) ──
            if (podCidr is null)
            {
                try
                {
                    string conf = await RunKubectlOutputAsync(
                        $"get cm kube-proxy -n kube-system -o jsonpath={{.data.config\\.conf}} --kubeconfig={tempKubeconfig}", ct);
                    Match m = Regex.Match(conf, @"clusterCIDR:\s*([0-9a-f.:/ ,]+)");
                    if (m.Success) podCidr = m.Groups[1].Value.Trim();
                }
                catch { }
            }

            // ── Source 5: compute supernet from all node spec.podCIDR values ──
            // This gives the minimum covering CIDR across all current nodes.
            // Note: the computed supernet may be narrower than the configured cluster CIDR
            // if not all nodes have been assigned subnets yet, but it's better than
            // returning a single /24 that only covers one node.
            if (podCidr is null)
            {
                try
                {
                    string raw = await RunKubectlOutputAsync(
                        $"get nodes -o jsonpath={{.items[*].spec.podCIDR}} --kubeconfig={tempKubeconfig}", ct);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        string[] nodeCidrs = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        podCidr = ComputeIpv4Supernet(nodeCidrs);
                    }
                }
                catch { }
            }

            // ── Source 6: k3s node annotation (k3s encodes its args on each node) ──
            if (serviceCidr is null)
            {
                try
                {
                    string raw = await RunKubectlOutputAsync(
                        $"get nodes -o jsonpath={{.items[0].metadata.annotations}} --kubeconfig={tempKubeconfig}", ct);
                    // k3s stores server args as a JSON-encoded string in an annotation
                    Match m = Regex.Match(raw, @"--service-cidr[=\s""\\]+([0-9./]+)");
                    if (m.Success) serviceCidr = m.Groups[1].Value;
                }
                catch { }
            }

            return (podCidr, serviceCidr);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    /// <summary>
    /// Computes the smallest IPv4 supernet that covers all given CIDRs.
    /// e.g. ["10.244.0.0/24","10.244.1.0/24","10.244.2.0/24"] → "10.244.0.0/22"
    /// </summary>
    private static string? ComputeIpv4Supernet(string[] cidrs)
    {
        var networks = new List<(uint network, int prefix)>();
        foreach (string cidr in cidrs)
        {
            int slash = cidr.IndexOf('/');
            if (slash < 0) continue;
            if (!System.Net.IPAddress.TryParse(cidr[..slash], out var addr)) continue;
            if (!int.TryParse(cidr[(slash + 1)..], out int prefix)) continue;
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) continue;
            uint ip = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
            uint mask = prefix > 0 ? ~((1u << (32 - prefix)) - 1) : 0u;
            networks.Add((ip & mask, prefix));
        }

        if (networks.Count == 0) return null;
        if (networks.Count == 1)
        {
            // Single node — widen the prefix by 8 bits as a heuristic
            // (e.g. /24 per node → likely /16 cluster CIDR).
            // The user can always adjust the value manually.
            (uint net, int pfx) = networks[0];
            int widenedPfx = Math.Max(0, pfx - 8);
            uint widenedMask = widenedPfx > 0 ? ~((1u << (32 - widenedPfx)) - 1) : 0u;
            uint widenedNet = net & widenedMask;
            return FormatCidr(widenedNet, widenedPfx);
        }

        // Find the common bit prefix of all network addresses.
        uint first = networks[0].network;
        int commonBits = 32;
        foreach ((uint net, _) in networks.Skip(1))
        {
            uint diff = first ^ net;
            if (diff == 0) continue;
            commonBits = Math.Min(commonBits, 31 - (int)Math.Log2(diff));
        }

        uint superMask = commonBits > 0 ? ~((1u << (32 - commonBits)) - 1) : 0u;
        return FormatCidr(first & superMask, commonBits);
    }

    private static string FormatCidr(uint ip, int prefix)
        => $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}/{prefix}";

    // ── Gateway component auto-link ───────────────────────────────────────────────

    /// <summary>
    /// For any local endpoint whose ComponentId is null, searches the cluster's
    /// installed components for a matching gateway (strongswan or submariner) and
    /// links it. Saves changes if any links are resolved.
    /// Called lazily on each tunnel load so the UI reflects reality without
    /// requiring manual wiring.
    /// </summary>
    public async Task ReconcileGatewayLinksAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<VpnLocalEndpoint> unlinked = await db.VpnLocalEndpoints
            .Where(e => e.ComponentId == null && e.Tunnel.TenantId == tenantId)
            .Include(e => e.Tunnel)
            .ToListAsync(ct);

        if (unlinked.Count == 0) return;

        bool changed = false;
        foreach (VpnLocalEndpoint ep in unlinked)
        {
            // Names to search for, depending on tunnel type and endpoint role.
            string[] candidateNames = ep.Tunnel.TunnelType == VpnTunnelType.SiteToSite
                ? ["strongswan"]
                : ep.Role == VpnEndpointRole.Gateway
                    ? ["submariner-broker", "submariner"]
                    : ["submariner", "submariner-gateway"];

            ClusterComponent? match = await db.ClusterComponents
                .Where(c => c.ClusterId == ep.ClusterId
                         && (candidateNames.Contains(c.Name)
                             || (c.ReleaseName != null && candidateNames.Contains(c.ReleaseName))))
                .OrderByDescending(c => c.InstalledAt)
                .FirstOrDefaultAsync(ct);

            if (match is not null)
            {
                ep.ComponentId = match.Id;
                ep.Status = match.Status == ComponentStatus.Installed
                    ? VpnEndpointStatus.Ready
                    : VpnEndpointStatus.Pending;
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    public static string[] ParseSubnets(string subnetsJson)
        => JsonSerializer.Deserialize<string[]>(subnetsJson) ?? [];

    public static string SerializeSubnets(IEnumerable<string> subnets)
        => JsonSerializer.Serialize(subnets.ToArray());

    private static string SanitizeConnectionName(string name)
        => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "-");

    private static async Task<string> RunKubectlOutputAsync(string arguments, CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        Task<string> outTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return await outTask;
    }

    private static string BuildSyncManifest(string swanctlConf, string secretsB64)
    {
        // Indent each line by 4 spaces so it embeds cleanly as a YAML literal block scalar.
        string escapedConf = string.Join("\n", swanctlConf.Split('\n').Select(l => "    " + l));
        return $"""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: vpn-system
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: strongswan-config
              namespace: vpn-system
            data:
              # Mounted at /etc/swanctl/conf.d/connections.conf via subPath
              connections.conf: |
            {escapedConf}
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: strongswan-secrets
              namespace: vpn-system
            type: Opaque
            data:
              # Mounted at /etc/swanctl/conf.d/secrets.conf via subPath (PSK encrypted in vault).
              secrets.conf: {secretsB64}
            """;
    }
}
