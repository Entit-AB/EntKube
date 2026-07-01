using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class MimirConfig
{
    public string Namespace { get; set; } = "monitoring";
    public string ServiceName { get; set; } = "mimir";
    public Guid? StorageLinkId { get; set; }
}

/// <summary>
/// Handles S3 object-storage configuration for the Grafana Mimir component.
/// Mirrors <see cref="LokiService"/>'s WriteStorageHelmValuesAsync: non-sensitive
/// S3 values (endpoint, bucket, region) are written straight into HelmValues,
/// credentials are stored as vault secrets and injected at install time via the
/// hidden catalog fields.
///
/// The mimir-distributed chart backs object storage with a bundled MinIO by
/// default. Selecting a storage link disables that MinIO and points Mimir at the
/// external bucket via <c>mimir.structuredConfig.common.storage</c>, which the
/// blocks / ruler / alertmanager stores inherit.
/// </summary>
public class MimirService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    StorageService storageService)
{
    /// <summary>
    /// Injects S3-compatible storage configuration into the component's Helm values
    /// and stores the access/secret key as vault secrets for injection at install time.
    /// </summary>
    public async Task WriteStorageHelmValuesAsync(
        Guid tenantId, Guid clusterComponentId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        string region = link.Region ?? "us-east-1";
        string bucket = link.BucketName ?? "";
        (string endpointHost, bool insecure) = S3EndpointUtil.Normalize(link.Endpoint, region);

        Dictionary<string, string> s3Values = new()
        {
            // Turn off the chart's bundled MinIO — external S3 now backs all object
            // storage. Must render as an *unquoted* YAML boolean: the chart's guard
            // {{- if .Values.minio.enabled }} is a Go-template truthiness check, and
            // a quoted string "false" is non-empty → truthy. YamlFormMerger emits
            // the literal true/false unquoted, so this correctly disables MinIO.
            ["minio.enabled"] = "false",
            // common.storage is inherited by the blocks / ruler / alertmanager stores,
            // so a single block configures the S3 backend for all three.
            ["mimir.structuredConfig.common.storage.backend"] = "s3",
            ["mimir.structuredConfig.common.storage.s3.endpoint"] = endpointHost,
            ["mimir.structuredConfig.common.storage.s3.region"] = region,
            ["mimir.structuredConfig.common.storage.s3.bucket_name"] = bucket,
            ["mimir.structuredConfig.common.storage.s3.insecure"] = insecure ? "true" : "false",
            // Mimir refuses to start when blocks, ruler and alertmanager storage share
            // the same bucket without distinct prefixes. One bucket, three prefixes.
            ["mimir.structuredConfig.blocks_storage.storage_prefix"] = "blocks",
            ["mimir.structuredConfig.ruler_storage.storage_prefix"] = "ruler",
            ["mimir.structuredConfig.alertmanager_storage.storage_prefix"] = "alertmanager",
        };

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", s3Values);

        // Persist the storage link ID so the edit form can re-populate the dropdown.
        MimirConfig storedConfig = TryDeserializeConfig(component.Configuration) ?? new MimirConfig();
        storedConfig.StorageLinkId = storageLinkId;
        component.Configuration = JsonSerializer.Serialize(storedConfig);

        await db.SaveChangesAsync(ct);

        // Store credentials as vault secrets — injected at install time via the hidden
        // catalog fields mimir-s3-access-key / mimir-s3-secret-key.
        await vaultService.InitializeVaultAsync(tenantId, ct);
        (string accessKey, string secretKey) = await storageService.GetStoredCredentialsInternalAsync(tenantId, storageLinkId, ct);

        if (!string.IsNullOrEmpty(accessKey))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, "mimir-s3-access-key", accessKey, ct);
        }

        if (!string.IsNullOrEmpty(secretKey))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, "mimir-s3-secret-key", secretKey, ct);
        }
    }

    public async Task<Guid?> GetStorageLinkIdForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct);
        if (component is null) return null;
        return TryDeserializeConfig(component.Configuration)?.StorageLinkId;
    }

    private static MimirConfig? TryDeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MimirConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }
}
