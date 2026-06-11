using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class RedisOperatorStatus
{
    public bool OperatorAvailable { get; set; }
    public string? OperatorClusterName { get; set; }
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Manages Redis clusters via the OT-Container-Kit Redis Operator.
///
/// Applies RedisCluster CRDs (redis.redis.opstreelabs.in/v1beta2). Before
/// creating the CRD, EntKube generates an auth password, stores it in the
/// tenant vault, and creates a Kubernetes Secret — because the operator reads
/// credentials from a pre-existing Secret rather than generating them itself.
///
/// Connection details (host, port, password) are surfaced in the Cache tab's
/// credentials panel so engineers can wire up applications.
/// </summary>
public class RedisService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    VaultService vaultService)
{
    private const int RedisPort = 6379;

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<RedisCluster>> GetClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RedisClusters
            .Include(c => c.KubernetesCluster).ThenInclude(k => k.Environment)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    // ── Operator detection ────────────────────────────────────────────────────

    public async Task<RedisOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? op = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Cluster.TenantId == tenantId
                && c.Status == ComponentStatus.Installed
                && (c.Name == "redis-operator"
                    || c.ReleaseName == "redis-operator"), ct);

        return new RedisOperatorStatus
        {
            OperatorAvailable = op is not null,
            OperatorClusterName = op?.Cluster.Name
        };
    }

    // ── Cluster lifecycle ─────────────────────────────────────────────────────

    public async Task<RedisCluster> CreateClusterAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        int clusterSize,
        string redisVersion,
        string storageSize,
        string? storageClass,
        bool persistenceEnabled,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (await db.RedisClusters.AnyAsync(
                c => c.KubernetesClusterId == kubernetesClusterId
                    && c.Name == name
                    && c.Namespace == ns, ct))
            throw new InvalidOperationException(
                $"A Redis cluster named '{name}' already exists in namespace '{ns}'.");

        RedisCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            ClusterSize = clusterSize,
            RedisVersion = redisVersion,
            StorageSize = storageSize,
            StorageClass = storageClass,
            PersistenceEnabled = persistenceEnabled,
            Status = RedisClusterStatus.Creating
        };

        db.RedisClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        try
        {
            KubernetesCluster k8sCluster = await db.KubernetesClusters
                .FirstAsync(c => c.Id == kubernetesClusterId, ct);
            string kubeconfig = k8sCluster.Kubeconfig!;

            // Generate a strong auth password and persist it in the vault.
            string password = GeneratePassword();
            await vaultService.SetRedisClusterSecretAsync(
                tenantId, cluster.Id, "REDIS_PASSWORD", password, ct);
            await vaultService.SetRedisClusterSecretAsync(
                tenantId, cluster.Id, "REDIS_HOST",
                $"{name}-leader.{ns}.svc.cluster.local", ct);
            await vaultService.SetRedisClusterSecretAsync(
                tenantId, cluster.Id, "REDIS_PORT", RedisPort.ToString(), ct);

            await k8s.EnsureNamespaceAsync(ns, kubeconfig, ct);

            // Create the auth Secret the operator reads at startup.
            string authSecretManifest = BuildAuthSecretManifest(name, ns, password);
            await k8s.ApplyManifestAsync(authSecretManifest, kubeconfig, ct);

            // Apply the RedisCluster CRD.
            string clusterManifest = BuildClusterManifest(cluster);
            await k8s.ApplyManifestAsync(clusterManifest, kubeconfig, ct);

            // Leave status as Creating — GetClusterDetailAsync reconciles to Running
            // once the operator reports all pods ready.
        }
        catch (Exception ex)
        {
            cluster.Status = RedisClusterStatus.Failed;
            cluster.LastError = ex.Message;
        }

        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.RedisClusters.Update(cluster);
        await db2.SaveChangesAsync(ct);

        return cluster;
    }

    public async Task DeleteClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RedisCluster cluster = await db.RedisClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Redis cluster not found.");

        cluster.Status = RedisClusterStatus.Deleting;
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            await k8s.DeleteManifestAsync("rediscluster", cluster.Name, cluster.Namespace, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            cluster.Status = RedisClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        db.RedisClusters.Remove(cluster);
        await db.SaveChangesAsync(ct);
    }

    // ── Credentials ───────────────────────────────────────────────────────────

    public async Task<(string? password, string? host, string? port)> GetCredentialsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        string? password = await vaultService.GetRedisClusterSecretValueAsync(
            tenantId, clusterId, "REDIS_PASSWORD", ct);
        string? host = await vaultService.GetRedisClusterSecretValueAsync(
            tenantId, clusterId, "REDIS_HOST", ct);
        string? port = await vaultService.GetRedisClusterSecretValueAsync(
            tenantId, clusterId, "REDIS_PORT", ct);

        return (password, host, port);
    }

    // ── App bindings (CacheBinding) ───────────────────────────────────────────

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

    public async Task<List<CacheBinding>> GetCacheBindingsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CacheBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.App).ThenInclude(a => a.Customer)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .Where(b => b.TenantId == tenantId && b.RedisClusterId == clusterId)
            .OrderBy(b => b.AppDeployment.Name)
            .ToListAsync(ct);
    }

    public async Task<List<CacheBinding>> GetCacheBindingsForDeploymentAsync(
        Guid appDeploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CacheBindings
            .Include(b => b.RedisCluster).ThenInclude(c => c.KubernetesCluster)
            .Where(b => b.AppDeploymentId == appDeploymentId)
            .OrderBy(b => b.RedisCluster.Name)
            .ToListAsync(ct);
    }

    public async Task<CacheBinding> CreateCacheBindingAsync(
        Guid tenantId, Guid clusterId, Guid appDeploymentId,
        string k8sSecretName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CacheBinding binding = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RedisClusterId = clusterId,
            AppDeploymentId = appDeploymentId,
            KubernetesSecretName = k8sSecretName,
            SyncEnabled = true
        };

        db.CacheBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await SyncCacheBindingAsync(tenantId, binding.Id, ct);

        return binding;
    }

    public async Task DeleteCacheBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CacheBinding binding = await db.CacheBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cache binding not found.");

        try
        {
            await k8s.DeleteManifestAsync(
                "secret", binding.KubernetesSecretName,
                binding.AppDeployment.Namespace, binding.AppDeployment.Cluster.Kubeconfig!, ct);
        }
        catch { }

        db.CacheBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    public async Task SyncCacheBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CacheBinding binding = await db.CacheBindings
            .Include(b => b.RedisCluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cache binding not found.");

        (string? password, string? host, string? port) =
            await GetCredentialsAsync(tenantId, binding.RedisClusterId, ct);

        if (password is null || host is null || port is null)
            throw new InvalidOperationException("Redis cluster credentials not found in vault.");

        string redisUrl = $"redis://:{password}@{host}:{port}";

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
              REDIS_HOST: {B64(host)}
              REDIS_PORT: {B64(port)}
              REDIS_PASSWORD: {B64(password)}
              REDIS_URL: {B64(redisUrl)}
            """;

        await k8s.ApplyManifestAsync(secretManifest, appKubeconfig, ct);

        binding.LastSyncedAt = DateTime.UtcNow;
        using ApplicationDbContext db2 = dbFactory.CreateDbContext();
        db2.CacheBindings.Update(binding);
        await db2.SaveChangesAsync(ct);
    }

    private static string B64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    // ── Live detail ───────────────────────────────────────────────────────────

    public async Task<RedisClusterDetail?> GetClusterDetailAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RedisCluster? cluster = await db.RedisClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        if (cluster is null)
            return null;

        RedisClusterDetail detail = new() { Cluster = cluster, Phase = "Querying..." };

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

            string crdJson = await k8s.GetJsonAsync(
                $"rediscluster.redis.redis.opstreelabs.in/{cluster.Name}",
                cluster.Namespace, kubeconfig, ct: ct);

            ParseCrdStatus(crdJson, detail);

            string leaderJson = await k8s.GetJsonAsync(
                "pods", cluster.Namespace, kubeconfig,
                $"app={cluster.Name}-leader", ct);

            string followerJson = await k8s.GetJsonAsync(
                "pods", cluster.Namespace, kubeconfig,
                $"app={cluster.Name}-follower", ct);

            detail.Pods =
            [
                .. ParsePodList(leaderJson, "leader"),
                .. ParsePodList(followerJson, "follower"),
            ];

            await ReconcileStatusAsync(cluster, detail, ct);
        }
        catch
        {
            detail.Phase = "Unable to reach cluster";
        }

        return detail;
    }

    private static void ParseCrdStatus(string json, RedisClusterDetail detail)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("status", out System.Text.Json.JsonElement status))
        {
            detail.Phase = "Initialising";
            return;
        }

        if (status.TryGetProperty("readyLeaderReplicas", out System.Text.Json.JsonElement rl))
            detail.ReadyLeaders = rl.GetInt32();

        if (status.TryGetProperty("readyFollowerReplicas", out System.Text.Json.JsonElement rf))
            detail.ReadyFollowers = rf.GetInt32();

        detail.Phase = (detail.ReadyLeaders >= detail.Cluster.ClusterSize
                        && detail.ReadyFollowers >= detail.Cluster.ClusterSize)
            ? "Cluster in healthy state"
            : "Initialising";
    }

    private static List<RedisPodInfo> ParsePodList(string json, string role)
    {
        List<RedisPodInfo> pods = [];
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("items", out System.Text.Json.JsonElement items))
            return pods;

        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            string name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
            string podStatus = "Unknown";
            bool ready = false;
            string? node = null;
            DateTime? startTime = null;
            int restarts = 0;

            if (item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                && spec.TryGetProperty("nodeName", out System.Text.Json.JsonElement nodeName))
                node = nodeName.GetString();

            if (item.TryGetProperty("status", out System.Text.Json.JsonElement statusEl))
            {
                if (statusEl.TryGetProperty("phase", out System.Text.Json.JsonElement phaseEl))
                    podStatus = phaseEl.GetString() ?? "Unknown";

                if (statusEl.TryGetProperty("startTime", out System.Text.Json.JsonElement startEl)
                    && DateTime.TryParse(startEl.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                    startTime = parsed;

                if (statusEl.TryGetProperty("containerStatuses", out System.Text.Json.JsonElement containers))
                {
                    foreach (System.Text.Json.JsonElement container in containers.EnumerateArray())
                    {
                        if (container.TryGetProperty("restartCount", out System.Text.Json.JsonElement rc))
                            restarts += rc.GetInt32();

                        if (container.TryGetProperty("ready", out System.Text.Json.JsonElement readyEl))
                            ready = ready || readyEl.GetBoolean();
                    }
                }
            }

            pods.Add(new RedisPodInfo
            {
                Name = name,
                Role = role,
                Status = podStatus,
                Ready = ready,
                Node = node,
                StartTime = startTime,
                Restarts = restarts
            });
        }

        return [.. pods.OrderBy(p => p.Name)];
    }

    private async Task ReconcileStatusAsync(
        RedisCluster cluster, RedisClusterDetail detail, CancellationToken ct)
    {
        bool healthy = detail.ReadyLeaders >= cluster.ClusterSize
                       && detail.ReadyFollowers >= cluster.ClusterSize;

        RedisClusterStatus newStatus = healthy
            ? RedisClusterStatus.Running
            : cluster.Status == RedisClusterStatus.Running
                ? RedisClusterStatus.Running   // don't regress a running cluster on transient blip
                : RedisClusterStatus.Creating;

        if (newStatus == cluster.Status)
            return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        RedisCluster? tracked = await db.RedisClusters.FindAsync([cluster.Id], ct);
        if (tracked is not null && tracked.Status != newStatus)
        {
            tracked.Status = newStatus;
            cluster.Status = newStatus;
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Manifest builders ─────────────────────────────────────────────────────

    private static string BuildAuthSecretManifest(string clusterName, string ns, string password)
    {
        string b64Password = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));

        return $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {clusterName}-auth
              namespace: {ns}
            type: Opaque
            data:
              password: {b64Password}
            """;
    }

    private static string BuildClusterManifest(RedisCluster cluster)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: redis.redis.opstreelabs.in/v1beta2");
        sb.AppendLine("kind: RedisCluster");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {cluster.Name}");
        sb.AppendLine($"  namespace: {cluster.Namespace}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  clusterSize: {cluster.ClusterSize}");
        sb.AppendLine($"  clusterVersion: {MajorVersion(cluster.RedisVersion)}");
        sb.AppendLine($"  persistenceEnabled: {cluster.PersistenceEnabled.ToString().ToLower()}");
        sb.AppendLine("  kubernetesConfig:");
        sb.AppendLine($"    image: quay.io/opstree/redis:{cluster.RedisVersion}");
        sb.AppendLine("    imagePullPolicy: IfNotPresent");
        sb.AppendLine("    redisSecret:");
        sb.AppendLine($"      name: {cluster.Name}-auth");
        sb.AppendLine("      key: password");
        sb.AppendLine("    resources:");
        sb.AppendLine("      requests:");
        sb.AppendLine("        cpu: 100m");
        sb.AppendLine("        memory: 128Mi");
        sb.AppendLine("      limits:");
        sb.AppendLine("        cpu: '1'");
        sb.AppendLine("        memory: 1Gi");

        if (cluster.PersistenceEnabled)
        {
            sb.AppendLine("  storage:");
            sb.AppendLine("    volumeClaimTemplate:");
            sb.AppendLine("      spec:");
            sb.AppendLine("        accessModes:");
            sb.AppendLine("          - ReadWriteOnce");
            sb.AppendLine("        resources:");
            sb.AppendLine("          requests:");
            sb.AppendLine($"            storage: {cluster.StorageSize}");

            if (!string.IsNullOrEmpty(cluster.StorageClass))
                sb.AppendLine($"        storageClassName: {cluster.StorageClass}");

            sb.AppendLine("    nodeConfVolume: true");
            sb.AppendLine("    nodeConfVolumeClaimTemplate:");
            sb.AppendLine("      spec:");
            sb.AppendLine("        accessModes:");
            sb.AppendLine("          - ReadWriteOnce");
            sb.AppendLine("        resources:");
            sb.AppendLine("          requests:");
            sb.AppendLine("            storage: 1Gi");

            if (!string.IsNullOrEmpty(cluster.StorageClass))
                sb.AppendLine($"        storageClassName: {cluster.StorageClass}");
        }

        // fsGroup 1000 matches the Redis process uid so the operator-mounted
        // /node-conf volume is writable and nodes.conf can be created/locked.
        sb.AppendLine("  podSecurityContext:");
        sb.AppendLine("    runAsUser: 1000");
        sb.AppendLine("    fsGroup: 1000");
        sb.AppendLine("  redisExporter:");
        sb.AppendLine("    enabled: true");
        sb.AppendLine("    image: quay.io/opstree/redis-exporter:v1.44.0");

        return sb.ToString();
    }

    // "v7.0.15" -> "v7", "v7" -> "v7"
    private static string MajorVersion(string version)
    {
        int dot = version.IndexOf('.');
        return dot > 0 ? version[..dot] : version;
    }

    private static string GeneratePassword()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").Replace("=", "")[..43];
    }
}
