namespace EntKube.Web.Data;

public enum VpnTunnelType
{
    SiteToSite,
    ClusterMesh
}

public enum VpnTunnelStatus
{
    Draft,
    Deploying,
    Active,
    Degraded,
    Failed,
    Disabled
}

public enum VpnEndpointRole
{
    /// <summary>Primary gateway — hosts StrongSwan (SiteToSite) or Submariner broker (ClusterMesh).</summary>
    Gateway,
    /// <summary>Participant cluster in a ClusterMesh that gets a Submariner gateway (not the broker).</summary>
    Participant
}

public enum VpnEndpointStatus
{
    Pending,
    Ready,
    Error
}

public enum VpnAuthMode
{
    Psk,
    Certificate
}

public enum VpnConnectionStatus
{
    Unknown,
    Connected,
    Disconnected
}

/// <summary>
/// A managed VPN tunnel scoped to a tenant. Two types are supported:
/// SiteToSite (StrongSwan IPsec to an external network) and ClusterMesh
/// (Submariner IPsec between K8s clusters within the tenant).
/// </summary>
public class VpnTunnel
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Human-friendly name, e.g. "Customer HQ Link". Unique within a tenant.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    public VpnTunnelType TunnelType { get; set; }

    public VpnTunnelStatus Status { get; set; } = VpnTunnelStatus.Draft;

    // ── IKEv2 crypto settings ──

    public int IkeVersion { get; set; } = 2;

    /// <summary>IKE phase-1 proposal, e.g. "aes256-sha256-modp2048".</summary>
    public string IkeProposal { get; set; } = "aes256-sha256-modp2048";

    /// <summary>ESP phase-2 proposal, e.g. "aes256gcm16-sha256".</summary>
    public string EspProposal { get; set; } = "aes256gcm16-sha256";

    /// <summary>Seconds between DPD liveness checks.</summary>
    public int DpdDelay { get; set; } = 30;

    /// <summary>Seconds before a peer is declared dead after no DPD response.</summary>
    public int DpdTimeout { get; set; } = 150;

    /// <summary>Seconds before the IKE SA (phase 1) is renegotiated. Default 24h.</summary>
    public int IkeLifetime { get; set; } = 86400;

    /// <summary>Seconds before the Child SA (phase 2 / ESP) is renegotiated. Default 1h.</summary>
    public int ChildLifetime { get; set; } = 3600;

    public DateTime? LastStatusCheckAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<VpnLocalEndpoint> LocalEndpoints { get; set; } = [];
    public ICollection<VpnRemoteEndpoint> RemoteEndpoints { get; set; } = [];
}
