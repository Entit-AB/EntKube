using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>What the operator asked to publish.</summary>
public record VmRouteRequest
{
    public VmRouteKind Kind { get; init; }
    public int VmPort { get; init; }
    public string? Hostname { get; init; }
    public string? ClusterIssuerName { get; init; }
    public int? RequestTimeoutSeconds { get; init; }
    public int? ExternalPort { get; init; }
}

/// <summary>
/// Published ports of KubeVirt virtual machines: the stored intent, its validation, and the objects it
/// renders. Applying and withdrawing live in <see cref="KubernetesOperationsService"/>, beside the app
/// routes, because they regenerate the same two Gateways.
///
/// <para><b>Reuses the app and component renderers rather than writing a third set.</b> An HTTP route
/// is projected into an <see cref="ExternalRoute"/> (only the Gateway's listener needs it: hostname, TLS
/// mode, issuer), and a TCP route into an <see cref="AppL4Route"/>. What a VM needs that neither has is
/// the Service in front of it, which is rendered here.</para>
/// </summary>
public class VmRouteService(
    IDbContextFactory<ConnectivityDbContext> dbFactory, EntKube.Contracts.Catalog.ICatalogApi catalog)
{
    /// <summary>The label every VM created by EntKube carries on its pod template, and the Service selects.</summary>
    public const string VmLabel = "kubevirt.io/vm";

    /// <summary>Ports the Istio sidecar and gateway use for themselves. Shared with AppL4RouteService.</summary>
    private static readonly HashSet<int> ReservedPorts = [15000, 15001, 15006, 15008, 15012, 15020, 15021, 15090, 15443];

    private static readonly Regex HostnamePattern = new(
        @"^(?=.{1,253}$)([a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled);

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<VmRoute>> GetForClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using ConnectivityDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.VmRoutes
            .Where(r => r.TenantId == tenantId && r.ClusterId == clusterId)
            .OrderBy(r => r.Namespace).ThenBy(r => r.VmName).ThenBy(r => r.Kind).ThenBy(r => r.VmPort)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // ── Changes to the stored intent ──────────────────────────────────────────

    /// <summary>
    /// Stores a new published port after validating it and checking that nothing else on the cluster
    /// already owns its hostname or external port. Does not touch the cluster — the caller applies it.
    /// </summary>
    public async Task<VmRoute> AddAsync(
        Guid tenantId, Guid clusterId, string ns, string vmName, VmRouteRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        // Component routes are keyed by component, so "on this cluster" means "on one of its components".
        List<Guid> clusterComponentIds =
            [.. (await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct)).Select(c => c.Id)];

        await using ConnectivityDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct))
            throw new InvalidOperationException("Cluster not found, or it is not this tenant's.");

        VmRoute route = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = clusterId,
            Namespace = ns,
            VmName = vmName,
            Kind = request.Kind,
            VmPort = request.VmPort,
        };

        if (request.Kind == VmRouteKind.Http)
        {
            string hostname = request.Hostname!.Trim().ToLowerInvariant();
            await EnsureHostnameFreeAsync(db, clusterId, hostname, clusterComponentIds, ct);

            route.Hostname = hostname;
            route.ClusterIssuerName = request.ClusterIssuerName!.Trim();
            route.RequestTimeoutSeconds = request.RequestTimeoutSeconds;
        }
        else
        {
            await EnsurePortFreeAsync(db, clusterId, request.ExternalPort!.Value, ct);
            route.ExternalPort = request.ExternalPort;
        }

        db.VmRoutes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    public static void Validate(VmRouteRequest r)
    {
        if (r.VmPort is < 1 or > 65535)
            throw new ArgumentException("The VM port must be between 1 and 65535.");

        if (r.Kind == VmRouteKind.Http)
        {
            if (string.IsNullOrWhiteSpace(r.Hostname) || !HostnamePattern.IsMatch(r.Hostname.Trim().ToLowerInvariant()))
                throw new ArgumentException("Enter a hostname such as vm.example.com.");
            if (string.IsNullOrWhiteSpace(r.ClusterIssuerName))
                throw new ArgumentException(
                    "Choose a ClusterIssuer: the gateway terminates TLS for the hostname and needs a certificate for it.");
            if (r.RequestTimeoutSeconds is < 0 or > 3600)
                throw new ArgumentException("The request timeout must be between 0 (none) and 3600 seconds.");
        }
        else
        {
            if (r.ExternalPort is not int port || port is < 1 or > 65535)
                throw new ArgumentException("The external port must be between 1 and 65535.");
            if (ReservedPorts.Contains(port))
                throw new ArgumentException($"Port {port} is used by the Istio gateway itself.");
        }
    }

    /// <summary>
    /// A hostname is one HTTPRoute on the cluster, whoever renders it: every route generator names the
    /// object after the hostname. So it is refused when a component route, an app route or another VM
    /// already serves it here.
    /// </summary>
    private static async Task EnsureHostnameFreeAsync(
        ConnectivityDbContext db, Guid clusterId, string hostname, IReadOnlyCollection<Guid> clusterComponentIds,
        CancellationToken ct)
    {
        bool taken =
            await db.VmRoutes.AnyAsync(r => r.ClusterId == clusterId && r.Hostname == hostname, ct)
            || await db.ExternalRoutes.AnyAsync(r => clusterComponentIds.Contains(r.ComponentId) && r.Hostname == hostname, ct)
            || await db.AppRoutes.AnyAsync(r => r.IsEnabled && r.Hostname.ToLower() == hostname
                && r.DeploymentRoutes.Any(dr => dr.IsEnabled && dr.AppDeployment.ClusterId == clusterId), ct);

        if (taken)
            throw new InvalidOperationException($"Hostname '{hostname}' is already in use on this cluster.");
    }

    /// <summary>An L4 listener is one LoadBalancer port; an app's TCP route and a VM's cannot share it.</summary>
    private static async Task EnsurePortFreeAsync(ConnectivityDbContext db, Guid clusterId, int port, CancellationToken ct)
    {
        bool taken =
            await db.VmRoutes.AnyAsync(r => r.ClusterId == clusterId && r.Kind == VmRouteKind.Tcp && r.ExternalPort == port, ct)
            || await db.AppL4Routes.AnyAsync(r => r.AppDeployment.ClusterId == clusterId
                && r.Protocol == L4Protocol.Tcp && r.ExternalPort == port, ct);

        if (taken)
            throw new InvalidOperationException($"TCP port {port} is already used by another route on this cluster.");
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The Service in front of a VM for one kind of route: <c>{vm}-web</c> for HTTP, <c>{vm}-tcp</c> for TCP.
    /// Two Services rather than one because each gets its own Istio DestinationRule, named after the
    /// Service — HTTP's carries the connection pool and TLS settings, TCP's only disables mTLS, and one
    /// Service would have the two overwrite each other.
    /// </summary>
    public static string ServiceName(string vmName, VmRouteKind kind) =>
        $"{vmName}-{(kind == VmRouteKind.Http ? "web" : "tcp")}";

    /// <summary>
    /// The Service for every route of one kind on one VM. Port names follow Istio's convention
    /// (<c>http-80</c>, <c>tcp-22</c>), which is how its gateway decides what protocol a port speaks.
    /// </summary>
    public static string BuildServiceYaml(string ns, string vmName, VmRouteKind kind, IEnumerable<int> vmPorts)
    {
        string prefix = kind == VmRouteKind.Http ? "http" : "tcp";
        string ports = string.Concat(vmPorts.Distinct().Order().Select(p =>
            $"    - name: {prefix}-{p}\n      port: {p}\n      targetPort: {p}\n      protocol: TCP\n"));

        return
            $"apiVersion: v1\n" +
            $"kind: Service\n" +
            $"metadata:\n" +
            $"  name: {ServiceName(vmName, kind)}\n" +
            $"  namespace: {ns}\n" +
            $"  labels:\n" +
            $"    app.kubernetes.io/managed-by: entkube\n" +
            $"    {VmLabel}: {vmName}\n" +
            $"spec:\n" +
            $"  selector:\n" +
            $"    {VmLabel}: {vmName}\n" +
            $"  ports:\n" +
            ports;
    }

    /// <summary>What the HTTPS Gateway needs to know about an HTTP route: a listener and its certificate.</summary>
    public static ExternalRoute ToGatewayRoute(VmRoute r, string gatewayName, string gatewayNamespace) => new()
    {
        Id = r.Id,
        Hostname = r.Hostname!,
        ServiceName = ServiceName(r.VmName, VmRouteKind.Http),
        ServicePort = r.VmPort,
        RequestTimeoutSeconds = r.RequestTimeoutSeconds,
        TlsMode = TlsMode.ClusterIssuer,
        ClusterIssuerName = r.ClusterIssuerName,
        GatewayName = gatewayName,
        GatewayNamespace = gatewayNamespace,
    };

    /// <summary>What the L4 Gateway needs to know about a TCP route: a listener on its port.</summary>
    public static AppL4Route ToL4Route(VmRoute r, string gatewayNamespace) => new()
    {
        Id = r.Id,
        Protocol = L4Protocol.Tcp,
        ExternalPort = r.ExternalPort!.Value,
        ServiceName = ServiceName(r.VmName, VmRouteKind.Tcp),
        ServicePort = r.VmPort,
        GatewayName = ExternalRouteService.L4GatewayName,
        GatewayNamespace = gatewayNamespace,
        IsEnabled = true,
        IsManaged = true,
    };

    /// <summary>The namespace an HTTPRoute renders into, in the shape the component renderer takes.</summary>
    public sealed record RouteNamespace(string? Namespace) : EntKube.Contracts.Catalog.IComponentNaming
    {
        public string Name => "";
        public string? HelmChartName => null;
        public string? ReleaseName => null;
    }
}
