using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Input model for creating or updating an external route.
/// Captures everything needed to expose a component externally.
/// </summary>
public class ExternalRouteRequest
{
    public required string Hostname { get; set; }
    public string? ServiceName { get; set; }
    public int ServicePort { get; set; } = 80;
    public string PathPrefix { get; set; } = "/";
    public TlsMode TlsMode { get; set; } = TlsMode.ClusterIssuer;
    public string? ClusterIssuerName { get; set; }
    public string? TlsCertificate { get; set; }
    public string? TlsPrivateKey { get; set; }
    public string? GatewayName { get; set; }
    public string? GatewayNamespace { get; set; }

    /// <summary>
    /// Request timeout for the generated HTTPRoute rule. Null takes the platform default
    /// (<see cref="ExternalRouteService.DefaultRequestTimeoutSeconds"/>); 0 disables the
    /// timeout for long-lived streams.
    /// </summary>
    public int? RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// Pin clients to one backend pod (Istio consistent hashing). Defaults to no affinity.
    /// </summary>
    public SessionAffinityMode SessionAffinity { get; set; } = SessionAffinityMode.None;

    /// <summary>Cookie / header / query-parameter name the affinity hash is taken from.</summary>
    public string? SessionAffinityKey { get; set; }

    /// <summary>Affinity cookie lifetime in seconds. Null issues a session cookie.</summary>
    public int? SessionAffinityTtlSeconds { get; set; }
}

/// <summary>
/// Manages external routes — the simple abstraction over Gateway API HTTPRoutes.
/// Operators specify a hostname and TLS strategy; this service handles the rest.
///
/// The flow is straightforward:
/// 1. Add a route to a component (hostname + TLS config)
/// 2. Generate the Kubernetes HTTPRoute YAML
/// 3. Apply it to the cluster (via kubectl or the K8s API)
///
/// Routes are stored in the database so we can track what's exposed,
/// regenerate manifests, and tear down routes when components are uninstalled.
/// </summary>
public class ExternalRouteService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ExternalRouteService> logger)
{
    /// <summary>
    /// Adds an external route to a component. The operator specifies the hostname
    /// and TLS strategy — the service fills in defaults for anything not provided
    /// (gateway name from the cluster's ingress controller, service name from the
    /// component's release name, etc.).
    /// </summary>
    public async Task<ExternalRoute> AddRouteAsync(
        Guid componentId, ExternalRouteRequest request, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load the component to fill in defaults.

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
                .ThenInclude(cl => cl.Components)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Validate the hostname is not empty.

        if (string.IsNullOrWhiteSpace(request.Hostname))
        {
            throw new InvalidOperationException("Hostname is required.");
        }

        // Validate TLS configuration.

        if (request.TlsMode == TlsMode.ClusterIssuer && string.IsNullOrWhiteSpace(request.ClusterIssuerName))
        {
            throw new InvalidOperationException(
                "ClusterIssuer name is required when using automatic TLS.");
        }

        if (request.TlsMode == TlsMode.Manual && string.IsNullOrWhiteSpace(request.TlsCertificate))
        {
            throw new InvalidOperationException(
                "TLS certificate is required when using manual TLS.");
        }

        if (ValidateSessionAffinity(request.SessionAffinity, request.SessionAffinityKey) is string affinityError)
        {
            throw new InvalidOperationException(affinityError);
        }

        // Check for duplicate hostname on this cluster.

        string normalizedHostname = request.Hostname.Trim().ToLowerInvariant();

        List<Guid> clusterComponentIds = component.Cluster.Components.Select(c => c.Id).ToList();
        bool duplicateHostname = await db.ExternalRoutes
            .AnyAsync(r => clusterComponentIds.Contains(r.ComponentId)
                && r.Hostname == normalizedHostname, ct);

        if (duplicateHostname)
        {
            throw new InvalidOperationException(
                $"Hostname '{request.Hostname}' is already in use on this cluster.");
        }

        // An AppRoute for the same hostname is not a second route — it is the same route. Both
        // sides render an object named ToListenerName(hostname) + "-route", and an AppRoute
        // renders one rule per path while this can only say "the whole host goes to one service".
        // Saving it would mean the next apply of any component on the cluster replaces the app's
        // per-path rules with a single backend, and every path that backend does not serve starts
        // answering 404. Refusing here is the last point where that is still cheap to prevent.

        bool servedByAppRoute = await db.AppRoutes
            .AnyAsync(r => r.IsEnabled
                && r.Hostname.ToLower() == normalizedHostname
                && r.DeploymentRoutes.Any(dr =>
                    dr.IsEnabled && dr.AppDeployment.ClusterId == component.Cluster.Id), ct);

        if (servedByAppRoute)
        {
            throw new InvalidOperationException(
                $"Hostname '{request.Hostname}' is already served by an application route on this "
                + "cluster. Add the path to that application route instead — an external route "
                + "here would replace its per-path rules with a single backend.");
        }

        // Resolve gateway details from the cluster's ingress controller if not provided.

        string gatewayName = request.GatewayName
            ?? ResolveGatewayName(component.Cluster.Components);
        string gatewayNamespace = request.GatewayNamespace
            ?? ResolveGatewayNamespace(component.Cluster.Components);

        ExternalRoute route = new()
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Hostname = normalizedHostname,
            ServiceName = request.ServiceName ?? component.ReleaseName ?? component.Name,
            ServicePort = request.ServicePort,
            PathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? "/" : request.PathPrefix.Trim(),
            TlsMode = request.TlsMode,
            ClusterIssuerName = request.ClusterIssuerName?.Trim(),
            TlsCertificate = request.TlsCertificate,
            TlsPrivateKey = request.TlsPrivateKey,
            GatewayName = gatewayName,
            GatewayNamespace = gatewayNamespace,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            SessionAffinity = request.SessionAffinity,
            SessionAffinityKey = NormalizeAffinityKey(request.SessionAffinity, request.SessionAffinityKey),
            SessionAffinityTtlSeconds = request.SessionAffinityTtlSeconds
        };

        db.ExternalRoutes.Add(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("External route {Hostname} added to component {ComponentId}", route.Hostname, componentId);

        return route;
    }

    /// <summary>
    /// Gets all external routes for a component.
    /// </summary>
    public async Task<List<ExternalRoute>> GetRoutesAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ExternalRoutes
            .Where(r => r.ComponentId == componentId)
            .OrderBy(r => r.Hostname)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Of a component's external routes, the hostnames an enabled AppRoute on the same cluster
    /// already serves.
    ///
    /// These routes are the ones the apply step refuses to send to the cluster, because doing so
    /// would replace the app's per-path rules with a single backend. Refusing is the right
    /// behaviour, but on its own it is invisible: the operator sees a route they configured,
    /// sitting in the list, apparently fine, and no explanation for why the cluster does not
    /// reflect it. This is what lets the UI say so, and offer to remove it.
    ///
    /// Creating such a route is blocked now, so the only ones left are those saved before that
    /// check existed.
    /// </summary>
    public async Task<IReadOnlySet<string>> ShadowedHostnamesAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // The cluster this component belongs to — an AppRoute only shadows a route on the same
        // cluster, since that is where the two would collide.
        Guid? clusterId = await db.ClusterComponents
            .Where(c => c.Id == componentId)
            .Select(c => (Guid?)c.ClusterId)
            .FirstOrDefaultAsync(ct);

        if (clusterId is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        List<string> routeHostnames = await db.ExternalRoutes
            .Where(r => r.ComponentId == componentId && r.TlsMode != TlsMode.Passthrough)
            .Select(r => r.Hostname)
            .ToListAsync(ct);

        if (routeHostnames.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Same set of AppRoutes the apply step consults, so the UI and the cluster agree on which
        // routes are being held back.
        List<string> appRouteHostnames = await db.AppRoutes
            .Where(r => r.IsEnabled && r.DeploymentRoutes.Any(dr =>
                dr.IsEnabled && dr.AppDeployment.ClusterId == clusterId))
            .Select(r => r.Hostname)
            .ToListAsync(ct);

        HashSet<string> owned = appRouteHostnames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return routeHostnames.Where(owned.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Changes the request timeout on an existing route. The caller re-applies the route
    /// afterwards — the value only reaches the cluster with the regenerated HTTPRoute.
    /// Null restores the platform default; 0 disables the timeout.
    /// </summary>
    public async Task UpdateRouteTimeoutAsync(
        Guid routeId, int? requestTimeoutSeconds, CancellationToken ct = default)
    {
        if (requestTimeoutSeconds is < 0)
        {
            throw new InvalidOperationException("Request timeout cannot be negative. Use 0 for no timeout.");
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ExternalRoute route = await db.ExternalRoutes
            .FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        route.RequestTimeoutSeconds = requestTimeoutSeconds;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "External route {Hostname} request timeout set to {Timeout}",
            route.Hostname, requestTimeoutSeconds?.ToString() ?? "default");
    }

    /// <summary>
    /// Sets session affinity on an existing route. Takes effect on the next apply of the
    /// component's routes — the DestinationRule is written there, not here, so nothing changes
    /// in the cluster until the operator re-applies.
    /// </summary>
    public async Task UpdateRouteSessionAffinityAsync(
        Guid routeId, SessionAffinityMode mode, string? key, int? ttlSeconds,
        CancellationToken ct = default)
    {
        if (ValidateSessionAffinity(mode, key) is string error)
        {
            throw new InvalidOperationException(error);
        }

        if (ttlSeconds is < 0)
        {
            throw new InvalidOperationException(
                "Affinity cookie lifetime cannot be negative. Leave it empty for a session cookie.");
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ExternalRoute route = await db.ExternalRoutes
            .FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        route.SessionAffinity = mode;
        route.SessionAffinityKey = NormalizeAffinityKey(mode, key);
        route.SessionAffinityTtlSeconds = mode == SessionAffinityMode.Cookie ? ttlSeconds : null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "External route {Hostname} session affinity set to {Mode}", route.Hostname, mode);
    }

    /// <summary>
    /// Trims the affinity key and drops it for the modes that have nothing to hash on, so a key
    /// left behind by a mode change cannot reappear if the mode is switched back.
    /// </summary>
    public static string? NormalizeAffinityKey(SessionAffinityMode mode, string? key)
    {
        if (mode is SessionAffinityMode.None or SessionAffinityMode.SourceIp)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>
    /// Deletes an external route. The caller should also remove the HTTPRoute
    /// from the cluster (via kubectl delete or the K8s API).
    /// </summary>
    public async Task DeleteRouteAsync(Guid routeId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ExternalRoute route = await db.ExternalRoutes
            .FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        db.ExternalRoutes.Remove(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("External route {RouteId} deleted", routeId);
    }

    /// <summary>
    /// Generates the Gateway API HTTPRoute YAML manifest for a route.
    /// This manifest can be applied to the cluster to expose the service.
    ///
    /// For ClusterIssuer TLS, it adds the cert-manager annotation.
    /// For Manual TLS, it references a Kubernetes Secret (the caller must
    /// create the Secret separately with the cert/key data).
    /// </summary>
    public async Task<string> GenerateHttpRouteYamlAsync(
        Guid routeId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ExternalRoute route = await db.ExternalRoutes
            .Include(r => r.Component)
            .FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        return GenerateHttpRouteYaml(route);
    }

    public async Task<string> GenerateFullManifestYamlAsync(
        Guid routeId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ExternalRoute route = await db.ExternalRoutes
            .Include(r => r.Component)
            .FirstOrDefaultAsync(r => r.Id == routeId, ct)
            ?? throw new InvalidOperationException("Route not found.");

        return GenerateFullManifestYaml(route);
    }

    /// <summary>
    /// Generates HTTPRoute YAML from an in-memory route object.
    /// Useful for previewing before saving.
    /// </summary>
    public static string GenerateHttpRouteYaml(ExternalRoute route)
    {
        string ns = route.Component?.Namespace ?? "default";
        // Name is hostname-based so it stays stable even when the service name is corrected.
        string routeName = ToListenerName(route.Hostname) + "-route";

        // TLS is terminated at the Gateway listener — HTTPRoute only routes by hostname/path.
        // No TLS section belongs in HTTPRoute.spec.

        var rule = new System.Text.StringBuilder();
        if (route.PathPrefix != "/")
        {
            rule.AppendLine("    - matches:");
            rule.AppendLine("        - path:");
            rule.AppendLine("            type: PathPrefix");
            rule.AppendLine($"            value: {route.PathPrefix}");
            rule.AppendLine("      backendRefs:");
            rule.AppendLine($"        - name: {route.ServiceName}");
            rule.AppendLine($"          port: {route.ServicePort}");
        }
        else
        {
            rule.AppendLine("    - backendRefs:");
            rule.AppendLine($"        - name: {route.ServiceName}");
            rule.AppendLine($"          port: {route.ServicePort}");
        }

        rule.Append(RenderHstsFilter("      "));
        rule.Append(RenderTimeouts(route.RequestTimeoutSeconds, "      "));
        rule.Append(RenderRetry("      "));

        return
            $"apiVersion: gateway.networking.k8s.io/v1\n" +
            $"kind: HTTPRoute\n" +
            $"metadata:\n" +
            $"  name: {routeName}\n" +
            $"  namespace: {ns}\n" +
            $"spec:\n" +
            RenderParentRefs(route.GatewayName, route.GatewayNamespace, [ToListenerName(route.Hostname)]) +
            $"  hostnames:\n" +
            $"    - {route.Hostname}\n" +
            $"  rules:\n" +
            rule.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Name of the Gateway's port-80 listener. Two things attach here and nothing else: the
    /// redirect to HTTPS, and cert-manager's HTTP-01 challenge solvers.
    /// </summary>
    public const string HttpListenerName = "http-redirect";

    /// <summary>
    /// Label a namespace must carry before its HTTPRoutes may attach to a TLS listener.
    /// </summary>
    public const string RouteNamespaceLabel = "entkube.io/routes";

    /// <summary>Value of <see cref="RouteNamespaceLabel"/> on a namespace allowed to attach.</summary>
    public const string RouteNamespaceLabelValue = "allowed";

    /// <summary>
    /// The <c>allowedRoutes</c> block for every TLS-terminating listener.
    ///
    /// <c>from: All</c> would let any namespace on the cluster attach an HTTPRoute to any
    /// hostname the Gateway serves — a route claiming <c>login.example.com</c> with a more
    /// specific path than the real one takes that path's traffic, and the only trace is a
    /// route object in a namespace nobody was watching. A namespace label is a weak boundary
    /// (whoever can label namespaces can cross it), but it moves the act from "create a route"
    /// to "hold cluster-scoped permissions", which is a different set of people.
    ///
    /// Namespaces are labelled as part of applying the Gateway, so this cannot detach a route
    /// EntKube knows about — see KubernetesOperationsService.LabelRouteNamespacesAsync.
    /// </summary>
    public const string RouteNamespaceSelector =
        "      allowedRoutes:\n" +
        "        namespaces:\n" +
        "          from: Selector\n" +
        "          selector:\n" +
        "            matchLabels:\n" +
        $"              {RouteNamespaceLabel}: {RouteNamespaceLabelValue}";

    /// <summary>
    /// Renders an HTTPRoute's <c>parentRefs</c>, pinned to named Gateway listeners.
    ///
    /// The pinning is the point. A parentRef without <c>sectionName</c> attaches to EVERY listener
    /// whose hostname matches — including the port-80 <c>http-redirect</c> listener, which matches
    /// everything because it has no hostname of its own. Both the app's route and
    /// <c>http-to-https-redirect</c> then sit on port 80, and Gateway API's precedence rules hand
    /// the request to the more specific hostname: the app's. The redirect never fires and the site
    /// is served in cleartext over HTTP, which is the opposite of what shipping a redirect listener
    /// was meant to achieve. Naming the HTTPS listener keeps port 80 to the redirect alone.
    /// </summary>
    public static string RenderParentRefs(
        string? gatewayName, string? gatewayNamespace, IEnumerable<string> sectionNames)
    {
        System.Text.StringBuilder refs = new("  parentRefs:\n");

        foreach (string section in sectionNames)
        {
            refs.Append(
                $"    - name: {gatewayName}\n" +
                $"      namespace: {gatewayNamespace}\n" +
                $"      sectionName: {section}\n");
        }

        return refs.ToString();
    }

    /// <summary>
    /// HSTS response header for routes served over a TLS-terminating listener. A year, with
    /// subdomains, and no <c>preload</c> — preload is a one-way door (removal takes months and a
    /// browser release) and is not ours to walk through on an operator's behalf.
    ///
    /// Safe to attach unconditionally now that routes no longer answer on port 80: the header only
    /// ever reaches a client that already completed a TLS handshake.
    /// </summary>
    public const string HstsHeaderValue = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// Renders the <c>ResponseHeaderModifier</c> filter carrying <see cref="HstsHeaderValue"/>,
    /// as a rule-level <c>filters:</c> block. Callers that already emit filters for the same rule
    /// must use <see cref="HstsFilterEntry"/> and merge instead — a second <c>filters:</c> key in
    /// one rule is invalid YAML.
    /// </summary>
    public static string RenderHstsFilter(string indent) =>
        $"{indent}filters:\n" + HstsFilterEntry(indent + "  ");

    /// <summary>The HSTS filter as a single list entry, for merging into an existing filter list.</summary>
    public static string HstsFilterEntry(string indent) =>
        $"{indent}- type: ResponseHeaderModifier\n" +
        $"{indent}  responseHeaderModifier:\n" +
        $"{indent}    set:\n" +
        $"{indent}      - name: Strict-Transport-Security\n" +
        $"{indent}        value: \"{HstsHeaderValue}\"\n";

    /// <summary>
    /// True when a Service port serves TLS, judged the way Istio itself judges it: the
    /// appProtocol field, or the port name (exactly "https"/"tls", or the "https-"/"tls-"
    /// prefix convention).
    ///
    /// Deliberately no port-number heuristic. Guessing that 8443 means TLS would break a
    /// working plaintext backend that happens to use that number; failing to spot a TLS port
    /// leaves the route exactly as broken as it already was, which is the safer direction to
    /// be wrong in.
    /// </summary>
    public static bool IsTlsBackendPort(KubeServicePort port)
    {
        if (port.AppProtocol is string app
            && (app.Equals("https", StringComparison.OrdinalIgnoreCase)
                || app.Equals("tls", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (port.Name is not string name)
        {
            return false;
        }

        return name.Equals("https", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tls", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("https-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tls-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generates the DestinationRule that tells an Istio ingress gateway how to connect to a
    /// backend Service, per port.
    ///
    /// The service-wide <c>trafficPolicy.tls.mode: DISABLE</c> exists so the gateway can reach
    /// sidecar-less pods without attempting mTLS. But it applies to EVERY port of the service,
    /// so on a service that mixes plaintext and TLS ports (keycloakx-http serves 80 and 8443)
    /// it also forces plaintext into the TLS port — the backend closes the connection and Envoy
    /// answers "upstream connect error or disconnect/reset before headers. reset reason:
    /// connection termination". <c>portLevelSettings</c> overrides the service-wide policy for
    /// exactly those ports, so the gateway originates TLS to them instead.
    ///
    /// Returns "" when <paramref name="alwaysEmit"/> is false and the service has no TLS port —
    /// callers that don't already ship a DestinationRule then ship nothing, leaving working
    /// clusters untouched.
    /// </summary>
    /// <param name="alwaysEmit">
    /// True for call sites that already emit a service-wide DISABLE rule today and must keep
    /// doing so; false for call sites adding a rule only because a TLS port needs one.
    /// </param>
    /// <param name="serviceWideTlsMode">
    /// The service-wide <c>trafficPolicy.tls.mode</c>. Defaults to DISABLE, the plaintext hop that
    /// lets a sidecar-less backend serve traffic. A namespace under STRICT mesh mTLS passes
    /// ISTIO_MUTUAL instead — see <see cref="MeshMtlsService.BackendTlsMode"/>.
    /// </param>
    /// <param name="sessionAffinity">
    /// Pins clients to a single backend pod via <c>loadBalancer.consistentHash</c>. Null or
    /// <see cref="SessionAffinityMode.None"/> emits no loadBalancer block at all, leaving the
    /// rule byte-identical to what it was before affinity existed.
    /// </param>
    public static string GenerateBackendDestinationRuleYaml(
        string serviceName, string serviceNamespace, string gatewayNamespace,
        IEnumerable<KubeServicePort> ports, bool alwaysEmit, string serviceWideTlsMode = "DISABLE",
        SessionAffinitySpec? sessionAffinity = null)
    {
        List<KubeServicePort> tlsPorts = ports.Where(IsTlsBackendPort).ToList();

        // Rendered once: an affinity that cannot be rendered (a header rule with no header name)
        // must not be the reason a rule is emitted below.
        string serviceWideHash = RenderConsistentHash(sessionAffinity, "    ");

        if (tlsPorts.Count == 0 && !alwaysEmit && serviceWideHash.Length == 0)
        {
            return "";
        }

        System.Text.StringBuilder yaml = new();
        yaml.Append(
            // The name is unchanged from when this rule only disabled mTLS. Renaming it would
            // leave the old service-wide rule orphaned in the cluster, still breaking the very
            // port this one fixes.
            $"apiVersion: networking.istio.io/v1beta1\n" +
            $"kind: DestinationRule\n" +
            $"metadata:\n" +
            $"  name: entkube-disable-mtls-{serviceName}\n" +
            $"  namespace: {gatewayNamespace}\n" +
            $"spec:\n" +
            $"  host: {serviceName}.{serviceNamespace}.svc.cluster.local\n" +
            $"  trafficPolicy:\n" +
            $"    tls:\n" +
            $"      mode: {serviceWideTlsMode}\n");

        yaml.Append(RenderConnectionPool("    "));
        yaml.Append(serviceWideHash);

        if (tlsPorts.Count > 0)
        {
            yaml.Append("    portLevelSettings:\n");
            foreach (KubeServicePort port in tlsPorts.OrderBy(p => p.Port))
            {
                // insecureSkipVerify because in-cluster backends almost always present a
                // self-signed certificate. This hop previously carried no encryption at all
                // (mode DISABLE), so encrypted-but-unverified is strictly an improvement on
                // what it replaces — it is not a downgrade of a verified path.
                yaml.Append(
                    $"      - port:\n" +
                    $"          number: {port.Port}\n" +
                    $"        tls:\n" +
                    $"          mode: SIMPLE\n" +
                    $"          insecureSkipVerify: true\n");

                // Repeated per port on purpose. Istio documents port-level settings as
                // replacing the destination-level policy rather than merging with it —
                // "traffic settings specified at the destination level will not be inherited
                // when overridden by port-level settings" — so a TLS port listed here would
                // otherwise fall back to the default load balancer and lose the affinity the
                // plaintext ports keep. The same applies to the connection pool: a port that
                // overrode tls but inherited nothing would keep Envoy's one-hour idle timeout,
                // which is the whole bug RenderConnectionPool exists to close.
                yaml.Append(RenderConnectionPool("        "));
                yaml.Append(RenderConsistentHash(sessionAffinity, "        "));
            }
        }

        return yaml.ToString();
    }

    /// <summary>
    /// How long Envoy may hold an idle upstream connection to a backend before closing it.
    ///
    /// This exists because of a race, not a performance concern. Envoy's default upstream idle
    /// timeout is one hour; Kestrel — every .NET backend EntKube publishes — closes an idle
    /// keep-alive socket after 130 seconds (<c>KeepAliveTimeout</c>). Between those two numbers
    /// sits a window where Envoy still believes a socket is good and Kestrel has already sent
    /// its FIN. A request dispatched into that socket gets "unexpected eof while reading", and
    /// the client sees <c>upstream connect error or disconnect/reset before headers. reset
    /// reason: connection termination</c> with <c>response_flags=UC</c> on the gateway. It is
    /// intermittent by nature: it needs a pool entry that has been idle past 130 seconds and a
    /// request arriving before Envoy notices the close.
    ///
    /// Any value comfortably below 130 makes the pool the first to give up, so the socket is
    /// gone before Kestrel can close it out from under a request. 60s leaves room for a backend
    /// tuned lower than the default without turning connection reuse off.
    /// </summary>
    public const int BackendConnectionIdleTimeoutSeconds = 60;

    /// <summary>
    /// Renders the <c>connectionPool</c> block of a DestinationRule trafficPolicy, every line
    /// prefixed with <paramref name="indent"/> (the indentation of <c>tls:</c> at the level it is
    /// being written into). See <see cref="BackendConnectionIdleTimeoutSeconds"/> for why.
    ///
    /// Unconditional: this is a property of the Envoy/Kestrel pairing, not of any one route's
    /// configuration, and a backend that closes idle sockets later than Envoy loses nothing by
    /// having its pool entries recycled a minute in.
    /// </summary>
    public static string RenderConnectionPool(string indent) =>
        $"{indent}connectionPool:\n" +
        $"{indent}  http:\n" +
        $"{indent}    idleTimeout: {BackendConnectionIdleTimeoutSeconds}s\n";

    /// <summary>
    /// Cookie name used when a route asks for cookie affinity without naming one. Envoy sets
    /// this cookie itself on the first response, so the application never has to know it exists.
    /// </summary>
    public const string DefaultAffinityCookieName = "ENTKUBE_AFFINITY";

    /// <summary>
    /// Renders the <c>loadBalancer.consistentHash</c> block of a DestinationRule trafficPolicy,
    /// every line prefixed with <paramref name="indent"/> (the indentation of <c>tls:</c> at the
    /// level it is being written into).
    ///
    /// Returns "" for no affinity, and also for an affinity that cannot be expressed — a header
    /// or query-parameter rule with no name to hash on. Emitting a half-written consistentHash
    /// would be rejected by the API server and take the whole apply down with it, including the
    /// TLS settings in the same document that are keeping the route alive.
    /// </summary>
    public static string RenderConsistentHash(SessionAffinitySpec? affinity, string indent)
    {
        if (affinity is not { } spec || spec.Mode == SessionAffinityMode.None)
        {
            return "";
        }

        string? key = string.IsNullOrWhiteSpace(spec.Key) ? null : spec.Key.Trim();

        string inner = spec.Mode switch
        {
            // ttl is always written, never omitted: Istio's validating webhook rejects an
            // httpCookie without one ("ttl required for HttpCookie"), and a rejected
            // DestinationRule fails the apply it travels with. 0s is Envoy's session cookie —
            // it expires when the browser closes — which is what "no lifetime set" means here.
            SessionAffinityMode.Cookie =>
                $"{indent}      httpCookie:\n" +
                $"{indent}        name: {key ?? DefaultAffinityCookieName}\n" +
                $"{indent}        ttl: {(spec.TtlSeconds is int ttl and > 0 ? ttl : 0)}s\n",
            SessionAffinityMode.Header when key is not null =>
                $"{indent}      httpHeaderName: {key}\n",
            SessionAffinityMode.QueryParameter when key is not null =>
                $"{indent}      httpQueryParameterName: {key}\n",
            SessionAffinityMode.SourceIp =>
                $"{indent}      useSourceIp: true\n",
            _ => ""
        };

        if (inner.Length == 0)
        {
            return "";
        }

        return $"{indent}loadBalancer:\n" +
               $"{indent}  consistentHash:\n" +
               inner;
    }

    /// <summary>
    /// Reports the reason <paramref name="mode"/> cannot be applied with <paramref name="key"/>,
    /// or null when the pair is valid. Callers surface this before saving, so an affinity that
    /// would silently render nothing is refused at the point someone can still fix it.
    /// </summary>
    public static string? ValidateSessionAffinity(SessionAffinityMode mode, string? key)
    {
        bool hasKey = !string.IsNullOrWhiteSpace(key);

        return mode switch
        {
            SessionAffinityMode.Header when !hasKey =>
                "Header affinity needs the name of the header to hash on (e.g. x-tenant-id).",
            SessionAffinityMode.QueryParameter when !hasKey =>
                "Query-parameter affinity needs the name of the parameter to hash on.",
            _ => null
        };
    }

    /// <summary>The request timeout applied to generated HTTPRoute rules when a route sets none.</summary>
    public const int DefaultRequestTimeoutSeconds = 60;

    /// <summary>
    /// Timeout for routes that legitimately carry multi-minute requests — container image pushes
    /// and pulls through Harbor, where a single layer upload can run far past the default. Long,
    /// but still finite, so a wedged registry eventually fails instead of hanging the client.
    /// </summary>
    public const int RegistryRequestTimeoutSeconds = 3600;

    /// <summary>
    /// Renders the Gateway API <c>timeouts</c> block for one HTTPRoute rule, each line prefixed
    /// with <paramref name="indent"/> (the indentation of the rule's other keys).
    ///
    /// Without this block the gateway applies no timeout at all, so a wedged upstream holds the
    /// browser's connection open indefinitely instead of failing fast. Returns an empty string
    /// when the route opts out with 0 — the escape hatch for long-lived streams, since
    /// <c>timeouts.request</c> bounds the whole exchange rather than the time to first byte.
    /// </summary>
    public static string RenderTimeouts(int? requestTimeoutSeconds, string indent)
    {
        int seconds = requestTimeoutSeconds ?? DefaultRequestTimeoutSeconds;
        if (seconds <= 0)
        {
            return "";
        }

        // backendRequest must not exceed request; equal values give each attempt the same budget
        // as the overall request. Gateway API bounds every retry attempt by request, so a retry
        // after a full backendRequest timeout has no budget left — deliberate. The retries this
        // route carries (see RenderRetry) are for connections that fail instantly, not for
        // second-guessing a backend that is merely slow.
        return $"{indent}timeouts:\n" +
               $"{indent}  request: {seconds}s\n" +
               $"{indent}  backendRequest: {seconds}s\n";
    }

    /// <summary>Attempts a failed backend request gets, including the first.</summary>
    public const int DefaultRetryAttempts = 2;

    /// <summary>
    /// Minimum wait between attempts. Small on purpose: the failures this retries are refused or
    /// reset connections, which fail in under a millisecond, so a long backoff would only add
    /// latency to a request that is about to succeed.
    /// </summary>
    public const string DefaultRetryBackoff = "50ms";

    /// <summary>
    /// Renders the Gateway API <c>retry</c> block for one HTTPRoute rule, each line prefixed with
    /// <paramref name="indent"/> (the indentation of the rule's other keys).
    ///
    /// A connection that is refused, reset, or dropped before the backend saw the request is safe
    /// to retry regardless of method — nothing happened on the other end to repeat. Without this
    /// the client owns that failure and sees a 503.
    ///
    /// Two caveats worth knowing before reading a retry into this block:
    ///
    /// Gateway API has no vocabulary for reset conditions. <c>retry</c> takes attempts, a backoff
    /// and status codes, nothing else, so the conditions cannot be named here. Istio fills them in
    /// on conversion with a fixed <c>connect-failure,refused-stream,unavailable,cancelled</c> plus
    /// whatever <c>codes</c> lists. Notably absent from that list is Envoy's <c>reset</c>, which is
    /// the condition an upstream half-closed socket actually trips — so this block does NOT cover
    /// the Kestrel idle-timeout race. <see cref="BackendConnectionIdleTimeoutSeconds"/> is what
    /// closes that; this is the layer underneath it.
    ///
    /// <c>codes</c> is listed rather than left empty because Istio applies a default retry policy
    /// of its own to any route that sets none, and that default retries 503 via
    /// <c>retriable-status-codes</c>. Setting <c>retry</c> at all replaces the default outright, so
    /// omitting 503 here would quietly take away retries the cluster has today.
    /// </summary>
    public static string RenderRetry(string indent) =>
        $"{indent}retry:\n" +
        $"{indent}  attempts: {DefaultRetryAttempts}\n" +
        $"{indent}  backoff: {DefaultRetryBackoff}\n" +
        $"{indent}  codes:\n" +
        $"{indent}    - 503\n";

    /// <summary>
    /// Generates a TLSRoute YAML for passthrough-mode routes. The gateway routes by SNI
    /// without terminating TLS — the backend pod must handle TLS itself.
    /// </summary>
    public static string GenerateTlsRouteYaml(ExternalRoute route)
    {
        string ns = route.Component?.Namespace ?? "default";
        string routeName = ToListenerName(route.Hostname) + "-route";
        string sectionName = ToListenerName(route.Hostname);

        return $"""
            apiVersion: gateway.networking.k8s.io/v1alpha2
            kind: TLSRoute
            metadata:
              name: {routeName}
              namespace: {ns}
            spec:
              parentRefs:
                - name: {route.GatewayName}
                  namespace: {route.GatewayNamespace}
                  sectionName: {sectionName}
              hostnames:
                - {route.Hostname}
              rules:
                - backendRefs:
                    - name: {route.ServiceName}
                      port: {route.ServicePort}
            """;
    }

    /// <summary>
    /// Generates a cert-manager Certificate resource for ClusterIssuer TLS mode.
    /// cert-manager will provision and renew the TLS secret automatically.
    /// Returns empty string for Manual TLS (user supplies the certificate).
    /// </summary>
    public static string GenerateCertificateYaml(ExternalRoute route)
    {
        if (route.TlsMode != TlsMode.ClusterIssuer || string.IsNullOrWhiteSpace(route.ClusterIssuerName))
        {
            return "";
        }

        string ns = route.Component?.Namespace ?? "default";
        string secretName = $"{route.ServiceName}-tls";

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

    /// <summary>
    /// Generates the complete manifest for a route: HTTPRoute plus a Certificate
    /// resource (when using ClusterIssuer TLS). Apply this single YAML to the cluster.
    /// </summary>
    public static string GenerateFullManifestYaml(ExternalRoute route)
    {
        string httpRoute = GenerateHttpRouteYaml(route);
        string certificate = GenerateCertificateYaml(route);

        return string.IsNullOrEmpty(certificate)
            ? httpRoute
            : $"{httpRoute}\n---\n{certificate}";
    }

    /// <summary>
    /// Generates a Kubernetes TLS Secret YAML for manual certificate mode.
    /// The caller applies this to the cluster before or alongside the HTTPRoute.
    /// </summary>
    public static string GenerateTlsSecretYaml(ExternalRoute route)
    {
        if (route.TlsMode != TlsMode.Manual || string.IsNullOrWhiteSpace(route.TlsCertificate))
        {
            return "";
        }

        string ns = route.Component?.Namespace ?? "default";
        string secretName = $"{route.ServiceName}-tls";

        // Base64-encode the cert and key for Kubernetes Secret.

        string certBase64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(route.TlsCertificate));
        string keyBase64 = !string.IsNullOrWhiteSpace(route.TlsPrivateKey)
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(route.TlsPrivateKey))
            : "";

        string yaml = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {secretName}
              namespace: {ns}
            type: kubernetes.io/tls
            data:
              tls.crt: {certBase64}
              tls.key: {keyBase64}
            """;

        return yaml;
    }

    /// <summary>
    /// Generates a <c>gateway.networking.k8s.io/v1/Gateway</c> resource with one HTTPS
    /// listener per unique hostname in <paramref name="routes"/>, plus an HTTP-to-HTTPS
    /// redirect listener, cert-manager Certificate resources, and a ReferenceGrant.
    ///
    /// Certificates are placed in <paramref name="certNamespace"/> (default: cert-manager)
    /// rather than in the gateway namespace. <c>istio-system</c> often has admission
    /// controls (Kyverno, OPA) that silently block CertificateRequest creation; the
    /// cert-manager namespace is guaranteed to be unrestricted for cert-manager's own
    /// controllers. A ReferenceGrant is generated in <paramref name="certNamespace"/>
    /// so the Gateway in <paramref name="gatewayNamespace"/> can cross-reference the
    /// resulting TLS Secrets.
    ///
    /// The returned YAML contains multiple documents separated by <c>---</c>:
    ///   1. The Gateway resource (certificateRefs include namespace: certNamespace)
    ///   2. An HTTP→HTTPS redirect HTTPRoute
    ///   3. A ReferenceGrant in certNamespace
    ///   4. One Certificate per ClusterIssuer-mode hostname (in certNamespace)
    ///   5. One ConfigMap per mTLS listener port, holding that port's client-CA trust store
    ///
    /// Routes requiring a client certificate get an extra listener on their trust anchor's port
    /// (see <see cref="MtlsService"/>) — client-certificate validation is per-port, so it cannot
    /// ride on the shared 443 listener without imposing certificates on every other hostname.
    /// Those routes must have <c>ClientCaBundle</c> loaded.
    /// </summary>
    public static string GenerateGatewayYaml(
        string gatewayName,
        string gatewayNamespace,
        IEnumerable<ExternalRoute> routes,
        IEnumerable<AppRoute>? appRoutes = null,
        string certNamespace = "cert-manager",
        string gatewayClass = "istio")
    {
        // Merge ExternalRoutes and AppRoutes into a unified hostname list.
        var allHostnames = routes
            .Select(r => (r.Hostname, r.TlsMode, r.ClusterIssuerName))
            .Concat((appRoutes ?? [])
                .Where(r => r.IsEnabled)
                .Select(r => (r.Hostname, r.TlsMode, r.ClusterIssuerName)));

        // Routes requiring a client certificate are published on their anchor's port. Hostnames
        // marked mTLS-only additionally drop off 443, so the app is unreachable without a cert.
        MtlsService.MtlsClusterPlan mtlsPlan = MtlsService.BuildPlan(appRoutes ?? [], gatewayNamespace);

        var grouped = allHostnames
            .GroupBy(r => r.Hostname)
            .Select(g => (
                Hostname: g.Key,
                ListenerName: ToListenerName(g.Key),
                CertSecretName: ToCertSecretName(g.Key),
                ClusterIssuerName: g.Select(r => r.ClusterIssuerName).FirstOrDefault(n => n != null),
                IsCertIssuer: g.Any(r => r.TlsMode == TlsMode.ClusterIssuer),
                IsPassthrough: g.All(r => r.TlsMode == TlsMode.Passthrough)
            ))
            .ToList();

        // certificateRefs include namespace so the Gateway can cross-reference Secrets
        // in certNamespace. Istio honours cross-namespace refs when a ReferenceGrant exists.
        // Passthrough-mode hostnames use TLS/Passthrough listeners (no cert at gateway level).
        // mTLS-only hostnames are skipped here and rendered as an mTLS listener below. They stay in
        // `grouped` so their cert-manager Certificate is still generated — the mTLS listener
        // terminates TLS with that same secret, so dropping it would leave the listener referencing
        // a Secret nothing creates.
        IEnumerable<string> httpsListeners = grouped
            .Where(g => !mtlsPlan.MtlsOnlyHosts.Contains(g.Hostname))
            .Select(g =>
            g.IsPassthrough
                ? $"    - name: {g.ListenerName}\n" +
                  $"      hostname: {g.Hostname}\n" +
                  $"      port: 443\n" +
                  $"      protocol: TLS\n" +
                  $"      tls:\n" +
                  $"        mode: Passthrough\n" +
                  RouteNamespaceSelector
                : $"    - name: {g.ListenerName}\n" +
                  $"      hostname: {g.Hostname}\n" +
                  $"      port: 443\n" +
                  $"      protocol: HTTPS\n" +
                  $"      tls:\n" +
                  $"        mode: Terminate\n" +
                  $"        certificateRefs:\n" +
                  $"          - name: {g.CertSecretName}\n" +
                  $"            namespace: {certNamespace}\n" +
                  RouteNamespaceSelector);

        // Port 80 stays open to all namespaces: cert-manager creates its ACME HTTP-01 solver
        // routes in its own namespace, which nothing here labels, and a challenge that cannot
        // attach is a certificate that cannot renew. The listener carries no hostname and only
        // ever redirects, so the exposure it grants is the right to be redirected to HTTPS.
        const string httpListener =
            $"    - name: {HttpListenerName}\n" +
            "      port: 80\n" +
            "      protocol: HTTP\n" +
            "      allowedRoutes:\n" +
            "        namespaces:\n" +
            "          from: All";

        // One HTTPS listener per mTLS hostname on its anchor's port. TLS termination is identical
        // to the 443 listener (same server certificate) — the client-certificate requirement comes
        // from the Gateway's spec.tls.frontend.perPort entry for this port, not from here.
        IEnumerable<string> mtlsListeners = mtlsPlan.HostPorts
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h =>
                $"    - name: {MtlsService.MtlsListenerName(h.Key, h.Value)}\n" +
                $"      hostname: {h.Key}\n" +
                $"      port: {h.Value}\n" +
                $"      protocol: HTTPS\n" +
                $"      tls:\n" +
                $"        mode: Terminate\n" +
                $"        certificateRefs:\n" +
                $"          - name: {ToCertSecretName(h.Key)}\n" +
                $"            namespace: {certNamespace}\n" +
                RouteNamespaceSelector);

        string allListeners = string.Join("\n", httpsListeners.Concat(mtlsListeners).Append(httpListener));

        // Istio needs an explicit address binding to avoid creating a second LoadBalancer service.
        // Traefik manages its own service — omitting addresses lets Traefik handle it.
        string addressesYaml = gatewayClass == "istio"
            ? $"  addresses:\n" +
              $"    - type: Hostname\n" +
              $"      value: {gatewayName}.{gatewayNamespace}.svc.cluster.local\n"
            : "";

        string gatewayYaml =
            $"apiVersion: gateway.networking.k8s.io/v1\n" +
            $"kind: Gateway\n" +
            $"metadata:\n" +
            $"  name: {gatewayName}\n" +
            $"  namespace: {gatewayNamespace}\n" +
            $"  annotations:\n" +
            $"    app.kubernetes.io/managed-by: entkube\n" +
            $"spec:\n" +
            $"  gatewayClassName: {gatewayClass}\n" +
            addressesYaml +
            MtlsService.BuildGatewayTlsBlock(mtlsPlan.BundlesByPort.Keys) +
            $"  listeners:\n" +
            allListeners;

        string httpRedirectRoute =
            $"apiVersion: gateway.networking.k8s.io/v1\n" +
            $"kind: HTTPRoute\n" +
            $"metadata:\n" +
            $"  name: http-to-https-redirect\n" +
            $"  namespace: {gatewayNamespace}\n" +
            $"spec:\n" +
            $"  parentRefs:\n" +
            $"    - name: {gatewayName}\n" +
            $"      namespace: {gatewayNamespace}\n" +
            $"      sectionName: {HttpListenerName}\n" +
            $"  rules:\n" +
            $"    - filters:\n" +
            $"        - type: RequestRedirect\n" +
            $"          requestRedirect:\n" +
            $"            scheme: https\n" +
            $"            statusCode: 301";

        // ReferenceGrant in certNamespace — allows the Gateway in gatewayNamespace to
        // read Secrets in certNamespace without needing cluster-admin permissions.
        string referenceGrant =
            $"apiVersion: gateway.networking.k8s.io/v1beta1\n" +
            $"kind: ReferenceGrant\n" +
            $"metadata:\n" +
            $"  name: gateway-tls-from-{ToListenerName(gatewayNamespace)}\n" +
            $"  namespace: {certNamespace}\n" +
            $"spec:\n" +
            $"  from:\n" +
            $"    - group: gateway.networking.k8s.io\n" +
            $"      kind: Gateway\n" +
            $"      namespace: {gatewayNamespace}\n" +
            $"  to:\n" +
            $"    - group: \"\"\n" +
            $"      kind: Secret";

        List<string> parts = [gatewayYaml, httpRedirectRoute, referenceGrant, .. mtlsPlan.CaConfigMaps];

        foreach (var g in grouped.Where(g => g.IsCertIssuer && !string.IsNullOrWhiteSpace(g.ClusterIssuerName)))
        {
            parts.Add(
                $"apiVersion: cert-manager.io/v1\n" +
                $"kind: Certificate\n" +
                $"metadata:\n" +
                $"  name: {g.CertSecretName}\n" +
                $"  namespace: {certNamespace}\n" +
                $"spec:\n" +
                $"  secretName: {g.CertSecretName}\n" +
                $"  issuerRef:\n" +
                $"    name: {g.ClusterIssuerName}\n" +
                $"    kind: ClusterIssuer\n" +
                $"  dnsNames:\n" +
                $"    - {g.Hostname}");
        }

        return string.Join("\n---\n", parts);
    }

    /// <summary>
    /// Sanitizes a hostname into a valid Kubernetes resource name / listener name
    /// by replacing non-alphanumeric characters with dashes, trimming edge dashes,
    /// and capping at 63 characters (DNS label limit).
    /// </summary>
    public static string ToListenerName(string hostname)
    {
        string sanitized = new string(hostname.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');
        return sanitized.Length > 63 ? sanitized[..63] : sanitized;
    }

    /// <summary>Derives the TLS secret name from a hostname (used in both the Gateway listener and Certificate).</summary>
    public static string ToCertSecretName(string hostname) => ToListenerName(hostname) + "-tls";

    // ── Raw L4 TCP/UDP (dedicated gateway) ──

    /// <summary>
    /// Name of the dedicated per-cluster Gateway that carries raw TCP/UDP listeners. Kept distinct
    /// from the HTTP <c>default-gateway</c> so it can be provisioned with its own LoadBalancer.
    /// </summary>
    public const string L4GatewayName = "entkube-l4-gateway";

    /// <summary>
    /// Resolves the (name, namespace) of the dedicated L4 Gateway. The namespace tracks the
    /// installed ingress controller's namespace (istio-system for Istio) so the auto-provisioned
    /// gateway Deployment/Service lands beside the ingress it belongs to.
    /// </summary>
    public static (string Name, string Namespace) ResolveL4Gateway(IEnumerable<ClusterComponent> components)
    {
        (_, string ns) = ResolveGateway(components);
        return (L4GatewayName, ns);
    }

    /// <summary>Listener/section name for a protocol+port (e.g. TCP 5432 → "tcp-5432", UDP 53 → "udp-53").</summary>
    public static string L4ListenerName(L4Protocol protocol, int port)
        => $"{protocol.ToString().ToLowerInvariant()}-{port}";

    /// <summary>
    /// Generates the dedicated L4 Gateway with one listener per enabled route (protocol: TCP or UDP).
    /// Unlike the HTTP gateway this omits <c>addresses</c>, so Istio's Gateway API controller
    /// auto-provisions a LoadBalancer Service that opens exactly these ports (its own external IP).
    /// TCP and UDP on the same port number produce two distinct listeners. Regenerate wholesale on
    /// every route change. Returns an empty string when there are no ports to expose.
    /// </summary>
    public static string GenerateL4GatewayYaml(
        string gatewayNamespace,
        IEnumerable<AppL4Route> routes,
        string gatewayClass = "istio")
    {
        // Distinct (protocol, port) pairs across all enabled routes — one listener each.
        var listenerSpecs = routes
            .Where(r => r.IsEnabled)
            .Select(r => (r.Protocol, r.ExternalPort))
            .Distinct()
            .OrderBy(x => x.Protocol)
            .ThenBy(x => x.ExternalPort)
            .ToList();

        if (listenerSpecs.Count == 0) return "";

        string listeners = string.Join("\n", listenerSpecs.Select(spec =>
        {
            string proto = spec.Protocol == L4Protocol.Udp ? "UDP" : "TCP";
            string kind = spec.Protocol == L4Protocol.Udp ? "UDPRoute" : "TCPRoute";
            return
                $"    - name: {L4ListenerName(spec.Protocol, spec.ExternalPort)}\n" +
                $"      port: {spec.ExternalPort}\n" +
                $"      protocol: {proto}\n" +
                $"      allowedRoutes:\n" +
                $"        kinds:\n" +
                $"          - kind: {kind}\n" +
                $"        namespaces:\n" +
                $"          from: All";
        }));

        return
            $"apiVersion: gateway.networking.k8s.io/v1\n" +
            $"kind: Gateway\n" +
            $"metadata:\n" +
            $"  name: {L4GatewayName}\n" +
            $"  namespace: {gatewayNamespace}\n" +
            $"  annotations:\n" +
            $"    app.kubernetes.io/managed-by: entkube\n" +
            $"spec:\n" +
            $"  gatewayClassName: {gatewayClass}\n" +
            $"  listeners:\n" +
            listeners;
    }

    // ── Private helpers ──

    /// <summary>
    /// Returns the (gatewayName, gatewayNamespace) for the ingress controller installed
    /// on the cluster. Checks both by component Name and by ReleaseName/HelmChartName
    /// to handle imported components and custom release names.
    /// </summary>
    public static (string Name, string Namespace) ResolveGateway(IEnumerable<ClusterComponent> components)
    {
        List<ClusterComponent> list = components.ToList();

        bool hasTraefik = list.Any(c =>
            string.Equals(c.Name, "traefik", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.HelmChartName, "traefik", StringComparison.OrdinalIgnoreCase));

        if (hasTraefik)
        {
            return ("traefik-gateway", "traefik");
        }

        ClusterComponent? istio = list.FirstOrDefault(c =>
            string.Equals(c.Name, "istio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.HelmChartName, "gateway", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(c.Namespace, "istio-system", StringComparison.OrdinalIgnoreCase)));

        if (istio is not null)
        {
            string name = istio.ReleaseName ?? istio.Name;
            return (name, istio.Namespace ?? "istio-system");
        }

        return ("default-gateway", "default");
    }

    /// <summary>
    /// Returns the Kubernetes GatewayClass name for the installed ingress controller.
    /// Must match what the controller registers as its GatewayClass — using the wrong
    /// class causes the controller to silently ignore the Gateway resource.
    /// </summary>
    public static string ResolveGatewayClass(IEnumerable<ClusterComponent> components)
    {
        bool hasTraefik = components.Any(c =>
            string.Equals(c.Name, "traefik", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.HelmChartName, "traefik", StringComparison.OrdinalIgnoreCase));

        return hasTraefik ? "traefik" : "istio";
    }

    /// <summary>
    /// Returns the Kubernetes ingressClassName for cert-manager HTTP-01 ACME challenges.
    /// Uses standard Ingress (not gatewayHTTPRoute) so no cert-manager experimental
    /// feature gates are needed. Istio handles ingressClassName "istio" natively.
    /// </summary>
    public static string ResolveIngressClass(IEnumerable<ClusterComponent> components)
    {
        bool hasTraefik = components.Any(c =>
            string.Equals(c.Name, "traefik", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.HelmChartName, "traefik", StringComparison.OrdinalIgnoreCase));

        return hasTraefik ? "traefik" : "istio";
    }

    public async Task<RouteUptimeSummary> GetRouteUptimeAsync(
        Guid routeId, int windowDays = 7, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        List<ExternalRouteHealthHistory> history = await db.ExternalRouteHealthHistories
            .Where(h => h.RouteId == routeId && h.CheckedAt >= from)
            .OrderBy(h => h.CheckedAt)
            .ToListAsync(ct);

        if (history.Count == 0)
            return new RouteUptimeSummary(routeId, windowDays, null, 0, history);

        double uptimePct = (double)history.Count(h => h.IsReachable) / history.Count * 100;
        double avgResponseMs = history
            .Where(h => h.ResponseMs.HasValue)
            .Select(h => (double)h.ResponseMs!.Value)
            .DefaultIfEmpty(0)
            .Average();

        return new RouteUptimeSummary(routeId, windowDays, Math.Round(uptimePct, 2), Math.Round(avgResponseMs), history);
    }

    private static string ResolveGatewayName(IEnumerable<ClusterComponent> components) =>
        ResolveGateway(components).Name;

    private static string ResolveGatewayNamespace(IEnumerable<ClusterComponent> components) =>
        ResolveGateway(components).Namespace;
}

public record RouteUptimeSummary(
    Guid RouteId,
    int WindowDays,
    double? UptimePercent,
    double AvgResponseMs,
    List<ExternalRouteHealthHistory> History)
{
    public string UptimeDisplay => UptimePercent.HasValue ? $"{UptimePercent:F2}%" : "No data";
}

/// <summary>
/// One backend Service's session-affinity setting, lifted out of whichever kind of route asked
/// for it. A DestinationRule belongs to the Service, and both an ExternalRoute (platform
/// component) and an AppDeploymentRoute (customer app) can point at the same Service, so the
/// generator takes this rather than a route.
/// </summary>
public sealed record SessionAffinitySpec(SessionAffinityMode Mode, string? Key, int? TtlSeconds)
{
    /// <summary>No affinity — the gateway load balances freely.</summary>
    public static readonly SessionAffinitySpec None = new(SessionAffinityMode.None, null, null);

    public bool IsActive => Mode != SessionAffinityMode.None;

    public static SessionAffinitySpec From(ExternalRoute route) =>
        new(route.SessionAffinity, route.SessionAffinityKey, route.SessionAffinityTtlSeconds);

    public static SessionAffinitySpec From(AppDeploymentRoute route) =>
        new(route.SessionAffinity, route.SessionAffinityKey, route.SessionAffinityTtlSeconds);

    /// <summary>
    /// Collapses the routes sharing one Service into the single affinity its DestinationRule can
    /// carry. The first route asking for affinity wins: there is one rule per Service, so two
    /// routes disagreeing cannot both be honoured, and picking the first (callers iterate in a
    /// stable order) at least keeps successive applies from flapping the cluster between them.
    /// </summary>
    public static SessionAffinitySpec Merge(IEnumerable<SessionAffinitySpec> specs) =>
        specs.FirstOrDefault(spec => spec.IsActive) ?? None;
}
