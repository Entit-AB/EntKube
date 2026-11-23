using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class RedisOperatorStatus
{
    public bool OperatorAvailable { get; set; }
    public string? OperatorClusterName { get; set; }
}

/// <summary>
/// One Redis a component can be pointed at, as offered in a picker.
/// </summary>
/// <param name="Host">In-cluster DNS name of the Service.</param>
/// <param name="Port">Port it answers on.</param>
/// <param name="Label">What the operator sees in the list.</param>
/// <param name="Managed">True for a Redis EntKube created — the only kind whose password it knows.</param>
/// <param name="RedisClusterId">The managed cluster's id, used to fetch that password from the vault.</param>
/// <param name="ClusterMode">
/// True when this is a sharded Redis Cluster rather than a single server. It matters because that is not
/// merely a bigger Redis: a cluster has only database 0, so <c>SELECT 1</c> is an error, and a client that
/// is not cluster-aware is redirected away from keys it does not own. Plenty of components — Harbor among
/// them — speak only to a single server or a sentinel set, and pointing one at a cluster fails in ways that
/// look like anything but the address. Always true for a Redis EntKube manages, because the operator it
/// uses only builds clusters; unknown, and so false, for a Service merely found on the cluster.
/// </param>
public sealed record RedisEndpointOption(
    string Host, int Port, string Label, bool Managed, Guid? RedisClusterId, bool ClusterMode = false)
{
    /// <summary>The <c>host:port</c> form a component's configuration wants.</summary>
    public string Endpoint => $"{Host}:{Port}";
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
    IDbContextFactory<DataServicesDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusters,
    VaultService vaultService,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    EntKube.Contracts.Delivery.IDeliveryApi delivery,
    // Optional, and last, so the existing positional constructions keep compiling. Safe to be
    // absent: BeginOperation only ever suppresses the second and later questions of one action,
    // never a refusal — so a null gate means more dialogs, never fewer.
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate? gate = null)
{
    private const int RedisPort = 6379;

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<RedisCluster>> GetClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        return await db.RedisClusters
            .Include(c => c.KubernetesCluster).ThenInclude(k => k.Environment)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    // ── Endpoint discovery ────────────────────────────────────────────────────

    /// <summary>
    /// Every Redis a component on this cluster could be pointed at: the ones EntKube manages, plus any
    /// other Service in the cluster that answers on the Redis port.
    ///
    /// <para>The point is to stop asking an operator to type a service DNS name from memory. A wrong one
    /// does not fail the install — the component starts, connects to nothing, and the symptom arrives much
    /// later as a filter that never learns or a cache that never hits.</para>
    ///
    /// <para>Unmanaged Services are included because a cluster may perfectly well run a Redis from a Helm
    /// chart or a bare StatefulSet, and refusing to see those would send the operator back to typing. They
    /// are marked as unmanaged so the difference stays visible: EntKube knows the password for one kind
    /// and not the other.</para>
    /// </summary>
    public async Task<List<RedisEndpointOption>> DiscoverEndpointsAsync(
        Guid tenantId, Guid kubernetesClusterId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == kubernetesClusterId, ct);
        if (cluster is null) return [];

        List<RedisEndpointOption> options = [];

        // Managed first: these carry a vaulted password, so choosing one can fill in the credential too.
        List<RedisCluster> managed = await db.RedisClusters
            .Where(c => c.KubernetesClusterId == kubernetesClusterId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        foreach (RedisCluster m in managed)
        {
            options.Add(new RedisEndpointOption(
                Host: $"{m.Name}-leader.{m.Namespace}.svc.cluster.local",
                Port: RedisPort,
                Label: $"{m.Name} ({m.Namespace}) — managed by EntKube, Redis Cluster mode",
                Managed: true,
                RedisClusterId: m.Id,
                ClusterMode: true));
        }

        // Then whatever else is listening. Best-effort by design: a cluster we cannot read right now
        // should still offer the managed list and a free-text box, not an empty form. A cluster with
        // no stored credential is one of those cases, and ForAsync says so by returning null.
        Clusters.IClusterClient? reachable = await clusters.ForAsync(tenantId, kubernetesClusterId, ct);

        if (reachable is null) return options;

        try
        {
            string json = await reachable.GetJsonAllNamespacesAsync("services", ct: ct);
            foreach (RedisEndpointOption found in ParseRedisServices(json))
            {
                if (!options.Any(o => string.Equals(o.Host, found.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(found);
                }
            }
        }
        catch (Exception)
        {
            // Discovery is a convenience on top of a field the operator can always type.
        }

        return options;
    }

    /// <summary>
    /// The managed Redis behind an endpoint string, or null when the address is not one of ours.
    ///
    /// <para>Deliberately database-only: this runs on the install path, where the question is "whose
    /// password is this?" and a cluster that happens to be unreachable at that moment must not turn the
    /// answer into "nobody's" — that would silently install a component with no credential.</para>
    /// </summary>
    public async Task<RedisEndpointOption?> ResolveManagedEndpointAsync(
        Guid kubernetesClusterId, string endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        string host = endpoint.Split(':')[0].Trim();
        if (host.Length == 0) return null;

        using DataServicesDbContext db = dbFactory.CreateDbContext();

        List<RedisCluster> managed = await db.RedisClusters
            .Where(c => c.KubernetesClusterId == kubernetesClusterId)
            .ToListAsync(ct);

        RedisCluster? match = managed.FirstOrDefault(m => string.Equals(
            $"{m.Name}-leader.{m.Namespace}.svc.cluster.local", host, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? null
            : new RedisEndpointOption(
                Host: host,
                Port: RedisPort,
                Label: $"{match.Name} ({match.Namespace})",
                Managed: true,
                RedisClusterId: match.Id,
                ClusterMode: true);
    }

    /// <summary>
    /// Why a string is not a Redis address, or null when it could be one. An empty string is not an
    /// address at all — it is "no external Redis" — and is accepted here for callers to read as that.
    ///
    /// <para>The check is worth having because of what a wrong value costs. Nothing downstream
    /// questions this string: it becomes <c>redis.external.addr</c>, the component starts, and the
    /// dial either times out for ever or — if the string happens to resolve — connects somewhere it
    /// has no business being. A browser password manager filling an operator's email address into
    /// this box produced exactly that: a registry whose core dialled a mail provider on port 6379,
    /// restarting every minute, with the address visible nowhere but a pod log.</para>
    ///
    /// <para>Deliberately a shape check, not a reachability one. It rejects what cannot be an
    /// address — credentials, a URL, a path, whitespace, a port out of range — and says nothing
    /// about whether the host exists, which is <see cref="InClusterServiceExistsAsync"/>'s job.</para>
    /// </summary>
    public static string? DescribeInvalidEndpoint(string? endpoint)
    {
        string value = (endpoint ?? "").Trim();

        if (value.Length == 0) return null;

        // A sentinel set is a comma-separated list of addresses; every element must stand on its own.
        foreach (string part in value.Split(','))
        {
            string address = part.Trim();

            if (address.Length == 0)
            {
                return $"'{value}' has an empty entry in its address list.";
            }

            if (address.Contains("://", StringComparison.Ordinal))
            {
                return $"'{address}' is a URL. Give the address as host:port — the scheme is Harbor's to add.";
            }

            if (address.Contains('@', StringComparison.Ordinal))
            {
                return $"'{address}' is not a host name — an address with an @ in it is a login or an "
                    + "email address, not a Redis endpoint. Check whether the browser filled this box in.";
            }

            if (address.Contains('/', StringComparison.Ordinal) || address.Any(char.IsWhiteSpace))
            {
                return $"'{address}' is not a host name. Give the address as host:port, with no path or spaces.";
            }

            string host = address;

            // host:port, or a bracketed IPv6 literal with an optional port.
            if (address.StartsWith('['))
            {
                int close = address.IndexOf(']');
                if (close < 0) return $"'{address}' is missing the closing bracket of its IPv6 address.";

                host = address[1..close];
                string rest = address[(close + 1)..];

                if (rest.Length > 0 && (rest[0] != ':' || !IsPort(rest[1..])))
                {
                    return $"'{address}' does not end in a valid port.";
                }
            }
            else if (address.LastIndexOf(':') is int colon && colon >= 0)
            {
                host = address[..colon];
                if (!IsPort(address[(colon + 1)..]))
                {
                    return $"'{address}' does not end in a valid port (1-65535).";
                }
            }

            if (host.Length == 0)
            {
                return $"'{address}' has no host in front of its port.";
            }

            if (!address.StartsWith('[')
                && !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return $"'{host}' is not a host name or IP address.";
            }
        }

        return null;
    }

    private static bool IsPort(string value) =>
        int.TryParse(value, out int port) && port is > 0 and <= 65535;

    /// <summary>
    /// Whether the cluster actually has a Service behind an in-cluster Redis address.
    ///
    /// <para>Null means "cannot say", and every uncertainty resolves to it: an address that is not an
    /// in-cluster Service name (a managed cloud Redis, a bare IP, a sentinel list), a cluster with no
    /// stored kubeconfig, or a listing that failed. Callers use this to refuse an install that is about
    /// to wait half an hour for a connection that can never be made, and refusing on a question we
    /// could not answer would be worse than the failure it prevents.</para>
    /// </summary>
    public async Task<bool?> InClusterServiceExistsAsync(
        Guid tenantId, Guid kubernetesClusterId, string endpoint, CancellationToken ct = default)
    {
        string host = (endpoint ?? "").Split(',')[0].Split(':')[0].Trim();

        // Only cluster-local DNS names are judged. Anything else is somebody else's network.
        if (!host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Clusters.IClusterClient? reachable = await clusters.ForAsync(tenantId, kubernetesClusterId, ct);

        if (reachable is null)
        {
            return null;
        }

        try
        {
            string json = await reachable.GetJsonAllNamespacesAsync("services", ct: ct);

            // Every Service, not only the Redis-looking ones: the question here is whether the name
            // resolves at all, and a Redis on a non-standard port is still a Redis.
            return ServiceDnsNames(json).Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The cluster-local DNS name of every Service in a <c>kubectl get services -A -o json</c> payload.</summary>
    public static IEnumerable<string> ServiceDnsNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)) yield break;

        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)) continue;

            string? name = meta.TryGetProperty("name", out System.Text.Json.JsonElement n) ? n.GetString() : null;
            string? ns = meta.TryGetProperty("namespace", out System.Text.Json.JsonElement nsEl) ? nsEl.GetString() : null;

            if (name is not null && ns is not null)
            {
                yield return $"{name}.{ns}.svc.cluster.local";
            }
        }
    }

    /// <summary>
    /// Picks the Redis-looking Services out of a <c>kubectl get services -A -o json</c> payload: anything
    /// exposing the Redis port, or a port named for it. Headless Services are skipped — they resolve to
    /// pod IPs, which is not an address a client should be handed as a stable endpoint.
    /// </summary>
    public static List<RedisEndpointOption> ParseRedisServices(string json)
    {
        List<RedisEndpointOption> found = [];
        if (string.IsNullOrWhiteSpace(json)) return found;

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)) return found;

        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)) continue;
            string? name = meta.TryGetProperty("name", out System.Text.Json.JsonElement n) ? n.GetString() : null;
            string? ns = meta.TryGetProperty("namespace", out System.Text.Json.JsonElement nsEl) ? nsEl.GetString() : null;
            if (name is null || ns is null) continue;

            if (!item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)) continue;
            if (spec.TryGetProperty("clusterIP", out System.Text.Json.JsonElement ip)
                && string.Equals(ip.GetString(), "None", StringComparison.Ordinal))
            {
                continue;   // headless
            }
            if (!spec.TryGetProperty("ports", out System.Text.Json.JsonElement ports)) continue;

            foreach (System.Text.Json.JsonElement port in ports.EnumerateArray())
            {
                int number = port.TryGetProperty("port", out System.Text.Json.JsonElement p) && p.TryGetInt32(out int v)
                    ? v : 0;
                string portName = port.TryGetProperty("name", out System.Text.Json.JsonElement pn)
                    ? pn.GetString() ?? "" : "";

                bool looksLikeRedis = number == RedisPort
                    || portName.Contains("redis", StringComparison.OrdinalIgnoreCase);
                if (!looksLikeRedis || number == 0) continue;

                found.Add(new RedisEndpointOption(
                    Host: $"{name}.{ns}.svc.cluster.local",
                    Port: number,
                    Label: $"{name} ({ns})",
                    Managed: false,
                    RedisClusterId: null));
                break;   // one entry per Service, not one per port
            }
        }

        return [.. found.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase)];
    }

    // ── Operator detection ────────────────────────────────────────────────────

    public async Task<RedisOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        // Asked of Catalog rather than of its table. The predicate is unchanged and
        // applied here: these alias lists are not symmetrical across Name,
        // ReleaseName and HelmChartName, so a contract method matching every key
        // against every column would answer about installations these never accepted.
        EntKube.Contracts.Catalog.InstalledComponent? op =
            (await catalog.GetComponentsForTenantAsync(tenantId, ct))
            .FirstOrDefault(c => c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed
                && (c.Name == "redis-operator"
                    || c.ReleaseName == "redis-operator"));

        return new RedisOperatorStatus
        {
            OperatorAvailable = op is not null,
            OperatorClusterName = op?.ClusterName
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
        using DataServicesDbContext db = dbFactory.CreateDbContext();

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
            Status = RedisClusterStatus.Creating,
            // Born with per-app users when the version can carry them: no app ever receives the
            // admin password, and there is no restart later to switch over.
            PerAppUsersSince = RedisAcl.SupportsPerAppUsers(redisVersion) ? DateTime.UtcNow : null
        };

        db.RedisClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        // One action, one acknowledgment: the namespace, the auth Secret and the CR are one
        // decision the operator already made by pressing Create.
        using IDisposable? operation = gate?.BeginOperation("create a Redis cluster");

        try
        {
            Clusters.IClusterClient target = await clusters.ForAsync(tenantId, kubernetesClusterId, ct)
                ?? throw new InvalidOperationException(
                    "The target cluster has no stored kubeconfig, so nothing can be applied to it.");

            // Generate a strong auth password and persist it in the vault.
            string password = GeneratePassword();
            await vaultService.SetRedisClusterSecretAsync(
                tenantId, cluster.Id, "REDIS_PASSWORD", password, ct);
            await vaultService.SetRedisClusterSecretAsync(
                tenantId, cluster.Id, "REDIS_HOST",
                $"{name}-leader.{ns}.svc.cluster.local", ct);
            await vaultService.SetRedisClusterSecretAsync(
                tenantId, cluster.Id, "REDIS_PORT", RedisPort.ToString(), ct);

            await target.EnsureNamespaceAsync(ns, ct);

            // Create the auth Secret the operator reads at startup.
            string authSecretManifest = BuildAuthSecretManifest(name, ns, password);
            await target.ApplyManifestAsync(authSecretManifest, ct);

            // The (empty) users file the pods include. It must exist before the pods do: the
            // include names a ConfigMap, and a pod whose volume source is missing never starts.
            if (cluster.PerAppUsers)
                await target.ApplyManifestAsync(RedisAcl.BuildUsersConfigMap(name, ns, []), ct);

            // Apply the RedisCluster CRD.
            string clusterManifest = BuildClusterManifest(cluster);
            await target.ApplyManifestAsync(clusterManifest, ct);

            // Leave status as Creating — GetClusterDetailAsync reconciles to Running
            // once the operator reports all pods ready.
        }
        catch (Exception ex)
        {
            cluster.Status = RedisClusterStatus.Failed;
            cluster.LastError = ex.Message;
        }

        using DataServicesDbContext db2 = dbFactory.CreateDbContext();
        db2.RedisClusters.Update(cluster);
        await db2.SaveChangesAsync(ct);

        return cluster;
    }

    public async Task DeleteClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        RedisCluster cluster = await db.RedisClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Redis cluster not found.");

        cluster.Status = RedisClusterStatus.Deleting;
        await db.SaveChangesAsync(ct);

        try
        {
            Clusters.IClusterClient target = await clusters.ForAsync(tenantId, cluster.KubernetesClusterId, ct)
                ?? throw new InvalidOperationException(
                    "The cluster has no stored kubeconfig, so the RedisCluster cannot be removed from it.");

            await target.DeleteManifestAsync("rediscluster", cluster.Name, cluster.Namespace, ct);
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

    /// <summary>
    /// The tenant's deployments, for the binding pickers.
    ///
    /// <para>Asked of Delivery. All four service tabs had this same query written out — the
    /// tenant's deployments with app, customer and cluster included — and it was the only thing
    /// keeping this service on the all-seeing context. <c>DeploymentSummary</c> now carries
    /// <c>ClusterName</c> beside <c>ClusterId</c>, which is what the pickers display.</para>
    /// </summary>
    public async Task<List<EntKube.Contracts.Delivery.DeploymentSummary>> GetTenantDeploymentsAsync(
        Guid tenantId, CancellationToken ct = default)
        => [.. (await delivery.GetDeploymentsForTenantAsync(tenantId, ct))
            .OrderBy(d => d.AppName, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)];

    public async Task<List<CacheBinding>> GetCacheBindingsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        return await db.CacheBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.App).ThenInclude(a => a.Customer)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .Where(b => b.TenantId == tenantId && b.RedisClusterId == clusterId)
            .OrderBy(b => b.AppDeployment.Name)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose deployment. A binding row names the Kubernetes Secret the credentials land in and
    /// the backing cluster, so listing them for an arbitrary deployment id describes another
    /// tenant's wiring. The app environment page calls this by id.
    /// </param>
    public async Task<List<CacheBinding>> GetCacheBindingsForDeploymentAsync(
        Guid tenantId, Guid appDeploymentId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        return await db.CacheBindings
            .Include(b => b.RedisCluster).ThenInclude(c => c.KubernetesCluster)
            .Where(b => b.AppDeploymentId == appDeploymentId && b.TenantId == tenantId)
            .OrderBy(b => b.RedisCluster.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Binds a Redis to an app deployment and writes the credentials into its Secret.
    ///
    /// <para><b>What is checked before anything is written.</b>
    /// <list type="bullet">
    ///   <item>The Redis and the deployment must both be this tenant's — the ids arrive from a form,
    ///     and a foreign id used to produce a row that could never sync.</item>
    ///   <item>The app's environment allow-list (Governance) must admit the Redis when it has entries.</item>
    ///   <item>No other cache binding may write into the same Secret. A Secret belongs to a namespace,
    ///     not a deployment, so two deployments in one namespace count: both would own the same REDIS_*
    ///     keys, the last sync would win, and deleting either would remove the other's credentials.</item>
    ///   <item>No other app on this Redis may already use the key prefix. Two users confined to the
    ///     same prefix share every key and channel under it, while the page shows each as confined.</item>
    /// </list></para>
    ///
    /// <para>A binding whose first sync fails is removed again rather than left behind as a row
    /// that looks bound and is not.</para>
    /// </summary>
    /// <param name="keyPrefix">
    /// The prefix the app's Redis user is confined to, without the colon; null for all keys. Only
    /// enforced on a cluster with per-app users, but delivered as REDIS_KEY_PREFIX either way, so
    /// an app can start using it before its cluster switches over.
    /// </param>
    public async Task<CacheBinding> CreateCacheBindingAsync(
        Guid tenantId, Guid clusterId, Guid appDeploymentId,
        string k8sSecretName, string? keyPrefix = null, CancellationToken ct = default)
    {
        string? prefix = RedisAcl.NormalizePrefix(keyPrefix);
        if (RedisAcl.DescribeInvalidPrefix(prefix) is string badPrefix)
            throw new InvalidOperationException(badPrefix);

        string secretName = (k8sSecretName ?? "").Trim();
        if (secretName.Length == 0)
            throw new InvalidOperationException("Name the Kubernetes Secret the credentials are written into.");

        using DataServicesDbContext db = dbFactory.CreateDbContext();

        RedisCluster cluster = await db.RedisClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Redis cluster not found.");

        EntKube.Contracts.Delivery.DeploymentSummary deployment =
            await delivery.GetDeploymentAsync(tenantId, appDeploymentId, ct)
            ?? throw new InvalidOperationException("Deployment not found.");

        IReadOnlyList<Guid> allowed = await delivery.GetAllowedCacheClusterIdsAsync(
            tenantId, deployment.AppId, deployment.EnvironmentId, ct);
        if (allowed.Count > 0 && !allowed.Contains(cluster.Id))
        {
            throw new InvalidOperationException(
                $"{deployment.AppName} may not use the Redis '{cluster.Name}' in {deployment.EnvironmentName}: "
                + "the environment's allow-list on the Governance tab names other caches. Add this one there first.");
        }

        if (await db.CacheBindings.AnyAsync(b => b.TenantId == tenantId
                && b.AppDeployment.ClusterId == deployment.ClusterId
                && b.AppDeployment.Namespace == deployment.Namespace
                && b.KubernetesSecretName == secretName, ct))
        {
            throw new InvalidOperationException(
                $"Another Redis binding already writes into the Secret '{secretName}' in {deployment.Namespace}. "
                + "Both would write the same REDIS_* keys, so the last sync would win and deleting either "
                + "would remove the other's credentials. Use a different Secret name.");
        }

        if (prefix is not null && await db.CacheBindings
                .Where(b => b.TenantId == tenantId && b.RedisClusterId == cluster.Id
                    && b.KeyPrefix == prefix && b.AppDeploymentId != appDeploymentId)
                .Select(b => b.AppDeployment.Name)
                .FirstOrDefaultAsync(ct) is string sharer)
        {
            throw new InvalidOperationException(
                $"The deployment '{sharer}' already uses the key prefix '{prefix}' on this Redis. Two apps with "
                + "one prefix can read and overwrite each other's keys and channels. Choose another prefix.");
        }

        CacheBinding binding = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RedisClusterId = cluster.Id,
            AppDeploymentId = appDeploymentId,
            KubernetesSecretName = secretName,
            KeyPrefix = prefix,
            SyncEnabled = true
        };

        db.CacheBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        // One action, one acknowledgment: the users file, the live users and the Secret are one
        // decision. The sync below opens its own operation, which nests into this one.
        using IDisposable? operation = gate?.BeginOperation("bind a Redis to an app");

        try
        {
            // The password exists before the binding's first reconcile, so it is never generated
            // by some other reconcile that happened to see the row first.
            if (cluster.PerAppUsers)
                await EnsureUserPasswordAsync(tenantId, cluster.Id, binding.Id, ct);

            await SyncCacheBindingAsync(tenantId, binding.Id, ct);
        }
        catch
        {
            // Not left behind: a row that never synced looks bound to everyone reading the page.
            // Its user, if one reached some nodes, is revoked by the next reconcile of this
            // cluster, which no longer finds the binding.
            using DataServicesDbContext cleanup = dbFactory.CreateDbContext();
            await cleanup.CacheBindings
                .Where(b => b.Id == binding.Id && b.TenantId == tenantId)
                .ExecuteDeleteAsync(CancellationToken.None);
            await vaultService.DeleteRedisClusterSecretAsync(
                tenantId, cluster.Id, RedisAcl.PasswordSecretName(binding.Id), CancellationToken.None);
            throw;
        }

        return binding;
    }

    /// <summary>
    /// Unbinds a Redis from an app: revokes the app's Redis user, removes the credentials from its
    /// Secret, and deletes the binding.
    ///
    /// <para><b>Revocation comes first and is not best-effort.</b> If the app's user cannot be removed
    /// from the Redis, the binding is kept and the error surfaces: deleting the row anyway would show
    /// an app as unbound while its credentials still work. Removing the keys from the app's Secret
    /// is different — once the user is gone those values open nothing — so a failure there is
    /// returned as a warning rather than undoing the unbind.</para>
    ///
    /// <para><b>The admin password is the exception.</b> An app that was ever handed it — bound before
    /// its Redis switched to per-app users, or never moved off it since — still knows it after its own
    /// user is gone, and only changing that password takes it back. The warning says so; it is decided
    /// from the binding's history, not from the cluster's current mode.</para>
    /// </summary>
    /// <returns>A warning for the operator, or null when everything was cleaned up.</returns>
    public async Task<string?> DeleteCacheBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        CacheBinding binding = await db.CacheBindings
            .Include(b => b.RedisCluster)
            .Include(b => b.AppDeployment)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cache binding not found.");

        // One action, one acknowledgment: the revocation and the key removal are one decision.
        using IDisposable? operation = gate?.BeginOperation("remove a Redis binding");

        List<string> warnings = [];

        if (binding.RedisCluster.PerAppUsers)
        {
            // Throws when the user could not be revoked everywhere; the row stays.
            await ReconcileUsersAsync(tenantId, binding.RedisClusterId, excludeBindingId: binding.Id, ct);
        }

        if (RedisAcl.EverHeldAdminPassword(binding, binding.RedisCluster))
        {
            warnings.Add(binding.RedisCluster.PerAppUsers
                ? $"This app was once given the Redis '{binding.RedisCluster.Name}' admin password, which stays valid: "
                  + "revoking its own user does not take that back. Change the admin password to do so."
                : $"This app held the Redis '{binding.RedisCluster.Name}' admin password, which stays valid: switch "
                  + "the Redis to per-app users so access can be taken back app by app.");
        }

        try
        {
            Clusters.IClusterClient appCluster =
                await clusters.ForAsync(tenantId, binding.AppDeployment.ClusterId, ct)
                ?? throw new InvalidOperationException("the app's cluster has no stored kubeconfig");

            // The keys, not the Secret. Deleting the object would take the app's own secrets
            // with it whenever the binding shares their Secret, which is the supported way
            // to set this up.
            await EntKube.Web.Services.Clusters.SharedSecretKeys.RemoveAsync(
                appCluster,
                binding.KubernetesSecretName,
                binding.AppDeployment.Namespace,
                EntKube.Web.Services.Clusters.SharedSecretKeys.Owners.Cache,
                summary: $"Remove Redis credentials from '{binding.KubernetesSecretName}'"
                       + $" in {binding.AppDeployment.Namespace}",
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add(
                $"The REDIS_* keys could not be removed from the Secret '{binding.KubernetesSecretName}' in "
                + $"{binding.AppDeployment.Namespace} ({ex.Message}); remove them by hand.");
        }

        db.CacheBindings.Remove(binding);
        await db.SaveChangesAsync(ct);

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    /// <summary>
    /// Changes the key prefix a binding's Redis user is confined to (null for all keys), then syncs
    /// so the user's rules and the app's REDIS_KEY_PREFIX change together.
    ///
    /// <para>⚠️ Tightening takes effect on the app's open connections at once: from its next command
    /// it can no longer touch keys outside the prefix. The app has to be using the prefix first.</para>
    ///
    /// <para><b>The saved prefix is the desired state.</b> It is stored with
    /// <see cref="CacheBinding.SyncPending"/> set, and only a successful sync clears that. When the
    /// sync fails, Redis may already enforce the new prefix on some nodes while the app's Secret still
    /// says the old one; the background reconcile retries the sync until both agree, and the page shows
    /// the binding as pending until then.</para>
    /// </summary>
    public async Task SetCacheBindingKeyPrefixAsync(
        Guid tenantId, Guid bindingId, string? keyPrefix, CancellationToken ct = default)
    {
        string? prefix = RedisAcl.NormalizePrefix(keyPrefix);
        if (RedisAcl.DescribeInvalidPrefix(prefix) is string badPrefix)
            throw new InvalidOperationException(badPrefix);

        using DataServicesDbContext db = dbFactory.CreateDbContext();

        CacheBinding binding = await db.CacheBindings
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cache binding not found.");

        if (binding.KeyPrefix == prefix) return;

        if (prefix is not null && await db.CacheBindings
                .Where(b => b.TenantId == tenantId && b.RedisClusterId == binding.RedisClusterId
                    && b.KeyPrefix == prefix && b.AppDeploymentId != binding.AppDeploymentId)
                .Select(b => b.AppDeployment.Name)
                .FirstOrDefaultAsync(ct) is string sharer)
        {
            throw new InvalidOperationException(
                $"The deployment '{sharer}' already uses the key prefix '{prefix}' on this Redis. Two apps with "
                + "one prefix can read and overwrite each other's keys and channels. Choose another prefix.");
        }

        binding.KeyPrefix = prefix;
        binding.SyncPending = true;
        await db.SaveChangesAsync(ct);

        using IDisposable? operation = gate?.BeginOperation("change the keys an app may use in Redis");
        try
        {
            await SyncCacheBindingAsync(tenantId, bindingId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The new key prefix is saved, but the app could not be synced yet: {ex.Message} EntKube retries "
                + "within 15 minutes; until then Redis may already enforce the new prefix while the app's Secret "
                + "still has the old one.", ex);
        }
    }

    /// <summary>
    /// Writes a binding's credentials into the app's Secret: REDIS_HOST, REDIS_PORT, REDIS_USERNAME,
    /// REDIS_PASSWORD, REDIS_URL and REDIS_KEY_PREFIX.
    ///
    /// <para>On a cluster with per-app users, the users on the Redis are reconciled first — the file
    /// every node loads and the live users on every running node — so the app never receives a
    /// password Redis does not yet accept. Without per-app users it receives the cluster's admin
    /// password as <c>default</c>, as every binding did before.</para>
    ///
    /// <para><b>What is written back, and when it counts from.</b> Only the binding's own sync columns,
    /// never the entity graph this method loaded: a graph saved back would carry a stale key prefix,
    /// or a stale <c>PerAppUsersSince</c>, over a change made while the sync ran. And the sync is
    /// stamped with the moment it <em>read</em> the cluster, not when it finished, so a sync that read
    /// a cluster just before it switched — and so wrote the admin password — still counts as behind
    /// the switch.</para>
    /// </summary>
    public async Task SyncCacheBindingAsync(
        Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        DateTime readAt = DateTime.UtcNow;

        using DataServicesDbContext db = dbFactory.CreateDbContext();

        CacheBinding binding = await db.CacheBindings.AsNoTracking()
            .Include(b => b.RedisCluster)
            .Include(b => b.AppDeployment)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cache binding not found.");

        (string? adminPassword, string? host, string? port) =
            await GetCredentialsAsync(tenantId, binding.RedisClusterId, ct);

        if (adminPassword is null || host is null || port is null)
            throw new InvalidOperationException("Redis cluster credentials not found in vault.");

        Clusters.IClusterClient appCluster =
            await clusters.ForAsync(tenantId, binding.AppDeployment.ClusterId, ct)
            ?? throw new InvalidOperationException(
                "The app's cluster has no stored kubeconfig, so the credential secret cannot be synced.");

        // One action, one acknowledgment: the users, the namespace and the credentials are halves
        // of the same sync.
        using IDisposable? operation = gate?.BeginOperation("sync Redis credentials into an app namespace");

        string username = "default";
        string password = adminPassword;
        string? keyPrefix = binding.KeyPrefix;

        if (binding.RedisCluster.PerAppUsers)
        {
            IReadOnlyDictionary<Guid, RedisAppUser> users =
                await ReconcileUsersAsync(tenantId, binding.RedisClusterId, excludeBindingId: null, ct);

            RedisAppUser own = users.TryGetValue(binding.Id, out RedisAppUser? u)
                ? u
                : throw new InvalidOperationException("The binding's Redis user was not created.");

            username = RedisAcl.UsernameFor(binding.Id);
            password = own.Password;
            // From the same read the user's rules were built from, so the prefix the app is told and
            // the prefix Redis enforces cannot come from two different moments.
            keyPrefix = own.KeyPrefix;
        }

        await appCluster.EnsureNamespaceAsync(binding.AppDeployment.Namespace, ct);

        // ⚠️ Named keys, not a whole Secret. Pointing a binding at the app's own Secret is
        // intended — an app wants one envFrom, not one per service it uses — so this Secret can
        // have other writers, the app's vault secrets among them. Applying a complete manifest
        // here looked additive and was not: apply's three-way merge deletes the keys named in
        // the object's last-applied annotation and missing from the new config, and that
        // annotation belongs to the object rather than to whoever wrote it. The vault had just
        // written it, so this apply deleted the app's own keys and left only these.
        await EntKube.Web.Services.Clusters.SharedSecretKeys.WriteAsync(
            appCluster,
            binding.KubernetesSecretName,
            binding.AppDeployment.Namespace,
            EntKube.Web.Services.Clusters.SharedSecretKeys.Owners.Cache,
            CredentialKeys(host, port, username, password, keyPrefix),
            summary: $"Sync Redis credentials into '{binding.KubernetesSecretName}'"
                   + $" in {binding.AppDeployment.Namespace}",
            ct: ct);

        // The Secret is written; recording it must not fail on a cancellation that came too late to
        // matter. The pending flag is cleared only if the prefix is still the one just written — a
        // change saved meanwhile stays pending for the next sync.
        string? writtenPrefix = keyPrefix;
        await db.CacheBindings
            .Where(b => b.Id == binding.Id && b.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LastSyncedAt, readAt)
                .SetProperty(b => b.SyncPending, b => b.SyncPending && b.KeyPrefix != writtenPrefix),
                CancellationToken.None);
    }

    /// <summary>
    /// The keys a binding writes. Always the same six, so a binding that moves from the admin password
    /// to its own user — or back — never leaves a stale key behind. The password is base64url and
    /// the username hex, so neither needs escaping in the URL.
    /// </summary>
    public static Dictionary<string, string?> CredentialKeys(
        string host, string port, string username, string password, string? keyPrefix) => new()
    {
        ["REDIS_HOST"] = host,
        ["REDIS_PORT"] = port,
        ["REDIS_USERNAME"] = username,
        ["REDIS_PASSWORD"] = password,
        ["REDIS_URL"] = $"redis://{username}:{password}@{host}:{port}",
        ["REDIS_KEY_PREFIX"] = RedisAcl.KeyPrefixValue(keyPrefix),
    };

    // ── Per-app Redis users ───────────────────────────────────────────────────

    /// <summary>
    /// The shell command run in a Redis container to reach its own node. The admin password comes
    /// from the container's own REDIS_PASSWORD (the operator sets it from the cluster's auth Secret)
    /// into redis-cli's REDISCLI_AUTH, inside the pod: it is on neither kubectl's argument list nor
    /// redis-cli's. The commands themselves arrive on stdin and carry only password hashes.
    /// </summary>
    public static readonly IReadOnlyList<string> RedisCliOnPod =
        ["sh", "-c", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" exec redis-cli -h 127.0.0.1 -p 6379"];

    /// <summary>How long one reconcile may hold a Redis's users before another instance may take over.</summary>
    private static readonly TimeSpan UsersLeaseLength = TimeSpan.FromMinutes(2);

    /// <summary>How long a reconcile waits for another one on the same Redis before giving up.</summary>
    private static readonly TimeSpan UsersLeaseWait = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Makes the Redis's users match its bindings: one user per binding, nothing else of EntKube's.
    /// Writes the users file every node loads at start, then applies the same set to every running
    /// node and revokes users whose binding is gone, and finally drops vault passwords nothing uses.
    ///
    /// <para><b>The set is every binding of this Redis</b> (minus <paramref name="excludeBindingId"/>,
    /// the one being deleted). It is never filtered further: whatever is missing from it is revoked,
    /// so a partial set would cut off apps that are still bound.</para>
    ///
    /// <para><b>One reconcile per Redis at a time, across instances.</b> The set is read and applied
    /// while holding <see cref="RedisCluster.UsersLeaseUntil"/>, so a reconcile that read the bindings
    /// earlier can never revoke a user another one just created. Before the lease is taken the file is
    /// applied once from a first read: that is where an operator is asked to acknowledge the change,
    /// so nobody holds the lease while a dialog waits for a human.</para>
    ///
    /// <para>A node that is not running Redis is skipped — it reads the file when it starts. A running
    /// node that refuses the change fails the reconcile, because an app synced against it would be
    /// handed a password that node does not accept.</para>
    /// </summary>
    /// <returns>The users now in force, by binding id.</returns>
    public async Task<IReadOnlyDictionary<Guid, RedisAppUser>> ReconcileUsersAsync(
        Guid tenantId, Guid redisClusterId, Guid? excludeBindingId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        RedisCluster cluster = await db.RedisClusters.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == redisClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Redis cluster not found.");

        if (!cluster.PerAppUsers)
            throw new InvalidOperationException(
                $"The Redis '{cluster.Name}' has not been switched to per-app users.");

        Clusters.IClusterClient target = await clusters.ForAsync(tenantId, cluster.KubernetesClusterId, ct)
            ?? throw new InvalidOperationException(
                "The Redis's cluster has no stored kubeconfig, so its users cannot be changed.");

        using IDisposable? operation = gate?.BeginOperation("update the Redis users of the apps bound to it");

        // Asked here, outside the lease: the acknowledgment (when there is anyone to ask) happens on
        // this first apply, and the applies under the lease belong to the same operation.
        await target.ApplyManifestAsync(RedisAcl.BuildUsersConfigMap(
            cluster.Name, cluster.Namespace, (await WantedUsersAsync(tenantId, cluster.Id, excludeBindingId, ct)).Values), ct);

        DateTime lease = await ClaimUsersLeaseAsync(tenantId, cluster, ct);
        try
        {
            Dictionary<Guid, RedisAppUser> users = await WantedUsersAsync(tenantId, cluster.Id, excludeBindingId, ct);

            // The file first: a node that restarts from here on comes back with this set, whatever
            // happens to the live apply below.
            await target.ApplyManifestAsync(RedisAcl.BuildUsersConfigMap(cluster.Name, cluster.Namespace, users.Values), ct);

            lease = await ExtendUsersLeaseAsync(tenantId, cluster, lease, ct);
            await ApplyUsersToRunningNodesAsync(target, cluster, [.. users.Values], ct);

            // Only after every node has let go of them, and only for bindings that are gone NOW: a
            // binding created while this ran has its row and password, and keeps both.
            HashSet<Guid> stillBound = [.. await db.CacheBindings.AsNoTracking()
                .Where(b => b.RedisClusterId == cluster.Id && b.TenantId == tenantId && b.Id != excludeBindingId)
                .Select(b => b.Id)
                .ToListAsync(ct)];
            foreach (VaultSecret row in await vaultService.GetRedisClusterSecretsAsync(tenantId, cluster.Id, ct))
            {
                if (RedisAcl.TryParsePasswordSecretName(row.Name, out Guid owner) && !stillBound.Contains(owner))
                    await vaultService.DeleteRedisClusterSecretAsync(tenantId, cluster.Id, row.Name, ct);
            }

            return users;
        }
        finally
        {
            await ReleaseUsersLeaseAsync(tenantId, cluster.Id, lease);
        }
    }

    /// <summary>Every binding of the Redis but one, with its password (generated on first use).</summary>
    private async Task<Dictionary<Guid, RedisAppUser>> WantedUsersAsync(
        Guid tenantId, Guid redisClusterId, Guid? excludeBindingId, CancellationToken ct)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        var bindings = await db.CacheBindings.AsNoTracking()
            .Where(b => b.RedisClusterId == redisClusterId && b.TenantId == tenantId && b.Id != excludeBindingId)
            .Select(b => new { b.Id, b.KeyPrefix })
            .ToListAsync(ct);

        Dictionary<Guid, RedisAppUser> users = [];
        foreach (var b in bindings)
            users[b.Id] = new RedisAppUser(b.Id, await EnsureUserPasswordAsync(tenantId, redisClusterId, b.Id, ct), b.KeyPrefix);
        return users;
    }

    /// <summary>
    /// Claims the Redis's users lease with a conditional update — only when it is free or expired —
    /// and waits for it while another reconcile holds it.
    /// </summary>
    private async Task<DateTime> ClaimUsersLeaseAsync(Guid tenantId, RedisCluster cluster, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            DateTime now = DateTime.UtcNow;
            DateTime until = now + UsersLeaseLength;

            using DataServicesDbContext db = dbFactory.CreateDbContext();
            int claimed = await db.RedisClusters
                .Where(c => c.Id == cluster.Id && c.TenantId == tenantId
                    && (c.UsersLeaseUntil == null || c.UsersLeaseUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsersLeaseUntil, until), ct);
            if (claimed == 1) return until;

            if (waited.Elapsed > UsersLeaseWait)
            {
                throw new InvalidOperationException(
                    $"Another change to the Redis '{cluster.Name}' users is still running. Try again in a minute.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <summary>Extends a held lease; refuses to go on when it was lost (expired and taken by another).</summary>
    private async Task<DateTime> ExtendUsersLeaseAsync(Guid tenantId, RedisCluster cluster, DateTime held, CancellationToken ct)
    {
        DateTime until = DateTime.UtcNow + UsersLeaseLength;
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        int kept = await db.RedisClusters
            .Where(c => c.Id == cluster.Id && c.TenantId == tenantId && c.UsersLeaseUntil == held)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsersLeaseUntil, until), ct);
        if (kept != 1)
        {
            throw new InvalidOperationException(
                $"Changing the Redis '{cluster.Name}' users took too long and another change took over; sync again.");
        }
        return until;
    }

    /// <summary>Releases the lease if it is still the one this reconcile holds.</summary>
    private async Task ReleaseUsersLeaseAsync(Guid tenantId, Guid redisClusterId, DateTime held)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        await db.RedisClusters
            .Where(c => c.Id == redisClusterId && c.TenantId == tenantId && c.UsersLeaseUntil == held)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsersLeaseUntil, (DateTime?)null), CancellationToken.None);
    }

    /// <summary>
    /// Sends the users to every leader and follower whose Redis is running: each node is asked which
    /// users it has, then told to set every wanted one and delete every EntKube user no longer wanted.
    ///
    /// <para>redis-cli exits 0 when a command it read from stdin fails, and when it never reached Redis
    /// it prints nothing a refusal check would catch. So the listing must positively contain the
    /// <c>default</c> user — every Redis has it — before an empty or partial answer is believed.</para>
    /// </summary>
    private async Task ApplyUsersToRunningNodesAsync(
        Clusters.IClusterClient target, RedisCluster cluster, IReadOnlyCollection<RedisAppUser> users,
        CancellationToken ct)
    {
        List<string> failures = [];

        foreach ((string pod, string container) in await ListRunningNodesAsync(target, cluster, ct))
        {
            try
            {
                string listed = await target.RunCommandOnPodWithStdinAsync(
                    pod, cluster.Namespace, RedisCliOnPod, "ACL USERS\n",
                    EntKube.Web.Services.ClusterChanges.ChangeIntent.Read, ct, container);
                if (RedisAcl.FindError(listed) is string listError)
                {
                    failures.Add($"{pod}: {listError}");
                    continue;
                }

                IReadOnlyList<string> existing = RedisAcl.ParseUsers(listed);
                if (!existing.Contains("default"))
                {
                    failures.Add($"{pod}: no answer from Redis ({listed.Trim()})");
                    continue;
                }

                string script = RedisAcl.BuildApplyScript(existing, users);
                if (script.Length == 0) continue;

                string applied = await target.RunCommandOnPodWithStdinAsync(
                    pod, cluster.Namespace, RedisCliOnPod, script,
                    EntKube.Web.Services.ClusterChanges.ChangeIntent.Change, ct, container);
                if (RedisAcl.FindError(applied) is string applyError)
                    failures.Add($"{pod}: {applyError}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{pod}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Redis users could not be applied on {failures.Count} node(s) of '{cluster.Name}': "
                + string.Join("; ", failures)
                + ". The users file is in place, so these nodes pick the users up when they restart; "
                + "sync again once the Redis is healthy.");
        }
    }

    /// <summary>
    /// The leader and follower pods whose Redis container is running, with that container's name. The
    /// operator names the container after its StatefulSet (<c>&lt;name&gt;-leader</c> / <c>-follower</c>)
    /// and labels the pods the same way; the exporter sidecar is the other container in the pod.
    /// </summary>
    private static async Task<List<(string Pod, string Container)>> ListRunningNodesAsync(
        Clusters.IClusterClient target, RedisCluster cluster, CancellationToken ct)
    {
        List<(string, string)> nodes = [];
        foreach (string role in new[] { "leader", "follower" })
        {
            string app = $"{cluster.Name}-{role}";
            string json = await target.GetJsonAsync("pods", cluster.Namespace, $"app={app}", ct);
            nodes.AddRange(RunningPodNames(json, app).Select(p => (p, app)));
        }
        return nodes;
    }

    /// <summary>
    /// The pods in a pod list whose <paramref name="container"/> is in the running state and that are
    /// not being deleted. Decided from the Redis container itself, not the pod phase: a pod stays
    /// <c>Running</c> while its Redis crash-loops, and treating that as a node to apply to would fail
    /// every sync on the cluster — while a node that is merely not ready yet does accept ACL commands
    /// and must get them.
    /// </summary>
    public static IEnumerable<string> RunningPodNames(string? podListJson, string container)
    {
        if (string.IsNullOrWhiteSpace(podListJson)) return [];

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(podListJson);
        if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)) return [];

        List<string> names = [];
        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            System.Text.Json.JsonElement meta = item.GetProperty("metadata");
            if (meta.TryGetProperty("deletionTimestamp", out _)) continue;

            bool running = item.TryGetProperty("status", out System.Text.Json.JsonElement status)
                && status.TryGetProperty("containerStatuses", out System.Text.Json.JsonElement statuses)
                && statuses.EnumerateArray().Any(s =>
                    s.TryGetProperty("name", out System.Text.Json.JsonElement n) && n.GetString() == container
                    && s.TryGetProperty("state", out System.Text.Json.JsonElement state)
                    && state.TryGetProperty("running", out _));
            if (running && meta.GetProperty("name").GetString() is string name)
                names.Add(name);
        }
        return names;
    }

    /// <summary>A binding's password, generated and vaulted on first use.</summary>
    private async Task<string> EnsureUserPasswordAsync(
        Guid tenantId, Guid redisClusterId, Guid bindingId, CancellationToken ct)
    {
        string name = RedisAcl.PasswordSecretName(bindingId);
        string? existing = await vaultService.GetRedisClusterSecretValueAsync(tenantId, redisClusterId, name, ct);
        if (!string.IsNullOrEmpty(existing)) return existing;

        string password = GeneratePassword();
        await vaultService.SetRedisClusterSecretAsync(tenantId, redisClusterId, name, password, ct);
        return password;
    }

    /// <summary>
    /// Switches a Redis created before per-app users existed over to them.
    ///
    /// <para><b>What happens, in order.</b> The switch is recorded first, so no sync that starts from
    /// here on hands out the admin password. Then every binding gets a password, the users file is
    /// written, and the RedisCluster is re-applied with the file included, which restarts its pods one
    /// at a time (the operator rolls the StatefulSets). Finally each binding is synced, which applies
    /// the users to the running nodes and moves the app's Secret to its own user. If the file or the
    /// template cannot be applied, the switch is undone and nothing else has changed.</para>
    ///
    /// <para><b>Apps keep working throughout</b>, because the admin password does not change —
    /// including any whose sync fails here because a node was restarting at that moment. Those stay on
    /// the admin password, are listed in the result, and are moved by the background reconcile.</para>
    ///
    /// <para>⚠️ Apps bound before the switch were given the admin password and still know it; only
    /// changing it takes that back. Bindings with a key prefix are confined to it from their first
    /// sync after the switch.</para>
    /// </summary>
    /// <returns>How many bindings moved, and the errors of those that did not.</returns>
    public async Task<(int Moved, IReadOnlyList<string> Failures)> EnablePerAppUsersAsync(
        Guid tenantId, Guid redisClusterId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        RedisCluster cluster = await db.RedisClusters
            .FirstOrDefaultAsync(c => c.Id == redisClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Redis cluster not found.");

        if (!RedisAcl.SupportsPerAppUsers(cluster.RedisVersion))
        {
            throw new InvalidOperationException(
                $"Per-app users need Redis 7 or later, and '{cluster.Name}' runs {cluster.RedisVersion}.");
        }

        using IDisposable? operation = gate?.BeginOperation(
            "switch a Redis to per-app users (its pods restart one at a time)");

        if (!cluster.PerAppUsers)
        {
            Clusters.IClusterClient target = await clusters.ForAsync(tenantId, cluster.KubernetesClusterId, ct)
                ?? throw new InvalidOperationException(
                    "The Redis's cluster has no stored kubeconfig, so it cannot be switched.");

            cluster.PerAppUsersSince = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            try
            {
                // The file before the pod template that includes it: a pod restarted by the roll must
                // find it, or it would not start.
                await target.ApplyManifestAsync(RedisAcl.BuildUsersConfigMap(
                    cluster.Name, cluster.Namespace, (await WantedUsersAsync(tenantId, cluster.Id, null, ct)).Values), ct);
                await target.ApplyManifestAsync(BuildClusterManifest(cluster), ct);
            }
            catch
            {
                await db.RedisClusters
                    .Where(c => c.Id == cluster.Id && c.TenantId == tenantId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.PerAppUsersSince, (DateTime?)null), CancellationToken.None);
                throw;
            }
        }

        List<Guid> bindingIds = await db.CacheBindings
            .Where(b => b.RedisClusterId == cluster.Id && b.TenantId == tenantId)
            .Select(b => b.Id)
            .ToListAsync(ct);

        int moved = 0;
        List<string> failures = [];
        foreach (Guid id in bindingIds)
        {
            try
            {
                await SyncCacheBindingAsync(tenantId, id, ct);
                moved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex.Message);
            }
        }

        return (moved, failures);
    }

    /// <summary>
    /// The background half of per-app users, for one tenant: reconciles the users of each of its
    /// Redis clusters that have them, and syncs bindings whose Secret is behind — still on the admin
    /// password, or with a key-prefix change whose sync failed.
    ///
    /// <para><b>Why this runs on a timer.</b> Deleting a deployment, an app or a tenant removes its
    /// bindings by database cascade without touching any cluster, so the Redis user would otherwise
    /// keep working until something else reconciled that Redis. And a switch-over or prefix change
    /// whose syncs failed while pods restarted finishes here instead of waiting for someone to press
    /// Sync.</para>
    /// </summary>
    /// <returns>What failed, one line per Redis or binding; empty when everything converged.</returns>
    public async Task<IReadOnlyList<string>> ReconcileTenantUsersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        List<RedisCluster> switched = await db.RedisClusters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.PerAppUsersSince != null
                && c.Status != RedisClusterStatus.Deleting)
            .ToListAsync(ct);

        List<string> failures = [];
        foreach (RedisCluster cluster in switched)
        {
            if (ct.IsCancellationRequested) break;

            // One Redis that cannot be reached must not keep the others from being cleaned up.
            try
            {
                await ReconcileUsersAsync(tenantId, cluster.Id, excludeBindingId: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{cluster.Name}: {ex.Message}");
                continue;
            }

            List<Guid> behind = await db.CacheBindings.AsNoTracking()
                .Where(b => b.RedisClusterId == cluster.Id && b.TenantId == tenantId && b.SyncEnabled
                    && (b.SyncPending || b.LastSyncedAt == null || b.LastSyncedAt < cluster.PerAppUsersSince))
                .Select(b => b.Id)
                .ToListAsync(ct);

            foreach (Guid id in behind)
            {
                try
                {
                    await SyncCacheBindingAsync(tenantId, id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{cluster.Name}, binding {id}: {ex.Message}");
                }
            }
        }

        return failures;
    }

    /// <summary>Every tenant in turn — the tenant is an input to each query, never read off a row.</summary>
    public async Task ReconcileAllUsersAsync(ILogger logger, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        foreach (Guid tenantId in await db.Tenants.Select(t => t.Id).ToListAsync(ct))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                foreach (string failure in await ReconcileTenantUsersAsync(tenantId, ct))
                    logger.LogWarning("Redis users for tenant {TenantId} did not converge: {Failure}", tenantId, failure);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconciling Redis users for tenant {TenantId} failed", tenantId);
            }
        }
    }

    // ── Live detail ───────────────────────────────────────────────────────────

    public async Task<RedisClusterDetail?> GetClusterDetailAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        RedisCluster? cluster = await db.RedisClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        if (cluster is null)
            return null;

        RedisClusterDetail detail = new() { Cluster = cluster, Phase = "Querying..." };

        try
        {
            Clusters.IClusterClient? target =
                await clusters.ForAsync(tenantId, cluster.KubernetesClusterId, ct);

            if (target is null)
            {
                detail.Phase = "No kubeconfig stored for this cluster";
                return detail;
            }

            string crdJson = await target.GetJsonAsync(
                $"rediscluster.redis.redis.opstreelabs.in/{cluster.Name}",
                cluster.Namespace, ct: ct);

            ParseCrdStatus(crdJson, detail);

            string leaderJson = await target.GetJsonAsync(
                "pods", cluster.Namespace, $"app={cluster.Name}-leader", ct);

            string followerJson = await target.GetJsonAsync(
                "pods", cluster.Namespace, $"app={cluster.Name}-follower", ct);

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

        using DataServicesDbContext db = dbFactory.CreateDbContext();
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

    public static string BuildClusterManifest(RedisCluster cluster)
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

        // Spread leader and follower pods across nodes (the operator sets no
        // anti-affinity by default). Preferred, so it won't block scheduling when
        // nodes < pods. The OT operator labels pods app=<name>-leader / -follower.
        sb.AppendLine("  redisLeader:");
        AppendUsersInclude(sb, cluster);
        sb.AppendLine("    affinity:");
        sb.AppendLine("      podAntiAffinity:");
        sb.AppendLine("        preferredDuringSchedulingIgnoredDuringExecution:");
        sb.AppendLine("          - weight: 100");
        sb.AppendLine("            podAffinityTerm:");
        sb.AppendLine("              topologyKey: kubernetes.io/hostname");
        sb.AppendLine("              labelSelector:");
        sb.AppendLine("                matchLabels:");
        sb.AppendLine($"                  app: {cluster.Name}-leader");
        sb.AppendLine("  redisFollower:");
        AppendUsersInclude(sb, cluster);
        sb.AppendLine("    affinity:");
        sb.AppendLine("      podAntiAffinity:");
        sb.AppendLine("        preferredDuringSchedulingIgnoredDuringExecution:");
        sb.AppendLine("          - weight: 100");
        sb.AppendLine("            podAffinityTerm:");
        sb.AppendLine("              topologyKey: kubernetes.io/hostname");
        sb.AppendLine("              labelSelector:");
        sb.AppendLine("                matchLabels:");
        sb.AppendLine($"                  app: {cluster.Name}-follower");

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

    /// <summary>
    /// Points a role's redis.conf at the per-app users file. The operator mounts the named ConfigMap
    /// beside redis.conf and includes it last, so its <c>user</c> lines add users while the
    /// <c>default</c> user stays on <c>requirepass</c> — see <see cref="RedisAcl"/> for why EntKube does
    /// not use the operator's full ACL-file route instead. Both roles need it: a follower is promoted
    /// to leader on failover and must already know every app.
    /// </summary>
    private static void AppendUsersInclude(StringBuilder sb, RedisCluster cluster)
    {
        if (!cluster.PerAppUsers) return;
        sb.AppendLine("    redisConfig:");
        sb.AppendLine($"      additionalRedisConfig: {RedisAcl.UsersConfigMapName(cluster.Name)}");
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
