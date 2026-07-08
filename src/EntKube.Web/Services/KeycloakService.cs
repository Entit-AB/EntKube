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
    /// <summary>Available login theme provider names from Keycloak server info.</summary>
    public List<string> Themes { get; set; } = [];
    /// <summary>Available account theme provider names from Keycloak server info.</summary>
    public List<string> AccountThemes { get; set; } = [];
}

public class CopyResult
{
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class KeycloakClientInfo
{
    public required string Id { get; set; }
    public required string ClientId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool PublicClient { get; set; }
    public bool BearerOnly { get; set; }
    public string? Protocol { get; set; }
}

public class KeycloakClientDetail : KeycloakClientInfo
{
    public string? BaseUrl { get; set; }
    public string? RootUrl { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public List<string> WebOrigins { get; set; } = [];
    public bool StandardFlowEnabled { get; set; }
    public bool DirectAccessGrantsEnabled { get; set; }
    public bool ServiceAccountsEnabled { get; set; }
    public bool ImplicitFlowEnabled { get; set; }
}

public class KeycloakProtocolMapper
{
    public string Id { get; set; } = "";
    public required string Name { get; set; }
    public string Protocol { get; set; } = "openid-connect";
    public required string ProtocolMapper { get; set; }
    public Dictionary<string, string> Config { get; set; } = [];
}

public class KeycloakRole
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Composite { get; set; }
    public bool ClientRole { get; set; }
}

public class KeycloakRealmSettings
{
    public int AccessTokenLifespan { get; set; } = 300;
    public int SsoSessionIdleTimeout { get; set; } = 1800;
    public int SsoSessionMaxLifespan { get; set; } = 36000;
    public int OfflineSessionIdleTimeout { get; set; } = 2592000;
    public bool RegistrationAllowed { get; set; }
    public bool ResetPasswordAllowed { get; set; }
    public bool RememberMe { get; set; }
    public bool LoginWithEmailAllowed { get; set; } = true;
    public bool DuplicateEmailsAllowed { get; set; }
    public bool VerifyEmail { get; set; }
    public bool BruteForceProtected { get; set; }
    public int FailureFactor { get; set; } = 30;
    public int WaitIncrementSeconds { get; set; } = 60;
    public int MaxFailureWaitSeconds { get; set; } = 900;
    public string SmtpHost { get; set; } = "";
    public string SmtpPort { get; set; } = "465";
    public string SmtpFrom { get; set; } = "";
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public bool SmtpSsl { get; set; }
    public bool SmtpStarttls { get; set; }
    public bool SmtpAuth { get; set; }
    public string PasswordPolicy { get; set; } = "";
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
    CnpgService cnpgService,
    IKubernetesClientFactory k8sFactory)
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
        List<string> accountThemes = [];

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

                if (tDoc.RootElement.TryGetProperty("themes", out JsonElement themesEl))
                {
                    if (themesEl.TryGetProperty("login", out JsonElement loginThemes))
                    {
                        themes = loginThemes.EnumerateArray()
                            .Select(t => t.GetStringOrDefault("name") ?? "")
                            .Where(t => t.Length > 0)
                            .ToList();
                    }

                    if (themesEl.TryGetProperty("account", out JsonElement acctThemes))
                    {
                        accountThemes = acctThemes.EnumerateArray()
                            .Select(t => t.GetStringOrDefault("name") ?? "")
                            .Where(t => t.Length > 0)
                            .ToList();
                    }
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
            Themes = themes,
            AccountThemes = accountThemes
        };
    }

    /// <summary>
    /// Sets (or clears) the automated backup schedule for a realm. EntKube-side config only —
    /// <c>KeycloakBackupSchedulerService</c> reads it and runs the export on cadence. A schedule
    /// requires a storage bucket. Pass a null/blank schedule to disable automated backups.
    /// </summary>
    public async Task SetBackupScheduleAsync(
        Guid tenantId, Guid realmId, string? schedule, Guid? storageLinkId,
        CancellationToken ct = default)
    {
        schedule = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();
        Guid? bucket = storageLinkId is null || storageLinkId == Guid.Empty ? null : storageLinkId;

        if (schedule is not null)
        {
            string[] parts = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                Cronos.CronExpression.Parse(schedule,
                    parts.Length >= 6 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard);
            }
            catch (Cronos.CronFormatException ex)
            {
                throw new InvalidOperationException($"Invalid cron schedule: {ex.Message}");
            }
            if (bucket is null)
                throw new InvalidOperationException("A storage bucket is required for scheduled backups.");
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KeycloakRealm realm = await db.KeycloakRealms
            .FirstOrDefaultAsync(r => r.Id == realmId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Realm not found.");

        realm.BackupSchedule = schedule;
        realm.StorageLinkId = bucket ?? realm.StorageLinkId;
        await db.SaveChangesAsync(ct);
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

        // Serialize only non-null fields. Keycloak treats "" as "use default theme", so
        // we send null (which Keycloak ignores/preserves) when the user wants default.
        string patchJson = JsonSerializer.Serialize(new
        {
            loginTheme = string.IsNullOrEmpty(loginTheme) ? null : loginTheme,
            accountTheme = string.IsNullOrEmpty(accountTheme) ? null : accountTheme
        });

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(patchJson, Encoding.UTF8, "application/json")
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

    public async Task<List<KeycloakRealm>> GetAllRealmsForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KeycloakRealms
            .Include(r => r.LinkedApp).ThenInclude(a => a!.Customer)
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.DisplayName)
            .ToListAsync(ct);
    }

    // ── Identity bindings ─────────────────────────────────────────────────────

    public async Task<List<IdentityBinding>> GetIdentityBindingsForDeploymentAsync(
        Guid appDeploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.IdentityBindings
            .Include(b => b.KeycloakRealm)
            .Where(b => b.AppDeploymentId == appDeploymentId)
            .OrderBy(b => b.ClientId)
            .ToListAsync(ct);
    }

    public async Task<IdentityBinding> CreateIdentityBindingAsync(
        Guid realmId, Guid appDeploymentId,
        string clientUuid, string clientId, string k8sSecretName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        IdentityBinding binding = new()
        {
            Id = Guid.NewGuid(),
            KeycloakRealmId = realmId,
            AppDeploymentId = appDeploymentId,
            ClientUuid = clientUuid,
            ClientId = clientId,
            KubernetesSecretName = k8sSecretName
        };

        db.IdentityBindings.Add(binding);
        await db.SaveChangesAsync(ct);
        return binding;
    }

    public async Task RemoveIdentityBindingAsync(Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        IdentityBinding binding = await db.IdentityBindings
            .FirstOrDefaultAsync(b => b.Id == bindingId, ct)
            ?? throw new InvalidOperationException("Identity binding not found.");
        db.IdentityBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fetches the OIDC client secret from Keycloak and writes it — together with
    /// the issuer URL and client ID — into a Kubernetes Secret in the app deployment's
    /// namespace. The Secret contains OIDC_ISSUER_URL, OIDC_CLIENT_ID, OIDC_CLIENT_SECRET.
    /// </summary>
    public async Task SyncIdentityBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        IdentityBinding binding = await db.IdentityBindings
            .Include(b => b.KeycloakRealm).ThenInclude(r => r.ComponentConfig)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId, ct)
            ?? throw new InvalidOperationException("Identity binding not found.");

        string? clientSecret = await GetClientSecretAsync(
            tenantId, binding.KeycloakRealmId, binding.ClientUuid, ct);

        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                $"No client secret found for client '{binding.ClientId}'. " +
                "Ensure the client has credentials enabled in Keycloak.");

        string adminUrl = binding.KeycloakRealm.ComponentConfig.AdminUrl!.TrimEnd('/');
        string issuerUrl = $"{adminUrl}/realms/{binding.KeycloakRealm.RealmName}";
        string kubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
        string ns = binding.AppDeployment.Namespace;

        await k8sFactory.EnsureNamespaceAsync(ns, kubeconfig, ct);

        string secretManifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {binding.KubernetesSecretName}
              namespace: {ns}
            type: Opaque
            data:
              OIDC_ISSUER_URL: {B64Id(issuerUrl)}
              OIDC_CLIENT_ID: {B64Id(binding.ClientId)}
              OIDC_CLIENT_SECRET: {B64Id(clientSecret)}
            """;

        await k8sFactory.ApplyManifestAsync(secretManifest, kubeconfig, ct);

        binding.LastSyncedAt = DateTime.UtcNow;
        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.IdentityBindings.Update(binding);
        await db2.SaveChangesAsync(ct);
    }

    private static string B64Id(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

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

    /// <summary>
    /// Derives a valid Keycloak theme provider name (slug) from a human-readable theme name.
    /// Example: "Capio Blue" → "capio-blue".
    /// </summary>
    private static string ToThemeSlug(string name)
    {
        string s = name.Trim().ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9]+", "-");
        s = s.Trim('-');
        return string.IsNullOrEmpty(s) ? "entkube-theme" : s;
    }

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

        // For managed Keycloak instances (ClusterComponentId set), auto-derive the login theme
        // slug from the theme name so the slug-named theme directory (deployed as a ConfigMap)
        // is what Keycloak picks up. The user can override this with an explicit loginTheme.
        string trimmedName = name.Trim();
        string? resolvedLoginTheme = string.IsNullOrWhiteSpace(loginTheme) ? null : loginTheme;
        if (resolvedLoginTheme is null)
        {
            KeycloakComponentConfig? config = await db.KeycloakComponentConfigs
                .FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct);
            if (config?.ClusterComponentId is not null)
                resolvedLoginTheme = ToThemeSlug(trimmedName);
        }

        KeycloakTheme theme = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeycloakComponentConfigId = configId,
            Name = trimmedName,
            LoginTheme = resolvedLoginTheme,
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

    /// <summary>
    /// Creates a duplicate of a named theme (new DB record + copy of all vault secrets)
    /// within the same Keycloak component. The duplicate is named "{original} (copy)".
    /// K8s deployment is not triggered for the copy.
    /// </summary>
    public async Task<KeycloakTheme> DuplicateNamedThemeAsync(
        Guid tenantId, Guid sourceThemeId, Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KeycloakTheme src = await db.KeycloakThemes
            .FirstOrDefaultAsync(t => t.Id == sourceThemeId && t.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Theme not found.");

        KeycloakTheme dup = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeycloakComponentConfigId = src.KeycloakComponentConfigId,
            Name = src.Name + " (copy)",
            LoginTheme = src.LoginTheme,
            AccountTheme = src.AccountTheme,
        };
        db.KeycloakThemes.Add(dup);
        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);

        // Copy variables
        string? vars = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{src.Id}-variables", ct);
        if (!string.IsNullOrEmpty(vars))
            await vaultService.SetComponentSecretAsync(
                tenantId, componentId, $"named-theme-{dup.Id}-variables", vars, ct);

        // Copy CSS for all types
        foreach (string themeType in new[] { "login", "account", "admin", "email" })
        {
            string? css = await vaultService.GetComponentSecretValueAsync(
                tenantId, componentId, $"named-theme-{src.Id}-{themeType}-css", ct);
            if (!string.IsNullOrEmpty(css))
                await vaultService.SetComponentSecretAsync(
                    tenantId, componentId, $"named-theme-{dup.Id}-{themeType}-css", css, ct);
        }

        // Copy resources
        List<string> resources = await ListThemeResourcesAsync(tenantId, componentId, src.Id, ct);
        foreach (string filename in resources)
        {
            string? b64 = await vaultService.GetComponentSecretValueAsync(
                tenantId, componentId, $"named-theme-{src.Id}-resource-{filename}", ct);
            if (!string.IsNullOrEmpty(b64))
                await vaultService.SetComponentSecretAsync(
                    tenantId, componentId, $"named-theme-{dup.Id}-resource-{filename}", b64, ct);
        }

        return dup;
    }

    // ── Theme Variables (structured visual editor) ────────────────────────────

    private static readonly JsonSerializerOptions ThemeVarsJsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Loads the structured <see cref="ThemeVariables"/> for a named theme from the vault.
    /// Returns defaults if none have been saved yet.
    /// </summary>
    public async Task<ThemeVariables> GetThemeVariablesAsync(
        Guid tenantId, Guid componentId, Guid themeId, CancellationToken ct = default)
    {
        string? json = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-variables", ct);
        if (string.IsNullOrEmpty(json))
            return new ThemeVariables();
        return JsonSerializer.Deserialize<ThemeVariables>(json, ThemeVarsJsonOptions) ?? new ThemeVariables();
    }

    /// <summary>Returns up to 5 snapshots of the theme variables saved before previous saves.</summary>
    public async Task<List<ThemeHistoryEntry>> GetThemeHistoryAsync(
        Guid tenantId, Guid componentId, Guid themeId, CancellationToken ct = default)
    {
        string? json = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-history", ct);
        if (string.IsNullOrEmpty(json))
            return [];
        return JsonSerializer.Deserialize<List<ThemeHistoryEntry>>(json, ThemeVarsJsonOptions) ?? [];
    }

    private async Task AppendThemeHistoryAsync(
        Guid tenantId, Guid componentId, Guid themeId, string currentVarsJson,
        CancellationToken ct)
    {
        ThemeVariables? current = JsonSerializer.Deserialize<ThemeVariables>(currentVarsJson, ThemeVarsJsonOptions);
        if (current is null)
            return;

        string? historyJson = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-history", ct);
        List<ThemeHistoryEntry> entries = string.IsNullOrEmpty(historyJson)
            ? []
            : JsonSerializer.Deserialize<List<ThemeHistoryEntry>>(historyJson, ThemeVarsJsonOptions) ?? [];

        entries.Insert(0, new ThemeHistoryEntry { SavedAt = DateTimeOffset.UtcNow, Variables = current });
        if (entries.Count > 5)
            entries = entries[..5];

        await vaultService.SetComponentSecretAsync(
            tenantId, componentId, $"named-theme-{themeId}-history",
            JsonSerializer.Serialize(entries, ThemeVarsJsonOptions), ct);
    }

    /// <summary>
    /// Persists <see cref="ThemeVariables"/>, auto-generates the login CSS from them
    /// (embedding any logo / background-image data URIs), and saves + deploys the CSS.
    /// Returns a non-null deploy error message if vault save succeeded but K8s deploy failed.
    /// </summary>
    public async Task<string?> SaveThemeVariablesAsync(
        Guid tenantId, Guid componentId, Guid themeId, ThemeVariables vars,
        CancellationToken ct = default)
    {
        await vaultService.InitializeVaultAsync(tenantId, ct);

        // Snapshot the current (pre-save) state into version history.
        string? existingJson = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-variables", ct);
        if (!string.IsNullOrEmpty(existingJson))
            await AppendThemeHistoryAsync(tenantId, componentId, themeId, existingJson, ct);

        string json = JsonSerializer.Serialize(vars, ThemeVarsJsonOptions);
        await vaultService.SetComponentSecretAsync(
            tenantId, componentId, $"named-theme-{themeId}-variables", json, ct);

        string? logoDataUri = null;
        if (vars.ShowLogo && !string.IsNullOrEmpty(vars.LogoResourceName))
            logoDataUri = await GetThemeResourceDataUriAsync(tenantId, componentId, themeId, vars.LogoResourceName, ct);

        string? bgDataUri = null;
        if (vars.UseBackgroundImage && !string.IsNullOrEmpty(vars.BackgroundImageResourceName))
            bgDataUri = await GetThemeResourceDataUriAsync(tenantId, componentId, themeId, vars.BackgroundImageResourceName, ct);

        string css = GenerateThemeCss(vars, logoDataUri, bgDataUri);
        return await SaveNamedThemeCssAsync(tenantId, componentId, themeId, "login", css, ct);
    }

    // ── Theme Resources (uploaded images) ────────────────────────────────────

    /// <summary>Returns the filenames of all uploaded resources for a named theme.</summary>
    public async Task<List<string>> ListThemeResourcesAsync(
        Guid tenantId, Guid componentId, Guid themeId, CancellationToken ct = default)
    {
        List<VaultSecret> secrets = await vaultService.GetComponentSecretsAsync(tenantId, componentId, ct);
        string prefix = $"named-theme-{themeId}-resource-";
        return secrets
            .Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(s => s.Name[prefix.Length..])
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>Stores an uploaded resource (base64-encoded) in the vault.</summary>
    public async Task UploadThemeResourceAsync(
        Guid tenantId, Guid componentId, Guid themeId, string filename, string base64Content,
        CancellationToken ct = default)
    {
        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(
            tenantId, componentId, $"named-theme-{themeId}-resource-{filename}", base64Content, ct);
    }

    /// <summary>Removes an uploaded resource from the vault.</summary>
    public async Task DeleteThemeResourceAsync(
        Guid tenantId, Guid componentId, Guid themeId, string filename,
        CancellationToken ct = default)
    {
        List<VaultSecret> secrets = await vaultService.GetComponentSecretsAsync(tenantId, componentId, ct);
        string key = $"named-theme-{themeId}-resource-{filename}";
        VaultSecret? secret = secrets.FirstOrDefault(s => s.Name == key);
        if (secret is not null)
            await vaultService.DeleteSecretAsync(secret.Id, ct);
    }

    /// <summary>Returns a CSS data URI (<c>data:image/png;base64,…</c>) for a stored resource, or null.</summary>
    public async Task<string?> GetThemeResourceDataUriAsync(
        Guid tenantId, Guid componentId, Guid themeId, string filename,
        CancellationToken ct = default)
    {
        string? base64 = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-resource-{filename}", ct);
        if (string.IsNullOrEmpty(base64))
            return null;
        string mime = GetResourceMimeType(filename);
        return $"data:{mime};base64,{base64}";
    }

    private static string GetResourceMimeType(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private static bool IsImageResource(string filename) =>
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp" }
            .Contains(Path.GetExtension(filename).ToLowerInvariant());

    /// <summary>
    /// Resolves the ordered set of images to publish under a theme's
    /// <c>resources/img/</c> directory: every uploaded image under its own name, plus the
    /// selected favicon resource republished as <c>favicon.ico</c> (the fixed path the
    /// Keycloak login template references). Returned as (deployName → sourceName) pairs in a
    /// stable ordinal order so the live StatefulSet patch and the Helm-values builder emit an
    /// identical ConfigMap item list (otherwise a later <c>helm upgrade</c> would churn).
    /// </summary>
    private async Task<List<(string DeployName, string SourceName)>> ResolveDeployImagesAsync(
        Guid tenantId, Guid componentId, Guid themeId, CancellationToken ct)
    {
        List<string> resourceNames = await ListThemeResourcesAsync(tenantId, componentId, themeId, ct);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal); // deployName → sourceName
        foreach (string n in resourceNames.Where(IsImageResource))
            map[n] = n;

        ThemeVariables vars = await GetThemeVariablesAsync(tenantId, componentId, themeId, ct);
        if (!string.IsNullOrEmpty(vars.FaviconResourceName)
            && resourceNames.Contains(vars.FaviconResourceName))
            map["favicon.ico"] = vars.FaviconResourceName!; // republish under the favicon path

        return map.Select(kvp => (kvp.Key, kvp.Value)).ToList();
    }

    // ── CSS generation from ThemeVariables ───────────────────────────────────

    private static string GenerateThemeCss(ThemeVariables v, string? logoDataUri, string? bgDataUri)
        => v.BaseTheme == "keycloak"
            ? GenerateV1ThemeCss(v, logoDataUri, bgDataUri)
            : GenerateV2ThemeCss(v, logoDataUri, bgDataUri);

    private static string CardShadowValue(int level) => level switch
    {
        0 => "none",
        1 => "0 1px 3px rgba(0,0,0,.12), 0 1px 2px rgba(0,0,0,.08)",
        2 => "0 4px 12px rgba(0,0,0,.16), 0 2px 4px rgba(0,0,0,.12)",
        3 => "0 12px 28px rgba(0,0,0,.22), 0 4px 8px rgba(0,0,0,.14)",
        _ => "none"
    };

    /// <summary>True when a <c>#rgb</c>/<c>#rrggbb</c> colour is dark (perceived luma &lt; 50%).</summary>
    private static bool IsColorDark(string hex)
    {
        try
        {
            string h = hex.TrimStart('#');
            if (h.Length == 3)
                h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            if (h.Length < 6) return false;
            int r = Convert.ToInt32(h[0..2], 16);
            int g = Convert.ToInt32(h[2..4], 16);
            int b = Convert.ToInt32(h[4..6], 16);
            return (r * 299 + g * 587 + b * 114) / 1000 < 128;
        }
        catch { return false; }
    }

    /// <summary>
    /// CSS <c>color-scheme</c> value matching a background colour. Native form controls
    /// follow the OS theme unless this is pinned, which is why Safari (and others) paint
    /// light-grey text on a themed input under OS dark mode. Pinning it to the input's
    /// own background keeps the browser's default text/caret colours legible.
    /// </summary>
    private static string ColorScheme(string backgroundHex) => IsColorDark(backgroundHex) ? "dark" : "light";

    /// <summary>
    /// Emits rules that force the configured input text/background colour onto the real
    /// <c>&lt;input&gt;/&lt;select&gt;/&lt;textarea&gt;</c> element (PatternFly only sets a CSS
    /// var on a wrapper), plus a pinned <c>color-scheme</c>, so typed text stays readable
    /// regardless of the visitor's browser dark-mode setting.
    /// </summary>
    private static void AppendInputColorScheme(StringBuilder sb, ThemeVariables v, string extraSelectors)
    {
        string scheme = ColorScheme(v.InputBackground);
        sb.AppendLine("\n/* Pin input colours so browser dark-mode doesn't override typed text */");
        sb.AppendLine(
            "input[type=\"text\"], input[type=\"email\"], input[type=\"password\"],\n" +
            "input[type=\"number\"], input[type=\"tel\"], input[type=\"url\"], input[type=\"search\"],\n" +
            $"select, textarea{extraSelectors} {{");
        sb.AppendLine($"  color: {v.InputTextColor} !important;");
        sb.AppendLine($"  -webkit-text-fill-color: {v.InputTextColor} !important;");
        sb.AppendLine($"  background-color: {v.InputBackground} !important;");
        sb.AppendLine($"  color-scheme: {scheme};");
        sb.AppendLine("}");
        sb.AppendLine($"input::placeholder, textarea::placeholder {{ color: {v.MutedTextColor} !important; opacity: 1; }}");
        // Browser autofill paints its own background/text — override both to keep contrast.
        sb.AppendLine("input:-webkit-autofill, input:-webkit-autofill:hover, input:-webkit-autofill:focus {");
        sb.AppendLine($"  -webkit-text-fill-color: {v.InputTextColor} !important;");
        sb.AppendLine($"  -webkit-box-shadow: 0 0 0 1000px {v.InputBackground} inset !important;");
        sb.AppendLine("}");
    }

    private static void AppendLogoAndFooter(StringBuilder sb, ThemeVariables v,
        string? logoDataUri, string logoImgSelector, string logoHideSelector, string footerSelector)
    {
        if (!v.ShowLogo)
        {
            sb.AppendLine($"\n{logoHideSelector} {{ display: none !important; }}");
        }
        else
        {
            string? logoSrc = logoDataUri ?? (string.IsNullOrEmpty(v.LogoExternalUrl) ? null : v.LogoExternalUrl);
            sb.AppendLine($"\n{logoImgSelector} {{");
            if (!string.IsNullOrEmpty(logoSrc))
                sb.AppendLine($"  content: url('{logoSrc}');");
            sb.AppendLine($"  height: {v.LogoHeightPx}px;");
            sb.AppendLine("  width: auto;");
            sb.AppendLine("}");
        }

        if (!v.ShowFooter)
        {
            sb.AppendLine($"\n{footerSelector} {{ display: none !important; }}");
        }
        else if (!string.IsNullOrEmpty(v.FooterText))
        {
            sb.AppendLine($"\n{footerSelector}::before {{");
            sb.AppendLine($"  content: '{v.FooterText.Replace("'", "\\'")}';");
            sb.AppendLine("  display: block;");
            sb.AppendLine("  text-align: center;");
            sb.AppendLine($"  color: {v.MutedTextColor};");
            sb.AppendLine("  font-size: 0.8em;");
            sb.AppendLine("  margin-bottom: 0.5em;");
            sb.AppendLine("}");
        }

        if (!string.IsNullOrWhiteSpace(v.ExtraCss))
        {
            sb.AppendLine("\n/* ── Extra CSS ──────────────────────────────────────────────────── */");
            sb.AppendLine(v.ExtraCss.Trim());
        }
    }

    // ── keycloak.v2 — PatternFly 5 (Keycloak 20+) ────────────────────────────

    private static string GenerateV2ThemeCss(ThemeVariables v, string? logoDataUri, string? bgDataUri)
    {
        var sb = new StringBuilder("/* Generated by EntKube Theme Editor — do not edit manually */\n");
        sb.AppendLine("/* Base: keycloak.v2 (PatternFly 5 · Keycloak 20+) */\n");

        string? logoSrcV2 = logoDataUri ?? (string.IsNullOrEmpty(v.LogoExternalUrl) ? null : v.LogoExternalUrl);

        sb.AppendLine(":root {");
        sb.AppendLine($"  --pf-v5-global--primary-color--100: {v.PrimaryColor};");
        sb.AppendLine($"  --pf-v5-global--primary-color--200: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-v5-global--primary-color--300: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-v5-global--link--Color: {v.LinkColor};");
        sb.AppendLine($"  --pf-v5-global--link--Color--hover: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-v5-global--Color--100: {v.TextColor};");
        sb.AppendLine($"  --pf-v5-global--Color--200: {v.MutedTextColor};");
        sb.AppendLine($"  --pf-v5-global--Color--400: {v.MutedTextColor};");
        sb.AppendLine($"  --pf-v5-global--BackgroundColor--100: {v.PageBackground};");
        sb.AppendLine($"  --pf-v5-global--BackgroundColor--200: {v.CardBackground};");
        sb.AppendLine($"  --pf-v5-global--BorderColor--100: {v.InputBorderColor};");
        sb.AppendLine($"  --pf-v5-global--BorderRadius--sm: {v.ButtonRadiusPx}px;");
        sb.AppendLine($"  --pf-v5-global--BorderRadius--md: {v.InputRadiusPx}px;");
        sb.AppendLine($"  --pf-v5-global--BorderRadius--lg: {v.CardRadiusPx}px;");
        sb.AppendLine($"  --pf-v5-global--FontSize--md: {v.FontSizePx}px;");
        sb.AppendLine($"  --pf-v5-global--FontFamily--sans-serif: {v.FontFamily};");
        sb.AppendLine($"  --pf-v5-global--danger-color--100: {v.ErrorColor};");
        // keycloak.v2 uses --keycloak-logo-url (and dark variant) to render the brand logo.
        // Setting these vars is the primary override mechanism for this theme version.
        if (!v.ShowLogo)
        {
            sb.AppendLine("  --keycloak-logo-url: none;");
            sb.AppendLine("  --keycloak-logo-url-dark: none;");
        }
        else if (!string.IsNullOrEmpty(logoSrcV2))
        {
            sb.AppendLine($"  --keycloak-logo-url: url('{logoSrcV2}');");
            sb.AppendLine($"  --keycloak-logo-url-dark: url('{logoSrcV2}');");
        }
        sb.AppendLine("}");

        // Page / body — keycloak.v2 hosts its default wavy SVG background on
        // .pf-v5-c-login, so we must target it explicitly and clear background-image.
        string bgImg = v.UseBackgroundImage && !string.IsNullOrEmpty(bgDataUri)
            ? $"url('{bgDataUri}')" : "none";
        sb.AppendLine($"\nbody, .login-pf-page, .pf-v5-c-login {{");
        sb.AppendLine($"  background-color: {v.PageBackground};");
        sb.AppendLine($"  background-image: {bgImg} !important;");
        if (v.UseBackgroundImage && !string.IsNullOrEmpty(bgDataUri))
        {
            sb.AppendLine("  background-size: cover;");
            sb.AppendLine("  background-position: center;");
        }
        sb.AppendLine($"  --pf-v5-c-login--BackgroundColor: {v.PageBackground};");
        sb.AppendLine($"  --pf-v5-c-login--BackgroundImage: {bgImg};");
        sb.AppendLine($"  font-family: {v.FontFamily};");
        sb.AppendLine($"  font-size: {v.FontSizePx}px;");
        sb.AppendLine($"  color: {v.TextColor};");
        sb.AppendLine("}");

        // Card — the white login box is <main class="pf-v5-c-login__main">, NOT __main-body
        sb.AppendLine($"\n.pf-v5-c-login__main {{");
        sb.AppendLine($"  background-color: {v.CardBackground};");
        sb.AppendLine($"  border-radius: {v.CardRadiusPx}px;");
        sb.AppendLine($"  box-shadow: {CardShadowValue(v.CardShadowLevel)};");
        sb.AppendLine($"  --pf-v5-c-login__main--BackgroundColor: {v.CardBackground};");
        sb.AppendLine("}");
        sb.AppendLine($"\n.pf-v5-c-card, .card-pf {{");
        sb.AppendLine($"  background-color: {v.CardBackground};");
        sb.AppendLine($"  border-radius: {v.CardRadiusPx}px;");
        sb.AppendLine("}");
        sb.AppendLine($"\n.pf-v5-c-login__container {{");
        sb.AppendLine($"  max-width: {v.CardMaxWidthPx}px;");
        sb.AppendLine("}");

        // Header region (floats above the card on the page background)
        sb.AppendLine($"\n.pf-v5-c-login__header {{");
        sb.AppendLine($"  color: {v.HeaderTextColor};");
        sb.AppendLine("}");

        // Primary button
        sb.AppendLine($"\n.pf-v5-c-button.pf-m-primary {{");
        sb.AppendLine($"  --pf-v5-c-button--m-primary--BackgroundColor: {v.PrimaryColor};");
        sb.AppendLine($"  --pf-v5-c-button--m-primary--hover--BackgroundColor: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-v5-c-button--m-primary--Color: {v.ButtonTextColor};");
        sb.AppendLine($"  border-radius: {v.ButtonRadiusPx}px;");
        sb.AppendLine("}");

        // Form inputs — .pf-v5-c-form-control is the <span> wrapper; inner <input> inherits Color var
        sb.AppendLine($"\n.pf-v5-c-form-control {{");
        sb.AppendLine($"  --pf-v5-c-form-control--BorderColor: {v.InputBorderColor};");
        sb.AppendLine($"  --pf-v5-c-form-control--BackgroundColor: {v.InputBackground};");
        sb.AppendLine($"  --pf-v5-c-form-control--Color: {v.InputTextColor};");
        sb.AppendLine($"  border-radius: {v.InputRadiusPx}px;");
        sb.AppendLine("}");
        // Password field uses pf-v5-c-input-group which wraps the form-control + toggle button
        sb.AppendLine($"\n.pf-v5-c-input-group {{");
        sb.AppendLine($"  --pf-v5-c-input-group--BackgroundColor: {v.InputBackground};");
        sb.AppendLine($"  background-color: {v.InputBackground};");
        sb.AppendLine($"  border-radius: {v.InputRadiusPx}px;");
        sb.AppendLine("}");
        // Password show/hide toggle — keep neutral, do not inherit primary button styles
        sb.AppendLine($"\n.pf-v5-c-button.pf-m-control {{");
        sb.AppendLine($"  --pf-v5-c-button--m-control--BackgroundColor: {v.InputBackground};");
        sb.AppendLine($"  --pf-v5-c-button--m-control--Color: {v.TextColor};");
        sb.AppendLine($"  --pf-v5-c-button--m-control--BorderColor: {v.InputBorderColor};");
        sb.AppendLine("}");
        AppendInputColorScheme(sb, v, ", .pf-v5-c-form-control, .pf-v5-c-form-control > input");

        // Links
        sb.AppendLine($"\na, a:visited, .pf-v5-c-button.pf-m-link {{ color: {v.LinkColor}; }}");
        sb.AppendLine($"\na:hover {{ color: {v.PrimaryColorDark}; }}");

        // Logo / header brand
        // keycloak.v2 renders the logo as background-image on #kc-header-wrapper via
        // --keycloak-logo-url (set in :root above). The .kc-logo-text div shows the
        // realm name as text fallback; Keycloak's own CSS hides it via color:transparent
        // when a logo URL is active, so we don't need to hide it ourselves.
        if (!v.ShowLogo)
        {
            sb.AppendLine("\n#kc-header, #kc-header-wrapper { display: none !important; }");
        }
        else
        {
            // Realm name text color (visible when no logo image is set)
            sb.AppendLine($"\n.kc-logo-text span {{ color: {v.HeaderTextColor}; }}");
            if (!string.IsNullOrEmpty(logoSrcV2))
            {
                // Control the height of the background-image logo container
                sb.AppendLine($"\n#kc-header-wrapper {{");
                sb.AppendLine($"  height: {v.LogoHeightPx}px;");
                sb.AppendLine($"  min-height: {v.LogoHeightPx}px;");
                sb.AppendLine("  min-width: 120px;");
                sb.AppendLine("}");
            }
        }

        // Footer — keycloak.v2 renders TWO empty <div class="pf-v5-c-login__main-footer">
        // elements: one nested inside .pf-v5-c-login__main-body (directly under the form's
        // Sign In button) and one as a direct child of <main class="pf-v5-c-login__main">
        // at the very bottom. We only want the bottom one, so scope to the direct child —
        // otherwise the ::before content renders in both and the footer text appears twice.
        // The pseudo-element must be appended to the footer selector itself ("a, b::before"
        // would bind ::before to b only, leaving the footer empty).
        const string footerSelV2 = ".pf-v5-c-login__main > .pf-v5-c-login__main-footer";
        if (!v.ShowFooter)
        {
            sb.AppendLine($"\n{footerSelV2} {{ display: none !important; }}");
        }
        else if (!string.IsNullOrEmpty(v.FooterText))
        {
            sb.AppendLine($"\n{footerSelV2} {{ display: block !important; text-align: center !important; }}");
            sb.AppendLine($"\n{footerSelV2}::before {{");
            sb.AppendLine($"  content: '{v.FooterText.Replace("'", "\\'")}';");
            sb.AppendLine("  display: block;");
            sb.AppendLine("  text-align: center;");
            sb.AppendLine($"  color: {v.MutedTextColor};");
            sb.AppendLine("  font-size: 0.8em;");
            sb.AppendLine("  margin-bottom: 0.5em;");
            sb.AppendLine("}");
        }

        if (!string.IsNullOrWhiteSpace(v.ExtraCss))
        {
            sb.AppendLine("\n/* ── Extra CSS ──────────────────────────────────────────────────── */");
            sb.AppendLine(v.ExtraCss.Trim());
        }

        return sb.ToString();
    }

    // ── keycloak (Classic — Bootstrap + PatternFly 4, Keycloak 19 and earlier) ─

    private static string GenerateV1ThemeCss(ThemeVariables v, string? logoDataUri, string? bgDataUri)
    {
        var sb = new StringBuilder("/* Generated by EntKube Theme Editor — do not edit manually */\n");
        sb.AppendLine("/* Base: keycloak (Classic/Bootstrap + PatternFly 4 · Keycloak ≤19) */\n");

        // PF4 global custom properties (no "v5" infix)
        sb.AppendLine(":root {");
        sb.AppendLine($"  --pf-global--primary-color--100: {v.PrimaryColor};");
        sb.AppendLine($"  --pf-global--primary-color--200: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-global--link--Color: {v.LinkColor};");
        sb.AppendLine($"  --pf-global--link--Color--hover: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-global--Color--100: {v.TextColor};");
        sb.AppendLine($"  --pf-global--Color--200: {v.MutedTextColor};");
        sb.AppendLine($"  --pf-global--BackgroundColor--100: {v.PageBackground};");
        sb.AppendLine($"  --pf-global--BackgroundColor--200: {v.CardBackground};");
        sb.AppendLine($"  --pf-global--BorderColor--100: {v.InputBorderColor};");
        sb.AppendLine($"  --pf-global--BorderRadius--sm: {v.ButtonRadiusPx}px;");
        sb.AppendLine($"  --pf-global--FontSize--md: {v.FontSizePx}px;");
        sb.AppendLine($"  --pf-global--FontFamily--sans-serif: {v.FontFamily};");
        sb.AppendLine($"  --pf-global--danger-color--100: {v.ErrorColor};");
        sb.AppendLine("}");

        // Page background
        sb.AppendLine($"\nbody, .login-pf-page {{");
        sb.AppendLine($"  background-color: {v.PageBackground};");
        if (v.UseBackgroundImage && !string.IsNullOrEmpty(bgDataUri))
        {
            sb.AppendLine($"  background-image: url('{bgDataUri}');");
            sb.AppendLine("  background-size: cover;");
            sb.AppendLine("  background-position: center;");
        }
        sb.AppendLine($"  font-family: {v.FontFamily};");
        sb.AppendLine($"  font-size: {v.FontSizePx}px;");
        sb.AppendLine($"  color: {v.TextColor};");
        sb.AppendLine("}");

        // Header bar (full-width strip at the top of the page in v1)
        sb.AppendLine($"\n#kc-header, #kc-header-wrapper {{");
        sb.AppendLine($"  background-color: {v.HeaderBackground};");
        sb.AppendLine($"  color: {v.HeaderTextColor};");
        sb.AppendLine("}");

        // Card / form
        sb.AppendLine($"\n#kc-login, .card-pf, .login-pf-card, .pf-c-card {{");
        sb.AppendLine($"  background-color: {v.CardBackground};");
        sb.AppendLine($"  border-radius: {v.CardRadiusPx}px;");
        sb.AppendLine($"  box-shadow: {CardShadowValue(v.CardShadowLevel)};");
        sb.AppendLine("}");
        sb.AppendLine($"\n#kc-content, #kc-content-wrapper, #kc-login {{");
        sb.AppendLine($"  max-width: {v.CardMaxWidthPx}px;");
        sb.AppendLine("  margin-left: auto; margin-right: auto;");
        sb.AppendLine("}");

        // Primary button — Bootstrap .btn-primary AND PF4 .pf-c-button
        sb.AppendLine($"\n.btn-primary {{");
        sb.AppendLine($"  background-color: {v.PrimaryColor} !important;");
        sb.AppendLine($"  border-color: {v.PrimaryColor} !important;");
        sb.AppendLine($"  color: {v.ButtonTextColor} !important;");
        sb.AppendLine($"  border-radius: {v.ButtonRadiusPx}px !important;");
        sb.AppendLine("}");
        sb.AppendLine($"\n.btn-primary:hover, .btn-primary:focus {{");
        sb.AppendLine($"  background-color: {v.PrimaryColorDark} !important;");
        sb.AppendLine($"  border-color: {v.PrimaryColorDark} !important;");
        sb.AppendLine("}");
        sb.AppendLine($"\n.pf-c-button.pf-m-primary {{");
        sb.AppendLine($"  --pf-c-button--m-primary--BackgroundColor: {v.PrimaryColor};");
        sb.AppendLine($"  --pf-c-button--m-primary--hover--BackgroundColor: {v.PrimaryColorDark};");
        sb.AppendLine($"  --pf-c-button--m-primary--Color: {v.ButtonTextColor};");
        sb.AppendLine($"  border-radius: {v.ButtonRadiusPx}px;");
        sb.AppendLine("}");

        // Form inputs — Bootstrap .form-control AND PF4 .pf-c-form-control
        sb.AppendLine($"\n.form-control {{");
        sb.AppendLine($"  border-color: {v.InputBorderColor};");
        sb.AppendLine($"  background-color: {v.InputBackground};");
        sb.AppendLine($"  color: {v.InputTextColor};");
        sb.AppendLine($"  border-radius: {v.InputRadiusPx}px;");
        sb.AppendLine("}");
        sb.AppendLine($"\n.form-control:focus {{");
        sb.AppendLine($"  border-color: {v.PrimaryColor};");
        sb.AppendLine($"  box-shadow: 0 0 0 3px color-mix(in srgb, {v.PrimaryColor} 20%, transparent);");
        sb.AppendLine("}");
        sb.AppendLine($"\n.pf-c-form-control {{");
        sb.AppendLine($"  --pf-c-form-control--BorderColor: {v.InputBorderColor};");
        sb.AppendLine($"  background-color: {v.InputBackground};");
        sb.AppendLine($"  color: {v.InputTextColor};");
        sb.AppendLine($"  border-radius: {v.InputRadiusPx}px;");
        sb.AppendLine("}");
        AppendInputColorScheme(sb, v, ", .form-control, .pf-c-form-control");

        // Links
        sb.AppendLine($"\na, a:visited {{ color: {v.LinkColor}; }}");
        sb.AppendLine($"\na:hover {{ color: {v.PrimaryColorDark}; }}");

        AppendLogoAndFooter(sb, v, logoDataUri,
            logoImgSelector:  ".login-pf-brand img, #kc-logo-link",
            logoHideSelector: ".login-pf-brand, #kc-logo-link, #kc-logo",
            footerSelector:   "#kc-info");

        return sb.ToString();
    }

    /// <summary>Returns CSS for the given type from a named theme's vault slot, or null if none saved.</summary>
    public async Task<string?> GetNamedThemeCssAsync(
        Guid tenantId, Guid componentId, Guid themeId, string themeType, CancellationToken ct = default)
        => await vaultService.GetComponentSecretValueAsync(
            tenantId, componentId, $"named-theme-{themeId}-{themeType}-css", ct);

    /// <summary>
    /// Persists CSS for the given type in a named theme's vault slot and, for managed
    /// Keycloak instances, deploys it to the Keycloak pod via a Kubernetes ConfigMap +
    /// strategic-merge-patch Deployment update.
    /// Returns a non-null deploy error message if the vault save succeeded but K8s
    /// deployment failed — the caller can surface this without blocking the save.
    /// </summary>
    public async Task<string?> SaveNamedThemeCssAsync(
        Guid tenantId, Guid componentId, Guid themeId, string themeType, string css,
        CancellationToken ct = default)
    {
        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(
            tenantId, componentId, $"named-theme-{themeId}-{themeType}-css", css, ct);

        // Deploy to Kubernetes for managed Keycloak instances.
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KeycloakTheme? theme = await db.KeycloakThemes
            .Include(t => t.ComponentConfig)
                .ThenInclude(c => c.ClusterComponent!)
                .ThenInclude(cc => cc.Cluster)
            .FirstOrDefaultAsync(t => t.Id == themeId && t.TenantId == tenantId, ct);

        if (theme?.ComponentConfig.ClusterComponentId is null
            || theme.ComponentConfig.ClusterComponent is null)
            return null; // external Keycloak — vault only

        try
        {
            // Sync realm loginTheme BEFORE deploying (while Keycloak is still up).
            // If done after, the rolling restart makes Keycloak unavailable and the API
            // call fails silently — leaving the realm pointing at the wrong theme slug.
            // Also include realms whose stored loginTheme matches old slug variants (e.g.
            // "theme-{slug}" written by earlier code) so they get corrected on the next save.
            string deployedSlug = ToThemeSlug(theme.Name);
            List<Guid> realmIds = await db.KeycloakRealms
                .Where(r => r.TenantId == tenantId && (
                    r.KeycloakThemeId == theme.Id ||
                    r.LoginTheme == deployedSlug ||
                    r.LoginTheme == "theme-" + deployedSlug))
                .Select(r => r.Id)
                .ToListAsync(ct);
            List<string> syncErrors = [];
            foreach (Guid rid in realmIds)
            {
                try { await AssignNamedThemeToRealmAsync(tenantId, rid, theme.Id, ct); }
                catch (Exception ex) { syncErrors.Add(ex.Message); }
            }

            await DeployNamedThemeToClusterAsync(tenantId, theme, ct);

            if (syncErrors.Count > 0)
                return $"Theme deployed but realm sync failed: {string.Join("; ", syncErrors)}";

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Deploys a named theme to the Keycloak pod as a ConfigMap-backed theme directory.
    /// Creates or updates the ConfigMap with all saved CSS types, patches the Keycloak
    /// Deployment to mount it at the right theme path, and triggers a rolling restart
    /// so Keycloak picks up the new theme files (subPath mounts require a restart).
    /// No-op for external Keycloak instances (ClusterComponentId not set).
    /// </summary>
    // Theme copier init container, shared by the live StatefulSet patch
    // (DeployNamedThemeToClusterAsync) and the Helm-values builder
    // (BuildKeycloakThemeHelmExtrasAsync) so both emit an identical container.
    private const string ThemeCopierImage = "busybox:1.36";
    // NOTE: ConfigMap volume entries are symlinks (key → ..data/key). `cp` dereferences
    // symlinks by default (so theme.properties/login.css copy fine), but `find -type f`
    // evaluates the symlink itself (type l) and matches nothing — which silently dropped
    // every image, including favicon.ico. `find -L` makes the type test follow the symlink
    // to its real (regular-file) target; `cp -L` copies contents rather than a dangling link.
    private const string ThemeCopierCommandRaw =
        "mkdir -p /dest/resources/css /dest/resources/img; " +
        "cp /src/theme.properties /dest/theme.properties; " +
        "cp /src/login.css /dest/resources/css/login.css; " +
        "find -L /src/imgs -maxdepth 1 -type f -exec cp -L {} /dest/resources/img/ \\; 2>/dev/null; true";

    /// <summary>
    /// Builds the keycloakx Helm values fragment (<c>extraVolumes</c> /
    /// <c>extraInitContainers</c> / <c>extraVolumeMounts</c>) that wires every named theme of
    /// this Keycloak component into the StatefulSet.
    ///
    /// Themes were previously applied only as an out-of-band strategic patch on the live
    /// StatefulSet. The keycloakx chart renders <c>volumes</c> (Helm-managed) but not
    /// <c>initContainers</c> (chart leaves it alone), so a later <c>helm upgrade</c> reset the
    /// volumes back to the chart's set — dropping the theme volumes — while the out-of-band
    /// init container survived, leaving volumeMounts pointing at volumes that no longer existed
    /// and failing the upgrade. Rendering all three pieces through these <c>extra*</c> values
    /// makes Helm manage them together, so upgrades stay consistent.
    ///
    /// The emitted spec mirrors the live patch exactly (same names, item order, optional
    /// ConfigMap, copier command) so the two paths produce no churn. Returns an empty string
    /// when the component has no themes.
    /// </summary>
    public async Task<string> BuildKeycloakThemeHelmExtrasAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KeycloakTheme> themes = await db.KeycloakThemes
            .Include(t => t.ComponentConfig)
            .Where(t => t.ComponentConfig.ClusterComponentId == componentId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        if (themes.Count == 0)
            return "";

        // JSON-escaped copier command, identical to the one spliced into the live patch.
        string copierCmd = JsonSerializer.Serialize(ThemeCopierCommandRaw)[1..^1];

        var volumes = new StringBuilder();
        var initContainers = new StringBuilder();
        var volumeMounts = new StringBuilder();

        foreach (KeycloakTheme theme in themes)
        {
            string cmName       = $"entkube-theme-{theme.Id:N}";
            string srcVolName   = $"{cmName}-src";
            string loginVolName = $"{cmName}-login";
            string copierName   = $"{cmName}-copier";
            string deploySlug   = "theme-" + ToThemeSlug(theme.Name);

            // Same source (vault) and ordering as the live patch, so the ConfigMap items
            // list matches byte-for-byte (includes the synthesized favicon.ico, if any).
            List<(string DeployName, string SourceName)> deployImages =
                await ResolveDeployImagesAsync(theme.TenantId, componentId, theme.Id, ct);
            List<string> imageNames = deployImages.Select(d => d.DeployName).ToList();

            // extraVolumes: optional ConfigMap source (never blocks startup if the theme hasn't
            // been deployed yet) + emptyDir target the init container populates.
            volumes.Append($"  - name: {srcVolName}\n");
            volumes.Append("    configMap:\n");
            volumes.Append($"      name: {cmName}\n");
            volumes.Append("      optional: true\n");
            volumes.Append("      items:\n");
            volumes.Append("        - key: login-theme.properties\n          path: theme.properties\n");
            volumes.Append("        - key: login.css\n          path: login.css\n");
            foreach (string n in imageNames)
                volumes.Append($"        - key: img-{n}\n          path: imgs/{n}\n");
            volumes.Append($"  - name: {loginVolName}\n    emptyDir: {{}}\n");

            // extraInitContainers: copier that turns the ConfigMap symlinks into real files.
            initContainers.Append($"  - name: {copierName}\n");
            initContainers.Append($"    image: {ThemeCopierImage}\n");
            initContainers.Append($"    command: [\"sh\", \"-c\", \"{copierCmd}\"]\n");
            initContainers.Append("    volumeMounts:\n");
            initContainers.Append($"      - name: {srcVolName}\n        mountPath: /src\n");
            initContainers.Append($"      - name: {loginVolName}\n        mountPath: /dest\n");

            // extraVolumeMounts: mount the populated emptyDir at the theme's login directory.
            volumeMounts.Append($"  - name: {loginVolName}\n    mountPath: /opt/keycloak/themes/{deploySlug}/login\n");
        }

        var sb = new StringBuilder();
        sb.Append("extraVolumes: |\n").Append(volumes);
        sb.Append("extraInitContainers: |\n").Append(initContainers);
        sb.Append("extraVolumeMounts: |\n").Append(volumeMounts);
        return sb.ToString();
    }

    public async Task DeployNamedThemeToClusterAsync(
        Guid tenantId, KeycloakTheme theme, CancellationToken ct = default)
    {
        if (theme.ComponentConfig is null)
        {
            // Reload with navigation properties.
            using ApplicationDbContext db2 = dbFactory.CreateDbContext();
            theme = await db2.KeycloakThemes
                .Include(t => t.ComponentConfig)
                    .ThenInclude(c => c.ClusterComponent!)
                    .ThenInclude(cc => cc.Cluster)
                .FirstOrDefaultAsync(t => t.Id == theme.Id && t.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("Theme not found.");
        }

        if (theme.ComponentConfig.ClusterComponentId is null
            || theme.ComponentConfig.ClusterComponent is null)
            return;

        ClusterComponent comp = theme.ComponentConfig.ClusterComponent;
        string? kubeconfig = comp.Cluster.Kubeconfig;
        if (string.IsNullOrEmpty(kubeconfig) || string.IsNullOrEmpty(comp.Namespace))
            return;

        Guid componentVaultId = comp.Id;
        string cmName = $"entkube-theme-{theme.Id:N}";
        string ns = comp.Namespace;
        string releaseName = comp.ReleaseName ?? comp.Name;

        // Find the Keycloak Deployment by the standard Helm instance label.
        // The keycloakx chart names the Deployment "{release}-keycloakx" (not just "{release}"),
        // so we resolve dynamically rather than hard-coding the naming convention.
        string deployName = await ResolveKeycloakDeploymentNameAsync(releaseName, ns, kubeconfig, ct);

        // Load login CSS from vault.
        string loginCss = await vaultService.GetComponentSecretValueAsync(
            tenantId, componentVaultId, $"named-theme-{theme.Id}-login-css", ct) ?? "";

        // Derive the slug and the deployed directory name.
        // The "theme-" prefix distinguishes our themes from Keycloak built-ins and matches
        // the loginTheme value already stored in Keycloak's realm database from prior deploys.
        string baseSlug   = ToThemeSlug(theme.Name);   // e.g. "salesdata"
        string deploySlug = "theme-" + baseSlug;        // e.g. "theme-salesdata"

        // Load uploaded resource images (logo, backgrounds, etc.) for deployment.
        // Images are stored as raw base64 in the vault. We include them in ConfigMap
        // binaryData so Keycloak can serve them from resources/img/ in the theme directory.
        // Each key uses the "img-" prefix to distinguish from CSS/properties entries.
        List<(string DeployName, string SourceName)> deployImages =
            await ResolveDeployImagesAsync(tenantId, componentVaultId, theme.Id, ct);
        var resources = new Dictionary<string, string>(StringComparer.Ordinal); // imgName → base64
        foreach ((string imgName, string sourceName) in deployImages)
        {
            string? b64 = await vaultService.GetComponentSecretValueAsync(
                tenantId, componentVaultId, $"named-theme-{theme.Id}-resource-{sourceName}", ct);
            if (!string.IsNullOrEmpty(b64))
                resources[imgName] = b64;
        }

        // Build ConfigMap with theme.properties, the custom CSS, and any uploaded images.
        // ConfigMap mounts (directory or subPath) both ultimately place symlinks in the
        // container filesystem. Keycloak's FlatFileThemeProvider uses NOFOLLOW_LINKS, so
        // it cannot read through those symlinks and silently falls back to keycloak.v2.
        // The only reliable approach: an init container copies the ConfigMap files into an
        // emptyDir volume as real files before the Keycloak process starts.
        string loginCssYaml = IndentForYaml(loginCss, 4);

        // binaryData section for image resources (Kubernetes decodes base64 → raw bytes).
        string binaryDataSection = resources.Count > 0
            ? "\nbinaryData:\n" + string.Join("\n", resources.Select(kvp => $"  img-{kvp.Key}: {kvp.Value}"))
            : "";

        string configMap = $"""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {cmName}
              namespace: {ns}
              labels:
                app.kubernetes.io/managed-by: entkube
                entkube.io/theme-id: "{theme.Id:N}"
            data:
              login-theme.properties: |
                parent=keycloak.v2
                styles=css/styles.css css/login.css
              login.css: |
            {loginCssYaml}{binaryDataSection}
            """;

        await k8sFactory.ApplyManifestAsync(configMap, kubeconfig, ct);

        // Read the StatefulSet to discover the actual container name — keycloakx chart uses
        // ".Chart.Name" as the container name, which may be "keycloakx" not "keycloak".
        string containerName = await GetFirstContainerNameAsync(
            "statefulset", deployName, ns, kubeconfig, ct)
            ?? throw new InvalidOperationException(
                $"StatefulSet '{deployName}' has no containers in spec.template.spec.containers.");

        // Volume/container names for this theme.
        // srcVolName:     ConfigMap-backed source volume, read by the init container.
        // loginVolName:   emptyDir populated by the init container; Keycloak mounts this.
        // accountVolName: legacy name kept only for $patch:delete cleanup.
        // copierName:     init container that copies ConfigMap symlinks → real files in emptyDir.
        string srcVolName     = $"entkube-theme-{theme.Id:N}-src";
        string loginVolName   = $"entkube-theme-{theme.Id:N}-login";
        string accountVolName = $"entkube-theme-{theme.Id:N}-account";
        string copierName     = $"entkube-theme-{theme.Id:N}-copier";

        // Build the configMap.items array: fixed theme files + one entry per image resource.
        // Images are mounted into /src/imgs/{filename} inside the init container.
        string imageItems = resources.Count > 0
            ? ",\n" + string.Join(",\n", resources.Keys.Select(n =>
                $"            {{ \"key\": \"img-{n}\", \"path\": \"imgs/{n}\" }}"))
            : "";

        // Init-container shell command: copy theme files and any uploaded images.
        // copierCmd is the value already JSON-string-escaped (without surrounding quotes)
        // so it can be safely spliced into the JSON patch literal. Shared with the Helm-values
        // builder (BuildKeycloakThemeHelmExtrasAsync) so both renderings emit an identical
        // init container and a `helm upgrade` produces no spec churn.
        string copierCmd = JsonSerializer.Serialize(ThemeCopierCommandRaw)[1..^1]; // strip surrounding quotes

        // Strategic merge patch:
        //  • Delete all previous volume/mount variants (subPath, directory, and legacy names).
        //  • Add ConfigMap source volume + emptyDir target volume.
        //  • Add/update the init container that copies files to the emptyDir as real files.
        //  • Mount the emptyDir at the theme login directory in the Keycloak container.
        // Only spec.template.* is touched — StatefulSet immutable fields are not in the patch.
        string patchJson = $$"""
            {
              "spec": {
                "template": {
                  "spec": {
                    "volumes": [
                      { "name": "{{cmName}}",         "$patch": "delete" },
                      { "name": "{{accountVolName}}", "$patch": "delete" },
                      { "name": "{{loginVolName}}",   "$patch": "delete" },
                      { "name": "{{srcVolName}}",     "$patch": "delete" },
                      {
                        "name": "{{srcVolName}}",
                        "configMap": {
                          "name": "{{cmName}}",
                          "optional": true,
                          "items": [
                            { "key": "login-theme.properties", "path": "theme.properties" },
                            { "key": "login.css",              "path": "login.css" }{{imageItems}}
                          ]
                        }
                      },
                      {
                        "name": "{{loginVolName}}",
                        "emptyDir": {}
                      }
                    ],
                    "initContainers": [
                      {
                        "name": "{{copierName}}",
                        "image": "{{ThemeCopierImage}}",
                        "command": ["sh", "-c", "{{copierCmd}}"],
                        "volumeMounts": [
                          { "name": "{{srcVolName}}",   "mountPath": "/src" },
                          { "name": "{{loginVolName}}", "mountPath": "/dest" }
                        ]
                      }
                    ],
                    "containers": [
                      {
                        "name": "{{containerName}}",
                        "volumeMounts": [
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/login/theme.properties",             "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/login/resources/css/login.css",      "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/login/resources/css/custom.css",     "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/account/theme.properties",           "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/account/resources/css/account.css",  "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/account",                            "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}",                                    "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{deploySlug}}/login",                              "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{baseSlug}}/login/theme.properties",               "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{baseSlug}}/login/resources/css/login.css",        "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{baseSlug}}/login/resources/css/custom.css",       "$patch": "delete" },
                          { "mountPath": "/opt/keycloak/themes/{{baseSlug}}/login",                                "$patch": "delete" },
                          {
                            "name": "{{loginVolName}}",
                            "mountPath": "/opt/keycloak/themes/{{deploySlug}}/login"
                          }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """;

        await k8sFactory.PatchStrategicAsync("statefulset", deployName, ns, patchJson, kubeconfig, ct);

        // Rolling restart so Keycloak reloads theme files from the new subPath mounts.
        string restartPatch =
            $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}}}}}}}}}";
        await k8sFactory.PatchJsonAsync("statefulset", deployName, ns, restartPatch, kubeconfig, ct);
    }

    private static string IndentForYaml(string content, int spaces)
    {
        string indent = new(' ', spaces);
        if (string.IsNullOrWhiteSpace(content))
            return indent + "/* no css */";
        // Normalize to LF only — YAML block scalars are sensitive to CR characters.
        string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return string.Join("\n", normalized.Split('\n').Select(l => indent + l));
    }

    /// <summary>
    /// Finds the name of the Keycloak StatefulSet in a namespace (keycloakx uses StatefulSets).
    /// Tries label-based lookup first, then falls back to listing all StatefulSets in
    /// the namespace and matching by name.  Throws InvalidOperationException with a
    /// diagnostic message (including all names found) when nothing matches.
    /// </summary>
    private async Task<string> ResolveKeycloakDeploymentNameAsync(
        string releaseName, string ns, string kubeconfig, CancellationToken ct)
    {
        // Strategy 1: Helm instance label.
        try
        {
            string json = await k8sFactory.GetJsonAsync(
                "statefulset", ns, kubeconfig,
                $"app.kubernetes.io/instance={releaseName}", ct);
            string? name = FirstDeploymentName(json);
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        catch { /* label query failed — fall through */ }

        // Strategy 2 & 3: list ALL deployments in the namespace.
        List<string> allNames = [];
        string? listError = null;
        try
        {
            string json = await k8sFactory.GetJsonAsync("statefulset", ns, kubeconfig, "", ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out JsonElement items))
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("metadata", out JsonElement meta))
                    {
                        string? n = meta.GetStringOrDefault("name");
                        if (!string.IsNullOrEmpty(n)) allNames.Add(n);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            listError = ex.Message;
        }

        // Exact name candidates in preference order.
        foreach (string candidate in new[]
        {
            releaseName,
            $"{releaseName}-keycloakx",
            $"{releaseName}-keycloak"
        })
        {
            if (allNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }

        // Prefix match as last resort.
        string? prefixed = allNames.FirstOrDefault(
            n => n.StartsWith(releaseName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(prefixed))
            return prefixed;

        // Nothing found — produce a diagnostic message.
        string found = allNames.Count > 0
            ? $"Deployments in namespace: {string.Join(", ", allNames)}"
            : listError is not null
                ? $"Could not list deployments: {listError}"
                : "No deployments found in namespace.";

        throw new InvalidOperationException(
            $"Could not find Keycloak Deployment in namespace '{ns}' for release '{releaseName}'. {found}");
    }

    private async Task<string?> GetFirstContainerNameAsync(
        string resource, string name, string ns, string kubeconfig, CancellationToken ct)
    {
        string json = await k8sFactory.GetJsonAsync($"{resource}/{name}", ns, kubeconfig, ct: ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("spec", out JsonElement spec)
            && spec.TryGetProperty("template", out JsonElement tmpl)
            && tmpl.TryGetProperty("spec", out JsonElement podSpec)
            && podSpec.TryGetProperty("containers", out JsonElement containers))
        {
            foreach (JsonElement c in containers.EnumerateArray())
            {
                if (c.TryGetProperty("name", out JsonElement n))
                    return n.GetString();
            }
        }
        return null;
    }

    private static string? FirstDeploymentName(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                string? name = item.TryGetProperty("metadata", out JsonElement meta)
                    ? meta.GetStringOrDefault("name") : null;
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        else if (root.TryGetProperty("metadata", out JsonElement singleMeta))
        {
            return singleMeta.GetStringOrDefault("name");
        }
        return null;
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
            .Include(t => t.ComponentConfig)
            .FirstOrDefaultAsync(t => t.Id == themeId && t.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Theme not found.");

        // For managed Keycloak (ClusterComponentId set), the login theme slug is always
        // "theme-{name-slug}" — that is exactly what DeployNamedThemeToClusterAsync
        // mounts as the theme directory name. The "theme-" prefix avoids collisions with
        // Keycloak built-ins and matches the value stored in Keycloak's realm database.
        // For external Keycloak, use the explicit LoginTheme/AccountTheme fields as before.
        string deployedSlug = "theme-" + ToThemeSlug(theme.Name);
        bool isManaged = theme.ComponentConfig?.ClusterComponentId is not null;
        string? effectiveLogin = (isManaged || string.IsNullOrWhiteSpace(theme.LoginTheme))
            ? deployedSlug
            : theme.LoginTheme;
        string? effectiveAccount = string.IsNullOrWhiteSpace(theme.AccountTheme)
            ? null
            : theme.AccountTheme;

        // Repair stale LoginTheme on the theme entity itself (e.g. old code wrote "theme-salesdata"
        // when the deployed directory is always the name-derived slug).  Writing it back here
        // means the next call is correct even if isManaged somehow evaluates false.
        if (isManaged && theme.LoginTheme != effectiveLogin)
            theme.LoginTheme = effectiveLogin;

        // Apply the theme's native login/account theme to Keycloak.
        string token = await GetAdminTokenAsync(tenantId, realm.ComponentConfig, ct);
        HttpClient http = CreateHttpClient();

        // Null values are serialized as JSON null, which Keycloak ignores (keeps existing value).
        // We never send "" — Keycloak stores it and shows the default theme, which is confusing.
        string patchJson = JsonSerializer.Serialize(new
        {
            loginTheme = effectiveLogin,
            accountTheme = effectiveAccount
        });

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(patchJson, Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();

        realm.KeycloakThemeId = themeId;
        realm.LoginTheme = effectiveLogin;
        realm.AccountTheme = effectiveAccount;
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

    /// <summary>
    /// Copies all named themes (visual editor variables, CSS overrides, and uploaded resources)
    /// from one Keycloak component to another within the same tenant.
    /// Themes are matched by name; new themes are created on the target if needed.
    /// K8s deployment is NOT triggered — the user must open the visual editor and
    /// click "Save &amp; Deploy" to push to the cluster.
    /// Returns the number of named themes copied.
    /// </summary>
    public async Task<int> CopyNamedThemesAsync(
        Guid tenantId, Guid sourceComponentId, Guid targetComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Resolve DB config IDs from vault component IDs
        KeycloakComponentConfig? srcConfig = await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && (
                c.ClusterComponentId == sourceComponentId ||
                (c.ClusterComponentId == null && c.Id == sourceComponentId)), ct);
        KeycloakComponentConfig? tgtConfig = await db.KeycloakComponentConfigs
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && (
                c.ClusterComponentId == targetComponentId ||
                (c.ClusterComponentId == null && c.Id == targetComponentId)), ct);

        if (srcConfig is null || tgtConfig is null) return 0;

        List<KeycloakTheme> srcThemes = await db.KeycloakThemes
            .Where(t => t.TenantId == tenantId && t.KeycloakComponentConfigId == srcConfig.Id)
            .ToListAsync(ct);
        if (srcThemes.Count == 0) return 0;

        List<KeycloakTheme> tgtThemes = await db.KeycloakThemes
            .Where(t => t.TenantId == tenantId && t.KeycloakComponentConfigId == tgtConfig.Id)
            .ToListAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);

        int copied = 0;
        foreach (KeycloakTheme src in srcThemes)
        {
            // Find or create a matching theme on the target (matched by name)
            KeycloakTheme? tgt = tgtThemes.FirstOrDefault(
                t => string.Equals(t.Name, src.Name, StringComparison.OrdinalIgnoreCase));
            if (tgt is null)
            {
                tgt = new KeycloakTheme
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    KeycloakComponentConfigId = tgtConfig.Id,
                    Name = src.Name,
                    LoginTheme = src.LoginTheme,
                    AccountTheme = src.AccountTheme,
                };
                db.KeycloakThemes.Add(tgt);
                await db.SaveChangesAsync(ct);
                tgtThemes.Add(tgt);
            }

            // Copy visual-editor variables JSON
            string? varsJson = await vaultService.GetComponentSecretValueAsync(
                tenantId, sourceComponentId, $"named-theme-{src.Id}-variables", ct);
            if (!string.IsNullOrEmpty(varsJson))
                await vaultService.SetComponentSecretAsync(
                    tenantId, targetComponentId, $"named-theme-{tgt.Id}-variables", varsJson, ct);

            // Copy CSS for all theme types (written directly to vault — no K8s deploy)
            foreach (string themeType in new[] { "login", "account", "admin", "email" })
            {
                string? css = await vaultService.GetComponentSecretValueAsync(
                    tenantId, sourceComponentId, $"named-theme-{src.Id}-{themeType}-css", ct);
                if (!string.IsNullOrEmpty(css))
                    await vaultService.SetComponentSecretAsync(
                        tenantId, targetComponentId, $"named-theme-{tgt.Id}-{themeType}-css", css, ct);
            }

            // Copy uploaded resources
            List<string> resources = await ListThemeResourcesAsync(tenantId, sourceComponentId, src.Id, ct);
            foreach (string filename in resources)
            {
                string? b64 = await vaultService.GetComponentSecretValueAsync(
                    tenantId, sourceComponentId, $"named-theme-{src.Id}-resource-{filename}", ct);
                if (!string.IsNullOrEmpty(b64))
                    await vaultService.SetComponentSecretAsync(
                        tenantId, targetComponentId, $"named-theme-{tgt.Id}-resource-{filename}", b64, ct);
            }

            copied++;
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

    // ── Clients ───────────────────────────────────────────────────────────────

    public async Task<List<KeycloakClientInfo>> GetClientsAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients?max=200")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(c => new KeycloakClientInfo
        {
            Id = c.GetStringOrDefault("id") ?? "",
            ClientId = c.GetStringOrDefault("clientId") ?? "",
            Name = c.GetStringOrDefault("name"),
            Description = c.GetStringOrDefault("description"),
            Enabled = c.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean(),
            PublicClient = c.TryGetProperty("publicClient", out JsonElement pc) && pc.GetBoolean(),
            BearerOnly = c.TryGetProperty("bearerOnly", out JsonElement bo) && bo.GetBoolean(),
            Protocol = c.GetStringOrDefault("protocol")
        }).ToList();
    }

    public async Task<KeycloakClientDetail> GetClientAsync(
        Guid tenantId, Guid realmId, string clientUuid, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        JsonElement c = doc.RootElement;

        return new KeycloakClientDetail
        {
            Id = c.GetStringOrDefault("id") ?? "",
            ClientId = c.GetStringOrDefault("clientId") ?? "",
            Name = c.GetStringOrDefault("name"),
            Description = c.GetStringOrDefault("description"),
            Enabled = c.TryGetProperty("enabled", out JsonElement en) && en.GetBoolean(),
            PublicClient = c.TryGetProperty("publicClient", out JsonElement pc) && pc.GetBoolean(),
            BearerOnly = c.TryGetProperty("bearerOnly", out JsonElement bo) && bo.GetBoolean(),
            Protocol = c.GetStringOrDefault("protocol"),
            BaseUrl = c.GetStringOrDefault("baseUrl"),
            RootUrl = c.GetStringOrDefault("rootUrl"),
            RedirectUris = c.TryGetProperty("redirectUris", out JsonElement ru)
                ? [.. ru.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0)]
                : [],
            WebOrigins = c.TryGetProperty("webOrigins", out JsonElement wo)
                ? [.. wo.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0)]
                : [],
            StandardFlowEnabled = c.TryGetProperty("standardFlowEnabled", out JsonElement sfe) && sfe.GetBoolean(),
            DirectAccessGrantsEnabled = c.TryGetProperty("directAccessGrantsEnabled", out JsonElement dage) && dage.GetBoolean(),
            ServiceAccountsEnabled = c.TryGetProperty("serviceAccountsEnabled", out JsonElement sae) && sae.GetBoolean(),
            ImplicitFlowEnabled = c.TryGetProperty("implicitFlowEnabled", out JsonElement ife) && ife.GetBoolean()
        };
    }

    public async Task<string> CreateClientAsync(
        Guid tenantId, Guid realmId, string clientId, bool publicClient, string? name,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new
        {
            clientId,
            name = name ?? clientId,
            enabled = true,
            publicClient,
            standardFlowEnabled = true,
            directAccessGrantsEnabled = !publicClient
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();

        // Extract the new client UUID from the Location header.
        string? location = resp.Headers.Location?.ToString();
        return location?.Split('/').LastOrDefault() ?? "";
    }

    public async Task UpdateClientAsync(
        Guid tenantId, Guid realmId, KeycloakClientDetail client, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new
        {
            id = client.Id,
            clientId = client.ClientId,
            name = client.Name ?? "",
            description = client.Description ?? "",
            enabled = client.Enabled,
            publicClient = client.PublicClient,
            bearerOnly = client.BearerOnly,
            standardFlowEnabled = client.StandardFlowEnabled,
            directAccessGrantsEnabled = client.DirectAccessGrantsEnabled,
            serviceAccountsEnabled = client.ServiceAccountsEnabled,
            implicitFlowEnabled = client.ImplicitFlowEnabled,
            baseUrl = client.BaseUrl ?? "",
            rootUrl = client.RootUrl ?? "",
            redirectUris = client.RedirectUris,
            webOrigins = client.WebOrigins
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{client.Id}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteClientAsync(
        Guid tenantId, Guid realmId, string clientUuid, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetClientSecretAsync(
        Guid tenantId, Guid realmId, string clientUuid, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}/client-secret")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        if (!resp.IsSuccessStatusCode) return null;

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetStringOrDefault("value");
    }

    public async Task<string?> RegenerateClientSecretAsync(
        Guid tenantId, Guid realmId, string clientUuid, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}/client-secret")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetStringOrDefault("value");
    }

    // ── Protocol Mappers ──────────────────────────────────────────────────────

    public async Task<List<KeycloakProtocolMapper>> GetClientMappersAsync(
        Guid tenantId, Guid realmId, string clientUuid, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}/protocol-mappers/models")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(m =>
        {
            Dictionary<string, string> config = [];
            if (m.TryGetProperty("config", out JsonElement cfgEl) && cfgEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in cfgEl.EnumerateObject())
                {
                    string? v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                    if (v is not null) config[p.Name] = v;
                }
            }

            return new KeycloakProtocolMapper
            {
                Id = m.GetStringOrDefault("id") ?? "",
                Name = m.GetStringOrDefault("name") ?? "",
                Protocol = m.GetStringOrDefault("protocol") ?? "openid-connect",
                ProtocolMapper = m.GetStringOrDefault("protocolMapper") ?? "",
                Config = config
            };
        }).ToList();
    }

    public async Task CreateClientMapperAsync(
        Guid tenantId, Guid realmId, string clientUuid, KeycloakProtocolMapper mapper,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new
        {
            name = mapper.Name,
            protocol = mapper.Protocol,
            protocolMapper = mapper.ProtocolMapper,
            consentRequired = false,
            config = mapper.Config
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}/protocol-mappers/models")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateClientMapperAsync(
        Guid tenantId, Guid realmId, string clientUuid, KeycloakProtocolMapper mapper,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new
        {
            id = mapper.Id,
            name = mapper.Name,
            protocol = mapper.Protocol,
            protocolMapper = mapper.ProtocolMapper,
            consentRequired = false,
            config = mapper.Config
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}/protocol-mappers/models/{mapper.Id}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteClientMapperAsync(
        Guid tenantId, Guid realmId, string clientUuid, string mapperId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/clients/{clientUuid}/protocol-mappers/models/{mapperId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── Realm Roles ───────────────────────────────────────────────────────────

    public async Task<List<KeycloakRole>> GetRealmRolesAsync(
        Guid tenantId, Guid realmId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/roles")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(r => new KeycloakRole
        {
            Id = r.GetStringOrDefault("id") ?? "",
            Name = r.GetStringOrDefault("name") ?? "",
            Description = r.GetStringOrDefault("description"),
            Composite = r.TryGetProperty("composite", out JsonElement comp) && comp.GetBoolean(),
            ClientRole = r.TryGetProperty("clientRole", out JsonElement cl) && cl.GetBoolean()
        }).ToList();
    }

    public async Task CreateRealmRoleAsync(
        Guid tenantId, Guid realmId, string name, string? description, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new { name, description = description ?? "" };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/roles")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteRealmRoleAsync(
        Guid tenantId, Guid realmId, string roleName, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/roles/{Uri.EscapeDataString(roleName)}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── User Updates ──────────────────────────────────────────────────────────

    public async Task UpdateUserAsync(
        Guid tenantId, Guid realmId, string userId,
        string? firstName, string? lastName, string? email,
        bool enabled, bool emailVerified,
        Dictionary<string, List<string>>? attributes,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new
        {
            firstName = firstName ?? "",
            lastName = lastName ?? "",
            email,
            enabled,
            emailVerified,
            attributes
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task ResetUserPasswordAsync(
        Guid tenantId, Guid realmId, string userId, string newPassword, bool temporary,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = new { type = "password", value = newPassword, temporary };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/reset-password")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<KeycloakRole>> GetUserRoleMappingsAsync(
        Guid tenantId, Guid realmId, string userId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/role-mappings/realm")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        return doc.RootElement.EnumerateArray().Select(r => new KeycloakRole
        {
            Id = r.GetStringOrDefault("id") ?? "",
            Name = r.GetStringOrDefault("name") ?? "",
            Description = r.GetStringOrDefault("description"),
            Composite = r.TryGetProperty("composite", out JsonElement comp) && comp.GetBoolean(),
            ClientRole = false
        }).ToList();
    }

    public async Task AddUserRoleMappingsAsync(
        Guid tenantId, Guid realmId, string userId, List<KeycloakRole> rolesToAdd,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = rolesToAdd.Select(r => new { id = r.Id, name = r.Name }).ToArray();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/role-mappings/realm")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteUserRoleMappingsAsync(
        Guid tenantId, Guid realmId, string userId, List<KeycloakRole> rolesToRemove,
        CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        var body = rolesToRemove.Select(r => new { id = r.Id, name = r.Name }).ToArray();

        HttpRequestMessage request = new(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/role-mappings/realm")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp = await http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<KeycloakGroupInfo>> GetUserGroupsAsync(
        Guid tenantId, Guid realmId, string userId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/groups")
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

    public async Task AddUserToGroupAsync(
        Guid tenantId, Guid realmId, string userId, string groupId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/groups/{groupId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    public async Task RemoveUserFromGroupAsync(
        Guid tenantId, Guid realmId, string userId, string groupId, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}/users/{userId}/groups/{groupId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        }, ct);

        resp.EnsureSuccessStatusCode();
    }

    // ── Realm Settings ────────────────────────────────────────────────────────

    public async Task<KeycloakRealmSettings> GetRealmSettingsAsync(
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
        JsonElement r = doc.RootElement;

        string smtpHost = "", smtpPort = "465", smtpFrom = "", smtpUser = "", smtpPassword = "";
        bool smtpSsl = false, smtpStarttls = false, smtpAuth = false;
        if (r.TryGetProperty("smtpServer", out JsonElement smtp) && smtp.ValueKind == JsonValueKind.Object)
        {
            smtpHost = smtp.GetStringOrDefault("host") ?? "";
            smtpPort = smtp.GetStringOrDefault("port") ?? "465";
            smtpFrom = smtp.GetStringOrDefault("from") ?? "";
            smtpUser = smtp.GetStringOrDefault("user") ?? "";
            smtpPassword = smtp.GetStringOrDefault("password") ?? "";
            smtpSsl = smtp.GetStringOrDefault("ssl") == "true";
            smtpStarttls = smtp.GetStringOrDefault("starttls") == "true";
            smtpAuth = smtp.GetStringOrDefault("auth") == "true";
        }

        return new KeycloakRealmSettings
        {
            AccessTokenLifespan = r.TryGetProperty("accessTokenLifespan", out JsonElement atl) ? atl.GetInt32() : 300,
            SsoSessionIdleTimeout = r.TryGetProperty("ssoSessionIdleTimeout", out JsonElement ssit) ? ssit.GetInt32() : 1800,
            SsoSessionMaxLifespan = r.TryGetProperty("ssoSessionMaxLifespan", out JsonElement ssml) ? ssml.GetInt32() : 36000,
            OfflineSessionIdleTimeout = r.TryGetProperty("offlineSessionIdleTimeout", out JsonElement osit) ? osit.GetInt32() : 2592000,
            RegistrationAllowed = r.TryGetProperty("registrationAllowed", out JsonElement ra) && ra.GetBoolean(),
            ResetPasswordAllowed = r.TryGetProperty("resetPasswordAllowed", out JsonElement rpa) && rpa.GetBoolean(),
            RememberMe = r.TryGetProperty("rememberMe", out JsonElement rm) && rm.GetBoolean(),
            LoginWithEmailAllowed = !r.TryGetProperty("loginWithEmailAllowed", out JsonElement lwea) || lwea.GetBoolean(),
            DuplicateEmailsAllowed = r.TryGetProperty("duplicateEmailsAllowed", out JsonElement dea) && dea.GetBoolean(),
            VerifyEmail = r.TryGetProperty("verifyEmail", out JsonElement ve) && ve.GetBoolean(),
            BruteForceProtected = r.TryGetProperty("bruteForceProtected", out JsonElement bfp) && bfp.GetBoolean(),
            FailureFactor = r.TryGetProperty("failureFactor", out JsonElement ff) ? ff.GetInt32() : 30,
            WaitIncrementSeconds = r.TryGetProperty("waitIncrementSeconds", out JsonElement wis) ? wis.GetInt32() : 60,
            MaxFailureWaitSeconds = r.TryGetProperty("maxFailureWaitSeconds", out JsonElement mfws) ? mfws.GetInt32() : 900,
            PasswordPolicy = r.GetStringOrDefault("passwordPolicy") ?? "",
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpFrom = smtpFrom,
            SmtpUser = smtpUser,
            SmtpPassword = smtpPassword,
            SmtpSsl = smtpSsl,
            SmtpStarttls = smtpStarttls,
            SmtpAuth = smtpAuth
        };
    }

    public async Task UpdateRealmSettingsAsync(
        Guid tenantId, Guid realmId, KeycloakRealmSettings settings, CancellationToken ct = default)
    {
        (KeycloakRealm realm, string token) = await LoadRealmAndTokenAsync(tenantId, realmId, ct);
        HttpClient http = CreateHttpClient();

        object smtpServer = string.IsNullOrWhiteSpace(settings.SmtpHost)
            ? new { }
            : settings.SmtpAuth
                ? (object)new
                {
                    host = settings.SmtpHost,
                    port = settings.SmtpPort,
                    from = settings.SmtpFrom,
                    user = settings.SmtpUser,
                    password = settings.SmtpPassword,
                    ssl = settings.SmtpSsl ? "true" : "false",
                    starttls = settings.SmtpStarttls ? "true" : "false",
                    auth = "true"
                }
                : new
                {
                    host = settings.SmtpHost,
                    port = settings.SmtpPort,
                    from = settings.SmtpFrom,
                    user = "",
                    password = "",
                    ssl = settings.SmtpSsl ? "true" : "false",
                    starttls = settings.SmtpStarttls ? "true" : "false",
                    auth = "false"
                };

        var body = new
        {
            accessTokenLifespan = settings.AccessTokenLifespan,
            ssoSessionIdleTimeout = settings.SsoSessionIdleTimeout,
            ssoSessionMaxLifespan = settings.SsoSessionMaxLifespan,
            offlineSessionIdleTimeout = settings.OfflineSessionIdleTimeout,
            registrationAllowed = settings.RegistrationAllowed,
            resetPasswordAllowed = settings.ResetPasswordAllowed,
            rememberMe = settings.RememberMe,
            loginWithEmailAllowed = settings.LoginWithEmailAllowed,
            duplicateEmailsAllowed = settings.DuplicateEmailsAllowed,
            verifyEmail = settings.VerifyEmail,
            bruteForceProtected = settings.BruteForceProtected,
            failureFactor = settings.FailureFactor,
            waitIncrementSeconds = settings.WaitIncrementSeconds,
            maxFailureWaitSeconds = settings.MaxFailureWaitSeconds,
            passwordPolicy = settings.PasswordPolicy,
            smtpServer
        };

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"{realm.ComponentConfig.AdminUrl}/admin/realms/{realm.RealmName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        }, ct);

        resp.EnsureSuccessStatusCode();
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
