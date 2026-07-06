namespace EntKube.Web.Data;

/// <summary>Transport-layer (L4) protocol for a raw port route.</summary>
public enum L4Protocol
{
    Tcp,
    Udp
}

/// <summary>
/// Exposes a customer application's raw L4 port (TCP or UDP) through the cluster's Istio external
/// ingress.
///
/// Unlike HTTP routes (which mux many hostnames over a single 443 listener via SNI/Host), raw L4
/// traffic cannot be virtual-hosted — each route consumes a real listener port on a dedicated
/// per-cluster L4 Gateway. That Gateway is created WITHOUT an address binding so Istio's Gateway
/// API controller auto-provisions its own LoadBalancer Service (own external IP) and opens exactly
/// the listener ports. Traffic is passed through raw to the backend Service — the app terminates
/// any TLS/DTLS itself.
///
/// One row = one protocol + one external port → one backend Service:port, scoped to a single
/// deployment (environment). TCP and UDP can share a port number (e.g. DNS on 53) — they are
/// distinct listeners. A Kubernetes TCPRoute/UDPRoute (gateway.networking.k8s.io/v1alpha2) is
/// generated per row; a single shared Gateway per cluster carries one listener per row.
/// </summary>
public class AppL4Route
{
    public Guid Id { get; set; }

    /// <summary>Owning app. Scalar scoping column (no cascade relationship — the route
    /// cascades from AppDeployment instead, keeping a single cascade path from App).</summary>
    public Guid AppId { get; set; }

    /// <summary>The deployment/environment whose backing Service receives the traffic.</summary>
    public Guid AppDeploymentId { get; set; }

    /// <summary>Transport protocol (TCP or UDP). Combined with ExternalPort forms the listener identity.</summary>
    public L4Protocol Protocol { get; set; } = L4Protocol.Tcp;

    /// <summary>
    /// The external port opened on the dedicated ingress LoadBalancer (operator-chosen). Must be
    /// unique per cluster <em>within a protocol</em> — TCP 53 and UDP 53 may coexist.
    /// </summary>
    public int ExternalPort { get; set; }

    /// <summary>The Kubernetes Service name (in the deployment's namespace) to route to.</summary>
    public required string ServiceName { get; set; }

    /// <summary>The port on the backing Service to forward the stream/datagrams to.</summary>
    public int ServicePort { get; set; }

    /// <summary>Dedicated L4 Gateway resource name (resolved from the installed ingress controller).</summary>
    public string? GatewayName { get; set; }

    /// <summary>Namespace where the dedicated L4 Gateway lives (typically istio-system).</summary>
    public string? GatewayNamespace { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When false the route is observed only — EntKube tracks it but never applies or
    /// reconciles the route, leaving ownership to whatever created it. Mirrors AppRoute.
    /// </summary>
    public bool IsManaged { get; set; } = true;

    /// <summary>Set when the route manifest was last successfully applied. Null = not applied.</summary>
    public DateTime? ClusterAppliedAt { get; set; }

    // Health monitoring. TCP routes are dialed against the LoadBalancer address:port; UDP is
    // connectionless and cannot be reliably probed this way, so IsReachable stays null for UDP.
    public DateTime? LastHealthCheckAt { get; set; }
    public bool? IsReachable { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AppDeployment AppDeployment { get; set; } = null!;
}
