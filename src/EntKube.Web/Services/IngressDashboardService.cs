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
                List<IngressRouteView> clusterRoutes = await LoadClusterAsync(
                    cluster, envName, gatewayClass, managedHosts, ct);
                routes.AddRange(clusterRoutes);
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
        return new IngressDashboard(routes, probes, orphans);
    }

    /// <summary>
    /// Queries one cluster's routing objects and endpoints, then folds them into a unified
    /// route list. Each source-object query is independently guarded: a missing CRD (e.g.
    /// no Gateway API, no Traefik) just yields nothing for that kind.
    /// </summary>
    private async Task<List<IngressRouteView>> LoadClusterAsync(
        KubernetesCluster cluster, string envName, string gatewayClass,
        HashSet<string> managedHosts, CancellationToken ct)
    {
        string kc = cluster.Kubeconfig!;

        // Endpoints (core) → readiness per service, keyed "namespace/serviceName".
        Dictionary<string, (int Ready, int Total)> endpoints = await LoadEndpointsAsync(kc, ct);

        // cert-manager Certificates → TLS state per dnsName.
        Dictionary<string, CertInfo> certs = await LoadCertificatesAsync(kc, ct);

        // Gateway API Gateways → hostnames served over a TLS listener (exact + wildcard suffix),
        // so HTTPRoutes attached to those gateways read as TLS-terminated even without a tracked cert.
        (HashSet<string> tlsHostsExact, List<string> tlsWildcards) = await LoadGatewayTlsHostsAsync(kc, ct);
        bool GatewayTerminatesTls(string host)
        {
            string h = host.ToLowerInvariant();
            return tlsHostsExact.Contains(h)
                || tlsWildcards.Any(suffix => h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        List<IngressRouteView> views = [];

        void Add(IngressRouteKind kind, string ns, string name, List<string> hosts,
            List<string> paths, List<(string Svc, int? Port)> backends, bool tls)
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

            views.Add(new IngressRouteView(
                cluster.Id, cluster.Name, cluster.EnvironmentId, envName,
                kind, ns, name,
                hosts.Distinct().ToList(), paths.Distinct().ToList(),
                resolved, tls, gatewayClass, cert, managed));
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
            if (spec.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (rule.TryGetProperty("matches", out JsonElement matches) && matches.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement m in matches.EnumerateArray())
                            if (m.TryGetProperty("path", out JsonElement pth) && pth.TryGetProperty("value", out JsonElement pv)
                                && pv.GetString() is { } path)
                                paths.Add(path);
                    if (rule.TryGetProperty("backendRefs", out JsonElement brefs) && brefs.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement b in brefs.EnumerateArray())
                        {
                            string svcName = b.TryGetProperty("name", out JsonElement bn) ? bn.GetString() ?? "" : "";
                            int? port = b.TryGetProperty("port", out JsonElement bp) && bp.TryGetInt32(out int bpn) ? bpn : null;
                            if (svcName.Length > 0) backends.Add((svcName, port));
                        }
                }

            // An HTTPRoute is TLS-terminated at its parent Gateway listener — so it's HTTPS if its
            // host matches a Gateway TLS listener, or (fallback) a cert-manager Certificate exists.
            bool tls = hosts.Any(h => GatewayTerminatesTls(h) || certs.ContainsKey(h.ToLowerInvariant()));
            Add(IngressRouteKind.HttpRoute, ns, name, hosts, paths, backends, tls);
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
            if (spec.TryGetProperty("routes", out JsonElement rArr) && rArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement r in rArr.EnumerateArray())
                {
                    if (r.TryGetProperty("match", out JsonElement mEl) && mEl.GetString() is { } match)
                    {
                        foreach (Match hm in HostRuleRegex().Matches(match))
                            hosts.Add(hm.Groups[1].Value);
                        foreach (Match pm in PathRuleRegex().Matches(match))
                            paths.Add(pm.Groups[1].Value);
                    }
                    if (r.TryGetProperty("services", out JsonElement sArr) && sArr.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement s in sArr.EnumerateArray())
                        {
                            string svcName = s.TryGetProperty("name", out JsonElement sn) ? sn.GetString() ?? "" : "";
                            int? port = s.TryGetProperty("port", out JsonElement sp) && sp.TryGetInt32(out int spn) ? spn : null;
                            if (svcName.Length > 0) backends.Add((svcName, port));
                        }
                }
            Add(IngressRouteKind.TraefikIngressRoute, ns, name, hosts, paths, backends, tls);
        }

        return views;
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
    /// Loads Gateway API Gateways → the set of hostnames served over a TLS/HTTPS listener.
    /// Exact hostnames go in the returned set; wildcard listeners (<c>*.example.com</c>) become
    /// suffix strings (<c>.example.com</c>) matched by EndsWith. Used to decide whether an
    /// HTTPRoute is TLS-terminated at its parent Gateway.
    /// </summary>
    private async Task<(HashSet<string> Exact, List<string> Wildcards)> LoadGatewayTlsHostsAsync(string kc, CancellationToken ct)
    {
        HashSet<string> exact = [];
        List<string> wildcards = [];
        foreach (JsonElement item in Items(await SafeGetAsync("gateways.gateway.networking.k8s.io", kc, ct)))
        {
            if (!item.TryGetProperty("spec", out JsonElement spec)
                || !spec.TryGetProperty("listeners", out JsonElement listeners)
                || listeners.ValueKind != JsonValueKind.Array) continue;

            foreach (JsonElement l in listeners.EnumerateArray())
            {
                string protocol = l.TryGetProperty("protocol", out JsonElement pr) ? pr.GetString() ?? "" : "";
                bool tlsListener = protocol is "HTTPS" or "TLS" || l.TryGetProperty("tls", out _);
                if (!tlsListener) continue;

                string host = l.TryGetProperty("hostname", out JsonElement hn) ? hn.GetString() ?? "" : "";
                if (host.Length == 0) continue; // no hostname = matches all; skip (can't attribute)
                if (host.StartsWith("*."))
                    wildcards.Add(host[1..].ToLowerInvariant()); // "*.ex.com" → ".ex.com"
                else
                    exact.Add(host.ToLowerInvariant());
            }
        }
        return (exact, wildcards);
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

/// <summary>A single unified ingress route, spanning Ingress / HTTPRoute / Traefik CRD.</summary>
public record IngressRouteView(
    Guid ClusterId, string ClusterName, Guid EnvironmentId, string EnvironmentName,
    IngressRouteKind Kind, string Namespace, string Name,
    IReadOnlyList<string> Hostnames, IReadOnlyList<string> Paths,
    IReadOnlyList<IngressBackend> Backends,
    bool TlsEnabled, string GatewayClass, CertInfo? Cert, bool IsManaged)
{
    public string PrimaryHost => Hostnames.Count > 0 ? Hostnames[0] : "(no host)";

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
    IReadOnlyList<OrphanedRoute> Orphans)
{
    public int Total => Routes.Count;
    public int Secured => Routes.Count(r => r.TlsEnabled);
    public double TlsCoverage => Total == 0 ? 0 : (double)Secured / Total * 100;
    public int UnhealthyBackends => Routes.Count(r => r.BackendUnhealthy);
    public int Unmanaged => Routes.Count(r => !r.IsManaged);
    public int ExpiringCerts => Routes.Count(r => r.Cert?.ExpiringSoon == true || r.Cert?.Expired == true);
    public int ProblemCount => Routes.Count(r => r.HasProblem);
}
