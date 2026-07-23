using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Registers a catalog component onto a cluster from a set of form-field values —
/// the same operation the Components tab performs when an operator picks a catalog
/// entry, fills the form and clicks "Add". It merges form values into the Helm
/// values YAML, stores secret-marked fields in the vault, and wires up the
/// component-specific side configuration (Keycloak DB, Harbor DB/S3, Loki/Mimir/
/// Tempo S3, plus their external routes).
///
/// Both the interactive UI (ClusterDetail) and the blueprint bootstrap runner call
/// this so a bootstrapped component is configured identically to a hand-added one.
/// Registration only creates the component record + side config; the actual Helm
/// install is driven separately by <see cref="ComponentInstallOrchestrator"/>.
/// </summary>
public class CatalogComponentRegistrar(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ComponentLifecycleService lifecycleService,
    VaultService vaultService,
    KeycloakService keycloakService,
    HarborService harborService,
    OpenLdapService openLdapService,
    LokiService lokiService,
    MimirService mimirService,
    TempoService tempoService,
    ExternalRouteService routeService)
{
    /// <summary>
    /// Registers (or, if already present, re-configures) the given catalog entry on a
    /// cluster using the supplied form-field values (keyed by
    /// <see cref="ComponentFormField.Key"/>). This is an <b>upsert</b>: a fresh
    /// bootstrap creates the component, while a blueprint-update run re-applies the
    /// latest values/version to the existing component (which is then upgraded in place
    /// by the install orchestrator's <c>helm upgrade --install</c>). Optional overrides
    /// let a blueprint supply an environment-specific namespace / release name.
    /// Returns the component record.
    /// </summary>
    public async Task<ClusterComponent> ApplyAsync(
        Guid clusterId,
        Guid tenantId,
        CatalogEntry entry,
        IReadOnlyDictionary<string, string> formValues,
        string? namespaceOverride = null,
        string? releaseNameOverride = null,
        CancellationToken ct = default)
    {
        // Load the currently-installed components so value merging can resolve
        // cluster-wide facts (e.g. the ingress Gateway for letsencrypt-issuer), and
        // so we can detect whether this component already exists (update vs create).
        List<ClusterComponent> existing;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            existing = await db.ClusterComponents
                .Where(c => c.ClusterId == clusterId)
                .ToListAsync(ct);
        }

        // The component's stable identity on a cluster is its Name, which
        // ToRegistration sets to the catalog key.
        ClusterComponent? current = existing.FirstOrDefault(c => c.Name == entry.Key);

        string mergedValues = MergeFormValues(entry, formValues, existing);
        string? helmValues = string.IsNullOrWhiteSpace(mergedValues) ? null : mergedValues;

        ClusterComponent component;
        if (current is not null)
        {
            // Update path — refresh values + chart version/repo to the blueprint's latest.
            component = await lifecycleService.UpdateConfigurationAsync(
                current.Id, helmValues, entry.HelmChartVersion, entry.HelmRepoUrl, ct: ct);
        }
        else
        {
            ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);
            if (!string.IsNullOrWhiteSpace(namespaceOverride))
            {
                registration.Namespace = namespaceOverride.Trim();
            }
            if (!string.IsNullOrWhiteSpace(releaseNameOverride))
            {
                registration.ReleaseName = releaseNameOverride.Trim();
            }
            registration.HelmValues = helmValues;
            component = await lifecycleService.RegisterComponentAsync(clusterId, registration, ct);
        }

        await SaveSecretFieldsToVaultAsync(tenantId, component.Id, entry, formValues, component.Namespace);
        await SaveKeycloakConfigIfNeededAsync(tenantId, component.Id, formValues, entry);
        await SaveHarborConfigIfNeededAsync(tenantId, component.Id, formValues, entry);
        await SaveOpenLdapConfigIfNeededAsync(tenantId, component.Id, formValues, entry);
        await SaveLokiConfigIfNeededAsync(tenantId, component.Id, formValues, entry);
        await SaveMimirConfigIfNeededAsync(tenantId, component.Id, formValues, entry);
        await SaveTempoConfigIfNeededAsync(tenantId, component.Id, formValues, entry);

        return component;
    }

    /// <summary>
    /// Builds the merged Helm values YAML from a catalog entry's form fields.
    /// Secret fields are excluded (they live in the vault); subchart toggles become
    /// comment markers; cnpg:/harbor:/loki: pseudo-paths are handled by side config.
    /// Mirrors the Components tab's merge so blueprint installs match manual ones.
    /// </summary>
    public static string MergeFormValues(
        CatalogEntry entry,
        IReadOnlyDictionary<string, string> formValues,
        IReadOnlyList<ClusterComponent> existingComponents)
    {
        string baseYaml = entry.DefaultValues ?? "";

        Dictionary<string, string> pathValues = new();
        List<string> subchartMarkers = [];

        foreach (ComponentFormField field in entry.FormFields)
        {
            if (field.StoreAsSecret)
            {
                continue;
            }

            if (field.YamlPath.StartsWith("subchart:", StringComparison.Ordinal))
            {
                string val = formValues.TryGetValue(field.Key, out string? v) ? v : field.DefaultValue ?? "true";
                subchartMarkers.Add($"# {field.YamlPath}={val}");
                continue;
            }

            if (field.YamlPath.StartsWith("cnpg:", StringComparison.Ordinal)
                || field.YamlPath.StartsWith("harbor:", StringComparison.Ordinal)
                || field.YamlPath.StartsWith("loki:", StringComparison.Ordinal)
                || field.YamlPath.StartsWith("ldap:", StringComparison.Ordinal))
            {
                continue;
            }

            // Fields with no YAML path are handled elsewhere (e.g. LetsEncryptSolverBuilder).
            if (string.IsNullOrEmpty(field.YamlPath))
            {
                continue;
            }

            if (formValues.TryGetValue(field.Key, out string? fieldVal) && !string.IsNullOrEmpty(fieldVal))
            {
                pathValues[field.YamlPath] = fieldVal;
            }
        }

        string result = pathValues.Count == 0 ? baseYaml : YamlFormMerger.MergeFormValues(baseYaml, pathValues);

        if (subchartMarkers.Count > 0)
        {
            result = string.Join("\n", subchartMarkers) + "\n" + result;
        }

        // For letsencrypt-issuer, build the ACME solvers (HTTP-01 and/or DNS-01 across
        // the selected provider) from the form selections.
        if (entry.Key == LetsEncryptSolverBuilder.CatalogKey)
        {
            result = LetsEncryptSolverBuilder.Apply(result, formValues, existingComponents);
        }

        return result;
    }

    private async Task SaveSecretFieldsToVaultAsync(
        Guid tenantId, Guid componentId, CatalogEntry entry,
        IReadOnlyDictionary<string, string> formValues, string? componentNamespace)
    {
        if (!entry.FormFields.Any(f => f.StoreAsSecret))
        {
            return;
        }

        await vaultService.InitializeVaultAsync(tenantId);

        foreach (ComponentFormField field in entry.FormFields)
        {
            if (!field.StoreAsSecret)
            {
                continue;
            }

            if (formValues.TryGetValue(field.Key, out string? val) && !string.IsNullOrEmpty(val))
            {
                string secretName = field.SecretName ?? field.Key;
                string? k8sNs = field.KubernetesSecretNamespace ?? componentNamespace;
                await vaultService.SetComponentSecretAsync(tenantId, componentId, secretName, val,
                    k8sSecretName: field.KubernetesSecretName, k8sNamespace: k8sNs);
            }
        }
    }

    private async Task SaveKeycloakConfigIfNeededAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> fieldValues, CatalogEntry catalogEntry)
    {
        // Skip if no CnpgDatabase field, or if this is a Harbor component (has StorageLink field).
        if (!catalogEntry.FormFields.Any(f => f.Type == FormFieldType.CnpgDatabase)
            || catalogEntry.FormFields.Any(f => f.Type == FormFieldType.StorageLink))
        {
            return;
        }

        // Write DB connection details as soon as a database is selected — no admin password required.
        if (fieldValues.TryGetValue("cnpg-database", out string? dbIdStr)
            && Guid.TryParse(dbIdStr, out Guid parsedDbId)
            && parsedDbId != Guid.Empty)
        {
            await keycloakService.WriteDatabaseHelmValuesAsync(tenantId, componentId, parsedDbId);
        }

        if (!fieldValues.TryGetValue("admin-password", out string? adminPassword)
            || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        Guid? cnpgDatabaseId = null;
        if (fieldValues.TryGetValue("cnpg-database", out string? dbIdStr2)
            && Guid.TryParse(dbIdStr2, out Guid parsedDbId2)
            && parsedDbId2 != Guid.Empty)
        {
            cnpgDatabaseId = parsedDbId2;
        }

        string hostname = fieldValues.TryGetValue("hostname", out string? h) ? h.Trim() : "";
        string httpPath = fieldValues.TryGetValue("http-path", out string? p) && !string.IsNullOrWhiteSpace(p) ? p : "/auth";
        string adminUrl = fieldValues.TryGetValue("admin-url", out string? u) && !string.IsNullOrWhiteSpace(u)
            ? u
            : string.IsNullOrEmpty(hostname) ? "" : $"https://{hostname}{httpPath}";
        string adminUsername = fieldValues.TryGetValue("admin-username", out string? n) && !string.IsNullOrWhiteSpace(n) ? n : "admin";

        await keycloakService.ConfigureAsync(tenantId, componentId, cnpgDatabaseId, adminUsername, adminPassword, adminUrl);

        if (!string.IsNullOrEmpty(hostname))
        {
            await EnsureRouteAsync(componentId, fieldValues, hostname, "keycloak", isKeycloak: true);
        }
    }

    private async Task SaveHarborConfigIfNeededAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> fieldValues, CatalogEntry catalogEntry)
    {
        if (catalogEntry.Key != "harbor")
        {
            return;
        }

        Guid? cnpgDatabaseId = null;
        if (fieldValues.TryGetValue("cnpg-database", out string? dbIdStr)
            && Guid.TryParse(dbIdStr, out Guid parsedDbId) && parsedDbId != Guid.Empty)
        {
            cnpgDatabaseId = parsedDbId;
        }

        Guid? storageLinkId = null;
        if (fieldValues.TryGetValue("storage-link", out string? slIdStr)
            && Guid.TryParse(slIdStr, out Guid parsedSlId) && parsedSlId != Guid.Empty)
        {
            storageLinkId = parsedSlId;
        }

        fieldValues.TryGetValue("admin-password", out string? adminPassword);
        fieldValues.TryGetValue("admin-username", out string? adminUsername);
        string hostname = fieldValues.TryGetValue("hostname", out string? h) ? h.Trim() : "";
        string registryUrl = string.IsNullOrEmpty(hostname) ? "" : $"https://{hostname}";

        await harborService.ConfigureAsync(
            tenantId, componentId,
            cnpgDatabaseId, storageLinkId,
            string.IsNullOrWhiteSpace(adminUsername) ? "admin" : adminUsername,
            string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword,
            string.IsNullOrWhiteSpace(registryUrl) ? null : registryUrl);

        if (!string.IsNullOrEmpty(registryUrl))
        {
            ClusterComponent? comp = await GetComponentAsync(componentId);
            if (comp is not null)
            {
                string harborReleaseName = comp.ReleaseName ?? comp.Name;
                string updated = YamlFormMerger.MergeFormValues(
                    comp.HelmValues ?? "",
                    new Dictionary<string, string>
                    {
                        ["externalURL"] = registryUrl,
                        ["expose.clusterIP.name"] = harborReleaseName
                    });
                await lifecycleService.UpdateConfigurationAsync(componentId, updated);
            }

            await EnsureRouteAsync(componentId, fieldValues, hostname, "harbor", isKeycloak: false);
        }
    }

    private async Task SaveOpenLdapConfigIfNeededAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> fieldValues, CatalogEntry catalogEntry)
    {
        if (catalogEntry.Key != OpenLdapService.CatalogKey)
        {
            return;
        }

        string baseDn = fieldValues.TryGetValue("base-dn", out string? b) && !string.IsNullOrWhiteSpace(b) ? b.Trim() : "dc=example,dc=com";
        string org = fieldValues.TryGetValue("organization", out string? o) && !string.IsNullOrWhiteSpace(o) ? o.Trim() : "EntKube";
        string tlsMode = fieldValues.TryGetValue("tls-mode", out string? t) && !string.IsNullOrWhiteSpace(t) ? t : "ClusterIssuer";
        string? issuer = fieldValues.TryGetValue("cluster-issuer", out string? i) && !string.IsNullOrWhiteSpace(i) ? i : null;
        int replicas = fieldValues.TryGetValue("replica-count", out string? r) && int.TryParse(r, out int rp) && rp > 0 ? rp : 1;
        string storage = fieldValues.TryGetValue("storage-size", out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : "8Gi";
        fieldValues.TryGetValue("admin-password", out string? adminPassword);
        fieldValues.TryGetValue("config-password", out string? configPassword);

        OpenLdapTlsMode mode = tlsMode switch
        {
            "Off" => OpenLdapTlsMode.Off,
            "Manual" => OpenLdapTlsMode.Manual,
            _ => OpenLdapTlsMode.ClusterIssuer,
        };

        await openLdapService.ConfigureAsync(
            tenantId, componentId,
            cfg =>
            {
                cfg.BaseDn = baseDn;
                cfg.Organization = org;
                cfg.TlsMode = mode;
                cfg.ClusterIssuer = mode == OpenLdapTlsMode.ClusterIssuer ? issuer : null;
                cfg.ReplicaCount = replicas;
                cfg.ReplicationEnabled = replicas > 1;
                cfg.StorageSize = storage;
            },
            string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword,
            string.IsNullOrWhiteSpace(configPassword) ? null : configPassword);
    }

    private async Task SaveLokiConfigIfNeededAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> fieldValues, CatalogEntry catalogEntry)
    {
        if (catalogEntry.Key != "loki") return;
        if (TryGetStorageLink(fieldValues, out Guid storageLinkId))
        {
            await lokiService.WriteStorageHelmValuesAsync(tenantId, componentId, storageLinkId);
        }
    }

    private async Task SaveMimirConfigIfNeededAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> fieldValues, CatalogEntry catalogEntry)
    {
        if (catalogEntry.Key != "mimir") return;
        if (TryGetStorageLink(fieldValues, out Guid storageLinkId))
        {
            await mimirService.WriteStorageHelmValuesAsync(tenantId, componentId, storageLinkId);
        }
    }

    private async Task SaveTempoConfigIfNeededAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> fieldValues, CatalogEntry catalogEntry)
    {
        if (catalogEntry.Key != "tempo") return;
        if (TryGetStorageLink(fieldValues, out Guid storageLinkId))
        {
            await tempoService.WriteStorageHelmValuesAsync(tenantId, componentId, storageLinkId);
        }
    }

    private static bool TryGetStorageLink(IReadOnlyDictionary<string, string> fieldValues, out Guid storageLinkId)
    {
        storageLinkId = Guid.Empty;
        return fieldValues.TryGetValue("storage-link", out string? slIdStr)
            && Guid.TryParse(slIdStr, out storageLinkId)
            && storageLinkId != Guid.Empty;
    }

    /// <summary>
    /// Creates an HTTPRoute + Certificate for the component's public hostname if one
    /// doesn't already exist. Shared by the Keycloak and Harbor config paths — the
    /// only difference is the backing service name derived from the release name.
    /// </summary>
    private async Task EnsureRouteAsync(
        Guid componentId, IReadOnlyDictionary<string, string> fieldValues, string hostname,
        string fallbackReleaseName, bool isKeycloak)
    {
        List<ExternalRoute> existing = await routeService.GetRoutesAsync(componentId);
        if (existing.Any(r => r.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string tlsMode = fieldValues.TryGetValue("tls-mode", out string? t) ? t : "ClusterIssuer";
        string issuerName = fieldValues.TryGetValue("cluster-issuer", out string? i) && !string.IsNullOrWhiteSpace(i) ? i : "letsencrypt-prod";
        string tlsCert = fieldValues.TryGetValue("tls-cert", out string? c) ? c : "";
        string tlsKey = fieldValues.TryGetValue("tls-key", out string? k) ? k : "";
        bool isManual = string.Equals(tlsMode, "Manual", StringComparison.Ordinal);

        ClusterComponent? comp = await GetComponentAsync(componentId);
        string releaseName = comp?.ReleaseName ?? comp?.Name ?? fallbackReleaseName;

        // keycloakx chart exposes {releaseName}-keycloakx-http; Harbor exposes the release name directly.
        string serviceName = isKeycloak ? $"{releaseName}-keycloakx-http" : releaseName;

        ExternalRouteRequest routeRequest = new()
        {
            Hostname = hostname,
            ServiceName = serviceName,
            ServicePort = 80,
            PathPrefix = "/",
            TlsMode = isManual ? TlsMode.Manual : TlsMode.ClusterIssuer,
            ClusterIssuerName = isManual ? null : issuerName,
            TlsCertificate = isManual ? tlsCert : null,
            TlsPrivateKey = isManual && !string.IsNullOrWhiteSpace(tlsKey) ? tlsKey : null
        };

        await routeService.AddRouteAsync(componentId, routeRequest);
    }

    private async Task<ClusterComponent?> GetComponentAsync(Guid componentId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents.FirstOrDefaultAsync(c => c.Id == componentId);
    }
}
