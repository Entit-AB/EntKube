namespace EntKube.Web.Data;

/// <summary>
/// How TLS is handled for an external route.
/// </summary>
public enum TlsMode
{
    /// <summary>Automatic TLS via a cert-manager ClusterIssuer (e.g. Let's Encrypt).</summary>
    ClusterIssuer,

    /// <summary>Manual TLS — operator uploads a certificate (and optionally a private key).</summary>
    Manual,

    /// <summary>
    /// TLS passthrough — the gateway routes TCP by SNI without terminating TLS.
    /// The backend pod handles TLS itself (e.g. via an nginx sidecar).
    /// No certificate is managed at the gateway level.
    /// </summary>
    Passthrough
}

/// <summary>
/// An external route exposes a cluster component to the outside world via
/// a Gateway API HTTPRoute. Each route maps a hostname to a backend service
/// with TLS termination handled either automatically (ClusterIssuer) or
/// manually (uploaded certificate).
///
/// This is the simple abstraction over Gateway API — operators just specify
/// the hostname and TLS strategy, the platform generates the resources.
/// </summary>
public class ExternalRoute
{
    public Guid Id { get; set; }

    /// <summary>The component this route exposes.</summary>
    public Guid ComponentId { get; set; }

    /// <summary>
    /// The hostname for external access (e.g. "grafana.example.com").
    /// Must be unique within a cluster — two routes can't serve the same hostname.
    /// </summary>
    public required string Hostname { get; set; }

    /// <summary>
    /// The Kubernetes service name to route traffic to.
    /// Defaults to the component's release name if not specified.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// The port on the service to route traffic to (e.g. 80, 3000, 9090).
    /// </summary>
    public int ServicePort { get; set; } = 80;

    /// <summary>
    /// Optional path prefix for the route (e.g. "/grafana"). Defaults to "/".
    /// </summary>
    public string PathPrefix { get; set; } = "/";

    /// <summary>
    /// Request timeout for the generated HTTPRoute rule (spec.rules[].timeouts). Null uses
    /// the platform default (<see cref="Services.ExternalRouteService.DefaultRequestTimeoutSeconds"/>);
    /// 0 emits no timeouts block at all, leaving the gateway to wait indefinitely.
    ///
    /// Only set 0 for routes carrying long-lived streams (websockets, SSE, HTTP upgrade such as
    /// Tailscale's ts2021): Gateway API's request timeout bounds the entire exchange, not the time
    /// to first byte, so a finite value cuts a live stream off mid-flight. Everything else wants a
    /// finite value — without one a wedged upstream hangs the browser instead of failing fast.
    /// </summary>
    public int? RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// Pins a client to one backend pod for the life of its session. Rendered as
    /// <c>loadBalancer.consistentHash</c> on the backend Service's DestinationRule, so it
    /// applies to every route pointing at <see cref="ServiceName"/> — a Service, not a hostname, is what Istio
    /// load balances.
    ///
    /// Istio-only: a Traefik gateway ignores DestinationRules entirely, and nothing is emitted
    /// for it. Defaults to <see cref="SessionAffinityMode.None"/>, which generates exactly the
    /// DestinationRule this field did not exist to change.
    /// </summary>
    public SessionAffinityMode SessionAffinity { get; set; } = SessionAffinityMode.None;

    /// <summary>
    /// The cookie name, header name, or query parameter the hash is taken from, depending on
    /// <see cref="SessionAffinity"/>. Required for
    /// <see cref="SessionAffinityMode.Header"/> and <see cref="SessionAffinityMode.QueryParameter"/>;
    /// optional for <see cref="SessionAffinityMode.Cookie"/> (blank uses
    /// <see cref="Services.ExternalRouteService.DefaultAffinityCookieName"/>); unused for
    /// <see cref="SessionAffinityMode.SourceIp"/>.
    /// </summary>
    public string? SessionAffinityKey { get; set; }

    /// <summary>
    /// Lifetime of the affinity cookie in seconds. Null issues a session cookie, which expires
    /// when the browser closes. Only meaningful for <see cref="SessionAffinityMode.Cookie"/>.
    /// </summary>
    public int? SessionAffinityTtlSeconds { get; set; }

    /// <summary>How TLS is handled — automatic via ClusterIssuer or manual cert upload.</summary>
    public TlsMode TlsMode { get; set; } = TlsMode.ClusterIssuer;

    /// <summary>
    /// Name of the ClusterIssuer to use when TlsMode is ClusterIssuer.
    /// Typically "letsencrypt-prod" or "letsencrypt-staging".
    /// </summary>
    public string? ClusterIssuerName { get; set; }

    /// <summary>
    /// PEM-encoded TLS certificate for manual TLS mode.
    /// Stored encrypted at rest via the vault.
    /// </summary>
    public string? TlsCertificate { get; set; }

    /// <summary>
    /// PEM-encoded private key for manual TLS mode.
    /// Stored encrypted at rest via the vault. Optional — some scenarios use
    /// certificates without keys (e.g. intermediates managed elsewhere).
    /// </summary>
    public string? TlsPrivateKey { get; set; }

    /// <summary>
    /// The name of the Kubernetes Gateway resource to attach this route to.
    /// Determined by the installed ingress controller (e.g. "traefik-gateway").
    /// </summary>
    public string? GatewayName { get; set; }

    /// <summary>The namespace where the Gateway resource lives.</summary>
    public string? GatewayNamespace { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Health monitoring ──

    /// <summary>When the last automated health check ran against this hostname.</summary>
    public DateTime? LastHealthCheckAt { get; set; }

    /// <summary>HTTP status code from the last health check. Null if the request failed entirely.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>True when the last health check returned a 2xx or redirect response.</summary>
    public bool? IsReachable { get; set; }

    // Navigation
    public ClusterComponent Component { get; set; } = null!;
}
