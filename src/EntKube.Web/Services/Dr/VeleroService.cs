using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Dr;

/// <summary>
/// Reads and drives Velero on a registered cluster.
///
/// Velero's own custom resources are the source of truth — EntKube keeps no parallel
/// record of what has been backed up. A second copy would drift from the cluster the
/// moment a backup expired or someone ran `velero backup create` by hand, and a DR
/// feature that lies about what is restorable is worse than none at all.
/// </summary>
public class VeleroService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    StorageService storageService,
    VaultService vaultService,
    ILogger<VeleroService> logger)
{
    /// <summary>
    /// Persisted on the component so the storage picker can be re-populated when the
    /// component is edited, rather than showing an empty dropdown for something that is
    /// already configured.
    /// </summary>
    public sealed class VeleroConfig
    {
        public Guid? StorageLinkId { get; set; }
    }

    /// <summary>
    /// Points Velero at a registered storage link: writes the bucket, region and endpoint
    /// into the component's Helm values and puts the bucket credentials in the vault.
    ///
    /// Nothing about the bucket is typed in by hand. Retyping an endpoint and a pair of
    /// keys into Helm values means they live in two places that can disagree, and a
    /// rotated key then has to be found and edited in every component that copied it.
    /// Going through the storage link means the credentials have exactly one home, and
    /// re-applying the component picks up a rotation.
    ///
    /// Velero reads its cloud credentials from a single INI file rather than from separate
    /// values, so the two keys are composed into one blob and stored as one vault secret,
    /// injected at install time through the hidden velero-s3-credentials catalog field.
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

        if (string.IsNullOrWhiteSpace(link.BucketName))
        {
            // Velero would install and then fail every backup with an unavailable storage
            // location. Refusing here puts the error where the operator can act on it.
            throw new InvalidOperationException(
                $"Storage link “{link.Name}” has no bucket name, so Velero has nowhere to write backups.");
        }

        Dictionary<string, string> values = BuildStorageValues(link);
        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", values);

        VeleroConfig config = TryReadConfig(component.Configuration) ?? new VeleroConfig();
        config.StorageLinkId = storageLinkId;
        component.Configuration = JsonSerializer.Serialize(config);

        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);
        (string accessKey, string secretKey) =
            await storageService.GetStoredCredentialsInternalAsync(tenantId, storageLinkId, ct);

        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            // A backup location with no credentials is not a partially-working setup — it is
            // one where every backup fails, so say so rather than installing something broken.
            throw new InvalidOperationException(
                $"Storage link “{link.Name}” has no stored credentials. Add them before installing Velero.");
        }

        await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, "velero-s3-credentials",
            BuildCredentialsFile(accessKey, secretKey), ct);

        logger.LogInformation(
            "Velero component {ComponentId} pointed at storage link {StorageLinkId} (bucket {Bucket})",
            clusterComponentId, storageLinkId, link.BucketName);
    }

    /// <summary>
    /// Maps a storage link onto Velero's backup storage location. Pure and public so the
    /// mapping can be checked without a database — an endpoint written into the wrong key
    /// produces a component that installs cleanly and cannot back anything up.
    /// </summary>
    public static Dictionary<string, string> BuildStorageValues(StorageLink link)
    {
        Dictionary<string, string> values = new()
        {
            ["configuration.backupStorageLocation.0.provider"] = "aws",
            ["configuration.backupStorageLocation.0.bucket"] = link.BucketName ?? "",
            // MinIO, Ceph RGW and most S3-compatible stores accept us-east-1 but reject an
            // empty region outright, so a link without one gets the conventional default.
            ["configuration.backupStorageLocation.0.config.region"] = string.IsNullOrWhiteSpace(link.Region)
                ? "us-east-1"
                : link.Region,
        };

        if (!string.IsNullOrWhiteSpace(link.Endpoint))
        {
            values["configuration.backupStorageLocation.0.config.s3Url"] = link.Endpoint;
        }

        // AWS proper serves virtual-hosted-style URLs; everything else needs path style.
        // Getting this backwards yields DNS failures against a bucket-prefixed hostname.
        values["configuration.backupStorageLocation.0.config.s3ForcePathStyle"] =
            link.Provider == StorageProvider.AwsS3 ? "false" : "true";

        return values;
    }

    /// <summary>
    /// Builds the credentials file the Velero AWS plugin expects. It reads an INI profile,
    /// not environment variables, so the whole block is one secret.
    /// </summary>
    public static string BuildCredentialsFile(string accessKey, string secretKey) =>
        $"[default]\naws_access_key_id={accessKey}\naws_secret_access_key={secretKey}\n";

    /// <summary>Reads the stored config, tolerating anything that is not ours.</summary>
    public static VeleroConfig? TryReadConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<VeleroConfig>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Namespace Velero is installed into by the catalog entry.</summary>
    public const string DefaultNamespace = "velero";

    /// <summary>Backups older than this without a successful one are considered stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);

    /// <summary>
    /// Reads the DR posture of one cluster. Never throws for an ordinary problem —
    /// an unreachable cluster or a missing CRD comes back as a status carrying the
    /// reason, so one broken cluster cannot fail a fleet-wide report.
    /// </summary>
    public async Task<ClusterDrStatus> GetStatusAsync(
        Guid clusterId, string clusterName, string? kubeconfig, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kubeconfig))
        {
            return new ClusterDrStatus
            {
                ClusterId = clusterId,
                ClusterName = clusterName,
                IsVeleroInstalled = false,
                Error = "Cluster has no kubeconfig configured.",
            };
        }

        try
        {
            string backupsJson = await k8s.GetJsonAsync("backups.velero.io", DefaultNamespace, kubeconfig, "", ct);

            // An empty or error response here means Velero's CRDs are not present, which is
            // simply "not installed" rather than a failure worth reporting as one.
            if (string.IsNullOrWhiteSpace(backupsJson))
            {
                return new ClusterDrStatus
                {
                    ClusterId = clusterId, ClusterName = clusterName, IsVeleroInstalled = false,
                };
            }

            return new ClusterDrStatus
            {
                ClusterId = clusterId,
                ClusterName = clusterName,
                IsVeleroInstalled = true,
                Backups = ParseBackups(backupsJson),
                Schedules = ParseSchedules(
                    await k8s.GetJsonAsync("schedules.velero.io", DefaultNamespace, kubeconfig, "", ct)),
                Restores = ParseRestores(
                    await k8s.GetJsonAsync("restores.velero.io", DefaultNamespace, kubeconfig, "", ct)),
                StorageLocations = ParseStorageLocations(
                    await k8s.GetJsonAsync("backupstoragelocations.velero.io", DefaultNamespace, kubeconfig, "", ct)),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read Velero state on cluster {ClusterId}", clusterId);
            return new ClusterDrStatus
            {
                ClusterId = clusterId,
                ClusterName = clusterName,
                IsVeleroInstalled = false,
                Error = ex.Message,
            };
        }
    }

    /// <summary>Reads DR status for every cluster in a tenant.</summary>
    public async Task<List<ClusterDrStatus>> GetTenantStatusAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var clusters = await db.KubernetesClusters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Kubeconfig })
            .ToListAsync(ct);

        List<ClusterDrStatus> statuses = [];
        foreach (var cluster in clusters)
        {
            statuses.Add(await GetStatusAsync(cluster.Id, cluster.Name, cluster.Kubeconfig, ct));
        }

        return statuses;
    }

    // ── Parsing. Public and static so the CR shapes can be tested without a cluster. ──

    public static IReadOnlyList<VeleroBackup> ParseBackups(string? json) =>
        ParseItems(json, item =>
        {
            JsonElement status = Child(item, "status");
            JsonElement spec = Child(item, "spec");

            return new VeleroBackup
            {
                Name = Str(Child(item, "metadata"), "name") ?? "",
                Phase = ParsePhase(Str(status, "phase")),
                StartedAt = Date(status, "startTimestamp"),
                CompletedAt = Date(status, "completionTimestamp"),
                ExpiresAt = Date(status, "expiration"),
                CreatedBySchedule = Label(item, "velero.io/schedule-name"),
                Errors = Int(status, "errors"),
                Warnings = Int(status, "warnings"),
                IncludedNamespaces = StrArray(spec, "includedNamespaces"),
                StorageLocation = Str(spec, "storageLocation"),
                FailureReason = Str(status, "failureReason"),
                ValidationErrors = StrArray(status, "validationErrors"),
            };
        });

    public static IReadOnlyList<VeleroSchedule> ParseSchedules(string? json) =>
        ParseItems(json, item =>
        {
            JsonElement spec = Child(item, "spec");
            JsonElement template = Child(spec, "template");

            return new VeleroSchedule
            {
                Name = Str(Child(item, "metadata"), "name") ?? "",
                Cron = Str(spec, "schedule") ?? "",
                IsPaused = Bool(spec, "paused"),
                LastBackupAt = Date(Child(item, "status"), "lastBackup"),
                Ttl = Str(template, "ttl"),
                IncludedNamespaces = StrArray(template, "includedNamespaces"),
                // Velero defaults snapshotVolumes to true when the field is absent, so an
                // absent field must not read as "volumes are not being captured".
                SnapshotVolumes = !HasField(template, "snapshotVolumes") || Bool(template, "snapshotVolumes"),
            };
        });

    public static IReadOnlyList<VeleroRestore> ParseRestores(string? json) =>
        ParseItems(json, item =>
        {
            JsonElement status = Child(item, "status");
            return new VeleroRestore
            {
                Name = Str(Child(item, "metadata"), "name") ?? "",
                BackupName = Str(Child(item, "spec"), "backupName") ?? "",
                Phase = ParsePhase(Str(status, "phase")),
                CompletedAt = Date(status, "completionTimestamp"),
                Errors = Int(status, "errors"),
            };
        });

    public static IReadOnlyList<VeleroStorageLocation> ParseStorageLocations(string? json) =>
        ParseItems(json, item =>
        {
            JsonElement spec = Child(item, "spec");
            JsonElement objectStorage = Child(spec, "objectStorage");

            return new VeleroStorageLocation
            {
                Name = Str(Child(item, "metadata"), "name") ?? "",
                Provider = Str(spec, "provider") ?? "unknown",
                Bucket = Str(objectStorage, "bucket"),
                Phase = Str(Child(item, "status"), "phase") ?? "Unknown",
                LastValidatedAt = Date(Child(item, "status"), "lastValidationTime"),
                IsDefault = Bool(spec, "default"),
            };
        });

    public static VeleroPhase ParsePhase(string? phase) => phase switch
    {
        "Completed" => VeleroPhase.Completed,
        "PartiallyFailed" => VeleroPhase.PartiallyFailed,
        "Failed" => VeleroPhase.Failed,
        "InProgress" => VeleroPhase.InProgress,
        "FailedValidation" => VeleroPhase.FailedValidation,
        "Deleting" => VeleroPhase.Deleting,
        "New" => VeleroPhase.New,
        _ => VeleroPhase.Unknown,
    };

    /// <summary>
    /// Builds a Velero Schedule manifest.
    ///
    /// Exposed and pure so the generated YAML can be checked without a cluster — a
    /// malformed schedule silently stops backing anything up, and nothing would notice
    /// until a restore was needed.
    /// </summary>
    public static string BuildScheduleManifest(
        string name, string cron, int retentionDays,
        IReadOnlyList<string>? includedNamespaces, bool snapshotVolumes, string? storageLocation)
    {
        System.Text.StringBuilder yaml = new();
        yaml.Append("apiVersion: velero.io/v1\n");
        yaml.Append("kind: Schedule\n");
        yaml.Append("metadata:\n");
        yaml.Append($"  name: {name}\n");
        yaml.Append($"  namespace: {DefaultNamespace}\n");
        yaml.Append("  labels:\n");
        // Marked as ours so the UI can tell an EntKube-managed schedule from one created
        // out of band with the velero CLI, and never silently take over the latter.
        yaml.Append("    app.kubernetes.io/managed-by: entkube\n");
        yaml.Append("spec:\n");
        yaml.Append($"  schedule: \"{cron}\"\n");
        yaml.Append("  template:\n");
        yaml.Append($"    ttl: {Math.Max(1, retentionDays) * 24}h0m0s\n");
        yaml.Append($"    snapshotVolumes: {(snapshotVolumes ? "true" : "false")}\n");

        if (!string.IsNullOrWhiteSpace(storageLocation))
        {
            yaml.Append($"    storageLocation: {storageLocation}\n");
        }

        // An omitted includedNamespaces means "everything" to Velero. Emitting an empty
        // list instead would be read as "nothing", producing a schedule that backs up
        // no resources at all while still looking configured.
        List<string> namespaces = [.. (includedNamespaces ?? []).Where(n => !string.IsNullOrWhiteSpace(n))];
        if (namespaces.Count > 0)
        {
            yaml.Append("    includedNamespaces:\n");
            foreach (string ns in namespaces)
            {
                yaml.Append($"      - {ns.Trim()}\n");
            }
        }

        return yaml.ToString();
    }

    /// <summary>Creates or updates a backup schedule on a cluster.</summary>
    public async Task<(bool Success, string Message)> SaveScheduleAsync(
        Guid tenantId, Guid clusterId, string name, string cron, int retentionDays,
        IReadOnlyList<string>? includedNamespaces, bool snapshotVolumes, string? storageLocation,
        CancellationToken ct = default)
    {
        string? kubeconfig = await ResolveKubeconfigAsync(tenantId, clusterId, ct);
        if (kubeconfig is null)
        {
            return (false, "Cluster not found in this tenant, or it has no kubeconfig.");
        }

        try
        {
            await k8s.ApplyManifestAsync(
                BuildScheduleManifest(name, cron, retentionDays, includedNamespaces, snapshotVolumes, storageLocation),
                kubeconfig, ct);

            return (true, $"Schedule '{name}' applied.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string Message)> DeleteScheduleAsync(
        Guid tenantId, Guid clusterId, string name, CancellationToken ct = default)
    {
        string? kubeconfig = await ResolveKubeconfigAsync(tenantId, clusterId, ct);
        if (kubeconfig is null)
        {
            return (false, "Cluster not found in this tenant, or it has no kubeconfig.");
        }

        try
        {
            await k8s.DeleteManifestAsync("schedule.velero.io", name, DefaultNamespace, kubeconfig, ct);
            return (true, $"Schedule '{name}' deleted. Existing backups it created are kept until they expire.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Triggers a one-off backup, outside any schedule.</summary>
    public async Task<(bool Success, string Message)> CreateBackupAsync(
        Guid tenantId, Guid clusterId, string name, IReadOnlyList<string>? includedNamespaces,
        int retentionDays, string? storageLocation, CancellationToken ct = default)
    {
        string? kubeconfig = await ResolveKubeconfigAsync(tenantId, clusterId, ct);
        if (kubeconfig is null)
        {
            return (false, "Cluster not found in this tenant, or it has no kubeconfig.");
        }

        System.Text.StringBuilder yaml = new();
        yaml.Append("apiVersion: velero.io/v1\n");
        yaml.Append("kind: Backup\n");
        yaml.Append("metadata:\n");
        yaml.Append($"  name: {name}\n");
        yaml.Append($"  namespace: {DefaultNamespace}\n");
        yaml.Append("  labels:\n");
        yaml.Append("    app.kubernetes.io/managed-by: entkube\n");
        yaml.Append("spec:\n");
        yaml.Append($"  ttl: {Math.Max(1, retentionDays) * 24}h0m0s\n");
        yaml.Append("  snapshotVolumes: true\n");

        if (!string.IsNullOrWhiteSpace(storageLocation))
        {
            yaml.Append($"  storageLocation: {storageLocation}\n");
        }

        List<string> namespaces = [.. (includedNamespaces ?? []).Where(n => !string.IsNullOrWhiteSpace(n))];
        if (namespaces.Count > 0)
        {
            yaml.Append("  includedNamespaces:\n");
            foreach (string ns in namespaces)
            {
                yaml.Append($"    - {ns.Trim()}\n");
            }
        }

        try
        {
            await k8s.ApplyManifestAsync(yaml.ToString(), kubeconfig, ct);
            return (true, $"Backup '{name}' started.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<string?> ResolveKubeconfigAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Scoped by tenant as well as cluster id, so a crafted id cannot reach another
        // tenant's cluster.
        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        return string.IsNullOrWhiteSpace(cluster?.Kubeconfig) ? null : cluster.Kubeconfig;
    }

    // ── JSON helpers. Deliberately tolerant: a missing field is absent, never an exception. ──

    private static IReadOnlyList<T> ParseItems<T>(string? json, Func<JsonElement, T> map)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. items.EnumerateArray().Select(map)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement child)
            ? child
            : default;

    private static bool HasField(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out _);

    private static string? Str(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static int Int(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int i) ? i : 0;

    private static bool Bool(JsonElement parent, string name) =>
        Child(parent, name).ValueKind == JsonValueKind.True;

    private static DateTime? Date(JsonElement parent, string name) =>
        Str(parent, name) is string s && DateTime.TryParse(
            s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> StrArray(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    private static string? Label(JsonElement item, string label) =>
        Str(Child(Child(item, "metadata"), "labels"), label);
}
