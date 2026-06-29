using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record HeadscaleUser(string Id, string Name, DateTime CreatedAt);

public record HeadscalePreAuthKey(
    string Id,
    string User,
    string Key,
    bool Reusable,
    bool Ephemeral,
    DateTime Expiration,
    bool Used);

public record HeadscaleNode(
    string Id,
    string Name,
    string[] IpAddresses,
    string User,
    DateTime LastSeen,
    bool Online,
    string[] AdvertisedRoutes,
    string[] EnabledRoutes);

public record HeadscaleRoute(
    string Id,
    string NodeId,
    string NodeName,
    string Prefix,
    bool Advertised,
    bool Enabled,
    bool IsPrimary);

// ── Service ───────────────────────────────────────────────────────────────────

public class HeadscaleService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8sFactory,
    VaultService vaultService,
    ExternalRouteService externalRouteService,
    ComponentLifecycleService lifecycleService,
    IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Component lookup ──────────────────────────────────────────────────────

    public async Task<ClusterComponent?> GetComponentAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.ClusterId == clusterId
                && (c.HelmChartName == "headscale" || c.Name == "headscale"), ct);
    }

    // ── Vault helpers ─────────────────────────────────────────────────────────

    public async Task<string?> GetServerUrlAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp is null) return null;
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster cluster = await db.KubernetesClusters
            .FirstAsync(c => c.Id == clusterId, ct);
        return await vaultService.GetComponentSecretValueAsync(cluster.TenantId, comp.Id, "server-url", ct);
    }

    public async Task<string?> GetClusterIssuerAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp is null) return null;
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster cluster = await db.KubernetesClusters
            .FirstAsync(c => c.Id == clusterId, ct);
        return await vaultService.GetComponentSecretValueAsync(cluster.TenantId, comp.Id, "cluster-issuer", ct);
    }

    public async Task<string?> GetApiKeyAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp is null) return null;
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster cluster = await db.KubernetesClusters
            .FirstAsync(c => c.Id == clusterId, ct);
        return await vaultService.GetComponentSecretValueAsync(cluster.TenantId, comp.Id, "api-key", ct);
    }

    public async Task SaveApiKeyAsync(Guid clusterId, string apiKey, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp is null) throw new InvalidOperationException("Headscale component not found.");
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster cluster = await db.KubernetesClusters
            .FirstAsync(c => c.Id == clusterId, ct);
        await vaultService.SetComponentSecretAsync(cluster.TenantId, comp.Id, "api-key", apiKey, ct);
    }

    // ── Bootstrap: generate API key via kubectl exec ──────────────────────────

    /// <summary>
    /// Runs `headscale apikeys create` inside the headscale pod and returns the key.
    /// This is necessary because the REST API requires an API key to call — chicken-and-egg.
    /// </summary>
    public async Task<string> GenerateApiKeyAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null)
            throw new InvalidOperationException("Headscale component not found or cluster has no kubeconfig.");

        string podName = await GetHeadscalePodNameAsync(clusterId, comp.Cluster.Kubeconfig, ct);
        string output = await k8sFactory.RunCommandOnPodAsync(
            podName, "headscale",
            ["headscale", "apikeys", "create"],
            comp.Cluster.Kubeconfig,
            ct: ct);

        // headscale writes structured log lines to stderr; the API key is the last
        // non-empty line on stdout that doesn't start with a date/time or '{'.
        string key = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => !string.IsNullOrEmpty(l)
                             && !l.StartsWith('{')           // JSON log lines
                             && !l.StartsWith("20")          // timestamp lines (2026-...)
                             && !l.Contains(' '))            // any line with spaces is a log line
            ?? "";

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Could not parse API key from output:\n{output}");
        return key;
    }

    private async Task<string> GetHeadscalePodNameAsync(Guid clusterId, string kubeconfig, CancellationToken ct)
    {
        string json = await k8sFactory.GetJsonAsync("pods", "headscale", kubeconfig,
            "app=headscale", ct);
        JsonNode? root = JsonNode.Parse(json);
        string? podName = root?["items"]?[0]?["metadata"]?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(podName))
            throw new InvalidOperationException("Headscale pod not found. Is headscale installed and running?");
        return podName;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> IsReachableAsync(string serverUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            using HttpClient http = BuildClient(serverUrl, apiKey);
            HttpResponseMessage resp = await http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    public async Task<List<HeadscaleUser>> ListUsersAsync(
        string serverUrl, string apiKey, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        string json = await http.GetStringAsync("/api/v1/user", ct);
        JsonNode? root = JsonNode.Parse(json);
        return ParseUsers(root?["users"] as JsonArray ?? []);
    }

    public async Task<HeadscaleUser> CreateUserAsync(
        string serverUrl, string apiKey, string name, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        StringContent body = JsonBody(new { name });
        HttpResponseMessage resp = await http.PostAsync("/api/v1/user", body, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? root = JsonNode.Parse(json);
        return ParseUser(root?["user"]!)!;
    }

    public async Task DeleteUserAsync(
        string serverUrl, string apiKey, string name, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        HttpResponseMessage resp = await http.DeleteAsync($"/api/v1/user/{Uri.EscapeDataString(name)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    // ── Pre-auth keys ─────────────────────────────────────────────────────────

    public async Task<List<HeadscalePreAuthKey>> ListPreAuthKeysAsync(
        string serverUrl, string apiKey, string user, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        string json = await http.GetStringAsync(
            $"/api/v1/preauthkey?user={Uri.EscapeDataString(user)}", ct);
        JsonNode? root = JsonNode.Parse(json);
        return ParsePreAuthKeys(root?["preAuthKeys"] as JsonArray ?? []);
    }

    public async Task<HeadscalePreAuthKey> CreatePreAuthKeyAsync(
        string serverUrl, string apiKey, string user,
        bool reusable, bool ephemeral, string? expiration = null,
        CancellationToken ct = default)
    {
        string exp = expiration ?? "9999-12-31T00:00:00.000Z";
        using HttpClient http = BuildClient(serverUrl, apiKey);
        StringContent body = JsonBody(new { user, reusable, ephemeral, expiration = exp, aclTags = Array.Empty<string>() });
        HttpResponseMessage resp = await http.PostAsync("/api/v1/preauthkey", body, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? root = JsonNode.Parse(json);
        return ParsePreAuthKey(root?["preAuthKey"]!)!;
    }

    public async Task ExpirePreAuthKeyAsync(
        string serverUrl, string apiKey, string user, string key, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        StringContent body = JsonBody(new { user, key });
        HttpResponseMessage resp = await http.PostAsync("/api/v1/preauthkey/expire", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ── Nodes ─────────────────────────────────────────────────────────────────

    public async Task<List<HeadscaleNode>> ListNodesAsync(
        string serverUrl, string apiKey, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        string json = await http.GetStringAsync("/api/v1/node", ct);
        JsonNode? root = JsonNode.Parse(json);
        return ParseNodes(root?["nodes"] as JsonArray ?? []);
    }

    public async Task DeleteNodeAsync(
        string serverUrl, string apiKey, string nodeId, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        HttpResponseMessage resp = await http.DeleteAsync($"/api/v1/node/{nodeId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    public async Task<List<HeadscaleRoute>> ListRoutesAsync(
        string serverUrl, string apiKey, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        string json = await http.GetStringAsync("/api/v1/routes", ct);
        JsonNode? root = JsonNode.Parse(json);
        return ParseRoutes(root?["routes"] as JsonArray ?? []);
    }

    public async Task EnableRouteAsync(
        string serverUrl, string apiKey, string routeId, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        HttpResponseMessage resp = await http.PostAsync($"/api/v1/routes/{routeId}/enable",
            new StringContent(""), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DisableRouteAsync(
        string serverUrl, string apiKey, string routeId, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        HttpResponseMessage resp = await http.DeleteAsync($"/api/v1/routes/{routeId}/enable", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task EnableAllRoutesAsync(
        string serverUrl, string apiKey, CancellationToken ct = default)
    {
        List<HeadscaleRoute> routes = await ListRoutesAsync(serverUrl, apiKey, ct);
        foreach (HeadscaleRoute route in routes.Where(r => r.Advertised && !r.Enabled))
            await EnableRouteAsync(serverUrl, apiKey, route.Id, ct);
    }

    // ── ACL Policy ────────────────────────────────────────────────────────────

    public async Task<string> GetPolicyAsync(
        string serverUrl, string apiKey, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        string json = await http.GetStringAsync("/api/v1/policy", ct);
        JsonNode? root = JsonNode.Parse(json);
        return root?["policy"]?.GetValue<string>() ?? "{}";
    }

    public async Task SetPolicyAsync(
        string serverUrl, string apiKey, string policy, CancellationToken ct = default)
    {
        using HttpClient http = BuildClient(serverUrl, apiKey);
        StringContent body = JsonBody(new { policy });
        HttpResponseMessage resp = await http.PutAsync("/api/v1/policy", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ── External route ────────────────────────────────────────────────────────

    /// <summary>
    /// Called after a component install completes. If the component is headscale and
    /// has server-url + cluster-issuer configured in vault, auto-creates the external
    /// route so headscale is immediately reachable without any manual steps.
    /// Safe to call for any component — silently no-ops if not headscale or config is missing.
    /// </summary>
    public async Task EnsureExternalRouteAfterInstallAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? comp = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        if (comp is null) return;
        if (comp.HelmChartName != "headscale" && comp.Name != "headscale") return;

        string? url = await vaultService.GetComponentSecretValueAsync(
            comp.Cluster.TenantId, comp.Id, "server-url", ct);
        string? issuer = await vaultService.GetComponentSecretValueAsync(
            comp.Cluster.TenantId, comp.Id, "cluster-issuer", ct);

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(issuer)) return;

        await EnsureExternalRouteAsync(componentId, url, issuer, ct);
        await EnsureIstioPermissiveAsync(comp.ClusterId, ct);
    }

    /// <summary>
    /// Creates an ExternalRoute for headscale if none exists yet, then applies it
    /// to the cluster so the Gateway and HTTPRoute resources are created.
    /// Called automatically when the VPN tab detects headscale is installed but
    /// not yet exposed.
    /// </summary>
    public async Task EnsureExternalRouteAsync(
        Guid componentId, string serverUrl, string clusterIssuer, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool hasRoute = await db.ExternalRoutes.AnyAsync(r => r.ComponentId == componentId, ct);
        if (hasRoute) return;

        // Extract hostname from the server URL.
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException($"Invalid server URL: {serverUrl}");

        ExternalRouteRequest req = new()
        {
            Hostname = uri.Host,
            ServiceName = "headscale",
            ServicePort = 80,
            PathPrefix = "/",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = clusterIssuer
        };

        await externalRouteService.AddRouteAsync(componentId, req, ct);
        await lifecycleService.ApplyExternalRoutesAsync(componentId, ct);
    }

    /// <summary>
    /// Applies Istio resources required for headscale to work through the Istio ingress gateway.
    ///
    /// Root cause of the 403: Tailscale's ts2021 noise protocol uses HTTP Upgrade
    /// ("Upgrade: ts2021") for its control-plane connection. Istio's Envoy gateway only allows
    /// "websocket" upgrades by default and returns 403 Forbidden for any other upgrade type —
    /// the request never reaches headscale at all. The EnvoyFilter patches the gateway's
    /// HttpConnectionManager to add "ts2021" to its upgrade_configs.
    ///
    /// The PeerAuthentication + DestinationRule resources are kept as belt-and-suspenders
    /// in case the headscale namespace ever gains Istio injection, but are not the root fix.
    ///
    /// No-ops for non-Istio gateway classes.
    /// </summary>
    public async Task EnsureIstioPermissiveAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<ClusterComponent> clusterComponents = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId)
            .ToListAsync(ct);

        string gatewayClass = ExternalRouteService.ResolveGatewayClass(clusterComponents);
        if (gatewayClass != "istio") return;

        (_, string gwNamespace) = ExternalRouteService.ResolveGateway(clusterComponents);
        string kubeconfig = comp.Cluster.Kubeconfig;

        // Get the headscale hostname so we can scope the ALPN fix to just this SNI.
        string? serverUrl = await GetServerUrlAsync(clusterId, ct);
        if (string.IsNullOrEmpty(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri))
            return;
        string hostname = uri.Host;

        // ROOT CAUSE FIX: Tailscale's ts2021 noise protocol requires HTTP/1.1 because it uses
        // HTTP Upgrade ("Upgrade: ts2021"). The Istio gateway advertises h2 in ALPN, so Go's
        // HTTP client negotiates HTTP/2, which does not support protocol upgrades at all.
        // The gateway returns 403 for every connection attempt before headscale ever sees it.
        //
        // Fix: patch the TLS filter chain for the headscale SNI to only advertise http/1.1,
        // forcing Tailscale to connect over HTTP/1.1 where the Upgrade mechanism works.
        string alpnFilter = $"""
            apiVersion: networking.istio.io/v1alpha3
            kind: EnvoyFilter
            metadata:
              name: entkube-headscale-h1-only
              namespace: {gwNamespace}
            spec:
              configPatches:
              - applyTo: FILTER_CHAIN
                match:
                  context: GATEWAY
                  listener:
                    filterChain:
                      sni: {hostname}
                patch:
                  operation: MERGE
                  value:
                    transport_socket:
                      name: envoy.transport_sockets.tls
                      typed_config:
                        "@type": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.DownstreamTlsContext
                        common_tls_context:
                          alpn_protocols:
                          - http/1.1
            """;
        await k8sFactory.ApplyManifestAsync(alpnFilter, kubeconfig, ct);

        // Secondary fix: with HTTP/1.1 in place, Envoy must recognise the ts2021 upgrade type
        // or it will strip the Upgrade header before forwarding. No portNumber filter so it
        // applies regardless of whether the gateway pod listens on 443 or 8443 internally.
        string upgradeFilter = $"""
            apiVersion: networking.istio.io/v1alpha3
            kind: EnvoyFilter
            metadata:
              name: entkube-headscale-ts2021
              namespace: {gwNamespace}
            spec:
              configPatches:
              - applyTo: NETWORK_FILTER
                match:
                  context: GATEWAY
                  listener:
                    filterChain:
                      filter:
                        name: envoy.filters.network.http_connection_manager
                patch:
                  operation: MERGE
                  value:
                    typed_config:
                      "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
                      upgrade_configs:
                      - upgrade_type: ts2021
            """;
        await k8sFactory.ApplyManifestAsync(upgradeFilter, kubeconfig, ct);

        // Belt-and-suspenders: allow plain-text and mTLS into headscale namespace.
        string peerAuth = $"""
            apiVersion: security.istio.io/v1beta1
            kind: PeerAuthentication
            metadata:
              name: entkube-permissive
              namespace: headscale
            spec:
              mtls:
                mode: PERMISSIVE
            """;
        await k8sFactory.ApplyManifestAsync(peerAuth, kubeconfig, ct);

        // Belt-and-suspenders: disable mTLS origination from the gateway toward headscale.
        string destinationRule = $"""
            apiVersion: networking.istio.io/v1beta1
            kind: DestinationRule
            metadata:
              name: entkube-disable-mtls-headscale
              namespace: {gwNamespace}
            spec:
              host: headscale.headscale.svc.cluster.local
              trafficPolicy:
                tls:
                  mode: DISABLE
            """;
        await k8sFactory.ApplyManifestAsync(destinationRule, kubeconfig, ct);
    }

    // ── TLS passthrough ───────────────────────────────────────────────────────

    /// <summary>
    /// Switches headscale from gateway-terminated TLS to TLS passthrough.
    /// Adds an nginx sidecar that terminates TLS inside the pod (using a cert-manager
    /// certificate), then updates the Gateway listener to SNI passthrough so the
    /// Tailscale ts2021 noise upgrade never touches Envoy's HTTP layer.
    /// The headscale pod will be briefly unavailable while the cert is provisioned
    /// and the Deployment rolls out (~1-2 minutes for Let's Encrypt).
    /// </summary>
    public async Task SwitchToTlsPassthroughAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null)
            throw new InvalidOperationException("Headscale component not found.");

        string kubeconfig = comp.Cluster.Kubeconfig;

        string? serverUrl = await GetServerUrlAsync(clusterId, ct);
        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("server-url not configured in vault.");

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException($"Invalid server URL: {serverUrl}");
        string hostname = uri.Host;

        // 1. Copy the gateway TLS secret (cert-manager namespace → headscale namespace).
        //    The cert in cert-manager namespace was already issued by Let's Encrypt for this
        //    hostname and trusted by browsers. Reusing it avoids a second ACME round-trip and
        //    eliminates cert-trust issues caused by HTTP-01 challenge redirects.
        await CopyGatewayCertToHeadscaleAsync(hostname, kubeconfig, ct);

        // 2. Patch headscale-config ConfigMap to add tls_cert_path / tls_key_path.
        //    headscale reads these from the config file (Viper env-var override is unreliable
        //    for keys already set in the file). With these keys present headscale serves HTTPS
        //    on the existing listen_addr (0.0.0.0:8080).
        await PatchHeadscaleTlsConfigAsync(kubeconfig, ct);

        // 3. Apply updated Deployment: mounts headscale-tls secret (optional so the pod starts
        //    immediately); init container blocks until cert-manager writes the cert file.
        await k8sFactory.ApplyManifestAsync(BuildTlsDeploymentManifest(), kubeconfig, ct);

        // 3b. Force a rolling restart so headscale always picks up the patched ConfigMap.
        //     ConfigMap subPath mounts are frozen at pod start time — a running pod won't
        //     see the tls_cert_path addition until it restarts. On the first run the
        //     Deployment spec change already triggers a rollout; on subsequent "Reapply"
        //     calls the manifest is unchanged so only this explicit restart does anything.
        await ForceHeadscaleRolloutAsync(kubeconfig, ct);

        // 4. Apply Service exposing port 443 → pod 8080 (headscale now serves HTTPS there).
        await k8sFactory.ApplyManifestAsync(BuildTlsServiceManifest(), kubeconfig, ct);

        // 5. Switch ExternalRoute to Passthrough so the Gateway gets a TLS listener + TLSRoute.
        //    Delete the old HTTPRoute first to avoid a name conflict (same name, different kind).
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ExternalRoute? route = await db.ExternalRoutes
            .FirstOrDefaultAsync(r => r.ComponentId == comp.Id, ct);

        if (route is not null)
        {
            // Remove old HTTPRoute from the cluster before switching kind.
            string oldRouteName = ExternalRouteService.ToListenerName(route.Hostname) + "-route";
            string routeNs = comp.Namespace ?? "default";
            await k8sFactory.DeleteManifestAsync("httproute", oldRouteName, routeNs, kubeconfig, ct);

            route.TlsMode = TlsMode.Passthrough;
            route.ServicePort = 443;
            await db.SaveChangesAsync(ct);
        }

        await lifecycleService.ApplyExternalRoutesAsync(comp.Id, ct);
    }

    /// <summary>
    /// Reads the live headscale-config ConfigMap and appends tls_cert_path / tls_key_path
    /// if they are not already present. headscale reads these from the file at startup;
    /// env-var overrides via Viper are unreliable when the key already exists in the file.
    /// </summary>
    private async Task PatchHeadscaleTlsConfigAsync(string kubeconfig, CancellationToken ct)
    {
        string cmJson = await k8sFactory.GetJsonAsync(
            "configmap/headscale-config", "headscale", kubeconfig, ct: ct);

        using JsonDocument doc = JsonDocument.Parse(cmJson);
        string configYaml = doc.RootElement
            .GetProperty("data")
            .GetProperty("config.yaml")
            .GetString() ?? "";

        if (configYaml.Contains("tls_cert_path:"))
            return; // already patched

        string patched = configYaml.TrimEnd()
            + "\ntls_cert_path: /etc/headscale-tls/tls.crt"
            + "\ntls_key_path: /etc/headscale-tls/tls.key\n";

        // Strategic merge patch: replace only the config.yaml key.
        string jsonPatch = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data"] = new Dictionary<string, string> { ["config.yaml"] = patched }
        });
        await k8sFactory.PatchStrategicAsync(
            "configmap", "headscale-config", "headscale", jsonPatch, kubeconfig, ct);
    }

    /// <summary>
    /// Copies the TLS secret from cert-manager namespace (where the gateway-level cert lives)
    /// into the headscale namespace so headscale can serve it directly.
    /// On "Reapply" this also syncs any cert renewal cert-manager may have performed.
    /// </summary>
    /// <summary>
    /// Compares the TLS cert in cert-manager namespace against the copy in headscale namespace
    /// and re-syncs + restarts headscale only when cert-manager has renewed the cert.
    /// Called by <see cref="HeadscaleCertSyncService"/> on a background timer.
    /// </summary>
    public async Task SyncCertIfChangedAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        bool isPassthrough = await db.ExternalRoutes
            .AnyAsync(r => r.ComponentId == comp.Id && r.TlsMode == TlsMode.Passthrough, ct);
        if (!isPassthrough) return;

        string? serverUrl = await GetServerUrlAsync(clusterId, ct);
        if (string.IsNullOrEmpty(serverUrl)) return;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri)) return;

        string hostname = uri.Host;
        string kubeconfig = comp.Cluster.Kubeconfig;

        string? sourceCrt = await ReadCertCrtAsync(
            ExternalRouteService.ToCertSecretName(hostname), "cert-manager", kubeconfig, ct);
        if (string.IsNullOrEmpty(sourceCrt)) return;

        string? currentCrt = await ReadCertCrtAsync("headscale-tls", "headscale", kubeconfig, ct);

        if (sourceCrt == currentCrt) return;

        await CopyGatewayCertToHeadscaleAsync(hostname, kubeconfig, ct);
        await ForceHeadscaleRolloutAsync(kubeconfig, ct);
    }

    private async Task<string?> ReadCertCrtAsync(
        string secretName, string ns, string kubeconfig, CancellationToken ct)
    {
        try
        {
            string json = await k8sFactory.GetJsonAsync($"secret/{secretName}", ns, kubeconfig, ct: ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement data = doc.RootElement.GetProperty("data");
            return data.TryGetProperty("tls.crt", out JsonElement el) ? el.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task CopyGatewayCertToHeadscaleAsync(string hostname, string kubeconfig, CancellationToken ct)
    {
        string certSecretName = ExternalRouteService.ToCertSecretName(hostname);

        string secretJson = await k8sFactory.GetJsonAsync(
            $"secret/{certSecretName}", "cert-manager", kubeconfig, ct: ct);

        using JsonDocument doc = JsonDocument.Parse(secretJson);
        JsonElement data = doc.RootElement.GetProperty("data");
        string? tlsCrt = data.GetProperty("tls.crt").GetString();
        string? tlsKey = data.GetProperty("tls.key").GetString();

        if (string.IsNullOrEmpty(tlsCrt) || string.IsNullOrEmpty(tlsKey))
            throw new InvalidOperationException(
                $"TLS secret '{certSecretName}' not found in cert-manager namespace. " +
                "Ensure the headscale external route is configured and cert-manager has issued the cert " +
                "before switching to TLS passthrough.");

        string manifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: headscale-tls
              namespace: headscale
              labels:
                app.kubernetes.io/managed-by: entkube
            type: kubernetes.io/tls
            data:
              tls.crt: {tlsCrt}
              tls.key: {tlsKey}
            """;

        await k8sFactory.ApplyManifestAsync(manifest, kubeconfig, ct);
    }

    private async Task ForceHeadscaleRolloutAsync(string kubeconfig, CancellationToken ct)
    {
        string patch = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["spec"] = new Dictionary<string, object>
            {
                ["template"] = new Dictionary<string, object>
                {
                    ["metadata"] = new Dictionary<string, object>
                    {
                        ["annotations"] = new Dictionary<string, string>
                        {
                            ["kubectl.kubernetes.io/restartedAt"] = DateTime.UtcNow.ToString("O")
                        }
                    }
                }
            }
        });
        await k8sFactory.PatchJsonAsync("deployment", "headscale", "headscale", patch, kubeconfig, ct);
    }

    private static string BuildTlsDeploymentManifest() => """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: headscale
          namespace: headscale
          labels:
            app: headscale
            app.kubernetes.io/managed-by: entkube
        spec:
          replicas: 1
          selector:
            matchLabels:
              app: headscale
          template:
            metadata:
              labels:
                app: headscale
            spec:
              securityContext:
                fsGroup: 999
              initContainers:
              - name: wait-for-cert
                image: busybox:1.36
                command: ["sh", "-c", "until [ -f /etc/headscale-tls/tls.crt ]; do echo waiting for certificate; sleep 5; done"]
                volumeMounts:
                - name: headscale-tls
                  mountPath: /etc/headscale-tls
                  readOnly: true
              containers:
              - name: headscale
                image: ghcr.io/juanfont/headscale:0.23.0
                args: ["serve"]
                ports:
                - name: https
                  containerPort: 8080
                - name: metrics
                  containerPort: 9090
                volumeMounts:
                - name: data
                  mountPath: /var/lib/headscale
                - name: config
                  mountPath: /etc/headscale/config.yaml
                  subPath: config.yaml
                - name: policy
                  mountPath: /etc/headscale/policy.hujson
                  subPath: policy.hujson
                - name: headscale-tls
                  mountPath: /etc/headscale-tls
                  readOnly: true
                livenessProbe:
                  httpGet:
                    path: /metrics
                    port: 9090
                  initialDelaySeconds: 15
                  periodSeconds: 30
                resources:
                  requests:
                    cpu: 100m
                    memory: 128Mi
                  limits:
                    memory: 256Mi
              volumes:
              - name: data
                persistentVolumeClaim:
                  claimName: headscale-data
              - name: config
                configMap:
                  name: headscale-config
              - name: policy
                configMap:
                  name: headscale-policy
              - name: headscale-tls
                secret:
                  secretName: headscale-tls
                  optional: true
        """;

    private static string BuildTlsServiceManifest() => """
        apiVersion: v1
        kind: Service
        metadata:
          name: headscale
          namespace: headscale
          labels:
            app: headscale
            app.kubernetes.io/managed-by: entkube
        spec:
          selector:
            app: headscale
          ports:
          - name: https
            port: 443
            targetPort: 8080
          - name: grpc
            port: 50443
            targetPort: 50443
        """;

    // ── Infra pre-auth key ────────────────────────────────────────────────────

    /// <summary>
    /// Creates the "infra" user (if absent) and generates a reusable, long-lived
    /// pre-auth key. Stores the key in the vault and syncs it to the cluster as
    /// a Kubernetes Secret so the subnet router pod can mount it.
    /// </summary>
    public async Task<string> SetupInfraAuthKeyAsync(
        Guid clusterId, string serverUrl, string apiKey, CancellationToken ct = default)
    {
        // Ensure infra user exists.
        List<HeadscaleUser> users = await ListUsersAsync(serverUrl, apiKey, ct);
        if (!users.Any(u => u.Name == "infra"))
            await CreateUserAsync(serverUrl, apiKey, "infra", ct);

        // Generate a reusable, non-ephemeral key.
        HeadscalePreAuthKey preauth = await CreatePreAuthKeyAsync(
            serverUrl, apiKey, "infra", reusable: true, ephemeral: false, ct: ct);

        // Store in vault.
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp is not null)
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            KubernetesCluster cluster = await db.KubernetesClusters
                .FirstAsync(c => c.Id == clusterId, ct);
            await vaultService.SetComponentSecretAsync(
                cluster.TenantId, comp.Id, "infra-preauth-key", preauth.Key, ct);
        }

        // Sync to cluster as K8s secret.
        await SyncInfraAuthKeyToClusterAsync(clusterId, preauth.Key, ct);

        return preauth.Key;
    }

    private async Task SyncInfraAuthKeyToClusterAsync(
        Guid clusterId, string preAuthKey, CancellationToken ct)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null) return;

        string manifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: headscale-infra-authkey
              namespace: headscale
            type: Opaque
            stringData:
              authkey: {preAuthKey}
            """;

        await k8sFactory.ApplyManifestAsync(manifest, comp.Cluster.Kubeconfig, ct);
    }

    // ── Subnet router ─────────────────────────────────────────────────────────

    /// <summary>
    /// Deploys a Tailscale-based subnet router DaemonSet into the headscale namespace.
    /// The router advertises the given service CIDR to headscale so VPN clients can
    /// reach cluster-internal services. Requires the infra pre-auth key secret.
    /// </summary>
    public async Task DeploySubnetRouterAsync(
        Guid clusterId, string serverUrl, string serviceCidr, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null)
            throw new InvalidOperationException("Headscale component not found.");

        string manifest = BuildSubnetRouterManifest(serverUrl, serviceCidr);
        await k8sFactory.ApplyManifestAsync(manifest, comp.Cluster.Kubeconfig, ct);
    }

    public async Task RemoveSubnetRouterAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null) return;

        await k8sFactory.DeleteManifestAsync(
            "daemonset", "headscale-subnet-router", "headscale", comp.Cluster.Kubeconfig, ct);
    }

    public async Task<bool> IsSubnetRouterRunningAsync(Guid clusterId, CancellationToken ct = default)
    {
        ClusterComponent? comp = await GetComponentAsync(clusterId, ct);
        if (comp?.Cluster?.Kubeconfig is null) return false;

        try
        {
            string json = await k8sFactory.GetJsonAsync(
                "daemonsets", "headscale", comp.Cluster.Kubeconfig, "app=headscale-subnet-router", ct);
            JsonNode? root = JsonNode.Parse(json);
            return root?["items"] is JsonArray arr && arr.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSubnetRouterManifest(string serverUrl, string serviceCidr) => $$"""
        apiVersion: apps/v1
        kind: DaemonSet
        metadata:
          name: headscale-subnet-router
          namespace: headscale
          labels:
            app: headscale-subnet-router
            app.kubernetes.io/managed-by: entkube
        spec:
          selector:
            matchLabels:
              app: headscale-subnet-router
          template:
            metadata:
              labels:
                app: headscale-subnet-router
            spec:
              hostNetwork: true
              initContainers:
              - name: enable-ip-forward
                image: busybox:1.36
                command: ["sysctl", "-w", "net.ipv4.ip_forward=1"]
                securityContext:
                  privileged: true
              containers:
              - name: tailscale
                image: ghcr.io/tailscale/tailscale:stable
                env:
                - name: TS_KUBE_SECRET
                  value: ""
                - name: TS_STATE_DIR
                  value: /var/lib/tailscale
                - name: TS_AUTHKEY
                  valueFrom:
                    secretKeyRef:
                      name: headscale-infra-authkey
                      key: authkey
                - name: TS_USERSPACE
                  value: "false"
                - name: TS_EXTRA_ARGS
                  value: "--login-server={{serverUrl}} --advertise-routes={{serviceCidr}} --accept-routes=true"
                securityContext:
                  privileged: true
                  capabilities:
                    add: ["NET_ADMIN", "NET_RAW"]
                volumeMounts:
                - name: ts-state
                  mountPath: /var/lib/tailscale
                - name: dev-net-tun
                  mountPath: /dev/net/tun
              volumes:
              - name: ts-state
                emptyDir: {}
              - name: dev-net-tun
                hostPath:
                  path: /dev/net/tun
                  type: CharDevice
        """;

    // ── HTTP client ───────────────────────────────────────────────────────────

    private HttpClient BuildClient(string serverUrl, string apiKey)
    {
        HttpClient http = httpClientFactory.CreateClient("HeadscaleApi");
        http.BaseAddress = new Uri(serverUrl.TrimEnd('/'));
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    // ── JSON parsers ──────────────────────────────────────────────────────────

    private static List<HeadscaleUser> ParseUsers(JsonArray arr) =>
        arr.Select(n => ParseUser(n!)).Where(u => u is not null).Select(u => u!).ToList();

    private static HeadscaleUser? ParseUser(JsonNode n)
    {
        string? id = n["id"]?.GetValue<string>();
        string? name = n["name"]?.GetValue<string>();
        if (id is null || name is null) return null;
        DateTime created = n["createdAt"]?.GetValue<DateTime>() ?? DateTime.MinValue;
        return new HeadscaleUser(id, name, created);
    }

    private static List<HeadscalePreAuthKey> ParsePreAuthKeys(JsonArray arr) =>
        arr.Select(n => ParsePreAuthKey(n!)).Where(k => k is not null).Select(k => k!).ToList();

    private static HeadscalePreAuthKey? ParsePreAuthKey(JsonNode n)
    {
        string? id = n["id"]?.GetValue<string>();
        string? user = n["user"]?.GetValue<string>();
        string? key = n["key"]?.GetValue<string>();
        if (id is null || user is null || key is null) return null;
        bool reusable = n["reusable"]?.GetValue<bool>() ?? false;
        bool ephemeral = n["ephemeral"]?.GetValue<bool>() ?? false;
        DateTime exp = n["expiration"]?.GetValue<DateTime>() ?? DateTime.MaxValue;
        bool used = n["used"]?.GetValue<bool>() ?? false;
        return new HeadscalePreAuthKey(id, user, key, reusable, ephemeral, exp, used);
    }

    private static List<HeadscaleNode> ParseNodes(JsonArray arr) =>
        arr.Select(n => ParseNode(n!)).Where(n => n is not null).Select(n => n!).ToList();

    private static HeadscaleNode? ParseNode(JsonNode n)
    {
        string? id = n["id"]?.GetValue<string>();
        string? name = n["name"]?.GetValue<string>();
        if (id is null || name is null) return null;

        string[] ips = (n["ipAddresses"] as JsonArray ?? [])
            .Select(x => x?.GetValue<string>() ?? "")
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray();

        string user = n["user"]?["name"]?.GetValue<string>() ?? "";
        DateTime lastSeen = n["lastSeen"]?.GetValue<DateTime>() ?? DateTime.MinValue;
        bool online = n["online"]?.GetValue<bool>() ?? false;

        string[] adv = (n["advertisedRoutes"] as JsonArray ?? [])
            .Select(x => x?.GetValue<string>() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToArray();
        string[] ena = (n["enabledRoutes"] as JsonArray ?? [])
            .Select(x => x?.GetValue<string>() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToArray();

        return new HeadscaleNode(id, name, ips, user, lastSeen, online, adv, ena);
    }

    private static List<HeadscaleRoute> ParseRoutes(JsonArray arr) =>
        arr.Select(n => ParseRoute(n!)).Where(r => r is not null).Select(r => r!).ToList();

    private static HeadscaleRoute? ParseRoute(JsonNode n)
    {
        string? id = n["id"]?.GetValue<string>();
        string? prefix = n["prefix"]?.GetValue<string>();
        if (id is null || prefix is null) return null;
        string nodeId = n["node"]?["id"]?.GetValue<string>() ?? "";
        string nodeName = n["node"]?["name"]?.GetValue<string>() ?? "";
        bool advertised = n["advertised"]?.GetValue<bool>() ?? false;
        bool enabled = n["enabled"]?.GetValue<bool>() ?? false;
        bool isPrimary = n["isPrimary"]?.GetValue<bool>() ?? false;
        return new HeadscaleRoute(id, nodeId, nodeName, prefix, advertised, enabled, isPrimary);
    }
}
