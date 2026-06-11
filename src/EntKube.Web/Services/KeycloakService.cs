using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class KeycloakUserInfo
{
    public required string Id { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool Enabled { get; set; }
    public bool EmailVerified { get; set; }
    /// <summary>Custom user attributes (e.g. org_id, department). Each key maps to one or more values.</summary>
    public Dictionary<string, List<string>> Attributes { get; set; } = [];
}

public class KeycloakGroupInfo
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Path { get; set; }
}

public class KeycloakIdpInfo
{
    public required string Alias { get; set; }
    public string? DisplayName { get; set; }
    public required string ProviderId { get; set; }
    public bool Enabled { get; set; }
}

public class KeycloakOrganizationInfo
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
}

public class KeycloakRealmDetails
{
    public required string Realm { get; set; }
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; }
    public string? LoginTheme { get; set; }
    public string? AccountTheme { get; set; }
    public List<string> Themes { get; set; } = [];
}

public class CopyResult
{
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// A Keycloak instance tracked by EntKube. May be an EntKube-managed install
/// (Component is set) or an externally deployed instance (Component is null).
/// </summary>
public class DetectedKeycloak
{
    /// <summary>Set for EntKube-managed installs; null for external instances.</summary>
    public ClusterComponent? Component { get; set; }
    public KeycloakComponentConfig? Config { get; set; }

    /// <summary>External URL from the component's ExternalRoutes, if any.</summary>
    public string? SuggestedAdminUrl { get; set; }

    public bool IsConfigured => Config is not null;
    public bool IsExternal => Component is null;
    public bool HasDatabase => Config?.CnpgDatabaseId is not null;

    /// <summary>
    /// Stable ID used as the vault partition key for secrets and theme CSS.
    /// For managed instances this is the ClusterComponent ID; for external instances
    /// the KeycloakComponentConfig ID is used instead.
    /// </summary>
    public Guid ComponentId => Component?.Id ?? Config!.Id;

    public string DisplayName => Component?.Name ?? Config?.DisplayName ?? "External Keycloak";
    public string? ClusterDisplayName => Component?.Cluster.Name;
}

/// <summary>
/// Manages Keycloak instances detected from installed ClusterComponents.
///
/// Detection: scans ClusterComponents with catalog key "keycloak" and status Installed.
/// Database: links to a managed CNPG database; stores KC_DB_URL/USERNAME/PASSWORD
///   as component vault secrets synced to a K8s Secret in the Keycloak namespace.
/// Admin password: stored as component vault secret "KEYCLOAK_ADMIN_PASSWORD",
///   synced to the same K8s Secret.
/// Admin API: token obtained on-demand from /realms/master/protocol/openid-connect/token.
/// </summary>
public class KeycloakService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IHttpClientFactory httpClientFactory,
    CnpgService cnpgService)
{
    private readonly record struct TokenCacheKey(Guid ComponentId);
    private readonly Dictionary<TokenCacheKey, (string Token, DateTime Expiry)> _tokenCache = [];

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all Keycloak components installed across the tenant's clusters,
    /// together with their configuration state and suggested admin URL.
    /// </summary>
    public async Task<List<DetectedKeycloak>> GetDetectedKeycloaksAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<ClusterComponent> components = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Include(c => c.ExternalRoutes)
            .Where(c => c.Cluster.TenantId == tenantId
                && (c.Status == ComponentStatus.Installed || c.Status == ComponentStatus.Failed)
                && (c.Name == "keycloak"
                    || c.ReleaseName == "keycloak"
                    || c.HelmChartName == "keycloakx"
                    || c.HelmChartName == "keycloak"))
            .OrderBy(c => c.Cluster.Name).ThenBy(c => c.Name)
            .ToListAsync(ct);

        List<KeycloakComponentConfig> configs = await db.KeycloakComponentConfigs
            .Include(cfg => cfg.CnpgDatabase).ThenInclude(d => d!.CnpgCluster)
            .Include(cfg => cfg.RegisteredPostgresDatabase)
                .ThenInclude(d => d!.RegisteredPostgresInstance)
            .Where(cfg => cfg.TenantId == tenantId)
            .ToListAsync(ct);

        List<DetectedKeycloak> result = components.Select(comp =>
        {
            KeycloakComponentConfig? cfg = configs.FirstOrDefault(c => c.ClusterComponentId == comp.Id);

            // Append the http.relativePath (e.g. "/auth") from Helm values to the suggested URL
            // so the pre-filled admin URL is correct when the user configures via the Identity tab.
            string relativePath = YamlFormMerger.ExtractValue(comp.HelmValues ?? "", "http.relativePath") ?? "/auth";
            if (relativePath != "/" && !relativePath.StartsWith('/'))
                relativePath = "/" + relativePath;

            string? suggestedUrl = comp.ExternalRoutes
                .OrderBy(r => r.CreatedAt)
                .Select(r => $"https://{r.Hostname}{(relativePath == "/" ? "" : relativePath)}")
                .FirstOrDefault();

            return new DetectedKeycloak
            {
                Component = comp,
                Config = cfg,
                SuggestedAdminUrl = suggestedUrl ?? cfg?.AdminUrl
            };
        }).ToList();

        // Also surface externally deployed Keycloaks (registered without a ClusterComponent).
        foreach (KeycloakComponentConfig cfg in configs.Where(c => c.ClusterComponentId is null))
        {
            result.Add(new DetectedKeycloak
            {
                Component = null,
                Config = cfg,
                SuggestedAdminUrl = cfg.AdminUrl
            });
        }

        return result;
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Configures a detected Keycloak component: links it to a CNPG database,
    /// stores the admin password, and syncs DB credentials + admin password
    /// to a K8s Secret in the Keycloak namespace so the Helm chart can use them.
    /// </summary>
    public async Task<KeycloakComponentConfig> ConfigureAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Guid? cnpgDatabaseId,
        string adminUsername,
        string adminPassword,
        string adminUrl,
        Guid? registeredPostgresDatabaseId = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Only load the CNPG database when one was explicitly selected.
        // Imported Keycloak instances backed by non-CNPG Postgres pass null here.
        CnpgDatabase? cnpgDb = null;
        if (cnpgDatabaseId.HasValue)
        {
            cnpgDb = await db.CnpgDatabases
                .Include(d => d.CnpgCluster)
                .FirstOrDefaultAsync(d => d.Id == cnpgDatabaseId && d.CnpgCluster.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("CNPG database not found.");
        }

        RegisteredPostgresDatabase? regPgDb = null;
        if (registeredPostgresDatabaseId.HasValue)
        {
            regPgDb = await db.RegisteredPostgresDatabases
                .Include(d => d.RegisteredPostgresInstance)
                .FirstOrDefaultAsync(d => d.Id == registeredPostgresDatabaseId
                    && d.RegisteredPostgresInstance.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("Registered Postgres database not found.");
        }

        // Upsert the config record.

        KeycloakComponentConfig? config = await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId, ct);

        if (config is null)
        {
            config = new KeycloakComponentConfig
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterComponentId = clusterComponentId
            };
            db.KeycloakComponentConfigs.Add(config);
        }

        config.CnpgDatabaseId = cnpgDatabaseId;
        config.RegisteredPostgresDatabaseId = registeredPostgresDatabaseId;
        config.AdminUsername = adminUsername;
        config.AdminUrl = adminUrl.TrimEnd('/');

        string keycloakNs = component.Namespace ?? "keycloak";
        string credSecretName = $"{component.ReleaseName ?? component.Name}-credentials";

        // Fix placeholder secret name if the release name differs from the default.
        if (!string.IsNullOrWhiteSpace(component.HelmValues))
        {
            string updatedYaml = component.HelmValues.Replace(
                "name: keycloak-credentials", $"name: {credSecretName}");
            if (updatedYaml != component.HelmValues)
            {
                component.HelmValues = updatedYaml;
            }
        }

        // Write DB connection details (non-sensitive) directly into Helm values so the chart
        // always knows where the database is, independent of the K8s secret sync.
        // KC_DB_URL in the secret takes precedence, but if the secret is missing or incomplete
        // (e.g. first install before Configure was run), the chart-native config is the fallback.
        if (cnpgDb is not null)
        {
            string dbHost = $"{cnpgDb.CnpgCluster.Name}-rw.{cnpgDb.CnpgCluster.Namespace}.svc.cluster.local";
            component.HelmValues = YamlFormMerger.MergeFormValues(
                component.HelmValues ?? "",
                new Dictionary<string, string>
                {
                    ["database.hostname"] = dbHost,
                    ["database.port"] = "5432",
                    ["database.database"] = cnpgDb.Name,
                    ["database.username"] = cnpgDb.Owner
                });
        }
        else if (regPgDb is not null)
        {
            RegisteredPostgresInstance rpi = regPgDb.RegisteredPostgresInstance;
            string dbHost = $"{rpi.ServiceName}.{rpi.Namespace}.svc.cluster.local";
            component.HelmValues = YamlFormMerger.MergeFormValues(
                component.HelmValues ?? "",
                new Dictionary<string, string>
                {
                    ["database.hostname"] = dbHost,
                    ["database.port"] = rpi.Port.ToString(),
                    ["database.database"] = regPgDb.Name,
                    ["database.username"] = regPgDb.Owner
                });
        }

        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);

        // Admin username + password → vault + K8s sync.
        // Keycloak 26+ uses KC_BOOTSTRAP_ADMIN_USERNAME / KC_BOOTSTRAP_ADMIN_PASSWORD.
        // Both are stored in the same K8s Secret so extraEnvFrom picks up all keys.

        VaultSecret adminUserSecret = await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, "KC_BOOTSTRAP_ADMIN_USERNAME", adminUsername, ct);
        await vaultService.ConfigureKubernetesSyncAsync(
            adminUserSecret.Id, true, credSecretName, keycloakNs, ct);

        VaultSecret adminPwSecret = await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, "KC_BOOTSTRAP_ADMIN_PASSWORD", adminPassword, ct);
        await vaultService.ConfigureKubernetesSyncAsync(
            adminPwSecret.Id, true, credSecretName, keycloakNs, ct);

        // DB credentials are only synced when a managed CNPG database is linked.
        // For imported instances backed by non-CNPG Postgres, the DB secrets already
        // exist in the cluster and are not managed by EntKube.

        if (cnpgDb is not null)
        {
            string host = $"{cnpgDb.CnpgCluster.Name}-rw.{cnpgDb.CnpgCluster.Namespace}.svc.cluster.local";
            string jdbcUrl = $"jdbc:postgresql://{host}/{cnpgDb.Name}";

            VaultSecret dbUrlSecret = await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "KC_DB_URL", jdbcUrl, ct);
            await vaultService.ConfigureKubernetesSyncAsync(
                dbUrlSecret.Id, true, credSecretName, keycloakNs, ct);

            VaultSecret dbUserSecret = await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "KC_DB_USERNAME", cnpgDb.Owner, ct);
            await vaultService.ConfigureKubernetesSyncAsync(
                dbUserSecret.Id, true, credSecretName, keycloakNs, ct);

            string? dbPassword = await vaultService.GetCnpgDatabasePasswordAsync(tenantId, cnpgDatabaseId!.Value, ct);

            if (dbPassword is not null)
            {
                VaultSecret dbPwSecret = await vaultService.SetComponentSecretAsync(
                    tenantId, clusterComponentId, "KC_DB_PASSWORD", dbPassword, ct);
                await vaultService.ConfigureKubernetesSyncAsync(
                    dbPwSecret.Id, true, credSecretName, keycloakNs, ct);
            }
        }
        else if (regPgDb is not null)
        {
            RegisteredPostgresInstance rpi = regPgDb.RegisteredPostgresInstance;
            string host = $"{rpi.ServiceName}.{rpi.Namespace}.svc.cluster.local";
            string jdbcUrl = $"jdbc:postgresql://{host}:{rpi.Port}/{regPgDb.Name}";

            VaultSecret dbUrlSecret = await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "KC_DB_URL", jdbcUrl, ct);
            await vaultService.ConfigureKubernetesSyncAsync(
                dbUrlSecret.Id, true, credSecretName, keycloakNs, ct);

            VaultSecret dbUserSecret = await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "KC_DB_USERNAME", regPgDb.Owner, ct);
            await vaultService.ConfigureKubernetesSyncAsync(
                dbUserSecret.Id, true, credSecretName, keycloakNs, ct);

            string? dbPassword = await vaultService.GetRegisteredPostgresDatabasePasswordAsync(
                tenantId, registeredPostgresDatabaseId!.Value, ct);

            if (dbPassword is not null)
            {
                VaultSecret dbPwSecret = await vaultService.SetComponentSecretAsync(
                    tenantId, clusterComponentId, "KC_DB_PASSWORD", dbPassword, ct);
                await vaultService.ConfigureKubernetesSyncAsync(
                    dbPwSecret.Id, true, credSecretName, keycloakNs, ct);
            }
        }

        return config;
    }

    /// <summary>
    /// Called automatically before every install/upgrade. If a KeycloakComponentConfig with a CNPG
    /// database exists for this component, refreshes database.hostname/port/database/username in
    /// HelmValues and rewrites the vault DB secrets so the pre-install sync picks them up.
    /// Safe no-op for non-Keycloak components or unconfigured instances.
    /// </summary>
    public async Task RefreshDatabaseHelmValuesIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig? config = await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        if (config?.CnpgDatabaseId is null)
        {
            return;
        }

        await WriteDatabaseHelmValuesAsync(tenantId, clusterComponentId, config.CnpgDatabaseId.Value, ct);
    }

    /// <summary>
    /// Grants full schema ownership to the database owner before every Keycloak install
    /// or upgrade. Safe no-op when no CNPG database is configured.
    ///
    /// This is idempotent in PostgreSQL — granting privileges that already exist does
    /// nothing. It fixes "permission denied for table databasechangelog" errors that
    /// occur when a database dump from another Keycloak instance is restored into the
    /// CNPG database, leaving Liquibase tables owned by the original user.
    /// </summary>
    public async Task GrantDatabaseOwnerPermissionsIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig? config = await db.KeycloakComponentConfigs
            .Include(c => c.CnpgDatabase)
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        if (config?.CnpgDatabase is null) return;

        await cnpgService.GrantDatabaseOwnerPermissionsAsync(
            tenantId, config.CnpgDatabase.CnpgClusterId, config.CnpgDatabase.Id, ct);
    }

    /// <summary>
    /// Releases a stuck Liquibase changelog lock before every Keycloak install or upgrade.
    /// Safe no-op when no CNPG database is configured or when no lock row exists.
    ///
    /// Liquibase can leave the lock set to true if Keycloak crashes mid-migration. A locked
    /// changelog causes Keycloak to hang at startup (pod Running, empty 500 responses) because
    /// it waits indefinitely to acquire the lock.
    /// </summary>
    public async Task ReleaseLiquibaseLockIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig? config = await db.KeycloakComponentConfigs
            .Include(c => c.CnpgDatabase)
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        if (config?.CnpgDatabase is null) return;

        await cnpgService.ReleaseLiquibaseLockAsync(
            tenantId, config.CnpgDatabase.CnpgClusterId, config.CnpgDatabase.Id, ct);
    }

    /// <summary>
    /// Updates the <c>frontendUrl</c> realm attribute for all realms in the Keycloak database
    /// to match the admin URL configured in EntKube. Safe to run on fresh installs (no-op if
    /// no frontendUrl rows exist). Use after restoring a database from another instance whose
    /// URL differs from the current deployment.
    /// </summary>
    public async Task FixRealmUrlsIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig? config = await db.KeycloakComponentConfigs
            .Include(c => c.CnpgDatabase)
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        if (config?.CnpgDatabase is null || string.IsNullOrWhiteSpace(config.AdminUrl)) return;

        string url = config.AdminUrl.TrimEnd('/');

        await cnpgService.FixKeycloakRealmFrontendUrlAsync(
            tenantId, config.CnpgDatabase.CnpgClusterId, config.CnpgDatabase.Id, url, ct);
    }

    /// <summary>
    /// Writes CNPG database connection details (non-sensitive: hostname, port, database name,
    /// username) into the component's HelmValues so the keycloakx chart always has a working
    /// KC_DB_URL_HOST / KC_DB_URL_DATABASE / KC_DB_USERNAME even before vault secrets are synced.
    /// Also writes KC_DB_URL + KC_DB_USERNAME + KC_DB_PASSWORD to vault for the K8s secret.
    /// No admin credentials required — call this at registration time when a database is selected.
    /// </summary>
    public async Task WriteDatabaseHelmValuesAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Guid cnpgDatabaseId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        CnpgDatabase cnpgDb = await db.CnpgDatabases
            .Include(d => d.CnpgCluster)
            .FirstOrDefaultAsync(d => d.Id == cnpgDatabaseId && d.CnpgCluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG database not found.");

        string keycloakNs = component.Namespace ?? "keycloak";
        string credSecretName = $"{component.ReleaseName ?? component.Name}-credentials";
        string host = $"{cnpgDb.CnpgCluster.Name}-rw.{cnpgDb.CnpgCluster.Namespace}.svc.cluster.local";

        component.HelmValues = YamlFormMerger.MergeFormValues(
            component.HelmValues ?? "",
            new Dictionary<string, string>
            {
                ["database.hostname"] = host,
                ["database.port"] = "5432",
                ["database.database"] = cnpgDb.Name,
                ["database.username"] = cnpgDb.Owner
            });

        await db.SaveChangesAsync(ct);

        // Also write env-var secrets so the K8s secret is complete when synced.
        await vaultService.InitializeVaultAsync(tenantId, ct);

        string jdbcUrl = $"jdbc:postgresql://{host}/{cnpgDb.Name}";

        VaultSecret dbUrlSecret = await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, "KC_DB_URL", jdbcUrl, ct);
        await vaultService.ConfigureKubernetesSyncAsync(
            dbUrlSecret.Id, true, credSecretName, keycloakNs, ct);

        VaultSecret dbUserSecret = await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, "KC_DB_USERNAME", cnpgDb.Owner, ct);
        await vaultService.ConfigureKubernetesSyncAsync(
            dbUserSecret.Id, true, credSecretName, keycloakNs, ct);

        string? dbPassword = await vaultService.GetCnpgDatabasePasswordAsync(tenantId, cnpgDatabaseId, ct);
        if (dbPassword is not null)
        {
            VaultSecret dbPwSecret = await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "KC_DB_PASSWORD", dbPassword, ct);
            await vaultService.ConfigureKubernetesSyncAsync(
                dbPwSecret.Id, true, credSecretName, keycloakNs, ct);
        }
    }

    /// <summary>
    /// Updates the admin URL / credentials without touching the database config.
    /// Pass null for newAdminPassword to keep the existing password.
    /// </summary>
    public async Task UpdateConfigAsync(
        Guid tenantId,
        Guid configId,
        string adminUrl,
        string adminUsername,
        string? newAdminPassword,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig config = await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Config not found.");

        config.AdminUrl = adminUrl.TrimEnd('/');
        config.AdminUsername = adminUsername;
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(newAdminPassword))
        {
            Guid vaultId = config.ClusterComponentId ?? config.Id;
            await vaultService.SetComponentSecretAsync(
                tenantId, vaultId, "KEYCLOAK_ADMIN_PASSWORD", newAdminPassword, ct);
        }

        _tokenCache.Remove(new TokenCacheKey(config.ClusterComponentId ?? config.Id));
    }

    /// <summary>
    /// Removes a Keycloak component config and all its realm/backup records.
    /// Does NOT modify anything in Keycloak itself.
    /// </summary>
    public async Task RemoveConfigAsync(Guid tenantId, Guid configId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig config = await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Config not found.");

        db.KeycloakComponentConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        _tokenCache.Remove(new TokenCacheKey(config.ClusterComponentId ?? config.Id));
    }

    /// <summary>
    /// Registers an externally deployed Keycloak (not installed via EntKube) so
    /// it appears in the Identity tab. Credentials are stored in vault keyed by
    /// the new config's own Id (no ClusterComponent is involved).
    /// </summary>
    public async Task<KeycloakComponentConfig> RegisterExternalAsync(
        Guid tenantId,
        string displayName,
        string adminUrl,
        string adminUsername,
        string adminPassword,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakComponentConfig config = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterComponentId = null,
            DisplayName = displayName.Trim(),
            AdminUrl = adminUrl.TrimEnd('/'),
            AdminUsername = adminUsername,
            CreatedAt = DateTime.UtcNow,
        };

        db.KeycloakComponentConfigs.Add(config);
        await db.SaveChangesAsync(ct);

        await vaultService.SetComponentSecretAsync(
            tenantId, config.Id, "KC_BOOTSTRAP_ADMIN_PASSWORD", adminPassword, ct);

        return config;
    }

    // ── Realms ────────────────────────────────────────────────────────────────

    public async Task<List<KeycloakRealm>> GetRealmsForConfigAsync(
        Guid tenantId, Guid configId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakRealms
            .Include(r => r.LinkedApp).ThenInclude(a => a!.Customer)
            .Where(r => r.TenantId == tenantId && r.KeycloakComponentConfigId == configId)
            .OrderBy(r => r.DisplayName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Fetches all realms from the live Keycloak Admin API and imports any that don't
    /// yet have a local record in EntKube. Skips the built-in master realm.
    /// Safe to call repeatedly — existing records are never overwritten.
    /// Returns the full up-to-date realm list.
    /// </summary>
    public async Task<List<KeycloakRealm>> SyncRealmsFromKeycloakAsync(
        Guid tenantId, Guid configId, CancellationToken ct = default)
    {
        KeycloakComponentConfig config = await GetConfigAsync(tenantId, configId, ct);
        string token = await GetAdminTokenAsync(tenantId, config, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{config.AdminUrl}/admin/realms")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        HashSet<string> existing = (await db.KeycloakRealms
            .Where(r => r.KeycloakComponentConfigId == configId && r.TenantId == tenantId)
            .Select(r => r.RealmName)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement realmEl in doc.RootElement.EnumerateArray())
        {
            string? realmName = realmEl.GetStringOrDefault("realm");
            if (string.IsNullOrWhiteSpace(realmName) || realmName == "master") continue;
            if (existing.Contains(realmName)) continue;

            string raw = realmEl.GetStringOrDefault("displayName") ?? "";
            string displayName = string.IsNullOrWhiteSpace(raw) ? realmName : raw;
            bool enabled = realmEl.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean();

            db.KeycloakRealms.Add(new KeycloakRealm
            {
                Id = Guid.NewGuid(),
                KeycloakComponentConfigId = configId,
                TenantId = tenantId,
                RealmName = realmName,
                DisplayName = displayName,
                Enabled = enabled
            });

            existing.Add(realmName);
        }

        await db.SaveChangesAsync(ct);

        return await db.KeycloakRealms
            .Include(r => r.LinkedApp).ThenInclude(a => a!.Customer)
            .Where(r => r.KeycloakComponentConfigId == configId && r.TenantId == tenantId)
            .OrderBy(r => r.DisplayName)
            .ToListAsync(ct);
    }

    public async Task<KeycloakRealm> CreateRealmAsync(
        Guid tenantId,
        Guid configId,
        string realmName,
        string displayName,
        string? loginTheme,
        CancellationToken ct = default)
    {
        KeycloakComponentConfig config = await GetConfigAsync(tenantId, configId, ct);
        string token = await GetAdminTokenAsync(tenantId, config, ct);

        HttpClient http = CreateHttpClient();

        var body = new { realm = realmName, displayName, enabled = true, loginTheme = loginTheme ?? "" };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{config.AdminUrl}/admin/realms")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = new()
        {
            Id = Guid.NewGuid(),
            KeycloakComponentConfigId = configId,
            TenantId = tenantId,
            RealmName = realmName,
            DisplayName = displayName,
            LoginTheme = loginTheme,
            Enabled = true
        };

        db.KeycloakRealms.Add(realm);
        await db.SaveChangesAsync(ct);
        return realm;
    }

    public async Task DeleteRealmAsync(Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .Include(r => r.ComponentConfig)
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        try
        {
            string token = await GetAdminTokenAsync(tenantId, realm.ComponentConfig, ct);
            HttpClient http = CreateHttpClient();
            HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
                $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
            }, ct);

            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                resp.EnsureSuccessStatusCode();
            }
        }
        catch (Exception) { /* best-effort Keycloak delete */ }

        db.KeycloakRealms.Remove(realm);
        await db.SaveChangesAsync(ct);
    }

    public async Task<KeycloakRealmDetails> GetRealmDetailsAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        List<string> themes = [];

        try
        {
            HttpResponseMessage themeResp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                $"{realm.ComponentConfig.AdminUrl}/admin/serverinfo")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
            }, ct);

            if (themeResp.IsSuccessStatusCode)
            {
                using JsonDocument tDoc = JsonDocument.Parse(await themeResp.Content.ReadAsStringAsync(ct));

                if (tDoc.RootElement.TryGetProperty("themes", out JsonElement themesEl)
                    && themesEl.TryGetProperty("login", out JsonElement loginThemes))
                {
                    themes = loginThemes.EnumerateArray()
                        .Select(t => t.GetStringOrDefault("name") ?? "")
                        .Where(t => t.Length > 0)
                        .ToList();
                }
            }
        }
        catch (Exception) { /* non-fatal */ }

        return new KeycloakRealmDetails
        {
            Realm = root.GetStringOrDefault("realm") ?? realm.RealmName,
            DisplayName = root.GetStringOrDefault("displayName"),
            Enabled = root.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean(),
            LoginTheme = root.GetStringOrDefault("loginTheme"),
            AccountTheme = root.GetStringOrDefault("accountTheme"),
            Themes = themes
        };
    }

    public async Task UpdateRealmThemesAsync(
        Guid tenantId, Guid realmId, string? loginTheme, string? accountTheme,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .Include(r => r.ComponentConfig)
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        string token = await GetAdminTokenAsync(tenantId, realm.ComponentConfig, ct);
        HttpClient http = CreateHttpClient();

        var patch = new { loginTheme, accountTheme };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();

        realm.LoginTheme = loginTheme;
        realm.AccountTheme = accountTheme;
        await db.SaveChangesAsync(ct);
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    public async Task<List<KeycloakUserInfo>> GetUsersAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users?max=100")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(u =>
        {
            Dictionary<string, List<string>> attrs = [];

            if (u.TryGetProperty("attributes", out JsonElement attrsEl)
                && attrsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in attrsEl.EnumerateObject())
                {
                    List<string> values = prop.Value.ValueKind == JsonValueKind.Array
                        ? prop.Value.EnumerateArray()
                            .Select(v => v.GetString() ?? "")
                            .Where(v => v.Length > 0)
                            .ToList()
                        : [];

                    if (values.Count > 0)
                    {
                        attrs[prop.Name] = values;
                    }
                }
            }

            return new KeycloakUserInfo
            {
                Id = u.GetStringOrDefault("id") ?? "",
                Username = u.GetStringOrDefault("username"),
                Email = u.GetStringOrDefault("email"),
                FirstName = u.GetStringOrDefault("firstName"),
                LastName = u.GetStringOrDefault("lastName"),
                Enabled = u.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean(),
                EmailVerified = u.TryGetProperty("emailVerified", out JsonElement ev) && ev.GetBoolean(),
                Attributes = attrs
            };
        }).ToList();
    }

    public async Task CreateUserAsync(
        Guid tenantId, Guid realmId,
        string username, string email, string firstName, string lastName,
        string? password, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        object body = password is not null
            ? new
            {
                username, email, firstName, lastName, enabled = true, emailVerified = true,
                credentials = new[] { new { type = "password", value = password, temporary = false } }
            }
            : (object)new { username, email, firstName, lastName, enabled = true, emailVerified = true };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteUserAsync(
        Guid tenantId, Guid realmId, string userId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── Identity Providers ────────────────────────────────────────────────────

    public async Task<List<KeycloakIdpInfo>> GetIdpsAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/identity-provider/instances")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(idp => new KeycloakIdpInfo
        {
            Alias = idp.GetStringOrDefault("alias") ?? "",
            DisplayName = idp.GetStringOrDefault("displayName"),
            ProviderId = idp.GetStringOrDefault("providerId") ?? "",
            Enabled = idp.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean()
        }).ToList();
    }

    public async Task CreateIdpAsync(
        Guid tenantId, Guid realmId,
        string alias, string providerId, string clientId, string clientSecret,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        var body = new
        {
            alias,
            providerId,
            enabled = true,
            config = new { clientId, clientSecret, useJwksUrl = "true" }
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/identity-provider/instances")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteIdpAsync(
        Guid tenantId, Guid realmId, string alias, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/identity-provider/instances/{alias}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── Groups ────────────────────────────────────────────────────────────────

    public async Task<List<KeycloakGroupInfo>> GetGroupsAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/groups")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(g => new KeycloakGroupInfo
        {
            Id = g.GetStringOrDefault("id") ?? "",
            Name = g.GetStringOrDefault("name") ?? "",
            Path = g.GetStringOrDefault("path")
        }).ToList();
    }

    public async Task CreateGroupAsync(
        Guid tenantId, Guid realmId, string name, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/groups")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteGroupAsync(
        Guid tenantId, Guid realmId, string groupId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/groups/{groupId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── Organizations (Keycloak 26+) ──────────────────────────────────────────

    public async Task<List<KeycloakOrganizationInfo>> GetOrganizationsAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/organizations")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(o => new KeycloakOrganizationInfo
        {
            Id = o.GetStringOrDefault("id") ?? "",
            Name = o.GetStringOrDefault("name") ?? "",
            Description = o.GetStringOrDefault("description"),
            Enabled = o.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean()
        }).ToList();
    }

    public async Task CreateOrganizationAsync(
        Guid tenantId, Guid realmId, string name, string? description,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);

        HttpClient http = CreateHttpClient();

        var body = new { name, description = description ?? "", enabled = true };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/organizations")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── Backup & Restore ──────────────────────────────────────────────────────

    public async Task<List<KeycloakBackup>> GetBackupsAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakBackups
            .Include(b => b.StorageLink)
            .Where(b => b.TenantId == tenantId && b.KeycloakRealmId == realmId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<KeycloakBackup> CreateBackupAsync(
        Guid tenantId, Guid realmId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .Include(r => r.ComponentConfig)
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        StorageLink storageLink = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        KeycloakBackup backup = new()
        {
            Id = Guid.NewGuid(),
            KeycloakRealmId = realmId,
            TenantId = tenantId,
            StorageLinkId = storageLinkId,
            ObjectKey = $"keycloak/{realm.RealmName}/{DateTime.UtcNow:yyyyMMdd-HHmmss}.json",
            RealmName = realm.RealmName,
            Status = KeycloakBackupStatus.Creating
        };

        db.KeycloakBackups.Add(backup);
        await db.SaveChangesAsync(ct);

        try
        {
            string token = await GetAdminTokenAsync(tenantId, realm.ComponentConfig, ct);
            HttpClient http = CreateHttpClient();

            HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/partial-export" +
                "?exportClients=true&exportGroupsAndRoles=true")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
            }, ct);

            resp.EnsureSuccessStatusCode();

            byte[] json = await resp.Content.ReadAsByteArrayAsync(ct);

            await UploadToS3Async(tenantId, storageLink, backup.ObjectKey, json, ct);

            backup.SizeBytes = json.Length;
            backup.Status = KeycloakBackupStatus.Ready;
            backup.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            backup.Status = KeycloakBackupStatus.Failed;
            backup.LastError = ex.Message;
        }

        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.KeycloakBackups.Update(backup);
        await db2.SaveChangesAsync(ct);

        return backup;
    }

    public async Task RestoreBackupAsync(
        Guid tenantId, Guid backupId, string? targetRealmName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakBackup backup = await db.KeycloakBackups
            .Include(b => b.Realm).ThenInclude(r => r.ComponentConfig)
            .Include(b => b.StorageLink)
            .FirstOrDefaultAsync(b => b.Id == backupId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Backup not found.");

        if (backup.StorageLink is null)
            throw new InvalidOperationException("The storage link for this backup has been deleted and the backup cannot be restored.");

        byte[] json = await DownloadFromS3Async(tenantId, backup.StorageLink, backup.ObjectKey, ct);

        if (!string.IsNullOrWhiteSpace(targetRealmName) && targetRealmName != backup.RealmName)
        {
            json = RenameRealmInJson(json, targetRealmName);
        }

        string token = await GetAdminTokenAsync(tenantId, backup.Realm.ComponentConfig, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{backup.Realm.ComponentConfig.AdminUrl}/admin/realms")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new ByteArrayContent(json)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteBackupAsync(Guid tenantId, Guid backupId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakBackup backup = await db.KeycloakBackups
            .Include(b => b.StorageLink)
            .FirstOrDefaultAsync(b => b.Id == backupId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Backup not found.");

        if (backup.StorageLink is not null)
        {
            try
            {
                await DeleteFromS3Async(tenantId, backup.StorageLink, backup.ObjectKey, ct);
            }
            catch (Exception) { /* best effort */ }
        }

        db.KeycloakBackups.Remove(backup);
        await db.SaveChangesAsync(ct);
    }

    // ── Portal linking ────────────────────────────────────────────────────────

    public async Task LinkRealmToAppAsync(
        Guid tenantId, Guid realmId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        realm.LinkedAppId = appId;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlinkRealmFromAppAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        realm.LinkedAppId = null;
        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public async Task<List<App>> GetAppsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Apps
            .Include(a => a.Customer)
            .Where(a => a.Customer.TenantId == tenantId)
            .OrderBy(a => a.Customer.Name).ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    public async Task<KeycloakRealm?> GetRealmForAppAsync(Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakRealms
            .Include(r => r.LinkedApp).ThenInclude(a => a!.Customer)
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.LinkedAppId == appId, ct);
    }

    public async Task<List<CnpgDatabase>> GetDatabasesForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent comp = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        return await db.CnpgDatabases
            .Include(d => d.CnpgCluster)
            .Where(d => d.CnpgCluster.TenantId == tenantId
                && d.CnpgCluster.KubernetesClusterId == comp.ClusterId
                && d.Status == CnpgDatabaseStatus.Ready)
            .OrderBy(d => d.CnpgCluster.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns registered Postgres databases on the same cluster as a component.
    /// Used to populate the database selector in the Keycloak setup form.
    /// </summary>
    public async Task<List<RegisteredPostgresDatabase>> GetRegisteredPostgresDatabasesForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent comp = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        return await db.RegisteredPostgresDatabases
            .Include(d => d.RegisteredPostgresInstance)
            .Where(d => d.RegisteredPostgresInstance.TenantId == tenantId
                && d.RegisteredPostgresInstance.KubernetesClusterId == comp.ClusterId
                && d.Status == RegisteredPostgresDatabaseStatus.Ready)
            .OrderBy(d => d.RegisteredPostgresInstance.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all registered Postgres databases for a tenant.
    /// Used when the Keycloak instance is external (no cluster component).
    /// </summary>
    public async Task<List<RegisteredPostgresDatabase>> GetAllRegisteredPostgresDatabasesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RegisteredPostgresDatabases
            .Include(d => d.RegisteredPostgresInstance)
            .Where(d => d.RegisteredPostgresInstance.TenantId == tenantId
                && d.Status == RegisteredPostgresDatabaseStatus.Ready)
            .OrderBy(d => d.RegisteredPostgresInstance.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns CNPG databases available on the given Kubernetes cluster.
    /// Used to populate the database selector when registering a Keycloak component.
    /// </summary>
    public async Task<List<CnpgDatabase>> GetDatabasesForClusterAsync(
        Guid tenantId, Guid kubernetesClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.CnpgDatabases
            .Include(d => d.CnpgCluster)
            .Where(d => d.CnpgCluster.TenantId == tenantId
                && d.CnpgCluster.KubernetesClusterId == kubernetesClusterId
                && d.Status == CnpgDatabaseStatus.Ready)
            .OrderBy(d => d.CnpgCluster.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the KeycloakComponentConfig for a specific installed component, if any.
    /// </summary>
    public async Task<KeycloakComponentConfig?> GetConfigForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId
                && c.TenantId == tenantId, ct);
    }

    public async Task<List<KeycloakComponentConfig>> GetConfigsForClusterAsync(
        Guid tenantId, Guid kubernetesClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakComponentConfigs
            .Include(c => c.CnpgDatabase)
            .Where(c => c.TenantId == tenantId
                && db.ClusterComponents
                    .Any(comp => comp.Id == c.ClusterComponentId && comp.ClusterId == kubernetesClusterId))
            .ToListAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private HttpClient CreateHttpClient()
    {
        HttpClient http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    private async Task<KeycloakComponentConfig> GetConfigAsync(
        Guid tenantId, Guid configId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Keycloak config not found.");
    }

    private async Task<(KeycloakRealm Realm, string Token)> LoadRealmAndTokenAsync(
        Guid tenantId, Guid realmId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .Include(r => r.ComponentConfig)
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        string token = await GetAdminTokenAsync(tenantId, realm.ComponentConfig, ct);
        return (realm, token);
    }

    private async Task<string> GetAdminTokenAsync(
        Guid tenantId, KeycloakComponentConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.AdminUrl))
        {
            throw new InvalidOperationException(
                "Keycloak admin URL is not configured. Set it in the Identity tab.");
        }

        // For externals (no ClusterComponent), vault secrets are keyed by config.Id.
        Guid vaultId = config.ClusterComponentId ?? config.Id;
        TokenCacheKey key = new(vaultId);

        if (_tokenCache.TryGetValue(key, out (string Token, DateTime Expiry) cached)
            && cached.Expiry > DateTime.UtcNow.AddSeconds(30))
        {
            return cached.Token;
        }

        // Try the Keycloak 26+ name first, fall back to the legacy name for instances
        // configured before the rename (imported clusters, pre-26 setups).
        string? password =
            await vaultService.GetComponentSecretValueAsync(
                tenantId, vaultId, "KC_BOOTSTRAP_ADMIN_PASSWORD", ct)
            ?? await vaultService.GetComponentSecretValueAsync(
                tenantId, vaultId, "KEYCLOAK_ADMIN_PASSWORD", ct);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Keycloak admin password not found in vault. Re-configure the Keycloak component.");
        }

        HttpClient http = CreateHttpClient();

        FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("client_id", "admin-cli"),
            new KeyValuePair<string, string>("username", config.AdminUsername),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("grant_type", "password")
        ]);

        HttpResponseMessage resp = await http.PostAsync(
            $"{config.AdminUrl}/realms/master/protocol/openid-connect/token", form, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Keycloak token request failed ({(int)resp.StatusCode}): {body}");
        }

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        string token = doc.RootElement.GetProperty("access_token").GetString()!;
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement ei)
            ? ei.GetInt32() : 300;

        _tokenCache[key] = (token, DateTime.UtcNow.AddSeconds(expiresIn));
        return token;
    }

    // ── Named themes ─────────────────────────────────────────────────────────

    /// <summary>Returns all named themes saved for a Keycloak instance.</summary>
    public async Task<List<KeycloakTheme>> GetNamedThemesAsync(
        Guid tenantId, Guid configId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KeycloakThemes
            .Where(t => t.TenantId == tenantId && t.KeycloakComponentConfigId == configId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    /// <summary>Creates a new named theme for a Keycloak instance.</summary>
    public async Task<KeycloakTheme> CreateNamedThemeAsync(
        Guid tenantId, Guid configId, string name,
        string? loginTheme, string? accountTheme,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakTheme theme = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeycloakComponentConfigId = configId,
            Name = name.Trim(),
            LoginTheme = string.IsNullOrWhiteSpace(loginTheme) ? null : loginTheme,
            AccountTheme = string.IsNullOrWhiteSpace(accountTheme) ? null : accountTheme,
        };

        db.KeycloakThemes.Add(theme);
        await db.SaveChangesAsync(ct);
        return theme;
    }

    /// <summary>Updates the name and native theme properties of a named theme.</summary>
    public async Task UpdateNamedThemeAsync(
        Guid tenantId, Guid themeId, string name,
        string? loginTheme, string? accountTheme,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakTheme theme = await db.KeycloakThemes
            .FirstOrDefaultAsync(t => t.Id == themeId && t.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Theme not found.");

        theme.Name = name.Trim();
        theme.LoginTheme = string.IsNullOrWhiteSpace(loginTheme) ? null : loginTheme;
        theme.AccountTheme = string.IsNullOrWhiteSpace(accountTheme) ? null : accountTheme;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a named theme and removes its CSS vault secrets.
    /// Realms referencing this theme will have their KeycloakThemeId set to null (SetNull cascade).
    /// </summary>
    public async Task DeleteNamedThemeAsync(
        Guid tenantId, Guid themeId, Guid componentId, CancellationToken ct = default)
    {
        // Remove CSS vault secrets for all theme types before deleting the DB record.
        List<VaultSecret> secrets = await vaultService.GetComponentSecretsAsync(tenantId, componentId, ct);
        string prefix = $"named-theme-{themeId}-";
        foreach (VaultSecret s in secrets.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            await vaultService.DeleteSecretAsync(s.Id, ct);
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KeycloakTheme? theme = await db.KeycloakThemes
            .FirstOrDefaultAsync(t => t.Id == themeId && t.TenantId == tenantId, ct);
        if (theme is not null)
        {
            db.KeycloakThemes.Remove(theme);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Returns CSS for the given type from a named theme's vault slot, or null if none saved.</summary>
    public async Task<string?> GetNamedThemeCssAsync(
        Guid tenantId, Guid componentId, Guid themeId, string themeType, CancellationToken ct = default)
        => await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-{themeType}-css", ct);

    /// <summary>Persists CSS for the given type in a named theme's vault slot (not synced to K8s).</summary>
    public async Task SaveNamedThemeCssAsync(
        Guid tenantId, Guid componentId, Guid themeId, string themeType, string css,
        CancellationToken ct = default)
    {
        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(
            tenantId, componentId, $"named-theme-{themeId}-{themeType}-css", css, ct);
    }

    /// <summary>
    /// Assigns a named theme to a realm and pushes its native login/account themes to Keycloak.
    /// Pass null themeId to detach the realm from any named theme (leaves Keycloak native themes as-is).
    /// </summary>
    public async Task AssignNamedThemeToRealmAsync(
        Guid tenantId, Guid realmId, Guid? themeId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakRealm realm = await db.KeycloakRealms
            .Include(r => r.ComponentConfig)
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        if (themeId is null)
        {
            realm.KeycloakThemeId = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        KeycloakTheme theme = await db.KeycloakThemes
            .FirstOrDefaultAsync(t => t.Id == themeId && t.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Theme not found.");

        // Apply the theme's native login/account theme to Keycloak.
        string token = await GetAdminTokenAsync(tenantId, realm.ComponentConfig, ct);
        HttpClient http = CreateHttpClient();

        var patch = new { loginTheme = theme.LoginTheme ?? "", accountTheme = theme.AccountTheme ?? "" };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();

        realm.KeycloakThemeId = themeId;
        realm.LoginTheme = theme.LoginTheme;
        realm.AccountTheme = theme.AccountTheme;
        await db.SaveChangesAsync(ct);
    }

    // ── Themes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns available theme names per type (login, account, admin, email, welcome)
    /// from the Keycloak server info API. Returns empty dict on failure.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetAvailableThemesAsync(
        Guid tenantId, Guid configId, CancellationToken ct = default)
    {
        KeycloakComponentConfig config = await GetConfigAsync(tenantId, configId, ct);
        string token = await GetAdminTokenAsync(tenantId, config, ct);

        HttpClient http = CreateHttpClient();
        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{config.AdminUrl}/admin/serverinfo")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        if (!doc.RootElement.TryGetProperty("themes", out JsonElement themesEl))
        {
            return [];
        }

        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty typeProp in themesEl.EnumerateObject())
        {
            List<string> names = [];

            foreach (JsonElement themeEl in typeProp.Value.EnumerateArray())
            {
                string? name = themeEl.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            result[typeProp.Name] = names;
        }

        return result;
    }

    /// <summary>Returns stored custom CSS for the given theme type, or null if none saved.</summary>
    public async Task<string?> GetThemeCssAsync(
        Guid tenantId, Guid clusterComponentId, string themeType, CancellationToken ct = default)
    {
        return await vaultService.GetComponentSecretValueAsync(
            tenantId, clusterComponentId, $"theme-{themeType}-css", ct);
    }

    /// <summary>Persists custom CSS for the given theme type in the vault (not synced to K8s).</summary>
    public async Task SaveThemeCssAsync(
        Guid tenantId, Guid clusterComponentId, string themeType, string css, CancellationToken ct = default)
    {
        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, $"theme-{themeType}-css", css, ct);
    }

    // ── Cross-instance migration ──────────────────────────────────────────────

    /// <summary>
    /// Copies all users from one realm to another (cross-instance supported).
    /// User credentials are not transferred — passwords are hashed and not
    /// accessible via the Admin REST API. Users will need to reset their passwords
    /// on the target instance.
    /// Users that already exist on the target (same username) are skipped.
    /// </summary>
    public async Task<CopyResult> CopyUsersAsync(
        Guid tenantId, Guid sourceRealmId, Guid targetRealmId, CancellationToken ct = default)
    {
        (KeycloakRealm sourceRealm, string srcToken) = await LoadRealmAndTokenAsync(tenantId, sourceRealmId, ct);
        (KeycloakRealm targetRealm, string dstToken) = await LoadRealmAndTokenAsync(tenantId, targetRealmId, ct);

        HttpClient http = CreateHttpClient();
        CopyResult result = new();

        static string srcUrl(KeycloakRealm r, string path) =>
            $"{r.ComponentConfig.AdminUrl}/admin/realms/{r.RealmName}/{path}";

        // Skip these server-managed fields when posting to the target.
        HashSet<string> skip = ["id", "createdTimestamp", "totp",
            "disableableCredentialTypes", "requiredActions", "notBefore", "access"];

        int first = 0;
        const int pageSize = 100;

        while (true)
        {
            HttpResponseMessage getResp = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get,
                    srcUrl(sourceRealm, $"users?first={first}&max={pageSize}&briefRepresentation=false"))
                {
                    Headers = { Authorization = new AuthenticationHeaderValue("Bearer", srcToken) }
                }, ct);

            if (!getResp.IsSuccessStatusCode)
            {
                string body = await getResp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Failed to fetch users from source realm ({(int)getResp.StatusCode}): {body}");
            }

            string pageJson = await getResp.Content.ReadAsStringAsync(ct);
            using JsonDocument page = JsonDocument.Parse(pageJson);
            JsonElement[] users = [.. page.RootElement.EnumerateArray()];

            if (users.Length == 0) break;

            foreach (JsonElement user in users)
            {
                string username = user.TryGetProperty("username", out JsonElement un)
                    ? un.GetString() ?? "" : "";

                try
                {
                    JsonNode? node = JsonNode.Parse(user.GetRawText());
                    if (node is not JsonObject obj) continue;

                    foreach (string key in skip)
                        obj.Remove(key);

                    HttpResponseMessage postResp = await http.SendAsync(
                        new HttpRequestMessage(HttpMethod.Post,
                            srcUrl(targetRealm, "users"))
                        {
                            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", dstToken) },
                            Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json")
                        }, ct);

                    if (postResp.StatusCode == System.Net.HttpStatusCode.Conflict)
                        result.Skipped++;
                    else if (!postResp.IsSuccessStatusCode)
                        result.Errors.Add($"{username}: {(int)postResp.StatusCode} {await postResp.Content.ReadAsStringAsync(ct)}");
                    else
                        result.Copied++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{username}: {ex.Message}");
                }
            }

            if (users.Length < pageSize) break;
            first += pageSize;
        }

        return result;
    }

    /// <summary>
    /// Copies the CSS overrides for all theme types (login, account, admin, email)
    /// stored in EntKube's vault from one Keycloak component to another.
    /// Returns the number of theme types that had CSS and were copied.
    /// </summary>
    public async Task<int> CopyThemeCssAsync(
        Guid tenantId, Guid sourceComponentId, Guid targetComponentId, CancellationToken ct = default)
    {
        int copied = 0;
        foreach (string themeType in new[] { "login", "account", "admin", "email" })
        {
            string? css = await GetThemeCssAsync(tenantId, sourceComponentId, themeType, ct);
            if (!string.IsNullOrEmpty(css))
            {
                await SaveThemeCssAsync(tenantId, targetComponentId, themeType, css, ct);
                copied++;
            }
        }
        return copied;
    }

    // ── S3 helpers ────────────────────────────────────────────────────────────

    private async Task UploadToS3Async(
        Guid tenantId, StorageLink link, string key, byte[] data, CancellationToken ct)
    {
        using AmazonS3Client s3 = await CreateS3ClientAsync(tenantId, link, ct);

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = link.BucketName,
            Key = key,
            InputStream = new MemoryStream(data),
            ContentType = "application/json"
        }, ct);
    }

    private async Task<byte[]> DownloadFromS3Async(
        Guid tenantId, StorageLink link, string key, CancellationToken ct)
    {
        using AmazonS3Client s3 = await CreateS3ClientAsync(tenantId, link, ct);

        GetObjectResponse obj = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = link.BucketName,
            Key = key
        }, ct);

        using MemoryStream ms = new();
        await obj.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task DeleteFromS3Async(
        Guid tenantId, StorageLink link, string key, CancellationToken ct)
    {
        using AmazonS3Client s3 = await CreateS3ClientAsync(tenantId, link, ct);

        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = link.BucketName,
            Key = key
        }, ct);
    }

    private async Task<AmazonS3Client> CreateS3ClientAsync(
        Guid tenantId, StorageLink link, CancellationToken ct)
    {
        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(
            tenantId, link.Id, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(
            tenantId, link.Id, "SECRET_KEY", ct);

        return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = link.Endpoint,
            ForcePathStyle = true
        });
    }

    private static byte[] RenameRealmInJson(byte[] json, string newRealmName)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();

        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "realm")
                writer.WriteString("realm", newRealmName);
            else
                prop.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }
}

internal static class JsonElementExtensions
{
    internal static string? GetStringOrDefault(this JsonElement el, string property)
    {
        return el.TryGetProperty(property, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
    }
}
