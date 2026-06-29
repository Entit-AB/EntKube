using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record TailscaleOperator(ClusterComponent Component, string ClusterName);

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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8sFactory)
{
    // ── Operator discovery ────────────────────────────────────────────────────

    public async Task<List<TailscaleOperator>> GetOperatorsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<ClusterComponent> components = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Cluster.TenantId == tenantId
                     && (c.HelmChartName == "tailscale-operator" || c.Name == "tailscale-operator")
                     && c.Status == ComponentStatus.Installed)
            .ToListAsync(ct);
        return components.Select(c => new TailscaleOperator(c, c.Cluster.Name)).ToList();
    }

    // ── L3 — service annotation ───────────────────────────────────────────────

    /// <summary>
    /// Returns all cluster services with real ClusterIPs plus their current Tailscale exposure state.
    /// Services with ClusterIP "None" (headless) are excluded.
    /// </summary>
    public async Task<List<TailscaleExposedService>> ListServicesAsync(
        string kubeconfig, CancellationToken ct = default)
    {
        string json = await k8sFactory.GetJsonAllNamespacesAsync("services", kubeconfig, ct: ct);
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
        string kubeconfig, CancellationToken ct = default)
    {
        JsonObject annotations = new() { ["tailscale.com/expose"] = JsonValue.Create("true") };
        if (!string.IsNullOrWhiteSpace(hostname))
            annotations["tailscale.com/hostname"] = JsonValue.Create(hostname.Trim());

        string patch = JsonSerializer.Serialize(new { metadata = new { annotations } });
        await k8sFactory.PatchJsonAsync("service", serviceName, ns, patch, kubeconfig, ct);
    }

    /// <summary>
    /// Removes Tailscale annotations from a service (null = remove in merge patch).
    /// The Tailscale Operator will delete the proxy device.
    /// </summary>
    public async Task UnexposeServiceAsync(
        string serviceName, string ns, string kubeconfig, CancellationToken ct = default)
    {
        const string patch = """
            {"metadata":{"annotations":{
                "tailscale.com/expose":null,
                "tailscale.com/hostname":null,
                "tailscale.com/tailnet-ip":null,
                "tailscale.com/dns-name":null
            }}}
            """;
        await k8sFactory.PatchJsonAsync("service", serviceName, ns, patch, kubeconfig, ct);
    }

    // ── L7 — Tailscale Ingress ────────────────────────────────────────────────

    /// <summary>
    /// Lists all Ingress resources with ingressClassName=tailscale across all namespaces.
    /// </summary>
    public async Task<List<TailscaleIngress>> ListIngressesAsync(
        string kubeconfig, CancellationToken ct = default)
    {
        string json = await k8sFactory.GetJsonAllNamespacesAsync("ingresses", kubeconfig, ct: ct);
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

            JsonNode? firstRule = item["spec"]?["rules"]?[0];
            string? host    = firstRule?["host"]?.GetValue<string>();
            JsonNode? firstPath = firstRule?["http"]?["paths"]?[0];
            string svcName  = firstPath?["backend"]?["service"]?["name"]?.GetValue<string>() ?? "";
            int svcPort     = firstPath?["backend"]?["service"]?["port"]?["number"]?.GetValue<int>() ?? 80;

            string? lbHost = item["status"]?["loadBalancer"]?["ingress"]?[0]?["hostname"]?.GetValue<string>();

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
        string? hostname, string kubeconfig, CancellationToken ct = default)
    {
        string manifest = BuildIngressManifest(name, ns, serviceName, servicePort, hostname);
        await k8sFactory.ApplyManifestAsync(manifest, kubeconfig, ct);
    }

    public async Task DeleteIngressAsync(
        string name, string ns, string kubeconfig, CancellationToken ct = default)
    {
        await k8sFactory.DeleteManifestAsync("ingress", name, ns, kubeconfig);
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
