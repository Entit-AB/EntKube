using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages the full lifecycle of MongoDB Community Operator clusters — from creation
/// through backup, restore, upgrade, database management, and deletion. Each operation
/// translates high-level intent into Kubernetes CRD manifests or Jobs applied to the
/// cluster where the MongoDB Community Operator is running.
///
/// The service owns the MongoCluster, MongoDatabase, and MongoBackup records in the
/// database. It coordinates with VaultService to store database credentials and tag
/// them for Kubernetes sync so applications can consume them as K8s Secrets.
///
/// Backups are implemented as Kubernetes Jobs: an init container runs mongodump and
/// writes to an emptyDir volume, then the main container uploads the archive to S3
/// using the aws-cli image. Restores reverse this process. No PITR — recovery is to
/// the point in time of the selected backup dump.
/// </summary>
public class MongoService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IKubernetesClientFactory k8sFactory)
{
    // ──────── Cluster Lifecycle ────────

    /// <summary>
    /// Creates a new managed MongoDB cluster via the Community Operator. Validates that
    /// the operator is installed on the target cluster, then generates the MongoDBCommunity
    /// CRD manifest and applies it. If a storage link is provided, syncs S3 credentials
    /// to a K8s Secret so backup Jobs can access the bucket.
    /// </summary>
    public async Task<MongoCluster> CreateClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        int members,
        string storageSize,
        Guid? storageLinkId,
        string? backupSchedule,
        int retentionDays = 30,
        int maxBackups = 20,
        string mongoVersion = "8.3.2",
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Verify the MongoDB Community Operator is installed on the target cluster.

        bool operatorInstalled = await db.ClusterComponents
            .AnyAsync(c => c.ClusterId == kubernetesClusterId
                && (c.Name == "mongodb-community-operator" || c.Name == "mongodb-operator"
                    || c.ReleaseName == "mongodb-community-operator" || c.ReleaseName == "mongodb-operator"
                    || c.HelmChartName == "community-operator")
                && c.Status == ComponentStatus.Installed, ct);

        if (!operatorInstalled)
        {
            throw new InvalidOperationException(
                "MongoDB Community Operator is not installed on this cluster. Install it first from the Components tab.");
        }

        // Load the cluster's kubeconfig for applying the manifest.

        KubernetesCluster k8sCluster = await db.KubernetesClusters
            .FirstAsync(k => k.Id == kubernetesClusterId, ct);

        // Load backup storage details if configured.

        StorageLink? storageLink = null;

        if (storageLinkId.HasValue)
        {
            storageLink = await db.StorageLinks
                .FirstOrDefaultAsync(s => s.Id == storageLinkId.Value && s.TenantId == tenantId, ct);
        }

        // Create the database record.

        MongoCluster mongoCluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            MongoVersion = string.IsNullOrWhiteSpace(mongoVersion) ? "8.3.2" : mongoVersion.Trim(),
            Members = members,
            StorageSize = storageSize,
            StorageLinkId = storageLinkId,
            BackupSchedule = backupSchedule,
            RetentionDays = retentionDays,
            MaxBackups = maxBackups,
            Status = MongoClusterStatus.Creating
        };

        db.MongoClusters.Add(mongoCluster);
        await db.SaveChangesAsync(ct);

        // If backup storage is configured, ensure the S3 credentials are synced
        // to Kubernetes so backup Jobs can access the bucket.

        string? s3SecretName = null;

        if (storageLink is not null)
        {
            s3SecretName = $"{name}-s3-credentials";
        }

        // Generate an admin password. Store it in the vault (encrypted, linked to this
        // cluster, with K8s sync enabled) and also apply the K8s Secret immediately so
        // the Community Operator can find it before it processes the MongoDBCommunity CRD.

        string adminPassword = GeneratePassword();
        string adminK8sSecretName = $"{name}-admin-password";

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetMongoClusterSecretAsync(
            tenantId, mongoCluster.Id, "ADMIN_PASSWORD", adminPassword,
            adminK8sSecretName, ns, ct);

        string adminSecretManifest = BuildAdminSecretManifest(name, ns, adminPassword);
        string clusterManifest = BuildClusterManifest(mongoCluster);

        await k8sFactory.EnsureNamespaceAsync(ns, k8sCluster.Kubeconfig!, ct);

        // The Community Operator sets serviceAccountName: mongodb-database on every pod it creates.
        // It only creates this ServiceAccount in its own installation namespace, so when the MongoDB
        // resource lives in a different namespace the pod admission fails with "serviceaccount not found".
        // We apply the ServiceAccount ourselves so it always exists in the target namespace.
        await k8sFactory.ApplyManifestAsync(BuildServiceAccountManifest(ns), k8sCluster.Kubeconfig!, ct);

        if (storageLink is not null && s3SecretName is not null)
        {
            await EnsureStorageSecretsInK8sAsync(
                tenantId, storageLink, s3SecretName, ns, k8sCluster.Kubeconfig!, ct);
        }

        await k8sFactory.ApplyManifestAsync(adminSecretManifest, k8sCluster.Kubeconfig!, ct);
        await k8sFactory.ApplyManifestAsync(clusterManifest, k8sCluster.Kubeconfig!, ct);

        if (!string.IsNullOrEmpty(backupSchedule) && storageLink is not null && s3SecretName is not null)
        {
            string cronManifest = BuildScheduledBackupCronJobManifest(mongoCluster, storageLink, s3SecretName);
            await k8sFactory.ApplyManifestAsync(cronManifest, k8sCluster.Kubeconfig!, ct);
        }

        return mongoCluster;
    }

    /// <summary>
    /// Removes an externally-registered MongoDB cluster from EntKube without touching
    /// any Kubernetes resources. Use when the cluster was registered (not created) and
    /// the user wants to remove it from EntKube only.
    /// </summary>
    public async Task UnregisterClusterAsync(Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        foreach (MongoDatabase database in mongo.Databases)
            await DeleteDatabaseSecretsAsync(database.Id, ct);

        await DeleteClusterSecretsAsync(mongo.Id, ct);

        db.MongoClusters.Remove(mongo);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a managed MongoDB cluster from Kubernetes and removes all associated
    /// records (databases, backups, vault secrets).
    /// </summary>
    public async Task DeleteClusterAsync(Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        // Attempt to delete the K8s resources. If the cluster never actually deployed
        // or the K8s API is unreachable, we still proceed with removing the DB record.

        try
        {
            await k8sFactory.DeleteManifestAsync(
                "mongodbcommunity.mongodbcommunity.mongodb.com", mongo.Name, mongo.Namespace,
                mongo.KubernetesCluster.Kubeconfig!, ct);
            await k8sFactory.DeleteManifestAsync(
                "secret", $"{mongo.Name}-admin-password", mongo.Namespace,
                mongo.KubernetesCluster.Kubeconfig!, ct);
            if (!string.IsNullOrEmpty(mongo.BackupSchedule))
            {
                await k8sFactory.DeleteManifestAsync(
                    "cronjob", $"{mongo.Name}-scheduled-backup", mongo.Namespace,
                    mongo.KubernetesCluster.Kubeconfig!, ct);
            }
        }
        catch (Exception)
        {
            // K8s operations failed — proceed with local cleanup regardless.
        }

        // Remove vault secrets for all databases in this cluster.

        foreach (MongoDatabase database in mongo.Databases)
        {
            await DeleteDatabaseSecretsAsync(database.Id, ct);
        }

        // Remove cluster-level vault secrets (admin password, etc.).

        await DeleteClusterSecretsAsync(mongo.Id, ct);

        // Remove the cluster record (cascades to databases and backups).

        db.MongoClusters.Remove(mongo);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Updates backup settings (schedule, retention, max backup count) for an existing cluster.
    /// Saves the values to the database and re-applies or removes the scheduled backup CronJob.
    /// </summary>
    public async Task UpdateBackupSettingsAsync(
        Guid tenantId, Guid mongoClusterId,
        string? schedule, int retentionDays, int maxBackups,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        mongo.BackupSchedule = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();
        mongo.RetentionDays = retentionDays;
        mongo.MaxBackups = maxBackups > 0 ? maxBackups : 20;
        await db.SaveChangesAsync(ct);

        string? s3SecretName = mongo.StorageLinkId.HasValue ? $"{mongo.Name}-s3-credentials" : null;

        if (!string.IsNullOrWhiteSpace(mongo.BackupSchedule) && mongo.StorageLink is not null && s3SecretName is not null)
        {
            string cronManifest = BuildScheduledBackupCronJobManifest(mongo, mongo.StorageLink, s3SecretName);
            await k8sFactory.ApplyManifestAsync(cronManifest, mongo.KubernetesCluster.Kubeconfig!, ct);
        }
        else if (mongo.StorageLink is not null)
        {
            try
            {
                await k8sFactory.DeleteManifestAsync(
                    "cronjob", $"{mongo.Name}-scheduled-backup", mongo.Namespace,
                    mongo.KubernetesCluster.Kubeconfig!, ct);
            }
            catch (Exception)
            {
                // CronJob may not exist yet — ignore.
            }
        }
    }

    /// <summary>
    /// Removes the oldest completed backup Jobs from Kubernetes beyond the MaxBackups limit.
    /// Does not affect backup data in S3 — use S3 lifecycle rules for that.
    /// </summary>
    public async Task<int> CleanupOldBackupsAsync(Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        int limit = mongo.MaxBackups > 0 ? mongo.MaxBackups : 20;

        List<MongoBackup> backups = await FetchMongoBackupsFromK8sAsync(mongo, ct);

        List<MongoBackup> completed = backups
            .Where(b => b.Status == MongoBackupStatus.Completed)
            .OrderByDescending(b => b.StartedAt)
            .ToList();

        if (completed.Count <= limit)
            return 0;

        List<MongoBackup> toDelete = completed.Skip(limit).ToList();
        int deleted = 0;

        foreach (MongoBackup backup in toDelete)
        {
            try
            {
                await k8sFactory.DeleteManifestAsync("Job", backup.Name, mongo.Namespace, mongo.KubernetesCluster.Kubeconfig!, ct);

                MongoBackup? dbRecord = await db.MongoBackups
                    .FirstOrDefaultAsync(b => b.MongoClusterId == mongoClusterId && b.Name == backup.Name, ct);
                if (dbRecord is not null)
                    db.MongoBackups.Remove(dbRecord);

                deleted++;
            }
            catch (Exception)
            {
                // Continue with remaining backups if one deletion fails.
            }
        }

        if (deleted > 0)
            await db.SaveChangesAsync(ct);

        return deleted;
    }

    /// <summary>
    /// Upgrades a MongoDB cluster to a new version. Updates spec.version in the CRD —
    /// the Community Operator performs a rolling upgrade of replica set members.
    /// </summary>
    public async Task UpgradeClusterAsync(
        Guid tenantId, Guid mongoClusterId, string targetVersion, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        mongo.MongoVersion = targetVersion;
        mongo.Status = MongoClusterStatus.Upgrading;
        await db.SaveChangesAsync(ct);

        if (mongo.IsExternal)
        {
            // External clusters: patch only spec.version to avoid clobbering existing spec.users and other config.
            string versionFull = targetVersion.Count(c => c == '.') < 2
                ? targetVersion + ".0"
                : targetVersion;

            string jsonPatch = $"{{\"spec\":{{\"version\":\"{versionFull}\"}}}}";
            await k8sFactory.PatchJsonAsync(
                "mongodbcommunity", mongo.Name, mongo.Namespace,
                jsonPatch, mongo.KubernetesCluster.Kubeconfig!, ct);
        }
        else
        {
            string? s3SecretName = mongo.StorageLinkId.HasValue ? $"{mongo.Name}-s3-credentials" : null;
            string manifest = BuildClusterManifest(mongo);

            await k8sFactory.ApplyManifestAsync(manifest, mongo.KubernetesCluster.Kubeconfig!, ct);

            // Re-apply the CronJob so the mongodump image version stays in sync.
            if (!string.IsNullOrEmpty(mongo.BackupSchedule) && mongo.StorageLink is not null && s3SecretName is not null)
            {
                string cronManifest = BuildScheduledBackupCronJobManifest(mongo, mongo.StorageLink, s3SecretName);
                await k8sFactory.ApplyManifestAsync(cronManifest, mongo.KubernetesCluster.Kubeconfig!, ct);
            }
        }
    }

    /// <summary>
    /// Triggers a rolling restart of a MongoDB cluster by bumping a pod-template annotation on
    /// the MongoDBCommunity CR. The Community Operator merges spec.statefulSet into the
    /// generated StatefulSet, so the changed template drives a StatefulSet rolling update —
    /// letting the scheduler re-place members according to the cluster's anti-affinity.
    /// </summary>
    public async Task RestartClusterAsync(
        Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        // Bump a pod-template annotation on the MongoDBCommunity CR. The Community Operator
        // merges spec.statefulSet into the generated StatefulSet, so changing the pod template
        // triggers a StatefulSet rolling update — recreating members one at a time and letting
        // the scheduler re-place them according to the cluster's anti-affinity.
        // Non-interpolated raw string (the trailing brace run is too long for $-interpolation),
        // with the timestamp substituted in afterwards.
        string patch = """
            {"spec":{"statefulSet":{"spec":{"template":{"metadata":{"annotations":{"kubectl.kubernetes.io/restartedAt":"__TS__"}}}}}}}
            """.Replace("__TS__", DateTime.UtcNow.ToString("O"));

        await k8sFactory.PatchJsonAsync(
            "mongodbcommunity.mongodbcommunity.mongodb.com", mongo.Name, mongo.Namespace, patch,
            mongo.KubernetesCluster.Kubeconfig!, ct);
    }

    /// <summary>
    /// Resizes a running MongoDB cluster. Two independent operations can be triggered:
    ///
    /// CPU/Memory — updates the DB record and re-applies the MongoDBCommunity manifest.
    /// The Community Operator propagates the change to the StatefulSet pod template and
    /// performs a rolling restart, so the cluster stays available throughout.
    ///
    /// Storage — patches each PVC directly via the Kubernetes API (bypasses the immutable
    /// StatefulSet volumeClaimTemplate). Requires the StorageClass to have
    /// allowVolumeExpansion: true. The kubelet expands the filesystem without restarting
    /// the pod when online expansion is supported; otherwise, a pod restart is required.
    /// StorageSize on the DB record is updated on success.
    ///
    /// Either set of changes can be applied independently or together.
    /// </summary>
    public async Task ResizeClusterAsync(
        Guid tenantId,
        Guid mongoClusterId,
        string? storageSize,
        string? cpuRequest,
        string? cpuLimit,
        string? memoryRequest,
        string? memoryLimit,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        bool resourcesChanged = cpuRequest  != mongo.CpuRequest
            || cpuLimit    != mongo.CpuLimit
            || memoryRequest != mongo.MemoryRequest
            || memoryLimit   != mongo.MemoryLimit;

        bool storageChanged = !string.IsNullOrWhiteSpace(storageSize)
            && storageSize.Trim() != mongo.StorageSize;

        // ── CPU / Memory ──────────────────────────────────────────────────────────
        if (resourcesChanged)
        {
            mongo.CpuRequest    = string.IsNullOrWhiteSpace(cpuRequest)    ? null : cpuRequest.Trim();
            mongo.CpuLimit      = string.IsNullOrWhiteSpace(cpuLimit)      ? null : cpuLimit.Trim();
            mongo.MemoryRequest = string.IsNullOrWhiteSpace(memoryRequest) ? null : memoryRequest.Trim();
            mongo.MemoryLimit   = string.IsNullOrWhiteSpace(memoryLimit)   ? null : memoryLimit.Trim();
            await db.SaveChangesAsync(ct);

            // Re-apply the full manifest — the operator performs a rolling restart to
            // pick up the new resource requests/limits on the StatefulSet pod template.
            string manifest = BuildClusterManifest(mongo);
            await k8sFactory.ApplyManifestAsync(manifest, mongo.KubernetesCluster.Kubeconfig!, ct);
        }

        // ── Storage ───────────────────────────────────────────────────────────────
        if (storageChanged)
        {
            string newSize = storageSize!.Trim();

            // Patch each PVC individually — StatefulSet volumeClaimTemplates are immutable
            // so we cannot go through the CRD. The kubelet expands the filesystem in-place
            // when the StorageClass supports online expansion.
            string patch = $"{{\"spec\":{{\"resources\":{{\"requests\":{{\"storage\":\"{newSize}\"}}}}}}}}";

            for (int i = 0; i < mongo.Members; i++)
            {
                string pvcName = $"data-volume-{mongo.Name}-{i}";
                await k8sFactory.PatchJsonAsync(
                    "pvc", pvcName, mongo.Namespace, patch,
                    mongo.KubernetesCluster.Kubeconfig!, ct);
            }

            mongo.StorageSize = newSize;
            await db.SaveChangesAsync(ct);
        }
    }

    // ──────── Backup & Restore ────────

    /// <summary>
    /// Triggers an on-demand backup by applying a Kubernetes Job that runs mongodump
    /// and uploads the gzip archive to the configured S3 bucket. The backup archive
    /// is stored at {prefix}/{backupName}.archive in the bucket.
    /// </summary>
    public async Task<MongoBackup> BackupAsync(Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        if (!mongo.StorageLinkId.HasValue)
        {
            throw new InvalidOperationException(
                "Backup storage is not configured for this cluster. Assign an S3 bucket first.");
        }

        // Generate a unique backup name with timestamp.

        string backupName = $"{mongo.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

        MongoBackup backup = new()
        {
            Id = Guid.NewGuid(),
            MongoClusterId = mongo.Id,
            Name = backupName,
            Type = MongoBackupType.OnDemand,
            Status = MongoBackupStatus.Running
        };

        db.MongoBackups.Add(backup);
        await db.SaveChangesAsync(ct);

        // Apply a Job that runs mongodump → S3 upload.

        string s3SecretName = $"{mongo.Name}-s3-credentials";
        StorageLink storageLink = await db.StorageLinks.FirstAsync(s => s.Id == mongo.StorageLinkId!.Value, ct);

        await EnsureStorageSecretsInK8sAsync(
            tenantId, storageLink, s3SecretName, mongo.Namespace, mongo.KubernetesCluster.Kubeconfig!, ct);

        // Both managed and external clusters use the same backup manifest: the Job connects
        // to {name}-svc (internal service) using the {name}-admin-password K8s Secret.
        // For external clusters the user stores their admin credentials via UpdateExternalClusterSettingsAsync,
        // which writes that same secret — no external URI required.
        string manifest = BuildBackupManifest(backupName, mongo, storageLink, s3SecretName);
        await k8sFactory.ApplyManifestAsync(manifest, mongo.KubernetesCluster.Kubeconfig!, ct);

        return backup;
    }

    /// <summary>
    /// Restores a MongoDB cluster from a previously taken backup. Creates a Kubernetes
    /// Job that downloads the backup archive from S3 and runs mongorestore --drop against
    /// the replica set. The sourceBackupId must refer to a completed OnDemand or Scheduled
    /// backup whose archive exists in the configured S3 bucket.
    /// </summary>
    public async Task<MongoBackup> RestoreAsync(
        Guid tenantId, Guid mongoClusterId, Guid sourceBackupId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        if (!mongo.StorageLinkId.HasValue)
        {
            throw new InvalidOperationException(
                "Restore requires backup storage configured on this cluster.");
        }

        MongoBackup sourceBackup = await db.MongoBackups
            .FirstOrDefaultAsync(b => b.Id == sourceBackupId && b.MongoClusterId == mongo.Id, ct)
            ?? throw new InvalidOperationException("Backup record not found.");

        string restoreName = $"{mongo.Name}-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

        MongoBackup restoreRecord = new()
        {
            Id = Guid.NewGuid(),
            MongoClusterId = mongo.Id,
            Name = restoreName,
            Type = MongoBackupType.OnDemand,
            Status = MongoBackupStatus.Running
        };

        db.MongoBackups.Add(restoreRecord);
        mongo.Status = MongoClusterStatus.Restoring;
        await db.SaveChangesAsync(ct);

        string s3SecretName = $"{mongo.Name}-s3-credentials";
        StorageLink storageLink = await db.StorageLinks.FirstAsync(s => s.Id == mongo.StorageLinkId!.Value, ct);

        await EnsureStorageSecretsInK8sAsync(
            tenantId, storageLink, s3SecretName, mongo.Namespace, mongo.KubernetesCluster.Kubeconfig!, ct);

        string manifest = BuildRestoreManifest(restoreName, mongo, storageLink, sourceBackup.Name, s3SecretName);
        await k8sFactory.ApplyManifestAsync(manifest, mongo.KubernetesCluster.Kubeconfig!, ct);

        return restoreRecord;
    }

    /// <summary>
    /// Restores a database from a MongoDB Atlas cluster into a managed MongoDB cluster.
    /// Creates a Kubernetes Job with two stages: an init container runs mongodump
    /// against the Atlas connection string, then the main container runs mongorestore
    /// into the target replica set. Atlas credentials are stored as a short-lived
    /// K8s Secret that is cleaned up automatically via ttlSecondsAfterFinished.
    /// </summary>
    public async Task RestoreFromAtlasAsync(
        Guid tenantId,
        Guid mongoClusterId,
        string atlasUri,
        string sourceDatabase,
        string targetDatabase,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        if (string.IsNullOrWhiteSpace(atlasUri))
        {
            throw new ArgumentException("Atlas connection string is required.");
        }

        string jobName = $"{mongo.Name}-atlas-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        string atlasSecretName = $"{jobName}-atlas-uri";

        // Store the Atlas URI in a short-lived K8s Secret so it is not embedded in the Job manifest.
        string atlasSecretManifest = BuildAtlasUriSecret(atlasSecretName, mongo.Namespace, atlasUri);
        await k8sFactory.ApplyManifestAsync(atlasSecretManifest, mongo.KubernetesCluster.Kubeconfig!, ct);

        string manifest = BuildAtlasRestoreManifest(
            jobName, mongo, atlasSecretName, sourceDatabase, targetDatabase);
        await k8sFactory.ApplyManifestAsync(manifest, mongo.KubernetesCluster.Kubeconfig!, ct);
    }

    private static string BuildAtlasUriSecret(string secretName, string ns, string atlasUri)
    {
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(atlasUri));
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        sb.AppendLine($"  uri: {encoded}");
        return sb.ToString();
    }

    private static string BuildAtlasRestoreManifest(
        string jobName, MongoCluster mongo,
        string atlasSecretName, string sourceDatabase, string targetDatabase)
    {
        // The dump container connects to Atlas and must use a modern image: Ubuntu 22.04 + OpenSSL 3.x
        // (mongo 7.0+). Atlas enforces TLS 1.2+ with cipher suites that older OpenSSL stacks
        // cannot negotiate, producing "remote error: tls: internal error" on the server side.
        // mongodump tools are backwards-compatible — a 7.0 client can dump any Atlas cluster version.
        const string atlasDumpImage = "mongo:7.0";

        // The restore container connects to the local in-cluster replica set, so it can match
        // the target cluster's version for maximum protocol compatibility.
        string mongoImage = MongoImage(mongo.MongoVersion);

        string targetHost = $"{mongo.Name}-svc.{mongo.Namespace}.svc.cluster.local";
        // socketTimeoutMS=0 disables per-operation socket timeouts so slow large-collection
        // writes don't cause the driver to RST the connection mid-write.
        // heartbeatFrequencyMS keeps the connection alive through NAT idle-timeout windows.
        string targetUri = $"mongodb://admin:${{MONGO_ADMIN_PASSWORD}}@{targetHost}:27017/?authSource=admin&replicaSet={mongo.Name}&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=10000";

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {jobName}");
        sb.AppendLine($"  namespace: {mongo.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {mongo.Name}");
        sb.AppendLine("    entkube.io/restore-source: atlas");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 0");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: dump-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: dump-from-atlas");
        sb.AppendLine($"          image: {atlasDumpImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongodump --uri=\\\"$ATLAS_URI\\\" --db={sourceDatabase} --numParallelCollections=1 --gzip --archive=/dump/dump.archive\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: ATLAS_URI");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {atlasSecretName}");
        sb.AppendLine("                  key: uri");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: dump-data");
        sb.AppendLine("              mountPath: /dump");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: restore-to-cluster");
        sb.AppendLine($"          image: {mongoImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongorestore --uri=\\\"{targetUri}\\\" --gzip --archive=/dump/dump.archive --nsFrom='{sourceDatabase}.*' --nsTo='{targetDatabase}.*' --drop --numInsertionWorkersPerCollection=1 --batchSize=100\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {mongo.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: dump-data");
        sb.AppendLine("              mountPath: /dump");

        return sb.ToString();
    }

    /// <summary>
    /// Creates a new MongoDB cluster from an existing backup. The new cluster is
    /// provisioned fresh (new admin credentials, RBAC, StatefulSet), then a restore
    /// Job is applied that downloads the backup archive from S3 and runs mongorestore
    /// into the new cluster's replica set.
    /// </summary>
    public async Task<MongoCluster> RestoreToNewClusterAsync(
        Guid tenantId, Guid sourceMongoClusterId, Guid sourceBackupId, string newName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster source = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == sourceMongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Source MongoDB cluster not found.");

        if (!source.StorageLinkId.HasValue)
        {
            throw new InvalidOperationException(
                "Restore requires backup storage configured on the source cluster.");
        }

        MongoBackup sourceBackup = await db.MongoBackups
            .FirstOrDefaultAsync(b => b.Id == sourceBackupId && b.MongoClusterId == source.Id, ct)
            ?? throw new InvalidOperationException("Backup record not found.");

        StorageLink storageLink = await db.StorageLinks
            .FirstAsync(s => s.Id == source.StorageLinkId!.Value, ct);

        MongoCluster newCluster = await CreateClusterAsync(
            tenantId, source.KubernetesClusterId, newName, source.Namespace,
            source.Members, source.StorageSize, source.StorageLinkId,
            source.BackupSchedule, source.RetentionDays, source.MaxBackups,
            source.MongoVersion, ct);

        string restoreName = $"{newName}-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        string s3SecretName = $"{newName}-s3-credentials";

        await EnsureStorageSecretsInK8sAsync(
            tenantId, storageLink, s3SecretName, newCluster.Namespace,
            source.KubernetesCluster.Kubeconfig!, ct);

        // Cross-cluster restore: download the backup from the source cluster's S3 path,
        // restore into the new cluster's service using the new cluster's admin credentials.
        string manifest = BuildCrossClusterRestoreManifest(
            restoreName, newCluster, source.Name, storageLink, sourceBackup.Name, s3SecretName);
        await k8sFactory.ApplyManifestAsync(manifest, source.KubernetesCluster.Kubeconfig!, ct);

        return newCluster;
    }

    /// <summary>
    /// Restores a MongoDB backup to an external MongoDB server via a connection URI.
    /// Creates a Kubernetes Job that downloads the archive from S3 and runs mongorestore
    /// against the provided URI — useful for migrating data to an on-premise or cloud-
    /// hosted MongoDB instance without creating a new Community Operator cluster.
    /// </summary>
    public async Task RestoreToExternalAsync(
        Guid tenantId, Guid mongoClusterId, Guid sourceBackupId, string externalMongoUri,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        if (!mongo.StorageLinkId.HasValue)
        {
            throw new InvalidOperationException(
                "Restore requires backup storage configured on this cluster.");
        }

        MongoBackup sourceBackup = await db.MongoBackups
            .FirstOrDefaultAsync(b => b.Id == sourceBackupId && b.MongoClusterId == mongo.Id, ct)
            ?? throw new InvalidOperationException("Backup record not found.");

        StorageLink storageLink = await db.StorageLinks
            .FirstAsync(s => s.Id == mongo.StorageLinkId!.Value, ct);

        string restoreName = $"{mongo.Name}-ext-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        string s3SecretName = $"{mongo.Name}-s3-credentials";

        await EnsureStorageSecretsInK8sAsync(
            tenantId, storageLink, s3SecretName, mongo.Namespace,
            mongo.KubernetesCluster.Kubeconfig!, ct);

        string manifest = BuildExternalRestoreManifest(
            restoreName, mongo, storageLink, sourceBackup.Name, s3SecretName, externalMongoUri);
        await k8sFactory.ApplyManifestAsync(manifest, mongo.KubernetesCluster.Kubeconfig!, ct);
    }

    // ──────── Database Management ────────

    /// <summary>
    /// Creates a new database and user within a running MongoDB cluster.
    /// Generates a random password, runs createUser via mongosh on the primary pod,
    /// then stores the connection credentials in the vault tagged for Kubernetes sync.
    /// </summary>
    public async Task<MongoDatabase> CreateDatabaseAsync(
        Guid tenantId, Guid mongoClusterId, string databaseName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        string owner = $"{databaseName}_owner";
        string password = GeneratePassword();

        // Create the database record.

        MongoDatabase database = new()
        {
            Id = Guid.NewGuid(),
            MongoClusterId = mongo.Id,
            Name = databaseName,
            Owner = owner,
            Status = MongoDatabaseStatus.Creating
        };

        db.MongoDatabases.Add(database);
        await db.SaveChangesAsync(ct);

        // Read the cluster admin password from the vault.

        string? adminPassword = await vaultService.GetMongoClusterSecretValueAsync(
            tenantId, mongo.Id, "ADMIN_PASSWORD", ct);

        // Execute mongosh command on the primary to create the user with readWrite role.

        string script = $$"""
            try {
              db.getSiblingDB("admin").createUser({
                user: "{{owner}}",
                pwd: "{{password}}",
                roles: [{ role: "readWrite", db: "{{databaseName}}" }]
              });
              print("ENTK_SUCCESS");
              process.exit(0);
            } catch(e) {
              print("ENTK_ERROR: " + e);
              process.exit(1);
            }
            """;

        string createOutput = await k8sFactory.ExecuteMongoWithOutputAsync(
            mongo.Name, mongo.Namespace, script, mongo.KubernetesCluster.Kubeconfig!,
            username: "admin", password: adminPassword, ct);

        if (!createOutput.Contains("ENTK_SUCCESS"))
            throw new InvalidOperationException(
                $"Failed to create MongoDB user '{owner}': {createOutput.Trim()}");

        // Mark as ready.

        database.Status = MongoDatabaseStatus.Ready;
        await db.SaveChangesAsync(ct);

        // Store connection credentials in the vault, tagged for K8s sync.

        string k8sSecretName = $"{mongo.Name}-{databaseName}-credentials";

        // Community Operator creates a headless service named {name}-svc.
        // The replica set name equals the MongoDBCommunity resource name.
        string host = $"{mongo.Name}-svc.{mongo.Namespace}.svc.cluster.local";

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "HOST", host, k8sSecretName, mongo.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PORT", "27017", k8sSecretName, mongo.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "DATABASE", databaseName, k8sSecretName, mongo.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "USERNAME", owner, k8sSecretName, mongo.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PASSWORD", password, k8sSecretName, mongo.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "CONNECTION_STRING",
            $"mongodb://{owner}:{password}@{host}:27017/{databaseName}?replicaSet={mongo.Name}&authSource=admin&authMechanism=SCRAM-SHA-256",
            k8sSecretName, mongo.Namespace, ct);

        return database;
    }

    /// <summary>
    /// Deletes a database user from a MongoDB cluster. Drops the user
    /// via mongosh and removes all associated vault secrets.
    /// </summary>
    public async Task DeleteDatabaseAsync(
        Guid tenantId, Guid mongoClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        MongoDatabase database = await db.MongoDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.MongoClusterId == mongo.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        // Drop the user from MongoDB. We don't drop the database itself because
        // MongoDB doesn't require explicit database creation — it exists implicitly.

        string? adminPassword = await vaultService.GetMongoClusterSecretValueAsync(
            tenantId, mongo.Id, "ADMIN_PASSWORD", ct);

        string script = $$"""
            db.getSiblingDB("admin").dropUser("{{database.Owner}}")
            """;

        await k8sFactory.ExecuteMongoAsync(
            mongo.Name, mongo.Namespace, script, mongo.KubernetesCluster.Kubeconfig!,
            username: "admin", password: adminPassword, ct);

        // Remove vault secrets.

        await DeleteDatabaseSecretsAsync(database.Id, ct);

        // Remove the database record.

        db.MongoDatabases.Remove(database);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a database from EntKube without running any MongoDB commands.
    /// Use when the cluster is external and the database should only be untracked,
    /// not actually dropped from the server.
    /// </summary>
    public async Task UnregisterDatabaseAsync(Guid tenantId, Guid mongoClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        MongoDatabase database = await db.MongoDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.MongoClusterId == mongo.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        await DeleteDatabaseSecretsAsync(database.Id, ct);

        db.MongoDatabases.Remove(database);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Database Migration ────────

    /// <summary>
    /// Migrates a single database from one managed MongoDB cluster to another by running a
    /// two-container Kubernetes Job on the source cluster's K8s environment.
    /// The init container dumps the source database via mongodump --db, the main container
    /// restores it to the target cluster with mongorestore --db.
    ///
    /// Both clusters must be reachable via their internal service endpoints from the K8s
    /// cluster where the Job runs (i.e. both clusters should be on the same K8s cluster,
    /// or the source cluster's K8s must have network access to the target's service).
    /// </summary>
    public async Task MigrateDatabaseAsync(
        Guid tenantId,
        Guid sourceClusterId,
        Guid sourceDatabaseId,
        Guid targetClusterId,
        string targetDatabaseName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster source = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == sourceClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Source MongoDB cluster not found.");

        MongoDatabase sourceDb = await db.MongoDatabases
            .FirstOrDefaultAsync(d => d.Id == sourceDatabaseId && d.MongoClusterId == sourceClusterId, ct)
            ?? throw new InvalidOperationException("Source database not found.");

        MongoCluster target = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == targetClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Target MongoDB cluster not found.");

        if (string.IsNullOrWhiteSpace(targetDatabaseName))
            targetDatabaseName = sourceDb.Name;

        string jobName = $"{sourceDb.Name}-to-{target.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        string manifest = BuildMigrateDatabaseManifest(jobName, source, sourceDb.Name, target, targetDatabaseName.Trim());

        // Run the Job on the source cluster's K8s so the source service is always reachable.
        await k8sFactory.ApplyManifestAsync(manifest, source.KubernetesCluster.Kubeconfig!, ct);
    }

    private static string BuildMigrateDatabaseManifest(
        string jobName, MongoCluster source, string sourceDbName,
        MongoCluster target, string targetDbName)
    {
        string sourceImage = MongoImage(source.MongoVersion);
        string targetImage = MongoImage(target.MongoVersion);

        string sourceHost = $"{source.Name}-svc.{source.Namespace}.svc.cluster.local";
        string sourceUri  = $"mongodb://admin:${{SOURCE_ADMIN_PASSWORD}}@{sourceHost}:27017/?authSource=admin&replicaSet={source.Name}&readPreference=secondaryPreferred&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=5000&serverSelectionTimeoutMS=60000";

        string targetHost = $"{target.Name}-svc.{target.Namespace}.svc.cluster.local";
        string targetUri  = $"mongodb://admin:${{TARGET_ADMIN_PASSWORD}}@{targetHost}:27017/?authSource=admin&replicaSet={target.Name}&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=5000&serverSelectionTimeoutMS=60000";

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {jobName}");
        sb.AppendLine($"  namespace: {source.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {source.Name}");
        sb.AppendLine($"    entkube.io/db-migration-target: {target.Name}");
        // Wait for the source to be ready — pings every 10 s until mongosh succeeds.
        // Catches the case where the cluster is still settling after a rolling upgrade.
        string sourceWaitUri = $"mongodb://admin:${{SOURCE_ADMIN_PASSWORD}}@{sourceHost}:27017/?authSource=admin&replicaSet={source.Name}&connectTimeoutMS=10000&serverSelectionTimeoutMS=15000";

        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 2");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: backup-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: wait-for-source");
        sb.AppendLine($"          image: {sourceImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"until mongosh \\\"{sourceWaitUri}\\\" --username admin --password \\\"${{SOURCE_ADMIN_PASSWORD}}\\\" --authenticationDatabase admin --eval 'db.adminCommand({{ping:1}})' --quiet; do echo 'waiting for source...'; sleep 10; done\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: SOURCE_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {source.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("        - name: dump-source-db");
        sb.AppendLine($"          image: {sourceImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongodump --uri=\\\"{sourceUri}\\\" --db='{sourceDbName}' --numParallelCollections=1 --gzip --archive=/backup/dump.archive\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: SOURCE_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {source.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: restore-target-db");
        sb.AppendLine($"          image: {targetImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongorestore --uri=\\\"{targetUri}\\\" --db='{targetDbName}' --gzip --archive=/backup/dump.archive --drop --nsFrom='{sourceDbName}.*' --nsTo='{targetDbName}.*'\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: TARGET_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {target.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        return sb.ToString();
    }

    // ──────── Database Credentials ────────

    /// <summary>
    /// Returns the decrypted connection credentials for a MongoDB database from the vault.
    /// Keys: HOST, PORT, DATABASE, USERNAME, PASSWORD, CONNECTION_STRING.
    /// </summary>
    public async Task<Dictionary<string, string>> GetDatabaseCredentialsAsync(
        Guid tenantId, Guid databaseId, CancellationToken ct = default)
    {
        List<VaultSecret> secrets = await vaultService.GetMongoDatabaseSecretsAsync(tenantId, databaseId, ct);

        Dictionary<string, string> credentials = [];
        foreach (VaultSecret secret in secrets)
        {
            string? value = await vaultService.GetSecretValueByIdAsync(secret.Id, ct);
            if (value is not null)
                credentials[secret.Name] = value;
        }
        return credentials;
    }

    /// <summary>
    /// Pushes the database's stored credentials to Kubernetes as a Secret in the cluster
    /// namespace. Creates or updates {cluster}-{database}-credentials in the cluster's
    /// namespace so applications can mount it as env vars.
    /// </summary>
    public async Task SyncDatabaseCredentialsToK8sAsync(
        Guid tenantId, Guid mongoClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        MongoDatabase database = await db.MongoDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.MongoClusterId == mongo.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        Dictionary<string, string> credentials = await GetDatabaseCredentialsAsync(tenantId, databaseId, ct);
        if (credentials.Count == 0)
            throw new InvalidOperationException("No credentials found in the vault for this database.");

        string secretName = $"{mongo.Name}-{database.Name}-credentials";
        string kubeconfig = mongo.KubernetesCluster.Kubeconfig!;

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");
        sb.AppendLine($"  namespace: {mongo.Namespace}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("stringData:");
        foreach (KeyValuePair<string, string> kv in credentials)
            sb.AppendLine($"  {kv.Key}: \"{kv.Value.Replace("\"", "\\\"")}\"");

        await k8sFactory.ApplyManifestAsync(sb.ToString(), kubeconfig, ct);

        // Mark vault secrets as synced.
        List<VaultSecret> vaultSecrets = await vaultService.GetMongoDatabaseSecretsAsync(tenantId, databaseId, ct);
        foreach (VaultSecret secret in vaultSecrets)
            await vaultService.ConfigureKubernetesSyncAsync(secret.Id, true, secretName, mongo.Namespace, ct);

        // Propagate to every app deployment bound to this database.
        List<DatabaseBinding> bindings = await db.DatabaseBindings
            .Include(b => b.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(b => b.MongoDatabaseId == databaseId && b.SyncEnabled)
            .ToListAsync(ct);

        foreach (DatabaseBinding binding in bindings)
        {
            string bindingKubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
            string ns = binding.AppDeployment.Namespace;

            StringBuilder bsb = new();
            bsb.AppendLine("apiVersion: v1");
            bsb.AppendLine("kind: Secret");
            bsb.AppendLine("metadata:");
            bsb.AppendLine($"  name: {binding.KubernetesSecretName}");
            bsb.AppendLine($"  namespace: {ns}");
            bsb.AppendLine("type: Opaque");
            bsb.AppendLine("stringData:");
            foreach (KeyValuePair<string, string> kv in credentials)
                bsb.AppendLine($"  {kv.Key}: \"{kv.Value.Replace("\"", "\\\"")}\"");

            await k8sFactory.EnsureNamespaceAsync(ns, bindingKubeconfig, ct);
            await k8sFactory.ApplyManifestAsync(bsb.ToString(), bindingKubeconfig, ct);

            binding.LastSyncedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Generates a new password for a MongoDB database user, updates it in MongoDB via
    /// mongosh exec on the primary pod, and stores the new credentials in the vault.
    /// </summary>
    public async Task RotateDatabasePasswordAsync(
        Guid tenantId, Guid mongoClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        MongoDatabase database = await db.MongoDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.MongoClusterId == mongo.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        string? adminPassword = await vaultService.GetMongoClusterSecretValueAsync(
            tenantId, mongo.Id, "ADMIN_PASSWORD", ct)
            ?? throw new InvalidOperationException("Admin password not found in vault.");

        string newPassword = GeneratePassword();

        // Drop the user from both the old location ({dbname}) and the new location (admin),
        // then recreate in admin. This handles users created by older EntKube versions that
        // stored users in the database itself rather than admin.
        string script = $$"""
            try { db.getSiblingDB('{{database.Name}}').dropUser('{{database.Owner}}') } catch(e) {}
            try { db.getSiblingDB('admin').dropUser('{{database.Owner}}') } catch(e) {}
            try {
              db.getSiblingDB('admin').createUser({
                user: '{{database.Owner}}',
                pwd: '{{newPassword}}',
                roles: [{ role: 'readWrite', db: '{{database.Name}}' }]
              });
              print("ENTK_SUCCESS");
              process.exit(0);
            } catch(e) {
              print("ENTK_ERROR: " + e);
              process.exit(1);
            }
            """;

        string rotateOutput = await k8sFactory.ExecuteMongoWithOutputAsync(
            mongo.Name, mongo.Namespace, script,
            mongo.KubernetesCluster.Kubeconfig!,
            username: "admin", password: adminPassword, ct: ct);

        if (!rotateOutput.Contains("ENTK_SUCCESS"))
            throw new InvalidOperationException(
                $"Failed to rotate password for MongoDB user '{database.Owner}': {rotateOutput.Trim()}");

        // Store updated credentials in vault.
        string host = $"{mongo.Name}-svc.{mongo.Namespace}.svc.cluster.local";
        string connectionString =
            $"mongodb://{database.Owner}:{newPassword}@{host}:27017/{database.Name}?authSource=admin&replicaSet={mongo.Name}&authMechanism=SCRAM-SHA-256";
        string k8sSecretName = $"{mongo.Name}-{database.Name}-credentials";

        await StoreDatabaseSecretAsync(tenantId, database.Id, "PASSWORD", newPassword, k8sSecretName, mongo.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "CONNECTION_STRING", connectionString, k8sSecretName, mongo.Namespace, ct);
    }

    // ──────── Queries ────────

    /// <summary>
    /// Gets all managed MongoDB clusters for a tenant, including their databases.
    /// </summary>
    public async Task<List<MongoCluster>> GetClustersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .ThenInclude(k => k.Environment)
            .Include(c => c.StorageLink)
            .Include(c => c.Databases)
            .Include(c => c.Backups.OrderByDescending(b => b.StartedAt).Take(5))
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Registers an existing MongoDB cluster that was deployed outside EntKube.
    /// Creates a MongoCluster record pointing to the live resource without applying
    /// any manifest. The cluster must already be running on the target K8s cluster.
    /// </summary>
    public async Task<MongoCluster> RegisterExistingClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        string? externalUri = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongoCluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            MongoVersion = "unknown",
            Members = 1,
            StorageSize = "unknown",
            Status = MongoClusterStatus.Running,
            IsExternal = true,
            ExternalUri = string.IsNullOrWhiteSpace(externalUri) ? null : externalUri.Trim()
        };

        db.MongoClusters.Add(mongoCluster);
        await db.SaveChangesAsync(ct);

        return mongoCluster;
    }

    /// <summary>
    /// Updates connection settings for an external MongoDB cluster. If adminPassword is
    /// provided, it is stored as the {name}-admin-password K8s Secret on the cluster so
    /// backup and migration Jobs can authenticate exactly like managed clusters — no
    /// external URI needed. The internal service endpoint ({name}-svc) is used automatically.
    /// </summary>
    public async Task UpdateExternalClusterSettingsAsync(
        Guid tenantId, Guid mongoClusterId,
        string? adminPassword,
        Guid? storageLinkId,
        string? backupSchedule,
        int retentionDays,
        int maxBackups,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId && c.IsExternal, ct)
            ?? throw new InvalidOperationException("External MongoDB cluster not found.");

        mongo.StorageLinkId = storageLinkId;
        mongo.BackupSchedule = string.IsNullOrWhiteSpace(backupSchedule) ? null : backupSchedule.Trim();
        mongo.RetentionDays = retentionDays > 0 ? retentionDays : 30;
        mongo.MaxBackups = maxBackups > 0 ? maxBackups : 20;

        await db.SaveChangesAsync(ct);

        string kubeconfig = mongo.KubernetesCluster.Kubeconfig!;

        // Store the admin password as a K8s Secret so Jobs can reference it by name,
        // exactly like managed clusters. Password is never persisted in EntKube's database.
        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            string adminSecretManifest = BuildAdminSecretManifest(mongo.Name, mongo.Namespace, adminPassword.Trim());
            await k8sFactory.ApplyManifestAsync(adminSecretManifest, kubeconfig, ct);
        }

        // Set up the scheduled backup CronJob if a schedule and storage link are configured.
        // Uses the same manifest as managed clusters — the admin secret makes it identical.
        if (!string.IsNullOrEmpty(mongo.BackupSchedule) && storageLinkId.HasValue)
        {
            StorageLink storageLink = await db.StorageLinks.FirstAsync(s => s.Id == storageLinkId.Value, ct);
            mongo.StorageLink = storageLink;

            string s3SecretName = $"{mongo.Name}-s3-credentials";
            await EnsureStorageSecretsInK8sAsync(tenantId, storageLink, s3SecretName, mongo.Namespace, kubeconfig, ct);

            string cronManifest = BuildScheduledBackupCronJobManifest(mongo, storageLink, s3SecretName);
            await k8sFactory.ApplyManifestAsync(cronManifest, kubeconfig, ct);
        }
    }

    /// <summary>
    /// Registers an existing database in an external MongoDB cluster without executing
    /// any MongoDB commands. Creates a tracking record so the database appears in EntKube.
    /// Use for databases that already exist in the external cluster.
    /// </summary>
    public async Task<MongoDatabase> RegisterExternalDatabaseAsync(
        Guid tenantId, Guid mongoClusterId, string databaseName, string owner,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId && c.IsExternal, ct)
            ?? throw new InvalidOperationException("External MongoDB cluster not found.");

        bool exists = await db.Set<MongoDatabase>()
            .AnyAsync(d => d.MongoClusterId == mongoClusterId
                && d.Name == databaseName, ct);

        if (exists)
            throw new InvalidOperationException($"Database '{databaseName}' is already tracked on this cluster.");

        MongoDatabase database = new()
        {
            Id = Guid.NewGuid(),
            MongoClusterId = mongo.Id,
            Name = databaseName,
            Owner = string.IsNullOrWhiteSpace(owner) ? databaseName : owner.Trim(),
            Status = MongoDatabaseStatus.Ready
        };

        db.Set<MongoDatabase>().Add(database);
        await db.SaveChangesAsync(ct);

        return database;
    }

    /// <summary>
    /// Discovers databases on a MongoDB cluster (managed or external) by executing a
    /// listDatabases command against the primary pod via kubectl exec. Reads the admin
    /// password from the {name}-admin-password K8s Secret. Any databases not yet tracked
    /// in EntKube are added automatically. System databases (admin, local, config) are
    /// skipped. Fails silently if the pod is unreachable or credentials are missing.
    /// </summary>
    public async Task<string> SyncLiveDatabasesAsync(
        Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster? cluster = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct);

        if (cluster is null) return string.Empty;

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

        // Read admin password from K8s Secret — same secret that backup Jobs use.
        string? adminPw = await k8sFactory.GetSecretValueAsync(
            $"{cluster.Name}-admin-password", "password", cluster.Namespace, kubeconfig, ct);

        // Prefix each name with ENTK_DB: so we can pick out exactly our output lines
        // and ignore all mongosh REPL echoes, banners, and warnings.
        const string script = "db.adminCommand({listDatabases:1}).databases.filter(d=>!['admin','local','config'].includes(d.name)).forEach(d=>print('ENTK_DB:'+d.name));";

        string output;
        // Hard timeout: if mongosh hangs (auth prompt, slow pod, etc.) we must not
        // block the cluster detail page. 12 seconds is generous for a local exec.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        output = await k8sFactory.ExecuteMongoWithOutputAsync(
            cluster.Name, cluster.Namespace, script, kubeconfig,
            username: string.IsNullOrEmpty(adminPw) ? null : "admin",
            password: adminPw,
            ct: timeout.Token);

        // Re-query existing names right before inserting — PersistDiscoveredMetadataAsync
        // may have added rows from spec.users in a separate context after we loaded above.
        List<MongoDatabase> existing = await db.Set<MongoDatabase>()
            .Where(d => d.MongoClusterId == cluster.Id)
            .ToListAsync(ct);

        // Remove any records whose names were inserted as garbage from earlier buggy
        // mongosh output parsing (e.g. "]" from the echoed array closing bracket).
        bool changed = false;
        foreach (MongoDatabase bad in existing.Where(d => !IsValidMongoDbName(d.Name)))
        {
            db.Set<MongoDatabase>().Remove(bad);
            changed = true;
        }

        HashSet<string> existingNames = existing
            .Where(d => IsValidMongoDbName(d.Name))
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The REPL prompt is written to the same stdout stream as print(), so ENTK_DB:name
        // may appear mid-line (e.g. "test> ENTK_DB:kp test>"). Search the whole output
        // for every occurrence of the prefix instead of matching line-starts.
        const string prefix = "ENTK_DB:";
        int searchFrom = 0;
        while (true)
        {
            int idx = output.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;

            int nameStart = idx + prefix.Length;
            int nameEnd = output.IndexOfAny([' ', '\r', '\n', '\t'], nameStart);
            string dbName = (nameEnd < 0 ? output[nameStart..] : output[nameStart..nameEnd]).Trim();
            searchFrom = nameEnd < 0 ? output.Length : nameEnd;

            if (string.IsNullOrEmpty(dbName) || existingNames.Contains(dbName))
                continue;

            if (!IsValidMongoDbName(dbName))
                continue;

            db.Set<MongoDatabase>().Add(new MongoDatabase
            {
                Id = Guid.NewGuid(),
                MongoClusterId = cluster.Id,
                Name = dbName,
                Owner = dbName,
                Status = MongoDatabaseStatus.Ready
            });
            existingNames.Add(dbName);
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);

        return output;
    }

    /// <summary>
    /// Migrates data from an external MongoDB cluster to a managed (Community Operator) cluster.
    /// The Job runs on the source cluster's K8s environment so both the source and target
    /// are reachable via their internal service endpoints. Both clusters must be on the same
    /// Kubernetes cluster, or the source K8s cluster must have network access to the target.
    ///
    /// Authentication uses {source.Name}-admin-password and {target.Name}-admin-password K8s
    /// Secrets — the same convention as managed clusters. Store the source admin credentials
    /// via UpdateExternalClusterSettingsAsync before migrating.
    /// </summary>
    public async Task MigrateFromExternalToManagedAsync(
        Guid tenantId, Guid sourceMongoClusterId, Guid targetMongoClusterId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster source = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == sourceMongoClusterId && c.TenantId == tenantId && c.IsExternal, ct)
            ?? throw new InvalidOperationException("Source external MongoDB cluster not found.");

        // Verify the admin secret exists on the source cluster.
        string? adminPw = await k8sFactory.GetSecretValueAsync(
            $"{source.Name}-admin-password", "password", source.Namespace,
            source.KubernetesCluster.Kubeconfig!, ct);

        if (string.IsNullOrEmpty(adminPw))
            throw new InvalidOperationException(
                $"Admin credentials not found. Create the '{source.Name}-admin-password' K8s Secret " +
                "by saving an admin password via the cluster settings.");

        MongoCluster target = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == targetMongoClusterId && c.TenantId == tenantId && !c.IsExternal, ct)
            ?? throw new InvalidOperationException("Target managed MongoDB cluster not found.");

        string jobName = $"{source.Name}-to-{target.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

        // The Job runs on the source cluster's K8s environment.
        // Both clusters must share network access for cross-cluster migrations.
        string manifest = BuildMigrateFromExternalManifest(jobName, source, target);
        await k8sFactory.ApplyManifestAsync(manifest, source.KubernetesCluster.Kubeconfig!, ct);
    }

    /// <summary>
    /// Gets the live detail for a Percona MongoDB cluster including pod status.
    /// Queries the PerconaServerMongoDB CRD status and lists pods.
    /// </summary>
    public async Task<MongoClusterDetail?> GetClusterDetailAsync(
        Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster? cluster = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .Include(c => c.Databases)
            .Include(c => c.Backups.OrderByDescending(b => b.StartedAt).Take(20))
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct);

        if (cluster is null)
        {
            return null;
        }

        MongoClusterDetail detail = new()
        {
            Cluster = cluster,
            Phase = "Querying...",
            Backups = cluster.Backups.OrderByDescending(b => b.StartedAt).ToList()
        };

        bool clusterReachable = false;
        string podsJsonForReconcile = string.Empty;

        try
        {
            // Query the MongoDBCommunity CRD status.
            string clusterJson = await k8sFactory.GetJsonAsync(
                $"mongodbcommunity.mongodbcommunity.mongodb.com/{cluster.Name}",
                cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct: ct);

            ParseClusterStatus(clusterJson, detail);

            // Persist discovered version and databases back to the DB so the cluster
            // header shows the correct version and db count without re-querying K8s.
            await PersistDiscoveredMetadataAsync(cluster, detail, ct);

            // Query all pods in the namespace and filter by StatefulSet naming convention
            // ({name}-N). Label-selector-based queries are unreliable across Community
            // Operator versions (some use `app`, some use `app.kubernetes.io/name`).
            string podsJson = await k8sFactory.GetJsonAsync(
                "pods", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct: ct);

            detail.Pods = ParsePodList(podsJson, cluster.Name);
            podsJsonForReconcile = podsJson; // reuse below for backup status reconciliation

            // Reconcile DB status now that we have live K8s phase information.
            await ReconcileMongoClusterStatusAsync(cluster, detail, ct);

            clusterReachable = true;
        }
        catch (Exception)
        {
            detail.Phase = "Unable to reach cluster";
        }

        // Supplement CRD-based discovery with a live listDatabases call via kubectl exec.
        // Runs for both managed and external clusters so databases restored directly via
        // mongorestore (bypassing spec.users) are picked up automatically.
        // Must never throw — any failure here would kill the detail view loaded above.
        try
        {
            string rawOutput = await SyncLiveDatabasesAsync(cluster.TenantId, cluster.Id, ct);

            // Reload databases so the detail reflects any newly synced ones.
            using ApplicationDbContext refreshDb = dbFactory.CreateDbContext();
            cluster.Databases = await refreshDb.Set<MongoDatabase>()
                .Where(d => d.MongoClusterId == cluster.Id)
                .ToListAsync(ct);

            // Surface raw output when no databases found so the operator can diagnose
            // mongosh connectivity or auth issues without reading container logs.
            if (cluster.Databases.Count == 0)
                detail.SyncError = $"No databases found. Raw mongosh output: {rawOutput.Trim()}";
        }
        catch (Exception ex)
        {
            detail.SyncError = ex.InnerException?.Message ?? ex.Message;
        }

        // Fetch backup job status independently — has its own try/catch so a cluster-query
        // failure above does not prevent backup status from being updated.
        // Scan K8s Jobs for completed backup jobs so scheduled backups appear in the UI
        // even when they were not triggered through EntKube.
        List<MongoBackup> k8sBackups = await FetchMongoBackupsFromK8sAsync(cluster, ct);
        if (k8sBackups.Count > 0)
            detail.Backups = k8sBackups;

        await PersistMongoBackupsAsync(cluster.Id, k8sBackups, ct);

        // When K8s is reachable, reconcile Running backups using live pod/job data.
        if (clusterReachable)
            await ReconcileRunningBackupsFromK8sAsync(cluster, podsJsonForReconcile, ct);

        // Always run the stale check regardless of K8s reachability.
        // A backup still Running after 25 h is certainly finished (TTL is 24 h).
        await MarkStaleRunningBackupsAsync(cluster.Id, ct);

        // Remove completed backup records that have aged past the retention window.
        await PruneExpiredBackupsAsync(cluster, ct);

        // Always reload from DB after persisting so backup objects carry real GUIDs.
        // K8s-fetched records have Id=Guid.Empty; DB records are what restore methods need.
        using ApplicationDbContext backupsDb = dbFactory.CreateDbContext();
        DateTime retentionCutoff = DateTime.UtcNow.AddDays(-cluster.RetentionDays);
        List<MongoBackup> dbBackups = await backupsDb.MongoBackups
            .Where(b => b.MongoClusterId == cluster.Id
                && (b.Status == MongoBackupStatus.Running || b.StartedAt >= retentionCutoff))
            .OrderByDescending(b => b.StartedAt)
            .Take(20)
            .ToListAsync(ct);

        if (dbBackups.Count > 0)
            detail.Backups = dbBackups;

        return detail;
    }

    private async Task ReconcileMongoClusterStatusAsync(
        MongoCluster cluster, MongoClusterDetail detail, CancellationToken ct)
    {
        MongoClusterStatus? newStatus = DetermineMongoStatusFromPhase(
            detail.Phase, detail.ReadyMembers, cluster.Members, cluster.Status);

        if (newStatus is null || newStatus == cluster.Status)
            return;

        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            MongoCluster? tracked = await db.MongoClusters.FindAsync([cluster.Id], ct);
            if (tracked is not null && tracked.Status != newStatus.Value)
            {
                tracked.Status = newStatus.Value;
                await db.SaveChangesAsync(ct);
                cluster.Status = newStatus.Value;
            }
        }
        catch { }
    }

    private static MongoClusterStatus? DetermineMongoStatusFromPhase(
        string phase, int readyMembers, int expectedMembers, MongoClusterStatus currentStatus)
    {
        string lower = phase.ToLowerInvariant();

        if (lower == "running" && readyMembers >= expectedMembers)
        {
            if (currentStatus is MongoClusterStatus.Creating
                or MongoClusterStatus.Upgrading
                or MongoClusterStatus.Restoring
                or MongoClusterStatus.Failed)
            {
                return MongoClusterStatus.Running;
            }
        }

        if (lower == "failed")
        {
            if (currentStatus is not MongoClusterStatus.Failed and not MongoClusterStatus.Deleting)
                return MongoClusterStatus.Failed;
        }

        return null;
    }

    private async Task<List<MongoBackup>> FetchMongoBackupsFromK8sAsync(
        MongoCluster cluster, CancellationToken ct)
    {
        try
        {
            // Query all jobs in the namespace — label filter is avoided because CronJob-spawned
            // jobs may not carry the entkube.io/mongo-cluster label if the CronJob was deployed
            // before that label was added to the jobTemplate. We identify our jobs by label OR
            // by ownerReference pointing to this cluster's scheduled-backup CronJob.
            string jobsJson = await k8sFactory.GetJsonAsync(
                "jobs", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct: ct);

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jobsJson);
            System.Text.Json.JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("items", out System.Text.Json.JsonElement items))
                return [];

            string scheduledCronJobName = $"{cluster.Name}-scheduled-backup";
            List<MongoBackup> result = [];

            foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta))
                    continue;

                string? name = meta.TryGetProperty("name", out System.Text.Json.JsonElement nameEl)
                    ? nameEl.GetString() : null;
                if (string.IsNullOrEmpty(name))
                    continue;

                bool hasClusterLabel = false;
                bool isScheduled = false;

                if (meta.TryGetProperty("labels", out System.Text.Json.JsonElement labels))
                {
                    // Skip restore and s3-prune jobs.
                    if (labels.TryGetProperty("entkube.io/restore-source", out _)
                        || labels.TryGetProperty("entkube.io/restore-type", out _)
                        || (labels.TryGetProperty("entkube.io/job-type", out System.Text.Json.JsonElement jobTypeEl)
                            && jobTypeEl.GetString() == "s3-prune"))
                        continue;

                    if (labels.TryGetProperty("entkube.io/mongo-cluster", out System.Text.Json.JsonElement clusterLabel)
                        && clusterLabel.GetString() == cluster.Name)
                        hasClusterLabel = true;
                }

                // Check ownerReferences: jobs from our scheduled CronJob belong to this cluster
                // even when the label is absent (older CronJob deployments).
                if (meta.TryGetProperty("ownerReferences", out System.Text.Json.JsonElement owners)
                    && owners.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (System.Text.Json.JsonElement owner in owners.EnumerateArray())
                    {
                        bool isCronJob = owner.TryGetProperty("kind", out System.Text.Json.JsonElement kindEl)
                            && kindEl.GetString() == "CronJob";
                        bool isOurCronJob = owner.TryGetProperty("name", out System.Text.Json.JsonElement ownerNameEl)
                            && ownerNameEl.GetString() == scheduledCronJobName;

                        if (isCronJob)
                        {
                            isScheduled = true;
                            if (isOurCronJob)
                                hasClusterLabel = true; // treat as belonging to this cluster
                            break;
                        }
                    }
                }

                // Skip jobs that don't belong to this cluster.
                if (!hasClusterLabel)
                    continue;

                MongoBackupStatus status = MongoBackupStatus.Running;
                DateTime? startedAt = null;
                DateTime? completedAt = null;

                if (item.TryGetProperty("status", out System.Text.Json.JsonElement jobStatus))
                {
                    if (jobStatus.TryGetProperty("startTime", out System.Text.Json.JsonElement startEl)
                        && DateTime.TryParse(startEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedStart))
                        startedAt = parsedStart;

                    if (jobStatus.TryGetProperty("completionTime", out System.Text.Json.JsonElement doneEl)
                        && DateTime.TryParse(doneEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedDone))
                        completedAt = parsedDone;

                    if (jobStatus.TryGetProperty("succeeded", out System.Text.Json.JsonElement succEl)
                        && succEl.TryGetInt32(out int succeededCount) && succeededCount > 0)
                        status = MongoBackupStatus.Completed;
                    else if (jobStatus.TryGetProperty("failed", out System.Text.Json.JsonElement failEl)
                        && failEl.TryGetInt32(out int failedCount) && failedCount > 0)
                        status = MongoBackupStatus.Failed;
                    else if (completedAt.HasValue)
                    {
                        // completionTime is set but succeeded/failed counts not yet visible —
                        // check conditions array for a definitive Complete or Failed signal.
                        if (jobStatus.TryGetProperty("conditions", out System.Text.Json.JsonElement conds)
                            && conds.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (System.Text.Json.JsonElement cond in conds.EnumerateArray())
                            {
                                bool isTrue = cond.TryGetProperty("status", out System.Text.Json.JsonElement condStatus)
                                    && condStatus.GetString() == "True";
                                if (!isTrue) continue;
                                if (cond.TryGetProperty("type", out System.Text.Json.JsonElement condType))
                                {
                                    string? t = condType.GetString();
                                    if (t == "Complete") { status = MongoBackupStatus.Completed; break; }
                                    if (t == "Failed") { status = MongoBackupStatus.Failed; break; }
                                }
                            }
                        }
                        // If still undecided but completionTime is set, treat as completed.
                        if (status == MongoBackupStatus.Running)
                            status = MongoBackupStatus.Completed;
                    }
                }

                result.Add(new MongoBackup
                {
                    Id = Guid.Empty,
                    MongoClusterId = cluster.Id,
                    Name = name,
                    Type = isScheduled ? MongoBackupType.Scheduled : MongoBackupType.OnDemand,
                    Status = status,
                    StartedAt = startedAt ?? DateTime.UtcNow,
                    CompletedAt = completedAt
                });
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-cluster.RetentionDays);
            return [.. result
                .Where(b => b.Status == MongoBackupStatus.Running || b.StartedAt >= cutoff)
                .OrderByDescending(b => b.StartedAt)
                .Take(20)];
        }
        catch
        {
            return [];
        }
    }

    private async Task PersistMongoBackupsAsync(
        Guid mongoClusterId, List<MongoBackup> k8sBackups, CancellationToken ct)
    {
        if (k8sBackups.Count == 0)
            return;

        foreach (MongoBackup k8s in k8sBackups)
        {
            if (k8s.Id != Guid.Empty)
                continue;

            try
            {
                using ApplicationDbContext db = dbFactory.CreateDbContext();
                MongoBackup? existing = await db.MongoBackups
                    .FirstOrDefaultAsync(
                        b => b.MongoClusterId == mongoClusterId && b.Name == k8s.Name, ct);

                if (existing is null)
                {
                    db.MongoBackups.Add(new MongoBackup
                    {
                        Id = Guid.NewGuid(),
                        MongoClusterId = mongoClusterId,
                        Name = k8s.Name,
                        Type = k8s.Type,
                        Status = k8s.Status,
                        StartedAt = k8s.StartedAt,
                        CompletedAt = k8s.CompletedAt
                    });
                    await db.SaveChangesAsync(ct);
                }
                else if (existing.Status != k8s.Status)
                {
                    existing.Status = k8s.Status;
                    if (k8s.Status != MongoBackupStatus.Running)
                        existing.CompletedAt = k8s.CompletedAt ?? DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Marks DB backup records still Running after 25 h as Completed.
    /// Runs unconditionally (even when K8s is unreachable) because a backup that has been
    /// Running for more than the job TTL (86400 s = 24 h) is certainly done.
    /// </summary>
    private async Task MarkStaleRunningBackupsAsync(Guid mongoClusterId, CancellationToken ct)
    {
        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            DateTime threshold = DateTime.UtcNow.AddHours(-25);

            List<MongoBackup> stale = await db.MongoBackups
                .Where(b => b.MongoClusterId == mongoClusterId
                    && b.Status == MongoBackupStatus.Running
                    && b.StartedAt < threshold)
                .ToListAsync(ct);

            if (stale.Count == 0)
                return;

            foreach (MongoBackup backup in stale)
            {
                backup.Status = MongoBackupStatus.Completed;
                backup.CompletedAt ??= backup.StartedAt.AddHours(1);
            }

            await db.SaveChangesAsync(ct);
        }
        catch { }
    }

    /// <summary>
    /// Removes completed/failed backup records older than RetentionDays from both the DB and S3.
    /// Fires a K8s Job to delete the S3 archives first, then removes the DB rows.
    /// Running backups are never pruned. Safe to call on every detail page load — no-ops when
    /// nothing has expired.
    /// </summary>
    private async Task PruneExpiredBackupsAsync(MongoCluster cluster, CancellationToken ct)
    {
        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            DateTime cutoff = DateTime.UtcNow.AddDays(-cluster.RetentionDays);

            List<MongoBackup> expired = await db.MongoBackups
                .Where(b => b.MongoClusterId == cluster.Id
                    && b.Status != MongoBackupStatus.Running
                    && b.StartedAt < cutoff)
                .ToListAsync(ct);

            if (expired.Count == 0)
                return;

            // Delete the S3 archives for each expired backup before removing DB records.
            if (cluster.StorageLink is not null)
            {
                string s3SecretName = $"{cluster.Name}-s3-credentials";
                string deleteCommands = string.Join(" && ", expired.Select(b =>
                    $"aws s3 rm \"s3://{cluster.StorageLink.BucketName}/{cluster.Name}/{b.Name}.archive\" --endpoint-url \"{cluster.StorageLink.Endpoint}\" || true"));

                string jobName = $"{cluster.Name}-s3-prune-{DateTime.UtcNow:yyyyMMddHHmmss}";
                string jobManifest = BuildS3PruneJobManifest(jobName, cluster, s3SecretName, deleteCommands);

                try
                {
                    await k8sFactory.ApplyManifestAsync(jobManifest, cluster.KubernetesCluster.Kubeconfig!, ct);
                }
                catch { }
            }

            db.MongoBackups.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }
        catch { }
    }

    private static string BuildS3PruneJobManifest(
        string jobName, MongoCluster cluster, string s3SecretName, string deleteCommands)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {jobName}");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {cluster.Name}");
        sb.AppendLine("    entkube.io/job-type: s3-prune");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 0");
        sb.AppendLine("  ttlSecondsAfterFinished: 300");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: s3-prune");
        sb.AppendLine("          image: amazon/aws-cli");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"{deleteCommands}\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: AWS_ACCESS_KEY_ID");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: ACCESS_KEY");
        sb.AppendLine("            - name: AWS_SECRET_ACCESS_KEY");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: SECRET_KEY");
        sb.AppendLine("            - name: AWS_DEFAULT_REGION");
        sb.AppendLine($"              value: \"{cluster.StorageLink!.Region ?? "us-east-1"}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Reconciles DB backup records still marked Running using two sources:
    /// 1. The already-fetched pods JSON — K8s automatically labels job pods with
    ///    "batch.kubernetes.io/job-name" so we can read pod phase without a separate
    ///    jobs query and without any special RBAC for the jobs resource.
    /// 2. A fallback all-jobs kubectl query for any backup not resolved via pods.
    /// Only called when K8s is reachable so we don't falsely update during outages.
    /// </summary>
    private async Task ReconcileRunningBackupsFromK8sAsync(
        MongoCluster cluster, string podsJson, CancellationToken ct)
    {
        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            List<MongoBackup> running = await db.MongoBackups
                .Where(b => b.MongoClusterId == cluster.Id && b.Status == MongoBackupStatus.Running)
                .ToListAsync(ct);

            if (running.Count == 0)
                return;

            HashSet<string> runningNames = running
                .Select(b => b.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ── Source 1: pod phases from the already-fetched pods JSON ──
            // K8s labels every job pod with batch.kubernetes.io/job-name (1.21+) or job-name (older).
            // Fallback: match by pod name prefix ({job-name}-{hash}) for distributions that omit labels.
            Dictionary<string, string> podPhaseByJobName = []; // job-name → best phase

            if (!string.IsNullOrEmpty(podsJson))
            {
                try
                {
                    using System.Text.Json.JsonDocument podsDoc = System.Text.Json.JsonDocument.Parse(podsJson);
                    if (podsDoc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement podItems))
                    {
                        foreach (System.Text.Json.JsonElement pod in podItems.EnumerateArray())
                        {
                            if (!pod.TryGetProperty("metadata", out System.Text.Json.JsonElement podMeta)) continue;

                            string? phase = null;
                            if (pod.TryGetProperty("status", out System.Text.Json.JsonElement podStatus)
                                && podStatus.TryGetProperty("phase", out System.Text.Json.JsonElement phaseEl))
                                phase = phaseEl.GetString();
                            if (phase is null) continue;

                            string? jobName = null;

                            // Primary: label-based lookup (K8s 1.21+ and older variants).
                            if (podMeta.TryGetProperty("labels", out System.Text.Json.JsonElement podLabels))
                            {
                                if (podLabels.TryGetProperty("batch.kubernetes.io/job-name", out System.Text.Json.JsonElement jn))
                                    jobName = jn.GetString();
                                else if (podLabels.TryGetProperty("job-name", out System.Text.Json.JsonElement jn2))
                                    jobName = jn2.GetString();
                            }

                            // Fallback: name-prefix matching — pod names are "{job-name}-{5-char-hash}".
                            if (string.IsNullOrEmpty(jobName)
                                && podMeta.TryGetProperty("name", out System.Text.Json.JsonElement podNameEl))
                            {
                                string? podName = podNameEl.GetString();
                                if (!string.IsNullOrEmpty(podName))
                                {
                                    foreach (string candidate in runningNames)
                                    {
                                        if (podName.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase))
                                        {
                                            jobName = candidate;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(jobName)) continue;

                            // Keep the "best" phase: Succeeded > Failed > anything else.
                            if (!podPhaseByJobName.TryGetValue(jobName, out string? bestSoFar)
                                || bestSoFar == "Running"
                                || (bestSoFar != "Succeeded" && phase == "Succeeded"))
                            {
                                podPhaseByJobName[jobName] = phase;
                            }
                        }
                    }
                }
                catch { }
            }

            // ── Source 2: all-jobs query as fallback (no label filter) ──
            Dictionary<string, (bool Succeeded, bool Failed, DateTime? CompletionTime)> k8sJobs = [];
            try
            {
                string allJobsJson = await k8sFactory.GetJsonAsync(
                    "jobs", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct: ct);

                using System.Text.Json.JsonDocument jobsDoc = System.Text.Json.JsonDocument.Parse(allJobsJson);
                if (jobsDoc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement jobItems))
                {
                    foreach (System.Text.Json.JsonElement item in jobItems.EnumerateArray())
                    {
                        if (!item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)) continue;
                        if (!meta.TryGetProperty("name", out System.Text.Json.JsonElement nameEl)) continue;
                        string? name = nameEl.GetString();
                        if (string.IsNullOrEmpty(name)) continue;

                        bool succeeded = false, failed = false;
                        DateTime? completionTime = null;

                        if (item.TryGetProperty("status", out System.Text.Json.JsonElement jobStatus))
                        {
                            succeeded = jobStatus.TryGetProperty("succeeded", out System.Text.Json.JsonElement s)
                                && s.TryGetInt32(out int sv) && sv > 0;
                            failed = jobStatus.TryGetProperty("failed", out System.Text.Json.JsonElement f)
                                && f.TryGetInt32(out int fv) && fv > 0;
                            if (jobStatus.TryGetProperty("completionTime", out System.Text.Json.JsonElement ct2)
                                && DateTime.TryParse(ct2.GetString(), null,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime ct3))
                                completionTime = ct3;
                        }

                        k8sJobs[name] = (succeeded, failed, completionTime);
                    }
                }
            }
            catch { } // jobs RBAC unavailable or other error — pods source above takes over

            bool changed = false;

            foreach (MongoBackup backup in running)
            {
                // Check pod phase first (most reliable, no extra kubectl call).
                if (podPhaseByJobName.TryGetValue(backup.Name, out string? podPhase))
                {
                    if (podPhase == "Succeeded")
                    {
                        backup.Status = MongoBackupStatus.Completed;
                        backup.CompletedAt ??= DateTime.UtcNow;
                        changed = true;
                        continue;
                    }
                    if (podPhase == "Failed")
                    {
                        backup.Status = MongoBackupStatus.Failed;
                        changed = true;
                        continue;
                    }
                    // Pod still Running → leave as Running
                    continue;
                }

                // Fall back to jobs query result.
                if (k8sJobs.TryGetValue(backup.Name, out var js))
                {
                    if (js.Succeeded || js.CompletionTime.HasValue)
                    {
                        backup.Status = MongoBackupStatus.Completed;
                        backup.CompletedAt ??= js.CompletionTime ?? DateTime.UtcNow;
                        changed = true;
                    }
                    else if (js.Failed)
                    {
                        backup.Status = MongoBackupStatus.Failed;
                        changed = true;
                    }
                    // else genuinely still active → leave as Running
                    continue;
                }

                // Job gone from K8s (TTL-deleted) but not yet old enough for stale check →
                // MarkStaleRunningBackupsAsync handles this unconditionally after this method.
            }

            if (changed)
                await db.SaveChangesAsync(ct);
        }
        catch { }
    }

    // ──────── Private Helpers ────────

    /// <summary>
    /// Returns the Docker Hub image tag for a given MongoDB version string.
    /// Uses major.minor only (e.g. "8.0") because Docker Hub carries minor-stream
    /// tags reliably, but not every X.Y.Z patch tag that the Percona operator accepts.
    /// Falls back to "mongo:8.0" for unknown/empty versions.
    /// </summary>
    private static string MongoImage(string version)
    {
        if (string.IsNullOrEmpty(version) || version == "unknown")
            return "mongo:8.0";

        string[] parts = version.Split('.');
        return parts.Length >= 2
            ? $"mongo:{parts[0]}.{parts[1]}"
            : $"mongo:{version}";
    }

    /// <summary>
    /// Ensures the S3 storage credentials are available as a Kubernetes Secret
    /// in the target namespace so backup Jobs can access the bucket.
    /// </summary>
    private async Task EnsureStorageSecretsInK8sAsync(
        Guid tenantId, StorageLink storageLink, string secretName, string ns,
        string kubeconfig, CancellationToken ct)
    {
        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenantId, storageLink.Id, ct);

        foreach (VaultSecret secret in secrets)
        {
            if (secret.Name == "ACCESS_KEY" || secret.Name == "SECRET_KEY")
            {
                await vaultService.ConfigureKubernetesSyncAsync(secret.Id, true, secretName, ns, ct);
            }
        }

        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, storageLink.Id, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, storageLink.Id, "SECRET_KEY", ct);

        if (accessKey is null || secretKey is null)
        {
            throw new InvalidOperationException(
                "S3 credentials (ACCESS_KEY and SECRET_KEY) are missing from the storage link vault. " +
                "Add them via the Storage tab before creating a backup-enabled cluster.");
        }

        string accessKeyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(accessKey));
        string secretKeyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secretKey));

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        sb.AppendLine($"  ACCESS_KEY: {accessKeyB64}");
        sb.AppendLine($"  SECRET_KEY: {secretKeyB64}");

        await k8sFactory.ApplyManifestAsync(sb.ToString(), kubeconfig, ct);
    }

    /// <summary>
    /// Stores a single database credential in the vault with K8s sync configuration.
    /// </summary>
    private async Task StoreDatabaseSecretAsync(
        Guid tenantId, Guid databaseId, string name, string value,
        string k8sSecretName, string k8sNamespace, CancellationToken ct)
    {
        await vaultService.SetMongoDatabaseSecretAsync(
            tenantId, databaseId, name, value, k8sSecretName, k8sNamespace, ct);
    }

    /// <summary>
    /// Removes all vault secrets associated with a MongoDB database.
    /// </summary>
    private async Task DeleteDatabaseSecretsAsync(Guid databaseId, CancellationToken ct)
    {
        using ApplicationDbContext secretDb = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await secretDb.VaultSecrets
            .Where(s => s.MongoDatabaseId == databaseId)
            .ToListAsync(ct);

        secretDb.VaultSecrets.RemoveRange(secrets);
        await secretDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes all vault secrets scoped to a managed MongoDB cluster (e.g. the admin password).
    /// </summary>
    private async Task DeleteClusterSecretsAsync(Guid mongoClusterId, CancellationToken ct)
    {
        using ApplicationDbContext secretDb = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await secretDb.VaultSecrets
            .Where(s => s.MongoClusterId == mongoClusterId)
            .ToListAsync(ct);

        secretDb.VaultSecrets.RemoveRange(secrets);
        await secretDb.SaveChangesAsync(ct);
    }

    // ──────── Admin Credentials ────────

    /// <summary>
    /// Reads the MongoDB admin password from the Kubernetes secret created by the
    /// Community Operator ({name}-admin-password) and stores it in the vault so
    /// EntKube can use it for database operations without re-reading from K8s each time.
    /// </summary>
    public async Task FetchAdminPasswordAsync(
        Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster mongo = await db.MongoClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == mongoClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("MongoDB cluster not found.");

        string secretName = $"{mongo.Name}-admin-password";
        string kubeconfig = mongo.KubernetesCluster.Kubeconfig!;

        string? password = await k8sFactory.GetSecretValueAsync(
            secretName, "password", mongo.Namespace, kubeconfig, ct);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"Could not read the MongoDB admin secret '{secretName}' in namespace '{mongo.Namespace}'. " +
                "Ensure the cluster is running and the kubeconfig has read access to Secrets.");
        }

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetMongoClusterSecretAsync(
            tenantId, mongoClusterId, "ADMIN_PASSWORD", password,
            secretName, mongo.Namespace, ct);
    }

    // ──────── DatabaseBinding management ────────

    /// <summary>
    /// Creates a binding between a MongoDB database and an app deployment. Call
    /// SyncDatabaseCredentialsToK8sAsync afterwards to push credentials to the app namespace.
    /// </summary>
    public async Task<DatabaseBinding> AddMongoDatabaseBindingAsync(
        Guid appDeploymentId, Guid mongoDatabaseId, string kubernetesSecretName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DatabaseBinding binding = new()
        {
            Id = Guid.NewGuid(),
            AppDeploymentId = appDeploymentId,
            MongoDatabaseId = mongoDatabaseId,
            KubernetesSecretName = kubernetesSecretName
        };

        db.DatabaseBindings.Add(binding);
        await db.SaveChangesAsync(ct);
        return binding;
    }

    /// <summary>
    /// Generates a secure random password for database users.
    /// </summary>
    private static string GeneratePassword()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "x").Replace("/", "y")[..32];
    }

    // ──────── Manifest Builders ────────

    /// <summary>
    /// Builds a multi-document manifest that creates the ServiceAccount, Role, and
    /// RoleBinding required by the MongoDB agent running inside each pod.
    ///
    /// The Community Operator provisions these resources only in its own installation
    /// namespace. When the MongoDB cluster lives in a different namespace the agent's
    /// readiness probe panics with "secrets forbidden" because the ServiceAccount has
    /// no permissions there. Applying these three resources fixes that.
    ///
    /// Permissions mirror the mongodb-database Role in the operator's Helm chart:
    ///   - secrets: get  (agent reads the mongodb-config automation-config secret)
    ///   - pods: get, patch, delete  (agent manages pod lifecycle during elections)
    /// </summary>
    private static string BuildServiceAccountManifest(string ns)
    {
        StringBuilder sb = new();

        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ServiceAccount");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: mongodb-database");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("---");
        sb.AppendLine("apiVersion: rbac.authorization.k8s.io/v1");
        sb.AppendLine("kind: Role");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: mongodb-database");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("rules:");
        sb.AppendLine("  - apiGroups: [\"\"]");
        sb.AppendLine("    resources: [\"secrets\"]");
        sb.AppendLine("    verbs: [\"get\"]");
        sb.AppendLine("  - apiGroups: [\"\"]");
        sb.AppendLine("    resources: [\"pods\"]");
        sb.AppendLine("    verbs: [\"get\", \"patch\", \"delete\"]");
        sb.AppendLine("---");
        sb.AppendLine("apiVersion: rbac.authorization.k8s.io/v1");
        sb.AppendLine("kind: RoleBinding");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: mongodb-database");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("subjects:");
        sb.AppendLine("  - kind: ServiceAccount");
        sb.AppendLine("    name: mongodb-database");
        sb.AppendLine($"    namespace: {ns}");
        sb.AppendLine("roleRef:");
        sb.AppendLine("  kind: Role");
        sb.AppendLine("  name: mongodb-database");
        sb.AppendLine("  apiGroup: rbac.authorization.k8s.io");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the K8s Secret manifest that holds the MongoDB admin password.
    /// Must be applied before the MongoDBCommunity CRD — the operator reads this
    /// Secret during startup to configure SCRAM credentials.
    /// </summary>
    private static bool IsValidMongoDbName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 63
        && name.IndexOfAny([' ', '/', '\\', '.', '"', '$', '*', '<', '>', ':', '|', '?', '[', ']', '\'']) < 0;

    private static string BuildAdminSecretManifest(string name, string ns, string password)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}-admin-password");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("stringData:");
        sb.AppendLine($"  password: \"{password}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the MongoDBCommunity CRD manifest. Defines an admin user referencing the
    /// pre-existing {name}-admin-password Secret so the operator can configure SCRAM
    /// authentication and create the StatefulSet.
    /// </summary>
    private static string BuildClusterManifest(MongoCluster mongo)
    {
        // Community Operator requires a full X.Y.Z version; coerce X.Y → X.Y.0.
        string version = mongo.MongoVersion.Count(c => c == '.') < 2
            ? mongo.MongoVersion + ".0"
            : mongo.MongoVersion;

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: mongodbcommunity.mongodb.com/v1");
        sb.AppendLine("kind: MongoDBCommunity");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {mongo.Name}");
        sb.AppendLine($"  namespace: {mongo.Namespace}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  members: {mongo.Members}");
        sb.AppendLine("  type: ReplicaSet");
        sb.AppendLine($"  version: \"{version}\"");
        sb.AppendLine("  security:");
        sb.AppendLine("    authentication:");
        sb.AppendLine("      modes: [\"SCRAM\"]");
        sb.AppendLine("  users:");
        sb.AppendLine("    - name: admin");
        sb.AppendLine("      db: admin");
        sb.AppendLine("      passwordSecretRef:");
        sb.AppendLine($"        name: {mongo.Name}-admin-password");
        sb.AppendLine($"      scramCredentialsSecretName: {mongo.Name}-scram");
        sb.AppendLine("      roles:");
        sb.AppendLine("        - name: clusterAdmin");
        sb.AppendLine("          db: admin");
        sb.AppendLine("        - name: userAdminAnyDatabase");
        sb.AppendLine("          db: admin");
        sb.AppendLine("        - name: readWriteAnyDatabase");
        sb.AppendLine("          db: admin");
        sb.AppendLine("        - name: dbAdminAnyDatabase");
        sb.AppendLine("          db: admin");
        sb.AppendLine("  statefulSet:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      template:");
        sb.AppendLine("        spec:");

        // Spread replica members across nodes so they don't all land on one node
        // (the operator sets no anti-affinity by default). Preferred, so it won't
        // block scheduling when there are fewer nodes than members. The community
        // operator labels each member pod app=<name>-svc.
        sb.AppendLine("          affinity:");
        sb.AppendLine("            podAntiAffinity:");
        sb.AppendLine("              preferredDuringSchedulingIgnoredDuringExecution:");
        sb.AppendLine("                - weight: 100");
        sb.AppendLine("                  podAffinityTerm:");
        sb.AppendLine("                    topologyKey: kubernetes.io/hostname");
        sb.AppendLine("                    labelSelector:");
        sb.AppendLine("                      matchLabels:");
        sb.AppendLine($"                        app: {mongo.Name}-svc");

        // Effective requests. Kubernetes defaults a container's request to its limit
        // when only a limit is set, so a limit-only spec reserves the full limit on a
        // node — over-booking it. Fall back to a modest request so the limit stays a
        // ceiling, not a reservation. (Mongo limits are DB-sized, so these floors fit.)
        string? cpuReq = !string.IsNullOrWhiteSpace(mongo.CpuRequest) ? mongo.CpuRequest
            : !string.IsNullOrWhiteSpace(mongo.CpuLimit) ? "100m" : null;
        string? memReq = !string.IsNullOrWhiteSpace(mongo.MemoryRequest) ? mongo.MemoryRequest
            : !string.IsNullOrWhiteSpace(mongo.MemoryLimit) ? "256Mi" : null;
        bool hasLimits = !string.IsNullOrWhiteSpace(mongo.CpuLimit)
            || !string.IsNullOrWhiteSpace(mongo.MemoryLimit);

        if (cpuReq is not null || memReq is not null || hasLimits)
        {
            sb.AppendLine("          containers:");
            sb.AppendLine("            - name: mongod");
            sb.AppendLine("              resources:");

            if (cpuReq is not null || memReq is not null)
            {
                sb.AppendLine("                requests:");
                if (cpuReq is not null)
                    sb.AppendLine($"                  cpu: \"{cpuReq}\"");
                if (memReq is not null)
                    sb.AppendLine($"                  memory: \"{memReq}\"");
            }

            if (hasLimits)
            {
                sb.AppendLine("                limits:");
                if (!string.IsNullOrWhiteSpace(mongo.CpuLimit))
                    sb.AppendLine($"                  cpu: \"{mongo.CpuLimit}\"");
                if (!string.IsNullOrWhiteSpace(mongo.MemoryLimit))
                    sb.AppendLine($"                  memory: \"{mongo.MemoryLimit}\"");
            }
        }

        sb.AppendLine("      volumeClaimTemplates:");
        sb.AppendLine("        - metadata:");
        sb.AppendLine("            name: data-volume");
        sb.AppendLine("          spec:");
        sb.AppendLine("            accessModes: [\"ReadWriteOnce\"]");
        sb.AppendLine("            resources:");
        sb.AppendLine("              requests:");
        sb.AppendLine($"                storage: {mongo.StorageSize}");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a Kubernetes Job manifest that runs mongodump and uploads the gzip
    /// archive to S3. Uses a two-stage approach: an init container runs mongodump
    /// (authenticating via the admin Secret) into a shared emptyDir volume, then the
    /// main container uploads via aws-cli. Archive stored at {bucket}/{name}/{backupName}.archive.
    /// </summary>
    private static string BuildBackupManifest(
        string backupName, MongoCluster mongo, StorageLink storageLink, string s3SecretName)
    {
        string host = $"{mongo.Name}-svc.{mongo.Namespace}.svc.cluster.local";
        // readPreference=secondaryPreferred: mongodump reads from a secondary when available,
        // so a rolling primary restart (e.g. during an upgrade) doesn't kill the backup job.
        string mongoUri = $"mongodb://admin:${{MONGO_ADMIN_PASSWORD}}@{host}:27017/?authSource=admin&replicaSet={mongo.Name}&readPreference=secondaryPreferred&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=5000&serverSelectionTimeoutMS=60000";
        string s3Path = $"s3://{storageLink.BucketName}/{mongo.Name}/{backupName}.archive";

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {backupName}");
        sb.AppendLine($"  namespace: {mongo.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {mongo.Name}");
        sb.AppendLine($"    entkube.io/backup-type: on-demand");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 0");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: backup-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: dump");
        sb.AppendLine($"          image: {MongoImage(mongo.MongoVersion)}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongodump --uri=\\\"{mongoUri}\\\" --numParallelCollections=1 --gzip --archive=/backup/dump.archive\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {mongo.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: upload");
        sb.AppendLine("          image: amazon/aws-cli");
        sb.AppendLine("          command: [\"aws\", \"s3\", \"cp\", \"/backup/dump.archive\",");
        sb.AppendLine($"            \"{s3Path}\", \"--endpoint-url\", \"{storageLink.Endpoint}\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: AWS_ACCESS_KEY_ID");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: ACCESS_KEY");
        sb.AppendLine("            - name: AWS_SECRET_ACCESS_KEY");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: SECRET_KEY");
        sb.AppendLine("            - name: AWS_DEFAULT_REGION");
        sb.AppendLine($"              value: \"{storageLink.Region ?? "us-east-1"}\"");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a Kubernetes Job manifest that downloads a backup archive from S3 and
    /// runs mongorestore --drop. The init container fetches the archive via aws-cli,
    /// then the main container runs mongorestore (authenticated via the admin Secret).
    /// </summary>
    private static string BuildRestoreManifest(
        string restoreName, MongoCluster mongo, StorageLink storageLink,
        string sourceBackupName, string s3SecretName)
    {
        string host = $"{mongo.Name}-svc.{mongo.Namespace}.svc.cluster.local";
        string mongoUri = $"mongodb://admin:${{MONGO_ADMIN_PASSWORD}}@{host}:27017/?authSource=admin&replicaSet={mongo.Name}&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=10000";
        string mongoshUri = $"mongodb://{host}:27017/?replicaSet={mongo.Name}";
        string s3Path = $"s3://{storageLink.BucketName}/{mongo.Name}/{sourceBackupName}.archive";

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {restoreName}");
        sb.AppendLine($"  namespace: {mongo.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {mongo.Name}");
        sb.AppendLine($"    entkube.io/restore-source: {sourceBackupName}");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 0");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: backup-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: download");
        sb.AppendLine("          image: amazon/aws-cli");
        sb.AppendLine("          command: [\"aws\", \"s3\", \"cp\",");
        sb.AppendLine($"            \"{s3Path}\", \"/backup/dump.archive\", \"--endpoint-url\", \"{storageLink.Endpoint}\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: AWS_ACCESS_KEY_ID");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: ACCESS_KEY");
        sb.AppendLine("            - name: AWS_SECRET_ACCESS_KEY");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: SECRET_KEY");
        sb.AppendLine("            - name: AWS_DEFAULT_REGION");
        sb.AppendLine($"              value: \"{storageLink.Region ?? "us-east-1"}\"");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        sb.AppendLine("        - name: wait-for-mongo");
        sb.AppendLine($"          image: {MongoImage(mongo.MongoVersion)}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"until mongosh \\\"{mongoshUri}\\\" --username admin --password \\\"${{MONGO_ADMIN_PASSWORD}}\\\" --authenticationDatabase admin --eval 'db.adminCommand({{ping:1}})' --quiet; do echo 'waiting for MongoDB...'; sleep 5; done\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {mongo.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: restore");
        sb.AppendLine($"          image: {MongoImage(mongo.MongoVersion)}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongorestore --uri=\\\"{mongoUri}\\\" --gzip --archive=/backup/dump.archive --drop --numInsertionWorkersPerCollection=1 --batchSize=100\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {mongo.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a restore Job that downloads a backup from the SOURCE cluster's S3 path
    /// and restores it into the TARGET cluster's service. Used for "restore to new cluster"
    /// where the source and target are different clusters.
    /// </summary>
    private static string BuildCrossClusterRestoreManifest(
        string restoreName, MongoCluster targetCluster, string sourceClusterName,
        StorageLink storageLink, string sourceBackupName, string s3SecretName)
    {
        string host = $"{targetCluster.Name}-svc.{targetCluster.Namespace}.svc.cluster.local";
        string mongoUri = $"mongodb://admin:${{MONGO_ADMIN_PASSWORD}}@{host}:27017/?authSource=admin&replicaSet={targetCluster.Name}&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=10000";
        string mongoshUri = $"mongodb://{host}:27017/?replicaSet={targetCluster.Name}";
        string s3Path = $"s3://{storageLink.BucketName}/{sourceClusterName}/{sourceBackupName}.archive";

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {restoreName}");
        sb.AppendLine($"  namespace: {targetCluster.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {targetCluster.Name}");
        sb.AppendLine($"    entkube.io/restore-source: {sourceBackupName}");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 2");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: backup-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: download");
        sb.AppendLine("          image: amazon/aws-cli");
        sb.AppendLine("          command: [\"aws\", \"s3\", \"cp\",");
        sb.AppendLine($"            \"{s3Path}\", \"/backup/dump.archive\", \"--endpoint-url\", \"{storageLink.Endpoint}\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: AWS_ACCESS_KEY_ID");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: ACCESS_KEY");
        sb.AppendLine("            - name: AWS_SECRET_ACCESS_KEY");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: SECRET_KEY");
        sb.AppendLine("            - name: AWS_DEFAULT_REGION");
        sb.AppendLine($"              value: \"{storageLink.Region ?? "us-east-1"}\"");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        sb.AppendLine("        - name: wait-for-mongo");
        sb.AppendLine($"          image: {MongoImage(targetCluster.MongoVersion)}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"until mongosh \\\"{mongoshUri}\\\" --username admin --password \\\"${{MONGO_ADMIN_PASSWORD}}\\\" --authenticationDatabase admin --eval 'db.adminCommand({{ping:1}})' --quiet; do echo 'waiting for MongoDB...'; sleep 5; done\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {targetCluster.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: restore");
        sb.AppendLine($"          image: {MongoImage(targetCluster.MongoVersion)}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongorestore --uri=\\\"{mongoUri}\\\" --gzip --archive=/backup/dump.archive --drop --numInsertionWorkersPerCollection=1 --batchSize=100\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {targetCluster.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a restore Job that downloads a backup from S3 and runs mongorestore
    /// against an external MongoDB URI. The URI must include credentials if auth is
    /// required (e.g. mongodb://user:pass@host:27017/). No cluster-local admin
    /// secret is needed — the URI carries all authentication information.
    /// </summary>
    private static string BuildExternalRestoreManifest(
        string restoreName, MongoCluster sourceCluster, StorageLink storageLink,
        string sourceBackupName, string s3SecretName, string externalMongoUri)
    {
        string s3Path = $"s3://{storageLink.BucketName}/{sourceCluster.Name}/{sourceBackupName}.archive";

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {restoreName}");
        sb.AppendLine($"  namespace: {sourceCluster.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {sourceCluster.Name}");
        sb.AppendLine($"    entkube.io/restore-type: external");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 0");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: backup-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: download");
        sb.AppendLine("          image: amazon/aws-cli");
        sb.AppendLine("          command: [\"aws\", \"s3\", \"cp\",");
        sb.AppendLine($"            \"{s3Path}\", \"/backup/dump.archive\", \"--endpoint-url\", \"{storageLink.Endpoint}\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: AWS_ACCESS_KEY_ID");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: ACCESS_KEY");
        sb.AppendLine("            - name: AWS_SECRET_ACCESS_KEY");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {s3SecretName}");
        sb.AppendLine("                  key: SECRET_KEY");
        sb.AppendLine("            - name: AWS_DEFAULT_REGION");
        sb.AppendLine($"              value: \"{storageLink.Region ?? "us-east-1"}\"");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: restore");
        sb.AppendLine($"          image: {MongoImage(sourceCluster.MongoVersion)}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongorestore --uri=\\\"{externalMongoUri}\\\" --gzip --archive=/backup/dump.archive --drop --numInsertionWorkersPerCollection=1 --batchSize=100\"]");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a CronJob manifest that runs mongodump on a schedule and uploads
    /// each archive to S3 at {bucket}/{name}/scheduled-{timestamp}.archive.
    /// The CronJob uses the mongo image matching the cluster version so upgrades
    /// keep the dump tool and server in sync.
    /// </summary>
    private static string BuildScheduledBackupCronJobManifest(
        MongoCluster mongo, StorageLink storageLink, string s3SecretName)
    {
        string host = $"{mongo.Name}-svc.{mongo.Namespace}.svc.cluster.local";
        string mongoUri = $"mongodb://admin:${{MONGO_ADMIN_PASSWORD}}@{host}:27017/?authSource=admin&replicaSet={mongo.Name}&readPreference=secondaryPreferred&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=5000&serverSelectionTimeoutMS=60000";
        string s3Base = $"s3://{storageLink.BucketName}/{mongo.Name}";

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: CronJob");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {mongo.Name}-scheduled-backup");
        sb.AppendLine($"  namespace: {mongo.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {mongo.Name}");
        sb.AppendLine("spec:");
        // CNPG uses a 6-field cron (seconds included); Kubernetes CronJob only accepts 5 fields.
        // Drop the leading seconds field when the schedule has 6 parts.
        string cronSchedule = mongo.BackupSchedule!;
        string[] parts = cronSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 6) cronSchedule = string.Join(' ', parts[1..]);

        sb.AppendLine($"  schedule: \"{cronSchedule}\"");
        sb.AppendLine("  concurrencyPolicy: Forbid");
        sb.AppendLine("  jobTemplate:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.AppendLine($"        entkube.io/mongo-cluster: {mongo.Name}");
        sb.AppendLine("    spec:");
        sb.AppendLine("      backoffLimit: 0");
        sb.AppendLine("      ttlSecondsAfterFinished: 86400");
        sb.AppendLine("      template:");
        sb.AppendLine("        spec:");
        sb.AppendLine("          restartPolicy: Never");
        sb.AppendLine("          volumes:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              emptyDir: {}");
        sb.AppendLine("          initContainers:");
        sb.AppendLine("            - name: dump");
        sb.AppendLine($"              image: {MongoImage(mongo.MongoVersion)}");
        sb.AppendLine("              command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"              args: [\"mongodump --uri=\\\"{mongoUri}\\\" --numParallelCollections=1 --gzip --archive=/backup/dump.archive\"]");
        sb.AppendLine("              env:");
        sb.AppendLine("                - name: MONGO_ADMIN_PASSWORD");
        sb.AppendLine("                  valueFrom:");
        sb.AppendLine("                    secretKeyRef:");
        sb.AppendLine($"                      name: {mongo.Name}-admin-password");
        sb.AppendLine("                      key: password");
        sb.AppendLine("              volumeMounts:");
        sb.AppendLine("                - name: backup-data");
        sb.AppendLine("                  mountPath: /backup");
        sb.AppendLine("          containers:");
        sb.AppendLine("            - name: upload");
        sb.AppendLine("              image: amazon/aws-cli");
        sb.AppendLine("              command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"              args: [\"aws s3 cp /backup/dump.archive {s3Base}/scheduled-$(date -u +%Y%m%dT%H%M%SZ).archive --endpoint-url {storageLink.Endpoint}\"]");
        sb.AppendLine("              env:");
        sb.AppendLine("                - name: AWS_ACCESS_KEY_ID");
        sb.AppendLine("                  valueFrom:");
        sb.AppendLine("                    secretKeyRef:");
        sb.AppendLine($"                      name: {s3SecretName}");
        sb.AppendLine("                      key: ACCESS_KEY");
        sb.AppendLine("                - name: AWS_SECRET_ACCESS_KEY");
        sb.AppendLine("                  valueFrom:");
        sb.AppendLine("                    secretKeyRef:");
        sb.AppendLine($"                      name: {s3SecretName}");
        sb.AppendLine("                      key: SECRET_KEY");
        sb.AppendLine("                - name: AWS_DEFAULT_REGION");
        sb.AppendLine($"                  value: \"{storageLink.Region ?? "us-east-1"}\"");
        sb.AppendLine("              volumeMounts:");
        sb.AppendLine("                - name: backup-data");
        sb.AppendLine("                  mountPath: /backup");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a migration Job that uses the same credential convention as managed clusters:
    /// source dump authenticates via {source.Name}-admin-password, restore authenticates
    /// via {target.Name}-admin-password. Both secrets must exist on the source K8s cluster
    /// (the Job runs there). The target host is reached via its service FQDN.
    /// </summary>
    private static string BuildMigrateFromExternalManifest(
        string jobName, MongoCluster source, MongoCluster target)
    {
        string sourceImage = MongoImage(source.MongoVersion);
        string targetImage = MongoImage(target.MongoVersion);

        string sourceHost = $"{source.Name}-svc.{source.Namespace}.svc.cluster.local";
        string sourceUri = $"mongodb://admin:${{SOURCE_ADMIN_PASSWORD}}@{sourceHost}:27017/?authSource=admin&replicaSet={source.Name}&readPreference=secondaryPreferred&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=5000&serverSelectionTimeoutMS=60000";

        string targetHost = $"{target.Name}-svc.{target.Namespace}.svc.cluster.local";
        string targetUri = $"mongodb://admin:${{TARGET_ADMIN_PASSWORD}}@{targetHost}:27017/?authSource=admin&replicaSet={target.Name}&socketTimeoutMS=0&connectTimeoutMS=30000&heartbeatFrequencyMS=5000&serverSelectionTimeoutMS=60000";

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {jobName}");
        sb.AppendLine($"  namespace: {source.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    entkube.io/mongo-cluster: {source.Name}");
        sb.AppendLine($"    entkube.io/migration-target: {target.Name}");
        string sourceWaitUri2 = $"mongodb://admin:${{SOURCE_ADMIN_PASSWORD}}@{sourceHost}:27017/?authSource=admin&replicaSet={source.Name}&connectTimeoutMS=10000&serverSelectionTimeoutMS=15000";

        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 2");
        sb.AppendLine("  ttlSecondsAfterFinished: 86400");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: backup-data");
        sb.AppendLine("          emptyDir: {}");
        sb.AppendLine("      initContainers:");
        sb.AppendLine("        - name: wait-for-source");
        sb.AppendLine($"          image: {sourceImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"until mongosh \\\"{sourceWaitUri2}\\\" --username admin --password \\\"${{SOURCE_ADMIN_PASSWORD}}\\\" --authenticationDatabase admin --eval 'db.adminCommand({{ping:1}})' --quiet; do echo 'waiting for source...'; sleep 10; done\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: SOURCE_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {source.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("        - name: dump-from-source");
        sb.AppendLine($"          image: {sourceImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongodump --uri=\\\"{sourceUri}\\\" --numParallelCollections=1 --gzip --archive=/backup/dump.archive\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: SOURCE_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {source.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: restore-to-managed");
        sb.AppendLine($"          image: {targetImage}");
        sb.AppendLine("          command: [\"/bin/sh\", \"-c\"]");
        sb.AppendLine($"          args: [\"mongorestore --uri=\\\"{targetUri}\\\" --gzip --archive=/backup/dump.archive --drop --numInsertionWorkersPerCollection=1 --batchSize=100\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: TARGET_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {target.Name}-admin-password");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: backup-data");
        sb.AppendLine("              mountPath: /backup");
        return sb.ToString();
    }

    // ──────── JSON Parsing ────────

    /// <summary>
    /// Parses the MongoDBCommunity CRD JSON to extract phase and ready member count.
    /// Community Operator status fields: status.phase, status.currentMongoDBMembers.
    /// </summary>
    /// <summary>
    /// Persists version and databases discovered from the live MongoDBCommunity CRD into
    /// the EntKube database, so the cluster header shows them without a live K8s query.
    /// Only updates fields that are still at their "unknown" placeholder values, so manual
    /// edits are not overwritten on subsequent loads.
    /// </summary>
    private async Task PersistDiscoveredMetadataAsync(
        MongoCluster cluster, MongoClusterDetail detail, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MongoCluster? record = await db.MongoClusters
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == cluster.Id, ct);

        if (record is null)
        {
            return;
        }

        bool changed = false;

        if (!string.IsNullOrEmpty(detail.Version)
            && (record.MongoVersion == "unknown" || record.MongoVersion != detail.Version))
        {
            record.MongoVersion = detail.Version;
            // Keep the in-memory cluster in sync so the UI reflects it immediately.
            cluster.MongoVersion = detail.Version;
            changed = true;
        }

        // Create MongoDatabase records for any databases discovered in spec.users that
        // are not already tracked. Existing databases are left untouched.
        HashSet<string> existingNames = record.Databases
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach ((string dbName, string owner) in detail.DiscoveredDatabases)
        {
            if (existingNames.Contains(dbName))
            {
                continue;
            }

            MongoDatabase newDb = new()
            {
                Id = Guid.NewGuid(),
                MongoClusterId = record.Id,
                Name = dbName,
                Owner = string.IsNullOrEmpty(owner) ? dbName : owner,
                Status = MongoDatabaseStatus.Ready
            };

            db.Set<MongoDatabase>().Add(newDb);
            cluster.Databases.Add(newDb);
            existingNames.Add(dbName);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static void ParseClusterStatus(string json, MongoClusterDetail detail)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        if (root.TryGetProperty("status", out System.Text.Json.JsonElement status))
        {
            if (status.TryGetProperty("phase", out System.Text.Json.JsonElement phase))
            {
                detail.Phase = phase.GetString() ?? "Unknown";
            }

            if (status.TryGetProperty("currentMongoDBMembers", out System.Text.Json.JsonElement members))
            {
                detail.ReadyMembers = members.GetInt32();
            }

            if (status.TryGetProperty("message", out System.Text.Json.JsonElement messageEl))
            {
                detail.Message = messageEl.GetString();
            }

            // Fall back to the most recent condition message if no top-level message.
            if (string.IsNullOrEmpty(detail.Message)
                && status.TryGetProperty("conditions", out System.Text.Json.JsonElement conditions)
                && conditions.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement condition in conditions.EnumerateArray())
                {
                    if (condition.TryGetProperty("message", out System.Text.Json.JsonElement condMsg))
                    {
                        string? msg = condMsg.GetString();
                        if (!string.IsNullOrEmpty(msg))
                        {
                            detail.Message = msg;
                            break;
                        }
                    }
                }
            }
        }

        // Read spec.version (e.g. "6.0.5") and spec.users to discover databases.
        if (root.TryGetProperty("spec", out System.Text.Json.JsonElement spec))
        {
            if (spec.TryGetProperty("version", out System.Text.Json.JsonElement versionEl))
            {
                detail.Version = versionEl.GetString();
            }

            // spec.users[].roles[].db reveals which databases each user has access to.
            // Skip system databases that are always present.
            HashSet<string> systemDbs = ["admin", "local", "config"];

            if (spec.TryGetProperty("users", out System.Text.Json.JsonElement users)
                && users.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.Json.JsonElement user in users.EnumerateArray())
                {
                    string? userName = user.TryGetProperty("name", out System.Text.Json.JsonElement nameEl2)
                        ? nameEl2.GetString() : null;

                    if (!user.TryGetProperty("roles", out System.Text.Json.JsonElement roles)
                        || roles.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (System.Text.Json.JsonElement role in roles.EnumerateArray())
                    {
                        if (!role.TryGetProperty("db", out System.Text.Json.JsonElement dbEl))
                        {
                            continue;
                        }

                        string? dbName = dbEl.GetString();

                        if (string.IsNullOrEmpty(dbName) || systemDbs.Contains(dbName) || !seen.Add(dbName))
                        {
                            continue;
                        }

                        detail.DiscoveredDatabases.Add((dbName, userName ?? ""));
                    }
                }
            }
        }

        // Derive the endpoint from the resource metadata name, which is stored on the cluster record.
        if (root.TryGetProperty("metadata", out System.Text.Json.JsonElement metadata)
            && metadata.TryGetProperty("name", out System.Text.Json.JsonElement nameEl)
            && metadata.TryGetProperty("namespace", out System.Text.Json.JsonElement nsEl))
        {
            detail.Endpoint = $"{nameEl.GetString()}-svc.{nsEl.GetString()}.svc.cluster.local:27017";
        }
    }

    /// <summary>
    /// Parses a kubectl "get pods -o json" response into a list of MongoPodInfo.
    /// </summary>
    private static List<MongoPodInfo> ParsePodList(string json, string clusterName)
    {
        List<MongoPodInfo> pods = [];
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("items", out System.Text.Json.JsonElement items))
        {
            return pods;
        }

        string prefix = clusterName + "-";

        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            string podName = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";

            // Only include StatefulSet member pods: {clusterName}-0, {clusterName}-1, ...
            // Skip Jobs, init containers, and any other pods in the namespace.
            if (!podName.StartsWith(prefix)) continue;
            string ordinal = podName[prefix.Length..];
            if (ordinal.Length == 0 || !ordinal.All(char.IsDigit)) continue;
            string podStatus = "Unknown";
            bool ready = false;
            string? node = null;
            DateTime? startTime = null;
            int restarts = 0;

            if (item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                && spec.TryGetProperty("nodeName", out System.Text.Json.JsonElement nodeName))
            {
                node = nodeName.GetString();
            }

            if (item.TryGetProperty("status", out System.Text.Json.JsonElement statusEl))
            {
                if (statusEl.TryGetProperty("phase", out System.Text.Json.JsonElement phaseEl))
                {
                    podStatus = phaseEl.GetString() ?? "Unknown";
                }

                if (statusEl.TryGetProperty("startTime", out System.Text.Json.JsonElement startEl))
                {
                    if (DateTime.TryParse(startEl.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                    {
                        startTime = parsed;
                    }
                }

                if (statusEl.TryGetProperty("containerStatuses", out System.Text.Json.JsonElement containers))
                {
                    foreach (System.Text.Json.JsonElement container in containers.EnumerateArray())
                    {
                        if (container.TryGetProperty("restartCount", out System.Text.Json.JsonElement rc))
                        {
                            restarts += rc.GetInt32();
                        }

                        if (container.TryGetProperty("ready", out System.Text.Json.JsonElement readyEl))
                        {
                            ready = ready || readyEl.GetBoolean();
                        }
                    }
                }
            }

            pods.Add(new MongoPodInfo
            {
                Name = podName,
                Status = podStatus,
                Ready = ready,
                Node = node,
                StartTime = startTime,
                Restarts = restarts
            });
        }

        return pods;
    }
}

/// <summary>
/// Detail view model for a managed MongoDB cluster.
/// </summary>
public class MongoClusterDetail
{
    public required MongoCluster Cluster { get; set; }
    public string Phase { get; set; } = "Unknown";
    public string? Message { get; set; }
    public int ReadyMembers { get; set; }
    public string? Endpoint { get; set; }
    public string? Version { get; set; }
    /// <summary>Databases discovered from spec.users roles in the MongoDBCommunity CRD.</summary>
    public List<(string DbName, string Owner)> DiscoveredDatabases { get; set; } = [];
    public List<MongoPodInfo> Pods { get; set; } = [];
    public List<MongoBackup> Backups { get; set; } = [];
    public string? SyncError { get; set; }
}

/// <summary>
/// Pod information for a MongoDB cluster member.
/// </summary>
public class MongoPodInfo
{
    public required string Name { get; set; }
    public string Status { get; set; } = "Unknown";
    public bool Ready { get; set; }
    public string? Node { get; set; }
    public DateTime? StartTime { get; set; }
    public int Restarts { get; set; }
}
