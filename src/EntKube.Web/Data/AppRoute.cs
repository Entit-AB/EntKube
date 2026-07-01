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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public App App { get; set; } = null!;
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
