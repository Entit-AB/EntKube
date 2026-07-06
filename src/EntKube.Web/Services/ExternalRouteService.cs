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

        // Check for duplicate hostname on this cluster.

        List<Guid> clusterComponentIds = component.Cluster.Components.Select(c => c.Id).ToList();
        bool duplicateHostname = await db.ExternalRoutes
            .AnyAsync(r => clusterComponentIds.Contains(r.ComponentId)
                && r.Hostname == request.Hostname.Trim().ToLowerInvariant(), ct);

        if (duplicateHostname)
        {
            throw new InvalidOperationException(
                $"Hostname '{request.Hostname}' is already in use on this cluster.");
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
            Hostname = request.Hostname.Trim().ToLowerInvariant(),
            ServiceName = request.ServiceName ?? component.ReleaseName ?? component.Name,
            ServicePort = request.ServicePort,
            PathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? "/" : request.PathPrefix.Trim(),
            TlsMode = request.TlsMode,
            ClusterIssuerName = request.ClusterIssuerName?.Trim(),
            TlsCertificate = request.TlsCertificate,
            TlsPrivateKey = request.TlsPrivateKey,
            GatewayName = gatewayName,
            GatewayNamespace = gatewayNamespace
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

        string pathMatch = route.PathPrefix != "/"
            ? $"""
                      - matches:
                          - path:
                              type: PathPrefix
                              value: {route.PathPrefix}
                        backendRefs:
                          - name: {route.ServiceName}
                            port: {route.ServicePort}
               """
            : $"""
                      - backendRefs:
                          - name: {route.ServiceName}
                            port: {route.ServicePort}
               """;

        return $"""
            apiVersion: gateway.networking.k8s.io/v1
            kind: HTTPRoute
            metadata:
              name: {routeName}
              namespace: {ns}
            spec:
              parentRefs:
                - name: {route.GatewayName}
                  namespace: {route.GatewayNamespace}
              hostnames:
                - {route.Hostname}
              rules:
            {pathMatch}
            """;
    }

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
        IEnumerable<string> httpsListeners = grouped.Select(g =>
            g.IsPassthrough
                ? $"    - name: {g.ListenerName}\n" +
                  $"      hostname: {g.Hostname}\n" +
                  $"      port: 443\n" +
                  $"      protocol: TLS\n" +
                  $"      tls:\n" +
                  $"        mode: Passthrough\n" +
                  $"      allowedRoutes:\n" +
                  $"        namespaces:\n" +
                  $"          from: All"
                : $"    - name: {g.ListenerName}\n" +
                  $"      hostname: {g.Hostname}\n" +
                  $"      port: 443\n" +
                  $"      protocol: HTTPS\n" +
                  $"      tls:\n" +
                  $"        mode: Terminate\n" +
                  $"        certificateRefs:\n" +
                  $"          - name: {g.CertSecretName}\n" +
                  $"            namespace: {certNamespace}\n" +
                  $"      allowedRoutes:\n" +
                  $"        namespaces:\n" +
                  $"          from: All");

        // HTTP listener allows routes from All namespaces so cert-manager can attach
        // its ACME HTTP-01 challenge HTTPRoute (created in the cert-manager namespace).
        const string httpListener =
            "    - name: http-redirect\n" +
            "      port: 80\n" +
            "      protocol: HTTP\n" +
            "      allowedRoutes:\n" +
            "        namespaces:\n" +
            "          from: All";

        string allListeners = string.Join("\n", httpsListeners.Append(httpListener));

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
            $"      sectionName: http-redirect\n" +
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

        List<string> parts = [gatewayYaml, httpRedirectRoute, referenceGrant];

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
