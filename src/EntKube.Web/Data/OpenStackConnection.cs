namespace EntKube.Web.Data;

/// <summary>
/// An OpenStack connection stores the authentication details needed to interact
/// with an OpenStack cloud (e.g. Cleura/City Cloud). This enables the platform
/// to manage S3 buckets, credentials, and other resources via the OpenStack API.
///
/// Credentials (password, application credential secret) are stored encrypted
/// in the vault — only metadata lives here.
/// </summary>
public class OpenStackConnection
{
    public Guid Id { get; set; }

    /// <summary>The tenant that owns this connection.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Human-friendly name (e.g. "Cleura Production", "City Cloud Dev").</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Keystone authentication URL.
    /// Example: "https://identity.c2.citycloud.com:5000/v3"
    /// </summary>
    public required string AuthUrl { get; set; }

    /// <summary>
    /// The OpenStack region (e.g. "Kna1", "Sto2", "Fra1").
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// The OpenStack project/tenant name.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// The OpenStack project/tenant ID.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// The user domain name (typically "Default" or the company domain).
    /// </summary>
    public string? UserDomainName { get; set; }

    /// <summary>
    /// The project domain name (typically "Default").
    /// </summary>
    public string? ProjectDomainName { get; set; }

    /// <summary>
    /// The OpenStack username for authentication.
    /// The password is stored in the vault under this connection's ID.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Explicit S3 endpoint for this cloud, e.g. "https://s3-sto2.citycloud.com".
    ///
    /// Normally left empty: the endpoint is read from the Keystone service
    /// catalog, which is authoritative. Set this only when the catalog does not
    /// advertise an object store, or advertises one that is not the S3 host.
    /// </summary>
    public string? S3Endpoint { get; set; }

    /// <summary>
    /// Optional outbound proxy for every call to this cloud's APIs, e.g.
    /// "socks5://10.0.0.5:1080" or "http://proxy.corp:3128".
    ///
    /// Set this when the cloud restricts its API to an IP allowlist that the
    /// EntKube server is not on: the proxy runs on a permitted network, so
    /// OpenStack sees the request arrive from an allowed address. Credentials
    /// must not be embedded here — use <see cref="ProxyUsername"/>.
    /// </summary>
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Username for an authenticating proxy. The password is stored in the vault
    /// under this connection's ID as OS_PROXY_PASSWORD.
    /// </summary>
    public string? ProxyUsername { get; set; }

    /// <summary>
    /// Route this cloud's API traffic through the egress relay running in this
    /// registered cluster, instead of straight out of the EntKube server.
    ///
    /// This is the option that fits a provider-side IP allowlist: the cluster
    /// already lives inside the provider's environment, so calls made from it
    /// arrive from an address the provider trusts — and EntKube reaches the relay
    /// over the cluster's API server, which it can already talk to, rather than
    /// needing anything published inbound.
    ///
    /// Mutually exclusive with <see cref="ProxyUrl"/>; the relay wins if both are
    /// somehow set.
    /// </summary>
    public Guid? RouteViaClusterId { get; set; }

    /// <summary>
    /// Route this cloud's API traffic through an <see cref="EgressAgent"/> running
    /// inside a network that is allowed to reach it.
    ///
    /// The last resort, and the only option when neither the EntKube server nor any
    /// managed cluster can reach the endpoint — the agent dials out from a network
    /// that can, so nothing has to be published inbound anywhere.
    ///
    /// Takes precedence over <see cref="RouteViaClusterId"/> and <see cref="ProxyUrl"/>.
    /// </summary>
    public Guid? RouteViaAgentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<StorageLink> StorageLinks { get; set; } = [];
}
