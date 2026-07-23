using System.Text.Json;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Aggregates a tenant's north-south ingress picture across every registered cluster —
/// the "Traefik dashboard, but better". Where the Traefik dashboard shows one cluster's
/// static router config, this pulls the live routing objects from ALL of a tenant's
/// clusters (Gateway API HTTPRoutes/Gateways, Traefik IngressRoute CRDs, and core
/// Ingresses), then enriches each route with:
///   • backend health  — ready vs total endpoints behind the route's service,
///   • TLS + cert state — cert-manager Certificate expiry / readiness,
///   • drift/security   — managed-by-EntKube vs unmanaged, no-TLS, dead backend,
///   • env/cluster attribution so one table spans the whole tenant.
///
/// Everything is fetched read-only and best-effort: a cluster that is unreachable or
/// lacks a given CRD is simply skipped (recorded in <see cref="IngressDashboard.Clusters"/>)
/// rather than failing the whole load. Per-route traffic metrics are loaded lazily via
/// <see cref="GetRouteTrafficAsync"/> so we don't hammer Prometheus for every row up front.
/// </summary>
public partial class IngressDashboardService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    PrometheusService prometheus,
    ILogger<IngressDashboardService> logger)
{
    private static readonly JsonDocumentOptions JsonOpts = new() { AllowTrailingCommas = true };

    /// <summary>
    /// Loads the full ingress picture for a tenant: one pass per registered cluster,
    /// each producing a set of <see cref="IngressRouteView"/>s plus a probe record so the
    /// UI can show which clusters answered and which were skipped.
    /// </summary>
    public async Task<IngressDashboard> LoadAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Only clusters whose kubeconfig is actually stored can be queried live.
        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Include(c => c.Components)
            .Where(c => c.TenantId == tenantId && c.KubeconfigSecretId != null)
            .ToListAsync(ct);

        Dictionary<Guid, string> envNames = await db.Environments
            .Where(e => e.TenantId == tenantId)
            .ToDictionaryAsync(e => e.Id, e => e.Name, ct);

        // Stored routes EntKube itself created — used to flag unmanaged/orphaned drift.
        List<ExternalRoute> stored = await db.ExternalRoutes
            .Include(r => r.Component).ThenInclude(c => c.Cluster)
            .Where(r => r.Component.Cluster.TenantId == tenantId)
            .ToListAsync(ct);
        HashSet<string> managedHosts = stored
            .Select(r => r.Hostname.Trim().ToLowerInvariant())
            .Where(h => h.Length > 0)
            .ToHashSet();

        List<IngressRouteView> routes = [];
        List<MiddlewareInfo> middlewares = [];
        List<GatewayListenerInfo> listeners = [];
        List<ClusterProbe> probes = [];

        foreach (KubernetesCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                probes.Add(new ClusterProbe(cluster.Id, cluster.Name, false, "?", "No kubeconfig materialized", 0));
                continue;
            }

            string gatewayClass = ExternalRouteService.ResolveGatewayClass(cluster.Components);
            string envName = envNames.GetValueOrDefault(cluster.EnvironmentId, "—");

            try
            {
                (List<IngressRouteView> clusterRoutes, List<MiddlewareInfo> clusterMiddlewares,
                    List<GatewayListenerInfo> clusterListeners) =
                    await LoadClusterAsync(cluster, envName, gatewayClass, managedHosts, ct);
                routes.AddRange(clusterRoutes);
                middlewares.AddRange(clusterMiddlewares);
                listeners.AddRange(clusterListeners);
                probes.Add(new ClusterProbe(cluster.Id, cluster.Name, true, gatewayClass, null, clusterRoutes.Count));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ingress load failed for cluster {Cluster}", cluster.Name);
                probes.Add(new ClusterProbe(cluster.Id, cluster.Name, false, gatewayClass, ex.Message, 0));
            }
        }

        // Orphaned config: a route EntKube stored whose hostname no longer appears live.
        HashSet<string> liveHosts = routes
            .SelectMany(r => r.Hostnames)
            .Select(h => h.ToLowerInvariant())
            .ToHashSet();
        List<OrphanedRoute> orphans = stored
            .Where(r => !string.IsNullOrWhiteSpace(r.Hostname)
                && !liveHosts.Contains(r.Hostname.Trim().ToLowerInvariant()))
            .Select(r => new OrphanedRoute(r.Hostname, r.ServiceName ?? "—",
                r.Component?.Cluster?.Name ?? "—"))
            .DistinctBy(o => o.Hostname)
            .ToList();

        routes = [.. routes.OrderByDescending(r => r.Severity).ThenBy(r => r.PrimaryHost)];
        return new IngressDashboard(routes, probes, orphans, middlewares, listeners);
    }

    /// <summary>
    /// Queries one cluster's routing objects and endpoints, then folds them into a unified
    /// route list. Each source-object query is independently guarded: a missing CRD (e.g.
    /// no Gateway API, no Traefik) just yields nothing for that kind.
    /// </summary>
    private async Task<(List<IngressRouteView> Routes, List<MiddlewareInfo> Middlewares, List<GatewayListenerInfo> Listeners)> LoadClusterAsync(
        KubernetesCluster cluster, string envName, string gatewayClass,
        HashSet<string> managedHosts, CancellationToken ct)
    {
        string kc = cluster.Kubeconfig!;

        // Endpoints (core) → readiness per service, keyed "namespace/serviceName".
        Dictionary<string, (int Ready, int Total)> endpoints = await LoadEndpointsAsync(kc, ct);

        // cert-manager Certificates → TLS state per dnsName.
        Dictionary<string, CertInfo> certs = await LoadCertificatesAsync(kc, ct);

        // Provider-native "middleware/filter" catalog: Traefik Middleware CRDs on a Traefik
        // cluster, Gateway API HTTPRoute filters (added below) on a Gateway API cluster.
        List<MiddlewareInfo> middlewareCatalog = string.Equals(gatewayClass, "traefik", StringComparison.OrdinalIgnoreCase)
            ? await LoadMiddlewaresAsync(cluster.Id, kc, ct)
            : [];

        // Gateway API Gateways → the real listeners (name/port/protocol/TLS/hostname). These are
        // the provider-agnostic analogue of Traefik "entrypoints", and let HTTPRoutes resolve
        // which listener (and port) they attach to via parentRefs, plus whether TLS terminates there.
        List<GatewayListenerInfo> listeners = await LoadGatewayListenersAsync(cluster.Id, kc, ct);

        // TLS host sets derived from the listeners (exact hostnames + wildcard suffixes).
        HashSet<string> tlsHostsExact = [];
        List<string> tlsWildcards = [];
        foreach (GatewayListenerInfo l in listeners.Where(l => l.Tls && l.Hostname.Length > 0))
        {
            if (l.Hostname.StartsWith("*."))
                tlsWildcards.Add(l.Hostname[1..].ToLowerInvariant());
            else
                tlsHostsExact.Add(l.Hostname.ToLowerInvariant());
        }
        bool GatewayTerminatesTls(string host)
        {
            string h = host.ToLowerInvariant();
            return tlsHostsExact.Contains(h)
                || tlsWildcards.Any(suffix => h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        // Gateway (ns/name) → its listeners, for HTTPRoute parentRef → entrypoint/TLS resolution.
        Dictionary<string, List<GatewayListenerInfo>> listenersByGateway = listeners
            .GroupBy(l => $"{l.Namespace}/{l.GatewayName}")
            .ToDictionary(g => g.Key, g => g.ToList());

        List<IngressRouteView> views = [];

        void Add(IngressRouteKind kind, string ns, string name, List<string> hosts,
            List<string> paths, List<(string Svc, int? Port)> backends, bool tls,
            string? rawRule = null, List<string>? entryPoints = null,
            List<MiddlewareRef>? middlewares = null, int priority = 0)
        {
            List<IngressBackend> resolved = backends.Select(b =>
            {
                (int ready, int total) = endpoints.GetValueOrDefault($"{ns}/{b.Svc}", (0, -1));
                return new IngressBackend(ns, b.Svc, b.Port, ready, total);
            }).ToList();

            CertInfo? cert = hosts
                .Select(h => certs.GetValueOrDefault(h.ToLowerInvariant()))
                .FirstOrDefault(c => c is not null);

            bool managed = hosts.Any(h => managedHosts.Contains(h.ToLowerInvariant()));

            // Traefik-style rule: preserve the CRD's own match, else synthesize from host+path.
            string rule = !string.IsNullOrWhiteSpace(rawRule) ? rawRule! : BuildRule(hosts, paths);

            // Entrypoint names: Traefik carries them explicitly; for Ingress/HTTPRoute derive
            // web/websecure from whether the route is TLS-terminated (Traefik's default names).
            List<string> eps = entryPoints is { Count: > 0 }
                ? entryPoints.Distinct().ToList()
                : [tls ? "websecure" : "web"];

            views.Add(new IngressRouteView(
                cluster.Id, cluster.Name, cluster.EnvironmentId, envName,
                kind, ns, name,
                hosts.Distinct().ToList(), paths.Distinct().ToList(),
                resolved, tls, gatewayClass, cert, managed,
                rule, eps, middlewares ?? [], priority));
        }

        // ── Core Ingress (networking.k8s.io) ──
        foreach (JsonElement item in Items(await SafeGetAsync("ingresses.networking.k8s.io", kc, ct)))
        {
            (string ns, string name) = Meta(item);
            if (!item.TryGetProperty("spec", out JsonElement spec)) continue;

            List<string> tlsHosts = [];
            if (spec.TryGetProperty("tls", out JsonElement tlsArr) && tlsArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement t in tlsArr.EnumerateArray())
                    if (t.TryGetProperty("hosts", out JsonElement th) && th.ValueKind == JsonValueKind.Array)
                        tlsHosts.AddRange(th.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));

            List<string> hosts = [];
            List<string> paths = [];
            List<(string, int?)> backends = [];
            if (spec.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (rule.TryGetProperty("host", out JsonElement h) && h.GetString() is { Length: > 0 } host)
                        hosts.Add(host);
                    if (rule.TryGetProperty("http", out JsonElement http)
                        && http.TryGetProperty("paths", out JsonElement pArr) && pArr.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement p in pArr.EnumerateArray())
                        {
                            if (p.TryGetProperty("path", out JsonElement pv) && pv.GetString() is { } path)
                                paths.Add(path);
                            if (p.TryGetProperty("backend", out JsonElement be)
                                && be.TryGetProperty("service", out JsonElement svc))
                            {
                                string svcName = svc.TryGetProperty("name", out JsonElement sn) ? sn.GetString() ?? "" : "";
                                int? port = svc.TryGetProperty("port", out JsonElement po)
                                    && po.TryGetProperty("number", out JsonElement pn) && pn.TryGetInt32(out int pnum)
                                    ? pnum : null;
                                if (svcName.Length > 0) backends.Add((svcName, port));
                            }
                        }
                }

            if (hosts.Count == 0) hosts.AddRange(tlsHosts);
            Add(IngressRouteKind.Ingress, ns, name, hosts, paths, backends, tlsHosts.Count > 0);
        }

        // ── Gateway API HTTPRoute ──
        foreach (JsonElement item in Items(await SafeGetAsync("httproutes.gateway.networking.k8s.io", kc, ct)))
        {
            (string ns, string name) = Meta(item);
            if (!item.TryGetProperty("spec", out JsonElement spec)) continue;

            List<string> hosts = spec.TryGetProperty("hostnames", out JsonElement hn) && hn.ValueKind == JsonValueKind.Array
                ? hn.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [];
            List<string> paths = [];
            List<(string, int?)> backends = [];
            List<MiddlewareRef> filters = [];
            if (spec.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (rule.TryGetProperty("matches", out JsonElement matches) && matches.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement m in matches.EnumerateArray())
                            if (m.TryGetProperty("path", out JsonElement pth) && pth.TryGetProperty("value", out JsonElement pv)
                                && pv.GetString() is { } path)
                                paths.Add(path);
                    // Gateway API filters are the provider-agnostic analogue of Traefik middlewares.
                    CollectHttpRouteFilters(rule, ns, filters);
                    if (rule.TryGetProperty("backendRefs", out JsonElement brefs) && brefs.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement b in brefs.EnumerateArray())
                        {
                            string svcName = b.TryGetProperty("name", out JsonElement bn) ? bn.GetString() ?? "" : "";
                            int? port = b.TryGetProperty("port", out JsonElement bp) && bp.TryGetInt32(out int bpn) ? bpn : null;
                            if (svcName.Length > 0) backends.Add((svcName, port));
                            CollectHttpRouteFilters(b, ns, filters);
                        }
                }
            filters = filters.DistinctBy(f => f.Name).ToList();

            // Resolve parentRefs → the actual Gateway listeners this route attaches to. Those give
            // the real entrypoint names/ports, and whether TLS terminates at that listener.
            List<GatewayListenerInfo> attached = ResolveParents(spec, ns, listenersByGateway);
            // Chip per attached Gateway (not per listener) — a route with no sectionName binds every
            // listener of a gateway, which on a per-hostname mesh could be dozens.
            List<string> entryPoints = attached
                .Select(l => l.GatewayName)
                .Where(n => n.Length > 0)
                .Distinct()
                .ToList();

            // TLS if the attached listener terminates TLS, else host matches a TLS listener / has a cert.
            bool tls = attached.Any(l => l.Tls)
                || hosts.Any(h => GatewayTerminatesTls(h) || certs.ContainsKey(h.ToLowerInvariant()));

            // Surface each distinct filter as a catalog row (provider-native "middleware/filter").
            foreach (MiddlewareRef f in filters)
                middlewareCatalog.Add(new MiddlewareInfo(cluster.Id, $"{name}", ns, f.Name, gatewayClass));

            Add(IngressRouteKind.HttpRoute, ns, name, hosts, paths, backends, tls,
                entryPoints: entryPoints, middlewares: filters);
        }

        // ── Traefik IngressRoute CRD (traefik.io, with legacy containo.us fallback) ──
        string traefikJson = await SafeGetAsync("ingressroutes.traefik.io", kc, ct);
        if (Items(traefikJson).Count == 0)
            traefikJson = await SafeGetAsync("ingressroutes.traefik.containo.us", kc, ct);
        foreach (JsonElement item in Items(traefikJson))
        {
            (string ns, string name) = Meta(item);
            if (!item.TryGetProperty("spec", out JsonElement spec)) continue;

            bool tls = spec.TryGetProperty("tls", out _);
            List<string> hosts = [];
            List<string> paths = [];
            List<(string, int?)> backends = [];
            List<string> matches = [];
            List<MiddlewareRef> mws = [];
            int priority = 0;

            // Entrypoints are declared once at the CRD's spec level.
            List<string> entryPoints = [];
            if (spec.TryGetProperty("entryPoints", out JsonElement epArr) && epArr.ValueKind == JsonValueKind.Array)
                entryPoints.AddRange(epArr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));

            if (spec.TryGetProperty("routes", out JsonElement rArr) && rArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement r in rArr.EnumerateArray())
                {
                    if (r.TryGetProperty("match", out JsonElement mEl) && mEl.GetString() is { Length: > 0 } match)
                    {
                        matches.Add(match);
                        foreach (Match hm in HostRuleRegex().Matches(match))
                            hosts.Add(hm.Groups[1].Value);
                        foreach (Match pm in PathRuleRegex().Matches(match))
                            paths.Add(pm.Groups[1].Value);
                    }
                    if (r.TryGetProperty("priority", out JsonElement pr) && pr.TryGetInt32(out int prio))
                        priority = Math.Max(priority, prio);
                    if (r.TryGetProperty("middlewares", out JsonElement mwArr) && mwArr.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement m in mwArr.EnumerateArray())
                        {
                            string mwName = m.TryGetProperty("name", out JsonElement mn) ? mn.GetString() ?? "" : "";
                            string mwNs = m.TryGetProperty("namespace", out JsonElement mns) ? mns.GetString() ?? ns : ns;
                            if (mwName.Length > 0) mws.Add(new MiddlewareRef(mwName, mwNs));
                        }
                    if (r.TryGetProperty("services", out JsonElement sArr) && sArr.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement s in sArr.EnumerateArray())
                        {
                            string svcName = s.TryGetProperty("name", out JsonElement sn) ? sn.GetString() ?? "" : "";
                            int? port = s.TryGetProperty("port", out JsonElement sp) && sp.TryGetInt32(out int spn) ? spn : null;
                            if (svcName.Length > 0) backends.Add((svcName, port));
                        }
                }

            // Multiple route entries under one CRD → OR them into a single rule string.
            string? rawRule = matches.Count > 0
                ? string.Join(" || ", matches.Select(m => matches.Count > 1 ? $"({m})" : m))
                : null;
            Add(IngressRouteKind.TraefikIngressRoute, ns, name, hosts, paths, backends, tls,
                rawRule, entryPoints, mws.DistinctBy(m => $"{m.Namespace}/{m.Name}").ToList(), priority);
        }

        return (views, middlewareCatalog.DistinctBy(m => $"{m.Namespace}/{m.Name}/{m.Type}").ToList(), listeners);
    }

    /// <summary>
    /// Loads core Endpoints across all namespaces → (ready, total) address counts per
    /// "namespace/serviceName". An Endpoints object shares its service's name, which is how
    /// we map a route's backend service to live endpoint readiness.
    /// </summary>
    private async Task<Dictionary<string, (int Ready, int Total)>> LoadEndpointsAsync(string kc, CancellationToken ct)
    {
        Dictionary<string, (int, int)> map = [];
        foreach (JsonElement item in Items(await SafeGetAsync("endpoints", kc, ct)))
        {
            (string ns, string name) = Meta(item);
            int ready = 0, notReady = 0;
            if (item.TryGetProperty("subsets", out JsonElement subsets) && subsets.ValueKind == JsonValueKind.Array)
                foreach (JsonElement ss in subsets.EnumerateArray())
                {
                    if (ss.TryGetProperty("addresses", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
                        ready += a.GetArrayLength();
                    if (ss.TryGetProperty("notReadyAddresses", out JsonElement nr) && nr.ValueKind == JsonValueKind.Array)
                        notReady += nr.GetArrayLength();
                }
            map[$"{ns}/{name}"] = (ready, ready + notReady);
        }
        return map;
    }

    /// <summary>
    /// Loads Traefik Middleware CRDs (traefik.io, legacy containo.us fallback) into a flat
    /// catalog, deriving each middleware's <em>type</em> from the first key under its spec
    /// (e.g. <c>stripPrefix</c>, <c>headers</c>, <c>basicAuth</c>) — exactly what Traefik's own
    /// dashboard shows. Absent Traefik just yields an empty list.
    /// </summary>
    private async Task<List<MiddlewareInfo>> LoadMiddlewaresAsync(Guid clusterId, string kc, CancellationToken ct)
    {
        List<MiddlewareInfo> list = [];
        string json = await SafeGetAsync("middlewares.traefik.io", kc, ct);
        if (Items(json).Count == 0)
            json = await SafeGetAsync("middlewares.traefik.containo.us", kc, ct);

        foreach (JsonElement item in Items(json))
        {
            (string ns, string name) = Meta(item);
            string type = "middleware";
            if (item.TryGetProperty("spec", out JsonElement spec) && spec.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty prop in spec.EnumerateObject()) { type = prop.Name; break; }
            list.Add(new MiddlewareInfo(clusterId, name, ns, type, "traefik"));
        }
        return list;
    }

    /// <summary>
    /// Loads Gateway API Gateways → a flat list of their listeners (name/port/protocol/TLS/hostname).
    /// These are the provider-agnostic analogue of Traefik entrypoints and drive the "Gateways"
    /// overview card plus HTTPRoute parentRef resolution. Absent Gateway API yields an empty list.
    /// </summary>
    private async Task<List<GatewayListenerInfo>> LoadGatewayListenersAsync(Guid clusterId, string kc, CancellationToken ct)
    {
        List<GatewayListenerInfo> list = [];
        foreach (JsonElement item in Items(await SafeGetAsync("gateways.gateway.networking.k8s.io", kc, ct)))
        {
            (string ns, string name) = Meta(item);
            if (!item.TryGetProperty("spec", out JsonElement spec)
                || !spec.TryGetProperty("listeners", out JsonElement listeners)
                || listeners.ValueKind != JsonValueKind.Array) continue;

            foreach (JsonElement l in listeners.EnumerateArray())
            {
                string lname = l.TryGetProperty("name", out JsonElement ln) ? ln.GetString() ?? "" : "";
                string protocol = l.TryGetProperty("protocol", out JsonElement pr) ? pr.GetString() ?? "" : "";
                int port = l.TryGetProperty("port", out JsonElement po) && po.TryGetInt32(out int pn) ? pn : 0;
                string hostname = l.TryGetProperty("hostname", out JsonElement hn) ? hn.GetString() ?? "" : "";
                bool tls = protocol is "HTTPS" or "TLS" || l.TryGetProperty("tls", out _);
                list.Add(new GatewayListenerInfo(clusterId, ns, name, lname, port, protocol, tls, hostname));
            }
        }
        return list;
    }

    /// <summary>
    /// Resolves an HTTPRoute's <c>spec.parentRefs</c> to the concrete Gateway listeners it binds to,
    /// using the cluster's loaded Gateways. A ref with no <c>sectionName</c> matches all of the
    /// gateway's listeners; a <c>sectionName</c> pins one. Refs to gateways we couldn't load
    /// (e.g. another namespace) resolve to nothing and the caller falls back to derived entrypoints.
    /// </summary>
    private static List<GatewayListenerInfo> ResolveParents(
        JsonElement spec, string routeNs, Dictionary<string, List<GatewayListenerInfo>> byGateway)
    {
        List<GatewayListenerInfo> matched = [];
        if (!spec.TryGetProperty("parentRefs", out JsonElement prefs) || prefs.ValueKind != JsonValueKind.Array)
            return matched;

        foreach (JsonElement p in prefs.EnumerateArray())
        {
            string gwName = p.TryGetProperty("name", out JsonElement gn) ? gn.GetString() ?? "" : "";
            if (gwName.Length == 0) continue;
            string gwNs = p.TryGetProperty("namespace", out JsonElement gns) ? gns.GetString() ?? routeNs : routeNs;
            string section = p.TryGetProperty("sectionName", out JsonElement sn) ? sn.GetString() ?? "" : "";

            if (!byGateway.TryGetValue($"{gwNs}/{gwName}", out List<GatewayListenerInfo>? ls)) continue;
            matched.AddRange(section.Length > 0 ? ls.Where(l => l.ListenerName == section) : ls);
        }
        return matched;
    }

    /// <summary>
    /// Collects Gateway API filter <em>types</em> (RequestHeaderModifier, RequestRedirect, URLRewrite,
    /// RequestMirror, ExtensionRef…) from an HTTPRoute rule or backendRef into a middleware-ref list.
    /// </summary>
    private static void CollectHttpRouteFilters(JsonElement node, string ns, List<MiddlewareRef> into)
    {
        if (!node.TryGetProperty("filters", out JsonElement filters) || filters.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement f in filters.EnumerateArray())
            if (f.TryGetProperty("type", out JsonElement t) && t.GetString() is { Length: > 0 } type)
                into.Add(new MiddlewareRef(type, ns));
    }

    /// <summary>
    /// Synthesizes a Traefik-style routing rule from a route's hosts and paths — used for core
    /// Ingress and Gateway API HTTPRoutes, which don't carry a Traefik match expression natively.
    /// </summary>
    private static string BuildRule(List<string> hosts, List<string> paths)
    {
        List<string> parts = [];
        List<string> h = hosts.Distinct().Where(x => x.Length > 0).ToList();
        if (h.Count > 0)
            parts.Add($"Host({string.Join(", ", h.Select(x => $"`{x}`"))})");
        List<string> p = paths.Distinct().Where(x => x.Length > 0 && x != "/").ToList();
        if (p.Count > 0)
            parts.Add($"PathPrefix({string.Join(", ", p.Select(x => $"`{x}`"))})");
        return parts.Count > 0 ? string.Join(" && ", parts) : "PathPrefix(`/`)";
    }

    /// <summary>
    /// Loads cert-manager Certificates → a <see cref="CertInfo"/> per dnsName (lower-cased),
    /// carrying expiry (status.notAfter) and readiness (Ready condition). Absent cert-manager
    /// just yields an empty map (routes then show as untracked TLS).
    /// </summary>
    private async Task<Dictionary<string, CertInfo>> LoadCertificatesAsync(string kc, CancellationToken ct)
    {
        Dictionary<string, CertInfo> map = [];
        foreach (JsonElement item in Items(await SafeGetAsync("certificates.cert-manager.io", kc, ct)))
        {
            (string ns, string name) = Meta(item);
            DateTime? notAfter = null;
            bool ready = false;
            if (item.TryGetProperty("status", out JsonElement status))
            {
                if (status.TryGetProperty("notAfter", out JsonElement na) && na.TryGetDateTime(out DateTime dt))
                    notAfter = dt.ToUniversalTime();
                if (status.TryGetProperty("conditions", out JsonElement conds) && conds.ValueKind == JsonValueKind.Array)
                    ready = conds.EnumerateArray().Any(c =>
                        c.TryGetProperty("type", out JsonElement t) && t.GetString() == "Ready"
                        && c.TryGetProperty("status", out JsonElement s) && s.GetString() == "True");
            }
            CertInfo info = new(name, ns, notAfter, ready);
            if (item.TryGetProperty("spec", out JsonElement spec)
                && spec.TryGetProperty("dnsNames", out JsonElement dns) && dns.ValueKind == JsonValueKind.Array)
                foreach (JsonElement d in dns.EnumerateArray())
                    if (d.GetString() is { Length: > 0 } host)
                        map[host.ToLowerInvariant()] = info;
        }
        return map;
    }

    /// <summary>
    /// Best-effort per-route request-rate time series, chosen by the cluster's gateway class:
    /// Traefik → <c>traefik_service_requests_total</c>, Istio → <c>istio_requests_total</c>.
    /// Returns an empty list when Prometheus isn't installed or no series match, so the UI can
    /// render a graceful "no traffic data" state rather than erroring.
    /// </summary>
    public async Task<List<TimeSeriesDataPoint>> GetRouteTrafficAsync(
        IngressRouteView route, TimeSpan window, CancellationToken ct = default)
    {
        IngressBackend? backend = route.Backends.FirstOrDefault();
        if (backend is null) return [];

        int rate = Math.Max(60, (int)(window.TotalSeconds / 60));
        string q = route.GatewayClass == "traefik"
            // Traefik labels its service as "{ns}-{svc}-{port}@kubernetes[crd]"; match the prefix.
            ? $"sum(rate(traefik_service_requests_total{{service=~\"{Escape(backend.Namespace)}-{Escape(backend.ServiceName)}-.*\"}}[{rate}s]))"
            : $"sum(rate(istio_requests_total{{destination_service_namespace=\"{Escape(backend.Namespace)}\",destination_service_name=\"{Escape(backend.ServiceName)}\"}}[{rate}s]))";

        try
        {
            KubernetesOperationResult<List<PrometheusTimeSeries>> res =
                await prometheus.GetMetricRangeAsync(route.ClusterId, q, window, ct);
            if (!res.IsSuccess || res.Data is null || res.Data.Count == 0) return [];

            // Collapse every returned series into one line by summing per timestamp.
            return res.Data
                .SelectMany(s => s.DataPoints)
                .GroupBy(p => p.Timestamp)
                .OrderBy(g => g.Key)
                .Select(g => new TimeSeriesDataPoint { Timestamp = g.Key, Value = g.Sum(p => p.Value) })
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Route traffic query failed for {Host}", route.PrimaryHost);
            return [];
        }
    }

    // ── JSON helpers ──

    private async Task<string> SafeGetAsync(string resource, string kc, CancellationToken ct)
    {
        try { return await k8s.GetJsonAllNamespacesAsync(resource, kc, ct: ct); }
        catch (Exception ex)
        {
            // CRD not installed / API group absent / transient — treat as "nothing of this kind".
            logger.LogDebug(ex, "GetJsonAllNamespaces {Resource} failed (skipped)", resource);
            return "";
        }
    }

    private static List<JsonElement> Items(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, JsonOpts);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                return [];
            // Clone so elements stay valid after the document is disposed.
            return items.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch { return []; }
    }

    private static (string Namespace, string Name) Meta(JsonElement item)
    {
        if (!item.TryGetProperty("metadata", out JsonElement meta)) return ("", "");
        string ns = meta.TryGetProperty("namespace", out JsonElement n) ? n.GetString() ?? "" : "";
        string name = meta.TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? "" : "";
        return (ns, name);
    }

    private static string Escape(string s) => Regex.Escape(s);

    [GeneratedRegex(@"Host(?:SNI)?\(`([^`]+)`\)")]
    private static partial Regex HostRuleRegex();

    [GeneratedRegex(@"Path(?:Prefix|Regexp)?\(`([^`]+)`\)")]
    private static partial Regex PathRuleRegex();
}

// ══════════════════════════════════════════════════════════════════════════
//  DTOs
// ══════════════════════════════════════════════════════════════════════════

public enum IngressRouteKind { Ingress, HttpRoute, TraefikIngressRoute }

public enum BackendState { NoBackend, Down, Degraded, Healthy }

/// <summary>A route's backing service with live endpoint readiness.</summary>
public record IngressBackend(string Namespace, string ServiceName, int? Port, int ReadyEndpoints, int TotalEndpoints)
{
    /// <summary>
    /// Total is -1 when no Endpoints object exists at all (service missing / never had endpoints).
    /// </summary>
    public BackendState State =>
        TotalEndpoints < 0 ? BackendState.NoBackend
        : ReadyEndpoints == 0 ? BackendState.Down
        : ReadyEndpoints < TotalEndpoints ? BackendState.Degraded
        : BackendState.Healthy;

    public string Display => Port is int p ? $"{ServiceName}:{p}" : ServiceName;
}

/// <summary>cert-manager Certificate state for a route's hostname.</summary>
public record CertInfo(string Name, string Namespace, DateTime? NotAfter, bool Ready)
{
    public int? DaysToExpiry => NotAfter is DateTime na ? (int)Math.Floor((na - DateTime.UtcNow).TotalDays) : null;
    public bool Expired => NotAfter is DateTime na && na < DateTime.UtcNow;
    public bool ExpiringSoon => DaysToExpiry is int d && d >= 0 && d <= 14;
}

/// <summary>
/// A request-processing step attached to a route — a Traefik Middleware or a Gateway API filter.
/// <see cref="Name"/> is the middleware/filter type shown in the router flow.
/// </summary>
public record MiddlewareRef(string Name, string Namespace);

/// <summary>
/// A provider-native request-processing element in a cluster: a Traefik Middleware CRD
/// (type = stripPrefix / headers…) or a Gateway API HTTPRoute filter
/// (type = RequestHeaderModifier / RequestRedirect / URLRewrite…). <see cref="Provider"/> is the
/// cluster's gateway class (traefik / istio / …).
/// </summary>
public record MiddlewareInfo(Guid ClusterId, string Name, string Namespace, string Type, string Provider)
{
    public string QualifiedName => $"{Namespace}-{Name}@{Provider}";
}

/// <summary>
/// A single Gateway API Gateway listener — the provider-agnostic analogue of a Traefik entrypoint.
/// </summary>
public record GatewayListenerInfo(
    Guid ClusterId, string Namespace, string GatewayName, string ListenerName,
    int Port, string Protocol, bool Tls, string Hostname)
{
    /// <summary>Concise entrypoint label for a route chip: the listener name, else the port.</summary>
    public string EntryPointLabel => ListenerName.Length > 0 ? ListenerName : Port > 0 ? Port.ToString() : GatewayName;
}

/// <summary>A single unified ingress route, spanning Ingress / HTTPRoute / Traefik CRD.</summary>
public record IngressRouteView(
    Guid ClusterId, string ClusterName, Guid EnvironmentId, string EnvironmentName,
    IngressRouteKind Kind, string Namespace, string Name,
    IReadOnlyList<string> Hostnames, IReadOnlyList<string> Paths,
    IReadOnlyList<IngressBackend> Backends,
    bool TlsEnabled, string GatewayClass, CertInfo? Cert, bool IsManaged,
    string Rule = "", IReadOnlyList<string>? EntryPoints = null,
    IReadOnlyList<MiddlewareRef>? Middlewares = null, int Priority = 0)
{
    public string PrimaryHost => Hostnames.Count > 0 ? Hostnames[0] : "(no host)";

    /// <summary>
    /// The controller/provider serving this route, derived from EntKube's config for the cluster
    /// (its gateway class — istio / traefik / …). Traefik CRDs are always Traefik regardless.
    /// Drives the provider column and the qualified name.
    /// </summary>
    public string Provider => Kind == IngressRouteKind.TraefikIngressRoute
        ? "traefik"
        : string.IsNullOrWhiteSpace(GatewayClass) ? "kubernetes" : GatewayClass;

    /// <summary>Qualified route name: <c>{namespace}-{name}@{provider}</c>.</summary>
    public string QualifiedName => $"{Namespace}-{Name}@{Provider}";

    public IReadOnlyList<string> EntryPointNames => EntryPoints ?? [];
    public IReadOnlyList<MiddlewareRef> MiddlewareRefs => Middlewares ?? [];

    public BackendState WorstBackend => Backends.Count == 0
        ? BackendState.NoBackend
        : Backends.Min(b => b.State);

    public bool BackendUnhealthy => WorstBackend is BackendState.Down or BackendState.NoBackend;

    /// <summary>Human-readable warnings driving the "problems only" filter and severity sort.</summary>
    public IReadOnlyList<string> Flags
    {
        get
        {
            List<string> f = [];
            if (!TlsEnabled) f.Add("No TLS");
            if (Cert?.Expired == true) f.Add("Cert expired");
            else if (Cert?.ExpiringSoon == true) f.Add($"Cert expires in {Cert.DaysToExpiry}d");
            if (WorstBackend == BackendState.NoBackend) f.Add("No backend");
            else if (WorstBackend == BackendState.Down) f.Add("Backend down");
            else if (WorstBackend == BackendState.Degraded) f.Add("Backend degraded");
            if (!IsManaged) f.Add("Unmanaged");
            return f;
        }
    }

    public bool HasProblem => Flags.Any(f => f != "Unmanaged");

    /// <summary>Higher = more urgent; drives the default ordering (worst first).</summary>
    public int Severity =>
        (Cert?.Expired == true ? 100 : 0)
        + (WorstBackend == BackendState.NoBackend ? 80 : WorstBackend == BackendState.Down ? 70 : WorstBackend == BackendState.Degraded ? 30 : 0)
        + (Cert?.ExpiringSoon == true ? 40 : 0)
        + (!TlsEnabled ? 20 : 0)
        + (!IsManaged ? 5 : 0);
}

/// <summary>Per-cluster fetch outcome, so the UI can show coverage/skips.</summary>
public record ClusterProbe(Guid ClusterId, string ClusterName, bool Reachable, string GatewayClass, string? Error, int RouteCount);

/// <summary>A stored EntKube route whose hostname no longer appears live (config drift).</summary>
public record OrphanedRoute(string Hostname, string ServiceName, string ClusterName);

/// <summary>The whole tenant's ingress picture.</summary>
public record IngressDashboard(
    IReadOnlyList<IngressRouteView> Routes,
    IReadOnlyList<ClusterProbe> Clusters,
    IReadOnlyList<OrphanedRoute> Orphans,
    IReadOnlyList<MiddlewareInfo> Middlewares,
    IReadOnlyList<GatewayListenerInfo> Listeners)
{
    public int Total => Routes.Count;
    public int Secured => Routes.Count(r => r.TlsEnabled);
    public double TlsCoverage => Total == 0 ? 0 : (double)Secured / Total * 100;
    public int UnhealthyBackends => Routes.Count(r => r.BackendUnhealthy);
    public int Unmanaged => Routes.Count(r => !r.IsManaged);
    public int ExpiringCerts => Routes.Count(r => r.Cert?.ExpiringSoon == true || r.Cert?.Expired == true);
    public int ProblemCount => Routes.Count(r => r.HasProblem);
}
