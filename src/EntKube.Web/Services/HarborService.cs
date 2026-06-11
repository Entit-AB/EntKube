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

public class HarborProjectInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Public { get; set; }
    public int RepoCount { get; set; }
    public long StorageUsedBytes { get; set; }
    public long StorageQuotaBytes { get; set; }
    public DateTime CreatedAt { get; set; }
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

/// <summary>
/// Manages Harbor container registry instances installed via the component catalog.
///
/// Integration points:
/// - CNPG: injects database host/port/name/user into Helm values; stores DB password
///   as a component vault secret so InjectSecretsIntoValuesAsync delivers it at install time.
/// - S3: reads StorageLink credentials from the vault and stores them as component
///   vault secrets (harbor-s3-access-key, harbor-s3-secret-key) for injection at install time.
/// - Vault: admin password stored as component vault secret HARBOR_ADMIN_PASSWORD.
/// </summary>
public class HarborService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    StorageService storageService,
    IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates the Harbor configuration for a component. Stores the admin
    /// password in vault and injects CNPG + S3 connection details into Helm values.
    /// Pass null for adminPassword to leave the existing password unchanged.
    /// </summary>
    public async Task<HarborComponentConfig> ConfigureAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Guid? cnpgDatabaseId,
        Guid? storageLinkId,
        string adminUsername,
        string? adminPassword,
        string? registryUrl,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

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

        return config;
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
    /// Refreshes database and S3 credentials in Helm values from the stored config.
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

    private HttpClient BuildHarborClient(string registryUrl, string username, string password)
    {
        HttpClient http = httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(registryUrl.TrimEnd('/'));
        http.Timeout = TimeSpan.FromSeconds(20);
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

    // ── System Info ───────────────────────────────────────────────────────────

    public async Task<HarborSystemInfo> GetSystemInfoAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/systeminfo", ct);
        response.EnsureSuccessStatusCode();

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
        response.EnsureSuccessStatusCode();

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
            CreatedAt = p?["creation_time"]?.GetValue<DateTime>() ?? DateTime.MinValue
        }).OrderBy(p => p.Name).ToList();
    }

    public async Task CreateProjectAsync(
        Guid tenantId, HarborComponentConfig config,
        string name, bool isPublic, string? description = null,
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

        HttpResponseMessage response = await http.PostAsync(
            "/api/v2.0/projects",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Harbor API error ({response.StatusCode}): {detail}");
        }
    }

    public async Task DeleteProjectAsync(
        Guid tenantId, HarborComponentConfig config, string projectName, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.DeleteAsync($"/api/v2.0/projects/{projectName}", ct);

        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Harbor API error ({response.StatusCode}): {detail}");
        }
    }

    // ── Repositories & Artifacts ─────────────────────────────────────────────

    public async Task<List<HarborRepositoryInfo>> GetRepositoriesAsync(
        Guid tenantId, HarborComponentConfig config, string projectName, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync(
            $"/api/v2.0/projects/{projectName}/repositories?page_size=100", ct);
        response.EnsureSuccessStatusCode();

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
            $"/api/v2.0/projects/{projectName}/repositories/{repoEncoded}/artifacts?with_tag=true&page_size=50", ct);
        response.EnsureSuccessStatusCode();

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
                PushedAt = a?["push_time"]?.GetValue<DateTime>() ?? DateTime.MinValue
            };
        }).OrderByDescending(a => a.PushedAt).ToList();
    }

    // ── Robot Accounts ────────────────────────────────────────────────────────

    public async Task<List<HarborRobotInfo>> GetRobotsAsync(
        Guid tenantId, HarborComponentConfig config, CancellationToken ct = default)
    {
        using HttpClient http = await GetHarborClientAsync(tenantId, config, ct);
        HttpResponseMessage response = await http.GetAsync("/api/v2.0/robots?page_size=100", ct);
        response.EnsureSuccessStatusCode();

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

        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Harbor API error ({response.StatusCode}): {detail}");
        }

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

        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Harbor API error ({response.StatusCode}): {detail}");
        }
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
            .FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct);

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
