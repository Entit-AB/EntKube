using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// A Helm release discovered from a cluster by reading Helm's storage secrets.
/// Contains all the metadata needed to import the release as a ClusterComponent.
/// </summary>
public class DiscoveredHelmRelease
{
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public string? ChartName { get; set; }
    public string? ChartVersion { get; set; }
    public string? AppVersion { get; set; }
    public string? Status { get; set; }
    public int Revision { get; set; }
    public string? Values { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// True if a ClusterComponent with this name already exists on the cluster.
    /// </summary>
    public bool AlreadyTracked { get; set; }

    /// <summary>
    /// If this release is a known subchart of another component, the parent
    /// catalog key is stored here (e.g. "cloudnative-pg" for plugin-barman-cloud).
    /// The UI can use this to hide subcharts or group them with their parent.
    /// </summary>
    public string? ParentComponentKey { get; set; }
}

/// <summary>
/// Scans a Kubernetes cluster for installed Helm releases by reading the Helm
/// storage secrets. Helm 3 stores release state as Kubernetes Secrets with the
/// label "owner=helm" in the release's target namespace.
///
/// The release data is encoded as: base64 (K8s) → base64 (Helm) → gzip → JSON.
/// This service decodes that chain to extract chart metadata, values, and status.
///
/// Found releases can then be imported as ClusterComponents so the platform
/// tracks what's already running — even if it was installed outside EntKube.
/// </summary>
public class ComponentScanService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    KyvernoPolicyService kyvernoPolicyService,
    RabbitMQService rabbitMQService)
{
    /// <summary>
    /// Scans the cluster for all installed Helm releases. Connects using the
    /// cluster's stored kubeconfig, queries all namespaces for Helm secrets,
    /// groups by release name (taking the latest revision), and decodes the
    /// release metadata.
    ///
    /// Returns a list of discovered releases with an AlreadyTracked flag
    /// indicating which ones are already registered as ClusterComponents.
    /// </summary>
    public async Task<List<DiscoveredHelmRelease>> ScanHelmReleasesAsync(
        KubernetesCluster cluster, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            return [];
        }

        // Connect to the cluster using its stored kubeconfig.

        Kubernetes client = CreateClient(cluster.Kubeconfig);

        // Query all namespaces for secrets labeled as Helm releases.
        // Helm 3 creates secrets with label "owner=helm" for each revision.

        V1SecretList helmSecrets;

        try
        {
            helmSecrets = await client.CoreV1.ListSecretForAllNamespacesAsync(
                labelSelector: "owner=helm",
                cancellationToken: ct);
        }
        catch
        {
            // If we can't reach the cluster or lack permissions, return empty.
            return [];
        }

        // Group secrets by release name, pick the latest revision for each.
        // Secret naming convention: sh.helm.release.v1.<name>.v<revision>

        List<DiscoveredHelmRelease> releases = [];
        Dictionary<string, (V1Secret secret, int revision)> latestByRelease = new();

        foreach (V1Secret secret in helmSecrets.Items)
        {
            string? releaseName = secret.Metadata?.Labels?.TryGetValue("name", out string? rn) == true ? rn : null;
            string? versionStr = secret.Metadata?.Labels?.TryGetValue("version", out string? vs) == true ? vs : null;

            if (releaseName is null || !int.TryParse(versionStr, out int revision))
            {
                continue;
            }

            string key = $"{secret.Metadata!.NamespaceProperty}/{releaseName}";

            if (!latestByRelease.TryGetValue(key, out (V1Secret secret, int revision) existing)
                || revision > existing.revision)
            {
                latestByRelease[key] = (secret, revision);
            }
        }

        // Get existing components on this cluster to mark already-tracked ones.

        List<ClusterComponent> existingComponents = await vaultService.GetComponentsAsync(cluster.Id, ct);

        // Include both Name and ReleaseName so that components registered via the
        // catalog (where Name = catalog key, e.g. "cloudnative-pg") are still
        // matched when the Helm release name differs (e.g. "cnpg").
        HashSet<string> trackedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClusterComponent c in existingComponents)
        {
            trackedNames.Add(c.Name);
            if (!string.IsNullOrEmpty(c.ReleaseName))
            {
                trackedNames.Add(c.ReleaseName);
            }
        }

        // Decode each latest-revision release secret into a DiscoveredHelmRelease.

        Dictionary<string, string> subchartParents = ComponentCatalog.GetSubchartParents();

        foreach (KeyValuePair<string, (V1Secret secret, int revision)> entry in latestByRelease)
        {
            (V1Secret secret, int revision) = entry.Value;
            DiscoveredHelmRelease? release = DecodeHelmRelease(secret, revision);

            if (release is not null)
            {
                release.AlreadyTracked = trackedNames.Contains(release.Name);

                // If this release is a known subchart, tag it with its parent key.

                if (subchartParents.TryGetValue(release.Name, out string? parentKey))
                {
                    release.ParentComponentKey = parentKey;
                }

                releases.Add(release);
            }
        }

        return releases.OrderBy(r => r.Namespace).ThenBy(r => r.Name).ToList();
    }

    /// <summary>
    /// Imports a discovered Helm release as a ClusterComponent. Creates the
    /// component with full lifecycle metadata (namespace, chart info, values,
    /// status). If a component with the same name already exists, it updates
    /// the existing one instead. Also discovers Ingress/HTTPRoute/LoadBalancer
    /// resources and creates ExternalRoute entries.
    /// </summary>
    public async Task<ClusterComponent> ImportReleaseAsync(
        KubernetesCluster cluster, DiscoveredHelmRelease release, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Check if a component with this name already exists on the cluster.

        ClusterComponent? existing = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.ClusterId == cluster.Id && c.Name == release.Name, ct);

        if (existing is not null)
        {
            // Update existing component with discovered state.
            // If it matches a catalog entry, enrich with repo URL.

            CatalogEntry? catalog = ComponentCatalog.FindByRelease(release.Name, release.ChartName);

            existing.Namespace = release.Namespace;
            existing.HelmChartName = catalog?.HelmChartName ?? release.ChartName;
            existing.HelmChartVersion = release.ChartVersion;
            existing.HelmValues = release.Values;
            existing.ReleaseName = release.Name;
            existing.Status = MapStatus(release.Status);
            existing.InstalledAt = release.UpdatedAt;

            if (string.IsNullOrWhiteSpace(existing.HelmRepoUrl) && catalog is not null)
            {
                existing.HelmRepoUrl = catalog.HelmRepoUrl;
            }

            await db.SaveChangesAsync(ct);

            // Extract secrets to vault if they don't already exist.

            await ExtractSecretsToVaultAsync(cluster.TenantId, existing, ct);

            // Discover exposed routes for existing component too.
            // Route discovery is best-effort — failure shouldn't block import.

            try
            {
                await DiscoverRoutesAsync(cluster, existing, ct);
            }
            catch
            {
                // Route discovery failed (cluster unreachable, CRD missing, etc.)
            }

            await TryAdoptManagedResourcesAsync(cluster, existing, ct);

            return existing;
        }

        // Create a new component from the discovered release.
        // If it matches a catalog entry, enrich with repo URL and correct chart name.

        CatalogEntry? catalogEntry = ComponentCatalog.FindByRelease(release.Name, release.ChartName);

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = release.Name,
            ComponentType = "HelmChart",
            Namespace = release.Namespace,
            HelmChartName = catalogEntry?.HelmChartName ?? release.ChartName,
            HelmChartVersion = release.ChartVersion,
            HelmRepoUrl = catalogEntry?.HelmRepoUrl,
            ReleaseName = release.Name,
            HelmValues = release.Values,
            Status = MapStatus(release.Status),
            InstalledAt = release.UpdatedAt
        };

        db.ClusterComponents.Add(component);
        await db.SaveChangesAsync(ct);

        // If this component matches a catalog entry with secret fields,
        // extract those secrets from the Helm values and store them in the vault.

        await ExtractSecretsToVaultAsync(cluster.TenantId, component, ct);

        // Discover exposed routes (Ingress, HTTPRoute, LoadBalancer) for the component.
        // Route discovery is best-effort — failure shouldn't block import.

        try
        {
            await DiscoverRoutesAsync(cluster, component, ct);
        }
        catch
        {
            // Route discovery failed (cluster unreachable, CRD missing, etc.)
        }

        await TryAdoptManagedResourcesAsync(cluster, component, ct);

        return component;
    }

    /// <summary>
    /// When an imported component is an operator/controller that EntKube manages the
    /// resources of, adopt those live resources too so the cluster's true state shows up
    /// instead of an empty view. Kyverno → adopt applied Policy resources; the RabbitMQ
    /// Cluster Operator → adopt existing RabbitmqCluster instances. Best-effort: a failure
    /// here never blocks the component import.
    /// </summary>
    private async Task TryAdoptManagedResourcesAsync(
        KubernetesCluster cluster, ClusterComponent component, CancellationToken ct)
    {
        try
        {
            bool isKyverno = Matches(component, "kyverno");
            bool isRabbitOperator = Matches(component, "rabbitmq-cluster-operator");

            if (isKyverno)
                await kyvernoPolicyService.DiscoverPoliciesAsync(cluster.TenantId, cluster.EnvironmentId, ct);

            if (isRabbitOperator)
                await rabbitMQService.DiscoverClustersAsync(cluster.TenantId, ct);
        }
        catch
        {
            // Adoption is best-effort; the component is already imported.
        }

        static bool Matches(ClusterComponent c, string key) =>
            string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ReleaseName, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.HelmChartName, key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Imports all non-tracked releases in a single operation.
    /// Returns the count of newly imported components. Individual import
    /// failures are collected and re-thrown as an aggregate after processing
    /// all releases, so one bad release doesn't block the rest.
    /// </summary>
    public async Task<int> ImportAllNewReleasesAsync(
        KubernetesCluster cluster, List<DiscoveredHelmRelease> releases, CancellationToken ct = default)
    {
        int imported = 0;
        List<string> errors = [];

        foreach (DiscoveredHelmRelease release in releases.Where(r => !r.AlreadyTracked && r.ParentComponentKey is null))
        {
            try
            {
                await ImportReleaseAsync(cluster, release, ct);
                release.AlreadyTracked = true;
                imported++;
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException?.Message ?? ex.Message;
                errors.Add($"{release.Name} ({release.Namespace}): {detail}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Imported {imported} releases but {errors.Count} failed:\n{string.Join("\n", errors)}");
        }

        return imported;
    }

    /// <summary>
    /// Discovers Ingress and HTTPRoute resources in the component's namespace
    /// that are related to the component (matched by release name label or
    /// by backend service name containing the release name). Creates ExternalRoute
    /// entries for each discovered hostname.
    ///
    /// This handles routes created by Helm charts (e.g. ingress-nginx values,
    /// Traefik IngressRoute converted to Ingress, etc.) regardless of whether
    /// EntKube originally created them.
    /// </summary>
    public async Task<List<ExternalRoute>> DiscoverRoutesAsync(
        KubernetesCluster cluster, ClusterComponent component, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig) || string.IsNullOrWhiteSpace(component.Namespace))
        {
            return [];
        }

        Kubernetes client = CreateClient(cluster.Kubeconfig);
        List<ExternalRoute> discovered = [];

        // Get existing routes for this component so we don't duplicate.

        HashSet<string> existingHostnames = (await db.ExternalRoutes
            .Where(r => r.ComponentId == component.Id)
            .Select(r => r.Hostname)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Scan Ingress resources ──

        try
        {
            V1IngressList ingresses = await client.NetworkingV1.ListNamespacedIngressAsync(
                component.Namespace, cancellationToken: ct);

            foreach (V1Ingress ingress in ingresses.Items)
            {
                // Match ingresses that belong to this release by label or name.

                if (!BelongsToRelease(ingress.Metadata, component.ReleaseName ?? component.Name))
                {
                    continue;
                }

                // Extract each rule's host and backend.

                if (ingress.Spec?.Rules is null)
                {
                    continue;
                }

                foreach (V1IngressRule rule in ingress.Spec.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Host) || existingHostnames.Contains(rule.Host))
                    {
                        continue;
                    }

                    // Find the primary backend service and port from the first path.

                    string? serviceName = null;
                    int servicePort = 80;

                    if (rule.Http?.Paths is { Count: > 0 })
                    {
                        V1IngressServiceBackend? backend = rule.Http.Paths[0].Backend?.Service;

                        if (backend is not null)
                        {
                            serviceName = backend.Name;
                            servicePort = backend.Port?.Number ?? 80;
                        }
                    }

                    // Determine TLS mode from ingress TLS config.

                    TlsMode tlsMode = TlsMode.ClusterIssuer;
                    string? issuerName = null;

                    if (ingress.Metadata?.Annotations is not null)
                    {
                        // cert-manager annotation pattern.

                        if (ingress.Metadata.Annotations.TryGetValue(
                            "cert-manager.io/cluster-issuer", out string? issuer))
                        {
                            issuerName = issuer;
                        }
                        else if (ingress.Metadata.Annotations.TryGetValue(
                            "cert-manager.io/issuer", out string? nsIssuer))
                        {
                            issuerName = nsIssuer;
                        }
                    }

                    bool hasTls = ingress.Spec.Tls?.Any(t =>
                        t.Hosts?.Contains(rule.Host) == true) ?? false;

                    if (hasTls && issuerName is null)
                    {
                        tlsMode = TlsMode.Manual;
                    }

                    ExternalRoute route = new()
                    {
                        Id = Guid.NewGuid(),
                        ComponentId = component.Id,
                        Hostname = rule.Host,
                        ServiceName = serviceName,
                        ServicePort = servicePort,
                        TlsMode = tlsMode,
                        ClusterIssuerName = issuerName ?? "letsencrypt-prod",
                        CreatedAt = DateTime.UtcNow
                    };

                    discovered.Add(route);
                    existingHostnames.Add(rule.Host);
                }
            }
        }
        catch
        {
            // Ingress API not available or permission denied — skip.
        }

        // ── Scan HTTPRoute resources (Gateway API) ──

        try
        {
            // HTTPRoute is a CRD — query via generic client.

            object httpRoutesRaw = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                "gateway.networking.k8s.io", "v1", component.Namespace, "httproutes",
                cancellationToken: ct);

            if (httpRoutesRaw is JsonElement routeListElement)
            {
                ParseHttpRoutes(routeListElement, component, existingHostnames, discovered);
            }
            else
            {
                // The K8s client returns different types depending on version.
                // Try parsing as JSON string.

                string json = JsonSerializer.Serialize(httpRoutesRaw);
                using JsonDocument doc = JsonDocument.Parse(json);
                ParseHttpRoutes(doc.RootElement, component, existingHostnames, discovered);
            }
        }
        catch
        {
            // Gateway API CRDs not installed or permission denied — skip.
        }

        // ── Scan LoadBalancer services for direct external IPs ──

        try
        {
            V1ServiceList services = await client.CoreV1.ListNamespacedServiceAsync(
                component.Namespace, cancellationToken: ct);

            foreach (V1Service svc in services.Items)
            {
                if (svc.Spec?.Type != "LoadBalancer")
                {
                    continue;
                }

                if (!BelongsToRelease(svc.Metadata, component.ReleaseName ?? component.Name))
                {
                    continue;
                }

                // Extract external IP or hostname from status.

                string? externalHost = svc.Status?.LoadBalancer?.Ingress?.FirstOrDefault()?.Hostname
                    ?? svc.Status?.LoadBalancer?.Ingress?.FirstOrDefault()?.Ip;

                if (string.IsNullOrWhiteSpace(externalHost) || existingHostnames.Contains(externalHost))
                {
                    continue;
                }

                int port = svc.Spec.Ports?.FirstOrDefault()?.Port ?? 80;

                ExternalRoute route = new()
                {
                    Id = Guid.NewGuid(),
                    ComponentId = component.Id,
                    Hostname = externalHost,
                    ServiceName = svc.Metadata?.Name,
                    ServicePort = port,
                    TlsMode = TlsMode.Manual,
                    CreatedAt = DateTime.UtcNow
                };

                discovered.Add(route);
                existingHostnames.Add(externalHost);
            }
        }
        catch
        {
            // Service list failed — skip.
        }

        // Persist all discovered routes.

        if (discovered.Count > 0)
        {
            db.ExternalRoutes.AddRange(discovered);
            await db.SaveChangesAsync(ct);
        }

        return discovered;
    }

    // ──────── Internal ────────

    /// <summary>
    /// Checks whether a Kubernetes resource belongs to a Helm release by examining
    /// common labels (app.kubernetes.io/instance, helm.sh/release-name) or by
    /// checking if the resource name starts with the release name.
    /// </summary>
    private static bool BelongsToRelease(V1ObjectMeta? metadata, string releaseName)
    {
        if (metadata is null)
        {
            return false;
        }

        string releaseNameLower = releaseName.ToLowerInvariant();

        // Check standard Helm/Kubernetes labels.

        if (metadata.Labels is not null)
        {
            if (metadata.Labels.TryGetValue("app.kubernetes.io/instance", out string? instance)
                && string.Equals(instance, releaseName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (metadata.Labels.TryGetValue("release", out string? rel)
                && string.Equals(rel, releaseName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (metadata.Labels.TryGetValue("helm.sh/release-name", out string? helmRel)
                && string.Equals(helmRel, releaseName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Fallback: resource name starts with the release name (common Helm pattern).

        if (metadata.Name?.ToLowerInvariant().StartsWith(releaseNameLower) == true)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses HTTPRoute resources from a Gateway API custom object list response.
    /// </summary>
    private static void ParseHttpRoutes(
        JsonElement routeList, ClusterComponent component,
        HashSet<string> existingHostnames, List<ExternalRoute> discovered)
    {
        if (!routeList.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement route in items.EnumerateArray())
        {
            // Check if this route belongs to the release.

            bool matches = false;

            if (route.TryGetProperty("metadata", out JsonElement meta)
                && meta.TryGetProperty("labels", out JsonElement labels))
            {
                if (labels.TryGetProperty("app.kubernetes.io/instance", out JsonElement inst)
                    && string.Equals(inst.GetString(), component.ReleaseName ?? component.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                }
                else if (labels.TryGetProperty("helm.sh/release-name", out JsonElement helmName)
                    && string.Equals(helmName.GetString(), component.ReleaseName ?? component.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                }
            }

            if (!matches && route.TryGetProperty("metadata", out JsonElement m2)
                && m2.TryGetProperty("name", out JsonElement nameEl))
            {
                string? routeName = nameEl.GetString();

                if (routeName?.StartsWith(component.ReleaseName ?? component.Name,
                    StringComparison.OrdinalIgnoreCase) == true)
                {
                    matches = true;
                }
            }

            if (!matches)
            {
                continue;
            }

            // Extract hostnames from spec.hostnames.

            if (!route.TryGetProperty("spec", out JsonElement spec))
            {
                continue;
            }

            if (spec.TryGetProperty("hostnames", out JsonElement hostnames)
                && hostnames.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement hostname in hostnames.EnumerateArray())
                {
                    string? host = hostname.GetString();

                    if (string.IsNullOrWhiteSpace(host) || existingHostnames.Contains(host))
                    {
                        continue;
                    }

                    // Try to find backend service from rules.

                    string? serviceName = null;
                    int servicePort = 80;

                    if (spec.TryGetProperty("rules", out JsonElement rules)
                        && rules.ValueKind == JsonValueKind.Array
                        && rules.GetArrayLength() > 0)
                    {
                        JsonElement firstRule = rules[0];

                        if (firstRule.TryGetProperty("backendRefs", out JsonElement backendRefs)
                            && backendRefs.ValueKind == JsonValueKind.Array
                            && backendRefs.GetArrayLength() > 0)
                        {
                            JsonElement firstBackend = backendRefs[0];
                            serviceName = firstBackend.TryGetProperty("name", out JsonElement sn)
                                ? sn.GetString() : null;
                            servicePort = firstBackend.TryGetProperty("port", out JsonElement sp)
                                ? sp.GetInt32() : 80;
                        }
                    }

                    // Extract gateway reference.

                    string? gatewayName = null;
                    string? gatewayNamespace = null;

                    if (spec.TryGetProperty("parentRefs", out JsonElement parentRefs)
                        && parentRefs.ValueKind == JsonValueKind.Array
                        && parentRefs.GetArrayLength() > 0)
                    {
                        JsonElement firstParent = parentRefs[0];
                        gatewayName = firstParent.TryGetProperty("name", out JsonElement gn)
                            ? gn.GetString() : null;
                        gatewayNamespace = firstParent.TryGetProperty("namespace", out JsonElement gns)
                            ? gns.GetString() : null;
                    }

                    ExternalRoute route2 = new()
                    {
                        Id = Guid.NewGuid(),
                        ComponentId = component.Id,
                        Hostname = host,
                        ServiceName = serviceName,
                        ServicePort = servicePort,
                        TlsMode = TlsMode.ClusterIssuer,
                        ClusterIssuerName = "letsencrypt-prod",
                        GatewayName = gatewayName,
                        GatewayNamespace = gatewayNamespace,
                        CreatedAt = DateTime.UtcNow
                    };

                    discovered.Add(route2);
                    existingHostnames.Add(host);
                }
            }
        }
    }

    /// <summary>
    /// Decodes a Helm release secret into a DiscoveredHelmRelease.
    /// The encoding chain is: K8s base64 → Helm base64 → gzip → JSON.
    /// Falls back to label-based discovery if data decoding fails.
    /// </summary>
    private static DiscoveredHelmRelease? DecodeHelmRelease(V1Secret secret, int revision)
    {
        // If the secret data is missing or doesn't have the "release" key,
        // fall back to building a minimal release from the secret's labels.
        // This can happen when RBAC strips data or storage format differs.

        if (secret.Data is null || !secret.Data.TryGetValue("release", out byte[]? rawData))
        {
            return BuildFallbackRelease(secret, revision);
        }

        try
        {
            // Step 1: K8s already base64-decoded the Secret data for us.
            // Step 2: Helm base64-encodes the release data before storing.

            string helmBase64 = Encoding.UTF8.GetString(rawData);
            byte[] compressed = Convert.FromBase64String(helmBase64);

            // Step 3: Decompress. Helm uses gzip by default, but newer versions
            // or custom builds may use zstd (magic bytes 0x28 0xB5 0x2F 0xFD).
            // Detect by the first two bytes (gzip magic: 0x1F 0x8B).

            byte[] jsonBytes;

            if (compressed.Length >= 2 && compressed[0] == 0x1F && compressed[1] == 0x8B)
            {
                // Standard gzip compression.
                using MemoryStream compressedStream = new(compressed);
                using GZipStream gzipStream = new(compressedStream, CompressionMode.Decompress);
                using MemoryStream decompressedStream = new();
                gzipStream.CopyTo(decompressedStream);
                jsonBytes = decompressedStream.ToArray();
            }
            else
            {
                // Not gzip — try interpreting as raw JSON (some Helm drivers
                // store uncompressed data), or fall back to labels.
                jsonBytes = compressed;
            }

            // Step 4: Parse JSON release object.

            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;

            string releaseName = root.GetProperty("name").GetString() ?? "unknown";
            string ns = root.GetProperty("namespace").GetString() ?? "default";

            // Chart metadata lives under root.chart.metadata.

            string? chartName = null;
            string? chartVersion = null;
            string? appVersion = null;

            if (root.TryGetProperty("chart", out JsonElement chart)
                && chart.TryGetProperty("metadata", out JsonElement metadata))
            {
                chartName = metadata.TryGetProperty("name", out JsonElement cn) ? cn.GetString() : null;
                chartVersion = metadata.TryGetProperty("version", out JsonElement cv) ? cv.GetString() : null;
                appVersion = metadata.TryGetProperty("appVersion", out JsonElement av) ? av.GetString() : null;
            }

            // Status from info.status.

            string? status = null;
            DateTime? updatedAt = null;

            if (root.TryGetProperty("info", out JsonElement info))
            {
                status = info.TryGetProperty("status", out JsonElement st) ? st.GetString() : null;

                if (info.TryGetProperty("last_deployed", out JsonElement ld))
                {
                    if (DateTime.TryParse(ld.GetString(), out DateTime parsed))
                    {
                        updatedAt = parsed.Kind == DateTimeKind.Utc
                            ? parsed
                            : parsed.ToUniversalTime();
                    }
                }
            }

            // User-supplied values (config).

            string? values = null;

            if (root.TryGetProperty("config", out JsonElement config)
                && config.ValueKind == JsonValueKind.Object)
            {
                // Convert to YAML-friendly JSON string. In a real scenario we might
                // convert to YAML, but JSON is sufficient for storage and display.

                values = config.GetRawText();

                // Empty config {} means no custom values.

                if (values == "{}")
                {
                    values = null;
                }
            }

            return new DiscoveredHelmRelease
            {
                Name = releaseName,
                Namespace = ns,
                ChartName = chartName,
                ChartVersion = chartVersion,
                AppVersion = appVersion,
                Status = status,
                Revision = revision,
                Values = values,
                UpdatedAt = updatedAt
            };
        }
        catch
        {
            // If decoding fails for any reason, build a minimal release from labels.

            return BuildFallbackRelease(secret, revision);
        }
    }

    /// <summary>
    /// Builds a minimal DiscoveredHelmRelease from the secret's labels when full
    /// decoding isn't possible. This ensures the release still appears in scan
    /// results even if data decoding fails.
    /// </summary>
    private static DiscoveredHelmRelease? BuildFallbackRelease(V1Secret secret, int revision)
    {
        string? fallbackName = secret.Metadata?.Labels?.TryGetValue("name", out string? fn) == true ? fn : null;
        string? fallbackStatus = secret.Metadata?.Labels?.TryGetValue("status", out string? fs) == true ? fs : null;

        if (fallbackName is null)
        {
            return null;
        }

        return new DiscoveredHelmRelease
        {
            Name = fallbackName,
            Namespace = secret.Metadata?.NamespaceProperty ?? "default",
            Status = fallbackStatus,
            Revision = revision
        };
    }

    private static ComponentStatus MapStatus(string? helmStatus)
    {
        return helmStatus?.ToLowerInvariant() switch
        {
            "deployed" => ComponentStatus.Installed,
            "failed" => ComponentStatus.Failed,
            "pending-install" or "pending-upgrade" or "pending-rollback" => ComponentStatus.Installing,
            "uninstalling" => ComponentStatus.Uninstalling,
            _ => ComponentStatus.NotInstalled
        };
    }

    /// <summary>
    /// If the imported component matches a catalog entry with secret-backed fields,
    /// extracts those values from HelmValues and stores them in the tenant vault.
    /// Only stores secrets that don't already exist — won't overwrite manually set values.
    /// </summary>
    private async Task ExtractSecretsToVaultAsync(
        Guid tenantId, ClusterComponent component, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(component.HelmValues))
        {
            return;
        }

        CatalogEntry? catalog = ComponentCatalog.FindByRelease(component.Name, component.HelmChartName);

        if (catalog is null)
        {
            return;
        }

        List<ComponentFormField> secretFields = catalog.FormFields
            .Where(f => f.StoreAsSecret)
            .ToList();

        foreach (ComponentFormField field in secretFields)
        {
            string secretName = field.SecretName ?? field.Key;

            // Only store if the vault doesn't already have this secret.

            string? existing = await vaultService.GetComponentSecretValueAsync(
                tenantId, component.Id, secretName, ct);

            if (!string.IsNullOrEmpty(existing))
            {
                continue;
            }

            // Extract the value from the stored Helm values YAML/JSON.

            string? value = YamlFormMerger.ExtractValue(component.HelmValues, field.YamlPath);

            if (!string.IsNullOrEmpty(value))
            {
                await vaultService.SetComponentSecretAsync(tenantId, component.Id, secretName, value, ct);
            }
        }
    }

    /// <summary>
    /// Checks whether the Gateway API CRDs (httproutes.gateway.networking.k8s.io) are
    /// installed on the cluster. Returns false if unreachable or CRDs are absent.
    /// </summary>
    public async Task<bool> CheckGatewayApiCrdsAsync(
        KubernetesCluster cluster, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            return false;
        }

        try
        {
            Kubernetes client = CreateClient(cluster.Kubeconfig);
            await client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(
                "httproutes.gateway.networking.k8s.io", cancellationToken: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether cert-manager has been started with Gateway API support enabled.
    /// Returns false if cert-manager is not installed or does not have the feature gate.
    /// </summary>
    public async Task<bool> CheckCertManagerGatewayApiAsync(
        KubernetesCluster cluster, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            return false;
        }

        try
        {
            Kubernetes client = CreateClient(cluster.Kubeconfig);
            k8s.Models.V1Deployment deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(
                "cert-manager", "cert-manager", cancellationToken: ct);

            return deployment.Spec?.Template?.Spec?.Containers?.Any(c =>
                c.Args?.Any(a =>
                    a.Contains("GatewayAPI=true", StringComparison.OrdinalIgnoreCase) ||
                    a.Contains("ExperimentalGatewayAPISupport=true", StringComparison.OrdinalIgnoreCase)) == true) == true;
        }
        catch
        {
            return false;
        }
    }

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }
}
