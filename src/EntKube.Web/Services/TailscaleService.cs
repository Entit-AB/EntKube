using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>
/// A Tailscale operator install, as the VPN tab sees it.
///
/// <para>It used to carry Catalog's <c>ClusterComponent</c>, and the page reached through it for
/// <c>Component.Cluster.Kubeconfig</c> six times — which is how a DTO handed to a Razor page became
/// this service's reason for holding a credential, and the reason its component read could not move
/// to <c>ICatalogApi</c>. Ids only now: the page passes them back and the seam resolves the
/// credential.</para>
/// </summary>
public record TailscaleOperator(Guid ComponentId, Guid ClusterId, string ClusterName, string? Namespace);

/// <summary>
/// A Kubernetes service and its current Tailscale exposure state.
/// Tailscale.com/* annotations on the service are set by the operator and by EntKube.
/// </summary>
public record TailscaleExposedService(
    string ServiceName,
    string Namespace,
    string ClusterIp,
    string? Ports,
    bool Exposed,
    string? RequestedHostname,   // tailscale.com/hostname — what we asked for
    string? TailnetIp,           // tailscale.com/tailnet-ip — set by operator once registered
    string? DnsName);            // tailscale.com/dns-name  — set by operator once registered

/// <summary>A Tailscale-backed Kubernetes Ingress (L7).</summary>
public record TailscaleIngress(
    string Name,
    string Namespace,
    string ServiceName,
    int ServicePort,
    string? TailscaleHostname,
    string? LoadBalancerHostname);  // ingress.status.loadBalancer.ingress[0].hostname

// ── Service ───────────────────────────────────────────────────────────────────

public class TailscaleService(
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Contracts.Catalog.ICatalogApi catalog)
{
    /// <summary>
    /// The cluster as this tenant, or a refusal. Every public method here goes through it, so a
    /// cluster id the tenant does not own reaches nothing — which is new: the credential used to
    /// arrive already resolved, from a DTO the page was holding.
    /// </summary>
    private async Task<EntKube.Web.Services.Clusters.IClusterClient> AccessAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct)
        => await clusterAccess.ForAsync(tenantId, clusterId, ct)
           ?? throw new InvalidOperationException(
               "That cluster is not this tenant's, or has no kubeconfig configured.");

    // ── Operator discovery ────────────────────────────────────────────────────

    public async Task<List<TailscaleOperator>> GetOperatorsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        // Asked of Catalog. The predicate is unchanged and deliberately asymmetric — the chart name
        // is upstream's, the component name is EntKube's own — which is why it stays in memory
        // rather than becoming a contract method.
        return
        [
            .. (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                .Where(c => c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed
                            && (c.HelmChartName == "tailscale-operator" || c.Name == "tailscale-operator"))
                .Select(c => new TailscaleOperator(c.Id, c.ClusterId, c.ClusterName, c.Namespace)),
        ];
    }

    // ── L3 — service annotation ───────────────────────────────────────────────

    /// <summary>
    /// Returns all cluster services with real ClusterIPs plus their current Tailscale exposure state.
    /// Services with ClusterIP "None" (headless) are excluded.
    /// </summary>
    public async Task<List<TailscaleExposedService>> ListServicesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        string json = await (await AccessAsync(tenantId, clusterId, ct)).GetJsonAllNamespacesAsync("services", ct: ct);
        JsonNode? root = JsonNode.Parse(json);
        List<TailscaleExposedService> services = [];

        if (root?["items"] is not JsonArray items) return services;

        foreach (JsonNode? item in items)
        {
            if (item is null) continue;

            string ns   = item["metadata"]?["namespace"]?.GetValue<string>() ?? "";
            string name = item["metadata"]?["name"]?.GetValue<string>() ?? "";
            string ip   = item["spec"]?["clusterIP"]?.GetValue<string>() ?? "";

            if (string.IsNullOrEmpty(ip) || ip == "None") continue;

            JsonNode? ann = item["metadata"]?["annotations"];
            bool exposed             = ann?["tailscale.com/expose"]?.GetValue<string>() == "true";
            string? reqHostname      = ann?["tailscale.com/hostname"]?.GetValue<string>();
            string? tailnetIp        = ann?["tailscale.com/tailnet-ip"]?.GetValue<string>();
            string? dnsName          = ann?["tailscale.com/dns-name"]?.GetValue<string>();

            string ports = "";
            if (item["spec"]?["ports"] is JsonArray portArr)
            {
                ports = string.Join(", ", portArr
                    .Select(p => $"{p?["port"]?.GetValue<int>()}/{p?["protocol"]?.GetValue<string>() ?? "TCP"}")
                    .Where(s => !s.StartsWith("0/")));
            }

            services.Add(new TailscaleExposedService(
                name, ns, ip, ports, exposed, reqHostname, tailnetIp, dnsName));
        }

        return [.. services.OrderBy(s => s.Namespace).ThenBy(s => s.ServiceName)];
    }

    /// <summary>
    /// Annotates a Kubernetes service to be exposed in the tailnet.
    /// The Tailscale Operator picks this up and creates the proxy device.
    /// </summary>
    public async Task ExposeServiceAsync(
        string serviceName, string ns, string? hostname,
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        JsonObject annotations = new() { ["tailscale.com/expose"] = JsonValue.Create("true") };
        if (!string.IsNullOrWhiteSpace(hostname))
            annotations["tailscale.com/hostname"] = JsonValue.Create(hostname.Trim());

        string patch = JsonSerializer.Serialize(new { metadata = new { annotations } });
        await (await AccessAsync(tenantId, clusterId, ct)).PatchJsonAsync("service", serviceName, ns, patch, ct);
    }

    /// <summary>
    /// Removes Tailscale annotations from a service (null = remove in merge patch).
    /// The Tailscale Operator will delete the proxy device.
    /// </summary>
    public async Task UnexposeServiceAsync(
        string serviceName, string ns, Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        const string patch = """
            {"metadata":{"annotations":{
                "tailscale.com/expose":null,
                "tailscale.com/hostname":null,
                "tailscale.com/tailnet-ip":null,
                "tailscale.com/dns-name":null
            }}}
            """;
        await (await AccessAsync(tenantId, clusterId, ct)).PatchJsonAsync("service", serviceName, ns, patch, ct);
    }

    // ── L7 — Tailscale Ingress ────────────────────────────────────────────────

    /// <summary>
    /// Lists all Ingress resources with ingressClassName=tailscale across all namespaces.
    /// </summary>
    public async Task<List<TailscaleIngress>> ListIngressesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        string json = await (await AccessAsync(tenantId, clusterId, ct))
            .GetJsonAllNamespacesAsync("ingresses", ct: ct);
        JsonNode? root = JsonNode.Parse(json);
        List<TailscaleIngress> ingresses = [];

        if (root?["items"] is not JsonArray items) return ingresses;

        foreach (JsonNode? item in items)
        {
            if (item is null) continue;

            string? className = item["spec"]?["ingressClassName"]?.GetValue<string>();
            if (!string.Equals(className, "tailscale", StringComparison.OrdinalIgnoreCase))
                continue;

            string ns   = item["metadata"]?["namespace"]?.GetValue<string>() ?? "";
            string name = item["metadata"]?["name"]?.GetValue<string>() ?? "";

            // FirstOrDefault rather than [0] throughout: indexing an EMPTY JsonArray throws
            // ArgumentOutOfRangeException, which the null-conditional chain does not catch. All
            // three of these arrays are legitimately empty — an Ingress may carry no rules, a rule
            // no paths, and `status.loadBalancer.ingress` is empty for every LoadBalancer that has
            // not been given an address yet, which is the normal state for a few seconds after
            // one is created. With [0] a single pending Ingress threw out of this loop and took
            // the whole listing with it. The line above already pattern-matches to JsonArray for
            // exactly this reason.
            JsonNode? firstRule = (item["spec"]?["rules"] as JsonArray)?.FirstOrDefault();
            string? host    = firstRule?["host"]?.GetValue<string>();
            JsonNode? firstPath = (firstRule?["http"]?["paths"] as JsonArray)?.FirstOrDefault();
            string svcName  = firstPath?["backend"]?["service"]?["name"]?.GetValue<string>() ?? "";
            int svcPort     = firstPath?["backend"]?["service"]?["port"]?["number"]?.GetValue<int>() ?? 80;

            string? lbHost = (item["status"]?["loadBalancer"]?["ingress"] as JsonArray)
                ?.FirstOrDefault()?["hostname"]?.GetValue<string>();

            ingresses.Add(new TailscaleIngress(name, ns, svcName, svcPort, host, lbHost));
        }

        return [.. ingresses.OrderBy(i => i.Namespace).ThenBy(i => i.Name)];
    }

    /// <summary>
    /// Creates a Kubernetes Ingress with ingressClassName=tailscale.
    /// If <paramref name="hostname"/> is provided it becomes the tailnet device name;
    /// otherwise the Ingress name is used.
    /// </summary>
    public async Task CreateIngressAsync(
        string name, string ns, string serviceName, int servicePort,
        string? hostname, Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        string manifest = BuildIngressManifest(name, ns, serviceName, servicePort, hostname);
        await (await AccessAsync(tenantId, clusterId, ct)).ApplyManifestAsync(manifest, ct);
    }

    public async Task DeleteIngressAsync(
        string name, string ns, Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await (await AccessAsync(tenantId, clusterId, ct)).DeleteManifestAsync("ingress", name, ns, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildIngressManifest(
        string name, string ns, string serviceName, int servicePort, string? hostname)
    {
        string rules = string.IsNullOrWhiteSpace(hostname)
            ? $"""
                  rules:
                  - http:
                      paths:
                      - path: /
                        pathType: Prefix
                        backend:
                          service:
                            name: {serviceName}
                            port:
                              number: {servicePort}
                """
            : $"""
                  rules:
                  - host: {hostname}
                    http:
                      paths:
                      - path: /
                        pathType: Prefix
                        backend:
                          service:
                            name: {serviceName}
                            port:
                              number: {servicePort}
                  tls:
                  - hosts:
                    - {hostname}
                """;

        return $"""
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata:
              name: {name}
              namespace: {ns}
              labels:
                app.kubernetes.io/managed-by: entkube
            spec:
              ingressClassName: tailscale
            {rules}
            """;
    }
}
