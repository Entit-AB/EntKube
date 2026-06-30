using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Adopts a workload that already runs in a cluster into EntKube management.
/// Scans one or more namespaces for the resource kinds EntKube supports,
/// presents a preview, then imports the selection as a new App + a
/// <see cref="DeploymentType.Yaml"/> deployment.
///
/// Three pieces of "smart" handling layered on top of a plain manifest copy:
///  - Secrets are imported into the tenant vault (with sync-back) instead of
///    being stored as raw manifests, so EntKube owns them.
///  - ExternalSecret CRs are used as hints for the target secret name + keys.
///  - Postgres connection strings are detected so the database (and, if needed,
///    the Postgres instance) can be adopted via <see cref="RegisteredPostgresService"/>.
///
/// Mirrors the scan→preview→import shape of <see cref="ComponentScanService"/>.
/// </summary>
public class DeploymentImportService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DeploymentService deploymentService,
    VaultService vaultService,
    RegisteredPostgresService postgresService,
    DockerRegistryService dockerRegistryService,
    AppRouteService appRouteService,
    ILogger<DeploymentImportService> logger)
{
    // ──────── Scan ────────

    /// <summary>
    /// Scans the given namespaces on a cluster and returns a preview of everything
    /// importable. Each resource lister is wrapped so a missing CRD or permission
    /// degrades gracefully (recorded as a warning) instead of aborting the scan.
    /// </summary>
    public async Task<ImportPreview> ScanAsync(
        KubernetesCluster cluster, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        ImportPreview preview = new()
        {
            ClusterId = cluster.Id,
            Namespaces = namespaces.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList()
        };

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            preview.Warnings.Add("This cluster has no stored kubeconfig, so it cannot be scanned.");
            return preview;
        }

        Kubernetes client = CreateClient(cluster.Kubeconfig);
        HashSet<string> boundPvNames = new(StringComparer.Ordinal);

        // Secrets EntKube already syncs from the vault to this cluster, keyed "name|namespace".
        // A live secret matching one of these is EntKube's own — recognize it instead of
        // re-importing it (covers secrets synced before the managed-by label existed).
        IReadOnlySet<string> managedTargets = await GetManagedSecretTargetsAsync(cluster, ct);

        foreach (string ns in preview.Namespaces)
        {
            await ScanNamespaceAsync(client, ns, preview, boundPvNames, managedTargets, ct);
        }

        // Pull in only the PersistentVolumes bound to the PVCs we discovered.
        // PVs are cluster-scoped and often bound/Retain, so they default to unselected.
        if (boundPvNames.Count > 0)
        {
            await ScanBoundPersistentVolumesAsync(client, boundPvNames, preview, ct);
        }

        DiscoveredResource? firstWorkload = preview.Resources.FirstOrDefault(r => r.Category == ImportCategory.Workload);
        preview.PrimaryNamespace = firstWorkload?.Namespace ?? preview.Namespaces.FirstOrDefault() ?? "default";

        await MatchPostgresInstancesAsync(cluster, preview, ct);

        preview.Resources = preview.Resources
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Namespace).ThenBy(r => r.Kind).ThenBy(r => r.Name)
            .ToList();

        preview.Warnings.Add(
            "Only the resource kinds EntKube supports are scanned. Any other resources in these namespaces are not imported.");

        return preview;
    }

    private async Task ScanNamespaceAsync(
        Kubernetes client, string ns, ImportPreview preview, HashSet<string> boundPvNames,
        IReadOnlySet<string> managedTargets, CancellationToken ct)
    {
        // ── Workloads ──
        await SafeAsync(preview, ns, "deployments", async () =>
        {
            V1DeploymentList list = await client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct);
            foreach (V1Deployment d in list.Items)
            {
                AddTyped(preview, d, "Deployment", "apps/v1", ns, d.Metadata.Name,
                    ImportCategory.Workload, 30, detail: $"{d.Spec?.Replicas ?? 1} replica(s)");
            }
        });

        await SafeAsync(preview, ns, "statefulsets", async () =>
        {
            V1StatefulSetList list = await client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct);
            foreach (V1StatefulSet s in list.Items)
            {
                AddTyped(preview, s, "StatefulSet", "apps/v1", ns, s.Metadata.Name,
                    ImportCategory.Workload, 30, detail: $"{s.Spec?.Replicas ?? 1} replica(s)");
            }
        });

        await SafeAsync(preview, ns, "daemonsets", async () =>
        {
            V1DaemonSetList list = await client.AppsV1.ListNamespacedDaemonSetAsync(ns, cancellationToken: ct);
            foreach (V1DaemonSet ds in list.Items)
            {
                AddTyped(preview, ds, "DaemonSet", "apps/v1", ns, ds.Metadata.Name,
                    ImportCategory.Workload, 30);
            }
        });

        // ── Config ──
        await SafeAsync(preview, ns, "configmaps", async () =>
        {
            V1ConfigMapList list = await client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct);
            foreach (V1ConfigMap cm in list.Items)
            {
                // Skip the cluster-injected CA bundle present in every namespace.
                if (cm.Metadata.Name == "kube-root-ca.crt")
                {
                    continue;
                }

                AddTyped(preview, cm, "ConfigMap", "v1", ns, cm.Metadata.Name,
                    ImportCategory.Config, 20, detail: $"{cm.Data?.Count ?? 0} key(s)");

                if (cm.Data is { Count: > 0 })
                {
                    DetectPostgres($"ConfigMap {cm.Metadata.Name}", cm.Data, preview);
                }
            }
        });

        // ── Storage ──
        await SafeAsync(preview, ns, "persistentvolumeclaims", async () =>
        {
            V1PersistentVolumeClaimList list = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct);
            foreach (V1PersistentVolumeClaim pvc in list.Items)
            {
                AddTyped(preview, pvc, "PersistentVolumeClaim", "v1", ns, pvc.Metadata.Name,
                    ImportCategory.Storage, 10, detail: pvc.Status?.Phase);

                if (!string.IsNullOrEmpty(pvc.Spec?.VolumeName))
                {
                    boundPvNames.Add(pvc.Spec.VolumeName);
                }
            }
        });

        // ── Networking ──
        await SafeAsync(preview, ns, "services", async () =>
        {
            V1ServiceList list = await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct);
            foreach (V1Service svc in list.Items)
            {
                // The default API service exists in every namespace's "default"/"kubernetes" — skip the well-known one.
                if (svc.Metadata.Name == "kubernetes" && ns == "default")
                {
                    continue;
                }

                AddTyped(preview, svc, "Service", "v1", ns, svc.Metadata.Name,
                    ImportCategory.Networking, 40, detail: svc.Spec?.Type);
            }
        });

        await SafeAsync(preview, ns, "ingresses", async () =>
        {
            V1IngressList list = await client.NetworkingV1.ListNamespacedIngressAsync(ns, cancellationToken: ct);
            foreach (V1Ingress ing in list.Items)
            {
                AddTyped(preview, ing, "Ingress", "networking.k8s.io/v1", ns, ing.Metadata.Name,
                    ImportCategory.Networking, 50);
            }
        });

        // HTTPRoutes become EntKube external-access routes (AppRoute), not raw manifests,
        // so the app shows external access and EntKube owns the regenerated HTTPRoute.
        foreach (JsonNode route in await ListCrAsync(client, "gateway.networking.k8s.io", ["v1", "v1beta1"], ns, "httproutes", ct))
        {
            AddRoute(route, ns, preview);
        }

        // ── Custom resources we understand (manifests) ──

        foreach (JsonNode so in await ListCrAsync(client, "keda.sh", ["v1alpha1"], ns, "scaledobjects", ct))
        {
            AddCrManifest(preview, so, "ScaledObject", ns, ImportCategory.CustomResource, 60);
        }

        foreach (JsonNode sj in await ListCrAsync(client, "keda.sh", ["v1alpha1"], ns, "scaledjobs", ct))
        {
            AddCrManifest(preview, sj, "ScaledJob", ns, ImportCategory.CustomResource, 60);
        }

        // ── Secrets + ExternalSecret hints (→ vault, never manifests) ──
        await ScanSecretsAsync(client, ns, preview, managedTargets, ct);
    }

    /// <summary>
    /// Lists plain Secrets and ExternalSecret CRs and reconciles them into
    /// <see cref="DetectedSecret"/> entries. An ExternalSecret's target Secret is
    /// preferred — the plain Secret it produces is not imported twice.
    /// </summary>
    private async Task ScanSecretsAsync(
        Kubernetes client, string ns, ImportPreview preview,
        IReadOnlySet<string> managedTargets, CancellationToken ct)
    {
        Dictionary<string, V1Secret> secretsByName = new(StringComparer.Ordinal);

        await SafeAsync(preview, ns, "secrets", async () =>
        {
            V1SecretList list = await client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: ct);
            foreach (V1Secret s in list.Items)
            {
                if (s.Type is "helm.sh/release.v1" or "kubernetes.io/service-account-token")
                {
                    continue;
                }
                secretsByName[s.Metadata.Name] = s;
            }
        });

        HashSet<string> esoTargets = new(StringComparer.Ordinal);

        foreach (JsonNode es in await ListCrAsync(client, "external-secrets.io", ["v1", "v1beta1"], ns, "externalsecrets", ct))
        {
            string esName = es["metadata"]?["name"]?.GetValue<string>() ?? "unknown";
            string targetName = es["spec"]?["target"]?["name"]?.GetValue<string>() ?? esName;
            esoTargets.Add(targetName);

            DetectedSecret detected = new() { SecretName = targetName, Namespace = ns, Source = "ExternalSecret" };

            // Key names come from spec.data[].secretKey; spec.dataFrom pulls whole secrets (keys unknown until materialized).
            if (es["spec"]?["data"] is JsonArray dataArr)
            {
                foreach (JsonNode? entry in dataArr)
                {
                    string? key = entry?["secretKey"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key))
                    {
                        detected.Keys.Add(new DetectedSecretKey { Key = key });
                    }
                }
            }

            bool hasDataFrom = es["spec"]?["dataFrom"] is JsonArray { Count: > 0 };

            // Fill values from the materialized target Secret if it exists.
            if (secretsByName.TryGetValue(targetName, out V1Secret? materialized) && materialized.Data is not null)
            {
                detected.Keys.Clear();
                foreach (KeyValuePair<string, byte[]> kv in materialized.Data)
                {
                    string value = Encoding.UTF8.GetString(kv.Value);
                    detected.Values[kv.Key] = value;
                    detected.Keys.Add(new DetectedSecretKey { Key = kv.Key, HasValue = value.Length > 0, Length = value.Length });
                }
                DetectPostgres($"ExternalSecret {targetName}", detected.Values, preview);
            }
            else if (hasDataFrom)
            {
                detected.Warning = "Uses dataFrom and the target Secret is not materialized yet — keys/values are unknown until the ExternalSecret syncs.";
            }
            else
            {
                detected.Warning = "Target Secret is not materialized yet — keys imported without values.";
            }

            preview.Secrets.Add(detected);
        }

        // Plain Secrets not already covered by an ExternalSecret target.
        foreach ((string name, V1Secret secret) in secretsByName)
        {
            // Covered by a discovered ExternalSecret hint above — don't import twice.
            if (esoTargets.Contains(name))
            {
                continue;
            }

            // Already an EntKube vault secret synced to this name/namespace — recognize it
            // rather than re-importing a duplicate (the live Secret may predate the
            // managed-by label, so we match against the vault's recorded sync targets).
            if (managedTargets.Contains($"{name}|{ns}"))
            {
                preview.SkippedSecrets.Add(new SkippedSecret
                {
                    Name = name,
                    Namespace = ns,
                    Reason = "already an EntKube vault secret (this is its Kubernetes sync target)"
                });
                continue;
            }

            // Image-pull secrets become registry credentials, not opaque vault secrets.
            // Detect by type OR by the presence of the dockerconfig data key — some charts
            // create the secret as Opaque (or untyped) with a .dockerconfigjson payload.
            // Checked before the controller-managed guard so a pull secret that carries an
            // ownerReference still becomes a registry credential (these never auto-push).
            bool isDockerConfig =
                secret.Type is "kubernetes.io/dockerconfigjson" or "kubernetes.io/dockercfg"
                || (secret.Data?.ContainsKey(".dockerconfigjson") ?? false)
                || (secret.Data?.ContainsKey(".dockercfg") ?? false);

            if (isDockerConfig)
            {
                AddRegistryCredentials(secret, name, ns, preview);
                continue;
            }

            // Skip secrets that another controller owns/derives (ExternalSecret target,
            // cert-manager Certificate, any ownerReference). These are recreated by their
            // controller, so EntKube must not adopt them — even if the CR wasn't discovered.
            if (IsControllerManaged(secret, out string reason))
            {
                preview.SkippedSecrets.Add(new SkippedSecret { Name = name, Namespace = ns, Reason = reason });
                continue;
            }

            DetectedSecret detected = new() { SecretName = name, Namespace = ns, Source = "Secret" };

            if (secret.Data is not null)
            {
                foreach (KeyValuePair<string, byte[]> kv in secret.Data)
                {
                    string value = Encoding.UTF8.GetString(kv.Value);
                    detected.Values[kv.Key] = value;
                    detected.Keys.Add(new DetectedSecretKey { Key = kv.Key, HasValue = value.Length > 0, Length = value.Length });
                }
            }

            if (secret.Type is not null && secret.Type != "Opaque")
            {
                detected.Warning = $"Original type was {secret.Type}; it will sync back as an Opaque Secret.";
            }

            DetectPostgres($"Secret {name}", detected.Values, preview);
            preview.Secrets.Add(detected);
        }
    }

    /// <summary>
    /// Returns the set of Kubernetes Secrets this tenant's vault already syncs to the
    /// cluster, keyed "secretName|namespace". A live Secret matching one of these is
    /// EntKube's own sync output, so the importer recognizes it instead of creating a
    /// duplicate vault secret. Reliable even for secrets synced before the managed-by
    /// label was introduced.
    /// </summary>
    private async Task<IReadOnlySet<string>> GetManagedSecretTargetsAsync(
        KubernetesCluster cluster, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<(string? Name, string? Namespace)> targets = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == cluster.TenantId
                && s.SyncToKubernetes
                && s.KubernetesSecretName != null
                && (s.KubernetesClusterId == cluster.Id || s.KubernetesClusterId == null))
            .Select(s => new ValueTuple<string?, string?>(s.KubernetesSecretName, s.KubernetesNamespace))
            .ToListAsync(ct);

        HashSet<string> set = new(StringComparer.Ordinal);
        foreach ((string? secretName, string? secretNs) in targets)
        {
            set.Add($"{secretName}|{secretNs ?? "default"}");
        }
        return set;
    }

    /// <summary>
    /// True when a Secret is owned or derived by another controller, so it should
    /// not be imported as a standalone vault secret. Detected from the secret's own
    /// metadata (no CR lookup required): any ownerReference, the External Secrets
    /// "managed" label, or the cert-manager certificate annotation.
    /// </summary>
    private static bool IsControllerManaged(V1Secret secret, out string reason)
    {
        reason = "";
        V1ObjectMeta? meta = secret.Metadata;

        // Already synced by EntKube itself (from the vault) — re-adopting would have
        // two apps/environments fighting over the same target Secret.
        if (meta?.Labels is not null
            && meta.Labels.TryGetValue(VaultService.ManagedByLabelKey, out string? managedBy)
            && string.Equals(managedBy, VaultService.ManagedByLabelValue, StringComparison.OrdinalIgnoreCase))
        {
            reason = "already managed by EntKube (synced from the vault)";
            return true;
        }

        if (meta?.OwnerReferences is { Count: > 0 } owners)
        {
            V1OwnerReference owner = owners[0];
            reason = $"managed by {owner.Kind} '{owner.Name}'";
            return true;
        }

        if (meta?.Labels is not null
            && meta.Labels.TryGetValue("reconcile.external-secrets.io/managed", out string? managed)
            && string.Equals(managed, "true", StringComparison.OrdinalIgnoreCase))
        {
            reason = "managed by an ExternalSecret (external-secrets.io)";
            return true;
        }

        if (meta?.Annotations is not null
            && meta.Annotations.TryGetValue("cert-manager.io/certificate-name", out string? certName))
        {
            reason = $"managed by cert-manager Certificate '{certName}'";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an image-pull Secret (<c>kubernetes.io/dockerconfigjson</c> or legacy
    /// <c>dockercfg</c>) into one <see cref="DetectedRegistryCredential"/> per registry
    /// under <c>auths</c>. Username/password come from the explicit fields, falling back
    /// to decoding the base64 <c>auth</c> (user:pass) entry.
    /// </summary>
    private static void AddRegistryCredentials(V1Secret secret, string secretName, string ns, ImportPreview preview)
    {
        if (secret.Data is null)
        {
            preview.Warnings.Add($"Image-pull secret '{secretName}' has no data — skipped.");
            return;
        }

        JsonNode? auths = null;
        try
        {
            if (secret.Data.TryGetValue(".dockerconfigjson", out byte[]? dcj))
            {
                auths = JsonNode.Parse(Encoding.UTF8.GetString(dcj))?["auths"];
            }
            else if (secret.Data.TryGetValue(".dockercfg", out byte[]? dc))
            {
                // Legacy format has no "auths" wrapper — the root IS the auths map.
                auths = JsonNode.Parse(Encoding.UTF8.GetString(dc));
            }
        }
        catch (Exception ex)
        {
            preview.Warnings.Add($"Image-pull secret '{secretName}' could not be parsed: {ex.Message}");
            return;
        }

        if (auths is not JsonObject entries || entries.Count == 0)
        {
            preview.Warnings.Add($"Image-pull secret '{secretName}' has no registry entries — skipped.");
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> kv in entries)
        {
            if (kv.Value is not JsonObject entry)
            {
                continue;
            }

            string? user = entry["username"]?.GetValue<string>();
            string? pass = entry["password"]?.GetValue<string>();
            string? email = entry["email"]?.GetValue<string>();

            if ((string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                && entry["auth"]?.GetValue<string>() is string auth && !string.IsNullOrEmpty(auth))
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth));
                    int colon = decoded.IndexOf(':');
                    if (colon > 0)
                    {
                        if (string.IsNullOrEmpty(user)) user = decoded[..colon];
                        if (string.IsNullOrEmpty(pass)) pass = decoded[(colon + 1)..];
                    }
                }
                catch
                {
                    // Malformed auth field — keep whatever explicit values we have.
                }
            }

            preview.RegistryCredentials.Add(new DetectedRegistryCredential
            {
                SecretName = secretName,
                Namespace = ns,
                Server = kv.Key,
                Username = user,
                Password = pass,
                Email = email,
                RegistryType = InferRegistryType(kv.Key)
            });
        }
    }

    /// <summary>
    /// Parses an HTTPRoute into one <see cref="DetectedRoute"/> per hostname (each
    /// carrying its path→backend rules). HTTPRoutes without a hostname or backend
    /// can't map to EntKube external access and are reported as warnings.
    /// </summary>
    private static void AddRoute(JsonNode httpRoute, string ns, ImportPreview preview)
    {
        string name = httpRoute["metadata"]?["name"]?.GetValue<string>() ?? "httproute";
        JsonNode? spec = httpRoute["spec"];
        if (spec is null)
        {
            return;
        }

        string? gatewayName = null;
        string? gatewayNamespace = null;
        if (spec["parentRefs"] is JsonArray parentRefs && parentRefs.Count > 0 && parentRefs[0] is JsonObject parent)
        {
            gatewayName = parent["name"]?.GetValue<string>();
            gatewayNamespace = parent["namespace"]?.GetValue<string>();
        }

        List<DetectedRouteRule> rules = [];
        if (spec["rules"] is JsonArray ruleArray)
        {
            foreach (JsonNode? ruleNode in ruleArray)
            {
                if (ruleNode is not JsonObject rule)
                {
                    continue;
                }

                DetectedRouteRule parsed = new();

                if (rule["matches"] is JsonArray matches && matches.Count > 0
                    && matches[0]?["path"] is JsonObject path
                    && path["value"]?.GetValue<string>() is string pathValue && !string.IsNullOrEmpty(pathValue))
                {
                    parsed.PathPrefix = pathValue;
                }

                if (rule["filters"] is JsonArray filters)
                {
                    foreach (JsonNode? filter in filters)
                    {
                        if (filter?["type"]?.GetValue<string>() == "URLRewrite")
                        {
                            parsed.RewritePath = filter["urlRewrite"]?["path"]?["replacePrefixMatch"]?.GetValue<string>();
                        }
                    }
                }

                if (rule["backendRefs"] is JsonArray backends && backends.Count > 0 && backends[0] is JsonObject backend)
                {
                    parsed.ServiceName = backend["name"]?.GetValue<string>();
                    if (backend["port"] is JsonValue portValue && portValue.TryGetValue(out int port))
                    {
                        parsed.ServicePort = port;
                    }
                }

                if (!string.IsNullOrEmpty(parsed.ServiceName))
                {
                    rules.Add(parsed);
                }
            }
        }

        if (rules.Count == 0)
        {
            preview.Warnings.Add($"HTTPRoute '{name}' has no usable backend rules — not imported as external access.");
            return;
        }

        List<string> hostnames = [];
        if (spec["hostnames"] is JsonArray hosts)
        {
            foreach (JsonNode? host in hosts)
            {
                if (host?.GetValue<string>() is string h && !string.IsNullOrEmpty(h))
                {
                    hostnames.Add(h);
                }
            }
        }

        if (hostnames.Count == 0)
        {
            preview.Warnings.Add($"HTTPRoute '{name}' has no hostnames — cannot map to EntKube external access; not imported.");
            return;
        }

        foreach (string hostname in hostnames)
        {
            preview.Routes.Add(new DetectedRoute
            {
                Hostname = hostname,
                Namespace = ns,
                GatewayName = gatewayName,
                GatewayNamespace = gatewayNamespace,
                SourceHttpRoute = name,
                Rules = rules.Select(r => new DetectedRouteRule
                {
                    PathPrefix = r.PathPrefix,
                    ServiceName = r.ServiceName,
                    ServicePort = r.ServicePort,
                    RewritePath = r.RewritePath
                }).ToList()
            });
        }
    }

    private static DockerRegistryType InferRegistryType(string server)
    {
        string s = server.ToLowerInvariant();
        if (s.Contains("docker.io")) return DockerRegistryType.DockerHub;
        if (s.Contains(".azurecr.io")) return DockerRegistryType.AzureContainerRegistry;
        if (s.Contains("ghcr.io")) return DockerRegistryType.GitHubContainerRegistry;
        if (s.Contains("quay.io")) return DockerRegistryType.Quay;
        if (s.Contains("harbor")) return DockerRegistryType.Harbor;
        return DockerRegistryType.Generic;
    }

    private async Task ScanBoundPersistentVolumesAsync(
        Kubernetes client, HashSet<string> boundPvNames, ImportPreview preview, CancellationToken ct)
    {
        await SafeAsync(preview, "(cluster)", "persistentvolumes", async () =>
        {
            V1PersistentVolumeList list = await client.CoreV1.ListPersistentVolumeAsync(cancellationToken: ct);
            foreach (V1PersistentVolume pv in list.Items)
            {
                if (!boundPvNames.Contains(pv.Metadata.Name))
                {
                    continue;
                }

                AddTyped(preview, pv, "PersistentVolume", "v1", "", pv.Metadata.Name,
                    ImportCategory.Storage, 0, selected: false,
                    detail: $"reclaim={pv.Spec?.PersistentVolumeReclaimPolicy}; re-applying a bound PV may be rejected");
            }
        });
    }

    // ──────── Import ────────

    /// <summary>
    /// Creates a new App + Yaml deployment from the (admin-edited) preview.
    /// Best-effort per item: a single resource/secret/postgres failure is collected
    /// into the result rather than aborting the whole import.
    /// </summary>
    public async Task<ImportResult> ImportAsync(KubernetesCluster cluster, ImportRequest request, CancellationToken ct = default)
    {
        Guid tenantId = cluster.TenantId;
        ImportPreview preview = request.Preview;
        ImportResult result = new() { AppName = request.AppName };

        // ── App: reuse if it already exists for this customer, else create. ──
        //    Apps span environments, and import runs one environment at a time, so
        //    a second-environment import must extend the existing app, not fail.
        Guid appId;
        bool appCreated;
        string envName;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            App? app = await db.Apps.FirstOrDefaultAsync(
                a => a.CustomerId == request.CustomerId && a.Name == request.AppName, ct);

            if (app is null)
            {
                app = new App
                {
                    Id = Guid.NewGuid(),
                    CustomerId = request.CustomerId,
                    Name = request.AppName,
                    Namespace = preview.PrimaryNamespace
                };
                db.Apps.Add(app);
                appCreated = true;
            }
            else
            {
                appCreated = false;
                app.Namespace ??= preview.PrimaryNamespace;
            }

            // Upsert the environment link, carrying the import's primary namespace.
            AppEnvironment? link = await db.AppEnvironments
                .FirstOrDefaultAsync(e => e.AppId == app.Id && e.EnvironmentId == request.EnvironmentId, ct);

            if (link is not null
                && !string.IsNullOrEmpty(link.Namespace)
                && !string.Equals(link.Namespace, preview.PrimaryNamespace, StringComparison.OrdinalIgnoreCase))
            {
                // The environment is namespace-locked to a different namespace than the
                // scanned workloads — importing here would target the wrong namespace.
                result.Errors.Add(
                    $"This app's '{request.AppName}' deployment in the selected environment is locked to namespace " +
                    $"'{link.Namespace}', but the scanned workloads are in '{preview.PrimaryNamespace}'. " +
                    "Choose the matching environment or adjust the namespace lock first.");
                return result;
            }

            if (link is null)
            {
                db.AppEnvironments.Add(new AppEnvironment
                {
                    AppId = app.Id,
                    EnvironmentId = request.EnvironmentId,
                    Namespace = preview.PrimaryNamespace
                });
            }
            else
            {
                link.Namespace = preview.PrimaryNamespace;
            }

            EntKube.Web.Data.Environment? environment = await db.Set<EntKube.Web.Data.Environment>()
                .FirstOrDefaultAsync(e => e.Id == request.EnvironmentId, ct);
            envName = environment?.Name ?? "env";

            await db.SaveChangesAsync(ct);
            appId = app.Id;
        }
        result.AppId = appId;
        result.AppCreated = appCreated;

        // ── Deployment: one per (app, environment, cluster). Reuse if present (update), else create. ──
        List<AppDeployment> existingDeployments = await deploymentService.GetDeploymentsAsync(appId, ct);
        AppDeployment? deployment = existingDeployments
            .FirstOrDefault(d => d.EnvironmentId == request.EnvironmentId && d.ClusterId == cluster.Id);

        HashSet<(string Kind, string Name)> existingManifestKeys = new();

        if (deployment is null)
        {
            string deployName = UniqueDeploymentName(existingDeployments, request.AppName, envName);
            deployment = await deploymentService.CreateDeploymentAsync(
                appId, deployName, DeploymentType.Yaml, request.EnvironmentId, cluster.Id,
                preview.PrimaryNamespace, performedBy: request.PerformedBy, ct: ct,
                // Observe-only on import — the live workload is likely owned by ArgoCD/Flux.
                isManaged: false);
            result.DeploymentCreated = true;
        }
        else
        {
            result.DeploymentCreated = false;
            foreach (DeploymentManifest m in await deploymentService.GetManifestsAsync(deployment.Id, ct))
            {
                existingManifestKeys.Add((m.Kind, m.Name));
            }
        }
        result.DeploymentId = deployment.Id;
        result.DeploymentName = deployment.Name;

        // ── Manifests (everything selected except secrets), skipping ones already present. ──
        foreach (DiscoveredResource res in preview.Resources.Where(r => r.Selected && r.Supported && !string.IsNullOrWhiteSpace(r.SanitizedYaml)))
        {
            if (existingManifestKeys.Contains((res.Kind, res.Name)))
            {
                result.ManifestSkipped++;
                continue;
            }

            try
            {
                await deploymentService.AddManifestAsync(deployment.Id, res.Kind, res.Name, res.SanitizedYaml, res.SortOrder, ct);
                existingManifestKeys.Add((res.Kind, res.Name));
                result.ManifestCount++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Manifest {res.Kind}/{res.Name}: {Detail(ex)}");
            }
        }

        // ── Secrets → vault (with sync-back) ──
        List<DetectedSecret> secretsToImport = preview.Secrets.Where(s => s.ImportToVault && s.Values.Count > 0).ToList();
        if (secretsToImport.Count > 0 || preview.RegistryCredentials.Any(r => r.Import))
        {
            // Registry credentials are also vault-backed, so ensure the vault exists.
            await vaultService.InitializeVaultAsync(tenantId, ct);
        }

        foreach (DetectedSecret secret in secretsToImport)
        {
            foreach (KeyValuePair<string, string> kv in secret.Values)
            {
                try
                {
                    VaultSecret stored = await vaultService.SetAppSecretAsync(
                        tenantId, appId, kv.Key, kv.Value, ct, request.EnvironmentId);

                    // Record the sync target (secret name/namespace/cluster) but leave sync
                    // DISABLED — the live Secret is likely owned by ArgoCD/Flux. EntKube holds
                    // the value in the vault (observe-only); the operator enables K8s sync per
                    // secret to take ownership, which re-reads the current live value first.
                    await vaultService.ConfigureKubernetesSyncAsync(
                        stored.Id, syncEnabled: false, secret.SecretName, secret.Namespace, ct, cluster.Id);

                    result.SecretKeyCount++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Secret {secret.SecretName}/{kv.Key}: {Detail(ex)}");
                }
            }
            result.SecretCount++;
        }

        // ── Image-pull secrets → registry credentials (sync back as dockerconfigjson) ──
        foreach (DetectedRegistryCredential reg in preview.RegistryCredentials.Where(r => r.Import))
        {
            try
            {
                DockerRegistryCredential cred = await dockerRegistryService.CreateAsync(
                    tenantId, appId,
                    name: reg.SecretName,
                    registryType: reg.RegistryType,
                    server: reg.Server,
                    username: reg.Username ?? "",
                    password: reg.Password ?? "",
                    email: reg.Email,
                    ct);

                await dockerRegistryService.ConfigureSyncAsync(cred.Id, cluster.Id, reg.SecretName, reg.Namespace, ct);
                result.RegistryCredentialCount++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Registry credential {reg.SecretName} ({reg.Server}): {Detail(ex)}");
            }
        }

        // ── HTTPRoutes → EntKube external access (AppRoute + AppDeploymentRoute) ──
        List<DetectedRoute> routesToImport = preview.Routes.Where(r => r.Import).ToList();
        if (routesToImport.Count > 0)
        {
            // Reuse an AppRoute if the hostname already exists on this app (one AppRoute
            // per hostname; multiple deployments/paths attach as AppDeploymentRoutes).
            Dictionary<string, Guid> routeIdByHost = new(StringComparer.OrdinalIgnoreCase);
            foreach (AppRoute existing in await appRouteService.GetRoutesForAppAsync(appId, ct))
            {
                routeIdByHost[existing.Hostname] = existing.Id;
            }

            foreach (DetectedRoute route in routesToImport)
            {
                try
                {
                    if (!routeIdByHost.TryGetValue(route.Hostname, out Guid routeId))
                    {
                        AppRoute created = await appRouteService.AddRouteAsync(appId, new AppRouteRequest
                        {
                            Hostname = route.Hostname,
                            TlsMode = route.TlsMode,
                            ClusterIssuerName = route.TlsMode == TlsMode.ClusterIssuer ? route.ClusterIssuerName : null,
                            // Imported routes start observe-only — the live HTTPRoute is likely
                            // owned by ArgoCD/Flux. The user enables management to take ownership.
                            IsManaged = false
                        }, ct);
                        routeId = created.Id;
                        routeIdByHost[route.Hostname] = routeId;
                        result.RouteCount++;
                    }

                    foreach (DetectedRouteRule rule in route.Rules)
                    {
                        try
                        {
                            await appRouteService.AddDeploymentRouteAsync(routeId, deployment.Id, new AppDeploymentRouteRequest
                            {
                                ServiceName = rule.ServiceName ?? "",
                                ServicePort = rule.ServicePort,
                                PathPrefix = rule.PathPrefix,
                                RewritePath = rule.RewritePath
                            }, ct);
                            result.RouteRuleCount++;
                        }
                        catch (Exception ex)
                        {
                            // A duplicate path prefix on the same hostname is non-fatal.
                            result.Warnings.Add($"Route {route.Hostname} {rule.PathPrefix}: {Detail(ex)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Route {route.Hostname}: {Detail(ex)}");
                }
            }
        }

        // ── Postgres adoption ──
        foreach (DetectedPostgres pg in preview.PostgresConnections.Where(p => p.Action != PostgresImportAction.Skip))
        {
            await AdoptPostgresAsync(cluster, deployment, pg, result, ct);
        }

        logger.LogInformation(
            "Imported app {App} from cluster {Cluster}: {Manifests} manifests, {Secrets} secrets, {Errors} errors",
            request.AppName, cluster.Name, result.ManifestCount, result.SecretCount, result.Errors.Count);

        return result;
    }

    private async Task AdoptPostgresAsync(
        KubernetesCluster cluster, AppDeployment deployment, DetectedPostgres pg, ImportResult result, CancellationToken ct)
    {
        Guid tenantId = cluster.TenantId;

        if (string.IsNullOrWhiteSpace(pg.Database) || string.IsNullOrWhiteSpace(pg.Username))
        {
            result.Warnings.Add($"Postgres {pg.Source}: missing database or username — skipped.");
            return;
        }

        try
        {
            Guid instanceId;

            if (pg.Action == PostgresImportAction.RegisterAndImport)
            {
                if (string.IsNullOrWhiteSpace(pg.AdminPodName) || string.IsNullOrWhiteSpace(pg.AdminPassword)
                    || string.IsNullOrWhiteSpace(pg.ServiceName))
                {
                    result.Warnings.Add($"Postgres {pg.Source}: register requires admin pod, admin password, and service name — skipped.");
                    return;
                }

                RegisteredPostgresInstance instance = await postgresService.RegisterInstanceAsync(
                    tenantId, cluster.Id,
                    name: pg.ServiceName!,
                    ns: pg.RegisterNamespace ?? deployment.Namespace,
                    serviceName: pg.ServiceName!,
                    port: pg.Port,
                    adminPodName: pg.AdminPodName!,
                    adminUsername: pg.AdminUsername ?? "postgres",
                    adminPassword: pg.AdminPassword!,
                    notes: $"Registered during import of {result.AppName}",
                    testConnection: false,
                    ct: ct);
                instanceId = instance.Id;
            }
            else // ImportDatabase
            {
                if (pg.MatchedInstanceId is null)
                {
                    result.Warnings.Add($"Postgres {pg.Source}: no registered instance matched — skipped.");
                    return;
                }
                instanceId = pg.MatchedInstanceId.Value;
            }

            RegisteredPostgresDatabase database = await postgresService.ImportDatabaseAsync(
                tenantId, instanceId, pg.Database!, pg.Username!, pg.Password ?? "", ct);

            using ApplicationDbContext db = dbFactory.CreateDbContext();
            db.DatabaseBindings.Add(new DatabaseBinding
            {
                Id = Guid.NewGuid(),
                RegisteredPostgresDatabaseId = database.Id,
                AppDeploymentId = deployment.Id,
                KubernetesSecretName = pg.BindingSecretName ?? $"{pg.Database}-db",
                // Observe-only on import — don't push a credentials Secret that may clash
                // with a GitOps-managed one until the operator opts in.
                SyncEnabled = false
            });
            await db.SaveChangesAsync(ct);

            result.PostgresOutcomes.Add($"Adopted database '{pg.Database}' and bound it to the deployment.");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Postgres {pg.Source}: {Detail(ex)}");
        }
    }

    // ──────── Helpers ────────

    private void AddTyped(
        ImportPreview preview, object item, string kind, string apiVersion, string ns, string name,
        ImportCategory category, int sortOrder, bool selected = true, string? detail = null)
    {
        string yaml;
        try
        {
            string json = KubernetesJson.Serialize(item);
            JsonNode node = JsonNode.Parse(json)!;
            node["apiVersion"] = apiVersion;
            node["kind"] = kind;
            yaml = ImportManifestSanitizer.ToYaml(node);
        }
        catch (Exception ex)
        {
            preview.Warnings.Add($"Could not serialize {kind}/{name}: {ex.Message}");
            return;
        }

        preview.Resources.Add(new DiscoveredResource
        {
            Kind = kind,
            Name = name,
            Namespace = ns,
            Category = category,
            Supported = true,
            Selected = selected,
            Detail = detail,
            SanitizedYaml = yaml,
            SortOrder = sortOrder
        });
    }

    private void AddCrManifest(
        ImportPreview preview, JsonNode item, string kind, string ns, ImportCategory category, int sortOrder)
    {
        string name = item["metadata"]?["name"]?.GetValue<string>() ?? "unknown";
        string yaml;
        try
        {
            yaml = ImportManifestSanitizer.ToYaml(item);
        }
        catch (Exception ex)
        {
            preview.Warnings.Add($"Could not serialize {kind}/{name}: {ex.Message}");
            return;
        }

        preview.Resources.Add(new DiscoveredResource
        {
            Kind = kind,
            Name = name,
            Namespace = ns,
            Category = category,
            Supported = true,
            Selected = true,
            SanitizedYaml = yaml,
            SortOrder = sortOrder
        });
    }

    /// <summary>
    /// Lists a namespaced custom resource, trying each candidate API version in
    /// turn. Returns the raw item nodes, or an empty list if the CRD is absent.
    /// </summary>
    private async Task<List<JsonNode>> ListCrAsync(
        Kubernetes client, string group, string[] versions, string ns, string plural, CancellationToken ct)
    {
        foreach (string version in versions)
        {
            try
            {
                object raw = await client.CustomObjects.ListNamespacedCustomObjectAsync(group, version, ns, plural, cancellationToken: ct);
                string json = JsonSerializer.Serialize(raw);
                JsonNode? root = JsonNode.Parse(json);

                if (root?["items"] is JsonArray items)
                {
                    List<JsonNode> result = [];
                    foreach (JsonNode? node in items)
                    {
                        if (node is not null)
                        {
                            result.Add(node);
                        }
                    }
                    return result;
                }
            }
            catch
            {
                // CRD/version not present or no permission — try the next version.
            }
        }

        return [];
    }

    /// <summary>
    /// Inspects key/value pairs for a Postgres connection (a postgres:// URL or a
    /// conventional HOST/PORT/USER/PASSWORD/DB key group) and records any new
    /// connection, deduped by host+database+user.
    /// </summary>
    private static void DetectPostgres(string source, IDictionary<string, string> values, ImportPreview preview)
    {
        foreach (KeyValuePair<string, string> kv in values)
        {
            DetectedPostgres? fromUrl = TryParsePostgresUrl(kv.Value);
            if (fromUrl is not null)
            {
                fromUrl.Source = $"{source} / {kv.Key}";
                AddPostgres(preview, fromUrl);
            }
        }

        // Key-group heuristic across the whole secret/configmap.
        string? host = FindBySuffix(values, "PGHOST", "DB_HOST", "POSTGRES_HOST", "DATABASE_HOST", "HOST");
        string? db = FindBySuffix(values, "PGDATABASE", "POSTGRES_DB", "DB_NAME", "DATABASE_NAME", "DATABASE", "DB");
        string? user = FindBySuffix(values, "PGUSER", "POSTGRES_USER", "DB_USER", "DATABASE_USER", "USER", "USERNAME");
        string? password = FindBySuffix(values, "PGPASSWORD", "POSTGRES_PASSWORD", "DB_PASSWORD", "DATABASE_PASSWORD", "PASSWORD");
        string? portStr = FindBySuffix(values, "PGPORT", "DB_PORT", "POSTGRES_PORT", "DATABASE_PORT", "PORT");

        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(db) && !string.IsNullOrWhiteSpace(user))
        {
            DetectedPostgres conn = new()
            {
                Source = $"{source} (env keys)",
                Host = host,
                Database = db,
                Username = user,
                Password = password,
                Port = int.TryParse(portStr, out int p) ? p : 5432
            };
            AddPostgres(preview, conn);
        }
    }

    private static DetectedPostgres? TryParsePostgresUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !(value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            Uri uri = new(value);
            string[] userInfo = uri.UserInfo.Split(':', 2);
            string database = uri.AbsolutePath.TrimStart('/');

            if (string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(database))
            {
                return null;
            }

            return new DetectedPostgres
            {
                Source = "url",
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : null,
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
                Database = database
            };
        }
        catch
        {
            return null;
        }
    }

    private static void AddPostgres(ImportPreview preview, DetectedPostgres conn)
    {
        string key = $"{conn.Host}|{conn.Database}|{conn.Username}";
        if (preview.PostgresConnections.Any(c => $"{c.Host}|{c.Database}|{c.Username}" == key))
        {
            return;
        }
        preview.PostgresConnections.Add(conn);
    }

    private static string? FindBySuffix(IDictionary<string, string> values, params string[] suffixes)
    {
        foreach (string suffix in suffixes)
        {
            foreach (KeyValuePair<string, string> kv in values)
            {
                if (kv.Key.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value))
                    {
                        return kv.Value;
                    }
                }
            }
        }
        return null;
    }

    private async Task MatchPostgresInstancesAsync(KubernetesCluster cluster, ImportPreview preview, CancellationToken ct)
    {
        if (preview.PostgresConnections.Count == 0)
        {
            return;
        }

        List<RegisteredPostgresInstance> instances = await postgresService.GetInstancesAsync(cluster.TenantId, ct);

        foreach (DetectedPostgres conn in preview.PostgresConnections)
        {
            RegisteredPostgresInstance? match = instances.FirstOrDefault(i =>
                i.KubernetesClusterId == cluster.Id && HostMatches(conn.Host, i));

            if (match is not null)
            {
                conn.MatchedInstanceId = match.Id;
                conn.MatchedInstanceName = match.Name;
                conn.Action = PostgresImportAction.ImportDatabase;
            }

            conn.RegisterNamespace ??= preview.PrimaryNamespace;
            conn.ServiceName ??= conn.Host?.Split('.').FirstOrDefault();
            conn.BindingSecretName ??= string.IsNullOrWhiteSpace(conn.Database) ? "imported-db" : $"{conn.Database}-db";
        }
    }

    private static bool HostMatches(string? host, RegisteredPostgresInstance instance)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string svc = instance.ServiceName;
        string ns = instance.Namespace;
        string[] candidates =
        [
            svc,
            $"{svc}.{ns}",
            $"{svc}.{ns}.svc",
            $"{svc}.{ns}.svc.cluster.local"
        ];

        return candidates.Any(c => string.Equals(c, host, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SafeAsync(ImportPreview preview, string ns, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            preview.Warnings.Add($"[{ns}] {what}: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks a deployment name unique within the app. Prefers the app name; if a
    /// deployment already uses it (e.g. another environment's import), qualifies
    /// with the environment name, then a numeric suffix as a last resort.
    /// </summary>
    private static string UniqueDeploymentName(IEnumerable<AppDeployment> existing, string appName, string envName)
    {
        HashSet<string> taken = existing.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(appName))
        {
            return appName;
        }

        string envToken = envName.Trim().ToLowerInvariant().Replace(' ', '-');
        string candidate = $"{appName}-{envToken}";

        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        int n = 2;
        while (taken.Contains($"{candidate}-{n}"))
        {
            n++;
        }
        return $"{candidate}-{n}";
    }

    private static string Detail(Exception ex) => ex.InnerException?.Message ?? ex.Message;

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }
}
