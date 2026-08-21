using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Deploys and maintains the in-cluster TCP relay that lets EntKube reach an
/// endpoint its own egress IP is blocked from.
///
/// The problem this solves is one of direction. A provider that allowlists a
/// customer's network will not accept traffic from wherever EntKube happens to
/// run, and the customer typically cannot publish anything inbound to satisfy
/// the reverse. But EntKube already holds a kubeconfig for a cluster inside the
/// provider's own environment — so traffic sent from a pod in that cluster
/// leaves from an address the provider already trusts.
///
/// The relay is nginx's stream module with <c>ssl_preread</c>: it reads the SNI
/// from the TLS ClientHello and connects onward to that same host, never
/// terminating TLS. EntKube's certificate validation, the Host header and the S3
/// SigV4 signature therefore all still see the real endpoint — nothing has to be
/// rewritten or re-signed. Reaching it uses <c>kubectl port-forward</c>
/// (see <see cref="ClusterEgressTunnel"/>), which only needs EntKube to reach the
/// cluster's API server.
///
/// Routing is driven by an explicit host allowlist compiled into the config, so
/// this is not an open relay: an unlisted SNI maps to an empty upstream and nginx
/// drops the connection.
/// </summary>
public class ClusterEgressRelay(IKubernetesClientFactory k8s, ILogger<ClusterEgressRelay> logger)
{
    /// <summary>Namespace the relay runs in. Dedicated so it is obvious what it is and easy to remove.</summary>
    public const string Namespace = "entkube-egress";

    /// <summary>Name shared by the Deployment, Service and ConfigMap.</summary>
    public const string Name = "entkube-egress";

    /// <summary>Port the relay listens on inside the cluster.</summary>
    public const int Port = 8443;

    /// <summary>
    /// Container image for the relay. The official nginx alpine image is built
    /// <c>--with-stream_ssl_preread_module</c>, which is the whole mechanism; an
    /// air-gapped install can point this at a mirror.
    /// </summary>
    public string Image { get; init; } = "nginx:1.27-alpine";

    /// <summary>
    /// Applies the relay to <paramref name="kubeconfig"/>, allowing exactly
    /// <paramref name="allowedHosts"/> as upstreams.
    ///
    /// Safe to call repeatedly: the manifest is declarative, and the pod template
    /// carries a hash of the generated config so changing the allowlist rolls the
    /// pods rather than leaving them serving a stale one.
    /// </summary>
    public async Task EnsureAsync(
        string kubeconfig, IEnumerable<string> allowedHosts, CancellationToken ct = default)
    {
        List<string> hosts = NormalizeHosts(allowedHosts);

        if (hosts.Count == 0)
        {
            throw new InvalidOperationException(
                "Refusing to deploy the egress relay with an empty allowlist — it would accept no traffic.");
        }

        string dnsIp = await ResolveClusterDnsAsync(kubeconfig, ct);
        string nginxConf = BuildNginxConfig(hosts, dnsIp);

        logger.LogInformation(
            "Ensuring egress relay in {Namespace} for {HostCount} upstream host(s)", Namespace, hosts.Count);

        await k8s.ApplyManifestAsync(BuildManifest(nginxConf), kubeconfig, ct);
    }

    /// <summary>
    /// Removes the relay and its namespace. Used when a connection stops routing
    /// through the cluster, so nothing is left running that nobody asked for.
    /// </summary>
    public async Task RemoveAsync(string kubeconfig, CancellationToken ct = default)
    {
        try
        {
            await k8s.DeleteManifestAsync("namespace", Namespace, "", kubeconfig, ct);
        }
        catch (Exception ex)
        {
            // Teardown is best-effort — a leftover namespace must not fail the caller.
            logger.LogWarning(ex, "Failed to remove the egress relay namespace (continuing)");
        }
    }

    /// <summary>
    /// Lower-cases and de-duplicates hosts, dropping any scheme, port or path the
    /// caller passed by accident. SNI carries a bare hostname, so that is what the
    /// map keys must be.
    /// </summary>
    public static List<string> NormalizeHosts(IEnumerable<string> hosts)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string value = raw.Trim();

            // Accept a full URL as well as a bare host — callers hold both.
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0)
            {
                value = uri.Host;
            }
            else
            {
                int slash = value.IndexOf('/');
                if (slash >= 0) value = value[..slash];
                int colon = value.IndexOf(':');
                if (colon >= 0) value = value[..colon];
            }

            if (value.Length > 0)
            {
                seen.Add(value.ToLowerInvariant());
            }
        }

        return [.. seen.OrderBy(h => h, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Builds the nginx stream config: one listener, SNI-routed to an allowlisted
    /// upstream on the same host and port 443.
    /// </summary>
    public static string BuildNginxConfig(IReadOnlyList<string> hosts, string dnsIp)
    {
        StringBuilder map = new();

        foreach (string host in hosts)
        {
            // Quoted so a leading digit or a hyphen cannot be read as a directive.
            map.AppendLine($"            \"{host}\" \"{host}:443\";");
        }

        // $$""" so that single braces stay literal for nginx and {{...}} interpolates.
        return $$"""
            worker_processes 1;
            error_log /dev/stderr warn;
            pid /tmp/nginx.pid;

            events {
                worker_connections 1024;
            }

            stream {
                # Upstreams are looked up at request time so a DNS change does not
                # require restarting the relay.
                resolver {{dnsIp}} ipv6=off valid=30s;

                # An SNI that is not on the allowlist maps to an empty upstream,
                # which nginx refuses — this is deliberately not an open relay.
                map $ssl_preread_server_name $entkube_upstream {
                    default "";
            {{map.ToString().TrimEnd()}}
                }

                log_format entkube '$remote_addr $ssl_preread_server_name -> $entkube_upstream';
                access_log /dev/stdout entkube;

                server {
                    listen {{Port}};

                    # Read the ClientHello only. TLS is never terminated here, so the
                    # caller's certificate validation and request signing stay intact.
                    ssl_preread on;

                    proxy_connect_timeout 10s;
                    proxy_timeout 120s;
                    proxy_pass $entkube_upstream;
                }
            }
            """;
    }

    /// <summary>
    /// Builds the relay manifest. The pod template is annotated with a hash of the
    /// config so that changing the allowlist actually restarts nginx — a ConfigMap
    /// update alone would leave the running process on the old config.
    /// </summary>
    public string BuildManifest(string nginxConf)
    {
        string configHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(nginxConf)))[..16].ToLowerInvariant();

        string indentedConf = string.Join("\n",
            nginxConf.Split('\n').Select(l => "    " + l));

        return $$"""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: {{Namespace}}
              labels:
                app.kubernetes.io/managed-by: entkube
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{Name}}
              namespace: {{Namespace}}
            data:
              nginx.conf: |
            {{indentedConf}}
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{Name}}
              namespace: {{Namespace}}
              labels:
                app.kubernetes.io/name: {{Name}}
                app.kubernetes.io/managed-by: entkube
            spec:
              replicas: 1
              selector:
                matchLabels:
                  app.kubernetes.io/name: {{Name}}
              template:
                metadata:
                  labels:
                    app.kubernetes.io/name: {{Name}}
                  annotations:
                    entkube.io/config-hash: "{{configHash}}"
                spec:
                  containers:
                    - name: nginx
                      image: {{Image}}
                      args: ["nginx", "-c", "/etc/nginx/entkube/nginx.conf", "-g", "daemon off;"]
                      ports:
                        - containerPort: {{Port}}
                          name: relay
                      volumeMounts:
                        - name: config
                          mountPath: /etc/nginx/entkube
                        - name: tmp
                          mountPath: /tmp
                      resources:
                        requests:
                          cpu: 10m
                          memory: 16Mi
                        limits:
                          memory: 64Mi
                      securityContext:
                        allowPrivilegeEscalation: false
                        readOnlyRootFilesystem: true
                        capabilities:
                          drop: ["ALL"]
                  volumes:
                    - name: config
                      configMap:
                        name: {{Name}}
                    - name: tmp
                      emptyDir: {}
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: {{Name}}
              namespace: {{Namespace}}
              labels:
                app.kubernetes.io/name: {{Name}}
            spec:
              type: ClusterIP
              selector:
                app.kubernetes.io/name: {{Name}}
              ports:
                - name: relay
                  port: {{Port}}
                  targetPort: {{Port}}
            """;
    }

    /// <summary>
    /// Finds the cluster DNS service IP for nginx's <c>resolver</c>, which needs an
    /// address rather than a name. Falls back to the common 10.96.0.10 only if the
    /// service cannot be read, since a wrong resolver breaks every lookup.
    /// </summary>
    private async Task<string> ResolveClusterDnsAsync(string kubeconfig, CancellationToken ct)
    {
        try
        {
            string json = await k8s.GetJsonAsync("svc", "kube-system", kubeconfig, ct: ct);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out JsonElement items))
            {
                foreach (JsonElement svc in items.EnumerateArray())
                {
                    string? name = svc.GetProperty("metadata").GetProperty("name").GetString();

                    if (name is "kube-dns" or "coredns"
                        && svc.TryGetProperty("spec", out JsonElement spec)
                        && spec.TryGetProperty("clusterIP", out JsonElement ip)
                        && ip.GetString() is { Length: > 0 } value
                        && value != "None")
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the cluster DNS service; falling back to 10.96.0.10");
        }

        return "10.96.0.10";
    }
}
