namespace EntKube.Web.Data;

/// <summary>
/// A cluster on the platform's side that participates in a VPN tunnel.
/// For SiteToSite tunnels this is the cluster running the StrongSwan gateway pod.
/// For ClusterMesh tunnels each participating cluster has one of these entries;
/// the cluster with Role=Gateway also hosts the Submariner broker.
/// </summary>
public class VpnLocalEndpoint
{
    public Guid Id { get; set; }

    public Guid VpnTunnelId { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>
    /// The ClusterComponent that represents the gateway pod (StrongSwan or Submariner) on
    /// this cluster. Null until the component has been registered.
    /// </summary>
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// JSON array of CIDRs that are reachable through this endpoint,
    /// e.g. ["10.244.0.0/16","10.96.0.0/12"]. Entered manually by the operator.
    /// </summary>
    public required string Subnets { get; set; }

    /// <summary>
    /// External IP of the gateway. Auto-populated from the LoadBalancer Service
    /// status after install, or set manually.
    /// </summary>
    public string? PublicIp { get; set; }

    /// <summary>
    /// Gateway = primary cluster (hosts StrongSwan for SiteToSite, or Submariner broker for ClusterMesh).
    /// Participant = additional cluster in a ClusterMesh that gets a Submariner gateway only.
    /// </summary>
    public VpnEndpointRole Role { get; set; } = VpnEndpointRole.Gateway;

    public VpnEndpointStatus Status { get; set; } = VpnEndpointStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public VpnTunnel Tunnel { get; set; } = null!;
    public KubernetesCluster Cluster { get; set; } = null!;
    public ClusterComponent? Component { get; set; }
}
