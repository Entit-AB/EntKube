using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>Component config persisted in <see cref="ClusterComponent.Configuration"/> for Velero.</summary>
public class VeleroConfig
{
    /// <summary>The storage link Velero backs up to (auto-provisioned CubeFS bucket or a chosen S3 link).</summary>
    public Guid? StorageLinkId { get; set; }
}

/// <summary>
/// Wires Velero's S3 backup target into its Helm values, reusing the same StorageLink rails as
/// Loki/Mimir/Harbor: non-secret S3 values (bucket, region, endpoint) are merged into HelmValues,
/// and the AWS-plugin credentials file is stored as a component vault secret so
/// <see cref="ComponentLifecycleService.InjectSecretsIntoValuesAsync"/> injects it at install time
/// via the hidden <c>velero-s3-credentials</c> catalog field.
///
/// For a from-zero cluster there is no S3 link yet, so <see cref="ConfigureFromRegistrationAsync"/>
/// auto-provisions one: it finds the CubeFS component installed earlier in the same bootstrap run,
/// mints a CubeFS object user + bucket (<see cref="StorageService.ProvisionCubeFSBackupTargetAsync"/>),
/// and wires Velero to it — the "Velero → CubeFS auto-wiring" path.
/// </summary>
public class VeleroService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<VeleroService> logger,
    VaultService vaultService,
    StorageService storageService)
{
    /// <summary>Vault/catalog secret name carrying the AWS-plugin credentials INI file.</summary>
    public const string CredentialsSecretName = "velero-s3-credentials";

    private const string DefaultBucketPrefix = "velero";

    /// <summary>
    /// Called during component registration. If an explicit storage link was chosen, wires Velero to
    /// it. Otherwise attempts zero-touch auto-wiring against a CubeFS component on the same cluster.
    /// Best-effort: on failure Velero still installs (with its placeholder target) and a warning is
    /// logged, so a missing/broken backup target never fails the whole bootstrap run.
    /// </summary>
    public async Task ConfigureFromRegistrationAsync(
        Guid tenantId, Guid clusterComponentId, Guid? explicitStorageLinkId, CancellationToken ct = default)
    {
        try
        {
            if (explicitStorageLinkId is Guid chosen && chosen != Guid.Empty)
            {
                await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, chosen, ct);
                return;
            }

            Guid clusterId;
            Guid environmentId;
            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                ClusterComponent velero = await db.ClusterComponents
                    .Include(c => c.Cluster)
                    .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
                    ?? throw new InvalidOperationException("Velero component not found.");
                clusterId = velero.ClusterId;
                environmentId = velero.Cluster.EnvironmentId;
            }

            Guid? cubefsComponentId = await FindCubeFsComponentOnClusterAsync(clusterId, ct);
            if (cubefsComponentId is null)
            {
                logger.LogInformation(
                    "Velero {Component}: no CubeFS component on the cluster — leaving the backup target unconfigured.",
                    clusterComponentId);
                return;
            }

            string bucket = $"{DefaultBucketPrefix}-backups";
            StorageLink link = await storageService.ProvisionCubeFSBackupTargetAsync(
                tenantId, environmentId, cubefsComponentId.Value, bucket,
                displayName: "Velero backups (CubeFS)", notes: "Auto-provisioned for cluster backups.", ct: ct);

            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, link.Id, ct);
            logger.LogInformation(
                "Velero {Component}: auto-wired to CubeFS bucket '{Bucket}' (link {Link}).",
                clusterComponentId, bucket, link.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Velero {Component}: could not auto-wire a backup target; Velero will install without one.",
                clusterComponentId);
        }
    }

    /// <summary>Re-applies the configured backup target's values/credentials before an install/upgrade.</summary>
    public async Task RefreshHelmValuesIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        Guid? linkId = await GetStorageLinkIdForComponentAsync(tenantId, clusterComponentId, ct);
        if (linkId is Guid id && id != Guid.Empty)
        {
            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, id, ct);
        }
    }

    /// <summary>
    /// Merges the storage link's S3 target (bucket/region/endpoint) into the Velero component's Helm
    /// values and stores the AWS-plugin credentials file as a component vault secret for install-time
    /// injection. Persists the link id in the component's Configuration so refresh/UI can re-resolve it.
    /// </summary>
    public async Task WriteStorageHelmValuesAsync(
        Guid tenantId, Guid clusterComponentId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Velero component not found.");

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        string region = link.Region ?? "us-east-1";
        string bucket = link.BucketName ?? "";

        Dictionary<string, string> s3Values = new()
        {
            ["configuration.backupStorageLocation.0.bucket"] = bucket,
            ["configuration.backupStorageLocation.0.config.region"] = region,
            ["configuration.backupStorageLocation.0.config.s3ForcePathStyle"] = "true",
            ["configuration.volumeSnapshotLocation.0.config.region"] = region,
        };
        if (!string.IsNullOrWhiteSpace(link.Endpoint))
        {
            s3Values["configuration.backupStorageLocation.0.config.s3Url"] = link.Endpoint;
        }

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", s3Values);

        VeleroConfig config = TryDeserializeConfig(component.Configuration) ?? new VeleroConfig();
        config.StorageLinkId = storageLinkId;
        component.Configuration = JsonSerializer.Serialize(config);

        await db.SaveChangesAsync(ct);

        // Store the AWS-plugin credentials file as a component secret; injected at install via the
        // hidden velero-s3-credentials field (→ credentials.secretContents.cloud).
        await vaultService.InitializeVaultAsync(tenantId, ct);
        (string accessKey, string secretKey) = await storageService.GetStoredCredentialsInternalAsync(tenantId, storageLinkId, ct);
        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, CredentialsSecretName, BuildCredentialsFile(accessKey, secretKey), ct);
        }
    }

    public async Task<Guid?> GetStorageLinkIdForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct);
        return component is null ? null : TryDeserializeConfig(component.Configuration)?.StorageLinkId;
    }

    /// <summary>The AWS shared-credentials INI the velero-plugin-for-aws reads from its secret.</summary>
    public static string BuildCredentialsFile(string accessKey, string secretKey) =>
        $"[default]\naws_access_key_id={accessKey}\naws_secret_access_key={secretKey}\n";

    private async Task<Guid?> FindCubeFsComponentOnClusterAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? cubefs = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId && c.Name == "cubefs" && c.Status == ComponentStatus.Installed)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return cubefs?.Id;
    }

    private static VeleroConfig? TryDeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<VeleroConfig>(json); }
        catch { return null; }
    }
}
