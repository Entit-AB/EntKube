using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class KafkaOperatorStatus
{
    public bool OperatorAvailable { get; set; }
    public string? OperatorClusterName { get; set; }
}

public class KafkaPodInfo
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public bool Ready { get; set; }
    public string? Node { get; set; }
    public int Restarts { get; set; }
}

public class KafkaClusterDetail
{
    public required KafkaCluster Cluster { get; set; }
    public string Phase { get; set; } = "Querying...";
    public bool Ready { get; set; }
    public List<KafkaPodInfo> Pods { get; set; } = [];
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Manages Apache Kafka clusters via the Strimzi operator.
///
/// Applies a <c>Kafka</c> CR plus a dual-role <c>KafkaNodePool</c> CR (KRaft), and
/// manages <c>KafkaTopic</c> and <c>KafkaUser</c> (SCRAM-SHA-512 + simple ACLs) CRs.
/// App bindings sync the bootstrap address and, for auth-enabled clusters, the
/// SASL credentials + cluster CA into the consuming app's namespace.
///
/// Broker replication factors are derived from the broker count so single-node
/// (dev) and multi-node (production) clusters both produce a valid config.
/// </summary>
public class KafkaService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    VaultService vaultService)
{
    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<KafkaCluster>> GetClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KafkaClusters
            .Include(c => c.KubernetesCluster).ThenInclude(k => k.Environment)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<KafkaOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? op = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Cluster.TenantId == tenantId
                && c.Status == ComponentStatus.Installed
                && (c.Name == "strimzi-kafka-operator"
                    || c.ReleaseName == "strimzi-kafka-operator"
                    || (c.HelmChartName ?? "") == "strimzi-kafka-operator"), ct);

        return new KafkaOperatorStatus
        {
            OperatorAvailable = op is not null,
            OperatorClusterName = op?.Cluster.Name
        };
    }

    // ── Cluster lifecycle ─────────────────────────────────────────────────────

    public async Task<KafkaCluster> CreateClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        int replicas,
        string kafkaVersion,
        string storageSize,
        string? storageClass,
        bool authEnabled,
        string? cpuRequest,
        string? memoryRequest,
        string? memoryLimit,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (await db.KafkaClusters.AnyAsync(
                c => c.KubernetesClusterId == kubernetesClusterId
                    && c.Name == name
                    && c.Namespace == ns, ct))
            throw new InvalidOperationException(
                $"A Kafka cluster named '{name}' already exists in namespace '{ns}'.");

        KafkaCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            Replicas = replicas,
            KafkaVersion = kafkaVersion,
            StorageSize = storageSize,
            StorageClass = storageClass,
            AuthEnabled = authEnabled,
            CpuRequest = cpuRequest,
            MemoryRequest = memoryRequest,
            MemoryLimit = memoryLimit,
            Status = KafkaClusterStatus.Creating
        };

        db.KafkaClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        try
        {
            KubernetesCluster k8sCluster = await db.KubernetesClusters
                .FirstAsync(c => c.Id == kubernetesClusterId, ct);
            string kubeconfig = k8sCluster.Kubeconfig!;

            await vaultService.SetKafkaClusterSecretAsync(
                tenantId, cluster.Id, "KAFKA_BOOTSTRAP_SERVERS", cluster.BootstrapAddress, ct);

            await k8s.EnsureNamespaceAsync(ns, kubeconfig, ct);
            await k8s.ApplyManifestAsync(BuildNodePoolManifest(cluster), kubeconfig, ct);
            await k8s.ApplyManifestAsync(BuildClusterManifest(cluster), kubeconfig, ct);
        }
        catch (Exception ex)
        {
            cluster.Status = KafkaClusterStatus.Failed;
            cluster.LastError = ex.Message;
        }

        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.KafkaClusters.Update(cluster);
        await db2.SaveChangesAsync(ct);

        return cluster;
    }

    /// <summary>
    /// Updates a cluster's broker count, storage size, version, or resources and
    /// re-applies the CRs. Note: Strimzi does not shrink storage; increasing the
    /// broker replica count is safe, decreasing may require partition reassignment.
    /// </summary>
    public async Task UpdateClusterAsync(
        Guid tenantId, Guid clusterId,
        int replicas, string kafkaVersion, string storageSize, string? storageClass,
        string? cpuRequest, string? memoryRequest, string? memoryLimit,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaCluster cluster = await db.KafkaClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Kafka cluster not found.");

        cluster.Replicas = replicas;
        cluster.KafkaVersion = kafkaVersion;
        cluster.StorageSize = storageSize;
        cluster.StorageClass = storageClass;
        cluster.CpuRequest = cpuRequest;
        cluster.MemoryRequest = memoryRequest;
        cluster.MemoryLimit = memoryLimit;
        cluster.Status = KafkaClusterStatus.Updating;
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            await k8s.ApplyManifestAsync(BuildNodePoolManifest(cluster), kubeconfig, ct);
            await k8s.ApplyManifestAsync(BuildClusterManifest(cluster), kubeconfig, ct);
        }
        catch (Exception ex)
        {
            cluster.Status = KafkaClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaCluster cluster = await db.KafkaClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Kafka cluster not found.");

        cluster.Status = KafkaClusterStatus.Deleting;
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            // Deleting the Kafka CR cascades to broker pods/PVCs; remove the node pool too.
            await k8s.DeleteManifestAsync("kafka", cluster.Name, cluster.Namespace, kubeconfig, ct);
            await k8s.DeleteManifestAsync("kafkanodepool", $"{cluster.Name}-pool", cluster.Namespace, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            cluster.Status = KafkaClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        db.KafkaClusters.Remove(cluster);
        await db.SaveChangesAsync(ct);
    }

    // ── Live detail + status reconciliation ────────────────────────────────────

    public async Task<KafkaClusterDetail?> GetClusterDetailAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaCluster? cluster = await db.KafkaClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        if (cluster is null) return null;

        KafkaClusterDetail detail = new() { Cluster = cluster };

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

            string crdJson = await k8s.GetJsonAsync(
                $"kafka.kafka.strimzi.io/{cluster.Name}", cluster.Namespace, kubeconfig, ct: ct);
            detail.Ready = ParseReadyCondition(crdJson);

            string podsJson = await k8s.GetJsonAsync(
                "pods", cluster.Namespace, kubeconfig, $"strimzi.io/cluster={cluster.Name}", ct);
            detail.Pods = ParsePodList(podsJson);

            int readyPods = detail.Pods.Count(p => p.Ready);
            detail.Phase = detail.Ready
                ? "Cluster ready"
                : detail.Pods.Count == 0 ? "Provisioning..." : $"Starting ({readyPods}/{detail.Pods.Count} ready)";

            await ReconcileStatusAsync(cluster.Id, detail.Ready, ct);
        }
        catch
        {
            detail.Phase = "Unable to reach cluster";
        }

        return detail;
    }

    /// <summary>
    /// Polls the live Kafka CR and updates the stored status. Safe to call from a
    /// background loop. Does not regress a Running cluster to Creating on a transient blip.
    /// </summary>
    public async Task ReconcileStatusAsync(Guid clusterId, bool ready, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KafkaCluster? tracked = await db.KafkaClusters.FindAsync([clusterId], ct);
        if (tracked is null) return;

        KafkaClusterStatus newStatus = ready
            ? KafkaClusterStatus.Running
            : tracked.Status == KafkaClusterStatus.Running
                ? KafkaClusterStatus.Running
                : tracked.Status == KafkaClusterStatus.Deleting
                    ? KafkaClusterStatus.Deleting
                    : KafkaClusterStatus.Creating;

        if (newStatus != tracked.Status)
        {
            tracked.Status = newStatus;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Reconciles every tenant's clusters — used by the background poller.</summary>
    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<KafkaCluster> clusters = await db.KafkaClusters
            .Include(c => c.KubernetesCluster)
            .Where(c => c.Status != KafkaClusterStatus.Failed)
            .ToListAsync(ct);

        foreach (KafkaCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.KubernetesCluster.Kubeconfig)) continue;
            try
            {
                string crdJson = await k8s.GetJsonAsync(
                    $"kafka.kafka.strimzi.io/{cluster.Name}", cluster.Namespace,
                    cluster.KubernetesCluster.Kubeconfig!, ct: ct);
                await ReconcileStatusAsync(cluster.Id, ParseReadyCondition(crdJson), ct);
            }
            catch { /* cluster unreachable — leave status as-is */ }
        }
    }

    // ── Topics ─────────────────────────────────────────────────────────────────

    public async Task<List<KafkaTopic>> GetTopicsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KafkaTopics
            .Where(t => t.TenantId == tenantId && t.KafkaClusterId == clusterId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task<KafkaTopic> CreateTopicAsync(
        Guid tenantId, Guid clusterId, string name, int partitions, int replicas,
        long? retentionMs, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaCluster cluster = await db.KafkaClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Kafka cluster not found.");

        if (await db.KafkaTopics.AnyAsync(t => t.KafkaClusterId == clusterId && t.Name == name, ct))
            throw new InvalidOperationException($"Topic '{name}' already exists on this cluster.");

        KafkaTopic topic = new()
        {
            Id = Guid.NewGuid(),
            KafkaClusterId = clusterId,
            TenantId = tenantId,
            Name = name,
            Partitions = partitions,
            Replicas = replicas,
            RetentionMs = retentionMs
        };

        await k8s.ApplyManifestAsync(BuildTopicManifest(cluster, topic), cluster.KubernetesCluster.Kubeconfig!, ct);

        db.KafkaTopics.Add(topic);
        await db.SaveChangesAsync(ct);
        return topic;
    }

    public async Task DeleteTopicAsync(Guid tenantId, Guid topicId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaTopic topic = await db.KafkaTopics
            .Include(t => t.KafkaCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(t => t.Id == topicId && t.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Topic not found.");

        try
        {
            await k8s.DeleteManifestAsync("kafkatopic", TopicResourceName(topic),
                topic.KafkaCluster.Namespace, topic.KafkaCluster.KubernetesCluster.Kubeconfig!, ct);
        }
        catch { /* best-effort; remove local record regardless */ }

        db.KafkaTopics.Remove(topic);
        await db.SaveChangesAsync(ct);
    }

    // ── Users (SCRAM + ACLs) ────────────────────────────────────────────────────

    public async Task<List<KafkaUser>> GetUsersAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KafkaUsers
            .Where(u => u.TenantId == tenantId && u.KafkaClusterId == clusterId)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
    }

    public async Task<KafkaUser> CreateUserAsync(
        Guid tenantId, Guid clusterId, string username,
        string? producerTopics, string? consumerTopics, string? consumerGroup, bool superUser,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaCluster cluster = await db.KafkaClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Kafka cluster not found.");

        if (!cluster.AuthEnabled)
            throw new InvalidOperationException(
                "This cluster has authentication disabled, so it cannot have Kafka users. Enable SCRAM/TLS on the cluster first.");

        if (await db.KafkaUsers.AnyAsync(u => u.KafkaClusterId == clusterId && u.Username == username, ct))
            throw new InvalidOperationException($"User '{username}' already exists on this cluster.");

        KafkaUser user = new()
        {
            Id = Guid.NewGuid(),
            KafkaClusterId = clusterId,
            TenantId = tenantId,
            Username = username,
            ProducerTopics = producerTopics,
            ConsumerTopics = consumerTopics,
            ConsumerGroup = string.IsNullOrWhiteSpace(consumerGroup) ? "*" : consumerGroup,
            SuperUser = superUser
        };

        await k8s.ApplyManifestAsync(BuildUserManifest(cluster, user), cluster.KubernetesCluster.Kubeconfig!, ct);

        db.KafkaUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task DeleteUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaUser user = await db.KafkaUsers
            .Include(u => u.KafkaCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("User not found.");

        try
        {
            await k8s.DeleteManifestAsync("kafkauser", user.Username,
                user.KafkaCluster.Namespace, user.KafkaCluster.KubernetesCluster.Kubeconfig!, ct);
        }
        catch { /* best-effort */ }

        db.KafkaUsers.Remove(user);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reads a Kafka user's SCRAM password from the Strimzi-generated Secret.</summary>
    public async Task<string?> GetUserPasswordAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KafkaUser? user = await db.KafkaUsers
            .Include(u => u.KafkaCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        if (user is null) return null;

        return await k8s.GetSecretValueAsync(user.CredentialsSecretName, "password",
            user.KafkaCluster.Namespace, user.KafkaCluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── App bindings ────────────────────────────────────────────────────────────

    public async Task<List<AppDeployment>> GetTenantDeploymentsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppDeployments
            .Include(d => d.App).ThenInclude(a => a.Customer)
            .Include(d => d.Cluster)
            .Where(d => d.App.Customer.TenantId == tenantId)
            .OrderBy(d => d.App.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    public async Task<List<KafkaBinding>> GetBindingsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KafkaBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.App).ThenInclude(a => a.Customer)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .Include(b => b.KafkaUser)
            .Where(b => b.TenantId == tenantId && b.KafkaClusterId == clusterId)
            .OrderBy(b => b.AppDeployment.Name)
            .ToListAsync(ct);
    }

    public async Task<List<KafkaBinding>> GetBindingsForDeploymentAsync(
        Guid appDeploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KafkaBindings
            .Include(b => b.KafkaCluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.KafkaUser)
            .Where(b => b.AppDeploymentId == appDeploymentId)
            .OrderBy(b => b.KafkaCluster.Name)
            .ToListAsync(ct);
    }

    public async Task<KafkaBinding> CreateBindingAsync(
        Guid tenantId, Guid clusterId, Guid appDeploymentId, Guid? kafkaUserId,
        string k8sSecretName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaBinding binding = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KafkaClusterId = clusterId,
            AppDeploymentId = appDeploymentId,
            KafkaUserId = kafkaUserId,
            KubernetesSecretName = k8sSecretName,
            SyncEnabled = true
        };

        db.KafkaBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await SyncBindingAsync(tenantId, binding.Id, ct);
        return binding;
    }

    public async Task DeleteBindingAsync(Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaBinding binding = await db.KafkaBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Kafka binding not found.");

        try
        {
            await k8s.DeleteManifestAsync("secret", binding.KubernetesSecretName,
                binding.AppDeployment.Namespace, binding.AppDeployment.Cluster.Kubeconfig!, ct);
        }
        catch { }

        db.KafkaBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    public async Task SyncBindingAsync(Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KafkaBinding binding = await db.KafkaBindings
            .Include(b => b.KafkaCluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.KafkaUser)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Kafka binding not found.");

        KafkaCluster cluster = binding.KafkaCluster;
        string appKubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
        string appNs = binding.AppDeployment.Namespace;

        Dictionary<string, string> data = new()
        {
            ["KAFKA_BOOTSTRAP_SERVERS"] = cluster.BootstrapAddress,
        };

        if (cluster.AuthEnabled)
        {
            if (binding.KafkaUser is null)
                throw new InvalidOperationException(
                    "This cluster requires SASL authentication — select a Kafka user for the binding.");

            string? password = await k8s.GetSecretValueAsync(
                binding.KafkaUser.CredentialsSecretName, "password",
                cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct)
                ?? throw new InvalidOperationException(
                    "The Kafka user's password secret is not available yet — the user may still be provisioning.");

            string? caCert = await k8s.GetSecretValueAsync(
                $"{cluster.Name}-cluster-ca-cert", "ca.crt",
                cluster.Namespace, cluster.KubernetesCluster.Kubeconfig!, ct);

            data["KAFKA_SECURITY_PROTOCOL"] = "SASL_SSL";
            data["KAFKA_SASL_MECHANISM"] = "SCRAM-SHA-512";
            data["KAFKA_SASL_USERNAME"] = binding.KafkaUser.Username;
            data["KAFKA_SASL_PASSWORD"] = password;
            if (!string.IsNullOrEmpty(caCert)) data["KAFKA_CA_CRT"] = caCert;
        }
        else
        {
            data["KAFKA_SECURITY_PROTOCOL"] = "PLAINTEXT";
        }

        await k8s.EnsureNamespaceAsync(appNs, appKubeconfig, ct);
        await k8s.ApplyManifestAsync(BuildBindingSecretManifest(binding.KubernetesSecretName, appNs, data), appKubeconfig, ct);

        binding.LastSyncedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ── Manifest builders ─────────────────────────────────────────────────────

    private static string BuildBindingSecretManifest(string name, string ns, Dictionary<string, string> data)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    app.kubernetes.io/managed-by: entkube");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        foreach ((string key, string value) in data)
            sb.AppendLine($"  {key}: {Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}");
        return sb.ToString();
    }

    private static string BuildNodePoolManifest(KafkaCluster cluster)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: kafka.strimzi.io/v1beta2");
        sb.AppendLine("kind: KafkaNodePool");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {cluster.Name}-pool");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    strimzi.io/cluster: {cluster.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {cluster.Replicas}");
        sb.AppendLine("  roles:");
        sb.AppendLine("    - controller");
        sb.AppendLine("    - broker");
        sb.AppendLine("  storage:");
        sb.AppendLine("    type: jbod");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - id: 0");
        sb.AppendLine("        type: persistent-claim");
        sb.AppendLine($"        size: {cluster.StorageSize}");
        sb.AppendLine("        deleteClaim: false");
        sb.AppendLine("        kraftMetadata: shared");
        if (!string.IsNullOrEmpty(cluster.StorageClass))
            sb.AppendLine($"        class: {cluster.StorageClass}");
        if (!string.IsNullOrEmpty(cluster.CpuRequest) || !string.IsNullOrEmpty(cluster.MemoryRequest)
            || !string.IsNullOrEmpty(cluster.MemoryLimit))
        {
            sb.AppendLine("  resources:");
            if (!string.IsNullOrEmpty(cluster.CpuRequest) || !string.IsNullOrEmpty(cluster.MemoryRequest))
            {
                sb.AppendLine("    requests:");
                if (!string.IsNullOrEmpty(cluster.CpuRequest)) sb.AppendLine($"      cpu: {cluster.CpuRequest}");
                if (!string.IsNullOrEmpty(cluster.MemoryRequest)) sb.AppendLine($"      memory: {cluster.MemoryRequest}");
            }
            if (!string.IsNullOrEmpty(cluster.MemoryLimit))
            {
                sb.AppendLine("    limits:");
                sb.AppendLine($"      memory: {cluster.MemoryLimit}");
            }
        }
        return sb.ToString();
    }

    private static string BuildClusterManifest(KafkaCluster cluster)
    {
        int rf = Math.Min(cluster.Replicas, 3);
        int minIsr = Math.Max(1, Math.Min(cluster.Replicas - 1, 2));

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: kafka.strimzi.io/v1beta2");
        sb.AppendLine("kind: Kafka");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {cluster.Name}");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("  annotations:");
        sb.AppendLine("    strimzi.io/node-pools: enabled");
        sb.AppendLine("    strimzi.io/kraft: enabled");
        sb.AppendLine("spec:");
        sb.AppendLine("  kafka:");
        sb.AppendLine($"    version: {cluster.KafkaVersion}");
        sb.AppendLine("    listeners:");
        if (cluster.AuthEnabled)
        {
            // TLS listener with SCRAM-SHA-512 authentication (production).
            sb.AppendLine("      - name: tls");
            sb.AppendLine($"        port: {KafkaCluster.TlsPort}");
            sb.AppendLine("        type: internal");
            sb.AppendLine("        tls: true");
            sb.AppendLine("        authentication:");
            sb.AppendLine("          type: scram-sha-512");
        }
        else
        {
            // Plaintext internal listener — rely on NetworkPolicy for isolation.
            sb.AppendLine("      - name: plain");
            sb.AppendLine($"        port: {KafkaCluster.PlaintextPort}");
            sb.AppendLine("        type: internal");
            sb.AppendLine("        tls: false");
        }
        if (cluster.AuthEnabled)
        {
            sb.AppendLine("    authorization:");
            sb.AppendLine("      type: simple");
        }
        sb.AppendLine("    config:");
        sb.AppendLine($"      offsets.topic.replication.factor: {rf}");
        sb.AppendLine($"      transaction.state.log.replication.factor: {rf}");
        sb.AppendLine($"      transaction.state.log.min.isr: {minIsr}");
        sb.AppendLine($"      default.replication.factor: {rf}");
        sb.AppendLine($"      min.insync.replicas: {minIsr}");
        // Mimir (and other producers) can emit ~15.2 MB records; the broker default
        // message.max.bytes of 1 MB would reject them. Raise to 16 MB.
        sb.AppendLine("      message.max.bytes: 16777216");
        sb.AppendLine("      replica.fetch.max.bytes: 16777216");
        sb.AppendLine("  entityOperator:");
        sb.AppendLine("    topicOperator: {}");
        sb.AppendLine("    userOperator: {}");
        return sb.ToString();
    }

    private static string BuildTopicManifest(KafkaCluster cluster, KafkaTopic topic)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: kafka.strimzi.io/v1beta2");
        sb.AppendLine("kind: KafkaTopic");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {TopicResourceName(topic)}");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    strimzi.io/cluster: {cluster.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  topicName: {topic.Name}");
        sb.AppendLine($"  partitions: {topic.Partitions}");
        sb.AppendLine($"  replicas: {Math.Min(topic.Replicas, cluster.Replicas)}");
        if (topic.RetentionMs.HasValue)
        {
            sb.AppendLine("  config:");
            sb.AppendLine($"    retention.ms: {topic.RetentionMs.Value}");
        }
        return sb.ToString();
    }

    private static string BuildUserManifest(KafkaCluster cluster, KafkaUser user)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: kafka.strimzi.io/v1beta2");
        sb.AppendLine("kind: KafkaUser");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {user.Username}");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    strimzi.io/cluster: {cluster.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine("  authentication:");
        sb.AppendLine("    type: scram-sha-512");
        sb.AppendLine("  authorization:");
        sb.AppendLine("    type: simple");
        sb.AppendLine("    acls:");
        if (user.SuperUser)
        {
            // Full access to all topics, groups, and cluster operations.
            AppendAcl(sb, "topic", "*", "literal", ["All"]);
            AppendAcl(sb, "group", "*", "literal", ["All"]);
            AppendAcl(sb, "cluster", "kafka-cluster", "literal", ["All"]);
        }
        else
        {
            foreach (string t in SplitTopics(user.ProducerTopics))
                AppendAcl(sb, "topic", t, t == "*" ? "literal" : "literal", ["Write", "Create", "Describe"]);
            foreach (string t in SplitTopics(user.ConsumerTopics))
                AppendAcl(sb, "topic", t, "literal", ["Read", "Describe"]);
            if (!string.IsNullOrWhiteSpace(user.ConsumerTopics))
                AppendAcl(sb, "group", string.IsNullOrWhiteSpace(user.ConsumerGroup) ? "*" : user.ConsumerGroup!,
                    "literal", ["Read"]);
        }
        return sb.ToString();
    }

    private static void AppendAcl(StringBuilder sb, string resourceType, string resourceName,
        string patternType, string[] operations)
    {
        sb.AppendLine("      - resource:");
        sb.AppendLine($"          type: {resourceType}");
        sb.AppendLine($"          name: \"{resourceName}\"");
        sb.AppendLine($"          patternType: {patternType}");
        sb.AppendLine("        operations:");
        foreach (string op in operations)
            sb.AppendLine($"          - {op}");
    }

    private static IEnumerable<string> SplitTopics(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // KafkaTopic resource names must be DNS-safe; a topic name may contain '.' or '_'.
    // Use a sanitized metadata.name and carry the true name in spec.topicName.
    private static string TopicResourceName(KafkaTopic topic) =>
        new string([.. topic.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');

    // ── JSON parsing ────────────────────────────────────────────────────────────

    private static bool ParseReadyCondition(string json)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out System.Text.Json.JsonElement status)
                || !status.TryGetProperty("conditions", out System.Text.Json.JsonElement conditions))
                return false;

            foreach (System.Text.Json.JsonElement c in conditions.EnumerateArray())
            {
                if (c.TryGetProperty("type", out System.Text.Json.JsonElement type)
                    && type.GetString() == "Ready"
                    && c.TryGetProperty("status", out System.Text.Json.JsonElement st))
                    return st.GetString() == "True";
            }
        }
        catch { }
        return false;
    }

    private static List<KafkaPodInfo> ParsePodList(string json)
    {
        List<KafkaPodInfo> pods = [];
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items))
                return pods;

            foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
            {
                string name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
                string role = "broker";
                if (item.GetProperty("metadata").TryGetProperty("labels", out System.Text.Json.JsonElement labels)
                    && labels.TryGetProperty("strimzi.io/controller-role", out _))
                    role = "controller/broker";

                string podStatus = "Unknown";
                bool ready = false;
                string? node = null;
                int restarts = 0;

                if (item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                    && spec.TryGetProperty("nodeName", out System.Text.Json.JsonElement nodeName))
                    node = nodeName.GetString();

                if (item.TryGetProperty("status", out System.Text.Json.JsonElement statusEl))
                {
                    if (statusEl.TryGetProperty("phase", out System.Text.Json.JsonElement phaseEl))
                        podStatus = phaseEl.GetString() ?? "Unknown";

                    if (statusEl.TryGetProperty("containerStatuses", out System.Text.Json.JsonElement containers))
                    {
                        ready = true;
                        foreach (System.Text.Json.JsonElement container in containers.EnumerateArray())
                        {
                            if (container.TryGetProperty("restartCount", out System.Text.Json.JsonElement rc))
                                restarts += rc.GetInt32();
                            if (container.TryGetProperty("ready", out System.Text.Json.JsonElement readyEl))
                                ready = ready && readyEl.GetBoolean();
                        }
                    }
                }

                pods.Add(new KafkaPodInfo
                {
                    Name = name, Role = role, Status = podStatus,
                    Ready = ready, Node = node, Restarts = restarts
                });
            }
        }
        catch { }
        return [.. pods.OrderBy(p => p.Name)];
    }
}
