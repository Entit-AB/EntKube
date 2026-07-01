using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class TempoConfig
{
    public string Namespace { get; set; } = "monitoring";
    public string ServiceName { get; set; } = "tempo";
    public Guid? StorageLinkId { get; set; }
}

/// <summary>
/// Handles S3 object-storage configuration for the Grafana Tempo component.
/// Mirrors <see cref="LokiService"/>'s WriteStorageHelmValuesAsync: non-sensitive
/// S3 values (endpoint, bucket, region) are written straight into HelmValues,
/// credentials are stored as vault secrets and injected at install time via the
/// hidden catalog fields.
///
/// The tempo-distributed chart renders <c>storage.trace</c> into Tempo's config.
/// Selecting a storage link flips <c>storage.trace.backend</c> to s3 and points it
/// at the external bucket, which all Tempo components then share.
/// </summary>
public class TempoService(
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
            ["storage.trace.backend"] = "s3",
            ["storage.trace.s3.bucket"] = bucket,
            ["storage.trace.s3.endpoint"] = endpointHost,
            ["storage.trace.s3.region"] = region,
            // insecure enables plain HTTP; forcepathstyle is required by MinIO and
            // other S3-compatible endpoints that don't support virtual-host buckets.
            ["storage.trace.s3.insecure"] = insecure ? "true" : "false",
            ["storage.trace.s3.forcepathstyle"] = "true",
        };

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", s3Values);

        // Persist the storage link ID so the edit form can re-populate the dropdown.
        TempoConfig storedConfig = TryDeserializeConfig(component.Configuration) ?? new TempoConfig();
        storedConfig.StorageLinkId = storageLinkId;
        component.Configuration = JsonSerializer.Serialize(storedConfig);

        await db.SaveChangesAsync(ct);

        // Store credentials as vault secrets — injected at install time via the hidden
        // catalog fields tempo-s3-access-key / tempo-s3-secret-key.
        await vaultService.InitializeVaultAsync(tenantId, ct);
        (string accessKey, string secretKey) = await storageService.GetStoredCredentialsInternalAsync(tenantId, storageLinkId, ct);

        if (!string.IsNullOrEmpty(accessKey))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, "tempo-s3-access-key", accessKey, ct);
        }

        if (!string.IsNullOrEmpty(secretKey))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, "tempo-s3-secret-key", secretKey, ct);
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

    private static TempoConfig? TryDeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TempoConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }
}
