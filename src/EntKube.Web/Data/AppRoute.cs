namespace EntKube.Web.Data;

/// <summary>
/// App-level hostname configuration for exposing a customer application externally.
/// Holds the hostname and TLS strategy shared across all deployment environments.
/// Each deployment environment can attach with its own path prefix via AppDeploymentRoute.
/// </summary>
public class AppRoute
{
    public Guid Id { get; set; }

    public Guid AppId { get; set; }

    /// <summary>
    /// The public hostname (e.g. "myapp.example.com"). Must be unique within a cluster.
    /// </summary>
    public required string Hostname { get; set; }

    /// <summary>How TLS is handled — automatic via ClusterIssuer or manual cert upload.</summary>
    public TlsMode TlsMode { get; set; } = TlsMode.ClusterIssuer;

    /// <summary>Name of the ClusterIssuer when TlsMode is ClusterIssuer (e.g. "letsencrypt-prod").</summary>
    public string? ClusterIssuerName { get; set; }

    /// <summary>PEM-encoded certificate for manual TLS mode.</summary>
    public string? TlsCertificate { get; set; }

    /// <summary>PEM-encoded private key for manual TLS mode.</summary>
    public string? TlsPrivateKey { get; set; }

    /// <summary>When false the route is kept in the database but no Kubernetes resources are applied.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When false the route is observed only: EntKube tracks the external access and
    /// shows it, but never applies or reconciles the HTTPRoute — leaving ownership to
    /// whatever created it (commonly ArgoCD or Flux). Imported routes start unmanaged;
    /// turning management on makes EntKube reconcile the HTTPRoute automatically.
    /// Defaults to true so routes created inside EntKube manage themselves as before.
    /// </summary>
    public bool IsManaged { get; set; } = true;

    /// <summary>
    /// Require a client certificate to reach this hostname (inbound mTLS). The route is then served
    /// on the trust anchor's <see cref="ClientCaBundle.ListenerPort"/> <em>in addition to</em> 443:
    /// client-certificate validation is a per-port property of the Gateway, so demanding a cert on
    /// 443 would demand one from every other customer sharing that gateway.
    ///
    /// Plain 443 keeps working for this hostname unless <see cref="ClientCertificateOnly"/> is set.
    /// </summary>
    public bool RequireClientCertificate { get; set; }

    /// <summary>
    /// The CA that signs the client certificates accepted for this hostname. Required when
    /// <see cref="RequireClientCertificate"/> is true.
    /// </summary>
    public Guid? ClientCaBundleId { get; set; }

    /// <summary>
    /// When true the hostname's plain 443 listener is dropped, so the app is reachable
    /// <em>only</em> over mTLS. Off by default: turning it on breaks any client that has not
    /// migrated to the mTLS port, so it is a deliberate second step.
    /// </summary>
    public bool ClientCertificateOnly { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public App App { get; set; } = null!;

    /// <summary>The trust anchor for inbound mTLS, when <see cref="RequireClientCertificate"/> is set.</summary>
    public ClientCaBundle? ClientCaBundle { get; set; }
    public ICollection<AppDeploymentRoute> DeploymentRoutes { get; set; } = [];
}

/// <summary>
/// Links one AppRoute to one AppDeployment, specifying the path prefix and target service
/// for that environment. A Kubernetes HTTPRoute is generated per AppDeploymentRoute.
/// </summary>
public class AppDeploymentRoute
{
    public Guid Id { get; set; }

    public Guid AppRouteId { get; set; }

    public Guid AppDeploymentId { get; set; }

    /// <summary>
    /// Path prefix for this deployment (e.g. "/" for prod, "/staging" for staging).
    /// Multiple deployments share the same hostname via different path prefixes.
    /// </summary>
    public string PathPrefix { get; set; } = "/";

    /// <summary>The Kubernetes service name to route traffic to.</summary>
    public required string ServiceName { get; set; }

    /// <summary>The port on the service to route traffic to.</summary>
    public int ServicePort { get; set; } = 80;

    /// <summary>
    /// When set, rewrites the matched path prefix to this value before forwarding to the backend.
    /// Use "/" to strip the prefix entirely (e.g. /int/company-data/foo → /foo).
    /// Null means the full path is forwarded as-is.
    /// </summary>
    public string? RewritePath { get; set; }

    /// <summary>
    /// Request timeout for this rule in the generated HTTPRoute (spec.rules[].timeouts). Null uses
    /// the platform default (<see cref="Services.ExternalRouteService.DefaultRequestTimeoutSeconds"/>);
    /// 0 emits no timeouts block, leaving the gateway to wait indefinitely.
    ///
    /// Only set 0 for paths carrying long-lived streams (websockets, SSE, chunked downloads):
    /// Gateway API's request timeout bounds the whole exchange, so a finite value cuts a live
    /// stream off mid-flight. Everything else wants a finite value so a wedged backend fails
    /// fast instead of hanging the browser.
    /// </summary>
    public int? RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// A second Kubernetes Service to send a share of traffic to — the canary side of a
    /// weighted release. Null means all traffic goes to <see cref="ServiceName"/>.
    ///
    /// EntKube does not create this Service or the workload behind it. Synthesising a
    /// canary workload means rewriting someone's manifests under a new name, and getting
    /// that subtly wrong produces a canary that is not actually the thing being tested.
    /// The operator declares what the canary is; EntKube owns the traffic split, which is
    /// the part that needs a control plane.
    /// </summary>
    public string? CanaryServiceName { get; set; }

    /// <summary>
    /// Percentage of traffic sent to <see cref="CanaryServiceName"/>, 0–100. Zero (the
    /// default) sends everything to the stable service and emits a single backend, so a
    /// route with no canary is byte-identical to what it was before this field existed.
    /// </summary>
    public int CanaryWeight { get; set; }

    /// <summary>Port on the canary service. Defaults to the stable service's port when unset.</summary>
    public int? CanaryServicePort { get; set; }

    /// <summary>Gateway resource name resolved from the cluster's installed ingress controller.</summary>
    public string? GatewayName { get; set; }

    /// <summary>Namespace where the Gateway resource lives.</summary>
    public string? GatewayNamespace { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Set when the HTTPRoute manifest was last successfully applied to the cluster. Null means not yet applied.</summary>
    public DateTime? ClusterAppliedAt { get; set; }

    // Health monitoring (updated by background health checks)
    public DateTime? LastHealthCheckAt { get; set; }
    public int? LastStatusCode { get; set; }
    public bool? IsReachable { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AppRoute AppRoute { get; set; } = null!;
    public AppDeployment AppDeployment { get; set; } = null!;
}
