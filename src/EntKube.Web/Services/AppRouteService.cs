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
}

public class AppDeploymentRouteRequest
{
    public required string ServiceName { get; set; }
    public int ServicePort { get; set; } = 80;
    public string PathPrefix { get; set; } = "/";
    public string? RewritePath { get; set; }
    public bool IsEnabled { get; set; } = true;
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

        AppRoute route = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Hostname = hostname,
            TlsMode = request.TlsMode,
            ClusterIssuerName = request.ClusterIssuerName?.Trim(),
            TlsCertificate = request.TlsCertificate,
            TlsPrivateKey = request.TlsPrivateKey,
            IsEnabled = request.IsEnabled
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

        route.Hostname = hostname;
        route.TlsMode = request.TlsMode;
        route.ClusterIssuerName = request.ClusterIssuerName?.Trim();
        route.TlsCertificate = request.TlsCertificate;
        route.TlsPrivateKey = request.TlsPrivateKey;
        route.IsEnabled = request.IsEnabled;

        await db.SaveChangesAsync(ct);
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

        bool duplicate = await db.AppDeploymentRoutes
            .AnyAsync(r => r.AppRouteId == appRouteId && r.AppDeploymentId == deploymentId, ct);
        if (duplicate)
            throw new InvalidOperationException("This deployment is already linked to this route.");

        (string gatewayName, string gatewayNamespace) =
            ExternalRouteService.ResolveGateway(deployment.Cluster.Components);

        AppDeploymentRoute dr = new()
        {
            Id = Guid.NewGuid(),
            AppRouteId = appRouteId,
            AppDeploymentId = deploymentId,
            PathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? "/" : request.PathPrefix.Trim(),
            RewritePath = string.IsNullOrWhiteSpace(request.RewritePath) ? null : request.RewritePath.Trim(),
            ServiceName = request.ServiceName.Trim(),
            ServicePort = request.ServicePort,
            GatewayName = gatewayName,
            GatewayNamespace = gatewayNamespace,
            IsEnabled = request.IsEnabled
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

        dr.PathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? "/" : request.PathPrefix.Trim();
        dr.RewritePath = string.IsNullOrWhiteSpace(request.RewritePath) ? null : request.RewritePath.Trim();
        dr.ServiceName = request.ServiceName.Trim();
        dr.ServicePort = request.ServicePort;
        dr.IsEnabled = request.IsEnabled;
        dr.ClusterAppliedAt = null; // route changed — must be re-applied

        await db.SaveChangesAsync(ct);
    }

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
            if (dr.PathPrefix != "/")
            {
                rules.AppendLine($"    - matches:");
                rules.AppendLine($"        - path:");
                rules.AppendLine($"            type: PathPrefix");
                rules.AppendLine($"            value: {dr.PathPrefix}");
                if (dr.RewritePath is not null)
                {
                    rules.AppendLine($"      filters:");
                    rules.AppendLine($"        - type: URLRewrite");
                    rules.AppendLine($"          urlRewrite:");
                    rules.AppendLine($"            path:");
                    rules.AppendLine($"              type: ReplacePrefixMatch");
                    rules.AppendLine($"              replacePrefixMatch: {dr.RewritePath}");
                }
                rules.AppendLine($"      backendRefs:");
                rules.AppendLine($"        - name: {dr.ServiceName}");
                rules.AppendLine($"          namespace: {drNs}");
                rules.AppendLine($"          port: {dr.ServicePort}");
            }
            else
            {
                rules.AppendLine($"    - backendRefs:");
                rules.AppendLine($"        - name: {dr.ServiceName}");
                rules.AppendLine($"          namespace: {drNs}");
                rules.AppendLine($"          port: {dr.ServicePort}");
            }
        }

        return
            $"apiVersion: gateway.networking.k8s.io/v1\n" +
            $"kind: HTTPRoute\n" +
            $"metadata:\n" +
            $"  name: {routeName}\n" +
            $"  namespace: {ns}\n" +
            $"spec:\n" +
            $"  parentRefs:\n" +
            $"    - name: {primary.GatewayName}\n" +
            $"      namespace: {primary.GatewayNamespace}\n" +
            $"  hostnames:\n" +
            $"    - {route.Hostname}\n" +
            $"  rules:\n" +
            rules.ToString().TrimEnd();
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

    public static string GenerateHttpRouteYaml(AppDeploymentRoute dr)
        => GenerateHttpRouteYaml(dr.AppRoute, [dr]);

    public static string GenerateCertificateYaml(AppDeploymentRoute dr)
        => GenerateCertificateYaml(dr.AppRoute, [dr]);
}
