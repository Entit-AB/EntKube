using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class AppRouteRequest
{
    public required string Hostname { get; set; }
    public TlsMode TlsMode { get; set; } = TlsMode.ClusterIssuer;
    public string? ClusterIssuerName { get; set; }
    public string? TlsCertificate { get; set; }
    public string? TlsPrivateKey { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When false the route is observed only and never applied/reconciled by EntKube
    /// (ownership stays with ArgoCD/Flux). Imported routes pass false; routes created
    /// in EntKube leave this true.
    /// </summary>
    public bool IsManaged { get; set; } = true;

    /// <summary>Require a client certificate (inbound mTLS). Needs <see cref="ClientCaBundleId"/>.</summary>
    public bool RequireClientCertificate { get; set; }

    /// <summary>The trust anchor client certificates are validated against.</summary>
    public Guid? ClientCaBundleId { get; set; }

    /// <summary>Drop the plain 443 listener, leaving the hostname reachable over mTLS only.</summary>
    public bool ClientCertificateOnly { get; set; }
}

public class AppDeploymentRouteRequest
{
    public required string ServiceName { get; set; }
    public int ServicePort { get; set; } = 80;
    public string PathPrefix { get; set; } = "/";
    public string? RewritePath { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Request timeout for this rule in the generated HTTPRoute. Null takes the platform
    /// default (<see cref="ExternalRouteService.DefaultRequestTimeoutSeconds"/>); 0 disables
    /// the timeout for long-lived streams.
    /// </summary>
    public int? RequestTimeoutSeconds { get; set; }

    // Session affinity is deliberately absent, exactly like the canary fields: this request is
    // rebuilt from scratch by every edit form, so carrying affinity here would let a form that
    // never showed it reset it to None on save. It is set through
    // <see cref="AppRouteService.UpdateDeploymentRouteSessionAffinityAsync"/> instead.
}

/// <summary>
/// Manages app-level external routes — exposes customer applications via Gateway API HTTPRoutes.
/// AppRoute owns the hostname + TLS config; AppDeploymentRoute links a deployment to that
/// hostname with a path prefix and target service, generating a Kubernetes HTTPRoute per deployment.
/// </summary>
public class AppRouteService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AppRouteService> logger)
{
    public async Task<List<AppRoute>> GetRoutesForAppAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.AppRoutes
            .Include(r => r.ClientCaBundle)
            .Include(r => r.DeploymentRoutes)
                .ThenInclude(dr => dr.AppDeployment)
                    .ThenInclude(d => d.Environment)
            .Where(r => r.AppId == appId)
            .OrderBy(r => r.Hostname)
            .ToListAsync(ct);
    }

    public async Task<List<AppRoute>> GetRoutesForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.AppRoutes
            .Include(r => r.App)
            .Include(r => r.ClientCaBundle)
            .Include(r => r.DeploymentRoutes)
                .ThenInclude(dr => dr.AppDeployment)
                    .ThenInclude(d => d.Environment)
            .Where(r => r.App.CustomerId == customerId && r.IsEnabled)
            .OrderBy(r => r.App.Name)
            .ThenBy(r => r.Hostname)
            .ToListAsync(ct);
    }

    public async Task<AppRoute> AddRouteAsync(Guid appId, AppRouteRequest request, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (string.IsNullOrWhiteSpace(request.Hostname))
            throw new InvalidOperationException("Hostname is required.");

        if (request.TlsMode == TlsMode.ClusterIssuer && string.IsNullOrWhiteSpace(request.ClusterIssuerName))
            throw new InvalidOperationException("ClusterIssuer name is required when using automatic TLS.");

        if (request.TlsMode == TlsMode.Manual && string.IsNullOrWhiteSpace(request.TlsCertificate))
            throw new InvalidOperationException("TLS certificate is required when using manual TLS.");

        string hostname = request.Hostname.Trim().ToLowerInvariant();

        bool duplicate = await db.AppRoutes
            .AnyAsync(r => r.AppId == appId && r.Hostname == hostname, ct);
        if (duplicate)
            throw new InvalidOperationException($"Hostname '{hostname}' is already configured for this app.");

        await ValidateClientCertificateAsync(db, appId, request, ct);

        AppRoute route = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Hostname = hostname,
            TlsMode = request.TlsMode,
            ClusterIssuerName = request.ClusterIssuerName?.Trim(),
            TlsCertificate = request.TlsCertificate,
            TlsPrivateKey = request.TlsPrivateKey,
            IsEnabled = request.IsEnabled,
            IsManaged = request.IsManaged,
            RequireClientCertificate = request.RequireClientCertificate,
            ClientCaBundleId = request.RequireClientCertificate ? request.ClientCaBundleId : null,
            ClientCertificateOnly = request.RequireClientCertificate && request.ClientCertificateOnly
        };

        db.AppRoutes.Add(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("App route {Hostname} added to app {AppId}", hostname, appId);

        return route;
    }

    public async Task UpdateRouteAsync(Guid routeId, AppRouteRequest request, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppRoute route = await db.AppRoutes.FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        if (string.IsNullOrWhiteSpace(request.Hostname))
            throw new InvalidOperationException("Hostname is required.");

        string hostname = request.Hostname.Trim().ToLowerInvariant();

        bool duplicate = await db.AppRoutes
            .AnyAsync(r => r.AppId == route.AppId && r.Hostname == hostname && r.Id != routeId, ct);
        if (duplicate)
            throw new InvalidOperationException($"Hostname '{hostname}' is already configured for this app.");

        await ValidateClientCertificateAsync(db, route.AppId, request, ct);

        route.Hostname = hostname;
        route.TlsMode = request.TlsMode;
        route.ClusterIssuerName = request.ClusterIssuerName?.Trim();
        route.TlsCertificate = request.TlsCertificate;
        route.TlsPrivateKey = request.TlsPrivateKey;
        route.IsEnabled = request.IsEnabled;
        route.RequireClientCertificate = request.RequireClientCertificate;
        route.ClientCaBundleId = request.RequireClientCertificate ? request.ClientCaBundleId : null;
        route.ClientCertificateOnly = request.RequireClientCertificate && request.ClientCertificateOnly;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks that a route asking for client certificates has a usable trust anchor, and that the
    /// anchor belongs to the app's own tenant — pointing a route at another tenant's anchor would
    /// publish it on that anchor's listener port and accept that tenant's client certificates.
    /// </summary>
    private static async Task ValidateClientCertificateAsync(
        ApplicationDbContext db, Guid appId, AppRouteRequest request, CancellationToken ct)
    {
        if (!request.RequireClientCertificate) return;

        if (request.ClientCaBundleId is not { } bundleId)
            throw new InvalidOperationException(
                "Pick the CA that signs your clients' certificates — mTLS can't be enabled without a trust anchor.");

        Guid tenantId = await db.Apps
            .Where(a => a.Id == appId)
            .Select(a => a.Customer.TenantId)
            .FirstOrDefaultAsync(ct);

        ClientCaBundle? bundle = await db.ClientCaBundles
            .Include(b => b.Certificates)
            .FirstOrDefaultAsync(b => b.Id == bundleId, ct);

        if (bundle is null || bundle.TenantId != tenantId)
            throw new InvalidOperationException("Trust anchor not found for this tenant.");

        // An anchor with no CA yields an empty trust store; Istio rejects the Gateway outright,
        // which would take down every listener on it, not just this route's.
        if (bundle.Certificates.Count == 0)
            throw new InvalidOperationException(
                $"Trust anchor '{bundle.Name}' has no CA certificate yet. Upload the customer's CA before " +
                "requiring client certificates.");
    }

    public async Task DeleteRouteAsync(Guid routeId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppRoute route = await db.AppRoutes.FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        db.AppRoutes.Remove(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("App route {RouteId} deleted", routeId);
    }

    public async Task<AppDeploymentRoute> AddDeploymentRouteAsync(
        Guid appRouteId, Guid deploymentId, AppDeploymentRouteRequest request, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppRoute appRoute = await db.AppRoutes.FirstOrDefaultAsync(r => r.Id == appRouteId, ct)
            ?? throw new InvalidOperationException("App route not found.");

        AppDeployment deployment = await db.AppDeployments
            .Include(d => d.Cluster)
                .ThenInclude(c => c.Components)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            ?? throw new InvalidOperationException("Deployment not found.");

        // A deployment may be linked to the same hostname multiple times — one Helm release can
        // expose several services that each need their own path. What can't collide is the path
        // prefix: two rules sharing a hostname + path prefix would produce an ambiguous HTTPRoute.
        string pathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? "/" : request.PathPrefix.Trim();

        bool pathTaken = await db.AppDeploymentRoutes
            .AnyAsync(r => r.AppRouteId == appRouteId && r.PathPrefix == pathPrefix, ct);
        if (pathTaken)
            throw new InvalidOperationException(
                $"Path prefix '{pathPrefix}' is already in use on this hostname. Choose a different path.");

        (string gatewayName, string gatewayNamespace) =
            ExternalRouteService.ResolveGateway(deployment.Cluster.Components);

        AppDeploymentRoute dr = new()
        {
            Id = Guid.NewGuid(),
            AppRouteId = appRouteId,
            AppDeploymentId = deploymentId,
            PathPrefix = pathPrefix,
            RewritePath = string.IsNullOrWhiteSpace(request.RewritePath) ? null : request.RewritePath.Trim(),
            ServiceName = request.ServiceName.Trim(),
            ServicePort = request.ServicePort,
            GatewayName = gatewayName,
            GatewayNamespace = gatewayNamespace,
            IsEnabled = request.IsEnabled,
            RequestTimeoutSeconds = ValidateTimeout(request.RequestTimeoutSeconds)
        };

        db.AppDeploymentRoutes.Add(dr);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deployment route {DeploymentId} linked to app route {AppRouteId}", deploymentId, appRouteId);

        return dr;
    }

    public async Task UpdateDeploymentRouteAsync(
        Guid deploymentRouteId, AppDeploymentRouteRequest request, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute dr = await db.AppDeploymentRoutes
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct)
            ?? throw new InvalidOperationException("Deployment route not found.");

        string pathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? "/" : request.PathPrefix.Trim();

        bool pathTaken = await db.AppDeploymentRoutes
            .AnyAsync(r => r.AppRouteId == dr.AppRouteId && r.PathPrefix == pathPrefix && r.Id != deploymentRouteId, ct);
        if (pathTaken)
            throw new InvalidOperationException(
                $"Path prefix '{pathPrefix}' is already in use on this hostname. Choose a different path.");

        dr.PathPrefix = pathPrefix;
        dr.RewritePath = string.IsNullOrWhiteSpace(request.RewritePath) ? null : request.RewritePath.Trim();
        dr.ServiceName = request.ServiceName.Trim();
        dr.ServicePort = request.ServicePort;
        dr.IsEnabled = request.IsEnabled;
        dr.RequestTimeoutSeconds = ValidateTimeout(request.RequestTimeoutSeconds);
        dr.ClusterAppliedAt = null; // route changed — must be re-applied

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Changes only the request timeout on a deployment route. Clears ClusterAppliedAt so the
    /// row shows as needing a re-apply — the new value reaches the cluster with the regenerated
    /// HTTPRoute, not before. Null restores the platform default; 0 disables the timeout.
    /// </summary>
    public async Task UpdateDeploymentRouteTimeoutAsync(
        Guid deploymentRouteId, int? requestTimeoutSeconds, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute dr = await db.AppDeploymentRoutes
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct)
            ?? throw new InvalidOperationException("Deployment route not found.");

        dr.RequestTimeoutSeconds = ValidateTimeout(requestTimeoutSeconds);
        dr.ClusterAppliedAt = null;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sets session affinity on a deployment route — the sticky-session flag the generated
    /// DestinationRule turns into <c>loadBalancer.consistentHash</c>.
    ///
    /// Clears ClusterAppliedAt like every other traffic change here: the affinity only exists
    /// once the DestinationRule reaches the cluster, and showing it as live before then would
    /// claim sessions were pinned when they were still being spread across pods.
    /// </summary>
    public async Task UpdateDeploymentRouteSessionAffinityAsync(
        Guid deploymentRouteId, SessionAffinityMode mode, string? key, int? ttlSeconds,
        CancellationToken ct = default)
    {
        if (ExternalRouteService.ValidateSessionAffinity(mode, key) is string error)
        {
            throw new InvalidOperationException(error);
        }

        if (ttlSeconds is < 0)
        {
            throw new InvalidOperationException(
                "Affinity cookie lifetime cannot be negative. Leave it empty for a session cookie.");
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute dr = await db.AppDeploymentRoutes
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct)
            ?? throw new InvalidOperationException("Deployment route not found.");

        dr.SessionAffinity = mode;
        dr.SessionAffinityKey = ExternalRouteService.NormalizeAffinityKey(mode, key);
        dr.SessionAffinityTtlSeconds = mode == SessionAffinityMode.Cookie ? ttlSeconds : null;
        dr.ClusterAppliedAt = null;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sets the canary service and traffic share for a route.
    ///
    /// Clears ClusterAppliedAt so the route shows as needing re-apply, exactly like a
    /// timeout change: the weight only takes effect once the HTTPRoute reaches the
    /// cluster, and a UI that showed the new number as live before then would be
    /// claiming traffic had moved when it had not.
    /// </summary>
    public async Task UpdateDeploymentRouteCanaryAsync(
        Guid deploymentRouteId, string? canaryServiceName, int canaryWeight,
        int? canaryServicePort = null, CancellationToken ct = default)
    {
        if (canaryWeight is < 0 or > 100)
        {
            throw new InvalidOperationException("Canary weight must be between 0 and 100.");
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute dr = await db.AppDeploymentRoutes
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct)
            ?? throw new InvalidOperationException("Deployment route not found.");

        string? service = string.IsNullOrWhiteSpace(canaryServiceName) ? null : canaryServiceName.Trim();

        // Naming the stable service as the canary would emit two identical backends and
        // split traffic between a workload and itself — valid YAML that quietly means
        // nothing, which is worse than an error.
        if (service is not null && string.Equals(service, dr.ServiceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The canary service must differ from the stable service.");
        }

        // Dropping the service also drops the weight: leaving a weight behind with no
        // destination is a half-configured state that reads as "10% is going somewhere".
        dr.CanaryServiceName = service;
        dr.CanaryWeight = service is null ? 0 : canaryWeight;
        dr.CanaryServicePort = service is null ? null : canaryServicePort;
        dr.ClusterAppliedAt = null;

        await db.SaveChangesAsync(ct);
    }

    private static int? ValidateTimeout(int? requestTimeoutSeconds)
        => requestTimeoutSeconds is < 0
            ? throw new InvalidOperationException("Request timeout cannot be negative. Use 0 for no timeout.")
            : requestTimeoutSeconds;

    public async Task DeleteDeploymentRouteAsync(Guid deploymentRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute dr = await db.AppDeploymentRoutes
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct)
            ?? throw new InvalidOperationException("Deployment route not found.");

        db.AppDeploymentRoutes.Remove(dr);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Generates the full Kubernetes manifest (HTTPRoute + Certificate) for a deployment route.
    /// Apply this YAML to the target cluster to expose the app.
    /// </summary>
    public async Task<string> GenerateManifestYamlAsync(Guid deploymentRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute dr = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
                .ThenInclude(r => r.ClientCaBundle)
            .Include(r => r.AppDeployment)
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct)
            ?? throw new InvalidOperationException("Deployment route not found.");

        return GenerateManifestYaml(dr);
    }

    /// <summary>
    /// Generates the full manifest (HTTPRoute + optional Certificate) for an AppRoute, combining
    /// ALL enabled deployment routes as rules in a single HTTPRoute resource. This is the correct
    /// form to apply to the cluster — a per-deployment-route manifest would overwrite other rules.
    /// Requires AppRoute.DeploymentRoutes.AppDeployment to be loaded.
    /// </summary>
    public static string GenerateManifestYaml(AppRoute route)
    {
        List<AppDeploymentRoute> enabled = route.DeploymentRoutes
            .Where(dr => dr.IsEnabled)
            .OrderByDescending(dr => dr.PathPrefix.Length)
            .ThenBy(dr => dr.PathPrefix)
            .ToList();
        return GenerateManifestYaml(route, enabled);
    }

    public static string GenerateManifestYaml(AppRoute route, IReadOnlyList<AppDeploymentRoute> deploymentRoutes)
    {
        string httpRouteNs = deploymentRoutes.Count > 0
            ? deploymentRoutes[0].AppDeployment?.Namespace ?? "default"
            : "default";

        List<string> parts = [
            GenerateHttpRouteYaml(route, deploymentRoutes),
            GenerateCertificateYaml(route, deploymentRoutes),
            GenerateReferenceGrantsYaml(httpRouteNs, deploymentRoutes),
        ];

        return string.Join("\n---\n", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>
    /// Generates ReferenceGrant resources for each namespace that differs from the HTTPRoute's
    /// namespace, allowing the HTTPRoute to cross-reference Services in those namespaces.
    /// Required by Gateway API when backendRefs span multiple namespaces.
    /// </summary>
    public static string GenerateReferenceGrantsYaml(string httpRouteNamespace, IReadOnlyList<AppDeploymentRoute> deploymentRoutes)
    {
        List<string> otherNamespaces = deploymentRoutes
            .Select(dr => dr.AppDeployment?.Namespace ?? "default")
            .Where(n => n != httpRouteNamespace)
            .Distinct()
            .ToList();

        if (otherNamespaces.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        foreach (string targetNs in otherNamespaces)
        {
            if (sb.Length > 0) sb.AppendLine("---");
            sb.AppendLine("apiVersion: gateway.networking.k8s.io/v1beta1");
            sb.AppendLine("kind: ReferenceGrant");
            sb.AppendLine("metadata:");
            sb.AppendLine($"  name: entkube-httproute-ref");
            sb.AppendLine($"  namespace: {targetNs}");
            sb.AppendLine("spec:");
            sb.AppendLine("  from:");
            sb.AppendLine("    - group: gateway.networking.k8s.io");
            sb.AppendLine("      kind: HTTPRoute");
            sb.AppendLine($"      namespace: {httpRouteNamespace}");
            sb.AppendLine("  to:");
            sb.AppendLine("    - group: \"\"");
            sb.Append("      kind: Service");
        }
        return sb.ToString();
    }

    public static string GenerateHttpRouteYaml(AppRoute route, IReadOnlyList<AppDeploymentRoute> deploymentRoutes)
    {
        if (deploymentRoutes.Count == 0) return "";

        AppDeploymentRoute primary = deploymentRoutes[0];
        string ns = primary.AppDeployment?.Namespace ?? "default";
        string routeName = ExternalRouteService.ToListenerName(route.Hostname) + "-route";

        var rules = new System.Text.StringBuilder();
        foreach (AppDeploymentRoute dr in deploymentRoutes)
        {
            string drNs = dr.AppDeployment?.Namespace ?? ns;
            // One filters: block per rule, holding every filter that rule needs — a rewrite and
            // HSTS both land here, and a second filters: key would be invalid YAML.
            string filters = "";
            if (dr.RewritePath is not null && dr.PathPrefix != "/")
            {
                filters +=
                    $"        - type: URLRewrite\n" +
                    $"          urlRewrite:\n" +
                    $"            path:\n" +
                    $"              type: ReplacePrefixMatch\n" +
                    $"              replacePrefixMatch: {dr.RewritePath}\n";
            }
            filters += ExternalRouteService.HstsFilterEntry("        ");

            if (dr.PathPrefix != "/")
            {
                rules.AppendLine($"    - matches:");
                rules.AppendLine($"        - path:");
                rules.AppendLine($"            type: PathPrefix");
                rules.AppendLine($"            value: {dr.PathPrefix}");
                rules.AppendLine($"      filters:");
                rules.Append(filters);
                rules.AppendLine($"      backendRefs:");
                rules.Append(RenderBackendRefs(dr, drNs, "        "));
            }
            else
            {
                rules.AppendLine($"    - filters:");
                rules.Append(filters);
                rules.AppendLine($"      backendRefs:");
                rules.Append(RenderBackendRefs(dr, drNs, "        "));
            }

            // Each deployment route carries its own timeout, so a streaming path can opt out
            // (0) while the rest of the hostname still fails fast.
            rules.Append(ExternalRouteService.RenderTimeouts(dr.RequestTimeoutSeconds, "      "));
            rules.Append(ExternalRouteService.RenderRetry("      "));
        }

        return
            $"apiVersion: gateway.networking.k8s.io/v1\n" +
            $"kind: HTTPRoute\n" +
            $"metadata:\n" +
            $"  name: {routeName}\n" +
            $"  namespace: {ns}\n" +
            $"spec:\n" +
            ExternalRouteService.RenderParentRefs(
                primary.GatewayName, primary.GatewayNamespace, ListenerSectionsFor(route)) +
            $"  hostnames:\n" +
            $"    - {route.Hostname}\n" +
            $"  rules:\n" +
            rules.ToString().TrimEnd();
    }

    /// <summary>
    /// The Gateway listeners this route's HTTPRoute must attach to — the plain 443 listener, the
    /// hostname's mTLS listener, or both, matching exactly what
    /// <see cref="ExternalRouteService.GenerateGatewayYaml"/> puts on the Gateway for it.
    ///
    /// Attaching by name rather than letting the route match every listener is what keeps the
    /// route off port 80 (see <see cref="ExternalRouteService.RenderParentRefs"/>), so this has to
    /// enumerate the listeners rather than leave them implicit.
    /// </summary>
    public static IEnumerable<string> ListenerSectionsFor(AppRoute route)
    {
        // ClientCertificateOnly drops the hostname from 443 entirely; without it, 443 stays.
        if (!(route.RequireClientCertificate && route.ClientCertificateOnly))
        {
            yield return ExternalRouteService.ToListenerName(route.Hostname);
        }

        if (!route.RequireClientCertificate)
        {
            yield break;
        }

        // Same contract as MtlsService.BuildPlan: an unloaded trust anchor cannot be told apart
        // from "no mTLS configured", and guessing the port would pin the route to a listener the
        // Gateway does not have — the route would attach nowhere and the hostname would go dark.
        ClientCaBundle bundle = route.ClientCaBundle
            ?? throw new InvalidOperationException(
                $"Route '{route.Hostname}' requires a client certificate but its trust anchor was not " +
                "loaded. Include AppRoute.ClientCaBundle before generating the HTTPRoute.");

        yield return MtlsService.MtlsListenerName(route.Hostname, bundle.ListenerPort);
    }

    public static string GenerateCertificateYaml(AppRoute route, IReadOnlyList<AppDeploymentRoute> deploymentRoutes)
    {
        if (route.TlsMode != TlsMode.ClusterIssuer || string.IsNullOrWhiteSpace(route.ClusterIssuerName))
            return "";

        // Certificate must live in cert-manager namespace so the Gateway's certificateRefs
        // can resolve the resulting TLS Secret (cross-namespace via ReferenceGrant).
        const string ns = "cert-manager";
        string secretName = ExternalRouteService.ToCertSecretName(route.Hostname);

        return $"""
            apiVersion: cert-manager.io/v1
            kind: Certificate
            metadata:
              name: {secretName}
              namespace: {ns}
            spec:
              secretName: {secretName}
              issuerRef:
                name: {route.ClusterIssuerName}
                kind: ClusterIssuer
              dnsNames:
                - {route.Hostname}
            """;
    }

    // Kept for backward compatibility (e.g. manifest preview for a single route).
    public static string GenerateManifestYaml(AppDeploymentRoute dr)
        => GenerateManifestYaml(dr.AppRoute, [dr]);

    /// <summary>
    /// Renders the backendRefs for one rule, splitting traffic when a canary is configured.
    ///
    /// A route without a canary emits exactly one backend and no weight field, so it is
    /// byte-identical to what was generated before weighted routing existed — an unrelated
    /// route must not start producing a different manifest, or every deployment would show
    /// as drifted the first time this shipped.
    ///
    /// Gateway API treats weights as relative shares, not percentages, so "10 and 90" and
    /// "1 and 9" behave identically. Emitting them as percentages that sum to 100 is purely
    /// so an operator reading the manifest sees the number they typed.
    /// </summary>
    public static string RenderBackendRefs(AppDeploymentRoute dr, string ns, string indent)
    {
        var backends = new System.Text.StringBuilder();

        int weight = Math.Clamp(dr.CanaryWeight, 0, 100);
        bool hasCanary = !string.IsNullOrWhiteSpace(dr.CanaryServiceName) && weight > 0;

        if (!hasCanary)
        {
            backends.AppendLine($"{indent}- name: {dr.ServiceName}");
            backends.AppendLine($"{indent}  namespace: {ns}");
            backends.AppendLine($"{indent}  port: {dr.ServicePort}");
            return backends.ToString();
        }

        backends.AppendLine($"{indent}- name: {dr.ServiceName}");
        backends.AppendLine($"{indent}  namespace: {ns}");
        backends.AppendLine($"{indent}  port: {dr.ServicePort}");
        backends.AppendLine($"{indent}  weight: {100 - weight}");
        backends.AppendLine($"{indent}- name: {dr.CanaryServiceName}");
        backends.AppendLine($"{indent}  namespace: {ns}");
        backends.AppendLine($"{indent}  port: {dr.CanaryServicePort ?? dr.ServicePort}");
        backends.AppendLine($"{indent}  weight: {weight}");

        return backends.ToString();
    }

    public static string GenerateHttpRouteYaml(AppDeploymentRoute dr)
        => GenerateHttpRouteYaml(dr.AppRoute, [dr]);

    public static string GenerateCertificateYaml(AppDeploymentRoute dr)
        => GenerateCertificateYaml(dr.AppRoute, [dr]);
}
