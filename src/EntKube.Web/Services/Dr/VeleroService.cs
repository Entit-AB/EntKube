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
    ILogger<VeleroService> logger)
{
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
