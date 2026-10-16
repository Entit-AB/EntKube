using System.Text.Json;
using EntKube.Web.Data;
using ICatalogApi = EntKube.Contracts.Catalog.ICatalogApi;
using InstalledComponent = EntKube.Contracts.Catalog.InstalledComponent;
using StorageLinkSummary = EntKube.Contracts.DataServices.StorageLinkSummary;
using ContractComponentStatus = EntKube.Contracts.Catalog.ComponentStatus;
using EntKube.Web.Data.Modules;
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
    IDbContextFactory<FleetDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusters,
    StorageService storageService,
    EntKube.Contracts.DataServices.IDataServicesApi storage,
    VaultService vaultService,
    ICatalogApi catalog,
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
        InstalledComponent component = await catalog.GetComponentAsync(tenantId, clusterComponentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Asked of DataServices, which owns storage links. The same lookup by id and tenant, and
        // the four fields used below — bucket, endpoint, region, name — are all on the summary.
        StorageLinkSummary link = await storage.GetStorageLinkAsync(tenantId, storageLinkId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        if (string.IsNullOrWhiteSpace(link.BucketName))
        {
            // Velero would install and then fail every backup with an unavailable storage
            // location. Refusing here puts the error where the operator can act on it.
            throw new InvalidOperationException(
                $"Storage link “{link.Name}” has no bucket name, so Velero has nowhere to write backups.");
        }

        Dictionary<string, string> values = BuildStorageValues(link);


        VeleroConfig config = TryReadConfig(component.Configuration) ?? new VeleroConfig();
        config.StorageLinkId = storageLinkId;
        await catalog.MergeHelmValuesAsync(tenantId, clusterComponentId, values,
            JsonSerializer.Serialize(config), ct);

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
    /// Decides Velero's backup target at registration time. An explicitly chosen storage link is
    /// wired straight through — and any problem with it is raised, because an operator who picked
    /// a bucket wants to hear that it cannot be used.
    ///
    /// With no link chosen there is nothing to raise: a cluster provisioned from zero has no S3
    /// anywhere yet. So a CubeFS component installed earlier in the same run is used to mint a
    /// bucket and its own object user, and Velero is pointed at that. This path is best-effort by
    /// design — the operator asked for backups, not for the bootstrap to stop when the storage
    /// they never mentioned is missing, so a failure leaves Velero installed and unconfigured.
    /// </summary>
    public async Task ConfigureFromRegistrationAsync(
        Guid tenantId, Guid clusterComponentId, Guid? explicitStorageLinkId, CancellationToken ct = default)
    {
        if (explicitStorageLinkId is Guid chosen && chosen != Guid.Empty)
        {
            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, chosen, ct);
            return;
        }

        try
        {
            // The context this used to open was never queried — the component comes from
            // ICatalogApi. An EF context opens no connection until it is asked something, so it
            // cost nothing and showed up as nothing; only a count of who holds the god context
            // could see it.
            InstalledComponent velero = await catalog.GetComponentAsync(tenantId, clusterComponentId, ct)
                ?? throw new InvalidOperationException("Velero component not found.");
            Guid clusterId = velero.ClusterId;
            Guid environmentId = velero.EnvironmentId;

            Guid? cubefsComponentId = await FindCubeFsComponentOnClusterAsync(tenantId, clusterId, ct);
            if (cubefsComponentId is null)
            {
                logger.LogInformation(
                    "Velero component {ComponentId}: no CubeFS component on the cluster, so the backup target is left unconfigured.",
                    clusterComponentId);
                return;
            }

            StorageLink link = await storageService.ProvisionCubeFSBackupTargetAsync(
                tenantId, environmentId, cubefsComponentId.Value, AutoBucketName,
                displayName: "Velero backups (CubeFS)",
                notes: "Auto-provisioned for cluster backups.", ct: ct);

            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, link.Id, ct);

            logger.LogInformation(
                "Velero component {ComponentId} auto-wired to CubeFS bucket {Bucket} (storage link {StorageLinkId})",
                clusterComponentId, AutoBucketName, link.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Velero component {ComponentId}: could not auto-provision a backup target; installing without one.",
                clusterComponentId);
        }
    }

    /// <summary>
    /// Re-applies the configured target's values and credentials before an install or upgrade, so a
    /// rotated key reaches the cluster without anyone re-picking the link. Mirrors Harbor's refresh.
    /// </summary>
    public async Task RefreshHelmValuesIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        Guid? linkId = await GetStorageLinkIdForComponentAsync(tenantId, clusterComponentId, ct);
        if (linkId is Guid id && id != Guid.Empty)
        {
            await WriteStorageHelmValuesAsync(tenantId, clusterComponentId, id, ct);
        }
    }

    /// <summary>The storage link this component was last pointed at, or null if it has none.</summary>
    public async Task<Guid?> GetStorageLinkIdForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        // Likewise dead: nothing here queries a context.
        InstalledComponent? component = await catalog.GetComponentAsync(tenantId, clusterComponentId, ct);
        return component is null ? null : TryReadConfig(component.Configuration)?.StorageLinkId;
    }

    /// <summary>Bucket minted for a cluster that has no S3 of its own yet.</summary>
    private const string AutoBucketName = "velero-backups";

    /// <param name="tenantId">
    /// Whose cluster. This read had no tenant at all, so a cluster id from anywhere in the
    /// installation answered with that cluster's CubeFS component — which then became the backup
    /// target written into a Velero config. The caller has always had a tenant.
    /// </param>
    private async Task<Guid?> FindCubeFsComponentOnClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        // ⚠️ This read was exempted from the contract, in a comment that justified itself: the
        // query took the most recently CREATED match and InstalledComponent carries no CreatedAt,
        // so "the contract cannot express it and converting it would silently change which
        // component is picked". Every sentence of that was true, and the conclusion was still
        // wrong, because nobody checked the schema: ClusterComponents has
        // HasIndex(c => new { c.ClusterId, c.Name }).IsUnique(), present with unique: true in all
        // three providers' migrations since the table was created. A cluster can hold at most ONE
        // component named cubefs, so the OrderByDescending had nothing to order — it could never
        // choose between two rows, and the missing CreatedAt could never matter.
        //
        // The lesson is about exemptions, not about CubeFS. An exemption is a claim that
        // something is impossible, and it then sits in the code justifying itself. Before writing
        // one, open the caller (#189) — and open the schema.
        IReadOnlyList<InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct);

        return components
            .FirstOrDefault(c => c.Name == "cubefs"
                              && c.Status == ContractComponentStatus.Installed)
            ?.Id;
    }

    /// <summary>
    /// Maps a storage link onto Velero's backup storage location. Pure and public so the
    /// mapping can be checked without a database — an endpoint written into the wrong key
    /// produces a component that installs cleanly and cannot back anything up.
    /// </summary>
    public static Dictionary<string, string> BuildStorageValues(StorageLinkSummary link)
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
        // The CONTRACT's enum now, not the entity's. The two mirror member for member and
        // ModuleApiTests pins the ordinals, because DataServicesApi casts between them — so a
        // member inserted in one and not the other would flip path-style addressing for every
        // provider after it, and DNS failures against a bucket-prefixed hostname are the symptom.
        values["configuration.backupStorageLocation.0.config.s3ForcePathStyle"] =
            link.Provider == EntKube.Contracts.DataServices.StorageProviderKind.AwsS3 ? "false" : "true";

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
    private async Task<ClusterDrStatus> GetStatusAsync(
        Guid tenantId, Guid clusterId, string clusterName, CancellationToken ct = default)
    {
        // The name is still a parameter: when the seam refuses there is no cluster to read it
        // from, and a fleet report that lists an unreachable cluster without naming it is useless.
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusters.ForAsync(tenantId, clusterId, ct);

        if (cluster is null)
        {
            return new ClusterDrStatus
            {
                ClusterId = clusterId,
                ClusterName = clusterName,
                IsVeleroInstalled = false,
                // ForAsync returns null for "no stored kubeconfig" and for "not this tenant's"
                // alike, and the report must not distinguish them: saying which one it was tells
                // the asker whether a cluster id they guessed exists somewhere else.
                Error = "Cluster is not reachable: no stored kubeconfig, or not this tenant's.",
            };
        }

        try
        {
            string backupsJson = await cluster.GetJsonAsync("backups.velero.io", DefaultNamespace, "", ct);

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
                    await cluster.GetJsonAsync("schedules.velero.io", DefaultNamespace, "", ct)),
                Restores = ParseRestores(
                    await cluster.GetJsonAsync("restores.velero.io", DefaultNamespace, "", ct)),
                StorageLocations = ParseStorageLocations(
                    await cluster.GetJsonAsync("backupstoragelocations.velero.io", DefaultNamespace, "", ct)),
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
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.KubernetesClusters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        List<ClusterDrStatus> statuses = [];
        foreach (var cluster in rows)
        {
            statuses.Add(await GetStatusAsync(tenantId, cluster.Id, cluster.Name, ct));
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
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusters.ForAsync(tenantId, clusterId, ct);
        if (cluster is null)
        {
            return (false, "Cluster not found in this tenant, or it has no kubeconfig.");
        }

        try
        {
            await cluster.ApplyManifestAsync(
                BuildScheduleManifest(name, cron, retentionDays, includedNamespaces, snapshotVolumes, storageLocation),
                ct);

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
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusters.ForAsync(tenantId, clusterId, ct);
        if (cluster is null)
        {
            return (false, "Cluster not found in this tenant, or it has no kubeconfig.");
        }

        try
        {
            await cluster.DeleteManifestAsync("schedule.velero.io", name, DefaultNamespace, ct);
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
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusters.ForAsync(tenantId, clusterId, ct);
        if (cluster is null)
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
            await cluster.ApplyManifestAsync(yaml.ToString(), ct);
            return (true, $"Backup '{name}' started.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
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
