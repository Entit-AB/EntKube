using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class RabbitMQClusterInfo
{
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public bool AllReplicasReady { get; set; }
    public bool ClusterAvailable { get; set; }
    public int ReadyReplicas { get; set; }
}

public class RabbitMQVhostInfo
{
    public required string K8sName { get; set; }
    public required string VhostName { get; set; }
}

public class RabbitMQQueueInfo
{
    public required string K8sName { get; set; }
    public required string QueueName { get; set; }
    public required string Vhost { get; set; }
    public string QueueType { get; set; } = "quorum";
    public bool Durable { get; set; } = true;
    public bool AutoDelete { get; set; }
}

public class RabbitMQExchangeInfo
{
    public required string K8sName { get; set; }
    public required string ExchangeName { get; set; }
    public required string Vhost { get; set; }
    public string ExchangeType { get; set; } = "direct";
    public bool Durable { get; set; } = true;
    public bool AutoDelete { get; set; }
}

public class RabbitMQRoutingBindingInfo
{
    public required string K8sName { get; set; }
    public required string Source { get; set; }
    public required string Destination { get; set; }
    public required string DestinationType { get; set; }
    public required string Vhost { get; set; }
    public string RoutingKey { get; set; } = "";
}

public class RabbitMQUserInfo
{
    public required string K8sName { get; set; }
    public required string Username { get; set; }
    public List<string> Tags { get; set; } = [];
}

// kept for backward compat with any existing callers
public class RabbitMQLiveTopology
{
    public List<string> Vhosts { get; set; } = [];
    public List<string> Queues { get; set; } = [];
    public List<string> Exchanges { get; set; } = [];
    public List<string> Users { get; set; } = [];
}

public class RabbitMQOperatorStatus
{
    public bool ClusterOperatorAvailable { get; set; }
    public bool TopologyOperatorAvailable { get; set; }
    public string? ClusterOperatorClusterName { get; set; }
    public string? TopologyOperatorClusterName { get; set; }
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Manages RabbitMQ clusters and their definitions backups within EntKube.
///
/// Cluster infrastructure is handled by the RabbitMQ Cluster Operator (RabbitmqCluster CRD).
/// Messaging topology (vhosts, queues, exchanges, bindings) is handled by the RabbitMQ
/// Messaging Topology Operator and discovered live from Kubernetes — EntKube does not
/// own topology lifecycle, only surfaces it for visibility.
///
/// Backup/restore exports the full broker definitions.json via rabbitmqctl and stores it
/// in S3, mirroring the Keycloak realm export pattern.
/// </summary>
public class RabbitMQService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    VaultService vaultService)
{
    // ── Cluster queries ───────────────────────────────────────────────────────

    public async Task<List<RabbitMQCluster>> GetClustersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster).ThenInclude(k => k.Environment)
            .Include(c => c.StorageLink)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<RabbitMQCluster?> GetClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster).ThenInclude(k => k.Environment)
            .Include(c => c.StorageLink)
            .Include(c => c.Backups.OrderByDescending(b => b.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
    }

    // ── Operator detection ────────────────────────────────────────────────────

    /// <summary>
    /// Detects whether the RabbitMQ Cluster Operator and Topology Operator are installed
    /// on any of the tenant's Kubernetes clusters by querying installed ClusterComponents.
    /// Follows the same pattern as DatabaseService.GetOperatorStatusAsync.
    /// </summary>
    public async Task<RabbitMQOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<ClusterComponent> installed = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Cluster.TenantId == tenantId
                        && c.Status == ComponentStatus.Installed
                        && (c.Name == "rabbitmq-cluster-operator"
                            || c.ReleaseName == "rabbitmq-cluster-operator"
                            || c.Name == "rabbitmq-messaging-topology-operator"
                            || c.ReleaseName == "rabbitmq-topology-operator"
                            || c.ReleaseName == "rabbitmq-messaging-topology-operator"))
            .ToListAsync(ct);

        ClusterComponent? clusterOp = installed.FirstOrDefault(
            c => c.Name == "rabbitmq-cluster-operator"
              || c.ReleaseName == "rabbitmq-cluster-operator");

        ClusterComponent? topologyOp = installed.FirstOrDefault(
            c => c.Name == "rabbitmq-messaging-topology-operator"
              || c.ReleaseName is "rabbitmq-topology-operator" or "rabbitmq-messaging-topology-operator");

        return new RabbitMQOperatorStatus
        {
            ClusterOperatorAvailable = clusterOp is not null,
            TopologyOperatorAvailable = topologyOp is not null,
            ClusterOperatorClusterName = clusterOp?.Cluster.Name,
            TopologyOperatorClusterName = topologyOp?.Cluster.Name
        };
    }

    // ── Cluster lifecycle ─────────────────────────────────────────────────────

    public async Task<RabbitMQCluster> CreateClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        string version,
        int replicas,
        string storageSize,
        string? storageClass,
        Guid? storageLinkId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (await db.RabbitMQClusters.AnyAsync(
                c => c.KubernetesClusterId == kubernetesClusterId && c.Name == name && c.Namespace == ns, ct))
            throw new InvalidOperationException($"A RabbitMQ cluster named '{name}' already exists in namespace '{ns}'.");

        RabbitMQCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            RabbitMQVersion = version,
            Replicas = replicas,
            StorageSize = storageSize,
            StorageClass = storageClass,
            StorageLinkId = storageLinkId,
            Status = RabbitMQClusterStatus.Creating
        };

        db.RabbitMQClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        try
        {
            KubernetesCluster k8sCluster = await db.KubernetesClusters
                .FirstAsync(c => c.Id == kubernetesClusterId, ct);
            string kubeconfig = k8sCluster.Kubeconfig!;

            await k8s.EnsureNamespaceAsync(ns, kubeconfig, ct);
            await k8s.ApplyManifestAsync(BuildClusterManifest(cluster), kubeconfig, ct);

            cluster.Status = RabbitMQClusterStatus.Running;
        }
        catch (Exception ex)
        {
            cluster.Status = RabbitMQClusterStatus.Failed;
            cluster.LastError = ex.Message;
        }

        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.RabbitMQClusters.Update(cluster);
        await db2.SaveChangesAsync(ct);

        // Best-effort: sync generated admin credentials to vault immediately.
        // The operator may not have provisioned the secret yet, so failures are silently ignored.
        // The operator can retry via the "Sync from K8s" button in the credentials tab.
        if (cluster.Status == RabbitMQClusterStatus.Running)
        {
            try { await SyncCredentialsToVaultAsync(tenantId, cluster.Id, ct); }
            catch { }
        }

        return cluster;
    }

    public async Task DeleteClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQCluster cluster = await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");

        cluster.Status = RabbitMQClusterStatus.Deleting;
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            await k8s.DeleteManifestAsync("rabbitmqcluster", cluster.Name, cluster.Namespace, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            cluster.Status = RabbitMQClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        db.RabbitMQClusters.Remove(cluster);
        await db.SaveChangesAsync(ct);
    }

    // ── Live K8s status ───────────────────────────────────────────────────────

    public async Task<RabbitMQClusterInfo?> GetLiveStatusAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQCluster cluster = await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            string json = await k8s.GetJsonAsync(
                $"rabbitmqcluster/{cluster.Name}", cluster.Namespace, kubeconfig, ct: ct);

            return ParseClusterStatus(json, cluster.Name, cluster.Namespace);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reconciles the EntKube status of a cluster against live K8s state.
    /// Call from a background poll or after user-triggered refresh.
    /// </summary>
    public async Task ReconcileStatusAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        RabbitMQClusterInfo? info = await GetLiveStatusAsync(tenantId, clusterId, ct);
        if (info is null) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQCluster cluster = await db.RabbitMQClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");

        if (info.ClusterAvailable && info.AllReplicasReady)
        {
            cluster.Status = RabbitMQClusterStatus.Running;
            cluster.LastError = null;
        }
        else if (cluster.Status != RabbitMQClusterStatus.Creating)
        {
            cluster.Status = RabbitMQClusterStatus.Failed;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Live topology (read from K8s topology operator CRDs) ──────────────────

    public async Task<RabbitMQLiveTopology> GetLiveTopologyAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQCluster cluster = await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string labelSelector = $"rabbitmq.com/cluster={cluster.Name}";

        RabbitMQLiveTopology topology = new();

        try
        {
            topology.Vhosts = await GetTopologyCrdNamesAsync(
                "vhosts.rabbitmq.com", cluster.Namespace, kubeconfig, labelSelector, ct);
        }
        catch { /* operator not installed or no resources */ }

        try
        {
            topology.Queues = await GetTopologyCrdNamesAsync(
                "queues.rabbitmq.com", cluster.Namespace, kubeconfig, labelSelector, ct);
        }
        catch { }

        try
        {
            topology.Exchanges = await GetTopologyCrdNamesAsync(
                "exchanges.rabbitmq.com", cluster.Namespace, kubeconfig, labelSelector, ct);
        }
        catch { }

        try
        {
            topology.Users = await GetTopologyCrdNamesAsync(
                "users.rabbitmq.com", cluster.Namespace, kubeconfig, labelSelector, ct);
        }
        catch { }

        return topology;
    }

    // ── Admin credentials ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads the admin username and password from the K8s Secret created by the cluster operator.
    /// The operator always names this secret {cluster-name}-default-user.
    /// </summary>
    public async Task<(string Username, string Password)?> GetAdminCredentialsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQCluster cluster = await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string secretName = $"{cluster.Name}-default-user";

        string? username = await k8s.GetSecretValueAsync(secretName, "username", cluster.Namespace, kubeconfig, ct);
        string? password = await k8s.GetSecretValueAsync(secretName, "password", cluster.Namespace, kubeconfig, ct);

        if (username is null || password is null) return null;
        return (username, password);
    }

    // ── Credential vault sync ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the admin credentials from the operator-created K8s secret and stores
    /// them encrypted in the tenant vault. Safe to call multiple times — overwrites
    /// the previous values. Throws if the K8s secret is not yet available.
    /// </summary>
    public async Task SyncCredentialsToVaultAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        (string Username, string Password)? creds = await GetAdminCredentialsAsync(tenantId, clusterId, ct);

        if (creds is null)
            throw new InvalidOperationException(
                "The operator secret is not yet available. The cluster may still be initializing.");

        await vaultService.SetRabbitMQClusterSecretAsync(tenantId, clusterId, "RABBITMQ_USERNAME", creds.Value.Username, ct);
        await vaultService.SetRabbitMQClusterSecretAsync(tenantId, clusterId, "RABBITMQ_PASSWORD", creds.Value.Password, ct);
    }

    /// <summary>
    /// Returns admin credentials from the vault (encrypted-at-rest).
    /// Returns null if they have not been synced yet.
    /// </summary>
    public async Task<(string Username, string Password)?> GetVaultCredentialsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        string? username = await vaultService.GetRabbitMQClusterSecretValueAsync(
            tenantId, clusterId, "RABBITMQ_USERNAME", ct);
        string? password = await vaultService.GetRabbitMQClusterSecretValueAsync(
            tenantId, clusterId, "RABBITMQ_PASSWORD", ct);

        if (username is null || password is null) return null;
        return (username, password);
    }

    // ── Backup / Restore ──────────────────────────────────────────────────────

    public async Task<List<RabbitMQBackup>> GetBackupsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RabbitMQBackups
            .Include(b => b.StorageLink)
            .Where(b => b.TenantId == tenantId && b.RabbitMQClusterId == clusterId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Exports the full broker definitions (vhosts, exchanges, queues, bindings, policies,
    /// users, permissions) via rabbitmqctl on the primary pod and uploads to S3.
    /// </summary>
    public async Task<RabbitMQBackup> CreateBackupAsync(
        Guid tenantId, Guid clusterId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQCluster cluster = await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");

        StorageLink storageLink = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        RabbitMQBackup backup = new()
        {
            Id = Guid.NewGuid(),
            RabbitMQClusterId = clusterId,
            TenantId = tenantId,
            StorageLinkId = storageLinkId,
            ObjectKey = $"rabbitmq/{cluster.Name}/{DateTime.UtcNow:yyyyMMdd-HHmmss}.json",
            ClusterName = cluster.Name,
            Status = RabbitMQBackupStatus.Creating
        };

        db.RabbitMQBackups.Add(backup);
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            string primaryPod = $"{cluster.Name}-server-0";

            // rabbitmqctl export_definitions - writes the JSON definitions to stdout.
            string json = await k8s.RunCommandOnPodAsync(
                primaryPod, cluster.Namespace,
                ["rabbitmqctl", "export_definitions", "-"],
                kubeconfig, ct: ct);

            byte[] data = Encoding.UTF8.GetBytes(json);
            await UploadToS3Async(tenantId, storageLink, backup.ObjectKey, data, ct);

            backup.SizeBytes = data.Length;
            backup.Status = RabbitMQBackupStatus.Ready;
            backup.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            backup.Status = RabbitMQBackupStatus.Failed;
            backup.LastError = ex.Message;
        }

        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.RabbitMQBackups.Update(backup);
        await db2.SaveChangesAsync(ct);

        // Prune oldest backups if over the limit.
        await PruneBackupsAsync(tenantId, clusterId, cluster.MaxBackups, ct);

        return backup;
    }

    /// <summary>
    /// Restores broker definitions from an S3 backup into the target cluster.
    /// Pipes the definitions JSON to rabbitmqctl import_definitions - on the primary pod.
    /// Existing topology that conflicts with the definitions will be overwritten.
    /// </summary>
    public async Task RestoreBackupAsync(
        Guid tenantId, Guid backupId, Guid? targetClusterId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQBackup backup = await db.RabbitMQBackups
            .Include(b => b.Cluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.StorageLink)
            .FirstOrDefaultAsync(b => b.Id == backupId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Backup not found.");

        if (backup.StorageLink is null)
            throw new InvalidOperationException("The storage link for this backup has been deleted and cannot be restored.");

        // Allow restoring into a different cluster (cross-environment migration).
        RabbitMQCluster cluster = targetClusterId.HasValue
            ? await db.RabbitMQClusters
                .Include(c => c.KubernetesCluster)
                .FirstOrDefaultAsync(c => c.Id == targetClusterId.Value && c.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("Target cluster not found.")
            : backup.Cluster;

        byte[] data = await DownloadFromS3Async(tenantId, backup.StorageLink, backup.ObjectKey, ct);
        string json = Encoding.UTF8.GetString(data);

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string primaryPod = $"{cluster.Name}-server-0";

        // rabbitmqctl import_definitions - reads JSON from stdin.
        await k8s.RunCommandOnPodWithStdinAsync(
            primaryPod, cluster.Namespace,
            ["rabbitmqctl", "import_definitions", "-"],
            json, kubeconfig, ct);
    }

    public async Task DeleteBackupAsync(Guid tenantId, Guid backupId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RabbitMQBackup backup = await db.RabbitMQBackups
            .Include(b => b.StorageLink)
            .FirstOrDefaultAsync(b => b.Id == backupId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Backup not found.");

        if (backup.StorageLink is not null)
        {
            try { await DeleteFromS3Async(tenantId, backup.StorageLink, backup.ObjectKey, ct); }
            catch { /* best-effort; remove the DB record even if S3 delete fails */ }
        }

        db.RabbitMQBackups.Remove(backup);
        await db.SaveChangesAsync(ct);
    }

    // ── Topology — Vhosts ─────────────────────────────────────────────────────

    public async Task<List<RabbitMQVhostInfo>> GetVhostsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string json = await k8s.GetJsonAsync(
            "vhosts.rabbitmq.com", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!,
            $"rabbitmq.com/cluster={cluster.Name}", ct);
        return ParseTopologyItems(json, item =>
        {
            string k8sName = GetSpecField(item, "metadata", "name") ?? "";
            string vhostName = GetSpecField(item, "spec", "name") ?? k8sName;
            return new RabbitMQVhostInfo { K8sName = k8sName, VhostName = vhostName };
        });
    }

    public async Task CreateVhostAsync(
        Guid tenantId, Guid clusterId, string vhostName, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string k8sName = ToK8sName(cluster.Name, "vh", vhostName);
        string manifest = $"""
            apiVersion: rabbitmq.com/v1beta1
            kind: Vhost
            metadata:
              name: {k8sName}
              namespace: {cluster.Namespace}
              labels:
                rabbitmq.com/cluster: {cluster.Name}
            spec:
              name: {vhostName}
              rabbitmqClusterReference:
                name: {cluster.Name}
                namespace: {cluster.Namespace}
            """;
        await k8s.ApplyManifestAsync(manifest, cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task DeleteVhostAsync(
        Guid tenantId, Guid clusterId, string k8sName, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        await k8s.DeleteManifestAsync("vhost", k8sName, cluster.Namespace,
            cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── Topology — Queues ─────────────────────────────────────────────────────

    public async Task<List<RabbitMQQueueInfo>> GetQueuesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string json = await k8s.GetJsonAsync(
            "queues.rabbitmq.com", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!,
            $"rabbitmq.com/cluster={cluster.Name}", ct);
        return ParseTopologyItems(json, item => new RabbitMQQueueInfo
        {
            K8sName = GetSpecField(item, "metadata", "name") ?? "",
            QueueName = GetSpecField(item, "spec", "name") ?? "",
            Vhost = GetSpecField(item, "spec", "vhost") ?? "/",
            QueueType = GetSpecField(item, "spec", "type") ?? "quorum",
            Durable = GetSpecBool(item, "spec", "durable") ?? true,
            AutoDelete = GetSpecBool(item, "spec", "autoDelete") ?? false
        });
    }

    public async Task CreateQueueAsync(
        Guid tenantId, Guid clusterId, string vhost, string name,
        string type, bool durable, bool autoDelete, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string k8sName = ToK8sName(cluster.Name, "q", name);
        string manifest = $"""
            apiVersion: rabbitmq.com/v1beta1
            kind: Queue
            metadata:
              name: {k8sName}
              namespace: {cluster.Namespace}
              labels:
                rabbitmq.com/cluster: {cluster.Name}
            spec:
              name: {name}
              vhost: {vhost}
              type: {type}
              durable: {durable.ToString().ToLower()}
              autoDelete: {autoDelete.ToString().ToLower()}
              rabbitmqClusterReference:
                name: {cluster.Name}
                namespace: {cluster.Namespace}
            """;
        await k8s.ApplyManifestAsync(manifest, cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task DeleteQueueAsync(
        Guid tenantId, Guid clusterId, string k8sName, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        await k8s.DeleteManifestAsync("queue", k8sName, cluster.Namespace,
            cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── Topology — Exchanges ──────────────────────────────────────────────────

    public async Task<List<RabbitMQExchangeInfo>> GetExchangesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string json = await k8s.GetJsonAsync(
            "exchanges.rabbitmq.com", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!,
            $"rabbitmq.com/cluster={cluster.Name}", ct);
        return ParseTopologyItems(json, item => new RabbitMQExchangeInfo
        {
            K8sName = GetSpecField(item, "metadata", "name") ?? "",
            ExchangeName = GetSpecField(item, "spec", "name") ?? "",
            Vhost = GetSpecField(item, "spec", "vhost") ?? "/",
            ExchangeType = GetSpecField(item, "spec", "type") ?? "direct",
            Durable = GetSpecBool(item, "spec", "durable") ?? true,
            AutoDelete = GetSpecBool(item, "spec", "autoDelete") ?? false
        });
    }

    public async Task CreateExchangeAsync(
        Guid tenantId, Guid clusterId, string vhost, string name,
        string type, bool durable, bool autoDelete, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string k8sName = ToK8sName(cluster.Name, "ex", name);
        string manifest = $"""
            apiVersion: rabbitmq.com/v1beta1
            kind: Exchange
            metadata:
              name: {k8sName}
              namespace: {cluster.Namespace}
              labels:
                rabbitmq.com/cluster: {cluster.Name}
            spec:
              name: {name}
              vhost: {vhost}
              type: {type}
              durable: {durable.ToString().ToLower()}
              autoDelete: {autoDelete.ToString().ToLower()}
              rabbitmqClusterReference:
                name: {cluster.Name}
                namespace: {cluster.Namespace}
            """;
        await k8s.ApplyManifestAsync(manifest, cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task DeleteExchangeAsync(
        Guid tenantId, Guid clusterId, string k8sName, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        await k8s.DeleteManifestAsync("exchange", k8sName, cluster.Namespace,
            cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── Topology — Routing bindings (exchange→queue/exchange) ─────────────────

    public async Task<List<RabbitMQRoutingBindingInfo>> GetRoutingBindingsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string json = await k8s.GetJsonAsync(
            "bindings.rabbitmq.com", cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!,
            $"rabbitmq.com/cluster={cluster.Name}", ct);
        return ParseTopologyItems(json, item => new RabbitMQRoutingBindingInfo
        {
            K8sName = GetSpecField(item, "metadata", "name") ?? "",
            Source = GetSpecField(item, "spec", "source") ?? "",
            Destination = GetSpecField(item, "spec", "destination") ?? "",
            DestinationType = GetSpecField(item, "spec", "destinationType") ?? "queue",
            Vhost = GetSpecField(item, "spec", "vhost") ?? "/",
            RoutingKey = GetSpecField(item, "spec", "routingKey") ?? ""
        });
    }

    public async Task CreateRoutingBindingAsync(
        Guid tenantId, Guid clusterId,
        string vhost, string source, string destination, string destType, string routingKey,
        CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string k8sName = ToK8sName(cluster.Name, "b", $"{source}-{destination}");
        string manifest = $"""
            apiVersion: rabbitmq.com/v1beta1
            kind: Binding
            metadata:
              name: {k8sName}
              namespace: {cluster.Namespace}
              labels:
                rabbitmq.com/cluster: {cluster.Name}
            spec:
              source: {source}
              destination: {destination}
              destinationType: {destType}
              vhost: {vhost}
              routingKey: {routingKey}
              rabbitmqClusterReference:
                name: {cluster.Name}
                namespace: {cluster.Namespace}
            """;
        await k8s.ApplyManifestAsync(manifest, cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task DeleteRoutingBindingAsync(
        Guid tenantId, Guid clusterId, string k8sName, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        await k8s.DeleteManifestAsync("binding", k8sName, cluster.Namespace,
            cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── Topology — Users ──────────────────────────────────────────────────────

    public async Task<List<RabbitMQUserInfo>> GetUsersAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

        string json = await k8s.GetJsonAsync(
            "users.rabbitmq.com", cluster.Namespace, kubeconfig,
            $"rabbitmq.com/cluster={cluster.Name}", ct);

        List<RabbitMQUserInfo> users = ParseTopologyItems(json, item =>
        {
            List<string> tags = [];
            if (item.TryGetProperty("spec", out JsonElement spec)
                && spec.TryGetProperty("tags", out JsonElement tagEl)
                && tagEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in tagEl.EnumerateArray())
                    if (t.GetString() is string s) tags.Add(s);
            }
            string k8sName = GetSpecField(item, "metadata", "name") ?? "";
            return new RabbitMQUserInfo
            {
                K8sName = k8sName,
                // Spec no longer has a name field in v1beta1; read from credentials secret below.
                Username = k8sName,
                Tags = tags
            };
        });

        // Enrich each user entry with the actual RabbitMQ username from the credentials secret.
        // Best-effort: leave the K8s object name as fallback if the secret doesn't exist yet.
        foreach (RabbitMQUserInfo u in users)
        {
            try
            {
                string? actualUsername = await k8s.GetSecretValueAsync(
                    $"{u.K8sName}-credentials", "username", cluster.Namespace, kubeconfig, ct);
                if (actualUsername is not null)
                    u.Username = actualUsername;
            }
            catch { }
        }

        return users;
    }

    public async Task CreateUserAsync(
        Guid tenantId, Guid clusterId, string username, string password,
        IEnumerable<string> tags, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string k8sName = ToK8sName(cluster.Name, "u", username);
        string credSecretName = $"{k8sName}-credentials";

        // Create the credential secret first so the User CRD can reference it.
        string credSecret = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {credSecretName}
              namespace: {cluster.Namespace}
            type: Opaque
            data:
              username: {B64(username)}
              password: {B64(password)}
            """;
        await k8s.ApplyManifestAsync(credSecret, cluster.KubernetesCluster.Kubeconfig!, ct);

        string tagsYaml = tags.Any()
            ? "\n  tags:\n" + string.Join("\n", tags.Select(t => $"    - {t}"))
            : "\n  tags: []";

        string manifest = $"""
            apiVersion: rabbitmq.com/v1beta1
            kind: User
            metadata:
              name: {k8sName}
              namespace: {cluster.Namespace}
              labels:
                rabbitmq.com/cluster: {cluster.Name}
            spec:{tagsYaml}
              importCredentialsSecret:
                name: {credSecretName}
              rabbitmqClusterReference:
                name: {cluster.Name}
                namespace: {cluster.Namespace}
            """;
        await k8s.ApplyManifestAsync(manifest, cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    public async Task DeleteUserAsync(
        Guid tenantId, Guid clusterId, string k8sName, CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        await k8s.DeleteManifestAsync("user", k8sName, cluster.Namespace,
            cluster.KubernetesCluster.Kubeconfig!, ct);
        // Best-effort cleanup of the credentials secret.
        try
        {
            await k8s.DeleteManifestAsync("secret", $"{k8sName}-credentials",
                cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct);
        }
        catch { }
    }

    // ── Topology — Permissions ────────────────────────────────────────────────

    public async Task SetPermissionAsync(
        Guid tenantId, Guid clusterId,
        string vhost, string username, string configure, string write, string read,
        CancellationToken ct = default)
    {
        RabbitMQCluster cluster = await LoadClusterAsync(tenantId, clusterId, ct);
        string k8sName = ToK8sName(cluster.Name, "perm", $"{username}-{vhost}");
        string manifest = $"""
            apiVersion: rabbitmq.com/v1beta1
            kind: Permission
            metadata:
              name: {k8sName}
              namespace: {cluster.Namespace}
              labels:
                rabbitmq.com/cluster: {cluster.Name}
            spec:
              vhost: {vhost}
              user: {username}
              permissions:
                configure: "{configure}"
                write: "{write}"
                read: "{read}"
              rabbitmqClusterReference:
                name: {cluster.Name}
                namespace: {cluster.Namespace}
            """;
        await k8s.ApplyManifestAsync(manifest, cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── App bindings (MessagingBinding) ───────────────────────────────────────

    public async Task<List<MessagingBinding>> GetMessagingBindingsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.MessagingBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.App).ThenInclude(a => a.Customer)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .Where(b => b.TenantId == tenantId && b.RabbitMQClusterId == clusterId)
            .OrderBy(b => b.AppDeployment.Name)
            .ToListAsync(ct);
    }

    public async Task<List<MessagingBinding>> GetMessagingBindingsForDeploymentAsync(
        Guid appDeploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.MessagingBindings
            .Include(b => b.Cluster)
            .Where(b => b.AppDeploymentId == appDeploymentId)
            .OrderBy(b => b.Vhost).ThenBy(b => b.QueueName ?? b.ExchangeName)
            .ToListAsync(ct);
    }

    public async Task<MessagingBinding> CreateMessagingBindingAsync(
        Guid tenantId, Guid clusterId, Guid appDeploymentId,
        string vhost, string? queueName, string? exchangeName,
        string k8sSecretName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MessagingBinding binding = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RabbitMQClusterId = clusterId,
            AppDeploymentId = appDeploymentId,
            Vhost = vhost,
            QueueName = queueName,
            ExchangeName = exchangeName,
            KubernetesSecretName = k8sSecretName,
            SyncEnabled = true
        };

        db.MessagingBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        // Sync immediately.
        await SyncMessagingBindingAsync(tenantId, binding.Id, ct);

        return binding;
    }

    public async Task DeleteMessagingBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MessagingBinding binding = await db.MessagingBindings
            .Include(b => b.Cluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Messaging binding not found.");

        // Best-effort: remove the K8s secret from the app's namespace.
        try
        {
            string kubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
            await k8s.DeleteManifestAsync(
                "secret", binding.KubernetesSecretName,
                binding.AppDeployment.Namespace, kubeconfig, ct);
        }
        catch { }

        // Best-effort: delete the dedicated RabbitMQ user from the cluster.
        try
        {
            string appUsername = BindingUsername(bindingId);
            string k8sUserName = ToK8sName(binding.Cluster.Name, "u", appUsername);
            string rmqKubeconfig = binding.Cluster.KubernetesCluster.Kubeconfig!;
            await k8s.DeleteManifestAsync("user", k8sUserName, binding.Cluster.Namespace, rmqKubeconfig, ct);
            await k8s.DeleteManifestAsync("secret", $"{k8sUserName}-credentials",
                binding.Cluster.Namespace, rmqKubeconfig, ct);
        }
        catch { }

        // Best-effort: remove the vault password for this binding.
        try
        {
            await vaultService.DeleteRabbitMQClusterSecretAsync(
                tenantId, binding.RabbitMQClusterId, BindingPasswordVaultKey(bindingId), ct);
        }
        catch { }

        db.MessagingBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    public async Task SyncMessagingBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MessagingBinding binding = await db.MessagingBindings
            .Include(b => b.Cluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Messaging binding not found.");

        // Use a dedicated per-binding RabbitMQ user instead of admin credentials.
        string appUsername = BindingUsername(bindingId);
        string passwordKey = BindingPasswordVaultKey(bindingId);

        string? appPassword = await vaultService.GetRabbitMQClusterSecretValueAsync(
            tenantId, binding.RabbitMQClusterId, passwordKey, ct);

        if (appPassword is null)
        {
            // First sync: provision the dedicated user on the RabbitMQ cluster.
            appPassword = GeneratePassword();
            await CreateUserAsync(tenantId, binding.RabbitMQClusterId, appUsername, appPassword, [], ct);
            await SetPermissionAsync(tenantId, binding.RabbitMQClusterId,
                binding.Vhost, appUsername, ".*", ".*", ".*", ct);
            await vaultService.SetRabbitMQClusterSecretAsync(
                tenantId, binding.RabbitMQClusterId, passwordKey, appPassword, ct);
        }

        string host = $"{binding.Cluster.Name}-svc.{binding.Cluster.Namespace}.svc.cluster.local";
        string encodedVhost = Uri.EscapeDataString(binding.Vhost == "/" ? "/" : binding.Vhost.TrimStart('/'));
        string amqpUrl = $"amqp://{appUsername}:{appPassword}@{host}:5672/{encodedVhost}";

        StringBuilder secretData = new();
        secretData.AppendLine($"  RABBITMQ_HOST: {B64(host)}");
        secretData.AppendLine($"  RABBITMQ_PORT: {B64("5672")}");
        secretData.AppendLine($"  RABBITMQ_VHOST: {B64(binding.Vhost)}");
        secretData.AppendLine($"  RABBITMQ_USERNAME: {B64(appUsername)}");
        secretData.AppendLine($"  RABBITMQ_PASSWORD: {B64(appPassword)}");
        secretData.AppendLine($"  RABBITMQ_URL: {B64(amqpUrl)}");

        if (!string.IsNullOrEmpty(binding.QueueName))
            secretData.AppendLine($"  RABBITMQ_QUEUE: {B64(binding.QueueName)}");
        if (!string.IsNullOrEmpty(binding.ExchangeName))
            secretData.AppendLine($"  RABBITMQ_EXCHANGE: {B64(binding.ExchangeName)}");

        string appKubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
        await k8s.EnsureNamespaceAsync(binding.AppDeployment.Namespace, appKubeconfig, ct);

        string secretManifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {binding.KubernetesSecretName}
              namespace: {binding.AppDeployment.Namespace}
            type: Opaque
            data:
            {secretData}
            """;

        await k8s.ApplyManifestAsync(secretManifest, appKubeconfig, ct);

        binding.LastSyncedAt = DateTime.UtcNow;
        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.MessagingBindings.Update(binding);
        await db2.SaveChangesAsync(ct);
    }

    // ── Manifest building ─────────────────────────────────────────────────────

    private static string BuildClusterManifest(RabbitMQCluster cluster)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: rabbitmq.com/v1beta1");
        sb.AppendLine("kind: RabbitmqCluster");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {cluster.Name}");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {cluster.Replicas}");
        sb.AppendLine($"  image: rabbitmq:{cluster.RabbitMQVersion}-management");
        sb.AppendLine("  persistence:");
        sb.AppendLine($"    storage: {cluster.StorageSize}");

        if (!string.IsNullOrEmpty(cluster.StorageClass))
            sb.AppendLine($"    storageClassName: {cluster.StorageClass}");

        sb.AppendLine("  resources:");
        sb.AppendLine("    requests:");
        sb.AppendLine("      cpu: 200m");
        sb.AppendLine("      memory: 512Mi");
        sb.AppendLine("    limits:");
        sb.AppendLine("      cpu: '2'");
        sb.AppendLine("      memory: 2Gi");

        // Enable the management plugin (included in the -management image tag).
        sb.AppendLine("  rabbitmq:");
        sb.AppendLine("    additionalPlugins:");
        sb.AppendLine("      - rabbitmq_management");
        sb.AppendLine("      - rabbitmq_peer_discovery_k8s");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RabbitMQClusterInfo? ParseClusterStatus(string json, string name, string ns)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            bool allReady = false;
            bool available = false;

            if (root.TryGetProperty("status", out JsonElement status)
                && status.TryGetProperty("conditions", out JsonElement conditions))
            {
                foreach (JsonElement condition in conditions.EnumerateArray())
                {
                    string? type = condition.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                    string? condStatus = condition.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;

                    if (type == "AllReplicasReady" && condStatus == "True") allReady = true;
                    if (type == "ClusterAvailable" && condStatus == "True") available = true;
                }
            }

            return new RabbitMQClusterInfo
            {
                Name = name,
                Namespace = ns,
                AllReplicasReady = allReady,
                ClusterAvailable = available
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<string>> GetTopologyCrdNamesAsync(
        string crdResource, string ns, string kubeconfig, string labelSelector, CancellationToken ct)
    {
        string json = await k8s.GetJsonAsync(crdResource, ns, kubeconfig, labelSelector, ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        List<string> names = [];

        if (doc.RootElement.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.TryGetProperty("metadata", out JsonElement meta)
                    && meta.TryGetProperty("name", out JsonElement nameEl))
                {
                    string? name = nameEl.GetString();
                    if (name is not null) names.Add(name);
                }
            }
        }

        return names;
    }

    private async Task PruneBackupsAsync(
        Guid tenantId, Guid clusterId, int maxBackups, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<RabbitMQBackup> backups = await db.RabbitMQBackups
            .Include(b => b.StorageLink)
            .Where(b => b.TenantId == tenantId && b.RabbitMQClusterId == clusterId
                        && b.Status == RabbitMQBackupStatus.Ready)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        List<RabbitMQBackup> toDelete = backups.Skip(maxBackups).ToList();

        foreach (RabbitMQBackup old in toDelete)
        {
            if (old.StorageLink is not null)
            {
                try { await DeleteFromS3Async(tenantId, old.StorageLink, old.ObjectKey, ct); }
                catch { }
            }

            db.RabbitMQBackups.Remove(old);
        }

        if (toDelete.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    // ── App deployment listing ────────────────────────────────────────────────

    public async Task<List<AppDeployment>> GetTenantDeploymentsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppDeployments
            .Include(d => d.App).ThenInclude(a => a.Customer)
            .Include(d => d.Cluster)
            .Where(d => d.App.Customer.TenantId == tenantId)
            .OrderBy(d => d.App.Customer.Name).ThenBy(d => d.App.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    // ── Topology helpers ──────────────────────────────────────────────────────

    private async Task<RabbitMQCluster> LoadClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.RabbitMQClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("RabbitMQ cluster not found.");
    }

    private static string ToK8sName(string clusterName, string prefix, string resourceName)
    {
        string sanitized = Regex.Replace(resourceName.ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
        if (sanitized.Length > 40) sanitized = sanitized[..40].TrimEnd('-');
        string name = $"{clusterName}-{prefix}-{sanitized}";
        if (name.Length > 63) name = name[..63].TrimEnd('-');
        return name;
    }

    private static string B64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string GeneratePassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(24, chars, static (span, c) =>
        {
            Span<byte> bytes = stackalloc byte[24];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            for (int i = 0; i < span.Length; i++)
                span[i] = c[bytes[i] % c.Length];
        });
    }

    private static string BindingUsername(Guid bindingId) => $"mb-{bindingId:N}";

    private static string BindingPasswordVaultKey(Guid bindingId) => $"MB_{bindingId:N}_PASSWORD";

    private static List<T> ParseTopologyItems<T>(string json, Func<JsonElement, T> selector)
    {
        List<T> results = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out JsonElement items))
                foreach (JsonElement item in items.EnumerateArray())
                    results.Add(selector(item));
        }
        catch { }
        return results;
    }

    private static string? GetSpecField(JsonElement item, string section, string field)
    {
        if (item.TryGetProperty(section, out JsonElement sec)
            && sec.TryGetProperty(field, out JsonElement f))
            return f.ValueKind == JsonValueKind.Null ? null : f.GetString();
        return null;
    }

    private static bool? GetSpecBool(JsonElement item, string section, string field)
    {
        if (item.TryGetProperty(section, out JsonElement sec)
            && sec.TryGetProperty(field, out JsonElement f)
            && (f.ValueKind == JsonValueKind.True || f.ValueKind == JsonValueKind.False))
            return f.GetBoolean();
        return null;
    }

    // ── S3 helpers ────────────────────────────────────────────────────────────

    private async Task UploadToS3Async(
        Guid tenantId, StorageLink link, string key, byte[] data, CancellationToken ct)
    {
        using AmazonS3Client s3 = await CreateS3ClientAsync(tenantId, link, ct);

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = link.BucketName,
            Key = key,
            InputStream = new MemoryStream(data),
            ContentType = "application/json"
        }, ct);
    }

    private async Task<byte[]> DownloadFromS3Async(
        Guid tenantId, StorageLink link, string key, CancellationToken ct)
    {
        using AmazonS3Client s3 = await CreateS3ClientAsync(tenantId, link, ct);

        GetObjectResponse obj = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = link.BucketName,
            Key = key
        }, ct);

        using MemoryStream ms = new();
        await obj.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task DeleteFromS3Async(
        Guid tenantId, StorageLink link, string key, CancellationToken ct)
    {
        using AmazonS3Client s3 = await CreateS3ClientAsync(tenantId, link, ct);

        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = link.BucketName,
            Key = key
        }, ct);
    }

    private async Task<AmazonS3Client> CreateS3ClientAsync(
        Guid tenantId, StorageLink link, CancellationToken ct)
    {
        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(
            tenantId, link.Id, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(
            tenantId, link.Id, "SECRET_KEY", ct);

        return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = link.Endpoint,
            ForcePathStyle = true
        });
    }
}
