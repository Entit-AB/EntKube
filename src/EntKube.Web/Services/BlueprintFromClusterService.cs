using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Generates a <see cref="ClusterBlueprint"/> from an already-running cluster by
/// reading its installed catalog components and provisioned services and backing
/// out the parameters that produced them. This is the inverse of the install path
/// (<see cref="CatalogComponentRegistrar.MergeFormValues"/> +
/// <see cref="BootstrapRunnerService"/>'s service create-args).
///
/// Environment-specific values (cluster issuer, domains/URLs, storage links,
/// database references) are auto-detected and rewritten as <c>${var}</c> tokens,
/// with the source cluster's environment seeded into a per-blueprint variable so
/// the generated blueprint reproduces exactly here and stays portable elsewhere.
///
/// v1 scope: secrets are never copied (they stay per-environment in the vault);
/// subchart toggles fall back to catalog defaults; side-config recovery covers the
/// common non-secret Keycloak/Harbor keys — anything else falls back to defaults
/// and can be edited afterwards in the blueprint editor.
/// </summary>
public class BlueprintFromClusterService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    ClusterBlueprintService blueprintService,
    CnpgService cnpgService,
    RedisService redisService,
    RabbitMQService rabbitMQService)
{
    /// <summary>Summary of what was captured, for surfacing in a toast.</summary>
    public record GenerateResult(
        ClusterBlueprint Blueprint, int ComponentSteps, int ServiceSteps,
        int VariablesCreated, IReadOnlyList<string> Skipped);

    public async Task<GenerateResult> GenerateAsync(
        Guid clusterId, string name, string? description, CancellationToken ct = default)
    {
        Guid tenantId;
        Guid environmentId;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            KubernetesCluster cluster = await db.KubernetesClusters.FirstAsync(c => c.Id == clusterId, ct);
            tenantId = cluster.TenantId;
            environmentId = cluster.EnvironmentId;
        }

        ClusterBlueprint bp = await blueprintService.CreateBlueprintAsync(tenantId, name, description, ct);

        List<string> skipped = [];
        Dictionary<string, Guid> createdVars = new(StringComparer.OrdinalIgnoreCase);

        // Ensure a variable exists (deduped by name within this generation) and seed
        // the source environment's value; returns the ${token} to store in the step.
        async Task<string> Variabilize(string suggestedName, string literal)
        {
            string vname = Slug(suggestedName);
            if (!createdVars.TryGetValue(vname, out Guid vid))
            {
                BlueprintVariable v = await blueprintService.AddVariableAsync(bp.Id, vname, null, null, ct);
                vid = v.Id;
                createdVars[vname] = vid;
            }
            await blueprintService.SetVariableValueAsync(vid, environmentId, literal, ct);
            return "${" + vname + "}";
        }

        // ── Service steps first (dependency-friendly: DBs/brokers before consumers) ──
        int serviceSteps = 0;

        foreach (CnpgCluster c in (await cnpgService.GetClustersAsync(tenantId, ct))
                     .Where(c => c.KubernetesClusterId == clusterId && !c.IsExternal))
        {
            Dictionary<string, string> p = new()
            {
                ["instances"] = c.Instances.ToString(),
                ["storageSize"] = c.StorageSize,
                ["postgresVersion"] = c.PostgresVersion,
                ["retentionDays"] = c.RetentionDays.ToString(),
                ["maxBackups"] = c.MaxBackups.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(c.BackupSchedule)) p["backupSchedule"] = c.BackupSchedule;
            if (c.StorageLinkId is Guid sl && sl != Guid.Empty)
                p["storageLinkId"] = await Variabilize($"{c.Name}_storageLink", sl.ToString());
            await blueprintService.AddStepAsync(bp.Id, BlueprintStepType.Service, "cnpg", c.Name, c.Namespace, p, ct);
            serviceSteps++;
        }

        foreach (RedisCluster c in (await redisService.GetClustersAsync(tenantId, ct))
                     .Where(c => c.KubernetesClusterId == clusterId))
        {
            Dictionary<string, string> p = new()
            {
                ["clusterSize"] = c.ClusterSize.ToString(),
                ["redisVersion"] = c.RedisVersion,
                ["storageSize"] = c.StorageSize,
                ["persistenceEnabled"] = c.PersistenceEnabled ? "true" : "false",
            };
            if (!string.IsNullOrWhiteSpace(c.StorageClass)) p["storageClass"] = c.StorageClass;
            await blueprintService.AddStepAsync(bp.Id, BlueprintStepType.Service, "redis", c.Name, c.Namespace, p, ct);
            serviceSteps++;
        }

        foreach (RabbitMQCluster c in (await rabbitMQService.GetClustersAsync(tenantId, ct))
                     .Where(c => c.KubernetesClusterId == clusterId))
        {
            Dictionary<string, string> p = new()
            {
                ["version"] = c.RabbitMQVersion,
                ["replicas"] = c.Replicas.ToString(),
                ["storageSize"] = c.StorageSize,
            };
            if (!string.IsNullOrWhiteSpace(c.StorageClass)) p["storageClass"] = c.StorageClass;
            if (c.StorageLinkId is Guid sl && sl != Guid.Empty)
                p["storageLinkId"] = await Variabilize($"{c.Name}_storageLink", sl.ToString());
            await blueprintService.AddStepAsync(bp.Id, BlueprintStepType.Service, "rabbitmq", c.Name, c.Namespace, p, ct);
            serviceSteps++;
        }

        // ── Component steps ──
        int componentSteps = 0;
        List<ClusterComponent> components = await vaultService.GetComponentsAsync(clusterId, ct);

        Dictionary<Guid, KeycloakComponentConfig> keycloakConfigs;
        Dictionary<Guid, HarborComponentConfig> harborConfigs;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            keycloakConfigs = await db.Set<KeycloakComponentConfig>()
                .Where(k => k.TenantId == tenantId && k.ClusterComponentId != null)
                .ToDictionaryAsync(k => k.ClusterComponentId!.Value, ct);
            harborConfigs = await db.Set<HarborComponentConfig>()
                .Where(h => h.TenantId == tenantId)
                .ToDictionaryAsync(h => h.ClusterComponentId, ct);
        }

        foreach (ClusterComponent comp in components)
        {
            CatalogEntry? entry = ComponentCatalog.ResolveForComponent(comp.Name, comp.HelmChartName);
            if (entry is null)
            {
                skipped.Add(comp.Name);
                continue;
            }

            keycloakConfigs.TryGetValue(comp.Id, out KeycloakComponentConfig? kc);
            harborConfigs.TryGetValue(comp.Id, out HarborComponentConfig? hc);

            Dictionary<string, string> p = new();
            foreach (ComponentFormField field in entry.FormFields)
            {
                if (field.StoreAsSecret) continue;                                   // secrets stay in the vault
                if (field.YamlPath.StartsWith("subchart:", StringComparison.Ordinal)) continue; // v1: catalog default

                string? value;
                if (IsPseudoPath(field.YamlPath))
                {
                    value = RecoverSideConfig(field.Key, kc, hc);
                }
                else if (!string.IsNullOrEmpty(field.YamlPath))
                {
                    value = YamlFormMerger.ExtractValue(comp.HelmValues ?? "", field.YamlPath);
                }
                else
                {
                    continue; // no path — handled elsewhere at install (e.g. solver builder)
                }

                if (string.IsNullOrEmpty(value)) continue;

                bool envSpecific = IsEnvSpecific(field);
                // Skip values that just match the catalog default (keeps steps lean),
                // but always keep env-specific values so they become variables.
                if (!envSpecific && string.Equals(value, field.DefaultValue, StringComparison.Ordinal)) continue;

                if (envSpecific)
                {
                    value = await Variabilize($"{entry.Key}_{field.Key}", value);
                }
                p[field.Key] = value;
            }

            await blueprintService.AddStepAsync(
                bp.Id, BlueprintStepType.Component, entry.Key, comp.Name, comp.Namespace, p, ct);
            componentSteps++;
        }

        return new GenerateResult(bp, componentSteps, serviceSteps, createdVars.Count, skipped);
    }

    // ── Helpers ──

    private static bool IsPseudoPath(string yamlPath) =>
        yamlPath.StartsWith("cnpg:", StringComparison.Ordinal)
        || yamlPath.StartsWith("harbor:", StringComparison.Ordinal)
        || yamlPath.StartsWith("loki:", StringComparison.Ordinal)
        || yamlPath.StartsWith("mimir:", StringComparison.Ordinal)
        || yamlPath.StartsWith("tempo:", StringComparison.Ordinal);

    /// <summary>
    /// Recovers the common non-secret Keycloak/Harbor side-config values from the
    /// tracked config entities. Fields not covered here fall back to catalog defaults
    /// at bootstrap and can be filled in via the blueprint editor.
    /// </summary>
    private static string? RecoverSideConfig(
        string fieldKey, KeycloakComponentConfig? kc, HarborComponentConfig? hc)
    {
        if (kc is not null)
        {
            switch (fieldKey)
            {
                case "cnpg-database":
                    Guid? kdb = kc.CnpgDatabaseId ?? kc.RegisteredPostgresDatabaseId;
                    return kdb is Guid g && g != Guid.Empty ? g.ToString() : null;
                case "admin-url": return kc.AdminUrl;
                case "admin-username": return kc.AdminUsername;
            }
        }

        if (hc is not null)
        {
            switch (fieldKey)
            {
                case "cnpg-database":
                    return hc.CnpgDatabaseId is Guid g && g != Guid.Empty ? g.ToString() : null;
                case "storage-link":
                    return hc.StorageLinkId is Guid s && s != Guid.Empty ? s.ToString() : null;
                case "hostname": return HostOf(hc.RegistryUrl);
            }
        }

        return null;
    }

    /// <summary>A field whose value is inherently tied to one environment and should become a variable.</summary>
    private static bool IsEnvSpecific(ComponentFormField field) =>
        field.Type is FormFieldType.CnpgDatabase or FormFieldType.ClusterIssuer
            or FormFieldType.StorageLink or FormFieldType.GatewaySelector
        || LooksLikeHost(field.Key) || LooksLikeHost(field.YamlPath);

    private static bool LooksLikeHost(string s) =>
        s.Contains("host", StringComparison.OrdinalIgnoreCase)
        || s.Contains("domain", StringComparison.OrdinalIgnoreCase)
        || s.Contains("url", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips scheme/path from a URL, leaving the bare host (best-effort).</summary>
    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : url;
    }

    /// <summary>Sanitizes a suggested name into a valid ${var} token: [A-Za-z0-9_.-].</summary>
    private static string Slug(string name)
    {
        char[] chars = name.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray();
        string slug = new string(chars).Trim('_');
        return string.IsNullOrEmpty(slug) ? "var" : slug;
    }
}
