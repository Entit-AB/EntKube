using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// A MinIO bucket discovered from a cluster's MinIO tenant/instance.
/// </summary>
public class MinioBucketInfo
{
    public required string Name { get; set; }
    public required string ClusterName { get; set; }
    public required string EnvironmentName { get; set; }
    public required string Namespace { get; set; }
    public string? Endpoint { get; set; }
    public string? Storage { get; set; }
    public string? Status { get; set; }
    /// <summary>"green", "yellow", or "red" from .status.healthStatus. Null on older operator versions.</summary>
    public string? HealthStatus { get; set; }
    public DateTime? CreatedAt { get; set; }
    public Guid ClusterId { get; set; }
    public List<PodInfo> Pods { get; set; } = [];
}

/// <summary>
/// Manages storage discovery and links for a tenant:
/// - Discovers MinIO tenants/buckets from clusters with the MinIO operator installed
/// - CRUD for external storage links (AWS S3, Azure Storage, Cleura S3)
/// - Credentials for external storage are kept in the vault
///
/// MinIO discovery reads the MinIO Tenant CRD from clusters.
/// External providers are registered manually — the service stores metadata
/// in StorageLink entities and credentials in the VaultSecret table.
/// </summary>
public class StorageService(IDbContextFactory<ApplicationDbContext> dbFactory, VaultService vaultService, OpenStackS3Service openStackS3, IKubernetesClientFactory k8sFactory, StorageLinkClientFactory storageClientFactory, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
{
    // CubeFS service coordinates for in-cluster access, overridable per deployment via config
    // (the CubeFS Helm chart's service names/ports can differ). Defaults match the chart's
    // conventional master (client port 17010) and object-gateway (17410) services.
    private string CubeFsMasterService => configuration?["CubeFS:MasterService"] ?? "master";
    private int CubeFsMasterPort => int.TryParse(configuration?["CubeFS:MasterPort"], out int p) ? p : 17010;
    private string CubeFsObjectNodeService => configuration?["CubeFS:ObjectNodeService"] ?? "objectnode";
    private int CubeFsObjectNodePort => int.TryParse(configuration?["CubeFS:ObjectNodePort"], out int p) ? p : 17410;

    // ──────── Operator Status ────────

    /// <summary>
    /// Checks if MinIO is installed on any of the tenant's clusters.
    /// </summary>
    public async Task<bool> IsMinioAvailableAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ClusterComponents
            .AnyAsync(c => c.Cluster.TenantId == tenantId
                && (c.Name == "minio" || c.Name == "minio-operator")
                && c.Status == ComponentStatus.Installed, ct);
    }

    /// <summary>
    /// Lists installed CubeFS components across the tenant's clusters, so the UI can offer
    /// them as the target for provisioning an S3 bucket on the CubeFS object gateway.
    /// </summary>
    public async Task<List<ClusterComponent>> GetCubeFSComponentsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Cluster.TenantId == tenantId
                && c.Name == "cubefs"
                && c.Status == ComponentStatus.Installed)
            .OrderBy(c => c.Cluster.Name)
            .ToListAsync(ct);
    }

    // ──────── MinIO Discovery ────────

    /// <summary>
    /// Discovers MinIO instances running on the tenant's clusters by querying
    /// for MinIO Tenant CRDs or by checking the MinIO service endpoints.
    /// Returns basic info about each MinIO deployment found.
    /// </summary>
    public async Task<List<MinioBucketInfo>> GetMinioInstancesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Find clusters with MinIO installed.

        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Include(k => k.Environment)
            .Include(k => k.Components)
            .Where(k => k.TenantId == tenantId
                && k.Components.Any(c => (c.Name == "minio" || c.Name == "minio-operator") && c.Status == ComponentStatus.Installed))
            .ToListAsync(ct);

        List<MinioBucketInfo> results = [];

        foreach (KubernetesCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                continue;
            }

            try
            {
                List<MinioBucketInfo> instances = await QueryMinioTenantsAsync(cluster, ct);
                results.AddRange(instances);
            }
            catch
            {
                // Cluster unreachable — skip gracefully.
            }
        }

        return results;
    }

    /// <summary>
    /// Queries a cluster for MinIO Tenant CRDs (minio.min.io/v2).
    /// </summary>
    private static async Task<List<MinioBucketInfo>> QueryMinioTenantsAsync(
        KubernetesCluster cluster, CancellationToken ct)
    {
        using Kubernetes client = CreateClient(cluster.Kubeconfig!);

        object response = await client.CustomObjects.ListClusterCustomObjectAsync(
            group: "minio.min.io",
            version: "v2",
            plural: "tenants",
            cancellationToken: ct);

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(response));

        List<MinioBucketInfo> results = [];

        if (json.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                MinioBucketInfo info = ParseMinioTenant(item, cluster);

                try
                {
                    V1PodList podList = await client.CoreV1.ListNamespacedPodAsync(
                        info.Namespace,
                        labelSelector: $"v1.min.io/tenant={info.Name}",
                        cancellationToken: ct);

                    info.Pods = podList.Items.Select(pod => new PodInfo
                    {
                        Name = pod.Metadata.Name,
                        Namespace = info.Namespace,
                        Status = pod.Status?.Phase ?? "Unknown",
                        ReadyContainers = pod.Status?.ContainerStatuses?.Count(cs => cs.Ready) ?? 0,
                        TotalContainers = pod.Spec?.Containers?.Count ?? 0,
                        Restarts = pod.Status?.ContainerStatuses?.Sum(cs => cs.RestartCount) ?? 0,
                        StartTime = pod.Status?.StartTime,
                        Containers = (pod.Status?.ContainerStatuses ?? []).Select(cs => new ContainerInfo
                        {
                            Name = cs.Name,
                            Image = cs.Image ?? "",
                            Ready = cs.Ready,
                            RestartCount = cs.RestartCount,
                            State = cs.State?.Running is not null ? "Running"
                                : cs.State?.Waiting is not null ? $"Waiting: {cs.State.Waiting.Reason}"
                                : cs.State?.Terminated is not null ? $"Terminated: {cs.State.Terminated.Reason}"
                                : "Unknown"
                        }).ToList()
                    }).ToList();
                }
                catch
                {
                    // Pod listing failed — leave Pods empty, status badge is still shown.
                }

                results.Add(info);
            }
        }

        return results;
    }

    private static MinioBucketInfo ParseMinioTenant(JsonElement item, KubernetesCluster cluster)
    {
        JsonElement metadata = item.GetProperty("metadata");

        string name = metadata.GetProperty("name").GetString() ?? "unknown";
        string ns = metadata.GetProperty("namespace").GetString() ?? "default";

        // Try to get storage and status from the spec/status.

        string? storage = null;

        if (item.TryGetProperty("spec", out JsonElement spec)
            && spec.TryGetProperty("pools", out JsonElement pools))
        {
            foreach (JsonElement pool in pools.EnumerateArray())
            {
                if (pool.TryGetProperty("volumeClaimTemplate", out JsonElement vct)
                    && vct.TryGetProperty("spec", out JsonElement vctSpec)
                    && vctSpec.TryGetProperty("resources", out JsonElement res)
                    && res.TryGetProperty("requests", out JsonElement req)
                    && req.TryGetProperty("storage", out JsonElement storEl))
                {
                    storage = storEl.GetString();
                    break;
                }
            }
        }

        string status = "Unknown";
        string? healthStatus = null;

        if (item.TryGetProperty("status", out JsonElement statusEl))
        {
            if (statusEl.TryGetProperty("currentState", out JsonElement stateEl))
                status = stateEl.GetString() ?? "Unknown";

            if (statusEl.TryGetProperty("healthStatus", out JsonElement healthEl))
                healthStatus = healthEl.GetString();
        }

        DateTime? createdAt = metadata.TryGetProperty("creationTimestamp", out JsonElement tsEl)
            ? tsEl.GetDateTime() : null;

        // The MinIO operator creates a service named after the tenant CR (e.g. "my-tenant.my-ns.svc...").
        // Standalone MinIO also uses the release name. Either way, the service name matches the CR/release name.
        //
        // TLS detection: MinIO operator enables TLS by default (requestAutoCert: true).
        // Only use http:// if requestAutoCert is explicitly false AND no external cert secret is configured.
        bool tlsEnabled = true;
        if (item.TryGetProperty("spec", out JsonElement tlsSpec))
        {
            if (tlsSpec.TryGetProperty("requestAutoCert", out JsonElement autocertEl)
                && autocertEl.ValueKind == JsonValueKind.False)
            {
                // requestAutoCert disabled — only plain HTTP if no external cert is set either.
                bool hasExternalCert = tlsSpec.TryGetProperty("externalCertSecret", out JsonElement extCert)
                    && extCert.ValueKind != JsonValueKind.Null
                    && extCert.ValueKind != JsonValueKind.Undefined;
                tlsEnabled = hasExternalCert;
            }
        }

        string scheme = tlsEnabled ? "https" : "http";
        string endpoint = $"{scheme}://{name}.{ns}.svc.cluster.local:9000";

        return new MinioBucketInfo
        {
            Name = name,
            ClusterName = cluster.Name,
            EnvironmentName = cluster.Environment.Name,
            Namespace = ns,
            Endpoint = endpoint,
            Storage = storage,
            Status = status,
            HealthStatus = healthStatus,
            CreatedAt = createdAt,
            ClusterId = cluster.Id
        };
    }

    // ──────── Storage Links (External Providers) ────────

    /// <summary>
    /// Lists all storage links for a tenant, optionally filtered by environment.
    /// </summary>
    public async Task<List<StorageLink>> GetStorageLinksAsync(
        Guid tenantId, Guid? environmentId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        IQueryable<StorageLink> query = db.StorageLinks
            .Include(s => s.Environment)
            .Include(s => s.Component)
            .Where(s => s.TenantId == tenantId);

        if (environmentId.HasValue)
        {
            query = query.Where(s => s.EnvironmentId == environmentId.Value);
        }

        return await query.OrderBy(s => s.Provider).ThenBy(s => s.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Creates a new external storage link and stores its credentials in the vault.
    /// The access key and secret key are encrypted and kept as component-free vault secrets.
    /// For Cleura S3, an OpenStack connection ID is required.
    /// </summary>
    public async Task<StorageLink> CreateStorageLinkAsync(
        Guid tenantId,
        Guid environmentId,
        StorageProvider provider,
        string name,
        string? endpoint,
        string? bucketName,
        string? region,
        string? accessKey,
        string? secretKey,
        string? notes,
        Guid? openStackConnectionId = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Provider = provider,
            Name = name,
            Endpoint = endpoint,
            BucketName = bucketName,
            Region = region,
            Notes = notes,
            OpenStackConnectionId = openStackConnectionId
        };

        db.StorageLinks.Add(link);
        await db.SaveChangesAsync(ct);

        // Store credentials in the vault scoped to this storage link.

        if (!string.IsNullOrWhiteSpace(accessKey))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "ACCESS_KEY", accessKey, ct);
        }

        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "SECRET_KEY", secretKey, ct);
        }

        return link;
    }

    /// <summary>
    /// Registers a discovered MinIO Tenant as a StorageLink so it can be browsed
    /// and managed like any other storage provider. Credentials are stored in the vault.
    /// ComponentId is set to the minio/minio-operator component on the cluster so the
    /// link is scoped to a specific cluster, preventing false "already registered" matches
    /// when multiple clusters share the same tenant namespace name.
    /// </summary>
    public async Task<StorageLink> RegisterMinioTenantAsync(
        Guid tenantId,
        Guid environmentId,
        Guid clusterId,
        string name,
        string endpoint,
        string accessKey,
        string secretKey,
        string? notes,
        CancellationToken ct = default)
    {
        StorageLink link = await CreateStorageLinkAsync(
            tenantId,
            environmentId,
            StorageProvider.MinIO,
            name,
            endpoint,
            bucketName: null,
            region: "us-east-1",
            accessKey,
            secretKey,
            notes,
            ct: ct);

        // Attach the link to the minio operator/standalone component on this cluster so that
        // IsMinioRegistered can distinguish tenants on different clusters by cluster identity.
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.ClusterId == clusterId
                && (c.Name == "minio-operator" || c.Name == "minio"), ct);

        if (component is not null)
        {
            StorageLink? stored = await db.StorageLinks.FindAsync([link.Id], ct);
            if (stored is not null)
            {
                stored.ComponentId = component.Id;
                await db.SaveChangesAsync(ct);
            }
        }

        return link;
    }

    /// <summary>
    /// Updates an existing storage link's metadata. Credentials are updated
    /// separately via the vault.
    /// </summary>
    public async Task UpdateStorageLinkAsync(
        Guid linkId,
        string name,
        string? endpoint,
        string? bucketName,
        string? region,
        string? notes,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StorageLink? link = await db.StorageLinks.FindAsync([linkId], ct);

        if (link is null)
        {
            return;
        }

        link.Name = name;
        link.Endpoint = endpoint;
        link.BucketName = bucketName;
        link.Region = region;
        link.Notes = notes;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a storage link and its associated vault secrets.
    /// </summary>
    public async Task DeleteStorageLinkAsync(Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StorageLink? link = await db.StorageLinks.FindAsync([linkId], ct);

        if (link is null)
        {
            return;
        }

        // Remove vault secrets associated with this link.

        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenantId, linkId, ct);

        foreach (VaultSecret secret in secrets)
        {
            await vaultService.DeleteSecretAsync(secret.Id, ct);
        }

        db.StorageLinks.Remove(link);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Cleura S3 Bucket Provisioning ────────

    /// <summary>
    /// Provisions a new Cleura S3 bucket end-to-end:
    /// 1. Authenticates via OpenStack Keystone
    /// 2. Creates EC2 credentials (S3 access/secret key)
    /// 3. Creates the bucket via the S3 API
    /// 4. Stores the credentials in the vault
    /// 5. Creates a StorageLink record
    ///
    /// The user specifies the bucket name; the OpenStack connection provides
    /// the authentication details and region.
    /// </summary>
    public async Task<StorageLink> ProvisionCleuraS3BucketAsync(
        Guid tenantId,
        Guid environmentId,
        Guid openStackConnectionId,
        string bucketName,
        string? displayName,
        string? notes,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load the OpenStack connection.

        OpenStackConnection connection = await db.OpenStackConnections
            .FirstOrDefaultAsync(c => c.Id == openStackConnectionId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("OpenStack connection not found.");

        // Provision the bucket via OpenStack (Keystone auth → EC2 creds → S3 create).

        CleuraS3ProvisionResult result = await openStackS3.CreateBucketAsync(
            tenantId, connection, bucketName, ct);

        // Create the storage link with the provisioned details.

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Provider = StorageProvider.CleuraS3,
            Name = displayName ?? bucketName,
            Endpoint = result.Endpoint,
            BucketName = result.BucketName,
            Region = result.Region,
            Notes = notes,
            OpenStackConnectionId = openStackConnectionId
        };

        db.StorageLinks.Add(link);
        await db.SaveChangesAsync(ct);

        // Store the generated S3 credentials and connection info in the vault.
        // These secrets are the full set needed by any app or component to use the bucket.

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "ACCESS_KEY", result.AccessKey, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "SECRET_KEY", result.SecretKey, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "ENDPOINT", result.Endpoint, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "BUCKET_NAME", result.BucketName, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "REGION", result.Region, ct);

        return link;
    }

    // ──────── CubeFS Bucket Provisioning ────────

    /// <summary>
    /// Provisions an S3 bucket on a cluster-hosted CubeFS object gateway and registers it
    /// as a <see cref="StorageProvider.CubeFS"/> storage link. The bucket is created through
    /// CubeFS's S3-compatible gateway (reached via the K8s API-server proxy, like MinIO) using
    /// the supplied CubeFS user credentials, which are then stored in the vault so any app,
    /// component, or telemetry backend can consume it through the standard storage-link rails.
    /// </summary>
    /// <param name="componentId">The installed CubeFS <c>ClusterComponent</c> (used to resolve the cluster kubeconfig).</param>
    /// <param name="endpoint">Cluster-internal ObjectNode service URL, e.g. "http://objectnode.cubefs-system.svc.cluster.local:17410".</param>
    public async Task<StorageLink> ProvisionCubeFSBucketAsync(
        Guid tenantId,
        Guid environmentId,
        Guid componentId,
        string endpoint,
        string accessKey,
        string secretKey,
        string bucketName,
        string? displayName,
        string? notes,
        CancellationToken ct = default)
    {
        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Provider = StorageProvider.CubeFS,
            Name = displayName ?? bucketName,
            Endpoint = endpoint,
            BucketName = bucketName,
            Region = "us-east-1", // CubeFS ignores region; a value is required by the S3 client.
            Notes = notes,
            ComponentId = componentId
        };

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            db.StorageLinks.Add(link);
            await db.SaveChangesAsync(ct);
        }

        // Credentials must exist before we build the S3 client (the factory reads them back).
        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "ACCESS_KEY", accessKey, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "SECRET_KEY", secretKey, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "ENDPOINT", endpoint, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "BUCKET_NAME", bucketName, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, link.Id, "REGION", link.Region!, ct);

        // Create the bucket through CubeFS's S3 gateway (idempotent — tolerate "already exists").
        (Amazon.S3.AmazonS3Client s3, IDisposable? cleanup) = await storageClientFactory.CreateClientAsync(tenantId, link, ct);
        try
        {
            await s3.PutBucketAsync(new Amazon.S3.Model.PutBucketRequest { BucketName = bucketName }, ct);
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Bucket already present — fine, the link still points at it.
        }
        finally
        {
            s3.Dispose();
            cleanup?.Dispose();
        }

        return link;
    }

    /// <summary>
    /// Zero-touch backup target: mints a fresh CubeFS object-store user on the cluster's CubeFS
    /// master, then provisions a bucket for it and registers a <see cref="StorageProvider.CubeFS"/>
    /// storage link — all without any pre-existing credentials. This is what lets a freshly
    /// provisioned cluster wire Velero to CubeFS with nothing supplied by the operator.
    ///
    /// The CubeFS master API is reached through the Kubernetes API-server service proxy (same
    /// mechanism as the object gateway). The access/secret keys are generated here and passed to
    /// the master so we know them regardless of the master's response shape; on a resumed run the
    /// user already exists and its stored keys are read back via <c>/user/info</c>.
    /// </summary>
    public async Task<StorageLink> ProvisionCubeFSBackupTargetAsync(
        Guid tenantId, Guid environmentId, Guid cubefsComponentId, string bucketName,
        string? displayName = null, string? notes = null, CancellationToken ct = default)
    {
        ClusterComponent component;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            component = await db.ClusterComponents
                .Include(c => c.Cluster)
                .FirstOrDefaultAsync(c => c.Id == cubefsComponentId && c.Cluster.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("CubeFS component not found.");
        }

        string kubeconfig = component.Cluster.Kubeconfig
            ?? throw new InvalidOperationException("CubeFS component's cluster has no kubeconfig.");
        string ns = string.IsNullOrWhiteSpace(component.Namespace) ? "cubefs-system" : component.Namespace;
        string endpoint = $"http://{CubeFsObjectNodeService}.{ns}.svc.cluster.local:{CubeFsObjectNodePort}";

        // Mint (or read back) a dedicated object user for this backup target.
        (string accessKey, string secretKey) = await MintCubeFSObjectUserAsync(
            kubeconfig, ns, bucketName, CubeFsMasterService, CubeFsMasterPort, ct);

        return await ProvisionCubeFSBucketAsync(
            tenantId, environmentId, cubefsComponentId, endpoint, accessKey, secretKey, bucketName,
            displayName ?? bucketName, notes, ct);
    }

    /// <summary>
    /// Creates a CubeFS object-store user (or returns the existing one's keys) via the CubeFS
    /// master API, proxied through the Kubernetes API server. CubeFS requires access keys to be
    /// 16 chars and secret keys 32 chars; we generate compliant keys and pass them on create so
    /// the caller always knows them. Idempotent: a "user already exists" response falls back to
    /// <c>/user/info</c> to read the persisted keys.
    /// </summary>
    private static async Task<(string AccessKey, string SecretKey)> MintCubeFSObjectUserAsync(
        string kubeconfig, string masterNamespace, string userId,
        string masterService, int masterPort, CancellationToken ct)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration cfg = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        using Kubernetes k8s = new(cfg);
        // The master client-facing service is reached via the K8s API-server service proxy.
        string proxyBase =
            $"{k8s.BaseUri.ToString().TrimEnd('/')}/api/v1/namespaces/{masterNamespace}/services/{masterService}:{masterPort}/proxy";

        string accessKey = GenerateCubeFsKey(16);
        string secretKey = GenerateCubeFsKey(32);

        // type 3 = a normal (non-admin) object-store user.
        string createBody = JsonSerializer.Serialize(new
        {
            id = userId,
            type = 3,
            access_key = accessKey,
            secret_key = secretKey,
        });

        using HttpContent content = new StringContent(createBody, Encoding.UTF8, "application/json");
        using HttpResponseMessage createResp = await k8s.HttpClient.PostAsync($"{proxyBase}/user/create", content, ct);
        string createJson = await createResp.Content.ReadAsStringAsync(ct);

        using JsonDocument createDoc = JsonDocument.Parse(createJson);
        int code = createDoc.RootElement.TryGetProperty("code", out JsonElement codeEl) ? codeEl.GetInt32() : -1;

        if (createResp.IsSuccessStatusCode && code == 0)
        {
            // Trust the keys we supplied (the response echoes them under data).
            return (accessKey, secretKey);
        }

        // Already exists (or the master rejected our keys) — read the persisted user back.
        using HttpResponseMessage infoResp = await k8s.HttpClient.GetAsync(
            $"{proxyBase}/user/info?user={Uri.EscapeDataString(userId)}", ct);
        string infoJson = await infoResp.Content.ReadAsStringAsync(ct);
        using JsonDocument infoDoc = JsonDocument.Parse(infoJson);

        if (infoResp.IsSuccessStatusCode
            && infoDoc.RootElement.TryGetProperty("data", out JsonElement data)
            && data.TryGetProperty("access_key", out JsonElement ak)
            && data.TryGetProperty("secret_key", out JsonElement sk))
        {
            return (ak.GetString() ?? accessKey, sk.GetString() ?? secretKey);
        }

        throw new InvalidOperationException(
            $"CubeFS user provisioning failed (create code {code}): {createJson.Trim()}");
    }

    private static readonly char[] CubeFsKeyAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    private static string GenerateCubeFsKey(int length)
    {
        char[] buffer = new char[length];
        byte[] randomBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        for (int i = 0; i < length; i++)
            buffer[i] = CubeFsKeyAlphabet[randomBytes[i] % CubeFsKeyAlphabet.Length];
        return new string(buffer);
    }

    // ──────── Cleura S3 Bucket Management ────────

    /// <summary>
    /// Lists all buckets accessible by the credentials stored for a given storage link.
    /// This shows what buckets exist under the project — useful for verifying
    /// provisioning and discovering buckets created outside EntKube.
    /// </summary>
    public async Task<List<S3BucketInfo>> ListCleuraS3BucketsAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        StorageLink link = await GetCleuraLinkOrThrowAsync(tenantId, linkId, ct);

        // Prefer the Swift API (via the stored OpenStack connection) because it
        // enumerates all project containers, whereas S3 ListBuckets scopes to the
        // specific EC2 credential's RGW user and often returns an empty list.

        if (link.OpenStackConnectionId.HasValue)
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            OpenStackConnection? connection = await db.OpenStackConnections
                .FirstOrDefaultAsync(c => c.Id == link.OpenStackConnectionId.Value, ct);

            if (connection is not null)
            {
                string? password = await vaultService.GetOpenStackSecretValueAsync(
                    tenantId, connection.Id, "OS_PASSWORD", ct);

                if (!string.IsNullOrEmpty(password))
                {
                    return await openStackS3.ListBucketsViaSwiftAsync(connection, password, ct);
                }
            }
        }

        // Fallback: use the stored EC2 credentials with the S3 API.

        (string accessKey, string secretKey) = await GetStoredCredentialsAsync(tenantId, linkId, ct);

        return await openStackS3.ListBucketsAsync(
            link.Endpoint ?? "", accessKey, secretKey, link.Region ?? "", ct);
    }

    /// <summary>
    /// Deletes a Cleura S3 bucket and removes the associated StorageLink.
    /// The bucket must be empty. This is a destructive operation — the bucket
    /// and all its metadata in EntKube will be permanently removed.
    /// </summary>
    public async Task DeleteCleuraS3BucketAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        StorageLink link = await GetCleuraLinkOrThrowAsync(tenantId, linkId, ct);

        // Only attempt S3 deletion when we have all the connection details.
        // If any field is missing the bucket may not exist (or was never fully provisioned),
        // so we fall through and clean up the DB record regardless.

        if (!string.IsNullOrEmpty(link.Endpoint)
            && !string.IsNullOrEmpty(link.BucketName)
            && !string.IsNullOrEmpty(link.Region))
        {
            string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "ACCESS_KEY", ct);
            string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "SECRET_KEY", ct);

            if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
            {
                await openStackS3.DeleteBucketAsync(
                    link.Endpoint, accessKey, secretKey, link.BucketName, link.Region, ct);
            }
            // If credentials are missing the bucket cannot be deleted via the S3 API,
            // but we still remove the StorageLink record so the link can be cleaned up.
        }

        // Remove the StorageLink and vault secrets.

        await DeleteStorageLinkAsync(tenantId, linkId, ct);
    }

    /// <summary>
    /// Lists all buckets accessible with the stored credentials for any S3-compatible link (MinIO or Cleura).
    /// MinIO links are proxied through the Kubernetes API server; Cleura uses the S3 API directly.
    /// </summary>
    public async Task<List<S3BucketInfo>> ListS3BucketsAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        StorageLink link = await GetS3LinkOrThrowAsync(tenantId, linkId, ct);
        (string accessKey, string secretKey) = await GetStoredCredentialsAsync(tenantId, linkId, ct);
        string? kubeconfig = await ResolveMinioKubeconfigAsync(link, ct);

        if (kubeconfig is not null && link.Endpoint is not null)
        {
            ProxiedS3Client? proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, link.Endpoint, accessKey, secretKey, ct: ct);
            if (proxied is not null)
            {
                using var _ = proxied;
                try
                {
                    Amazon.S3.Model.ListBucketsResponse resp = await proxied.S3.ListBucketsAsync(ct);
                    return (resp.Buckets ?? [])
                        .Select(b => new S3BucketInfo { Name = b.BucketName ?? "", CreatedAt = b.CreationDate.GetValueOrDefault() })
                        .ToList();
                }
                catch (Amazon.S3.AmazonS3Exception s3ex)
                {
                    throw new InvalidOperationException(
                        $"MinIO S3 error [{(int)s3ex.StatusCode} {s3ex.ErrorCode}]: {s3ex.Message}", s3ex);
                }
            }
        }

        return await openStackS3.ListBucketsAsync(
            link.Endpoint ?? "", accessKey, secretKey, link.Region ?? "us-east-1", ct);
    }

    /// <summary>
    /// Gets the CORS configuration for an S3-compatible bucket (MinIO or Cleura).
    /// Returns null if no CORS configuration exists.
    /// </summary>
    public async Task<List<S3CorsRule>?> GetBucketCorsAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        StorageLink link = await GetS3LinkOrThrowAsync(tenantId, linkId, ct);
        (string accessKey, string secretKey) = await GetStoredCredentialsAsync(tenantId, linkId, ct);
        string? kubeconfig = await ResolveMinioKubeconfigAsync(link, ct);

        if (kubeconfig is not null && link.Endpoint is not null)
        {
            ProxiedS3Client? proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, link.Endpoint, accessKey, secretKey, ct: ct);
            if (proxied is not null)
            {
                using var _ = proxied;
                return await openStackS3.GetBucketCorsAsync(proxied.S3, link.BucketName!, ct);
            }
        }

        return await openStackS3.GetBucketCorsAsync(
            link.Endpoint!, accessKey, secretKey, link.BucketName!, link.Region!, ct);
    }

    /// <summary>
    /// Sets the CORS configuration for an S3-compatible bucket (MinIO or Cleura).
    /// Pass an empty list to remove all CORS rules.
    /// </summary>
    public async Task SetBucketCorsAsync(
        Guid tenantId, Guid linkId, List<S3CorsRule> rules, CancellationToken ct = default)
    {
        StorageLink link = await GetS3LinkOrThrowAsync(tenantId, linkId, ct);
        (string accessKey, string secretKey) = await GetStoredCredentialsAsync(tenantId, linkId, ct);
        string? kubeconfig = await ResolveMinioKubeconfigAsync(link, ct);

        if (kubeconfig is not null && link.Endpoint is not null)
        {
            ProxiedS3Client? proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, link.Endpoint, accessKey, secretKey, ct: ct);
            if (proxied is not null)
            {
                using var _ = proxied;
                await openStackS3.SetBucketCorsAsync(proxied.S3, link.BucketName!, rules, ct);
                return;
            }
        }

        await openStackS3.SetBucketCorsAsync(
            link.Endpoint!, accessKey, secretKey, link.BucketName!, link.Region!, rules, ct);
    }

    /// <summary>
    /// Gets the bucket policy as a JSON string for an S3-compatible link (MinIO or Cleura).
    /// Returns null if no policy is set.
    /// </summary>
    public async Task<string?> GetBucketPolicyAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        StorageLink link = await GetS3LinkOrThrowAsync(tenantId, linkId, ct);
        (string accessKey, string secretKey) = await GetStoredCredentialsAsync(tenantId, linkId, ct);
        string? kubeconfig = await ResolveMinioKubeconfigAsync(link, ct);

        if (kubeconfig is not null && link.Endpoint is not null)
        {
            ProxiedS3Client? proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, link.Endpoint, accessKey, secretKey, ct: ct);
            if (proxied is not null)
            {
                using var _ = proxied;
                return await openStackS3.GetBucketPolicyAsync(proxied.S3, link.BucketName!, ct);
            }
        }

        return await openStackS3.GetBucketPolicyAsync(
            link.Endpoint!, accessKey, secretKey, link.BucketName!, link.Region!, ct);
    }

    /// <summary>
    /// Sets the bucket policy from a JSON string for an S3-compatible link (MinIO or Cleura).
    /// Pass null to remove the policy.
    /// </summary>
    public async Task SetBucketPolicyAsync(
        Guid tenantId, Guid linkId, string? policyJson, CancellationToken ct = default)
    {
        StorageLink link = await GetS3LinkOrThrowAsync(tenantId, linkId, ct);
        (string accessKey, string secretKey) = await GetStoredCredentialsAsync(tenantId, linkId, ct);
        string? kubeconfig = await ResolveMinioKubeconfigAsync(link, ct);

        if (kubeconfig is not null && link.Endpoint is not null)
        {
            ProxiedS3Client? proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, link.Endpoint, accessKey, secretKey, ct: ct);
            if (proxied is not null)
            {
                using var _ = proxied;
                await openStackS3.SetBucketPolicyAsync(proxied.S3, link.BucketName!, policyJson, ct);
                return;
            }
        }

        await openStackS3.SetBucketPolicyAsync(
            link.Endpoint!, accessKey, secretKey, link.BucketName!, link.Region!, policyJson, ct);
    }

    /// <summary>
    /// Rotates the EC2 credentials for a Cleura S3 storage link.
    /// Creates new credentials, verifies they work, and updates the vault.
    /// The old credentials become invalid after this operation.
    /// </summary>
    public async Task RotateCleuraS3CredentialsAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TenantId == tenantId && l.Provider == StorageProvider.CleuraS3, ct)
            ?? throw new InvalidOperationException("Cleura S3 storage link not found.");

        OpenStackConnection connection = await db.OpenStackConnections
            .FirstOrDefaultAsync(c => c.Id == link.OpenStackConnectionId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("OpenStack connection not found.");

        // Rotate credentials via Keystone (creates new EC2 credentials and verifies them).

        (string newAccessKey, string newSecretKey) = await openStackS3.RotateCredentialsAsync(
            tenantId, connection, link.BucketName!, ct);

        // Update the vault with the new credentials.

        await vaultService.SetStorageLinkSecretAsync(tenantId, linkId, "ACCESS_KEY", newAccessKey, ct);
        await vaultService.SetStorageLinkSecretAsync(tenantId, linkId, "SECRET_KEY", newSecretKey, ct);
    }

    // ──────── Private Helpers ────────

    /// <summary>
    /// Loads a Cleura S3 StorageLink or throws if not found.
    /// </summary>
    private async Task<StorageLink> GetCleuraLinkOrThrowAsync(
        Guid tenantId, Guid linkId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.StorageLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TenantId == tenantId && l.Provider == StorageProvider.CleuraS3, ct)
            ?? throw new InvalidOperationException("Cleura S3 storage link not found.");
    }

    /// <summary>
    /// Loads any S3-compatible StorageLink (MinIO or CleuraS3) or throws if not found.
    /// For MinIO links, Component and its Cluster are included so the kubeconfig is available.
    /// </summary>
    private async Task<StorageLink> GetS3LinkOrThrowAsync(
        Guid tenantId, Guid linkId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.StorageLinks
            .Include(l => l.Component).ThenInclude(c => c!.Cluster)
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TenantId == tenantId
                && (l.Provider == StorageProvider.CleuraS3
                    || l.Provider == StorageProvider.MinIO
                    || l.Provider == StorageProvider.CubeFS), ct)
            ?? throw new InvalidOperationException("S3-compatible storage link not found.");
    }

    /// <summary>
    /// Returns the kubeconfig for the cluster that hosts a cluster-local S3 gateway
    /// (MinIO or CubeFS). First checks the eagerly-loaded Component → Cluster navigation
    /// (set for links registered after ComponentId tracking was added). Falls back to a
    /// DB query for older links where ComponentId is null, matching by tenant + the
    /// gateway component namespace derived from the stored internal endpoint.
    /// Returns null for external (non-cluster-hosted) links.
    /// </summary>
    private async Task<string?> ResolveMinioKubeconfigAsync(StorageLink link, CancellationToken ct)
    {
        if (link.Provider is not (StorageProvider.MinIO or StorageProvider.CubeFS))
            return null;

        // Only proxy through K8s for internal cluster-local endpoints.
        // External endpoints are reached directly — no proxy needed.
        if (KubernetesS3Proxy.ParseInternalServiceUrl(link.Endpoint ?? "") is null)
            return null;

        // Fast path: ComponentId was set at registration time.
        string? kubeconfig = link.Component?.Cluster?.Kubeconfig;
        if (!string.IsNullOrWhiteSpace(kubeconfig))
            return kubeconfig;

        // Fallback for links registered before ComponentId tracking.
        // Parse the namespace from the internal endpoint and find a matching cluster.
        var parsed = KubernetesS3Proxy.ParseInternalServiceUrl(link.Endpoint ?? "");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        IQueryable<ClusterComponent> query = db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Cluster.TenantId == link.TenantId
                && (c.Name == "minio-operator" || c.Name == "minio" || c.Name == "cubefs")
                && c.Status == ComponentStatus.Installed
                && c.Cluster.KubeconfigSecretId != null);

        // Prefer the component whose namespace matches the endpoint namespace.
        if (parsed is not null)
            query = query.OrderByDescending(c => c.Namespace == parsed.Value.Namespace);

        ClusterComponent? component = await query.FirstOrDefaultAsync(ct);
        return component?.Cluster?.Kubeconfig;
    }

    /// <summary>
    /// Returns the stored S3 credentials for a storage link. Returns empty strings when not found.
    /// Used by other services (e.g. HarborService) to read credentials without a vault error.
    /// </summary>
    public async Task<(string AccessKey, string SecretKey)> GetStoredCredentialsInternalAsync(
        Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "SECRET_KEY", ct);
        return (accessKey ?? "", secretKey ?? "");
    }

    /// <summary>
    /// Retrieves the stored S3 credentials (access/secret key) from vault for a storage link.
    /// </summary>
    private async Task<(string AccessKey, string SecretKey)> GetStoredCredentialsAsync(
        Guid tenantId, Guid linkId, CancellationToken ct)
    {
        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "SECRET_KEY", ct);

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "S3 credentials not found in vault. The credentials may need to be re-provisioned.");
        }

        return (accessKey, secretKey);
    }

    /// <summary>
    /// Gets the list of environments for a tenant (for the UI dropdown).
    /// </summary>
    public async Task<List<Data.Environment>> GetEnvironmentsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<Data.Environment>()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Name)
            .ToListAsync(ct);
    }

    // ──────── OpenStack Connections ────────

    /// <summary>
    /// Lists all OpenStack connections for a tenant.
    /// These are required for Cleura S3 storage management.
    /// </summary>
    public async Task<List<OpenStackConnection>> GetOpenStackConnectionsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.OpenStackConnections
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a new OpenStack connection and stores the password in the vault.
    /// The connection enables managing Cleura S3 buckets and credentials via
    /// the OpenStack API (Keystone auth → Swift/S3 operations).
    /// </summary>
    public async Task<OpenStackConnection> CreateOpenStackConnectionAsync(
        Guid tenantId,
        string name,
        string authUrl,
        string? region,
        string? projectName,
        string? projectId,
        string? userDomainName,
        string? projectDomainName,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OpenStackConnection connection = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            AuthUrl = authUrl,
            Region = region,
            ProjectName = projectName,
            ProjectId = projectId,
            UserDomainName = userDomainName,
            ProjectDomainName = projectDomainName,
            Username = username
        };

        db.OpenStackConnections.Add(connection);
        await db.SaveChangesAsync(ct);

        // Store the password in the vault, keyed by the connection ID.

        if (!string.IsNullOrWhiteSpace(password))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetOpenStackSecretAsync(tenantId, connection.Id, "OS_PASSWORD", password, ct);
        }

        return connection;
    }

    /// <summary>
    /// Updates an OpenStack connection's metadata. Password is updated
    /// separately via the vault if provided.
    /// </summary>
    public async Task UpdateOpenStackConnectionAsync(
        Guid connectionId,
        string name,
        string authUrl,
        string? region,
        string? projectName,
        string? projectId,
        string? userDomainName,
        string? projectDomainName,
        string? username,
        string? password,
        Guid tenantId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OpenStackConnection? connection = await db.OpenStackConnections.FindAsync([connectionId], ct);

        if (connection is null)
        {
            return;
        }

        connection.Name = name;
        connection.AuthUrl = authUrl;
        connection.Region = region;
        connection.ProjectName = projectName;
        connection.ProjectId = projectId;
        connection.UserDomainName = userDomainName;
        connection.ProjectDomainName = projectDomainName;
        connection.Username = username;
        await db.SaveChangesAsync(ct);

        // Update password if a new one was provided.

        if (!string.IsNullOrWhiteSpace(password))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetOpenStackSecretAsync(tenantId, connectionId, "OS_PASSWORD", password, ct);
        }
    }

    /// <summary>
    /// Deletes an OpenStack connection and its vault-stored password.
    /// Fails silently if any storage links still reference this connection.
    /// </summary>
    public async Task<bool> DeleteOpenStackConnectionAsync(
        Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Don't delete if storage links depend on this connection.

        bool hasLinks = await db.StorageLinks
            .AnyAsync(s => s.OpenStackConnectionId == connectionId, ct);

        if (hasLinks)
        {
            return false;
        }

        OpenStackConnection? connection = await db.OpenStackConnections.FindAsync([connectionId], ct);

        if (connection is null)
        {
            return false;
        }

        // Remove vault secret.

        List<VaultSecret> secrets = await vaultService.GetOpenStackSecretsAsync(tenantId, connectionId, ct);

        foreach (VaultSecret secret in secrets)
        {
            await vaultService.DeleteSecretAsync(secret.Id, ct);
        }

        db.OpenStackConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  Storage Bindings
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Binds a storage link to an app deployment. This tells the platform that
    /// the deployment's workload needs access to this storage — so the credentials
    /// should be projected as a Kubernetes Secret in the deployment's namespace.
    ///
    /// When the binding is created, any existing vault secrets for the storage link
    /// are configured for K8s sync (setting the target secret name and namespace).
    /// </summary>
    public async Task<StorageBinding> BindStorageToDeploymentAsync(
        Guid storageLinkId, Guid deploymentId, string kubernetesSecretName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Create the binding record linking storage → deployment.

        StorageBinding binding = new()
        {
            Id = Guid.NewGuid(),
            StorageLinkId = storageLinkId,
            AppDeploymentId = deploymentId,
            KubernetesSecretName = kubernetesSecretName
        };

        db.Set<StorageBinding>().Add(binding);
        await db.SaveChangesAsync(ct);

        // Configure the storage link's vault secrets for K8s sync.
        // We need the deployment's namespace to know where the K8s Secret goes.

        AppDeployment? deployment = await db.AppDeployments.FindAsync([deploymentId], ct);

        if (deployment is not null)
        {
            StorageLink? link = await db.StorageLinks.FindAsync([storageLinkId], ct);

            if (link is not null)
            {
                await ConfigureSecretsForSyncAsync(
                    link.TenantId, storageLinkId, kubernetesSecretName, deployment.Namespace, ct);
            }
        }

        return binding;
    }

    /// <summary>
    /// Binds a storage link to a cluster component. The credentials will be
    /// projected as a Kubernetes Secret in the component's namespace.
    /// </summary>
    public async Task<StorageBinding> BindStorageToComponentAsync(
        Guid storageLinkId, Guid componentId, string kubernetesSecretName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Create the binding record linking storage → component.

        StorageBinding binding = new()
        {
            Id = Guid.NewGuid(),
            StorageLinkId = storageLinkId,
            ComponentId = componentId,
            KubernetesSecretName = kubernetesSecretName
        };

        db.Set<StorageBinding>().Add(binding);
        await db.SaveChangesAsync(ct);

        // Configure vault secrets for K8s sync using the component's namespace.

        ClusterComponent? component = await db.ClusterComponents.FindAsync([componentId], ct);

        if (component is not null && component.Namespace is not null)
        {
            StorageLink? link = await db.StorageLinks.FindAsync([storageLinkId], ct);

            if (link is not null)
            {
                await ConfigureSecretsForSyncAsync(
                    link.TenantId, storageLinkId, kubernetesSecretName, component.Namespace, ct);
            }
        }

        return binding;
    }

    /// <summary>
    /// Returns all storage bindings for a deployment, including the linked StorageLink
    /// so the UI can display which storage providers are connected.
    /// </summary>
    public async Task<List<StorageBinding>> GetBindingsForDeploymentAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<StorageBinding>()
            .Include(b => b.StorageLink)
            .Where(b => b.AppDeploymentId == deploymentId)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all storage bindings for a cluster component.
    /// </summary>
    public async Task<List<StorageBinding>> GetBindingsForComponentAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<StorageBinding>()
            .Include(b => b.StorageLink)
            .Where(b => b.ComponentId == componentId)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Removes a storage binding. When unbound, the vault secrets for the storage
    /// link have their K8s sync disabled — the platform will no longer project
    /// credentials into the workload's namespace.
    /// </summary>
    public async Task<bool> UnbindStorageAsync(Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StorageBinding? binding = await db.Set<StorageBinding>()
            .Include(b => b.StorageLink)
            .FirstOrDefaultAsync(b => b.Id == bindingId, ct);

        if (binding is null)
        {
            return false;
        }

        // Disable K8s sync on the storage link's secrets since this binding is going away.

        await DisableSecretsForSyncAsync(binding.StorageLink.TenantId, binding.StorageLinkId, ct);

        db.Set<StorageBinding>().Remove(binding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ──────── Internal ────────

    /// <summary>
    /// Marks all vault secrets for a storage link to sync to K8s with the
    /// given secret name and namespace.
    /// </summary>
    /// <summary>
    /// Pushes storage credentials for a deployment binding into its Kubernetes namespace.
    /// Writes ACCESS_KEY, SECRET_KEY and the static metadata (endpoint, bucket, region)
    /// into a single K8s Secret named by <see cref="StorageBinding.KubernetesSecretName"/>.
    /// </summary>
    public async Task SyncStorageBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StorageBinding binding = await db.Set<StorageBinding>()
            .Include(b => b.StorageLink)
            .Include(b => b.AppDeployment!).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.AppDeploymentId.HasValue, ct)
            ?? throw new InvalidOperationException("Storage binding not found.");

        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(
            tenantId, binding.StorageLinkId, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(
            tenantId, binding.StorageLinkId, "SECRET_KEY", ct);

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Storage credentials not found in vault.");

        StorageLink link = binding.StorageLink;
        AppDeployment deployment = binding.AppDeployment!;
        string kubeconfig = deployment.Cluster.Kubeconfig!;
        string ns = deployment.Namespace;

        await k8sFactory.EnsureNamespaceAsync(ns, kubeconfig, ct);

        string secretManifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {binding.KubernetesSecretName}
              namespace: {ns}
              labels:
                app.kubernetes.io/managed-by: entkube
                entkube.io/managed: "true"
            type: Opaque
            data:
              STORAGE_ACCESS_KEY: {B64(accessKey)}
              STORAGE_SECRET_KEY: {B64(secretKey)}
              STORAGE_ENDPOINT: {B64(link.Endpoint ?? "")}
              STORAGE_BUCKET: {B64(link.BucketName ?? "")}
              STORAGE_REGION: {B64(link.Region ?? "")}
            """;

        await k8sFactory.ApplyManifestAsync(secretManifest, kubeconfig, ct);

        binding.LastSyncedAt = DateTime.UtcNow;
        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.Set<StorageBinding>().Update(binding);
        await db2.SaveChangesAsync(ct);
    }

    private static string B64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private async Task ConfigureSecretsForSyncAsync(
        Guid tenantId, Guid storageLinkId, string secretName, string ns, CancellationToken ct)
    {
        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenantId, storageLinkId, ct);

        foreach (VaultSecret secret in secrets)
        {
            await vaultService.ConfigureKubernetesSyncAsync(secret.Id, true, secretName, ns, ct);
        }
    }

    /// <summary>
    /// Disables K8s sync on all vault secrets for a storage link.
    /// </summary>
    private async Task DisableSecretsForSyncAsync(
        Guid tenantId, Guid storageLinkId, CancellationToken ct)
    {
        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenantId, storageLinkId, ct);

        foreach (VaultSecret secret in secrets)
        {
            await vaultService.ConfigureKubernetesSyncAsync(secret.Id, false, null, null, ct);
        }
    }

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }
}
