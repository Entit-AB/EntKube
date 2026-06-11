namespace EntKube.Web.Data;

/// <summary>
/// An external (non-platform) site that a SiteToSite VPN tunnel connects to,
/// e.g. a customer data centre or a cloud VGW.
/// The PSK (or certificate) is stored as a VaultSecret scoped to this endpoint.
/// </summary>
public class VpnRemoteEndpoint
{
    public Guid Id { get; set; }

    public Guid VpnTunnelId { get; set; }

    /// <summary>Human-friendly label, e.g. "Customer HQ Frankfurt".</summary>
    public required string Name { get; set; }

    /// <summary>Public IP of the remote gateway.</summary>
    public required string PublicIp { get; set; }

    /// <summary>
    /// JSON array of CIDRs behind the remote gateway,
    /// e.g. ["192.168.10.0/24","172.16.0.0/16"].
    /// </summary>
    public required string Subnets { get; set; }

    public VpnAuthMode AuthMode { get; set; } = VpnAuthMode.Psk;

    /// <summary>
    /// Our IKE identity sent to this peer. Null means use the gateway's public IP.
    /// Examples: @vpn.example.com (FQDN), 203.0.113.1 (IP), C=SE,O=Org,CN=vpn (DN).
    /// </summary>
    public string? LocalId { get; set; }

    /// <summary>
    /// Expected IKE identity from the remote peer. Null means accept any.
    /// Should be set to prevent impersonation.
    /// </summary>
    public string? RemoteId { get; set; }

    public VpnConnectionStatus Status { get; set; } = VpnConnectionStatus.Unknown;

    public DateTime? LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public VpnTunnel Tunnel { get; set; } = null!;

    /// <summary>
    /// Vault secrets scoped to this endpoint. For PSK auth this contains one
    /// secret named "PSK". For certificate auth: "CERT" and optionally "KEY".
    /// </summary>
    public ICollection<VaultSecret> Secrets { get; set; } = [];
}
