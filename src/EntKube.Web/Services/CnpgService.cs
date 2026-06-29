using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages the full lifecycle of CloudNativePG clusters — from creation through
/// backup, restore, upgrade, database management, and deletion. Each operation
/// translates high-level intent into Kubernetes CRD manifests applied to the
/// cluster where the CNPG operator is running.
///
/// The service owns the CnpgCluster, CnpgDatabase, and CnpgBackup records in the
/// database. It coordinates with VaultService to store database credentials and
/// tag them for Kubernetes sync so applications can consume them as K8s Secrets.
/// </summary>
public class CnpgService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IKubernetesClientFactory k8sFactory)
{
    // ──────── Cluster Lifecycle ────────

    /// <summary>
    /// Creates a new managed CNPG cluster. First validates that the CNPG operator
    /// is installed on the target cluster, then generates the Cluster CRD manifest
    /// and applies it to Kubernetes. If a storage link is provided, configures
    /// Barman backup with WAL archiving to the selected S3 bucket.
    /// </summary>
    public async Task<CnpgCluster> CreateClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        int instances,
        string storageSize,
        Guid? storageLinkId,
        string? backupSchedule,
        int retentionDays = 30,
        int maxBackups = 20,
        string postgresVersion = "18",
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Verify the CNPG operator is installed on the target cluster.

        bool operatorInstalled = await db.ClusterComponents
            .AnyAsync(c => c.ClusterId == kubernetesClusterId
                && c.Name == "cloudnative-pg"
                && c.Status == ComponentStatus.Installed, ct);

        if (!operatorInstalled)
        {
            throw new InvalidOperationException(
                "CloudNativePG operator is not installed on this cluster. Install it first from the Components tab.");
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

        CnpgCluster cnpgCluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            PostgresVersion = string.IsNullOrWhiteSpace(postgresVersion) ? "18" : postgresVersion.Trim(),
            Instances = instances,
            StorageSize = storageSize,
            StorageLinkId = storageLinkId,
            BackupSchedule = backupSchedule,
            RetentionDays = retentionDays,
            MaxBackups = maxBackups,
            Status = CnpgClusterStatus.Creating
        };

        db.CnpgClusters.Add(cnpgCluster);
        await db.SaveChangesAsync(ct);

        // If backup storage is configured, ensure the S3 credentials are synced
        // to Kubernetes so the CNPG operator can access the backup bucket.

        string? s3SecretName = null;

        if (storageLink is not null)
        {
            s3SecretName = $"{name}-s3-credentials";
        }

        // Generate and apply the CNPG Cluster manifest.

        string manifest = BuildClusterManifest(cnpgCluster, storageLink, s3SecretName);

        // Ensure the target namespace exists before applying any resources.

        await k8sFactory.EnsureNamespaceAsync(ns, k8sCluster.Kubeconfig!, ct);

        // Now that the namespace exists, create the S3 credentials Secret.

        if (storageLink is not null && s3SecretName is not null)
        {
            await EnsureStorageSecretsInK8sAsync(
                tenantId, storageLink, s3SecretName, ns, k8sCluster.Kubeconfig!, ct);
        }

        await k8sFactory.ApplyManifestAsync(manifest, k8sCluster.Kubeconfig!, ct);

        // If backup storage is configured, apply the ObjectStore then the ScheduledBackup
        // as separate calls so each failure surfaces a precise error.

        if (storageLink is not null && s3SecretName is not null)
        {
            string objectStoreManifest = BuildObjectStoreManifest(name, ns, storageLink, s3SecretName, retentionDays);

            try
            {
                await k8sFactory.ApplyManifestAsync(objectStoreManifest, k8sCluster.Kubeconfig!, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no matches for kind"))
            {
                throw new InvalidOperationException(
                    "Cluster created but the ObjectStore could not be applied — the Barman Cloud Plugin " +
                    "CRD (barmancloud.cnpg.io/ObjectStore) is not available. Ensure the Barman Cloud " +
                    "Plugin toggle is enabled on the CloudNativePG component and CNPG ≥ 1.24 is " +
                    "installed. Then delete and recreate the cluster.");
            }

            if (!string.IsNullOrWhiteSpace(backupSchedule))
            {
                string scheduledBackupManifest = BuildScheduledBackupManifest(name, ns, backupSchedule);

                try
                {
                    await k8sFactory.ApplyManifestAsync(scheduledBackupManifest, k8sCluster.Kubeconfig!, ct);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        $"Cluster and ObjectStore created but the ScheduledBackup could not be applied. " +
                        $"Ensure CNPG ≥ 1.24 is installed and the backup schedule is a valid 6-field " +
                        $"cron expression (e.g. \"0 0 2 * * *\"). kubectl error: {ex.Message}");
                }
            }
        }

        return cnpgCluster;
    }

    /// <summary>
    /// Deletes a managed CNPG cluster from Kubernetes and removes all associated
    /// records (databases, backups, vault secrets).
    /// </summary>
    public async Task DeleteClusterAsync(Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        // Attempt to delete the K8s resources. If the cluster never actually deployed
        // or the K8s API is unreachable, we still proceed with removing the DB record
        // so the user isn't stuck with an un-deletable entry.

        try
        {
            await k8sFactory.DeleteManifestAsync(
                "clusters.postgresql.cnpg.io", cnpg.Name, cnpg.Namespace,
                cnpg.KubernetesCluster.Kubeconfig!, ct);

            await k8sFactory.DeleteManifestAsync(
                "objectstores.barmancloud.cnpg.io", $"{cnpg.Name}-object-store",
                cnpg.Namespace, cnpg.KubernetesCluster.Kubeconfig!, ct);

            if (!string.IsNullOrWhiteSpace(cnpg.BackupSchedule))
            {
                await k8sFactory.DeleteManifestAsync(
                    "scheduledbackups.postgresql.cnpg.io", $"{cnpg.Name}-scheduled",
                    cnpg.Namespace, cnpg.KubernetesCluster.Kubeconfig!, ct);
            }
        }
        catch (Exception)
        {
            // K8s operations failed — the cluster may never have been created,
            // the kubeconfig may be invalid, or the API server is unreachable.
            // We proceed with local cleanup regardless.
        }

        // Remove vault secrets for all databases in this cluster.

        foreach (CnpgDatabase database in cnpg.Databases)
        {
            await DeleteDatabaseSecretsAsync(tenantId, database.Id, ct);
        }

        // Remove the cluster record (cascades to databases and backups).

        db.CnpgClusters.Remove(cnpg);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Registers an existing CNPG cluster (already running in Kubernetes) with EntKube.
    /// Reads the live Cluster CRD to discover version, instances, and storage size when
    /// not provided, then creates a database record with IsExternal=true and Status=Running.
    /// No Kubernetes resources are created or modified.
    /// </summary>
    public async Task<CnpgCluster> ImportClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster k8sCluster = await db.KubernetesClusters
            .FirstAsync(k => k.Id == kubernetesClusterId, ct);

        // Query the live Cluster CRD to discover its configuration.
        string postgresVersion = "17";
        int instances = 1;
        string storageSize = "10Gi";

        try
        {
            string clusterJson = await k8sFactory.GetJsonAsync(
                $"cluster.postgresql.cnpg.io/{name}", ns, k8sCluster.Kubeconfig!, ct: ct);

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(clusterJson);
            System.Text.Json.JsonElement root = doc.RootElement;

            if (root.TryGetProperty("spec", out System.Text.Json.JsonElement spec))
            {
                // imageName is like "ghcr.io/cloudnative-pg/postgresql:17.2"
                if (spec.TryGetProperty("imageName", out System.Text.Json.JsonElement imgEl))
                {
                    string img = imgEl.GetString() ?? "";
                    int colon = img.LastIndexOf(':');
                    if (colon >= 0)
                    {
                        string tag = img[(colon + 1)..];
                        int dot = tag.IndexOf('.');
                        postgresVersion = dot > 0 ? tag[..dot] : tag;
                    }
                }

                if (spec.TryGetProperty("instances", out System.Text.Json.JsonElement instEl))
                    instances = instEl.GetInt32();

                if (spec.TryGetProperty("storage", out System.Text.Json.JsonElement storEl)
                    && storEl.TryGetProperty("size", out System.Text.Json.JsonElement sizeEl))
                    storageSize = sizeEl.GetString() ?? storageSize;
            }
        }
        catch
        {
            // If the CRD query fails the cluster may not exist or the kubeconfig lacks
            // permission — surface a clear error rather than silently using defaults.
            throw new InvalidOperationException(
                $"Could not read CNPG Cluster '{name}' in namespace '{ns}'. " +
                "Ensure the cluster exists and the kubeconfig has read access to clusters.postgresql.cnpg.io.");
        }

        CnpgCluster cnpgCluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            PostgresVersion = postgresVersion,
            Instances = instances,
            StorageSize = storageSize,
            IsExternal = true,
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpgCluster);
        await db.SaveChangesAsync(ct);

        // Pull the superuser secret into vault so credentials are immediately available.
        try
        {
            await FetchSuperuserCredentialsAsync(tenantId, cnpgCluster.Id, ct);
        }
        catch
        {
            // Non-fatal: the cluster is registered even if the superuser secret is missing
            // (e.g. kubeconfig lacks Secret read access). The user can fetch it manually.
        }

        return cnpgCluster;
    }

    /// <summary>
    /// Removes an externally registered CNPG cluster from EntKube without touching
    /// any Kubernetes resources. Also removes database records and vault secrets for
    /// all databases in the cluster, but does NOT drop databases or roles in PostgreSQL.
    /// </summary>
    public async Task UnregisterClusterAsync(Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        foreach (CnpgDatabase database in cnpg.Databases)
        {
            await DeleteDatabaseSecretsAsync(tenantId, database.Id, ct);
        }

        db.CnpgClusters.Remove(cnpg);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Import existing databases ────────

    /// <summary>
    /// Queries the live CNPG cluster primary pod for non-system databases and returns
    /// each database name with its owner role. Uses psql -t -A so output is one
    /// "name|owner" entry per line with no headers.
    /// </summary>
    public async Task<List<(string Name, string Owner)>> DiscoverDatabasesAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        const string sql = """
            SELECT datname || '|' || pg_catalog.pg_get_userbyid(datdba)
            FROM pg_catalog.pg_database
            WHERE datistemplate = false
              AND datname NOT IN ('postgres', 'template0', 'template1')
            ORDER BY datname;
            """;

        string output = await k8sFactory.ExecuteSqlInCnpgDatabaseWithOutputAsync(
            cnpg.Name, cnpg.Namespace, "postgres", sql, cnpg.KubernetesCluster.Kubeconfig!, ct);

        List<(string Name, string Owner)> result = [];

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            int pipe = trimmed.IndexOf('|');
            if (pipe > 0)
                result.Add((trimmed[..pipe], trimmed[(pipe + 1)..]));
        }

        return result;
    }

    /// <summary>
    /// Registers an existing PostgreSQL database (already present in the CNPG cluster)
    /// with EntKube. Creates a CnpgDatabase record with Status=Ready and stores the
    /// provided credentials in vault without running any CREATE DATABASE/ROLE SQL.
    /// </summary>
    public async Task<CnpgDatabase> ImportDatabaseAsync(
        Guid tenantId, Guid cnpgClusterId,
        string databaseName, string ownerRole, string password,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cnpg.Id,
            Name = databaseName,
            Owner = ownerRole,
            Status = CnpgDatabaseStatus.Ready
        };

        db.CnpgDatabases.Add(database);
        await db.SaveChangesAsync(ct);

        string k8sSecretName = $"{cnpg.Name}-{databaseName}-credentials";
        string host = $"{cnpg.Name}-rw.{cnpg.Namespace}.svc.cluster.local";

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "HOST", host, k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PORT", "5432", k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "DATABASE", databaseName, k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "USERNAME", ownerRole, k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PASSWORD", password, k8sSecretName, cnpg.Namespace, ct);

        return database;
    }

    /// <summary>
    /// Upgrades a CNPG cluster to a new PostgreSQL major version.
    /// Updates the imageName in the Cluster CRD — CNPG handles the rolling upgrade.
    /// </summary>
    public async Task UpgradeClusterAsync(
        Guid tenantId, Guid cnpgClusterId, string targetVersion, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        // CloudNativePG supports zero-downtime rolling updates for minor version
        // changes (e.g. 16.2 → 16.4). Major version upgrades (e.g. 16 → 17) require
        // a new cluster + restore because PostgreSQL data formats are incompatible.
        // We block major version jumps here—use restore-to-new-cluster instead.

        int currentMajor = ExtractMajorVersion(cnpg.PostgresVersion);
        int targetMajor = ExtractMajorVersion(targetVersion);

        if (targetMajor != currentMajor)
        {
            throw new InvalidOperationException(
                $"Major version upgrades ({currentMajor} → {targetMajor}) are not supported in-place. " +
                "Use point-in-time restore to create a new cluster with the target version.");
        }

        cnpg.PostgresVersion = targetVersion;
        cnpg.Status = CnpgClusterStatus.Upgrading;
        await db.SaveChangesAsync(ct);

        // Re-apply the manifest with the updated version. CNPG will perform a rolling
        // update: replicas first, then switchover to a promoted replica, then update
        // the old primary—zero downtime for clients using the -rw service endpoint.

        string s3SecretName = cnpg.StorageLinkId.HasValue ? $"{cnpg.Name}-s3-credentials" : null!;
        string manifest = BuildClusterManifest(cnpg, cnpg.StorageLink, s3SecretName);

        await k8sFactory.ApplyManifestAsync(manifest, cnpg.KubernetesCluster.Kubeconfig!, ct);
    }

    /// <summary>
    /// Triggers a rolling restart of a CNPG cluster. Sets the
    /// <c>kubectl.kubernetes.io/restartedAt</c> annotation on the Cluster resource — the same
    /// mechanism as <c>kubectl cnpg restart</c>. CloudNativePG orchestrates the rollout
    /// (replicas first, then a switchover before recreating the old primary), which also lets
    /// the scheduler re-place pods according to the cluster's anti-affinity.
    /// </summary>
    public async Task RestartClusterAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        string patch =
            $"{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}}}}}";

        await k8sFactory.PatchJsonAsync(
            "cluster.postgresql.cnpg.io", cnpg.Name, cnpg.Namespace, patch,
            cnpg.KubernetesCluster.Kubeconfig!, ct);
    }

    /// <summary>
    /// Performs a major version upgrade by restoring to a new cluster with the target
    /// PostgreSQL version, then swapping the service name so existing clients reconnect
    /// automatically, and finally removing the old cluster.
    ///
    /// The flow is:
    /// 1. Take a fresh backup of the source cluster (ensures we have the latest WALs).
    /// 2. Create a new cluster with the target version bootstrapping from that backup.
    /// 3. Once the new cluster is ready, rename it to take over the original name.
    ///    K8s services like "{name}-rw" will then point to the new cluster—zero DNS changes needed.
    /// 4. Delete the old cluster resources from Kubernetes.
    /// 5. Transfer all database records and vault secrets to the new cluster.
    ///
    /// NOTE: There is a brief window of downtime (~10-30s) between deleting the old cluster
    /// and the new cluster's services becoming available under the original name. For true
    /// zero-downtime major upgrades, use logical replication instead.
    /// </summary>
    public async Task<CnpgCluster> MajorUpgradeAsync(
        Guid tenantId, Guid sourceCnpgClusterId, string targetVersion,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster source = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .Include(c => c.Databases)
            .FirstOrDefaultAsync(c => c.Id == sourceCnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Source CNPG cluster not found.");

        if (source.StorageLink is null)
        {
            throw new InvalidOperationException(
                "Major version upgrade requires backup storage. Assign an S3 bucket first.");
        }

        int sourceMajor = ExtractMajorVersion(source.PostgresVersion);
        int targetMajor = ExtractMajorVersion(targetVersion);

        if (targetMajor <= sourceMajor)
        {
            throw new InvalidOperationException(
                $"Target version ({targetVersion}) must be a higher major version than current ({source.PostgresVersion}).");
        }

        // Step 1: Take a fresh backup so the restore includes the latest data.

        source.Status = CnpgClusterStatus.Upgrading;
        await db.SaveChangesAsync(ct);

        string backupName = $"{source.Name}-pre-upgrade-{DateTime.UtcNow:yyyyMMddHHmmss}";
        string backupManifest = BuildBackupManifest(backupName, source.Name, source.Namespace);
        await k8sFactory.ApplyManifestAsync(backupManifest, source.KubernetesCluster.Kubeconfig!, ct);

        // Step 2: Create the new cluster with a temporary name, targeting the new major version.
        // It bootstraps from the source's Barman backup (latest point in time).

        string tempName = $"{source.Name}-v{targetMajor}";

        CnpgCluster upgraded = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = source.KubernetesClusterId,
            Name = tempName,
            Namespace = source.Namespace,
            PostgresVersion = targetVersion,
            Instances = source.Instances,
            StorageSize = source.StorageSize,
            StorageLinkId = source.StorageLinkId,
            BackupSchedule = source.BackupSchedule,
            Status = CnpgClusterStatus.Restoring
        };

        db.CnpgClusters.Add(upgraded);
        await db.SaveChangesAsync(ct);

        string s3SecretName = $"{tempName}-s3-credentials";
        await EnsureStorageSecretsInK8sAsync(
            tenantId, source.StorageLink, s3SecretName, source.Namespace, source.KubernetesCluster.Kubeconfig!, ct);

        // Use current time as recovery target — we want the very latest state.

        string restoreManifest = BuildRestoreManifest(upgraded, source, DateTime.UtcNow, s3SecretName);
        restoreManifest += "\n---\n" + BuildObjectStoreManifest(tempName, source.Namespace, source.StorageLink, s3SecretName, source.RetentionDays);
        await k8sFactory.ApplyManifestAsync(restoreManifest, source.KubernetesCluster.Kubeconfig!, ct);

        // Step 3: Delete the old cluster from Kubernetes to free the name.

        await k8sFactory.DeleteManifestAsync(
            "Cluster", source.Name, source.Namespace, source.KubernetesCluster.Kubeconfig!, ct);

        // Step 4: Transfer databases and remove the old cluster record from the DB.
        // Must happen before renaming the new cluster, because the unique index
        // (KubernetesClusterId, Name, Namespace) would conflict otherwise.

        foreach (CnpgDatabase database in source.Databases)
        {
            database.CnpgClusterId = upgraded.Id;
        }

        db.CnpgClusters.Remove(source);
        await db.SaveChangesAsync(ct);

        // Step 5: Rename the new cluster to the original name so that the K8s services
        // ({name}-rw, {name}-ro, {name}-r) resolve to the upgraded cluster.
        // CNPG doesn't support renaming a running cluster, so we delete the temp
        // and re-apply with the original name.

        await k8sFactory.DeleteManifestAsync(
            "Cluster", tempName, source.Namespace, source.KubernetesCluster.Kubeconfig!, ct);

        upgraded.Name = source.Name;
        upgraded.Status = CnpgClusterStatus.Running;
        await db.SaveChangesAsync(ct);

        string s3SecretFinal = $"{source.Name}-s3-credentials";
        string finalManifest = BuildClusterManifest(upgraded, source.StorageLink, s3SecretFinal);
        finalManifest += "\n---\n" + BuildObjectStoreManifest(source.Name, source.Namespace, source.StorageLink, s3SecretFinal, source.RetentionDays);
        await k8sFactory.ApplyManifestAsync(finalManifest, source.KubernetesCluster.Kubeconfig!, ct);

        return upgraded;
    }

    // ──────── Backup & Restore ────────

    /// <summary>
    /// Updates the backup schedule, retention window, and max-backup count for an existing
    /// cluster. Saves all three values to the DB then re-applies the ObjectStore (retention)
    /// and ScheduledBackup (schedule) K8s resources so they take effect immediately.
    /// Pass null for schedule to leave it unchanged; pass an empty string to remove it.
    /// </summary>
    public async Task UpdateBackupSettingsAsync(
        Guid tenantId, Guid cnpgClusterId,
        string? schedule, int retentionDays, int maxBackups,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        if (schedule is not null)
            cnpg.BackupSchedule = string.IsNullOrWhiteSpace(schedule) ? null : schedule;

        cnpg.RetentionDays = retentionDays;
        cnpg.MaxBackups = maxBackups;
        await db.SaveChangesAsync(ct);

        if (cnpg.StorageLink is null)
            return;

        string kubeconfig = cnpg.KubernetesCluster.Kubeconfig!;
        string s3SecretName = $"{cnpg.Name}-s3-credentials";

        // Re-apply ObjectStore so the updated retentionPolicy reaches Barman.
        await k8sFactory.ApplyManifestAsync(
            BuildObjectStoreManifest(cnpg.Name, cnpg.Namespace, cnpg.StorageLink, s3SecretName, cnpg.RetentionDays),
            kubeconfig, ct);

        if (!string.IsNullOrWhiteSpace(cnpg.BackupSchedule))
        {
            await k8sFactory.ApplyManifestAsync(
                BuildScheduledBackupManifest(cnpg.Name, cnpg.Namespace, cnpg.BackupSchedule),
                kubeconfig, ct);
        }
        else
        {
            // Schedule was removed — delete the ScheduledBackup CR if it exists.
            try
            {
                await k8sFactory.DeleteManifestAsync(
                    "scheduledbackups.postgresql.cnpg.io", $"{cnpg.Name}-scheduled",
                    cnpg.Namespace, kubeconfig, ct);
            }
            catch { /* CR may not exist — non-fatal */ }
        }
    }

    /// <summary>
    /// Deletes completed Backup CRs (and their DB records) that exceed the cluster's
    /// MaxBackups limit. The oldest completed backups are removed first. Running and
    /// failed backups are never deleted. Best-effort — individual failures are logged
    /// to the returned message list rather than thrown.
    /// </summary>
    public async Task<int> CleanupOldBackupsAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        // Fetch the live backup list from K8s so we operate on current state.
        List<CnpgBackup> liveBackups = await FetchBackupsFromK8sAsync(cnpg, ct);

        int limit = cnpg.MaxBackups > 0 ? cnpg.MaxBackups : 20;

        List<CnpgBackup> completed = liveBackups
            .Where(b => b.Status == CnpgBackupStatus.Completed)
            .OrderByDescending(b => b.StartedAt)
            .ToList();

        if (completed.Count <= limit)
            return 0;

        List<CnpgBackup> toDelete = completed.Skip(limit).ToList();
        int deleted = 0;

        foreach (CnpgBackup backup in toDelete)
        {
            try
            {
                await k8sFactory.DeleteManifestAsync(
                    "backups.postgresql.cnpg.io", backup.Name,
                    cnpg.Namespace, cnpg.KubernetesCluster.Kubeconfig!, ct);

                // Also remove from DB if present.
                CnpgBackup? dbRow = await db.CnpgBackups
                    .FirstOrDefaultAsync(b => b.CnpgClusterId == cnpgClusterId && b.Name == backup.Name, ct);
                if (dbRow is not null)
                    db.CnpgBackups.Remove(dbRow);

                deleted++;
            }
            catch { /* Non-fatal — K8s delete may fail if already gone */ }
        }

        if (deleted > 0)
            await db.SaveChangesAsync(ct);

        return deleted;
    }

    /// <summary>
    /// Applies (or re-applies) the ScheduledBackup CR for an existing cluster.
    /// If <paramref name="schedule"/> is provided it is saved to the cluster record
    /// before applying — use this to configure a schedule on a cluster that was
    /// created without one. If omitted, the stored BackupSchedule is used.
    /// Safe to call repeatedly — kubectl apply is idempotent.
    /// </summary>
    public async Task ApplyScheduledBackupAsync(
        Guid tenantId, Guid cnpgClusterId, string? schedule = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        if (!string.IsNullOrWhiteSpace(schedule))
        {
            cnpg.BackupSchedule = schedule;
            await db.SaveChangesAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(cnpg.BackupSchedule))
        {
            throw new InvalidOperationException(
                "This cluster has no backup schedule configured.");
        }

        string manifest = BuildScheduledBackupManifest(cnpg.Name, cnpg.Namespace, cnpg.BackupSchedule);

        try
        {
            await k8sFactory.ApplyManifestAsync(manifest, cnpg.KubernetesCluster.Kubeconfig!, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Failed to apply ScheduledBackup: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers an on-demand backup of a CNPG cluster. Creates a Backup CR
    /// in Kubernetes that tells Barman to take a base backup to the S3 bucket.
    /// </summary>
    public async Task<CnpgBackup> BackupAsync(Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        if (!cnpg.StorageLinkId.HasValue)
        {
            throw new InvalidOperationException(
                "Backup storage is not configured for this cluster. Assign an S3 bucket first.");
        }

        string kubeconfig = cnpg.KubernetesCluster.Kubeconfig!;

        // Re-apply the cluster manifest before every on-demand backup. This is a
        // best-effort idempotent reconcile that picks up any config changes (e.g.
        // archive_timeout) without requiring a separate upgrade operation.
        // Ignored on failure — the backup itself is the critical operation.
        try
        {
            string s3SecretName = $"{cnpg.Name}-s3-credentials";
            await k8sFactory.ApplyManifestAsync(
                BuildClusterManifest(cnpg, cnpg.StorageLink, s3SecretName), kubeconfig, ct);
        }
        catch { /* non-fatal */ }

        // Generate a unique backup name with timestamp.

        string backupName = $"{cnpg.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

        CnpgBackup backup = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cnpg.Id,
            Name = backupName,
            Type = CnpgBackupType.OnDemand,
            Status = CnpgBackupStatus.Running
        };

        db.CnpgBackups.Add(backup);
        await db.SaveChangesAsync(ct);

        // Apply the Backup CR to trigger Barman.

        string manifest = BuildBackupManifest(backupName, cnpg.Name, cnpg.Namespace);
        await k8sFactory.ApplyManifestAsync(manifest, kubeconfig, ct);

        return backup;
    }

    /// <summary>
    /// Performs a point-in-time restore into a new CNPG cluster, mirroring the MongoDB
    /// restore pattern: (1) create an empty cluster so it appears in the UI immediately,
    /// (2) transition to Restoring, (3) replace the empty Cluster CR with a recovery CR
    /// that bootstraps from the source cluster's Barman backup up to the target time.
    ///
    /// When <paramref name="backupName"/> is provided the recovery references that specific
    /// CNPG Backup CR directly — simpler and more reliable than using externalClusters.
    /// </summary>
    public async Task<CnpgCluster> RestoreAsync(
        Guid tenantId, Guid sourceCnpgClusterId, string newClusterName,
        DateTime? targetTime = null, string? barmanBackupId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster source = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == sourceCnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Source CNPG cluster not found.");

        if (source.StorageLink is null)
        {
            throw new InvalidOperationException(
                "Source cluster has no backup storage configured. Cannot restore without backups.");
        }

        string kubeconfig = source.KubernetesCluster.Kubeconfig!;

        // Step 1: Persist the new cluster record immediately so it appears in the UI.
        // Skip the empty-cluster-then-delete pattern: CNPG clusters have finalizers, so
        // a delete does not complete instantly. Applying a restore CR against a name that
        // is still being finalized results in an update that CNPG discards, silently
        // leaving the cluster in a broken state. Applying the recovery CR directly on a
        // fresh name is the correct CNPG bootstrap.recovery pattern.

        CnpgCluster restored = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = source.KubernetesClusterId,
            Name = newClusterName,
            Namespace = source.Namespace,
            PostgresVersion = source.PostgresVersion,
            Instances = source.Instances,
            StorageSize = source.StorageSize,
            StorageLinkId = source.StorageLinkId,
            BackupSchedule = source.BackupSchedule,
            RetentionDays = source.RetentionDays,
            Status = CnpgClusterStatus.Restoring
        };

        db.CnpgClusters.Add(restored);
        await db.SaveChangesAsync(ct);

        await k8sFactory.EnsureNamespaceAsync(source.Namespace, kubeconfig, ct);

        // Set up the new cluster's S3 credentials and ObjectStore so they exist
        // before the recovery Cluster CR is applied (CNPG validates the ObjectStore
        // reference at reconcile time).
        string s3SecretName = $"{newClusterName}-s3-credentials";
        await EnsureStorageSecretsInK8sAsync(
            tenantId, source.StorageLink, s3SecretName, source.Namespace, kubeconfig, ct);
        await k8sFactory.ApplyManifestAsync(
            BuildObjectStoreManifest(newClusterName, source.Namespace, source.StorageLink, s3SecretName, source.RetentionDays),
            kubeconfig, ct);

        // Ensure the SOURCE cluster's ObjectStore and S3 credentials are present so
        // the recovery externalClusters reference can reach the backup archive.
        string sourceS3SecretName = $"{source.Name}-s3-credentials";
        await EnsureStorageSecretsInK8sAsync(
            tenantId, source.StorageLink, sourceS3SecretName, source.Namespace, kubeconfig, ct);
        await k8sFactory.ApplyManifestAsync(
            BuildObjectStoreManifest(source.Name, source.Namespace, source.StorageLink, sourceS3SecretName, source.RetentionDays),
            kubeconfig, ct);

        // Step 2: Apply the recovery Cluster CR directly. CNPG's bootstrap.recovery is
        // only evaluated on initial cluster creation, so this must be applied to a name
        // that has no existing Cluster CR or PVCs in K8s.
        await k8sFactory.ApplyManifestAsync(
            BuildRestoreManifest(restored, source, targetTime, s3SecretName, barmanBackupId: barmanBackupId),
            kubeconfig, ct);

        return restored;
    }

    /// <summary>
    /// Restores a CNPG cluster in-place from its own Barman backup. Deletes the current
    /// Kubernetes Cluster resource (preserving the ObjectStore and S3 data), then
    /// recreates it with a recovery bootstrap so CNPG recovers WALs up to targetTime.
    /// The database record is reused — only the status changes to Restoring.
    /// </summary>
    public async Task<CnpgCluster> RestoreInPlaceAsync(
        Guid tenantId, Guid cnpgClusterId, DateTime targetTime, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster source = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        if (source.StorageLink is null)
        {
            throw new InvalidOperationException(
                "Cluster has no backup storage configured. Cannot restore without backups.");
        }

        // Delete only the Cluster CRD, not the ObjectStore — the recovery bootstrap
        // references the existing ObjectStore to locate WAL archives in S3.
        try
        {
            await k8sFactory.DeleteManifestAsync(
                "clusters.postgresql.cnpg.io", source.Name, source.Namespace,
                source.KubernetesCluster.Kubeconfig!, ct);
        }
        catch { }

        source.Status = CnpgClusterStatus.Restoring;
        await db.SaveChangesAsync(ct);

        string s3SecretName = $"{source.Name}-s3-credentials";
        await EnsureStorageSecretsInK8sAsync(
            tenantId, source.StorageLink, s3SecretName, source.Namespace,
            source.KubernetesCluster.Kubeconfig!, ct);

        // Re-apply the ObjectStore before the Cluster so CNPG finds it immediately.
        // The source ObjectStore already exists, so this is effectively a no-op update,
        // but applying it first ensures the Cluster CRD is never processed without it.
        await k8sFactory.ApplyManifestAsync(
            BuildObjectStoreManifest(source.Name, source.Namespace, source.StorageLink, s3SecretName, source.RetentionDays),
            source.KubernetesCluster.Kubeconfig!, ct);

        // Use "backup-source" as the external cluster alias so the manifest does not
        // reference the cluster by its own name (CNPG rejects self-referential bootstraps).
        await k8sFactory.ApplyManifestAsync(
            BuildRestoreManifest(source, source, targetTime, s3SecretName, sourceAlias: "backup-source"),
            source.KubernetesCluster.Kubeconfig!, ct);

        return source;
    }

    // ──────── Database Management ────────

    /// <summary>
    /// Creates a new database within a running CNPG cluster. Generates a random
    /// password, runs CREATE ROLE + CREATE DATABASE on the primary, then stores
    /// the connection credentials in the vault tagged for Kubernetes sync.
    /// </summary>
    public async Task<CnpgDatabase> CreateDatabaseAsync(
        Guid tenantId, Guid cnpgClusterId, string databaseName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        string owner = $"{databaseName}_owner";
        string password = GeneratePassword();

        // Create the database record.

        CnpgDatabase database = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cnpg.Id,
            Name = databaseName,
            Owner = owner,
            Status = CnpgDatabaseStatus.Creating
        };

        db.CnpgDatabases.Add(database);
        await db.SaveChangesAsync(ct);

        // Execute SQL on the primary to create the role and database.

        string sql = $"""
            CREATE ROLE "{owner}" WITH LOGIN PASSWORD '{password}';
            CREATE DATABASE "{databaseName}" OWNER "{owner}";
            """;

        await k8sFactory.ExecuteSqlAsync(
            cnpg.Name, cnpg.Namespace, sql, cnpg.KubernetesCluster.Kubeconfig!, ct);

        // Mark as ready.

        database.Status = CnpgDatabaseStatus.Ready;
        await db.SaveChangesAsync(ct);

        // Store connection credentials in the vault, tagged for K8s sync.
        // The K8s Secret will be named "{cluster}-{database}-credentials" in the
        // cluster's namespace, containing all fields apps need to connect.

        string k8sSecretName = $"{cnpg.Name}-{databaseName}-credentials";
        string host = $"{cnpg.Name}-rw.{cnpg.Namespace}.svc.cluster.local";

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "HOST", host, k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PORT", "5432", k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "DATABASE", databaseName, k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "USERNAME", owner, k8sSecretName, cnpg.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PASSWORD", password, k8sSecretName, cnpg.Namespace, ct);

        return database;
    }

    /// <summary>
    /// Deletes a database from a CNPG cluster. Drops the database and role
    /// from PostgreSQL and removes all associated vault secrets.
    /// </summary>
    public async Task DeleteDatabaseAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpg.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        // Drop the database and role from PostgreSQL.
        // If this fails (e.g. database never created, cluster unreachable), we still
        // proceed with removing the record so the user isn't stuck.

        try
        {
            string sql = $"""
                DROP DATABASE IF EXISTS "{database.Name}";
                DROP ROLE IF EXISTS "{database.Owner}";
                """;

            await k8sFactory.ExecuteSqlAsync(
                cnpg.Name, cnpg.Namespace, sql, cnpg.KubernetesCluster.Kubeconfig!, ct);
        }
        catch (InvalidOperationException)
        {
            // SQL execution failed — the database may never have been created
            // (e.g. stuck in "Creating" state). Continue with cleanup.
        }

        // Delete the K8s Secret from the cluster's own namespace and from every
        // bound app namespace. Failures are swallowed so a missing or unreachable
        // cluster doesn't block the cleanup.

        string primarySecretName = $"{cnpg.Name}-{database.Name}-credentials";
        string kubeconfig = cnpg.KubernetesCluster.Kubeconfig!;

        try
        {
            await k8sFactory.DeleteManifestAsync("Secret", primarySecretName, cnpg.Namespace, kubeconfig, ct);
        }
        catch { }

        List<DatabaseBinding> bindings = await db.DatabaseBindings
            .Include(b => b.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(b => b.CnpgDatabaseId == databaseId)
            .ToListAsync(ct);

        foreach (DatabaseBinding binding in bindings)
        {
            try
            {
                await k8sFactory.DeleteManifestAsync(
                    "Secret",
                    binding.KubernetesSecretName,
                    binding.AppDeployment.Namespace,
                    binding.AppDeployment.Cluster.Kubeconfig!,
                    ct);
            }
            catch { }
        }

        // Remove vault secrets.

        await DeleteDatabaseSecretsAsync(tenantId, database.Id, ct);

        // Remove the database record (cascades to DatabaseBinding rows).

        db.CnpgDatabases.Remove(database);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Database Credentials ────────

    /// <summary>
    /// Retrieves the decrypted connection credentials for a database. Returns a
    /// dictionary with keys HOST, PORT, DATABASE, USERNAME, PASSWORD — everything
    /// an application needs to connect. These come from the vault, never stored
    /// in plaintext outside of it.
    /// </summary>
    public async Task<Dictionary<string, string>> GetDatabaseCredentialsAsync(
        Guid tenantId, Guid databaseId, CancellationToken ct = default)
    {
        List<VaultSecret> secrets = await vaultService.GetCnpgDatabaseSecretsAsync(tenantId, databaseId, ct);

        Dictionary<string, string> credentials = new();

        foreach (VaultSecret secret in secrets)
        {
            string? value = await vaultService.GetSecretValueByIdAsync(secret.Id, ct);

            if (value is not null)
            {
                credentials[secret.Name] = value;
            }
        }

        return credentials;
    }

    /// <summary>
    /// Pushes the database credentials to Kubernetes as a Secret in the cluster's
    /// namespace. This creates (or updates) a single opaque Secret containing all
    /// connection fields: HOST, PORT, DATABASE, USERNAME, PASSWORD. Applications
    /// can then mount this Secret as env vars or volume to connect to the database.
    /// </summary>
    public async Task SyncDatabaseCredentialsToK8sAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpg.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        Dictionary<string, string> credentials = await GetDatabaseCredentialsAsync(tenantId, databaseId, ct);

        if (credentials.Count == 0)
        {
            throw new InvalidOperationException("No credentials found in the vault for this database.");
        }

        // Sync to the primary namespace (the cluster's own namespace).

        string primarySecretName = $"{cnpg.Name}-{database.Name}-credentials";
        string primaryKubeconfig = cnpg.KubernetesCluster.Kubeconfig!;

        await ApplyCredentialSecretAsync(
            credentials, primarySecretName, cnpg.Namespace, primaryKubeconfig, ct);

        // Mark vault secrets as synced so the UI reflects the current state.

        List<VaultSecret> vaultSecrets = await vaultService.GetCnpgDatabaseSecretsAsync(tenantId, databaseId, ct);

        foreach (VaultSecret secret in vaultSecrets)
        {
            await vaultService.ConfigureKubernetesSyncAsync(secret.Id, true, primarySecretName, cnpg.Namespace, ct);
        }

        // Propagate to every app deployment bound to this database.

        List<DatabaseBinding> bindings = await db.DatabaseBindings
            .Include(b => b.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(b => b.CnpgDatabaseId == databaseId && b.SyncEnabled)
            .ToListAsync(ct);

        foreach (DatabaseBinding binding in bindings)
        {
            string kubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
            string ns = binding.AppDeployment.Namespace;

            await k8sFactory.EnsureNamespaceAsync(ns, kubeconfig, ct);
            await ApplyCredentialSecretAsync(credentials, binding.KubernetesSecretName, ns, kubeconfig, ct);

            binding.LastSyncedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes (or overwrites) a Kubernetes Opaque Secret with the supplied
    /// credential key/value pairs into the given namespace.
    /// </summary>
    private async Task ApplyCredentialSecretAsync(
        Dictionary<string, string> credentials,
        string secretName, string ns, string kubeconfig,
        CancellationToken ct)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");

        foreach (KeyValuePair<string, string> kvp in credentials)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(kvp.Value));
            sb.AppendLine($"  {kvp.Key}: {encoded}");
        }

        await k8sFactory.ApplyManifestAsync(sb.ToString(), kubeconfig, ct);
    }

    // ──────── Password rotation ────────

    /// <summary>
    /// Rotates the password for a database owner role. Generates a new random
    /// password, applies it via ALTER ROLE on the primary, updates the vault,
    /// then syncs credentials to every namespace that holds a copy — the cluster's
    /// own namespace and all bound app deployments.
    /// </summary>
    public async Task RotateDatabasePasswordAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpg.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        string newPassword = GeneratePassword();

        await k8sFactory.ExecuteSqlAsync(
            cnpg.Name, cnpg.Namespace,
            $"ALTER ROLE \"{database.Owner}\" PASSWORD '{newPassword}';",
            cnpg.KubernetesCluster.Kubeconfig!, ct);

        string k8sSecretName = $"{cnpg.Name}-{database.Name}-credentials";

        await vaultService.SetCnpgDatabaseSecretAsync(
            tenantId, databaseId, "PASSWORD", newPassword, k8sSecretName, cnpg.Namespace, ct);

        // Re-sync to the cluster namespace and all bound app namespaces.
        await SyncDatabaseCredentialsToK8sAsync(tenantId, cnpgClusterId, databaseId, ct);
    }

    // ──────── Superuser credentials ────────

    /// <summary>
    /// Reads the CNPG-managed <c>&lt;cluster&gt;-superuser</c> Kubernetes Secret and stores
    /// the postgres username and password in vault scoped to the cluster (not a database).
    /// Safe to call repeatedly — upserts the vault entries.
    /// </summary>
    public async Task FetchSuperuserCredentialsAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        string secretName = $"{cnpg.Name}-superuser";
        string kubeconfig = cnpg.KubernetesCluster.Kubeconfig!;

        string? username = await k8sFactory.GetSecretValueAsync(secretName, "username", cnpg.Namespace, kubeconfig, ct);
        string? password = await k8sFactory.GetSecretValueAsync(secretName, "password", cnpg.Namespace, kubeconfig, ct);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"Could not read the CNPG superuser secret '{secretName}' in namespace '{cnpg.Namespace}'. " +
                "Ensure the cluster is running and the kubeconfig has read access to Secrets.");
        }

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetCnpgClusterSecretAsync(tenantId, cnpgClusterId, "SUPERUSER_USERNAME", username ?? "postgres", ct);
        await vaultService.SetCnpgClusterSecretAsync(tenantId, cnpgClusterId, "SUPERUSER_PASSWORD", password, ct);
    }

    public async Task<(string? Username, string? Password)> GetSuperuserCredentialsAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        string? username = await vaultService.GetCnpgClusterSecretValueAsync(tenantId, cnpgClusterId, "SUPERUSER_USERNAME", ct);
        string? password = await vaultService.GetCnpgClusterSecretValueAsync(tenantId, cnpgClusterId, "SUPERUSER_PASSWORD", ct);
        return (username, password);
    }

    /// <summary>
    /// Returns true when all tables in the public schema are already owned by the
    /// database's configured owner — i.e. no ownership fix is needed.
    /// </summary>
    public async Task<bool> IsDatabaseOwnershipCorrectAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpgClusterId, ct)
            ?? throw new InvalidOperationException("Database not found.");

        string sql = $"""
            SELECT CASE WHEN COUNT(*) = 0 THEN 'ok' ELSE 'fix' END
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tableowner != '{database.Owner}';
            """;

        string result = await k8sFactory.ExecuteSqlInCnpgDatabaseWithOutputAsync(
            cnpg.Name, cnpg.Namespace, database.Name, sql,
            cnpg.KubernetesCluster.Kubeconfig!, ct);

        return result.Trim() == "ok";
    }

    /// <summary>
    /// Grants full ownership of all objects in the public schema to the database owner.
    /// Runs as the <c>postgres</c> superuser via <c>kubectl exec</c> (peer auth —
    /// no password needed). Use this to fix permission errors after restoring a
    /// database dump from another Keycloak or application instance.
    /// </summary>
    public async Task GrantDatabaseOwnerPermissionsAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpgClusterId, ct)
            ?? throw new InvalidOperationException("Database not found.");

        // Grant all schema-level permissions, reassign default privileges, and transfer
        // ownership of existing objects so Liquibase DDL operations succeed.
        // Running as 'postgres' superuser via kubectl exec peer auth — no password needed.
        string sql = $"""
            GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO "{database.Owner}";
            GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO "{database.Owner}";
            GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO "{database.Owner}";
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO "{database.Owner}";
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO "{database.Owner}";
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
                    EXECUTE format('ALTER TABLE public.%I OWNER TO "{database.Owner}"', r.tablename);
                END LOOP;
                FOR r IN SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = 'public' LOOP
                    EXECUTE format('ALTER SEQUENCE public.%I OWNER TO "{database.Owner}"', r.sequence_name);
                END LOOP;
                FOR r IN SELECT table_name FROM information_schema.views WHERE table_schema = 'public' LOOP
                    EXECUTE format('ALTER VIEW public.%I OWNER TO "{database.Owner}"', r.table_name);
                END LOOP;
            END $$;
            """;

        await k8sFactory.ExecuteSqlInCnpgDatabaseAsync(
            cnpg.Name, cnpg.Namespace, database.Name, sql,
            cnpg.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task ReleaseLiquibaseLockAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpgClusterId, ct)
            ?? throw new InvalidOperationException("Database not found.");

        const string sql = """
            UPDATE databasechangeloglock
               SET locked = false, lockgranted = null, lockedby = null
             WHERE id = 1;
            """;

        await k8sFactory.ExecuteSqlInCnpgDatabaseAsync(
            cnpg.Name, cnpg.Namespace, database.Name, sql,
            cnpg.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task FixKeycloakRealmFrontendUrlAsync(
        Guid tenantId, Guid cnpgClusterId, Guid databaseId, string frontendUrl,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster cnpg = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("CNPG cluster not found.");

        CnpgDatabase database = await db.CnpgDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.CnpgClusterId == cnpgClusterId, ct)
            ?? throw new InvalidOperationException("Database not found.");

        // Replace frontendUrl for every realm (delete+insert avoids relying on constraint names).
        // Also reset the rootUrl of built-in clients to Keycloak's ${authBaseUrl} placeholder so
        // the admin/account consoles derive their base URL from the running server rather than the
        // old hostname that was baked in by the restored database.
        string sql = $$"""
            DELETE FROM realm_attribute WHERE name = 'frontendUrl';
            INSERT INTO realm_attribute (realm_id, name, value)
            SELECT id, 'frontendUrl', '{{frontendUrl}}' FROM realm;

            UPDATE keycloak_client
               SET root_url = '${authBaseUrl}'
             WHERE client_id IN ('security-admin-console', 'account', 'account-console', 'broker');
            """;

        await k8sFactory.ExecuteSqlInCnpgDatabaseAsync(
            cnpg.Name, cnpg.Namespace, database.Name, sql,
            cnpg.KubernetesCluster.Kubeconfig!, ct);
    }

    // ──────── DatabaseBinding management ────────

    /// <summary>
    /// Returns all database bindings for a specific app deployment, with their
    /// CNPG and MongoDB database navigation properties populated.
    /// </summary>
    public async Task<List<DatabaseBinding>> GetDatabaseBindingsAsync(
        Guid appDeploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.DatabaseBindings
            .Include(b => b.CnpgDatabase)
                .ThenInclude(d => d!.CnpgCluster)
            .Include(b => b.MongoDatabase)
                .ThenInclude(d => d!.MongoCluster)
            .Include(b => b.RegisteredPostgresDatabase)
            .Where(b => b.AppDeploymentId == appDeploymentId)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a binding between a CNPG database and an app deployment. Does not
    /// sync immediately — call SyncDatabaseCredentialsToK8sAsync to push credentials.
    /// </summary>
    public async Task<DatabaseBinding> AddCnpgDatabaseBindingAsync(
        Guid appDeploymentId, Guid cnpgDatabaseId, string kubernetesSecretName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DatabaseBinding binding = new()
        {
            Id = Guid.NewGuid(),
            AppDeploymentId = appDeploymentId,
            CnpgDatabaseId = cnpgDatabaseId,
            KubernetesSecretName = kubernetesSecretName
        };

        db.DatabaseBindings.Add(binding);
        await db.SaveChangesAsync(ct);
        return binding;
    }

    /// <summary>
    /// Removes a database binding. Does not delete the Kubernetes Secret from the
    /// app's namespace — that must be cleaned up manually if desired.
    /// </summary>
    public async Task RemoveDatabaseBindingAsync(Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DatabaseBinding binding = await db.DatabaseBindings
            .FirstOrDefaultAsync(b => b.Id == bindingId, ct)
            ?? throw new InvalidOperationException("Binding not found.");

        db.DatabaseBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Queries ────────

    /// <summary>
    /// Gets all managed CNPG clusters for a tenant, including their databases.
    /// </summary>
    public async Task<List<CnpgCluster>> GetClustersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.CnpgClusters
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
    /// Gets a single managed CNPG cluster by ID.
    /// </summary>
    public async Task<CnpgCluster?> GetClusterAsync(Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .ThenInclude(k => k.Environment)
            .Include(c => c.StorageLink)
            .Include(c => c.Databases)
            .Include(c => c.Backups.OrderByDescending(b => b.StartedAt).Take(10))
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct);
    }

    /// <summary>
    /// Gets the full detail for a CNPG cluster including live pod status from K8s.
    /// Queries the Cluster CRD status for phase/primary info and lists pods to
    /// show which is primary vs replica, their readiness, and replication lag.
    /// </summary>
    public async Task<CnpgClusterDetail?> GetClusterDetailAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgCluster? cluster = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.StorageLink)
            .Include(c => c.Databases)
            .Include(c => c.Backups.OrderByDescending(b => b.StartedAt).Take(20))
            .FirstOrDefaultAsync(c => c.Id == cnpgClusterId && c.TenantId == tenantId, ct);

        if (cluster is null)
        {
            return null;
        }

        CnpgClusterDetail detail = new()
        {
            Cluster = cluster,
            Phase = "Querying...",
            Backups = cluster.Backups.OrderByDescending(b => b.StartedAt).ToList()
        };

        try
        {
            // Query the CNPG Cluster CRD status for phase and primary info.

            string clusterJson = await k8sFactory.GetJsonAsync(
                $"cluster.postgresql.cnpg.io/{cluster.Name}",
                cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct: ct);

            ParseClusterStatus(clusterJson, detail);

            // Query pods belonging to this CNPG cluster via label selector.

            string podsJson = await k8sFactory.GetJsonAsync(
                "pods", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!,
                $"cnpg.io/cluster={cluster.Name}", ct);

            detail.Pods = ParsePodList(podsJson, detail.CurrentPrimary);

            // Reconcile the persisted status with the live K8s state.
            // If the cluster reports healthy but we still say "Creating", update it.

            await ReconcileClusterStatusAsync(cluster, detail, ct);

            // Push any code-level config changes (e.g. archive_timeout, target:primary on
            // ScheduledBackup) to the K8s resources. kubectl apply is idempotent — when the
            // manifest is already in sync this is a cheap no-op GET with no write. Best-effort:
            // a failure here must not prevent the detail view from loading.
            _ = ReconcileClusterManifestsAsync(cluster, ct);

            // Query K8s Backup CRs and populate detail.Backups directly so the UI always
            // reflects live state. DB persistence is a separate best-effort step below.

            detail.Backups = await FetchBackupsFromK8sAsync(cluster, ct);
        }
        catch (Exception)
        {
            // If K8s is unreachable, we still return the DB record with empty pod info.
            detail.Phase = "Unable to reach cluster";
        }

        // Persist any newly discovered backups to the DB (for restore source selection etc.).
        // This is best-effort — a failure here never prevents backups from being displayed,
        // since detail.Backups is already populated from K8s above.

        await PersistK8sBackupsAsync(cluster.Id, detail.Backups, ct);

        // If K8s was unreachable, fall back to whatever is already in the DB.

        if (detail.Backups.Count == 0)
        {
            using ApplicationDbContext backupsDb = dbFactory.CreateDbContext();
            detail.Backups = await backupsDb.CnpgBackups
                .Where(b => b.CnpgClusterId == cluster.Id)
                .OrderByDescending(b => b.StartedAt)
                .Take(20)
                .ToListAsync(ct);
        }

        return detail;
    }

    /// <summary>
    /// Re-applies the CNPG Cluster and ScheduledBackup manifests to K8s so that
    /// any code-level config changes (e.g. archive_timeout, target:primary) reach
    /// existing clusters without requiring a manual upgrade. kubectl apply is
    /// idempotent — when the spec is already in sync it issues no write. Errors
    /// are swallowed because this is a best-effort background reconcile.
    /// </summary>
    private async Task ReconcileClusterManifestsAsync(CnpgCluster cluster, CancellationToken ct)
    {
        if (cluster.StorageLink is null || cluster.KubernetesCluster.Kubeconfig is null)
            return;

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig;
            string s3SecretName = $"{cluster.Name}-s3-credentials";

            await k8sFactory.ApplyManifestAsync(
                BuildClusterManifest(cluster, cluster.StorageLink, s3SecretName), kubeconfig, ct);

            if (!string.IsNullOrWhiteSpace(cluster.BackupSchedule))
            {
                await k8sFactory.ApplyManifestAsync(
                    BuildScheduledBackupManifest(cluster.Name, cluster.Namespace, cluster.BackupSchedule),
                    kubeconfig, ct);
            }
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Reconciles the persisted cluster status with the live Kubernetes state.
    /// Transitions from Creating/Restoring to Running when the cluster becomes healthy,
    /// and marks Failed if the cluster phase indicates an error condition.
    /// </summary>
    private async Task ReconcileClusterStatusAsync(
        CnpgCluster cluster, CnpgClusterDetail detail, CancellationToken ct)
    {
        CnpgClusterStatus? newStatus = DetermineStatusFromPhase(detail.Phase, cluster.Status);

        if (newStatus is null || newStatus == cluster.Status)
        {
            return;
        }

        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            CnpgCluster? tracked = await db.CnpgClusters.FindAsync([cluster.Id], ct);

            if (tracked is not null && tracked.Status != newStatus.Value)
            {
                tracked.Status = newStatus.Value;
                await db.SaveChangesAsync(ct);
                cluster.Status = newStatus.Value;
            }
        }
        catch
        {
            // Non-fatal: status reconciliation failure should not prevent the detail view loading.
        }
    }

    /// <summary>
    /// Maps the live CNPG cluster phase string to the appropriate managed status.
    /// Returns null if no transition should occur.
    /// </summary>
    /// <summary>
    /// Queries K8s for all Backup CRs in the cluster's namespace and returns them as
    /// in-memory CnpgBackup objects (not yet persisted). This is the display-time source
    /// of truth — the UI always shows what K8s has, regardless of DB state.
    /// Returns an empty list if the query fails (K8s unreachable, CRD missing, etc.).
    /// </summary>
    private async Task<List<CnpgBackup>> FetchBackupsFromK8sAsync(
        CnpgCluster cluster, CancellationToken ct)
    {
        try
        {
            // Query all Backup CRs in the namespace without a label selector so we are
            // not sensitive to label differences across CNPG versions or plugin configurations.
            string backupsJson = await k8sFactory.GetJsonAsync(
                "backups.postgresql.cnpg.io",
                cluster.Namespace,
                cluster.KubernetesCluster.Kubeconfig!,
                ct: ct);

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(backupsJson);
            System.Text.Json.JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("items", out System.Text.Json.JsonElement items))
                return [];

            List<CnpgBackup> result = [];

            foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta))
                    continue;

                string? name = meta.TryGetProperty("name", out System.Text.Json.JsonElement nameEl)
                    ? nameEl.GetString() : null;
                if (string.IsNullOrEmpty(name))
                    continue;

                // Filter to backups belonging to this cluster via spec.cluster.name.
                if (!item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                    || !spec.TryGetProperty("cluster", out System.Text.Json.JsonElement clusterEl)
                    || !clusterEl.TryGetProperty("name", out System.Text.Json.JsonElement clusterNameEl)
                    || clusterNameEl.GetString() != cluster.Name)
                    continue;

                string phase = "";
                string? barmanId = null;
                DateTime? startedAt = null;
                DateTime? stoppedAt = null;

                if (item.TryGetProperty("status", out System.Text.Json.JsonElement status))
                {
                    if (status.TryGetProperty("phase", out System.Text.Json.JsonElement phaseEl))
                        phase = phaseEl.GetString() ?? "";

                    if (status.TryGetProperty("backupId", out System.Text.Json.JsonElement barmanIdEl))
                        barmanId = barmanIdEl.GetString();

                    if (status.TryGetProperty("startedAt", out System.Text.Json.JsonElement startEl)
                        && DateTime.TryParse(startEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedStart))
                        startedAt = parsedStart;

                    if (status.TryGetProperty("stoppedAt", out System.Text.Json.JsonElement stopEl)
                        && DateTime.TryParse(stopEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedStop))
                        stoppedAt = parsedStop;
                }

                CnpgBackupStatus backupStatus = phase.ToLowerInvariant() switch
                {
                    "completed" => CnpgBackupStatus.Completed,
                    "failed" => CnpgBackupStatus.Failed,
                    _ => CnpgBackupStatus.Running
                };

                // A backup not created by our BackupAsync is from the ScheduledBackup CR.
                CnpgBackupType backupType = name.StartsWith($"{cluster.Name}-")
                    && name.Length > cluster.Name.Length + 1
                    && !meta.TryGetProperty("ownerReferences", out _)
                    ? CnpgBackupType.OnDemand
                    : CnpgBackupType.Scheduled;

                result.Add(new CnpgBackup
                {
                    Id = Guid.Empty,
                    CnpgClusterId = cluster.Id,
                    Name = name,
                    Type = backupType,
                    Status = backupStatus,
                    StartedAt = startedAt ?? DateTime.UtcNow,
                    CompletedAt = backupStatus != CnpgBackupStatus.Running ? stoppedAt : null,
                    BarmanId = barmanId
                });
            }

            return [.. result.OrderByDescending(b => b.StartedAt).Take(20)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Persists backups fetched from K8s into the CnpgBackup table. Inserts rows that
    /// are not yet in the DB and updates the status of any rows that have changed.
    /// Best-effort — exceptions are swallowed so a DB failure never breaks the detail view.
    /// </summary>
    private async Task PersistK8sBackupsAsync(
        Guid cnpgClusterId, List<CnpgBackup> k8sBackups, CancellationToken ct)
    {
        if (k8sBackups.Count == 0)
            return;

        // Each backup is saved in its own context so a unique-constraint violation on one
        // row (e.g. from a concurrent request) does not roll back saves for other rows.
        foreach (CnpgBackup k8s in k8sBackups)
        {
            try
            {
                using ApplicationDbContext db = dbFactory.CreateDbContext();

                CnpgBackup? existing = await db.CnpgBackups
                    .FirstOrDefaultAsync(
                        b => b.CnpgClusterId == cnpgClusterId && b.Name == k8s.Name, ct);

                if (existing is null)
                {
                    db.CnpgBackups.Add(new CnpgBackup
                    {
                        Id = Guid.NewGuid(),
                        CnpgClusterId = cnpgClusterId,
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
                    if (k8s.Status != CnpgBackupStatus.Running)
                        existing.CompletedAt = k8s.CompletedAt ?? DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch
            {
                // DB persistence is optional — the backup list is already set from K8s.
            }
        }
    }

    private static CnpgClusterStatus? DetermineStatusFromPhase(string phase, CnpgClusterStatus currentStatus)
    {
        string lower = phase.ToLowerInvariant();

        // Healthy / running phases — cluster is ready.

        if (lower.Contains("healthy") || lower == "cluster in healthy state")
        {
            if (currentStatus is CnpgClusterStatus.Creating
                or CnpgClusterStatus.Restoring
                or CnpgClusterStatus.Upgrading
                or CnpgClusterStatus.Failed)
            {
                return CnpgClusterStatus.Running;
            }
        }

        // Failure phases.

        if (lower.Contains("failed") || lower.Contains("error"))
        {
            if (currentStatus is not CnpgClusterStatus.Failed and not CnpgClusterStatus.Deleting)
            {
                return CnpgClusterStatus.Failed;
            }
        }

        return null;
    }

    // ──────── Private Helpers ────────

    /// <summary>
    /// Ensures the S3 storage credentials are available as a Kubernetes Secret
    /// in the target namespace so CNPG's Barman can access the backup bucket.
    /// </summary>
    private async Task EnsureStorageSecretsInK8sAsync(
        Guid tenantId, StorageLink storageLink, string secretName, string ns,
        string kubeconfig, CancellationToken ct)
    {
        // Retrieve the S3 credentials from the vault and physically create a
        // Kubernetes Secret in the target namespace. The CNPG ObjectStore references
        // this Secret to authenticate with the S3-compatible storage.

        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenantId, storageLink.Id, ct);

        foreach (VaultSecret secret in secrets)
        {
            if (secret.Name == "ACCESS_KEY" || secret.Name == "SECRET_KEY")
            {
                await vaultService.ConfigureKubernetesSyncAsync(secret.Id, true, secretName, ns, ct);
            }
        }

        // Decrypt the actual credential values so we can create the K8s Secret immediately.

        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, storageLink.Id, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, storageLink.Id, "SECRET_KEY", ct);

        if (accessKey is null || secretKey is null)
        {
            throw new InvalidOperationException(
                "S3 credentials (ACCESS_KEY and SECRET_KEY) are missing from the storage link vault. " +
                "Add them via the Storage tab before creating a backup-enabled cluster.");
        }

        // Apply the Secret manifest directly so it exists immediately for the ObjectStore.

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
        await vaultService.SetCnpgDatabaseSecretAsync(
            tenantId, databaseId, name, value, k8sSecretName, k8sNamespace, ct);
    }

    /// <summary>
    /// Removes all vault secrets associated with a CNPG database.
    /// </summary>
    private async Task DeleteDatabaseSecretsAsync(Guid tenantId, Guid databaseId, CancellationToken ct)
    {
        using ApplicationDbContext secretDb = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await secretDb.VaultSecrets
            .Where(s => s.CnpgDatabaseId == databaseId)
            .ToListAsync(ct);

        secretDb.VaultSecrets.RemoveRange(secrets);
        await secretDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Generates a secure random password for database roles.
    /// </summary>
    private static string GeneratePassword()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "x").Replace("/", "y")[..32];
    }

    // ──────── Manifest Builders ────────

    /// <summary>
    /// Extracts the major version number from a PostgreSQL version string.
    /// Handles formats like "18", "18.1", "16.4-1" etc.
    /// </summary>
    private static int ExtractMajorVersion(string version)
    {
        string majorPart = version.Split('.')[0].Split('-')[0];
        return int.Parse(majorPart);
    }

    /// <summary>
    /// Builds the CNPG Cluster CRD manifest. Includes Barman backup configuration
    /// if a storage link is configured.
    /// </summary>
    private static string BuildClusterManifest(CnpgCluster cnpg, StorageLink? storageLink, string? s3SecretName)
    {
        StringBuilder sb = new();

        sb.AppendLine("apiVersion: postgresql.cnpg.io/v1");
        sb.AppendLine("kind: Cluster");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {cnpg.Name}");
        sb.AppendLine($"  namespace: {cnpg.Namespace}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  instances: {cnpg.Instances}");
        sb.AppendLine($"  imageName: ghcr.io/cloudnative-pg/postgresql:{cnpg.PostgresVersion}");
        sb.AppendLine("  enableSuperuserAccess: true");

        // Spread instances across nodes so a single node loss can't take down the whole
        // cluster. Preferred (soft) so it degrades to co-location when there are fewer
        // nodes than instances, rather than leaving pods Pending. CNPG generates the
        // pod anti-affinity from this native block (keyed on the cnpg.io/cluster label).
        sb.AppendLine("  affinity:");
        sb.AppendLine("    enablePodAntiAffinity: true");
        sb.AppendLine("    topologyKey: kubernetes.io/hostname");
        sb.AppendLine("    podAntiAffinityType: preferred");

        // Rolling update strategy: replicas are updated first, then a switchover
        // promotes a replica to primary before updating the old primary. This gives
        // zero-downtime for minor version changes and config updates.

        sb.AppendLine("  primaryUpdateStrategy: unsupervised");
        sb.AppendLine("  primaryUpdateMethod: switchover");
        sb.AppendLine("  switchoverDelay: 600");

        // Synchronous replication requires at least 2 instances. For HA clusters,
        // we guarantee one synchronous replica so no committed transactions are lost.

        if (cnpg.Instances > 1)
        {
            sb.AppendLine("  minSyncReplicas: 1");
            sb.AppendLine("  maxSyncReplicas: 1");
        }

        sb.AppendLine("  postgresql:");
        sb.AppendLine("    parameters:");
        sb.AppendLine("      shared_buffers: \"256MB\"");
        sb.AppendLine("      max_connections: \"200\"");
        // Force WAL segment rotation at least every 5 minutes on idle clusters.
        // Without this, segments never fill (16 MB each) so WAL 9 is never archived
        // and backup-based recovery always fails with "recovery ended before target".
        if (storageLink is not null)
            sb.AppendLine("      archive_timeout: \"5min\"");
        sb.AppendLine("  storage:");
        sb.AppendLine($"    size: {cnpg.StorageSize}");

        // Use the Barman Cloud Plugin for backup/WAL archiving instead of the
        // deprecated native spec.backup.barmanObjectStore approach.

        if (storageLink is not null && s3SecretName is not null)
        {
            sb.AppendLine("  plugins:");
            sb.AppendLine("    - name: barman-cloud.cloudnative-pg.io");
            sb.AppendLine("      parameters:");
            sb.AppendLine($"        barmanObjectName: {cnpg.Name}-object-store");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the ObjectStore CRD manifest for the Barman Cloud Plugin.
    /// This replaces the deprecated inline spec.backup.barmanObjectStore config.
    /// The ObjectStore references the cluster and defines S3 destination/credentials.
    /// </summary>
    private static string BuildObjectStoreManifest(
        string clusterName, string ns, StorageLink storageLink, string s3SecretName,
        int retentionDays = 30)
    {
        StringBuilder sb = new();

        sb.AppendLine("apiVersion: barmancloud.cnpg.io/v1");
        sb.AppendLine("kind: ObjectStore");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {clusterName}-object-store");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("spec:");
        sb.AppendLine("  configuration:");
        sb.AppendLine($"    destinationPath: s3://{storageLink.BucketName}/{clusterName}/");
        sb.AppendLine($"    endpointURL: {storageLink.Endpoint}");
        sb.AppendLine("    s3Credentials:");
        sb.AppendLine("      accessKeyId:");
        sb.AppendLine($"        name: {s3SecretName}");
        sb.AppendLine("        key: ACCESS_KEY");
        sb.AppendLine("      secretAccessKey:");
        sb.AppendLine($"        name: {s3SecretName}");
        sb.AppendLine("        key: SECRET_KEY");
        sb.AppendLine("    wal:");
        sb.AppendLine("      compression: gzip");
        sb.AppendLine($"  retentionPolicy: \"{retentionDays}d\"");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a ScheduledBackup CRD manifest for automated periodic backups.
    /// Uses the pluginBarmanCloud method which works with the Barman Cloud Plugin.
    /// </summary>
    private static string BuildScheduledBackupManifest(string clusterName, string ns, string schedule)
    {
        StringBuilder sb = new();

        sb.AppendLine("apiVersion: postgresql.cnpg.io/v1");
        sb.AppendLine("kind: ScheduledBackup");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {clusterName}-scheduled");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  schedule: \"{schedule}\"");
        sb.AppendLine("  immediate: true");
        sb.AppendLine("  backupOwnerReference: self");
        sb.AppendLine("  target: primary");
        sb.AppendLine("  method: plugin");
        sb.AppendLine("  pluginConfiguration:");
        sb.AppendLine("    name: barman-cloud.cloudnative-pg.io");
        sb.AppendLine("    parameters:");
        sb.AppendLine($"      barmanObjectName: {clusterName}-object-store");
        sb.AppendLine("  cluster:");
        sb.AppendLine($"    name: {clusterName}");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a Backup CRD manifest for an on-demand backup via the Barman Cloud Plugin.
    /// </summary>
    private static string BuildBackupManifest(string backupName, string clusterName, string ns)
    {
        StringBuilder sb = new();

        sb.AppendLine("apiVersion: postgresql.cnpg.io/v1");
        sb.AppendLine("kind: Backup");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {backupName}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("spec:");
        sb.AppendLine("  target: primary");
        sb.AppendLine("  method: plugin");
        sb.AppendLine("  pluginConfiguration:");
        sb.AppendLine("    name: barman-cloud.cloudnative-pg.io");
        sb.AppendLine("    parameters:");
        sb.AppendLine($"      barmanObjectName: {clusterName}-object-store");
        sb.AppendLine("  cluster:");
        sb.AppendLine($"    name: {clusterName}");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a CNPG Cluster manifest that bootstraps from a Barman Cloud Plugin backup
    /// with point-in-time recovery to the specified target time.
    ///
    /// When <paramref name="backupName"/> is provided the recovery references that specific
    /// CNPG Backup CR directly — CNPG resolves the ObjectStore from the Backup and does not
    /// need an externalClusters entry. This is the preferred path when a concrete backup
    /// is selected in the UI.
    ///
    /// When <paramref name="backupName"/> is null the manifest falls back to externalClusters
    /// with the Barman Cloud Plugin referencing <paramref name="source"/>'s ObjectStore.
    /// <paramref name="sourceAlias"/> overrides the external-cluster alias (required for
    /// in-place restores where source.Name == restored.Name).
    /// </summary>
    private static string BuildRestoreManifest(
        CnpgCluster restored, CnpgCluster source, DateTime? targetTime, string s3SecretName,
        string? sourceAlias = null, string? barmanBackupId = null)
    {
        // Always use externalClusters — the barman-cloud plugin requires an explicit
        // ObjectStore reference to authenticate against the source backup archive.
        // The backup.name (Backup CR reference) approach does not propagate credentials
        // to the recovery pod, causing "no credentials defined" at restore time.
        string alias = sourceAlias ?? source.Name;

        StringBuilder sb = new();

        sb.AppendLine("apiVersion: postgresql.cnpg.io/v1");
        sb.AppendLine("kind: Cluster");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {restored.Name}");
        sb.AppendLine($"  namespace: {restored.Namespace}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  instances: {restored.Instances}");
        sb.AppendLine($"  imageName: ghcr.io/cloudnative-pg/postgresql:{restored.PostgresVersion}");
        sb.AppendLine("  postgresql:");
        sb.AppendLine("    parameters:");
        sb.AppendLine("      archive_timeout: \"5min\"");
        sb.AppendLine("  storage:");
        sb.AppendLine($"    size: {restored.StorageSize}");
        sb.AppendLine("  plugins:");
        sb.AppendLine("    - name: barman-cloud.cloudnative-pg.io");
        sb.AppendLine("      parameters:");
        sb.AppendLine($"        barmanObjectName: {restored.Name}-object-store");
        sb.AppendLine("  bootstrap:");
        sb.AppendLine("    recovery:");
        sb.AppendLine($"      source: {alias}");
        sb.AppendLine("      recoveryTarget:");
        if (barmanBackupId is not null)
        {
            // backupID tells barman which base backup to restore; omit targetImmediate so
            // PostgreSQL recovers to the latest available WAL then promotes. targetImmediate
            // requires the backup-end WAL record (which lives at end_lsn, start of the NEXT
            // segment) to be in S3 — that segment may not be archived if the cluster is idle.
            sb.AppendLine($"        backupID: \"{barmanBackupId}\"");
        }
        else
        {
            DateTime resolvedTarget = (targetTime ?? DateTime.UtcNow).ToUniversalTime();
            sb.AppendLine($"        targetTime: \"{resolvedTarget:yyyy-MM-ddTHH:mm:ssZ}\"");
        }
        sb.AppendLine("  externalClusters:");
        sb.AppendLine($"    - name: {alias}");
        sb.AppendLine("      plugin:");
        sb.AppendLine("        name: barman-cloud.cloudnative-pg.io");
        sb.AppendLine("        parameters:");
        sb.AppendLine($"          barmanObjectName: {source.Name}-object-store");
        // serverName tells the plugin which sub-directory of destinationPath holds the
        // source backups. Without it the plugin defaults to the NEW cluster's name and
        // finds an empty catalog because the backups live under the source cluster's name.
        sb.AppendLine($"          serverName: {source.Name}");

        return sb.ToString();
    }

    // ──────── JSON Parsing ────────

    /// <summary>
    /// Parses the CNPG Cluster CRD JSON to extract phase, primary, timeline info.
    /// </summary>
    private static void ParseClusterStatus(string json, CnpgClusterDetail detail)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        if (root.TryGetProperty("status", out System.Text.Json.JsonElement status))
        {
            if (status.TryGetProperty("phase", out System.Text.Json.JsonElement phase))
            {
                detail.Phase = phase.GetString() ?? "Unknown";
            }

            if (status.TryGetProperty("currentPrimary", out System.Text.Json.JsonElement primary))
            {
                detail.CurrentPrimary = primary.GetString();
            }

            if (status.TryGetProperty("readyInstances", out System.Text.Json.JsonElement ready))
            {
                detail.ReadyInstances = ready.GetInt32();
            }

            if (status.TryGetProperty("currentPrimaryTimestamp", out _)
                && status.TryGetProperty("timelineID", out System.Text.Json.JsonElement timeline))
            {
                detail.CurrentTimeline = timeline.GetInt32();
            }
        }
    }

    /// <summary>
    /// Parses a kubectl "get pods -o json" response into a list of CnpgPodInfo.
    /// Determines role (primary vs replica) by comparing pod name to current primary.
    /// </summary>
    private static List<CnpgPodInfo> ParsePodList(string json, string? currentPrimary)
    {
        List<CnpgPodInfo> pods = [];
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("items", out System.Text.Json.JsonElement items))
        {
            return pods;
        }

        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            string podName = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
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

                // Sum up restart counts from all containers.

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

            // Determine role from CNPG labels or by matching against known primary.

            string role = "replica";

            if (item.TryGetProperty("metadata", out System.Text.Json.JsonElement metadata)
                && metadata.TryGetProperty("labels", out System.Text.Json.JsonElement labels)
                && labels.TryGetProperty("cnpg.io/instanceRole", out System.Text.Json.JsonElement roleLabel))
            {
                role = roleLabel.GetString() ?? "replica";
            }
            else if (podName == currentPrimary)
            {
                role = "primary";
            }

            pods.Add(new CnpgPodInfo
            {
                Name = podName,
                Role = role,
                Status = podStatus,
                Ready = ready,
                Node = node,
                StartTime = startTime,
                Restarts = restarts
            });
        }

        // Sort: primary first, then replicas by name.

        return [.. pods.OrderBy(p => p.Role == "primary" ? 0 : 1).ThenBy(p => p.Name)];
    }
}
