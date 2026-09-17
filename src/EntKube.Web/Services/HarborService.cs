using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public class DetectedHarbor
{
    public ClusterComponent Component { get; set; } = null!;
    public HarborComponentConfig? Config { get; set; }

    public bool IsConfigured => Config is not null;
    public Guid ComponentId => Component.Id;
    public string DisplayName => Component.Name;
    public string? ClusterDisplayName => Component.Cluster.Name;
}

/// <summary>Result of checking the stored Harbor admin credentials against the live instance.</summary>
public class HarborCredentialCheck
{
    public bool Authenticated { get; set; }
    public bool IsSysAdmin { get; set; }
    public string? Username { get; set; }
    public string Message { get; set; } = "";
}

public class HarborProjectInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Public { get; set; }
    public int RepoCount { get; set; }
    public long StorageUsedBytes { get; set; }
    public long StorageQuotaBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsProxyCache { get; set; }
    public long? RegistryId { get; set; }
}

public class HarborRepositoryInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int ArtifactCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class HarborArtifactInfo
{
    public string Digest { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }
    public DateTime PushedAt { get; set; }

    /// <summary>Trivy scan summary, when Harbor has scanned this artifact. Null when never scanned.</summary>
    public HarborScanOverview? ScanOverview { get; set; }
}

/// <summary>Trivy's summary for one artifact, as Harbor reports it on the artifact listing.</summary>
public class HarborScanOverview
{
    /// <summary>"Success", "Running", "Error", "Queued" — anything but Success means counts are provisional.</summary>
    public string ScanStatus { get; set; } = "";

    /// <summary>Highest severity found ("Critical", "High", …), or "None".</summary>
    public string Severity { get; set; } = "None";

    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Unknown { get; set; }

    /// <summary>Total findings, as reported by Harbor rather than summed locally.</summary>
    public int Total { get; set; }

    /// <summary>How many findings have a fix available — the actionable subset.</summary>
    public int Fixable { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>True when Harbor has a completed scan for this artifact.</summary>
    public bool IsScanned => string.Equals(ScanStatus, "Success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A single CVE found in an artifact.</summary>
public class HarborVulnerability
{
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string? FixVersion { get; set; }
    public string Severity { get; set; } = "Unknown";
    public string? Description { get; set; }
    public List<string> Links { get; set; } = [];

    /// <summary>True when upstream has published a fixed version — i.e. upgrading resolves it.</summary>
    public bool IsFixable => !string.IsNullOrWhiteSpace(FixVersion);
}

public class HarborRobotInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Disabled { get; set; }
    public long ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HarborSystemInfo
{
    public string? HarborVersion { get; set; }
    public string? RegistryUrl { get; set; }
    public bool WithTrivy { get; set; }
    public string? AuthMode { get; set; }
}

public class HarborRegistryInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Url { get; set; }
    public bool Insecure { get; set; }
    public string Status { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One registry provider Harbor actually has an adapter for, as reported by
/// /replication/adapterinfos. <see cref="Type"/> is the only spelling Harbor accepts in the
/// "type" field of a registry — an adapter name it does not know makes Harbor's own registry
/// controller fail before it reaches any error it has a code for, which reaches the caller as a
/// bare 500 "internal server error".
/// </summary>
public class HarborRegistryAdapter
{
    /// <summary>Harbor's adapter identifier, e.g. "docker-hub", "github-ghcr", "google-gcr".</summary>
    public string Type { get; set; } = "";

    /// <summary>Human-readable name for the picker; falls back to <see cref="Type"/>.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// The one endpoint this provider can ever have (Harbor's "EndpointPatternTypeFix"), e.g.
    /// https://hub.docker.com for Docker Hub. Null when the URL is the operator's to choose.
    /// </summary>
    public string? FixedUrl { get; set; }

    /// <summary>Endpoints Harbor suggests for this provider; empty when it has no opinion.</summary>
    public List<string> SuggestedUrls { get; set; } = [];
}

public class HarborReplicationInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public long? SrcRegistryId { get; set; }
    public string? SrcRegistryName { get; set; }
    public long? DestRegistryId { get; set; }
    public string? DestRegistryName { get; set; }
    public string? DestNamespace { get; set; }
    public string? TriggerType { get; set; }
    public string? NameFilter { get; set; }
    public bool Override { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HarborWebhookInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public List<string> EventTypes { get; set; } = [];
    public string? TargetUrl { get; set; }
    public string NotifyType { get; set; } = "http";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Manages Harbor container registry instances installed via the component catalog.
///
/// Integration points:
/// - CNPG: injects database host/port/name/user into Helm values; stores DB password
///   as a component vault secret so InjectSecretsIntoValuesAsync delivers it at install time.
/// - S3: reads StorageLink credentials from the vault and stores them as component
///   vault secrets (harbor-s3-access-key, harbor-s3-secret-key) for injection at install time.
/// - Redis: points the chart at a Redis already on the cluster instead of the one it would
///   otherwise run for itself; the password is resolved from the vault when that Redis is one
///   EntKube manages, and stored as harbor-redis-password for injection at install time.
/// - Vault: admin password stored as component vault secret HARBOR_ADMIN_PASSWORD.
/// </summary>
public class HarborService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    StorageService storageService,
    RedisService redisService,
    CnpgService cnpgService,
    IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The highest golang-migrate schema version the catalog's pinned Harbor ships — 0180, the last
    /// migration in Harbor 2.15.x (chart 1.19.x).
    ///
    /// <para>Bump this with the chart, never separately. Harbor migrates its schema forward only, so this
    /// number is the whole of what decides which databases this Harbor can be pointed at, and a pin that
    /// moves without it would either refuse databases it can handle or accept ones it cannot.</para>
    /// </summary>
    public const int HarborSchemaVersion = 180;

    /// <summary>The Harbor release behind <see cref="HarborSchemaVersion"/>, for saying so in a message.</summary>
    public const string HarborAppVersion = "2.15.2";
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates the Harbor configuration for a component. Stores the admin
    /// password in vault and injects CNPG, S3 and Redis connection details into Helm values.
    /// Pass null for adminPassword to leave the existing password unchanged.
    ///
    /// <para><paramref name="redisEndpoint"/> empty means Harbor keeps the Redis the chart runs for
    /// itself, and an endpoint that was previously set is taken back off — so clearing the field in the
    /// form is a real instruction, not a no-op.</para>
    /// </summary>
    public async Task<HarborComponentConfig> ConfigureAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Guid? cnpgDatabaseId,
        Guid? storageLinkId,
        string adminUsername,
        string? adminPassword,
        string? registryUrl,
        string? redisEndpoint = null,
        string? redisPassword = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Before anything is written. A Redis Harbor cannot use must not reach the config record, or the
        // refusal would move from "your form was rejected" to "every install of this component now throws".
        await EnsureRedisEndpointUsableAsync(component.ClusterId, redisEndpoint, ct);

        // Upsert the config record.
        HarborComponentConfig? config = await db.HarborComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId, ct);

        if (config is null)
        {
            config = new HarborComponentConfig
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterComponentId = clusterComponentId
            };
            db.HarborComponentConfigs.Add(config);
        }

        config.CnpgDatabaseId = cnpgDatabaseId;
        config.StorageLinkId = storageLinkId;
        config.RedisEndpoint = string.IsNullOrWhiteSpace(redisEndpoint) ? null : redisEndpoint.Trim();
        config.AdminUsername = adminUsername;

        if (!string.IsNullOrWhiteSpace(registryUrl))
        {
            config.RegistryUrl = registryUrl.TrimEnd('/');
        }

        await db.SaveChangesAsync(ct);

        // Store admin password in vault.
        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            string harborNs = component.Namespace ?? "harbor";
            string credSecretName = $"{component.ReleaseName ?? component.Name}-credentials";

            VaultSecret adminPwSecret = await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "HARBOR_ADMIN_PASSWORD", adminPassword, ct);
            await vaultService.ConfigureKubernetesSyncAsync(
                adminPwSecret.Id, true, credSecretName, harborNs, ct);
        }

        // Inject CNPG and S3 connection details into Helm values.
        if (cnpgDatabaseId.HasValue)
        {
            await WriteDatabaseHelmValuesAsync(tenantId, clusterComponentId, cnpgDatabaseId.Value, ct);
        }

        if (storageLinkId.HasValue)
        {
            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, storageLinkId.Value, ct);
        }

        await WriteRedisHelmValuesAsync(tenantId, clusterComponentId, config.RedisEndpoint, redisPassword, ct);

        // Last, and from here rather than from the page that collected the form: every Write* above
        // re-reads the component and saves it, so a caller holding a ClusterComponent it loaded
        // before this call is holding values that are now several writes out of date. Merging into
        // that copy and saving it put the database back the way it was before Harbor was configured
        // — which is how a Harbor with no Redis selected ended up still pointed at an external one.
        await WriteExposeHelmValuesAsync(tenantId, clusterComponentId, config.RegistryUrl, ct);

        return config;
    }

    /// <summary>The IngressClass the chart's leftover Ingress is pinned to. Nothing serves it.</summary>
    /// <remarks>
    /// The chart omits its bundled nginx proxy only when told that something in front is doing the
    /// path split, which it expresses as <c>expose.type</c> being "ingress" or "route" — and both of
    /// those make it write a routing object of its own. EntKube publishes Harbor through the
    /// cluster's gateway with an HTTPRoute it owns, so the chart's object must carry no traffic:
    /// "ingress" leaves an Ingress, and an Ingress with a class no controller watches is inert.
    /// ("route" would leave an HTTPRoute for the same hostname as EntKube's, and two of those is a
    /// fight over one object.)
    /// </remarks>
    public const string UnusedIngressClass = "entkube-unused";

    /// <summary>
    /// Writes the values that follow from Harbor's public hostname: the external URL Harbor hands to
    /// docker clients, and the expose mode that keeps the chart's nginx proxy out of the namespace.
    ///
    /// <para>Re-reads the component itself, deliberately. It is called at the end of a sequence of
    /// writers that each open their own context, so anything merged into a copy loaded earlier would
    /// silently undo them.</para>
    /// </summary>
    public async Task WriteExposeHelmValuesAsync(
        Guid tenantId,
        Guid clusterComponentId,
        string? registryUrl,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // The expose mode is written whether or not a hostname is configured: it is what keeps the
        // chart's nginx proxy uninstalled, and a Harbor nobody has published yet should not be
        // running one either.
        Dictionary<string, string> values = new()
        {
            ["expose.type"] = "ingress",
            ["expose.ingress.className"] = UnusedIngressClass
        };

        // The URL Harbor prints in its docker commands and signs its registry tokens for. Only known
        // once a hostname is, and wrong to guess: a token issued for the wrong audience is refused by
        // the registry with an error about authentication rather than about the URL.
        if (HostnameOf(registryUrl) is { Length: > 0 } hostname)
        {
            values["externalURL"] = $"https://{hostname}";
            values["expose.ingress.hosts.core"] = hostname;
        }

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", values);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The host part of a stored registry URL, with or without its scheme or trailing path.</summary>
    private static string HostnameOf(string? registryUrl)
    {
        string value = (registryUrl ?? "").Trim();

        if (value.Length == 0)
        {
            return "";
        }

        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        int slash = value.IndexOf('/');
        return slash >= 0 ? value[..slash] : value;
    }

    /// <summary>
    /// Injects CNPG database connection details into the component's Helm values.
    /// Non-sensitive parts (host, port, db name, username) are written directly to HelmValues.
    /// The database password is stored as a component vault secret so it is injected at
    /// install time by InjectSecretsIntoValuesAsync without appearing in plain Helm YAML.
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

        string host = $"{cnpgDb.CnpgCluster.Name}-rw.{cnpgDb.CnpgCluster.Namespace}.svc.cluster.local";

        component.HelmValues = YamlFormMerger.MergeFormValues(
            component.HelmValues ?? "",
            new Dictionary<string, string>
            {
                ["database.type"] = "external",
                ["database.external.host"] = host,
                ["database.external.port"] = "5432",
                ["database.external.username"] = cnpgDb.Owner,
                ["database.external.coreDatabase"] = cnpgDb.Name,
                ["database.external.sslmode"] = "disable"
            });

        await db.SaveChangesAsync(ct);

        // Store DB password as a component vault secret; injected at install time via the
        // harbor-db-password hidden catalog field.
        string? dbPassword = await vaultService.GetCnpgDatabasePasswordAsync(tenantId, cnpgDatabaseId, ct);
        if (dbPassword is not null)
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "harbor-db-password", dbPassword, ct);
        }
    }

    /// <summary>
    /// Injects S3-compatible storage configuration into the component's Helm values.
    /// Non-sensitive parts (region, bucket, endpoint) are written directly to HelmValues.
    /// Access key and secret key are stored as component vault secrets so they are injected
    /// at install time by InjectSecretsIntoValuesAsync without appearing in plain Helm YAML.
    /// </summary>
    public async Task WriteStorageHelmValuesAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Guid storageLinkId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        string s3Endpoint = link.Endpoint ?? "";
        string region = link.Region ?? "us-east-1";
        string bucket = link.BucketName ?? "";

        Dictionary<string, string> s3Values = new()
        {
            ["persistence.imageChartStorage.type"] = "s3",
            ["persistence.imageChartStorage.disableredirect"] = "true",
            ["persistence.imageChartStorage.s3.region"] = region,
            ["persistence.imageChartStorage.s3.bucket"] = bucket
        };

        if (!string.IsNullOrWhiteSpace(s3Endpoint))
        {
            s3Values["persistence.imageChartStorage.s3.regionendpoint"] = s3Endpoint;
        }

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", s3Values);
        await db.SaveChangesAsync(ct);

        // Store S3 credentials as component vault secrets; injected at install time via hidden
        // harbor-s3-access-key and harbor-s3-secret-key catalog fields.
        await vaultService.InitializeVaultAsync(tenantId, ct);

        (string accessKey, string secretKey) = await storageService.GetStoredCredentialsInternalAsync(tenantId, storageLinkId, ct);

        if (!string.IsNullOrEmpty(accessKey))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "harbor-s3-access-key", accessKey, ct);
        }

        if (!string.IsNullOrEmpty(secretKey))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "harbor-s3-secret-key", secretKey, ct);
        }
    }

    /// <summary>
    /// Why this Harbor cannot be installed against the database it is pointed at, or null when it can.
    ///
    /// <para>The case this exists for: a database that already holds a <em>newer</em> Harbor's schema.
    /// Harbor's migrator only goes forward, so it connects, reads a version it has no migration for, and
    /// dies with <c>no migration found for version N</c> — several minutes into a Helm wait, in a pod log
    /// nobody is watching, while helm reports only that its deadline lapsed. Every pod downstream then
    /// fails on top of it, so the namespace is full of errors and none of them is the reason.</para>
    ///
    /// <para>Best-effort and silent about everything else. A database it cannot read, a component with no
    /// managed database, an empty schema — all of those are "no objection", because refusing an install on
    /// a question we could not answer is worse than the failure this prevents.</para>
    /// </summary>
    public async Task<string?> DescribeDatabaseSchemaConflictAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        HarborComponentConfig? config = await db.HarborComponentConfigs
            .Include(c => c.CnpgDatabase)
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        if (config?.CnpgDatabase is null) return null;

        (int Version, bool Dirty)? schema = await cnpgService.ReadMigrateSchemaVersionAsync(
            tenantId, config.CnpgDatabase.CnpgClusterId, config.CnpgDatabase.Id, ct);

        return schema is null
            ? null
            : DescribeSchemaConflict(schema.Value.Version, schema.Value.Dirty, config.CnpgDatabase.Name);
    }

    /// <summary>
    /// The message for a schema this Harbor cannot work with, or null when it can. Separated from the
    /// lookup so the wording is testable — it is the only thing an operator will have to go on.
    /// </summary>
    public static string? DescribeSchemaConflict(int schemaVersion, bool dirty, string databaseName)
    {
        if (schemaVersion > HarborSchemaVersion)
        {
            return $"The database '{databaseName}' already holds a Harbor schema at version {schemaVersion}, "
                + $"written by a newer Harbor than this one. EntKube installs Harbor {HarborAppVersion}, "
                + $"whose last migration is {HarborSchemaVersion}, and Harbor migrates a schema forward "
                + "only — it would start, fail with \"no migration found for version "
                + $"{schemaVersion}\", and take the rest of the release down with it.\n\n"
                + "Point this component at an empty database, or install a Harbor at least as new as the "
                + "one that wrote this schema.";
        }

        if (dirty)
        {
            return $"The database '{databaseName}' is at Harbor schema version {schemaVersion} with the "
                + "migration marked dirty — an earlier upgrade stopped half-applied. Harbor refuses to "
                + "migrate from a dirty state, so the install would fail on startup.\n\n"
                + "Restore that database from a backup, or point this component at an empty one.";
        }

        return null;
    }

    /// <summary>
    /// Why this Harbor cannot be installed against the Redis it is pointed at, or null when it can.
    ///
    /// <para>Harbor holds its sessions, its job queue and its scan results in Redis, so a Redis it cannot
    /// reach is not a degraded registry — it is a core that never becomes ready. What an operator sees
    /// instead is the Helm deadline lapsing half an hour later, with the address nowhere in the message,
    /// and a namespace where the obvious missing thing is a Redis pod the chart was told not to run.</para>
    ///
    /// <para>Only ever objects to an in-cluster address with no Service behind it. A Redis outside the
    /// cluster, a cluster that cannot be read, a component with no stored values — all of those are "no
    /// objection", for the same reason the schema check is silent about a database it cannot read.</para>
    /// </summary>
    public async Task<string?> DescribeRedisConflictAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct);

        if (component is null) return null;

        HarborComponentConfig? config = await db.HarborComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        // What the install will actually use. With a config record the refresh that runs just before it
        // rewrites redis.type from the stored endpoint, so the record is the authority and an empty one
        // means the chart's own Redis. Without a record — an adopted release, or values hand-edited in
        // the advanced editor — the values are all there is, and they are taken at their word.
        string endpoint = config is not null
            ? config.RedisEndpoint ?? ""
            : string.Equals(
                YamlFormMerger.ExtractValue(component.HelmValues ?? "", "redis.type"), "external",
                StringComparison.OrdinalIgnoreCase)
                ? YamlFormMerger.ExtractValue(component.HelmValues ?? "", "redis.external.addr") ?? ""
                : "";

        if (endpoint.Trim().Length == 0) return null;

        // A stored address that cannot be one at all — the shape check now refuses these on save, so
        // what reaches here was stored before it existed, or written straight into the values.
        if (RedisService.DescribeInvalidEndpoint(endpoint) is string malformed)
        {
            return RedisEndpointUnusable(malformed, config is null);
        }

        bool? exists = await redisService.InClusterServiceExistsAsync(component.ClusterId, endpoint, ct);

        return exists == false ? RedisEndpointMissing(endpoint.Trim(), config is null) : null;
    }

    /// <summary>
    /// The message for a stored Redis address that is not an address. Kept beside the missing-Service
    /// one because an operator meets them in the same place and has to act on them the same way.
    /// </summary>
    public static string RedisEndpointUnusable(string reason, bool fromValues) =>
        $"This Harbor is configured to use an external Redis, and the address it was given cannot be "
        + $"one: {reason}\n\nHarbor keeps its sessions, its job queue and its scan results in Redis, so "
        + "the core would never become ready.\n\n"
        + (fromValues
            ? "The address is in this component's Helm values (redis.external.addr). Correct it, or set "
              + "redis.type back to \"internal\" to let Harbor run its own Redis."
            : "Clear the Redis field on this component to let Harbor run its own Redis.");

    /// <summary>
    /// The message for an external Redis that is not on the cluster. Separated from the lookup so the
    /// wording is testable — it is the only thing an operator will have to go on.
    /// </summary>
    public static string RedisEndpointMissing(string endpoint, bool fromValues) =>
        $"This Harbor is configured to use a Redis at '{endpoint}', and no Service of that name exists on "
        + "the cluster. Harbor keeps its sessions, its job queue and its scan results there, so the core "
        + "would start, fail to connect, and never become ready — the install would end in a Helm "
        + "deadline half an hour from now.\n\n"
        + (fromValues
            ? "That address comes from this component's Helm values (redis.external.addr). Point it at a "
              + "Redis that exists, or set redis.type back to \"internal\" to let Harbor run its own."
            : "Clear the Redis field on this component to let Harbor run its own Redis, or pick one the "
              + "cluster actually has.");

    /// <summary>
    /// Refuses a Redis endpoint Harbor cannot actually speak to, so the form says no now rather than the
    /// registry misbehaving a week later.
    ///
    /// <para>The one case that matters today is a sharded Redis Cluster — which is every Redis EntKube
    /// manages, because the operator behind the Cache tab only builds clusters. Harbor addresses Redis as
    /// a single server and puts its core, job service, registry and scanner state in databases 0, 1, 2 and
    /// 5; a cluster has only database 0, so <c>SELECT</c> fails outright, and a client that is not
    /// cluster-aware is redirected away from keys another shard owns. The install would still succeed.</para>
    /// </summary>
    private async Task EnsureRedisEndpointUsableAsync(
        Guid kubernetesClusterId, string? redisEndpoint, CancellationToken ct)
    {
        string endpoint = (redisEndpoint ?? "").Trim();
        if (endpoint.Length == 0) return;

        // Before anything asks whether this Redis is reachable or sharded: whether it is an address
        // at all. Nothing downstream questions the string, so a box a password manager filled in
        // becomes redis.external.addr and the registry dials it for ever.
        if (RedisService.DescribeInvalidEndpoint(endpoint) is string malformed)
        {
            throw new InvalidOperationException(
                $"That is not a Redis address: {malformed} Leave the field empty to let Harbor run its own.");
        }

        RedisEndpointOption? managed = await redisService.ResolveManagedEndpointAsync(
            kubernetesClusterId, endpoint, ct);

        if (managed?.ClusterMode == true)
        {
            throw ShardedRedisRefused(endpoint);
        }
    }

    private static InvalidOperationException ShardedRedisRefused(string endpoint) =>
        new($"'{endpoint}' is a sharded Redis Cluster, which Harbor cannot use: it addresses Redis as a "
            + "single server and needs databases 0, 1, 2 and 5, while a cluster has only database 0. Point "
            + "Harbor at a standalone Redis or a sentinel set, or leave the field empty to let it run its own.");

    /// <summary>
    /// Points Harbor at a Redis already running on the cluster, instead of the one the chart would start
    /// for itself. An empty endpoint puts it back on its own Redis, so clearing the field is an
    /// instruction rather than a no-op.
    ///
    /// <para>The password is resolved here rather than taken on trust from the form. When the address is a
    /// Redis EntKube manages, its vaulted password is authoritative — which also means a rotation is
    /// picked up on the next apply, because this runs before every install. For anything else the
    /// operator's typed password is kept, and a blank one is a Redis with no authentication rather than an
    /// error.</para>
    ///
    /// <para>A sharded Redis Cluster is refused. Harbor addresses Redis as a single server or a sentinel
    /// set and uses four separate databases (0, 1, 2 and 5) — a cluster has only database 0, so
    /// <c>SELECT</c> fails outright, and a non-cluster client is redirected away from keys another shard
    /// owns. Installing anyway produces a Harbor that comes up and then misbehaves in the job service and
    /// the scanner, which is a far worse outcome than being told now.</para>
    /// </summary>
    public async Task WriteRedisHelmValuesAsync(
        Guid tenantId,
        Guid clusterComponentId,
        string? redisEndpoint,
        string? redisPassword,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        string endpoint = (redisEndpoint ?? "").Trim();

        if (endpoint.Length == 0)
        {
            // The address is cleared with the type, not merely ignored. The chart reads neither once
            // the type is internal, but `helm get values` and the advanced editor both still show it,
            // and an address nothing uses is worse than no address when the next person is working out
            // which Redis this registry talks to — especially when it got there by autofill.
            component.HelmValues = YamlFormMerger.MergeFormValues(
                component.HelmValues ?? "",
                new Dictionary<string, string>
                {
                    ["redis.type"] = "internal",
                    ["redis.external.addr"] = ""
                });

            await db.SaveChangesAsync(ct);
            return;
        }

        RedisEndpointOption? managed = await redisService.ResolveManagedEndpointAsync(
            component.ClusterId, endpoint, ct);

        if (managed?.ClusterMode == true)
        {
            throw ShardedRedisRefused(endpoint);
        }

        component.HelmValues = YamlFormMerger.MergeFormValues(
            component.HelmValues ?? "",
            new Dictionary<string, string>
            {
                ["redis.type"] = "external",
                ["redis.external.addr"] = endpoint
            });

        await db.SaveChangesAsync(ct);

        // The credential follows the same route as the database and S3 ones: the component vault secret
        // behind redis.external.password, which InjectSecretsIntoValuesAsync merges in at install time so
        // it is never written into the stored Helm YAML in the clear.
        //
        // Only written when there is something to write. For a Redis EntKube manages the vault is
        // authoritative and overwrites whatever the form held — that is what makes a rotated password
        // arrive on the next apply. For any other Redis, an empty box means "leave the stored password
        // alone", because the form never echoes a password back and blanking on every save would
        // otherwise erase it the first time the operator changed anything else.
        string? password = managed is { RedisClusterId: Guid managedId }
            ? (await redisService.GetCredentialsAsync(tenantId, managedId, ct)).password
            : redisPassword;

        if (!string.IsNullOrEmpty(password))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "harbor-redis-password", password, ct);
        }
    }

    /// <summary>
    /// Refreshes database, S3 and Redis credentials in Helm values from the stored config.
    /// Call this before every install/upgrade to ensure the latest credentials are used.
    /// </summary>
    public async Task RefreshHelmValuesIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        HarborComponentConfig? config = await GetConfigForComponentAsync(tenantId, clusterComponentId, ct);

        if (config is null)
        {
            return;
        }

        if (config.CnpgDatabaseId.HasValue)
        {
            await WriteDatabaseHelmValuesAsync(tenantId, clusterComponentId, config.CnpgDatabaseId.Value, ct);
        }

        if (config.StorageLinkId.HasValue)
        {
            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, config.StorageLinkId.Value, ct);
        }

        // Re-resolved rather than left as stored: for a Redis EntKube manages this is what picks up a
        // rotated password. No password is passed because there is none to pass here — the operator's
        // own is already in the vault, and an unmanaged endpoint leaves it exactly where it is.
        await WriteRedisHelmValuesAsync(tenantId, clusterComponentId, config.RedisEndpoint, null, ct);

        // Also on every install, not just when the hostname is first set: this is what moves a Harbor
        // installed before EntKube took over the path split off the chart's bundled nginx proxy.
        await WriteExposeHelmValuesAsync(tenantId, clusterComponentId, config.RegistryUrl, ct);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<HarborComponentConfig?> GetConfigForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.HarborComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId
                && c.TenantId == tenantId, ct);
    }

    public async Task<List<HarborComponentConfig>> GetConfigsForClusterAsync(
        Guid tenantId, Guid kubernetesClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.HarborComponentConfigs
            .Where(c => c.TenantId == tenantId
                && db.ClusterComponents
                    .Any(comp => comp.Id == c.ClusterComponentId && comp.ClusterId == kubernetesClusterId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all CNPG databases available on the same cluster as the given component.
    /// Used to populate the database selector in the Harbor config form.
    /// </summary>
    public async Task<List<CnpgDatabase>> GetDatabasesForClusterAsync(
        Guid tenantId, Guid kubernetesClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.CnpgDatabases
            .Include(d => d.CnpgCluster)
            .Where(d => d.CnpgCluster.TenantId == tenantId
                && d.CnpgCluster.KubernetesClusterId == kubernetesClusterId)
            .OrderBy(d => d.CnpgCluster.Name)
            .ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all installed Harbor ClusterComponents across the tenant's clusters,
    /// together with their configuration state.
    /// </summary>
    public async Task<List<DetectedHarbor>> GetDetectedHarborInstancesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<ClusterComponent> components = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Include(c => c.ExternalRoutes)
            .Where(c => c.Cluster.TenantId == tenantId
                && (c.Status == ComponentStatus.Installed || c.Status == ComponentStatus.Failed)
                && (c.Name == "harbor" || c.HelmChartName == "harbor"))
            .OrderBy(c => c.Cluster.Name).ThenBy(c => c.Name)
            .ToListAsync(ct);

        List<HarborComponentConfig> configs = await db.HarborComponentConfigs
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct);

        return components.Select(comp => new DetectedHarbor
        {
            Component = comp,
            Config = configs.FirstOrDefault(c => c.ClusterComponentId == comp.Id)
        }).ToList();
    }

    // ── Harbor API helpers ────────────────────────────────────────────────────

    private async Task<string?> GetAdminPasswordAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct)
    {
        return await vaultService.GetComponentSecretValueAsync(
            tenantId, clusterComponentId, "HARBOR_ADMIN_PASSWORD", ct);
    }

    // Uses the "HarborApi" named client which has UseCookies=false. Without a session
    // cookie Harbor does not enforce CSRF for Basic-Auth requests.
    private HttpClient BuildHarborClient(string registryUrl, string username, string password)
    {
        HttpClient http = httpClientFactory.CreateClient("HarborApi");
        http.BaseAddress = new Uri(registryUrl.TrimEnd('/'));
        http.Timeout = TimeSpan.FromSeconds(30);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private async Task<HttpClient> GetHarborClientAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.RegistryUrl))
            throw new InvalidOperationException("Harbor registry URL is not configured.");

        string? password = await GetAdminPasswordAsync(tenantId, config.ClusterComponentId, ct);
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Harbor admin password is not stored in vault.");

        return BuildHarborClient(config.RegistryUrl, config.AdminUsername, password);
    }

    /// <summary>
    /// Turns a failed Harbor response into an actionable exception.
    ///
    /// Harbor serves its public surface (system info, public projects) to anonymous callers and
    /// answers 401 — never 403 — for everything that needs an account. So when the stored password
    /// is wrong, the registry looks half-alive: the overview and project list render fine while
    /// registries, replication, robot accounts and webhooks all fail. The 401 branch below names
    /// that cause rather than leaving the caller with a bare status code.
    /// </summary>
    private static async Task ThrowIfErrorAsync(
        HttpResponseMessage response, CancellationToken ct, HarborComponentConfig? config = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(ct);
        string user = string.IsNullOrWhiteSpace(config?.AdminUsername) ? "admin" : config!.AdminUsername;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                $"Harbor rejected the stored credentials for \"{user}\" (401 Unauthorized). The admin "
                + "password held in the vault no longer matches Harbor's own admin password — the Helm "
                + "chart only seeds harborAdminPassword when Harbor's database is first bootstrapped, so "
                + "a password changed inside Harbor, or rotated here after the install, never reaches it. "
                + "Re-enter Harbor's current admin password on the component's configuration form and use "
                + "\"Test credentials\" to confirm.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"Harbor authenticated \"{user}\" but refused this operation (403 Forbidden) — the account "
                + $"is not a Harbor system administrator. {detail}");
        }

        throw new InvalidOperationException(
            $"Harbor API error ({(int)response.StatusCode} {response.StatusCode}): {detail}");
    }

    /// <summary>
    /// Checks the stored admin credentials against Harbor's /users/current endpoint, which requires a
    /// real session. Reports both whether the password works and whether the account carries the
    /// system-administrator flag that registries, replication and robot accounts require.
    /// </summary>
    public async Task<HarborCredentialCheck> VerifyCredentialsAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/users/current", ct);

        string user = string.IsNullOrWhiteSpace(config.AdminUsername) ? "admin" : config.AdminUsername;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new HarborCredentialCheck
            {
                Authenticated = false,
                Message = $"Harbor rejected the stored password for \"{user}\". Enter Harbor's current "
                    + "admin password on the component's configuration form — the vault copy has drifted "
                    + "from what Harbor actually holds."
            };
        }

        await ThrowIfErrorAsync(response, ct, config);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        string? username = json?["username"]?.GetValue<string>();
        bool sysAdmin = json?["sysadmin_flag"]?.GetValue<bool>() ?? false;

        return new HarborCredentialCheck
        {
            Authenticated = true,
            IsSysAdmin = sysAdmin,
            Username = username,
            Message = sysAdmin
                ? $"Authenticated as \"{username}\" (system administrator)."
                : $"Authenticated as \"{username}\", but the account is not a Harbor system "
                    + "administrator, so registries, replication and robot accounts stay unavailable."
        };
    }

    // ── System Info ───────────────────────────────────────────────────────────

    public async Task<HarborSystemInfo> GetSystemInfoAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/systeminfo", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return new HarborSystemInfo
        {
            HarborVersion = json?["harbor_version"]?.GetValue<string>(),
            RegistryUrl = json?["registry_url"]?.GetValue<string>(),
            WithTrivy = json?["with_trivy"]?.GetValue<bool>() ?? false,
            AuthMode = json?["auth_mode"]?.GetValue<string>()
        };
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    public async Task<List<HarborProjectInfo>> GetProjectsAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/projects?page_size=100", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? projects = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (projects is null) return [];

        return projects.Select(p => new HarborProjectInfo
        {
            Id = p?["id"]?.GetValue<int>() ?? 0,
            Name = p?["name"]?.GetValue<string>() ?? "",
            Public = p?["metadata"]?["public"]?.GetValue<string>() == "true",
            RepoCount = p?["repo_count"]?.GetValue<int>() ?? 0,
            StorageUsedBytes = p?["quota"]?["used"]?["storage"]?.GetValue<long>() ?? 0,
            StorageQuotaBytes = p?["quota"]?["hard"]?["storage"]?.GetValue<long>() ?? -1,
            CreatedAt = p?["creation_time"]?.GetValue<DateTime>() ?? DateTime.MinValue,
            IsProxyCache = p?["registry_id"] is not null,
            RegistryId = p?["registry_id"]?.GetValue<long>()
        }).OrderBy(p => p.Name).ToList();
    }

    public async Task CreateProjectAsync(
        Guid tenantId, HarborComponentConfig config,
        string name, bool isPublic, string? description = null,
        long? proxyRegistryId = null,
        CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);

        JsonObject body = new()
        {
            ["project_name"] = name,
            ["metadata"] = new JsonObject { ["public"] = isPublic ? "true" : "false" }
        };
        if (!string.IsNullOrWhiteSpace(description))
            body["description"] = description;
        if (proxyRegistryId.HasValue)
            body["registry_id"] = proxyRegistryId.Value;

        HttpResponseMessage response = await http.PostAsync(
            "/api/v2.0/projects",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);

        await ThrowIfErrorAsync(response, ct);
    }

    public async Task DeleteProjectAsync(
        Guid tenantId, HarborComponentConfig config, string projectName, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.DeleteAsync($"/api/v2.0/projects/{projectName}", ct);
        await ThrowIfErrorAsync(response, ct);
    }

    // ── Repositories & Artifacts ─────────────────────────────────────────────

    public async Task<List<HarborRepositoryInfo>> GetRepositoriesAsync(
        Guid tenantId, HarborComponentConfig config, string projectName, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync(
            $"/api/v2.0/projects/{projectName}/repositories?page_size=100", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? repos = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (repos is null) return [];

        return repos.Select(r => new HarborRepositoryInfo
        {
            Id = r?["id"]?.GetValue<long>() ?? 0,
            Name = r?["name"]?.GetValue<string>() ?? "",
            ArtifactCount = r?["artifact_count"]?.GetValue<int>() ?? 0,
            SizeBytes = r?["size"]?.GetValue<long>() ?? 0,
            UpdatedAt = r?["update_time"]?.GetValue<DateTime>()
        }).OrderBy(r => r.Name).ToList();
    }

    public async Task<List<HarborArtifactInfo>> GetArtifactsAsync(
        Guid tenantId, HarborComponentConfig config,
        string projectName, string repositoryName, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        string repoEncoded = Uri.EscapeDataString(repositoryName);
        HttpResponseMessage response = await http.GetAsync(
            $"/api/v2.0/projects/{projectName}/repositories/{repoEncoded}/artifacts"
            + "?with_tag=true&with_scan_overview=true&page_size=50", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? artifacts = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (artifacts is null) return [];

        return artifacts.Select(a =>
        {
            List<string> tags = a?["tags"]?.AsArray()
                .Select(t => t?["name"]?.GetValue<string>() ?? "")
                .Where(t => t != "")
                .ToList() ?? [];

            return new HarborArtifactInfo
            {
                Digest = a?["digest"]?.GetValue<string>() ?? "",
                Tags = tags,
                SizeBytes = a?["size"]?.GetValue<long>() ?? 0,
                MediaType = a?["media_type"]?.GetValue<string>(),
                PushedAt = a?["push_time"]?.GetValue<DateTime>() ?? DateTime.MinValue,
                ScanOverview = ParseScanOverview(a?["scan_overview"])
            };
        }).OrderByDescending(a => a.PushedAt).ToList();
    }

    /// <summary>
    /// Reads Harbor's scan_overview. The object is keyed by the scanner's report media
    /// type (e.g. "application/vnd.security.vulnerability.report; v=1.1"), which changes
    /// between Harbor and Trivy versions — so take whichever single entry is present
    /// rather than hard-coding a key that will silently stop matching after an upgrade.
    /// </summary>
    private static HarborScanOverview? ParseScanOverview(JsonNode? scanOverview)
    {
        JsonObject? root = scanOverview?.AsObject();
        if (root is null || root.Count == 0) return null;

        JsonNode? report = root.First().Value;
        if (report is null) return null;

        JsonNode? summary = report["summary"];
        JsonNode? bySeverity = summary?["summary"];

        return new HarborScanOverview
        {
            ScanStatus = report["scan_status"]?.GetValue<string>() ?? "",
            Severity = report["severity"]?.GetValue<string>() ?? "None",
            Total = summary?["total"]?.GetValue<int>() ?? 0,
            Fixable = summary?["fixable"]?.GetValue<int>() ?? 0,
            Critical = bySeverity?["Critical"]?.GetValue<int>() ?? 0,
            High = bySeverity?["High"]?.GetValue<int>() ?? 0,
            Medium = bySeverity?["Medium"]?.GetValue<int>() ?? 0,
            Low = bySeverity?["Low"]?.GetValue<int>() ?? 0,
            Unknown = bySeverity?["Unknown"]?.GetValue<int>() ?? 0,
            CompletedAt = report["end_time"]?.GetValue<DateTime?>(),
        };
    }

    /// <summary>
    /// Fetches the full CVE list for one artifact. <paramref name="reference"/> is a digest
    /// or a tag. Returns an empty list when the artifact has never been scanned — an
    /// unscanned image is not a clean one, so callers must check
    /// <see cref="HarborScanOverview.IsScanned"/> rather than reading empty as safe.
    /// </summary>
    public async Task<List<HarborVulnerability>> GetVulnerabilitiesAsync(
        Guid tenantId, HarborComponentConfig config,
        string projectName, string repositoryName, string reference, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        string repoEncoded = Uri.EscapeDataString(repositoryName);

        HttpResponseMessage response = await http.GetAsync(
            $"/api/v2.0/projects/{projectName}/repositories/{repoEncoded}"
            + $"/artifacts/{reference}/additions/vulnerabilities", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        await ThrowIfErrorAsync(response, ct, config);

        JsonObject? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
        if (root is null || root.Count == 0) return [];

        JsonArray? vulnerabilities = root.First().Value?["vulnerabilities"]?.AsArray();
        if (vulnerabilities is null) return [];

        return [.. vulnerabilities.Select(v => new HarborVulnerability
        {
            Id = v?["id"]?.GetValue<string>() ?? "",
            Package = v?["package"]?.GetValue<string>() ?? "",
            Version = v?["version"]?.GetValue<string>() ?? "",
            FixVersion = v?["fix_version"]?.GetValue<string>(),
            Severity = v?["severity"]?.GetValue<string>() ?? "Unknown",
            Description = v?["description"]?.GetValue<string>(),
            Links = v?["links"]?.AsArray().Select(l => l?.GetValue<string>() ?? "").Where(l => l != "").ToList() ?? [],
        })];
    }

    // ── Robot Accounts ────────────────────────────────────────────────────────

    public async Task<List<HarborRobotInfo>> GetRobotsAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/robots?page_size=100", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? robots = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (robots is null) return [];

        return robots.Select(r => new HarborRobotInfo
        {
            Id = r?["id"]?.GetValue<long>() ?? 0,
            Name = r?["name"]?.GetValue<string>() ?? "",
            Description = r?["description"]?.GetValue<string>(),
            Disabled = r?["disable"]?.GetValue<bool>() ?? false,
            ExpiresAt = r?["expires_at"]?.GetValue<long>() ?? -1,
            CreatedAt = r?["creation_time"]?.GetValue<DateTime>() ?? DateTime.MinValue
        }).OrderBy(r => r.Name).ToList();
    }

    /// <summary>
    /// Creates a robot account scoped to one or more projects (or system-wide).
    /// Pass null for projectNames to create a system-level robot with push/pull on all projects.
    /// Returns the generated secret (only available at creation time).
    /// The secret is also stored in vault as "harbor-robot-{name}" for reference.
    /// </summary>
    public async Task<string> CreateRobotAsync(
        Guid tenantId, HarborComponentConfig config,
        string name, string? description,
        IEnumerable<string>? projectNames,
        bool canPush, bool canPull,
        long durationDays = -1,
        CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);

        JsonArray permissions;
        if (projectNames is not null)
        {
            List<string> projects = projectNames.ToList();
            permissions = new JsonArray(projects.Select(proj =>
            {
                JsonArray access = [];
                if (canPull)
                {
                    access.Add(new JsonObject { ["resource"] = "repository", ["action"] = "pull" });
                    access.Add(new JsonObject { ["resource"] = "artifact", ["action"] = "read" });
                }
                if (canPush)
                {
                    access.Add(new JsonObject { ["resource"] = "repository", ["action"] = "push" });
                    access.Add(new JsonObject { ["resource"] = "artifact", ["action"] = "delete" });
                    access.Add(new JsonObject { ["resource"] = "tag", ["action"] = "create" });
                    access.Add(new JsonObject { ["resource"] = "tag", ["action"] = "delete" });
                }
                return (JsonNode)new JsonObject
                {
                    ["kind"] = "project",
                    ["namespace"] = proj,
                    ["access"] = access
                };
            }).ToArray());
        }
        else
        {
            JsonArray sysAccess = [];
            if (canPull)
            {
                sysAccess.Add(new JsonObject { ["resource"] = "repository", ["action"] = "pull" });
                sysAccess.Add(new JsonObject { ["resource"] = "artifact", ["action"] = "read" });
            }
            if (canPush)
            {
                sysAccess.Add(new JsonObject { ["resource"] = "repository", ["action"] = "push" });
                sysAccess.Add(new JsonObject { ["resource"] = "artifact", ["action"] = "delete" });
                sysAccess.Add(new JsonObject { ["resource"] = "tag", ["action"] = "create" });
                sysAccess.Add(new JsonObject { ["resource"] = "tag", ["action"] = "delete" });
            }
            permissions = [new JsonObject
            {
                ["kind"] = "system",
                ["namespace"] = "/",
                ["access"] = sysAccess
            }];
        }

        JsonObject body = new()
        {
            ["name"] = name,
            ["level"] = projectNames is not null ? "project" : "system",
            ["duration"] = durationDays,
            ["permissions"] = permissions
        };
        if (!string.IsNullOrWhiteSpace(description))
            body["description"] = description;

        HttpResponseMessage response = await http.PostAsync(
            "/api/v2.0/robots",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);

        await ThrowIfErrorAsync(response, ct);

        JsonNode? result = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        string secret = result?["secret"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Harbor did not return a robot secret.");

        // Store the secret in vault so it can be retrieved later if needed.
        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(
            tenantId, config.ClusterComponentId, $"harbor-robot-{name}", secret, ct);

        return secret;
    }

    public async Task DeleteRobotAsync(
        Guid tenantId, HarborComponentConfig config, long robotId, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.DeleteAsync($"/api/v2.0/robots/{robotId}", ct);
        await ThrowIfErrorAsync(response, ct);
    }

    // ── Remote Registries (proxy cache endpoints) ─────────────────────────────

    /// <summary>
    /// The providers offered when Harbor cannot be asked which adapters it has. Every identifier
    /// here must be one of Harbor's own adapter names: Harbor resolves the "type" field against an
    /// adapter factory, and the "not found" raised there carries no error code, so a misspelled
    /// provider comes back as a bare 500 "internal server error" naming nothing.
    /// </summary>
    public static readonly IReadOnlyList<HarborRegistryAdapter> FallbackRegistryAdapters =
    [
        new() { Type = "docker-hub",        Label = "Docker Hub", FixedUrl = "https://hub.docker.com" },
        new() { Type = "quay",              Label = "Quay.io" },
        new() { Type = "github-ghcr",       Label = "GitHub Container Registry" },
        new() { Type = "google-gcr",        Label = "Google Container Registry" },
        new() { Type = "docker-registry",   Label = "Docker Registry V2" },
        new() { Type = "harbor",            Label = "Another Harbor" },
        new() { Type = "aws-ecr",           Label = "Amazon ECR" },
        new() { Type = "azure-acr",         Label = "Azure Container Registry" },
        new() { Type = "jfrog-artifactory", Label = "JFrog Artifactory" },
        new() { Type = "gitlab",            Label = "GitLab Registry" }
    ];

    /// <summary>
    /// Friendly names for the adapters Harbor ships with. Only cosmetic — the identifiers come
    /// from Harbor itself, and an adapter missing from this table is shown under its own name.
    /// </summary>
    private static readonly Dictionary<string, string> AdapterLabels = new()
    {
        ["harbor"] = "Another Harbor",
        ["docker-hub"] = "Docker Hub",
        ["docker-registry"] = "Docker Registry V2",
        ["github-ghcr"] = "GitHub Container Registry",
        ["google-gcr"] = "Google Container Registry",
        ["aws-ecr"] = "Amazon ECR",
        ["azure-acr"] = "Azure Container Registry",
        ["ali-acr"] = "Alibaba Cloud Container Registry",
        ["huawei-SWR"] = "Huawei SWR",
        ["tencent-tcr"] = "Tencent Container Registry",
        ["volcengine-cr"] = "Volcengine Container Registry",
        ["jfrog-artifactory"] = "JFrog Artifactory",
        ["quay"] = "Quay.io",
        ["gitlab"] = "GitLab Registry",
        ["dtr"] = "Docker Trusted Registry",
        ["helm-hub"] = "Helm Hub",
        ["artifact-hub"] = "Artifact Hub"
    };

    /// <summary>
    /// Asks Harbor which registry providers it has adapters for, rather than guessing. Harbor
    /// rejects a "type" it does not recognise deep inside its registry controller, where the error
    /// carries no code, so the caller gets an unexplained 500 instead of "no such provider" — the
    /// only safe source for this list is the running Harbor.
    ///
    /// /replication/adapterinfos also carries each provider's endpoint pattern, which is what
    /// decides whether the URL is the operator's to type (Docker Registry V2) or fixed by the
    /// provider (Docker Hub is always https://hub.docker.com).
    /// </summary>
    public async Task<List<HarborRegistryAdapter>> GetRegistryAdaptersAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);

        HttpResponseMessage response = await http.GetAsync("/api/v2.0/replication/adapterinfos", ct);
        if (response.IsSuccessStatusCode)
        {
            JsonObject? infos = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
            if (infos is not null)
            {
                List<HarborRegistryAdapter> adapters = [];
                foreach ((string type, JsonNode? info) in infos)
                {
                    JsonNode? pattern = info?["endpoint_pattern"];
                    List<string> endpoints = pattern?["endpoints"]?.AsArray()
                        .Select(e => e?["value"]?.GetValue<string>())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!)
                        .ToList() ?? [];

                    // "EndpointPatternTypeFix" means the provider only ever lives at one address,
                    // so the URL box is filled in and locked rather than left to be mistyped.
                    bool fixedEndpoint =
                        pattern?["endpoint_type"]?.GetValue<string>() == "EndpointPatternTypeFix";

                    adapters.Add(new HarborRegistryAdapter
                    {
                        Type = type,
                        Label = AdapterLabels.TryGetValue(type, out string? label) ? label : type,
                        FixedUrl = fixedEndpoint && endpoints.Count == 1 ? endpoints[0] : null,
                        SuggestedUrls = endpoints
                    });
                }
                return adapters.OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // Older Harbors, and any Harbor that answers adapterinfos oddly, still list the bare names.
        response = await http.GetAsync("/api/v2.0/replication/adapters", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? names = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        return (names ?? [])
            .Select(n => n?.GetValue<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => new HarborRegistryAdapter
            {
                Type = n!,
                Label = AdapterLabels.TryGetValue(n!, out string? label) ? label : n!
            })
            .OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<HarborRegistryInfo>> GetRegistriesAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/registries?page_size=100", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? arr = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (arr is null) return [];

        return arr.Select(r => new HarborRegistryInfo
        {
            Id = r?["id"]?.GetValue<long>() ?? 0,
            Name = r?["name"]?.GetValue<string>() ?? "",
            Type = r?["type"]?.GetValue<string>() ?? "",
            Url = r?["url"]?.GetValue<string>(),
            Insecure = r?["insecure"]?.GetValue<bool>() ?? false,
            Status = r?["status"]?.GetValue<string>() ?? "",
            Description = r?["description"]?.GetValue<string>(),
            CreatedAt = r?["creation_time"]?.GetValue<DateTime>() ?? DateTime.MinValue
        }).OrderBy(r => r.Name).ToList();
    }

    public async Task<HarborRegistryInfo> CreateRegistryAsync(
        Guid tenantId, HarborComponentConfig config,
        string name, string type, string? url,
        string? accessKey, string? accessSecret,
        bool insecure, string? description = null,
        CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);

        JsonObject body = new()
        {
            ["name"] = name,
            ["type"] = type,
            ["insecure"] = insecure
        };
        if (!string.IsNullOrWhiteSpace(url)) body["url"] = url;
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        if (!string.IsNullOrWhiteSpace(accessKey) || !string.IsNullOrWhiteSpace(accessSecret))
        {
            body["credential"] = new JsonObject
            {
                ["type"] = "basic",
                ["access_key"] = accessKey ?? "",
                ["access_secret"] = accessSecret ?? ""
            };
        }

        HttpResponseMessage response = await http.PostAsync(
            "/api/v2.0/registries",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);

        // Harbor validates the provider by looking up an adapter factory, and the "not found" it
        // raises there carries no error code — Harbor turns that into a bare 500 "internal server
        // error" with nothing to act on. Name the real cause before handing the 500 on.
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            await ThrowIfUnknownProviderAsync(tenantId, config, type, ct);
        }

        await ThrowIfErrorAsync(response, ct, config);

        JsonNode? result = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return new HarborRegistryInfo
        {
            Id = result?["id"]?.GetValue<long>() ?? 0,
            Name = result?["name"]?.GetValue<string>() ?? name,
            Type = type,
            Url = url,
            Status = result?["status"]?.GetValue<string>() ?? ""
        };
    }

    /// <summary>
    /// Turns Harbor's unexplained 500 into the provider mismatch it almost always is. Best effort:
    /// when the adapter list cannot be read, the caller falls through to the original error.
    /// </summary>
    private async Task ThrowIfUnknownProviderAsync(
        Guid tenantId, HarborComponentConfig config, string type, CancellationToken ct)
    {
        List<HarborRegistryAdapter> adapters;
        try { adapters = await GetRegistryAdaptersAsync(tenantId, config, ct); }
        catch { return; }

        if (adapters.Count == 0 || adapters.Any(a => a.Type == type)) return;

        throw new InvalidOperationException(
            $"Harbor has no registry adapter called \"{type}\", and reports that as an unexplained "
            + "500 rather than a validation error. This Harbor accepts: "
            + string.Join(", ", adapters.Select(a => a.Type)) + ".");
    }

    public async Task DeleteRegistryAsync(
        Guid tenantId, HarborComponentConfig config, long registryId, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.DeleteAsync($"/api/v2.0/registries/{registryId}", ct);
        await ThrowIfErrorAsync(response, ct, config);
    }

    public async Task<string> PingRegistryAsync(
        Guid tenantId, HarborComponentConfig config, long registryId, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync($"/api/v2.0/registries/{registryId}/ping", ct);
        if (response.IsSuccessStatusCode) return "healthy";
        string detail = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Registry ping failed ({response.StatusCode}): {detail}");
    }

    // ── Replication Policies ──────────────────────────────────────────────────

    public async Task<List<HarborReplicationInfo>> GetReplicationsAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/replication/policies?page_size=100", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? arr = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (arr is null) return [];

        return arr.Select(r =>
        {
            string? nameFilter = r?["filters"]?.AsArray()
                .FirstOrDefault(f => f?["type"]?.GetValue<string>() == "name")
                ?["value"]?.GetValue<string>();

            return new HarborReplicationInfo
            {
                Id = r?["id"]?.GetValue<long>() ?? 0,
                Name = r?["name"]?.GetValue<string>() ?? "",
                Description = r?["description"]?.GetValue<string>(),
                Enabled = r?["enabled"]?.GetValue<bool>() ?? false,
                SrcRegistryId = r?["src_registry"]?["id"]?.GetValue<long>(),
                SrcRegistryName = r?["src_registry"]?["name"]?.GetValue<string>(),
                DestRegistryId = r?["dest_registry"]?["id"]?.GetValue<long>(),
                DestRegistryName = r?["dest_registry"]?["name"]?.GetValue<string>(),
                DestNamespace = r?["dest_namespace"]?.GetValue<string>(),
                TriggerType = r?["trigger"]?["type"]?.GetValue<string>(),
                NameFilter = nameFilter,
                Override = r?["override"]?.GetValue<bool>() ?? false,
                CreatedAt = r?["creation_time"]?.GetValue<DateTime>() ?? DateTime.MinValue
            };
        }).OrderBy(r => r.Name).ToList();
    }

    public async Task CreateReplicationAsync(
        Guid tenantId, HarborComponentConfig config,
        string name, string? description,
        long? srcRegistryId, long? destRegistryId,
        string? destNamespace, string nameFilter,
        bool enabled, bool overrideExisting,
        CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);

        JsonObject body = new()
        {
            ["name"] = name,
            ["enabled"] = enabled,
            ["override"] = overrideExisting,
            ["trigger"] = new JsonObject { ["type"] = "manual" },
            ["filters"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "name",
                    ["value"] = string.IsNullOrWhiteSpace(nameFilter) ? "**" : nameFilter
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        if (!string.IsNullOrWhiteSpace(destNamespace)) body["dest_namespace"] = destNamespace;
        if (srcRegistryId.HasValue)
            body["src_registry"] = new JsonObject { ["id"] = srcRegistryId.Value };
        if (destRegistryId.HasValue)
            body["dest_registry"] = new JsonObject { ["id"] = destRegistryId.Value };

        HttpResponseMessage response = await http.PostAsync(
            "/api/v2.0/replication/policies",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);
        await ThrowIfErrorAsync(response, ct);
    }

    public async Task DeleteReplicationAsync(
        Guid tenantId, HarborComponentConfig config, long policyId, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.DeleteAsync($"/api/v2.0/replication/policies/{policyId}", ct);
        await ThrowIfErrorAsync(response, ct);
    }

    public async Task TriggerReplicationAsync(
        Guid tenantId, HarborComponentConfig config, long policyId, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        JsonObject body = new() { ["policy_id"] = policyId };
        HttpResponseMessage response = await http.PostAsync(
            "/api/v2.0/replication/executions",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);
        await ThrowIfErrorAsync(response, ct);
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    public async Task<List<HarborWebhookInfo>> GetWebhooksAsync(
        Guid tenantId, HarborComponentConfig config, string projectName, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync(
            $"/api/v2.0/projects/{projectName}/webhook/policies", ct);
        await ThrowIfErrorAsync(response, ct, config);

        JsonArray? arr = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray();
        if (arr is null) return [];

        return arr.Select(w =>
        {
            JsonNode? target = w?["targets"]?.AsArray()?.FirstOrDefault();
            List<string> events = w?["event_types"]?.AsArray()
                .Select(e => e?.GetValue<string>() ?? "")
                .Where(e => e != "")
                .ToList() ?? [];

            return new HarborWebhookInfo
            {
                Id = w?["id"]?.GetValue<long>() ?? 0,
                Name = w?["name"]?.GetValue<string>() ?? "",
                Enabled = w?["enabled"]?.GetValue<bool>() ?? false,
                Description = w?["description"]?.GetValue<string>(),
                EventTypes = events,
                TargetUrl = target?["address"]?.GetValue<string>(),
                NotifyType = target?["type"]?.GetValue<string>() ?? "http",
                CreatedAt = w?["creation_time"]?.GetValue<DateTime>() ?? DateTime.MinValue
            };
        }).OrderBy(w => w.Name).ToList();
    }

    public async Task CreateWebhookAsync(
        Guid tenantId, HarborComponentConfig config,
        string projectName, string name, string targetUrl,
        IEnumerable<string> eventTypes, bool enabled = true,
        string? authHeader = null, bool skipCertVerify = false,
        CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);

        JsonObject target = new()
        {
            ["type"] = "http",
            ["address"] = targetUrl,
            ["skip_cert_verify"] = skipCertVerify
        };
        if (!string.IsNullOrWhiteSpace(authHeader)) target["auth_header"] = authHeader;

        JsonObject body = new()
        {
            ["name"] = name,
            ["enabled"] = enabled,
            ["targets"] = new JsonArray { target },
            ["event_types"] = new JsonArray(
                eventTypes.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray())
        };

        HttpResponseMessage response = await http.PostAsync(
            $"/api/v2.0/projects/{projectName}/webhook/policies",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);
        await ThrowIfErrorAsync(response, ct);
    }

    public async Task DeleteWebhookAsync(
        Guid tenantId, HarborComponentConfig config,
        string projectName, long webhookId, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.DeleteAsync(
            $"/api/v2.0/projects/{projectName}/webhook/policies/{webhookId}", ct);
        await ThrowIfErrorAsync(response, ct);
    }

    // ── Project–App linking ───────────────────────────────────────────────────

    /// <summary>
    /// Returns all HarborProject tracking records for a given Harbor config,
    /// including the linked App and its Customer.
    /// </summary>
    public async Task<List<HarborProject>> GetTrackedProjectsAsync(
        Guid tenantId, Guid harborComponentConfigId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.HarborProjects
            .Include(p => p.LinkedApp).ThenInclude(a => a!.Customer)
            .Where(p => p.TenantId == tenantId && p.HarborComponentConfigId == harborComponentConfigId)
            .OrderBy(p => p.ProjectName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Links a Harbor project to a customer app so the customer can manage
    /// the project through the portal. Creates the tracking record if it
    /// does not exist yet.
    /// </summary>
    public async Task<HarborProject> LinkProjectToAppAsync(
        Guid tenantId, Guid harborComponentConfigId,
        string projectName, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        HarborProject? existing = await db.HarborProjects
            .FirstOrDefaultAsync(p => p.HarborComponentConfigId == harborComponentConfigId
                && p.ProjectName == projectName, ct);

        if (existing is null)
        {
            existing = new HarborProject
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                HarborComponentConfigId = harborComponentConfigId,
                ProjectName = projectName
            };
            db.HarborProjects.Add(existing);
        }

        existing.LinkedAppId = appId;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>Removes the app link from a tracked Harbor project.</summary>
    public async Task UnlinkProjectFromAppAsync(
        Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        HarborProject? project = await db.HarborProjects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId);

        if (project is null) return;

        project.LinkedAppId = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the Harbor project (with its config) linked to a specific customer app,
    /// or null if the app has no linked Harbor project. Used by the customer portal.
    /// </summary>
    public async Task<HarborProject?> GetProjectForAppAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.HarborProjects
            .Include(p => p.HarborComponentConfig)
            .Include(p => p.LinkedApp).ThenInclude(a => a!.Customer)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.LinkedAppId == appId, ct);
    }
}
