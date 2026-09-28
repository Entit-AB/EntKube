using System.Globalization;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class ElasticsearchOperatorStatus
{
    public bool OperatorAvailable { get; set; }
    public string? OperatorClusterName { get; set; }
}

public class ElasticsearchPodInfo
{
    public string Name { get; set; } = "";
    /// <summary>The node tier this pod belongs to: master, hot, warm, cold, ingest, all or kibana.</summary>
    public string Tier { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public bool Ready { get; set; }
    public string? Node { get; set; }
    public int Restarts { get; set; }
}

public class ElasticsearchClusterDetail
{
    public required ElasticsearchCluster Cluster { get; set; }
    public string Phase { get; set; } = "Querying...";
    public bool Ready { get; set; }
    /// <summary>Cluster health as Elasticsearch reports it: green, yellow or red.</summary>
    public string? Health { get; set; }
    public int AvailableNodes { get; set; }
    public string? KibanaHealth { get; set; }
    public List<ElasticsearchPodInfo> Pods { get; set; } = [];
}

/// <summary>One tier's share of a cluster's resource footprint.</summary>
/// <param name="Tier">Tier name as it appears in the manifest ("master", "hot", …).</param>
/// <param name="Count">Pod count in this tier.</param>
/// <param name="CpuMilliPerPod">CPU request per pod, in millicores.</param>
/// <param name="MemoryBytesPerPod">Memory request (and limit) per pod, in bytes.</param>
/// <param name="StorageBytesPerPod">PVC size per pod, in bytes.</param>
/// <param name="HeapMb">JVM heap this tier's nodes will be started with, in MB. Zero for Kibana.</param>
public sealed record ElasticsearchTierFootprint(
    string Tier, int Count, long CpuMilliPerPod, long MemoryBytesPerPod, long StorageBytesPerPod, int HeapMb)
{
    public long CpuMilliTotal => CpuMilliPerPod * Count;
    public long MemoryBytesTotal => MemoryBytesPerPod * Count;
    public long StorageBytesTotal => StorageBytesPerPod * Count;
}

/// <summary>What a topology will ask the Kubernetes cluster for, before anything is applied.</summary>
public sealed class ElasticsearchFootprint
{
    public List<ElasticsearchTierFootprint> Tiers { get; init; } = [];
    public int PodCount => Tiers.Sum(t => t.Count);
    public long CpuMilliTotal => Tiers.Sum(t => t.CpuMilliTotal);
    public long MemoryBytesTotal => Tiers.Sum(t => t.MemoryBytesTotal);
    public long StorageBytesTotal => Tiers.Sum(t => t.StorageBytesTotal);
    /// <summary>The largest single pod — the one that has to fit on one node, or nothing schedules.</summary>
    public long LargestPodMemoryBytes => Tiers.Where(t => t.Count > 0).Select(t => t.MemoryBytesPerPod).DefaultIfEmpty(0).Max();
    public long LargestPodCpuMilli => Tiers.Where(t => t.Count > 0).Select(t => t.CpuMilliPerPod).DefaultIfEmpty(0).Max();
}

/// <summary>
/// The answer to "will this fit, and what is left afterwards" for one Kubernetes cluster.
/// <see cref="Blocking"/> is what separates a cluster that will be tight from one where pods
/// simply stay Pending forever.
/// </summary>
public sealed class ElasticsearchCapacityCheck
{
    public bool CapacityKnown { get; set; }
    public int NodeCount { get; set; }
    public long AllocatableCpuMilli { get; set; }
    public long AllocatableMemoryBytes { get; set; }
    /// <summary>Already requested by pods on the cluster, this cluster's current pods included.</summary>
    public long CommittedCpuMilli { get; set; }
    public long CommittedMemoryBytes { get; set; }
    /// <summary>Memory this change adds (negative when a topology is being shrunk).</summary>
    public long DeltaMemoryBytes { get; set; }
    public long DeltaCpuMilli { get; set; }
    /// <summary>Largest per-node free memory, for the "does one pod fit anywhere" test.</summary>
    public long LargestNodeFreeMemoryBytes { get; set; }
    public long LargestNodeFreeCpuMilli { get; set; }

    public long FreeMemoryBytes => Math.Max(0, AllocatableMemoryBytes - CommittedMemoryBytes);
    public long FreeCpuMilli => Math.Max(0, AllocatableCpuMilli - CommittedCpuMilli);

    /// <summary>Share of allocatable memory committed once this change lands, 0–1+.</summary>
    public double MemoryPressureAfter => AllocatableMemoryBytes <= 0
        ? 0
        : (double)(CommittedMemoryBytes + DeltaMemoryBytes) / AllocatableMemoryBytes;

    /// <summary>Reasons this will not schedule. Non-empty means refuse unless explicitly acknowledged.</summary>
    public List<string> Blocking { get; } = [];

    /// <summary>Reasons to think twice. Never refuses on its own.</summary>
    public List<string> Warnings { get; } = [];

    public bool Fits => Blocking.Count == 0;
}

/// <summary>
/// The non-secret half of an S3 snapshot repository: where the bucket is and how to talk to it.
/// The access key and secret never appear here — they go into the Elasticsearch keystore through a
/// Secret, and this record is what ends up in elasticsearch.yml beside it.
/// </summary>
/// <param name="Endpoint">Host and port, without a scheme — how Elasticsearch wants it.</param>
/// <param name="Protocol">"http" or "https".</param>
/// <param name="PathStyleAccess">True for MinIO, CubeFS and most self-hosted gateways.</param>
/// <param name="Region">Optional; meaningful for AWS.</param>
/// <param name="Bucket">The bucket snapshots are written to.</param>
/// <param name="BasePath">Prefix within the bucket, so two clusters can share one.</param>
public sealed record ElasticsearchS3Settings(
    string Endpoint, string Protocol, bool PathStyleAccess, string? Region, string Bucket, string BasePath)
{
    /// <summary>
    /// Splits a storage link's endpoint URL into the scheme and host:port Elasticsearch wants them
    /// as. A link stored without a scheme is assumed to be https, which is the safer guess.
    /// </summary>
    public static (string Endpoint, string Protocol) SplitEndpoint(string? url)
    {
        string raw = (url ?? "").Trim();
        if (raw.Length == 0) return ("", "https");

        if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            string hostPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return (hostPort, uri.Scheme);
        }

        return (raw.TrimEnd('/'), "https");
    }
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Manages Elasticsearch clusters and their Kibana through the ECK operator, plus the index
/// lifecycle policies that make a tiered cluster actually move data between its tiers.
///
/// <para><b>The topology.</b> Each role becomes its own nodeSet: dedicated master-eligible nodes
/// that only hold cluster state, a hot tier that takes the writes (and the ingest pipelines unless
/// a dedicated ingest tier is asked for), and optional warm and cold tiers that ILM ages data down
/// into. The roles are what make a search cluster survivable; the alternative — one nodeSet doing
/// everything — turns any heavy query into a master election.</para>
///
/// <para><b>The resources.</b> Every tier carries an explicit memory request that is also its
/// limit, and the JVM heap is written out as half of it, capped below the 31Gi compressed-oops
/// boundary where larger heaps start costing performance rather than buying it. Before anything is
/// applied, the whole footprint is measured against what the Kubernetes cluster actually has free,
/// and a topology that cannot schedule is refused with the numbers rather than accepted into a
/// permanent Pending.</para>
///
/// <para><b>The lifecycle policies.</b> ECK's declarative route for these (StackConfigPolicy) needs
/// an ECK Enterprise licence, so EntKube instead runs a short-lived Job inside the cluster that
/// PUTs the policies and index templates to the Elasticsearch API as the operator-generated
/// <c>elastic</c> user. ILM itself is free, the password never leaves the cluster, and the Job is
/// re-runnable — applying twice is a no-op.</para>
/// </summary>
public class ElasticsearchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    VaultService vaultService,
    AuditService auditService,
    ILogger<ElasticsearchService> logger)
{
    /// <summary>Above this share of committed memory the cluster is warned about, not refused.</summary>
    public const double MemoryPressureWarnThreshold = 0.80;

    /// <summary>Heap never goes above this: past ~31Gi the JVM loses compressed object pointers.</summary>
    public const int MaxHeapMb = 31 * 1024;

    /// <summary>Smallest heap worth starting an Elasticsearch node with.</summary>
    public const int MinHeapMb = 512;

    /// <summary>
    /// How often the apply Job is polled and how many times, i.e. two minutes by default. Settable
    /// so a test of the still-running path does not have to wait two real minutes for it.
    /// </summary>
    public TimeSpan JobPollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc cref="JobPollInterval"/>
    public int JobPollAttempts { get; set; } = 40;

    private const string EsApiVersion = "elasticsearch.k8s.elastic.co/v1";
    private const string KibanaApiVersion = "kibana.k8s.elastic.co/v1";

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<ElasticsearchCluster>> GetClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster).ThenInclude(k => k.Environment)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<ElasticsearchCluster?> GetClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
    }

    /// <summary>Is the ECK operator installed anywhere in this tenant's fleet?</summary>
    public async Task<ElasticsearchOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? op = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Cluster.TenantId == tenantId
                && c.Status == ComponentStatus.Installed
                && (c.Name == "eck-operator"
                    || c.ReleaseName == "elastic-operator"
                    || (c.HelmChartName ?? "") == "eck-operator"), ct);

        return new ElasticsearchOperatorStatus
        {
            OperatorAvailable = op is not null,
            OperatorClusterName = op?.Cluster.Name
        };
    }

    // ── Footprint and capacity ────────────────────────────────────────────────

    /// <summary>
    /// What this topology asks for, tier by tier. Pure arithmetic over the entity — no cluster
    /// access — so the UI can show it while the operator is still typing.
    /// </summary>
    public static ElasticsearchFootprint Footprint(ElasticsearchCluster c)
    {
        List<ElasticsearchTierFootprint> tiers = [];

        void Add(string tier, int count, string cpu, string memory, string storage, bool jvm = true)
        {
            if (count <= 0) return;
            long mem = ParseMemoryBytes(memory);
            tiers.Add(new ElasticsearchTierFootprint(
                tier, count, ParseCpuMilli(cpu), mem, ParseMemoryBytes(storage),
                jvm ? HeapMbFor(memory) : 0));
        }

        // With no data tier enabled the master set is a single all-roles node, and its "master"
        // storage is the data volume — see BuildElasticsearchManifest.
        Add(c.HasDataTiers ? "master" : "all", c.MasterCount, c.MasterCpuRequest, c.MasterMemory, c.MasterStorageSize);
        Add("hot", c.HotCount, c.HotCpuRequest, c.HotMemory, c.HotStorageSize);
        Add("warm", c.WarmCount, c.WarmCpuRequest, c.WarmMemory, c.WarmStorageSize);
        Add("cold", c.ColdCount, c.ColdCpuRequest, c.ColdMemory, c.ColdStorageSize);
        Add("ingest", c.IngestCount, c.IngestCpuRequest, c.IngestMemory, c.IngestStorageSize);

        if (c.KibanaEnabled)
            Add("kibana", c.KibanaCount, c.KibanaCpuRequest, c.KibanaMemory, "0", jvm: false);

        return new ElasticsearchFootprint { Tiers = tiers };
    }

    /// <summary>
    /// Measures a planned topology against the live Kubernetes cluster: what it has, what is
    /// already spoken for, and whether the biggest pod in the plan fits on any single node.
    /// </summary>
    /// <param name="current">
    /// The topology already applied, when this is an update. Only the difference is charged against
    /// free capacity — an edit that changes a storage class would otherwise look like a second
    /// cluster being created.
    /// </param>
    public async Task<ElasticsearchCapacityCheck> CheckCapacityAsync(
        Guid kubernetesClusterId,
        ElasticsearchCluster planned,
        ElasticsearchCluster? current = null,
        CancellationToken ct = default)
    {
        ElasticsearchCapacityCheck check = new();

        ElasticsearchFootprint want = Footprint(planned);
        ElasticsearchFootprint have = current is null ? new ElasticsearchFootprint() : Footprint(current);

        check.DeltaMemoryBytes = want.MemoryBytesTotal - have.MemoryBytesTotal;
        check.DeltaCpuMilli = want.CpuMilliTotal - have.CpuMilliTotal;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? k8sCluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == kubernetesClusterId, ct);

        if (k8sCluster is null || string.IsNullOrWhiteSpace(k8sCluster.Kubeconfig))
        {
            check.Warnings.Add("The Kubernetes cluster has no kubeconfig, so its free capacity could not be read. The topology below is what will be requested.");
            return check;
        }

        try
        {
            string nodesJson = await k8s.GetJsonAllNamespacesAsync("nodes", k8sCluster.Kubeconfig!, ct: ct);
            string podsJson = await k8s.GetJsonAllNamespacesAsync("pods", k8sCluster.Kubeconfig!, ct: ct);

            Dictionary<string, (long Cpu, long Mem)> allocatable = ParseNodeAllocatable(nodesJson);
            Dictionary<string, (long Cpu, long Mem)> committed = ParsePodRequestsByNode(podsJson);

            check.CapacityKnown = allocatable.Count > 0;
            check.NodeCount = allocatable.Count;
            check.AllocatableCpuMilli = allocatable.Values.Sum(v => v.Cpu);
            check.AllocatableMemoryBytes = allocatable.Values.Sum(v => v.Mem);
            check.CommittedCpuMilli = committed.Values.Sum(v => v.Cpu);
            check.CommittedMemoryBytes = committed.Values.Sum(v => v.Mem);

            foreach ((string node, (long cpu, long mem)) in allocatable)
            {
                committed.TryGetValue(node, out (long Cpu, long Mem) used);
                check.LargestNodeFreeMemoryBytes = Math.Max(check.LargestNodeFreeMemoryBytes, mem - used.Mem);
                check.LargestNodeFreeCpuMilli = Math.Max(check.LargestNodeFreeCpuMilli, cpu - used.Cpu);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Capacity check failed for cluster {ClusterId}", kubernetesClusterId);
            check.Warnings.Add($"Free capacity could not be read from the cluster ({ex.Message}). The topology below is what will be requested.");
            return check;
        }

        if (!check.CapacityKnown)
        {
            check.Warnings.Add("The cluster reported no nodes, so free capacity is unknown.");
            return check;
        }

        // Does the whole change fit in what is unallocated?
        if (check.DeltaMemoryBytes > check.FreeMemoryBytes)
        {
            check.Blocking.Add(
                $"This topology needs {FormatBytes(check.DeltaMemoryBytes)} more memory than the cluster has unallocated "
                + $"({FormatBytes(check.FreeMemoryBytes)} free of {FormatBytes(check.AllocatableMemoryBytes)} across {check.NodeCount} node(s)).");
        }

        if (check.DeltaCpuMilli > check.FreeCpuMilli)
        {
            check.Blocking.Add(
                $"This topology requests {FormatCpu(check.DeltaCpuMilli)} more CPU than the cluster has unallocated "
                + $"({FormatCpu(check.FreeCpuMilli)} free of {FormatCpu(check.AllocatableCpuMilli)}).");
        }

        // Does the biggest single pod fit on any one node? This is the check that catches three
        // 8Gi nodes planned onto a cluster whose largest machine has 6Gi free: the totals can look
        // fine while every pod stays Pending.
        if (want.LargestPodMemoryBytes > check.LargestNodeFreeMemoryBytes)
        {
            ElasticsearchTierFootprint biggest = want.Tiers.OrderByDescending(t => t.MemoryBytesPerPod).First();
            check.Blocking.Add(
                $"The '{biggest.Tier}' tier asks for {FormatBytes(biggest.MemoryBytesPerPod)} per pod, and no node has that much free "
                + $"(the roomiest has {FormatBytes(check.LargestNodeFreeMemoryBytes)}). Those pods would stay Pending.");
        }

        if (check.MemoryPressureAfter >= MemoryPressureWarnThreshold && check.Blocking.Count == 0)
        {
            check.Warnings.Add(
                $"The cluster would be {check.MemoryPressureAfter:P0} committed on memory afterwards, leaving little room for "
                + "anything else to be scheduled or for a node to be drained.");
        }

        if (planned.HasDataTiers && planned.MasterCount == 1)
            check.Warnings.Add("A single master node is a single point of failure: lose it and the cluster has no elected master. Three is the smallest quorum worth running in production.");

        return check;
    }

    // ── Cluster lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a managed Elasticsearch cluster (and its Kibana) and applies the CRs.
    /// </summary>
    /// <param name="acknowledgeCapacity">
    /// Applies the topology even though the capacity check says it will not schedule. Legitimate on
    /// a cluster with an autoscaler that has not grown yet; a mistake everywhere else, which is why
    /// it has to be said explicitly.
    /// </param>
    public async Task<ElasticsearchCluster> CreateClusterAsync(
        ElasticsearchCluster cluster,
        bool acknowledgeCapacity = false,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (await db.ElasticsearchClusters.AnyAsync(
                c => c.KubernetesClusterId == cluster.KubernetesClusterId
                    && c.Name == cluster.Name
                    && c.Namespace == cluster.Namespace, ct))
            throw new InvalidOperationException(
                $"An Elasticsearch cluster named '{cluster.Name}' already exists in namespace '{cluster.Namespace}'.");

        ValidateTopology(cluster);

        if (!acknowledgeCapacity)
        {
            ElasticsearchCapacityCheck check =
                await CheckCapacityAsync(cluster.KubernetesClusterId, cluster, null, ct);
            if (!check.Fits) throw new InvalidOperationException(CapacityRefusal(check));
        }

        cluster.Id = cluster.Id == Guid.Empty ? Guid.NewGuid() : cluster.Id;
        cluster.Status = ElasticsearchClusterStatus.Creating;
        cluster.CreatedAt = DateTime.UtcNow;

        db.ElasticsearchClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        try
        {
            KubernetesCluster k8sCluster = await db.KubernetesClusters
                .FirstAsync(c => c.Id == cluster.KubernetesClusterId, ct);
            string kubeconfig = k8sCluster.Kubeconfig
                ?? throw new InvalidOperationException("The Kubernetes cluster has no kubeconfig.");

            await k8s.EnsureNamespaceAsync(cluster.Namespace, kubeconfig, ct);
            await k8s.ApplyManifestAsync(
                BuildElasticsearchManifest(cluster, await ResolveS3Async(db, cluster, ct)), kubeconfig, ct);
            if (cluster.KibanaEnabled)
                await k8s.ApplyManifestAsync(BuildKibanaManifest(cluster), kubeconfig, ct);
        }
        catch (Exception ex)
        {
            cluster.Status = ElasticsearchClusterStatus.Failed;
            cluster.LastError = ex.Message;
        }

        await db.SaveChangesAsync(ct);
        return cluster;
    }

    /// <summary>
    /// Re-applies a changed topology. Elasticsearch handles node counts and added tiers by itself;
    /// shrinking a data tier relocates its shards first, which the operator does gracefully but
    /// not instantly. PVCs never shrink — a smaller storage size is refused by the API server.
    /// </summary>
    public async Task UpdateClusterAsync(
        Guid tenantId,
        ElasticsearchCluster edited,
        bool acknowledgeCapacity = false,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == edited.Id && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        ElasticsearchCluster before = Clone(cluster);
        CopyTopology(from: edited, to: cluster);
        ValidateTopology(cluster);

        if (!acknowledgeCapacity)
        {
            ElasticsearchCapacityCheck check =
                await CheckCapacityAsync(cluster.KubernetesClusterId, cluster, before, ct);
            if (!check.Fits) throw new InvalidOperationException(CapacityRefusal(check));
        }

        cluster.Status = ElasticsearchClusterStatus.Updating;
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            await k8s.ApplyManifestAsync(
                BuildElasticsearchManifest(cluster, await ResolveS3Async(db, cluster, ct)), kubeconfig, ct);

            if (cluster.KibanaEnabled)
                await k8s.ApplyManifestAsync(BuildKibanaManifest(cluster), kubeconfig, ct);
            else if (before.KibanaEnabled)
                await k8s.DeleteManifestAsync("kibana", cluster.Name, cluster.Namespace, kubeconfig, ct);

            cluster.LastError = null;
        }
        catch (Exception ex)
        {
            cluster.Status = ElasticsearchClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes the Elasticsearch and Kibana CRs and forgets the cluster. ECK removes the
    /// StatefulSets with them; the PVCs go too, and with them the indices.
    /// </summary>
    public async Task DeleteClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .Include(c => c.Users)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        cluster.Status = ElasticsearchClusterStatus.Deleting;
        await db.SaveChangesAsync(ct);

        // Bindings first, and explicitly: a binding holds the user back with a Restrict FK, and
        // leaving the Secret behind in an application's namespace would leave it holding working-
        // looking credentials for a cluster that no longer exists.
        List<ElasticsearchBinding> bindings = await db.ElasticsearchBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .Where(b => b.ElasticsearchClusterId == clusterId)
            .ToListAsync(ct);

        foreach (ElasticsearchBinding binding in bindings)
        {
            try
            {
                await k8s.DeleteManifestAsync("secret", binding.KubernetesSecretName,
                    binding.AppDeployment.Namespace, binding.AppDeployment.Cluster.Kubeconfig!, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Removing binding Secret {Secret} failed", binding.KubernetesSecretName);
            }
        }

        db.ElasticsearchBindings.RemoveRange(bindings);
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            if (cluster.KibanaEnabled)
                await k8s.DeleteManifestAsync("kibana", cluster.Name, cluster.Namespace, kubeconfig, ct);
            await k8s.DeleteManifestAsync("elasticsearch", cluster.Name, cluster.Namespace, kubeconfig, ct);

            // The lifecycle ConfigMaps only exist if policies were ever applied, so their absence
            // is normal and must not fail a delete that has already removed the cluster itself.
            foreach ((string kindLabel, string name) in Leftovers(cluster).Concat(UserLeftovers(cluster)))
            {
                try
                {
                    await k8s.DeleteManifestAsync(kindLabel, name, cluster.Namespace, kubeconfig, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "No {Name} to remove for {Cluster}", name, cluster.Name);
                }
            }
        }
        catch (Exception ex)
        {
            cluster.Status = ElasticsearchClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        db.ElasticsearchClusters.Remove(cluster);
        await db.SaveChangesAsync(ct);
    }

    // ── Live detail + status reconciliation ────────────────────────────────────

    public async Task<ElasticsearchClusterDetail?> GetClusterDetailAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster? cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        if (cluster is null) return null;

        ElasticsearchClusterDetail detail = new() { Cluster = cluster };

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

            string crJson = await k8s.GetJsonAsync(
                $"elasticsearch.elasticsearch.k8s.elastic.co/{cluster.Name}", cluster.Namespace, kubeconfig, ct: ct);

            (string? health, string? phase, int available) = ParseElasticsearchStatus(crJson);
            detail.Health = health;
            detail.AvailableNodes = available;
            detail.Ready = string.Equals(phase, "Ready", StringComparison.OrdinalIgnoreCase);

            string podsJson = await k8s.GetJsonAsync(
                "pods", cluster.Namespace, kubeconfig,
                $"elasticsearch.k8s.elastic.co/cluster-name={cluster.Name}", ct);
            detail.Pods = ParsePodList(podsJson);

            if (cluster.KibanaEnabled)
            {
                try
                {
                    string kbJson = await k8s.GetJsonAsync(
                        $"kibana.kibana.k8s.elastic.co/{cluster.Name}", cluster.Namespace, kubeconfig, ct: ct);
                    detail.KibanaHealth = ParseKibanaHealth(kbJson);

                    string kbPodsJson = await k8s.GetJsonAsync(
                        "pods", cluster.Namespace, kubeconfig,
                        $"kibana.k8s.elastic.co/name={cluster.Name}", ct);
                    detail.Pods.AddRange(ParsePodList(kbPodsJson, "kibana"));
                }
                catch { /* Kibana not created yet — the Elasticsearch view is still useful */ }
            }

            int readyPods = detail.Pods.Count(p => p.Ready);
            detail.Phase = detail.Ready
                ? $"Ready — health {detail.Health ?? "unknown"}"
                : detail.Pods.Count == 0
                    ? "Provisioning..."
                    : $"{phase ?? "Starting"} ({readyPods}/{detail.Pods.Count} pods ready)";

            await ReconcileStatusAsync(cluster.Id, detail.Ready, detail.Health, ct);
        }
        catch
        {
            detail.Phase = "Unable to reach cluster";
        }

        return detail;
    }

    /// <summary>
    /// Updates the stored status from a live reading. Safe to call from a background loop, and
    /// deliberately does not regress a Running cluster on a transient failure to read it.
    /// </summary>
    public async Task ReconcileStatusAsync(
        Guid clusterId, bool ready, string? health, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ElasticsearchCluster? tracked = await db.ElasticsearchClusters.FindAsync([clusterId], ct);
        if (tracked is null) return;

        ElasticsearchClusterStatus newStatus = ready
            ? ElasticsearchClusterStatus.Running
            : tracked.Status switch
            {
                ElasticsearchClusterStatus.Running => ElasticsearchClusterStatus.Running,
                ElasticsearchClusterStatus.Deleting => ElasticsearchClusterStatus.Deleting,
                _ => ElasticsearchClusterStatus.Creating
            };

        if (newStatus == tracked.Status && health == tracked.Health) return;

        tracked.Status = newStatus;
        if (health is not null) tracked.Health = health;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>How stale a disk/shard reading may get before the poller takes another.</summary>
    public static readonly TimeSpan InsightMaxAge = TimeSpan.FromMinutes(15);

    /// <summary>How often the poller asks SLM whether the snapshots are still happening.</summary>
    public static readonly TimeSpan SnapshotCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Reconciles every tenant's clusters — used by the background poller.
    ///
    /// <para>Three readings, at three cadences, because they cost three different amounts. The CR's
    /// phase is a kubectl get and happens every run. Disk and unassigned shards need an exec into a
    /// node, so they are taken when the stored reading is older than <see cref="InsightMaxAge"/>.
    /// Snapshot state costs a Job, so it is taken every <see cref="SnapshotCheckInterval"/> — often
    /// enough that "nothing has been backed up for days" is noticed within hours of being true,
    /// which is the whole point of the advisor knowing about it at all.</para>
    /// </summary>
    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<ElasticsearchCluster> clusters = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .Where(c => c.Status != ElasticsearchClusterStatus.Failed)
            .ToListAsync(ct);

        DateTime now = DateTime.UtcNow;

        foreach (ElasticsearchCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.KubernetesCluster.Kubeconfig)) continue;

            bool ready = false;
            try
            {
                string json = await k8s.GetJsonAsync(
                    $"elasticsearch.elasticsearch.k8s.elastic.co/{cluster.Name}", cluster.Namespace,
                    cluster.KubernetesCluster.Kubeconfig!, ct: ct);
                (string? health, string? phase, _) = ParseElasticsearchStatus(json);
                ready = string.Equals(phase, "Ready", StringComparison.OrdinalIgnoreCase);
                await ReconcileStatusAsync(cluster.Id, ready, health, ct);
            }
            catch { /* cluster unreachable — leave status as-is */ }

            // Only worth asking a cluster that is up; a starting one has no disk story to tell.
            if (!ready) continue;

            if (cluster.InsightCheckedAt is null || now - cluster.InsightCheckedAt.Value > InsightMaxAge)
                await RefreshInsightAsync(cluster.Id, ct);

            if (cluster.SnapshotsEnabled
                && (cluster.SnapshotLastCheckedAt is null
                    || now - cluster.SnapshotLastCheckedAt.Value > SnapshotCheckInterval))
            {
                try
                {
                    await RefreshSnapshotStatusAsync(cluster.TenantId, cluster.Id, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Snapshot status refresh failed for {Cluster}", cluster.Name);
                }
            }
        }
    }

    /// <summary>Reads the operator-generated password for the built-in <c>elastic</c> superuser.</summary>
    public async Task<string?> GetElasticPasswordAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ElasticsearchCluster? cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
        if (cluster is null) return null;

        return await k8s.GetSecretValueAsync(
            cluster.ElasticUserSecretName, "elastic", cluster.Namespace,
            cluster.KubernetesCluster.Kubeconfig!, ct);
    }

    // ── Index lifecycle policies ───────────────────────────────────────────────

    public async Task<List<ElasticsearchIlmPolicy>> GetPoliciesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchIlmPolicies
            .Where(p => p.TenantId == tenantId && p.ElasticsearchClusterId == clusterId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<ElasticsearchIlmPolicy> CreatePolicyAsync(
        ElasticsearchIlmPolicy policy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .FirstOrDefaultAsync(c => c.Id == policy.ElasticsearchClusterId && c.TenantId == policy.TenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (await db.ElasticsearchIlmPolicies.AnyAsync(
                p => p.ElasticsearchClusterId == policy.ElasticsearchClusterId && p.Name == policy.Name, ct))
            throw new InvalidOperationException($"A policy named '{policy.Name}' already exists on this cluster.");

        ValidatePolicy(policy, cluster);

        policy.Id = policy.Id == Guid.Empty ? Guid.NewGuid() : policy.Id;
        policy.CreatedAt = DateTime.UtcNow;
        db.ElasticsearchIlmPolicies.Add(policy);
        await db.SaveChangesAsync(ct);

        await ApplyPoliciesAsync(policy.TenantId, policy.ElasticsearchClusterId, ct);
        return policy;
    }

    public async Task UpdatePolicyAsync(
        ElasticsearchIlmPolicy edited, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchIlmPolicy policy = await db.ElasticsearchIlmPolicies
            .FirstOrDefaultAsync(p => p.Id == edited.Id && p.TenantId == edited.TenantId, ct)
            ?? throw new InvalidOperationException("Policy not found.");

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .FirstAsync(c => c.Id == policy.ElasticsearchClusterId, ct);

        policy.IndexPattern = edited.IndexPattern;
        policy.UseDataStream = edited.UseDataStream;
        policy.Shards = edited.Shards;
        policy.Replicas = edited.Replicas;
        policy.RolloverMaxPrimaryShardGb = edited.RolloverMaxPrimaryShardGb;
        policy.RolloverMaxAgeDays = edited.RolloverMaxAgeDays;
        policy.WarmAfterDays = edited.WarmAfterDays;
        policy.ColdAfterDays = edited.ColdAfterDays;
        policy.DeleteAfterDays = edited.DeleteAfterDays;
        policy.TemplatePriority = edited.TemplatePriority;

        ValidatePolicy(policy, cluster);
        await db.SaveChangesAsync(ct);

        await ApplyPoliciesAsync(policy.TenantId, policy.ElasticsearchClusterId, ct);
    }

    /// <summary>
    /// Removes the policy and its index template from Elasticsearch, then forgets it. The template
    /// goes first: a policy still referenced by one cannot be deleted. Indices already created from
    /// the template keep their settings — this stops new ones inheriting the policy, it does not
    /// retroactively unmanage old data.
    /// </summary>
    public async Task DeletePolicyAsync(
        Guid tenantId, Guid policyId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchIlmPolicy policy = await db.ElasticsearchIlmPolicies
            .Include(p => p.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(p => p.Id == policyId && p.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Policy not found.");

        ElasticsearchCluster cluster = policy.ElasticsearchCluster;

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
            string jobName = JobName(cluster, "ilm-delete");
            string script = BuildIlmDeleteScript(cluster, policy);

            await k8s.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, IlmConfigMapName(cluster, "delete"), script), kubeconfig, ct);
            await k8s.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, IlmConfigMapName(cluster, "delete")), kubeconfig, ct);
            await WaitForJobAsync(cluster, jobName, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            // Best effort, as with every other "delete the remote thing then the local row" path:
            // a policy that could not be removed from Elasticsearch is a leftover, not a reason to
            // keep showing one EntKube no longer manages.
            logger.LogWarning(ex, "Removing ILM policy {Policy} from Elasticsearch failed", policy.Name);
        }

        db.ElasticsearchIlmPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Pushes every policy this cluster has to Elasticsearch, through a Job that runs inside the
    /// cluster. Idempotent: the same policies applied twice are the same policies.
    /// </summary>
    public async Task ApplyPoliciesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        List<ElasticsearchIlmPolicy> policies = await db.ElasticsearchIlmPolicies
            .Where(p => p.ElasticsearchClusterId == clusterId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        if (policies.Count == 0) return;

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = IlmConfigMapName(cluster);
        string jobName = JobName(cluster, "ilm-apply");

        JobOutcome outcome;
        string log;

        try
        {
            await k8s.ApplyManifestAsync(BuildIlmConfigMapManifest(cluster, policies), kubeconfig, ct);
            await k8s.ApplyManifestAsync(BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);
            (outcome, log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            foreach (ElasticsearchIlmPolicy p in policies) p.LastError = Truncate(ex.Message, 2000);
            await db.SaveChangesAsync(ct);
            throw;
        }

        switch (outcome)
        {
            case JobOutcome.Succeeded:
                foreach (ElasticsearchIlmPolicy p in policies)
                {
                    p.LastError = null;
                    p.LastAppliedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(ct);
                return;

            case JobOutcome.Failed:
                foreach (ElasticsearchIlmPolicy p in policies) p.LastError = Truncate(log, 2000);
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException(
                    "Applying the index lifecycle policies failed. Elasticsearch said:\n" + Truncate(log, 1200));

            default:
                // Still running is not a failure, and must not be recorded as one. The job waits up
                // to five minutes for a cluster that is still forming — longer than this method is
                // willing to hold a page open — so the honest answer is that the answer is not in
                // yet, and the policies keep their previous state until it is.
                throw new InvalidOperationException(
                    $"The apply job '{jobName}' is still running — the cluster is probably still "
                    + "starting. It will finish on its own; use Re-apply to pick up the result.");
        }
    }

    /// <summary>How a one-shot Job ended, as far as a bounded wait could tell.</summary>
    public enum JobOutcome { Succeeded, Failed, StillRunning }

    // ── Version upgrades ───────────────────────────────────────────────────────

    /// <summary>
    /// What an upgrade would do, and every reason it should not happen yet.
    /// </summary>
    /// <param name="CurrentVersion">The version the cluster runs now.</param>
    /// <param name="TargetVersion">The version asked for.</param>
    /// <param name="Blockers">
    /// Reasons this will fail, or lose data if it does not. Non-empty means refuse unless the
    /// operator explicitly accepts them.
    /// </param>
    /// <param name="Warnings">Reasons to pick a better moment. Never refuse on their own.</param>
    /// <param name="Notes">What will happen, so nobody has to infer it from the version numbers.</param>
    public sealed record ElasticsearchUpgradePlan(
        string CurrentVersion,
        string TargetVersion,
        IReadOnlyList<string> Blockers,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Notes)
    {
        public bool CanProceed => Blockers.Count == 0;
    }

    /// <summary>
    /// Works out whether a cluster can move to a version, before ECK's webhook says no in one line
    /// or — worse — accepts it and rolls the pods.
    ///
    /// <para>The version rules are Elasticsearch's: it cannot be downgraded at all, because the data
    /// directory is migrated in place on first start, and a major can only be reached from the one
    /// directly below it. The rest of the checks are about the moment rather than the versions: a
    /// rolling upgrade restarts every node in turn, which a red cluster, a full disk or a missing
    /// backup each turn from routine into an incident.</para>
    /// </summary>
    public async Task<ElasticsearchUpgradePlan> PlanUpgradeAsync(
        Guid tenantId, Guid clusterId, string targetVersion, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        return PlanUpgrade(cluster, targetVersion, DateTime.UtcNow);
    }

    /// <summary>The plan, as pure arithmetic over the cluster's stored state. Split out to be tested.</summary>
    public static ElasticsearchUpgradePlan PlanUpgrade(
        ElasticsearchCluster cluster, string targetVersion, DateTime now)
    {
        List<string> blockers = [];
        List<string> warnings = [];
        List<string> notes = [];

        string target = (targetVersion ?? "").Trim();

        if (!TryParseVersion(cluster.Version, out (int Major, int Minor, int Patch) from))
            blockers.Add($"The cluster's current version ('{cluster.Version}') is not a version number this can reason about.");

        if (!TryParseVersion(target, out (int Major, int Minor, int Patch) to))
            blockers.Add($"'{target}' is not an Elastic Stack version, e.g. 9.5.0.");

        if (blockers.Count > 0)
            return new ElasticsearchUpgradePlan(cluster.Version, target, blockers, warnings, notes);

        int comparison = Compare(from, to);

        if (comparison == 0)
        {
            blockers.Add($"The cluster already runs {target}.");
            return new ElasticsearchUpgradePlan(cluster.Version, target, blockers, warnings, notes);
        }

        if (comparison > 0)
        {
            // Not a policy: Elasticsearch migrates the data directory in place on first start, and
            // an older node will not open a newer one's data at all.
            blockers.Add(
                $"Elasticsearch cannot be downgraded. {cluster.Version} has already migrated its data directory, and "
                + $"{target} would refuse to start on it. Restore a snapshot into a new cluster instead.");
            return new ElasticsearchUpgradePlan(cluster.Version, target, blockers, warnings, notes);
        }

        if (to.Major - from.Major > 1)
        {
            blockers.Add(
                $"{from.Major}.x cannot reach {to.Major}.x directly. Elasticsearch supports one major at a time, so go to "
                + $"the last {from.Major + 1}.x release first, then on from there.");
        }
        else if (to.Major > from.Major)
        {
            notes.Add(
                $"This is a major upgrade. Elasticsearch supports it from the final minor of {from.Major}.x — if this "
                + $"cluster is not on that minor yet, upgrade within {from.Major}.x first.");
            warnings.Add(
                "Read the release notes for breaking changes before starting: a major upgrade is the one that removes "
                + "settings and APIs an application may still be using.");
        }

        // The moment, rather than the versions.

        if (string.Equals(cluster.Health, "red", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(
                "The cluster is red. A rolling upgrade restarts every node in turn, and doing that while shards are "
                + "already unavailable is how a recoverable problem becomes a lost index.");
        }
        else if (string.Equals(cluster.Health, "yellow", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                "The cluster is yellow, so some shards have no replica. Each node restart takes their only copy offline "
                + "for the duration.");
        }

        if (!cluster.SnapshotsEnabled)
        {
            blockers.Add(
                "This cluster has no snapshots. An upgrade cannot be undone — the only way back from a bad one is a "
                + "restore, and there is nothing to restore from.");
        }
        else if (cluster.SnapshotLastSuccessAt is null)
        {
            blockers.Add("Snapshots are configured but none has ever succeeded, so there is nothing to go back to.");
        }
        else if (now - cluster.SnapshotLastSuccessAt.Value > TimeSpan.FromDays(2))
        {
            warnings.Add(
                $"The last successful snapshot was {(int)(now - cluster.SnapshotLastSuccessAt.Value).TotalDays} days ago. "
                + "Take one first so the way back is recent.");
        }

        if (cluster.HighestNodeDiskPercent is int disk && disk >= DiskHighWatermarkPercent)
        {
            warnings.Add(
                $"A node is {disk}% full. A rolling upgrade moves shards around, and past the {DiskHighWatermarkPercent}% "
                + "watermark there is nowhere for them to move to.");
        }

        if (cluster.Status != ElasticsearchClusterStatus.Running)
            warnings.Add($"The cluster is {cluster.Status}, not Running — let it settle before adding an upgrade to it.");

        notes.Add(
            $"ECK rolls the nodes one at a time, masters last. Kibana is applied at {target} too and stays on the old "
            + "version until Elasticsearch is ready for it.");

        if (cluster.MasterCount == 1 && !cluster.HasDataTiers)
            notes.Add("This is a single node, so it will be down for the duration of the restart rather than rolling.");

        return new ElasticsearchUpgradePlan(cluster.Version, target, blockers, warnings, notes);
    }

    /// <summary>
    /// Moves the cluster (and Kibana) to a new version.
    /// </summary>
    /// <param name="acceptBlockers">
    /// Goes ahead despite the plan's blockers. Every one of them is a real failure mode, so this is
    /// recorded with the operator's name against it.
    /// </param>
    public async Task<ElasticsearchUpgradePlan> UpgradeAsync(
        Guid tenantId, Guid clusterId, string targetVersion, string performedBy,
        bool acceptBlockers = false, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        ElasticsearchUpgradePlan plan = PlanUpgrade(cluster, targetVersion, DateTime.UtcNow);

        if (!plan.CanProceed && !acceptBlockers)
            throw new InvalidOperationException(
                string.Join("\n", plan.Blockers)
                + "\n\nEvery one of these is a way this goes wrong. Accept them explicitly if you have a reason to.");

        await auditService.RecordAsync(
            deploymentId: null,
            action: "elasticsearch.upgrade",
            resourceKind: "Elasticsearch",
            resourceName: $"{cluster.Namespace}/{cluster.Name}",
            details: $"{plan.CurrentVersion} → {plan.TargetVersion}"
                + (plan.CanProceed ? "" : $"; accepted blockers: {string.Join("; ", plan.Blockers)}"),
            performedBy: performedBy,
            ct: ct);

        cluster.Version = plan.TargetVersion;
        cluster.Status = ElasticsearchClusterStatus.Updating;
        await db.SaveChangesAsync(ct);

        try
        {
            string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

            // Elasticsearch first. ECK holds Kibana at its current version until the cluster it is
            // associated with can serve it, so applying both is safe and saves a second visit.
            await k8s.ApplyManifestAsync(
                BuildElasticsearchManifest(cluster, await ResolveS3Async(db, cluster, ct)), kubeconfig, ct);

            if (cluster.KibanaEnabled)
                await k8s.ApplyManifestAsync(BuildKibanaManifest(cluster), kubeconfig, ct);

            cluster.LastError = null;
        }
        catch (Exception ex)
        {
            cluster.Status = ElasticsearchClusterStatus.Failed;
            cluster.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>Parses "9.5.0" and "9.5.0-SNAPSHOT" into its three numbers.</summary>
    public static bool TryParseVersion(string? version, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version)) return false;

        // A pre-release suffix does not change which release it is a build of.
        string core = version.Trim().Split('-', '+')[0];
        string[] parts = core.Split('.');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch))
            return false;

        if (major < 0 || minor < 0 || patch < 0) return false;

        parsed = (major, minor, patch);
        return true;
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }

    // ── Live cluster reads ─────────────────────────────────────────────────────

    /// <summary>Disk as one Elasticsearch node reports it.</summary>
    public sealed record ElasticsearchNodeDisk(string Node, long UsedBytes, long TotalBytes, int Shards)
    {
        public int PercentUsed => TotalBytes <= 0 ? 0 : (int)Math.Round(UsedBytes * 100.0 / TotalBytes);
    }

    /// <summary>One index, as the browser shows it.</summary>
    public sealed record ElasticsearchIndexInfo(
        string Name, string Health, string Status, long DocCount, long StoreBytes,
        int PrimaryShards, int Replicas, string? IlmPhase, string? IlmError);

    /// <summary>What a live look at the cluster turned up.</summary>
    public sealed class ElasticsearchInsight
    {
        public string? Health { get; init; }
        public int UnassignedShards { get; init; }
        public List<ElasticsearchNodeDisk> Nodes { get; init; } = [];
        public List<ElasticsearchIndexInfo> Indices { get; init; } = [];

        /// <summary>The fullest node. Elasticsearch stops allocating at 85% and goes read-only at 95%.</summary>
        public int HighestDiskPercent => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.PercentUsed);
    }

    /// <summary>Elasticsearch stops allocating new shards to a node past this.</summary>
    public const int DiskHighWatermarkPercent = 85;

    /// <summary>Past this, Elasticsearch makes every index with a shard on the node read-only.</summary>
    public const int DiskFloodStagePercent = 95;

    /// <summary>
    /// Asks Elasticsearch something, from inside one of its own pods.
    ///
    /// <para><b>Why exec rather than the API server's proxy.</b> The proxy strips the caller's
    /// Authorization header before forwarding — it exists so a user's token never reaches a pod —
    /// which is exactly the header Elasticsearch needs. The Jobs elsewhere in this service solve
    /// that by running inside the cluster, but a Job costs the better part of a minute, and this
    /// path is used by a background poller and a page. So: exec into a node, ask over loopback, and
    /// pass the password on stdin so it is never in an argument list.</para>
    /// </summary>
    public async Task<string> QueryAsync(
        ElasticsearchCluster cluster, string path, CancellationToken ct = default)
    {
        string kubeconfig = cluster.KubernetesCluster.Kubeconfig
            ?? throw new InvalidOperationException("The Kubernetes cluster has no kubeconfig.");

        string podsJson = await k8s.GetJsonAsync(
            "pods", cluster.Namespace, kubeconfig,
            $"elasticsearch.k8s.elastic.co/cluster-name={cluster.Name}", ct);

        string pod = FirstReadyPodName(podsJson)
            ?? throw new InvalidOperationException(
                $"No Ready Elasticsearch pod in '{cluster.Namespace}' to ask — the cluster may still be starting.");

        string password = await k8s.GetSecretValueAsync(
            cluster.ElasticUserSecretName, "elastic", cluster.Namespace, kubeconfig, ct)
            ?? throw new InvalidOperationException(
                $"The '{cluster.ElasticUserSecretName}' Secret is not readable, so there is no way to authenticate.");

        return await k8s.RunCommandOnPodWithStdinAsync(
            pod, cluster.Namespace, ["sh", "-c", BuildQueryCommand(path)], password + "\n",
            kubeconfig, ct, "elasticsearch");
    }

    /// <summary>
    /// The shell run inside the node. The password arrives on stdin; the CA is whichever of ECK's
    /// two mount points exists in this version, and if neither does the request still goes over
    /// loopback inside the very container that serves it — there is no position to intercept it
    /// from, which is the only reason skipping verification is defensible anywhere in this service.
    /// </summary>
    public static string BuildQueryCommand(string path) =>
        "read -r ES_PW; " +
        "CA=''; " +
        "for f in /usr/share/elasticsearch/config/http-certs/ca.crt /mnt/elastic-internal/http-certs/ca.crt; do " +
        "  if [ -f \"$f\" ]; then CA=\"--cacert $f\"; break; fi; " +
        "done; " +
        "if [ -z \"$CA\" ]; then CA='--insecure'; fi; " +
        $"curl -sS $CA -u \"elastic:$ES_PW\" \"https://localhost:9200{path}\"";

    /// <summary>
    /// Reads health, per-node disk, and the indices with their lifecycle phase. One place, because
    /// every caller wants the same three things and each one costs an exec.
    /// </summary>
    public async Task<ElasticsearchInsight> GetInsightAsync(
        Guid tenantId, Guid clusterId, bool includeIndices = true, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        (string? health, int unassigned) = ParseClusterHealth(
            await QueryAsync(cluster, "/_cluster/health", ct));

        List<ElasticsearchNodeDisk> nodes = ParseAllocation(
            await QueryAsync(cluster, "/_cat/allocation?format=json&bytes=b", ct));

        List<ElasticsearchIndexInfo> indices = [];
        if (includeIndices)
        {
            string catJson = await QueryAsync(cluster,
                "/_cat/indices?format=json&bytes=b&h=index,health,status,pri,rep,docs.count,store.size", ct);

            // Best effort: a cluster with no ILM-managed index answers this perfectly well, but an
            // older one, or one still starting, should not cost us the index list.
            string ilmJson = "";
            try { ilmJson = await QueryAsync(cluster, "/_all/_ilm/explain", ct); }
            catch (Exception ex) { logger.LogDebug(ex, "ILM explain unavailable on {Cluster}", cluster.Name); }

            indices = ParseIndices(catJson, ilmJson);
        }

        return new ElasticsearchInsight
        {
            Health = health,
            UnassignedShards = unassigned,
            Nodes = nodes,
            Indices = indices
        };
    }

    /// <summary>
    /// Records what a live look found, so the advisor and the list can read it without touching the
    /// cluster. Called by the poller; never throws at the caller.
    /// </summary>
    public async Task RefreshInsightAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster? cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null || string.IsNullOrWhiteSpace(cluster.KubernetesCluster.Kubeconfig)) return;

        try
        {
            (string? health, int unassigned) = ParseClusterHealth(
                await QueryAsync(cluster, "/_cluster/health", ct));
            List<ElasticsearchNodeDisk> nodes = ParseAllocation(
                await QueryAsync(cluster, "/_cat/allocation?format=json&bytes=b", ct));

            if (health is not null) cluster.Health = health;
            cluster.UnassignedShards = unassigned;
            cluster.HighestNodeDiskPercent = nodes.Count == 0 ? null : nodes.Max(n => n.PercentUsed);
            cluster.InsightCheckedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // A cluster that cannot be asked keeps its last reading, and InsightCheckedAt stops
            // moving — which is what tells the advisor the numbers are stale rather than fine.
            logger.LogDebug(ex, "Insight refresh failed for {Cluster}", cluster.Name);
        }
    }

    // ── Live-read parsing ──────────────────────────────────────────────────────

    public static string? FirstReadyPodName(string podsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(podsJson);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return null;

            foreach (JsonElement pod in items.EnumerateArray())
            {
                if (!pod.TryGetProperty("status", out JsonElement status)) continue;
                if (!status.TryGetProperty("containerStatuses", out JsonElement containers)) continue;

                bool ready = containers.EnumerateArray().All(
                    c => c.TryGetProperty("ready", out JsonElement r) && r.GetBoolean());

                if (ready) return pod.GetProperty("metadata").GetProperty("name").GetString();
            }
        }
        catch { }
        return null;
    }

    public static (string? Status, int UnassignedShards) ParseClusterHealth(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string? status = doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
            int unassigned = doc.RootElement.TryGetProperty("unassigned_shards", out JsonElement u)
                && u.TryGetInt32(out int n) ? n : 0;
            return (status, unassigned);
        }
        catch { return (null, 0); }
    }

    public static List<ElasticsearchNodeDisk> ParseAllocation(string json)
    {
        List<ElasticsearchNodeDisk> nodes = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                string node = Str(row, "node") ?? "";

                // _cat/allocation ends with an UNASSIGNED row that has no node and no disk figures.
                // Counting it as a node at 0% would quietly drag the fleet's worst disk down.
                if (node.Length == 0 || node == "UNASSIGNED") continue;

                nodes.Add(new ElasticsearchNodeDisk(
                    node,
                    Num(row, "disk.used"),
                    Num(row, "disk.total"),
                    (int)Num(row, "shards")));
            }
        }
        catch { }
        return [.. nodes.OrderByDescending(n => n.PercentUsed)];
    }

    public static List<ElasticsearchIndexInfo> ParseIndices(string catJson, string ilmJson)
    {
        Dictionary<string, (string? Phase, string? Error)> ilm = ParseIlmExplain(ilmJson);

        List<ElasticsearchIndexInfo> indices = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(catJson);
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                string name = Str(row, "index") ?? "";
                if (name.Length == 0) continue;

                ilm.TryGetValue(name, out (string? Phase, string? Error) lifecycle);

                indices.Add(new ElasticsearchIndexInfo(
                    name,
                    Str(row, "health") ?? "",
                    Str(row, "status") ?? "",
                    Num(row, "docs.count"),
                    Num(row, "store.size"),
                    (int)Num(row, "pri"),
                    (int)Num(row, "rep"),
                    lifecycle.Phase,
                    lifecycle.Error));
            }
        }
        catch { }

        // Biggest first: the question behind opening this list is nearly always "what is eating the
        // disk", and a hidden index does not answer it.
        return [.. indices.OrderByDescending(i => i.StoreBytes).ThenBy(i => i.Name, StringComparer.Ordinal)];
    }

    /// <summary>Index → lifecycle phase, and the failure message when a policy has stalled.</summary>
    public static Dictionary<string, (string? Phase, string? Error)> ParseIlmExplain(string json)
    {
        Dictionary<string, (string?, string?)> result = [];
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("indices", out JsonElement indices)) return result;

            foreach (JsonProperty index in indices.EnumerateObject())
            {
                if (index.Value.TryGetProperty("managed", out JsonElement managed)
                    && managed.ValueKind == JsonValueKind.False)
                    continue;

                string? phase = index.Value.TryGetProperty("phase", out JsonElement p) ? p.GetString() : null;

                // A policy that has stopped is reported as the ERROR step, with the reason nested
                // under step_info. Anything else there is a step name, not a problem.
                string? error = null;
                if (index.Value.TryGetProperty("step", out JsonElement step)
                    && step.GetString() == "ERROR"
                    && index.Value.TryGetProperty("step_info", out JsonElement info))
                {
                    error = info.TryGetProperty("reason", out JsonElement reason)
                        ? reason.GetString()
                        : info.ToString();
                }

                result[index.Name] = (phase, error);
            }
        }
        catch { }
        return result;
    }

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

    private static long Num(JsonElement row, string name)
    {
        // _cat with format=json returns every column as a string, including the numeric ones.
        if (!row.TryGetProperty(name, out JsonElement v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out long direct) ? direct : 0;
        return long.TryParse(v.GetString(), out long parsed) ? parsed : 0;
    }

    // ── Users and app bindings ─────────────────────────────────────────────────

    public async Task<List<ElasticsearchUser>> GetUsersAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchUsers
            .Where(u => u.TenantId == tenantId && u.ElasticsearchClusterId == clusterId)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a native-realm user with a role scoped to one index pattern.
    ///
    /// <para>The password is generated here, written to a Secret in the Elasticsearch namespace, and
    /// handed to the Job through an environment variable from that Secret. It is never in a command
    /// line, never in a manifest field EntKube keeps, and never in the management plane's database —
    /// bindings read it back out of the cluster when they need it.</para>
    /// </summary>
    public async Task<ElasticsearchUser> CreateUserAsync(
        Guid tenantId,
        Guid clusterId,
        string username,
        string indexPattern,
        ElasticsearchAccess access,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        username = (username ?? "").Trim().ToLowerInvariant();
        indexPattern = (indexPattern ?? "").Trim();

        ValidateUsername(username);

        if (string.IsNullOrWhiteSpace(indexPattern))
            throw new InvalidOperationException(
                "An index pattern is required. A user with access to everything is the superuser this exists to avoid.");

        if (await db.ElasticsearchUsers.AnyAsync(
                u => u.ElasticsearchClusterId == clusterId && u.Username == username, ct))
            throw new InvalidOperationException($"A user named '{username}' already exists on this cluster.");

        ElasticsearchUser user = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ElasticsearchClusterId = clusterId,
            Username = username,
            IndexPattern = indexPattern,
            Access = access
        };

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string password = GeneratePassword();

        await k8s.ApplyManifestAsync(BuildUserCredentialsSecret(cluster, user, password), kubeconfig, ct);

        string configMap = UserConfigMapName(cluster, user);
        string jobName = JobName(cluster, $"user-{username}");

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildUserApplyScript(cluster, user)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap, user.CredentialsSecretName), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

        user.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) user.LastAppliedAt = DateTime.UtcNow;

        db.ElasticsearchUsers.Add(user);
        await db.SaveChangesAsync(ct);

        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException("Creating the user failed. Elasticsearch said:\n" + Truncate(log, 1200));
        if (outcome == JobOutcome.StillRunning)
            throw new InvalidOperationException(
                $"The job '{jobName}' is still running — the cluster may still be starting. The user is recorded here "
                + "and can be re-applied once it is up.");

        return user;
    }

    /// <summary>Re-applies a user and its role, e.g. after the cluster was rebuilt.</summary>
    public async Task ApplyUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchUser user = await db.ElasticsearchUsers
            .Include(u => u.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("User not found.");

        ElasticsearchCluster cluster = user.ElasticsearchCluster;
        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = UserConfigMapName(cluster, user);
        string jobName = JobName(cluster, $"user-{user.Username}");

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildUserApplyScript(cluster, user)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap, user.CredentialsSecretName), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

        user.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) user.LastAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (outcome != JobOutcome.Succeeded)
            throw new InvalidOperationException("Applying the user failed:\n" + Truncate(log, 1200));
    }

    /// <summary>
    /// Removes a user, its role and its password Secret. Refuses while an application is still bound
    /// to it: deleting it out from under a binding leaves the app holding credentials that stop
    /// working, with nothing anywhere saying why.
    /// </summary>
    public async Task DeleteUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchUser user = await db.ElasticsearchUsers
            .Include(u => u.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("User not found.");

        int bindings = await db.ElasticsearchBindings.CountAsync(b => b.ElasticsearchUserId == userId, ct);
        if (bindings > 0)
            throw new InvalidOperationException(
                $"{bindings} application binding(s) still use '{user.Username}'. Remove those first — deleting the user "
                + "would leave them with credentials that quietly stop working.");

        ElasticsearchCluster cluster = user.ElasticsearchCluster;
        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;

        try
        {
            string configMap = UserConfigMapName(cluster, user, "delete");
            string jobName = JobName(cluster, $"user-del-{user.Username}");

            await k8s.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildUserDeleteScript(cluster, user)), kubeconfig, ct);
            await k8s.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);
            await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

            await k8s.DeleteManifestAsync("secret", user.CredentialsSecretName, cluster.Namespace, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing Elasticsearch user {User} from the cluster failed", user.Username);
        }

        db.ElasticsearchUsers.Remove(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ElasticsearchBinding>> GetBindingsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.App).ThenInclude(a => a.Customer)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .Include(b => b.ElasticsearchUser)
            .Where(b => b.TenantId == tenantId && b.ElasticsearchClusterId == clusterId)
            .OrderBy(b => b.AppDeployment.Name)
            .ToListAsync(ct);
    }

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

    public async Task<ElasticsearchBinding> CreateBindingAsync(
        Guid tenantId, Guid clusterId, Guid appDeploymentId, Guid userId, string secretName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (!await db.ElasticsearchUsers.AnyAsync(
                u => u.Id == userId && u.ElasticsearchClusterId == clusterId && u.TenantId == tenantId, ct))
            throw new InvalidOperationException("Pick a user on this cluster for the application to connect as.");

        ElasticsearchBinding binding = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ElasticsearchClusterId = clusterId,
            AppDeploymentId = appDeploymentId,
            ElasticsearchUserId = userId,
            KubernetesSecretName = string.IsNullOrWhiteSpace(secretName) ? "elasticsearch" : secretName.Trim()
        };

        db.ElasticsearchBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await SyncBindingAsync(tenantId, binding.Id, ct);
        return binding;
    }

    /// <summary>
    /// Writes the endpoint, credentials and CA into the application's namespace.
    ///
    /// <para>The password is read back out of the Elasticsearch namespace rather than kept here, so
    /// this is the only moment it exists in the management plane's memory, and re-syncing is how a
    /// rotated password reaches the application.</para>
    /// </summary>
    public async Task SyncBindingAsync(Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchBinding binding = await db.ElasticsearchBindings
            .Include(b => b.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .Include(b => b.ElasticsearchUser)
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Binding not found.");

        ElasticsearchCluster cluster = binding.ElasticsearchCluster;
        string esKubeconfig = cluster.KubernetesCluster.Kubeconfig!;

        string password = await k8s.GetSecretValueAsync(
            binding.ElasticsearchUser.CredentialsSecretName, "password", cluster.Namespace, esKubeconfig, ct)
            ?? throw new InvalidOperationException(
                $"The password Secret for '{binding.ElasticsearchUser.Username}' is not in the cluster. "
                + "Re-apply the user to recreate it.");

        Dictionary<string, string> data = new()
        {
            ["ELASTICSEARCH_URL"] = cluster.HttpEndpoint,
            ["ELASTICSEARCH_USERNAME"] = binding.ElasticsearchUser.Username,
            ["ELASTICSEARCH_PASSWORD"] = password
        };

        // Without the CA the application either fails to connect or is told to skip verification,
        // and the second one is how a search cluster becomes reachable by anything on the network.
        string? caCert = await k8s.GetSecretValueAsync(
            cluster.HttpCertsSecretName, "ca.crt", cluster.Namespace, esKubeconfig, ct);
        if (!string.IsNullOrEmpty(caCert)) data["ELASTICSEARCH_CA_CRT"] = caCert;

        string appKubeconfig = binding.AppDeployment.Cluster.Kubeconfig!;
        string appNs = binding.AppDeployment.Namespace;

        await k8s.EnsureNamespaceAsync(appNs, appKubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildBindingSecretManifest(binding.KubernetesSecretName, appNs, data), appKubeconfig, ct);

        binding.LastSyncedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a binding and the Secret it put in the application's namespace.</summary>
    public async Task DeleteBindingAsync(Guid tenantId, Guid bindingId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchBinding binding = await db.ElasticsearchBindings
            .Include(b => b.AppDeployment).ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(b => b.Id == bindingId && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Binding not found.");

        try
        {
            await k8s.DeleteManifestAsync("secret", binding.KubernetesSecretName,
                binding.AppDeployment.Namespace, binding.AppDeployment.Cluster.Kubeconfig!, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the binding Secret {Secret} failed", binding.KubernetesSecretName);
        }

        db.ElasticsearchBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("The user needs a name.");

        if (username.Length > 63)
            throw new InvalidOperationException("Usernames are 63 characters or fewer.");

        // The name is also a Kubernetes Secret name and a file name inside the Job, so it is held to
        // the strictest of the three rather than to Elasticsearch's own rules.
        if (!username.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-'))
            throw new InvalidOperationException(
                "A username may only contain lowercase letters, digits and '-' — it also names a Kubernetes Secret.");

        if (!char.IsAsciiLetterOrDigit(username[0]) || !char.IsAsciiLetterOrDigit(username[^1]))
            throw new InvalidOperationException("A username must start and end with a letter or digit.");
    }

    private static string GeneratePassword()
    {
        // Base64 with the two characters that would need escaping inside a JSON body swapped out:
        // the password travels to Elasticsearch as a JSON string built in a shell script.
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "x").Replace("/", "y")[..32];
    }

    // ── Snapshots ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns on snapshots: puts the S3 credentials into the Elasticsearch keystore, re-applies the
    /// cluster so every node knows where the bucket is, then registers the repository and the SLM
    /// policy that fills it.
    ///
    /// <para>The repository registration is retried inside the Job, because the keystore reaches
    /// the nodes a little after the CR does — and a repository registered one second too early
    /// fails with an authentication error that looks exactly like wrong credentials.</para>
    /// </summary>
    public async Task ConfigureSnapshotsAsync(
        Guid tenantId,
        Guid clusterId,
        Guid storageLinkId,
        string? basePath,
        string scheduleCron,
        int expireAfterDays,
        int minCount,
        int maxCount,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(l => l.Id == storageLinkId && l.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        if (string.IsNullOrWhiteSpace(link.BucketName))
            throw new InvalidOperationException(
                $"Storage link '{link.Name}' has no bucket, so there is nowhere to write snapshots.");

        if (minCount < 1 || maxCount < minCount)
            throw new InvalidOperationException("Keep at least one snapshot, and no fewer than the minimum.");

        if (expireAfterDays < 1)
            throw new InvalidOperationException("Snapshots must be kept for at least a day.");

        if (string.IsNullOrWhiteSpace(scheduleCron))
            throw new InvalidOperationException("A schedule is required.");

        cluster.SnapshotsEnabled = true;
        cluster.SnapshotStorageLinkId = storageLinkId;
        cluster.SnapshotBasePath = string.IsNullOrWhiteSpace(basePath) ? cluster.Name : basePath!.Trim();
        cluster.SnapshotScheduleCron = scheduleCron.Trim();
        cluster.SnapshotExpireAfterDays = expireAfterDays;
        cluster.SnapshotMinCount = minCount;
        cluster.SnapshotMaxCount = maxCount;
        await db.SaveChangesAsync(ct);

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        ElasticsearchS3Settings s3 = await BuildS3SettingsAsync(tenantId, cluster, link, ct);

        await k8s.ApplyManifestAsync(BuildKeystoreSecretManifest(cluster, await ReadS3CredentialsAsync(tenantId, link, ct)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(BuildElasticsearchManifest(cluster, s3), kubeconfig, ct);

        string configMap = SnapshotConfigMapName(cluster);
        string jobName = JobName(cluster, "snapshot-setup");

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotSetupScript(cluster, s3)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

        if (outcome == JobOutcome.Failed)
        {
            cluster.SnapshotLastFailure = Truncate(log, 2000);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException(
                "Registering the snapshot repository failed. Elasticsearch said:\n" + Truncate(log, 1200));
        }

        if (outcome == JobOutcome.StillRunning)
            throw new InvalidOperationException(
                $"The setup job '{jobName}' is still running — the keystore may not have reached every node yet. "
                + "It will finish on its own; use Check now to pick up the result.");

        cluster.SnapshotLastFailure = null;
        cluster.SnapshotLastCheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stops taking snapshots: removes the SLM policy so nothing new is written. The repository and
    /// everything already in the bucket are left alone — deleting a repository is how you lose the
    /// backups you turned this off while still having.
    /// </summary>
    public async Task DisableSnapshotsAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = SnapshotConfigMapName(cluster, "disable");
        string jobName = JobName(cluster, "snapshot-disable");

        try
        {
            await k8s.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildSnapshotDisableScript(cluster)), kubeconfig, ct);
            await k8s.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);
            await WaitForJobAsync(cluster, jobName, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the SLM policy for {Cluster} failed", cluster.Name);
        }

        cluster.SnapshotsEnabled = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes a snapshot now, off-schedule, and waits for the policy to accept it.</summary>
    public async Task RunSnapshotNowAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (!cluster.SnapshotsEnabled)
            throw new InvalidOperationException("Snapshots are not configured for this cluster.");

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = SnapshotConfigMapName(cluster, "execute");
        string jobName = JobName(cluster, "snapshot-run");

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotExecuteScript(cluster)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException(
                "Starting the snapshot failed. Elasticsearch said:\n" + Truncate(log, 1200));

        // Starting a snapshot is not finishing one — SLM accepts the request and the snapshot runs
        // in the background, so the result is read back separately.
        await RefreshSnapshotStatusAsync(tenantId, clusterId, ct);
    }

    /// <summary>
    /// Reads the SLM policy's own record of its last run back into EntKube. This is the only place
    /// a snapshot's success is established: a scheduled snapshot happens entirely inside the
    /// cluster, so nothing else would ever notice it failing.
    /// </summary>
    public async Task RefreshSnapshotStatusAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (!cluster.SnapshotsEnabled) return;

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = SnapshotConfigMapName(cluster, "status");
        string jobName = JobName(cluster, "snapshot-status");

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotStatusScript(cluster)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);
        if (outcome != JobOutcome.Succeeded) return;

        SnapshotStatus status = ParseSnapshotStatus(log, cluster.SnapshotPolicyName);
        cluster.SnapshotLastSuccessAt = status.LastSuccessAt;
        cluster.SnapshotLastSuccessName = status.LastSuccessName;
        cluster.SnapshotLastFailure = Truncate(status.LastFailure, 2000);
        cluster.SnapshotLastCheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One snapshot as the repository lists it.</summary>
    /// <param name="Name">The snapshot name SLM gave it.</param>
    /// <param name="State">SUCCESS, PARTIAL, FAILED or IN_PROGRESS.</param>
    /// <param name="StartedAt">When it started, UTC.</param>
    /// <param name="EndedAt">When it finished, UTC. Null while it is still running.</param>
    /// <param name="IndexCount">How many indices it holds.</param>
    /// <param name="FailedShards">Shards that did not make it in — non-zero means PARTIAL.</param>
    public sealed record ElasticsearchSnapshotInfo(
        string Name, string State, DateTime? StartedAt, DateTime? EndedAt, int IndexCount, int FailedShards)
    {
        /// <summary>A PARTIAL snapshot restores, but not all of it — say so rather than listing it as a backup.</summary>
        public bool Complete => string.Equals(State, "SUCCESS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How a restore treats data that is already in the cluster.</summary>
    public enum RestoreMode
    {
        /// <summary>
        /// Restore under a new name, leaving everything live untouched. The safe default, and the
        /// only one that lets somebody look at what they restored before trusting it.
        /// </summary>
        SideBySide,

        /// <summary>
        /// Restore over the existing indices. They are closed first, because Elasticsearch refuses
        /// to restore into an open index, and it reopens them itself once the data is back.
        /// </summary>
        InPlace
    }

    /// <summary>
    /// Lists what is actually in the repository. This is a read, but it still goes through a Job —
    /// the repository lives behind the same authentication as everything else.
    /// </summary>
    public async Task<List<ElasticsearchSnapshotInfo>> ListSnapshotsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (!cluster.SnapshotsEnabled)
            throw new InvalidOperationException("This cluster has no snapshot repository configured.");

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = SnapshotConfigMapName(cluster, "list");
        string jobName = JobName(cluster, "snapshot-list");

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotListScript(cluster)), kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException("Listing the snapshots failed:\n" + Truncate(log, 1200));
        if (outcome == JobOutcome.StillRunning)
            throw new InvalidOperationException(
                "The cluster did not answer in time. It may still be starting — try again in a moment.");

        return ParseSnapshotList(log);
    }

    /// <summary>
    /// Restores one snapshot.
    ///
    /// <para>Side by side is the default and touches nothing: the indices come back under a prefix,
    /// so somebody can look at what they restored before trusting it. In place closes the matching
    /// indices first — Elasticsearch will not restore into an open index — and Elasticsearch reopens
    /// them itself when the data is back. That one replaces live data, which is why it is a separate
    /// mode rather than a checkbox, and why it is written to the audit log with a name on it.</para>
    ///
    /// <para>Returns the job's own output, so the caller can show what Elasticsearch actually did
    /// rather than a claim that it worked.</para>
    /// </summary>
    public async Task<string> RestoreSnapshotAsync(
        Guid tenantId,
        Guid clusterId,
        string snapshotName,
        string indexPattern,
        RestoreMode mode,
        string renamePrefix,
        bool includeGlobalState,
        string performedBy,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (!cluster.SnapshotsEnabled)
            throw new InvalidOperationException("This cluster has no snapshot repository configured.");

        if (string.IsNullOrWhiteSpace(snapshotName))
            throw new InvalidOperationException("Which snapshot?");

        if (string.IsNullOrWhiteSpace(indexPattern))
            throw new InvalidOperationException("An index pattern is required — \"*\" for everything in the snapshot.");

        if (mode == RestoreMode.SideBySide && string.IsNullOrWhiteSpace(renamePrefix))
            throw new InvalidOperationException(
                "A side-by-side restore needs a prefix to restore under, or it would collide with the live indices "
                + "it is meant to leave alone.");

        if (mode == RestoreMode.SideBySide && includeGlobalState)
            throw new InvalidOperationException(
                "The global state cannot be restored side by side — there is only one set of cluster settings, "
                + "templates and ILM policies, so restoring them always overwrites the live ones.");

        string kubeconfig = cluster.KubernetesCluster.Kubeconfig!;
        string configMap = SnapshotConfigMapName(cluster, "restore");
        string jobName = JobName(cluster, "snapshot-restore");

        await auditService.RecordAsync(
            deploymentId: null,
            action: mode == RestoreMode.InPlace ? "elasticsearch.restore.in-place" : "elasticsearch.restore",
            resourceKind: "Elasticsearch",
            resourceName: $"{cluster.Namespace}/{cluster.Name}",
            details: $"snapshot '{snapshotName}', indices '{indexPattern}'"
                + (mode == RestoreMode.SideBySide ? $", restored under '{renamePrefix}'" : ", over the live indices")
                + (includeGlobalState ? ", including the global cluster state" : ""),
            performedBy: performedBy,
            ct: ct);

        await k8s.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap,
                BuildRestoreScript(cluster, snapshotName, indexPattern, mode, renamePrefix, includeGlobalState)),
            kubeconfig, ct);
        await k8s.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), kubeconfig, ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, kubeconfig, ct);

        return outcome switch
        {
            JobOutcome.Succeeded => log,
            JobOutcome.Failed => throw new InvalidOperationException(
                "The restore failed. Elasticsearch said:\n" + Truncate(log, 1200)),
            _ => throw new InvalidOperationException(
                $"The restore job '{jobName}' is still running — a large restore takes longer than this page waits. "
                + "It continues in the cluster; watch the job's logs or the cluster's health for the result.")
        };
    }

    /// <summary>Reads the snapshot list out of the list job's log.</summary>
    public static List<ElasticsearchSnapshotInfo> ParseSnapshotList(string log)
    {
        List<ElasticsearchSnapshotInfo> result = [];

        const string marker = "---ENTKUBE-SNAPSHOTS---";
        int at = log.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return result;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(log[(at + marker.Length)..].Trim());
            if (!doc.RootElement.TryGetProperty("snapshots", out JsonElement snapshots)) return result;

            foreach (JsonElement snap in snapshots.EnumerateArray())
            {
                string name = snap.TryGetProperty("snapshot", out JsonElement n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;

                string state = snap.TryGetProperty("state", out JsonElement st) ? st.GetString() ?? "" : "";

                DateTime? started = snap.TryGetProperty("start_time_in_millis", out JsonElement s1)
                    && s1.TryGetInt64(out long sms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(sms).UtcDateTime : null;

                // A snapshot still running has no end time, which is how it is told apart from one
                // that finished in the same second it started.
                DateTime? ended = snap.TryGetProperty("end_time_in_millis", out JsonElement e1)
                    && e1.TryGetInt64(out long ems) && ems > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ems).UtcDateTime : null;

                int indices = snap.TryGetProperty("indices", out JsonElement ix) && ix.ValueKind == JsonValueKind.Array
                    ? ix.GetArrayLength() : 0;

                int failed = snap.TryGetProperty("shards", out JsonElement sh)
                    && sh.TryGetProperty("failed", out JsonElement f) && f.TryGetInt32(out int fc) ? fc : 0;

                result.Add(new ElasticsearchSnapshotInfo(name, state, started, ended, indices, failed));
            }
        }
        catch { /* an unreadable list reads as no snapshots, which the caller shows as such */ }

        return [.. result.OrderByDescending(r => r.StartedAt ?? DateTime.MinValue)];
    }

    /// <summary>What an SLM policy says about its own last run.</summary>
    public sealed record SnapshotStatus(DateTime? LastSuccessAt, string? LastSuccessName, string? LastFailure);

    /// <summary>
    /// Pulls the SLM record out of the status job's log. The script prints a marker first so the
    /// JSON can be found whatever else the container said on its way there.
    /// </summary>
    public static SnapshotStatus ParseSnapshotStatus(string log, string policyName)
    {
        const string marker = "---ENTKUBE-SLM---";
        int at = log.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return new SnapshotStatus(null, null, null);

        string json = log[(at + marker.Length)..].Trim();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(policyName, out JsonElement policy))
                return new SnapshotStatus(null, null, null);

            DateTime? successAt = null;
            string? successName = null;
            if (policy.TryGetProperty("last_success", out JsonElement ok))
            {
                if (ok.TryGetProperty("snapshot_name", out JsonElement n)) successName = n.GetString();
                if (ok.TryGetProperty("time", out JsonElement t) && t.TryGetInt64(out long ms))
                    successAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }

            string? failure = null;
            if (policy.TryGetProperty("last_failure", out JsonElement bad))
            {
                string? details = bad.TryGetProperty("details", out JsonElement d) ? d.GetString() : null;
                long? failedMs = bad.TryGetProperty("time", out JsonElement ft) && ft.TryGetInt64(out long fm) ? fm : null;

                // A failure older than the last success is history, not a problem — SLM keeps both
                // forever, and reporting the stale one would leave a healthy cluster looking broken.
                bool stale = failedMs is long f && successAt is DateTime sAt
                    && DateTimeOffset.FromUnixTimeMilliseconds(f).UtcDateTime <= sAt;

                if (!stale) failure = details;
            }

            return new SnapshotStatus(successAt, successName, failure);
        }
        catch
        {
            return new SnapshotStatus(null, null, null);
        }
    }

    /// <summary>
    /// Resolves the repository settings for a cluster, or null when it keeps no snapshots. A storage
    /// link that has been deleted out from under the cluster reads as "no snapshots" rather than
    /// failing every apply from then on.
    /// </summary>
    private async Task<ElasticsearchS3Settings?> ResolveS3Async(
        ApplicationDbContext db, ElasticsearchCluster cluster, CancellationToken ct)
    {
        if (!cluster.SnapshotsEnabled || cluster.SnapshotStorageLinkId is not Guid linkId) return null;

        StorageLink? link = await db.StorageLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null || string.IsNullOrWhiteSpace(link.BucketName))
        {
            logger.LogWarning(
                "Elasticsearch cluster {Cluster} points at storage link {Link}, which no longer exists or has no bucket",
                cluster.Name, linkId);
            return null;
        }

        return await BuildS3SettingsAsync(cluster.TenantId, cluster, link, ct);
    }

    private Task<ElasticsearchS3Settings> BuildS3SettingsAsync(
        Guid tenantId, ElasticsearchCluster cluster, StorageLink link, CancellationToken ct)
    {
        (string endpoint, string protocol) = ElasticsearchS3Settings.SplitEndpoint(link.Endpoint);

        // MinIO and CubeFS are addressed by path, not by a bucket-named host — virtual-host style
        // against them resolves to a hostname that does not exist.
        bool pathStyle = link.Provider is StorageProvider.MinIO or StorageProvider.CubeFS
            or StorageProvider.CleuraS3;

        return Task.FromResult(new ElasticsearchS3Settings(
            endpoint, protocol, pathStyle, link.Region, link.BucketName!,
            string.IsNullOrWhiteSpace(cluster.SnapshotBasePath) ? cluster.Name : cluster.SnapshotBasePath!));
    }

    private async Task<(string AccessKey, string SecretKey)> ReadS3CredentialsAsync(
        Guid tenantId, StorageLink link, CancellationToken ct)
    {
        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, link.Id, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, link.Id, "SECRET_KEY", ct);

        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            throw new InvalidOperationException(
                $"Storage link '{link.Name}' has no ACCESS_KEY/SECRET_KEY in the vault. Add them on the Storage tab "
                + "before turning snapshots on.");

        return (accessKey, secretKey);
    }

    // ── Manifest builders ──────────────────────────────────────────────────────

    /// <summary>
    /// Renders the Elasticsearch CR: one nodeSet per enabled tier, each with its roles, its
    /// resources, its heap and its volume claim.
    /// </summary>
    public static string BuildElasticsearchManifest(ElasticsearchCluster c, ElasticsearchS3Settings? s3 = null)
    {
        StringBuilder sb = new();
        sb.AppendLine($"apiVersion: {EsApiVersion}");
        sb.AppendLine("kind: Elasticsearch");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.Name}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  version: {c.Version}");
        // Keep the volumes of a node that was scaled away, so scaling back up does not silently
        // start with empty disks, and a mistaken scale-down is recoverable.
        sb.AppendLine("  volumeClaimDeletePolicy: DeleteOnScaledownOnly");

        if (s3 is not null)
        {
            // The S3 access key and secret live in the Elasticsearch keystore, which ECK fills from
            // this Secret and reloads without a restart. Only the non-secret half of the client
            // settings — where the bucket is and how to talk to it — goes in elasticsearch.yml below.
            sb.AppendLine("  secureSettings:");
            sb.AppendLine($"    - secretName: {c.SnapshotCredentialsSecretName}");
        }

        sb.AppendLine("  nodeSets:");

        if (!c.HasDataTiers)
        {
            // No data tier: this is the single-node (or small all-roles) shape. Leaving node.roles
            // unset gives the node every role, which is what makes one node a working cluster.
            AppendNodeSet(sb, c, "all", c.MasterCount, roles: null,
                c.MasterCpuRequest, c.MasterMemory, c.MasterStorageSize, s3);
        }
        else
        {
            AppendNodeSet(sb, c, "master", c.MasterCount, ["master"],
                c.MasterCpuRequest, c.MasterMemory, c.MasterStorageSize, s3);

            // The hot tier holds the write indices and the content indices Kibana's own state lives
            // in. It also runs the ingest pipelines unless a dedicated ingest tier was asked for.
            List<string> hotRoles = ["data_hot", "data_content"];
            if (c.IngestCount <= 0) hotRoles.Add("ingest");
            AppendNodeSet(sb, c, "hot", c.HotCount, hotRoles,
                c.HotCpuRequest, c.HotMemory, c.HotStorageSize, s3);

            AppendNodeSet(sb, c, "warm", c.WarmCount, ["data_warm"],
                c.WarmCpuRequest, c.WarmMemory, c.WarmStorageSize, s3);

            AppendNodeSet(sb, c, "cold", c.ColdCount, ["data_cold"],
                c.ColdCpuRequest, c.ColdMemory, c.ColdStorageSize, s3);

            // A node holding only the ingest role is also the cluster's coordinating node: it takes
            // the client connections and fans out the search, without any shard of its own to lose.
            AppendNodeSet(sb, c, "ingest", c.IngestCount, ["ingest"],
                c.IngestCpuRequest, c.IngestMemory, c.IngestStorageSize, s3);
        }

        return sb.ToString();
    }

    private static void AppendNodeSet(
        StringBuilder sb, ElasticsearchCluster c, string name, int count, IReadOnlyList<string>? roles,
        string cpu, string memory, string storage, ElasticsearchS3Settings? s3 = null)
    {
        if (count <= 0) return;

        sb.AppendLine($"    - name: {name}");
        sb.AppendLine($"      count: {count}");

        // Only emit config: when there is something under it. An all-roles node with mmap left on
        // and no snapshot repository has nothing to say here, and "config:" with an empty body is a
        // null the operator rejects.
        if (roles is not null || !c.AllowMmap || s3 is not null)
        {
            sb.AppendLine("      config:");
            if (roles is not null)
                sb.AppendLine($"        node.roles: [{string.Join(", ", roles.Select(r => $"\"{r}\""))}]");
            if (!c.AllowMmap)
            {
                // Without vm.max_map_count raised on the host, an mmap-using node dies on startup
                // with a bootstrap check. Turning mmap off trades some search performance for
                // starting at all.
                sb.AppendLine("        node.store.allow_mmap: false");
            }
            if (s3 is not null)
            {
                // These are client settings, so every node needs them — a repository registered on
                // one node is used by all of them.
                sb.AppendLine($"        s3.client.default.endpoint: \"{s3.Endpoint}\"");
                sb.AppendLine($"        s3.client.default.protocol: {s3.Protocol}");
                if (s3.PathStyleAccess)
                    sb.AppendLine("        s3.client.default.path_style_access: true");
                if (!string.IsNullOrWhiteSpace(s3.Region))
                    sb.AppendLine($"        s3.client.default.region: \"{s3.Region}\"");
            }
        }

        if (c.ZoneAware)
        {
            sb.AppendLine("      zoneAwareness:");
            sb.AppendLine("        topologyKey: topology.kubernetes.io/zone");
        }

        int heapMb = HeapMbFor(memory);
        sb.AppendLine("      podTemplate:");
        sb.AppendLine("        spec:");
        sb.AppendLine("          containers:");
        sb.AppendLine("            - name: elasticsearch");
        sb.AppendLine("              env:");
        sb.AppendLine("                - name: ES_JAVA_OPTS");
        sb.AppendLine($"                  value: \"-Xms{heapMb}m -Xmx{heapMb}m\"");
        sb.AppendLine("              resources:");
        sb.AppendLine("                requests:");
        sb.AppendLine($"                  cpu: {cpu}");
        sb.AppendLine($"                  memory: {memory}");
        sb.AppendLine("                limits:");
        // Memory limit equals the request: the heap is sized from this number, and a node that can
        // burst past it gets OOM-killed by the kernel rather than throttled. CPU is deliberately
        // left without a limit — throttling a JVM mid-GC turns a slow query into a timing-out one,
        // and the request already guarantees the floor this tier was sized for.
        sb.AppendLine($"                  memory: {memory}");
        sb.AppendLine("      volumeClaimTemplates:");
        sb.AppendLine("        - metadata:");
        sb.AppendLine("            name: elasticsearch-data");
        sb.AppendLine("          spec:");
        sb.AppendLine("            accessModes: [\"ReadWriteOnce\"]");
        sb.AppendLine("            resources:");
        sb.AppendLine("              requests:");
        sb.AppendLine($"                storage: {storage}");
        if (!string.IsNullOrWhiteSpace(c.StorageClass))
            sb.AppendLine($"            storageClassName: {c.StorageClass}");
    }

    /// <summary>
    /// Renders the Kibana CR. Node's old-space is pinned below the container limit, because Kibana
    /// left to itself will grow its heap until the kernel takes the pod away mid-request.
    /// </summary>
    public static string BuildKibanaManifest(ElasticsearchCluster c)
    {
        int oldSpaceMb = (int)(ParseMemoryBytes(c.KibanaMemory) / (1024 * 1024) * 0.75);

        StringBuilder sb = new();
        sb.AppendLine($"apiVersion: {KibanaApiVersion}");
        sb.AppendLine("kind: Kibana");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.Name}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine($"  version: {c.Version}");
        sb.AppendLine($"  count: {c.KibanaCount}");
        sb.AppendLine("  elasticsearchRef:");
        sb.AppendLine($"    name: {c.Name}");
        sb.AppendLine("  podTemplate:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: kibana");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: NODE_OPTIONS");
        sb.AppendLine($"              value: \"--max-old-space-size={oldSpaceMb}\"");
        sb.AppendLine("          resources:");
        sb.AppendLine("            requests:");
        sb.AppendLine($"              cpu: {c.KibanaCpuRequest}");
        sb.AppendLine($"              memory: {c.KibanaMemory}");
        sb.AppendLine("            limits:");
        sb.AppendLine($"              memory: {c.KibanaMemory}");
        return sb.ToString();
    }

    /// <summary>The ILM policy body, exactly as Elasticsearch's <c>_ilm/policy</c> API takes it.</summary>
    public static string BuildIlmPolicyJson(ElasticsearchIlmPolicy p)
    {
        Dictionary<string, object> phases = new()
        {
            ["hot"] = new Dictionary<string, object>
            {
                ["actions"] = new Dictionary<string, object>
                {
                    ["rollover"] = new Dictionary<string, object>
                    {
                        ["max_primary_shard_size"] = $"{p.RolloverMaxPrimaryShardGb}gb",
                        ["max_age"] = $"{p.RolloverMaxAgeDays}d"
                    },
                    ["set_priority"] = new Dictionary<string, object> { ["priority"] = 100 }
                }
            }
        };

        if (p.WarmAfterDays is int warm)
        {
            phases["warm"] = new Dictionary<string, object>
            {
                ["min_age"] = $"{warm}d",
                ["actions"] = new Dictionary<string, object>
                {
                    // One segment per shard: warm data is read-only, and merging it down is what
                    // makes the tier cheaper to search than the hot one it came from.
                    ["forcemerge"] = new Dictionary<string, object> { ["max_num_segments"] = 1 },
                    ["set_priority"] = new Dictionary<string, object> { ["priority"] = 50 }
                }
            };
        }

        if (p.ColdAfterDays is int cold)
        {
            phases["cold"] = new Dictionary<string, object>
            {
                ["min_age"] = $"{cold}d",
                ["actions"] = new Dictionary<string, object>
                {
                    ["set_priority"] = new Dictionary<string, object> { ["priority"] = 0 }
                }
            };
        }

        if (p.DeleteAfterDays is int del)
        {
            phases["delete"] = new Dictionary<string, object>
            {
                ["min_age"] = $"{del}d",
                ["actions"] = new Dictionary<string, object>
                {
                    ["delete"] = new Dictionary<string, object>()
                }
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["policy"] = new Dictionary<string, object> { ["phases"] = phases }
        }, JsonOpts);
    }

    /// <summary>
    /// The composable index template that binds the policy to a pattern. Data tiers do their own
    /// routing — an index in the warm phase moves because its <c>_tier_preference</c> says so — so
    /// the template only has to name the policy, the shape and the pattern.
    /// </summary>
    public static string BuildIndexTemplateJson(ElasticsearchIlmPolicy p)
    {
        Dictionary<string, object> settings = new()
        {
            ["index.lifecycle.name"] = p.Name,
            ["index.number_of_shards"] = p.Shards,
            ["index.number_of_replicas"] = p.Replicas
        };

        Dictionary<string, object> template = new()
        {
            ["index_patterns"] = new[] { p.IndexPattern },
            ["priority"] = p.TemplatePriority,
            ["template"] = new Dictionary<string, object> { ["settings"] = settings }
        };

        if (p.UseDataStream)
            template["data_stream"] = new Dictionary<string, object>();
        else
            // A non-data-stream index needs an explicit rollover alias, or ILM rolls nothing over
            // and the policy silently parks at the hot phase forever.
            settings["index.lifecycle.rollover_alias"] = p.Name;

        return JsonSerializer.Serialize(template, JsonOpts);
    }

    /// <summary>
    /// The ConfigMap the apply Job reads: one JSON file per policy and per template, plus the
    /// script that PUTs them.
    /// </summary>
    public static string BuildIlmConfigMapManifest(
        ElasticsearchCluster c, IReadOnlyList<ElasticsearchIlmPolicy> policies)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {IlmConfigMapName(c)}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("data:");
        AppendBlockLiteral(sb, "apply.sh", BuildIlmApplyScript(c, policies));
        foreach (ElasticsearchIlmPolicy p in policies)
        {
            AppendBlockLiteral(sb, $"{p.Name}.policy.json", BuildIlmPolicyJson(p));
            AppendBlockLiteral(sb, $"{p.Name}.template.json", BuildIndexTemplateJson(p));
        }
        return sb.ToString();
    }

    /// <summary>A ConfigMap holding just a script — used by the delete path.</summary>
    public static string BuildScriptConfigMap(ElasticsearchCluster c, string name, string script)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("data:");
        AppendBlockLiteral(sb, "apply.sh", script);
        return sb.ToString();
    }

    /// <summary>
    /// The Job that talks to Elasticsearch. It runs the stack's own image (already pulled on these
    /// nodes, and it carries curl), authenticates as the operator-generated <c>elastic</c> user
    /// straight from the Secret, and verifies the HTTP layer against the operator's own CA — so no
    /// credential and no <c>-k</c> ever appears anywhere EntKube can see.
    /// </summary>
    /// <param name="userPasswordSecretName">
    /// A second Secret whose "password" key is handed to the script as <c>ES_USER_PASSWORD</c>. Used
    /// when the Job creates a user: the password reaches Elasticsearch without ever being in a
    /// command line, a manifest field or this process's database.
    /// </param>
    public static string BuildElasticsearchJobManifest(
        ElasticsearchCluster c, string jobName, string configMapName, string? userPasswordSecretName = null)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {jobName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine("  backoffLimit: 1");
        // Long enough that the record of a failure outlives the page that reported it; the result
        // is read back and stored before this expires either way.
        sb.AppendLine("  ttlSecondsAfterFinished: 3600");
        sb.AppendLine("  template:");
        sb.AppendLine("    spec:");
        sb.AppendLine("      restartPolicy: Never");
        sb.AppendLine("      securityContext:");
        sb.AppendLine("        runAsNonRoot: true");
        sb.AppendLine("        runAsUser: 1000");
        sb.AppendLine("        seccompProfile:");
        sb.AppendLine("          type: RuntimeDefault");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: scripts");
        sb.AppendLine("          configMap:");
        sb.AppendLine($"            name: {configMapName}");
        sb.AppendLine("            defaultMode: 0555");
        sb.AppendLine("        - name: es-ca");
        sb.AppendLine("          secret:");
        sb.AppendLine($"            secretName: {c.HttpCertsSecretName}");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: apply");
        sb.AppendLine($"          image: docker.elastic.co/elasticsearch/elasticsearch:{c.Version}");
        sb.AppendLine("          command: [\"/bin/bash\", \"/scripts/apply.sh\"]");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: ELASTIC_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {c.ElasticUserSecretName}");
        sb.AppendLine("                  key: elastic");
        if (userPasswordSecretName is not null)
        {
            sb.AppendLine("            - name: ES_USER_PASSWORD");
            sb.AppendLine("              valueFrom:");
            sb.AppendLine("                secretKeyRef:");
            sb.AppendLine($"                  name: {userPasswordSecretName}");
            sb.AppendLine("                  key: password");
        }
        sb.AppendLine("          resources:");
        sb.AppendLine("            requests:");
        sb.AppendLine("              cpu: 50m");
        sb.AppendLine("              memory: 128Mi");
        sb.AppendLine("            limits:");
        sb.AppendLine("              memory: 256Mi");
        sb.AppendLine("          securityContext:");
        sb.AppendLine("            allowPrivilegeEscalation: false");
        sb.AppendLine("            capabilities:");
        sb.AppendLine("              drop: [\"ALL\"]");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: scripts");
        sb.AppendLine("              mountPath: /scripts");
        sb.AppendLine("            - name: es-ca");
        sb.AppendLine("              mountPath: /es-ca");
        sb.AppendLine("              readOnly: true");
        return sb.ToString();
    }

    private static string BuildIlmApplyScript(
        ElasticsearchCluster c, IReadOnlyList<ElasticsearchIlmPolicy> policies)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        foreach (ElasticsearchIlmPolicy p in policies)
        {
            sb.AppendLine($"put \"/_ilm/policy/{p.Name}\" \"/scripts/{p.Name}.policy.json\"");
            sb.AppendLine($"put \"/_index_template/{p.Name}\" \"/scripts/{p.Name}.template.json\"");
        }
        sb.AppendLine();
        sb.AppendLine("echo \"all policies and templates applied\"");
        return sb.ToString();
    }

    /// <summary>
    /// The Secret ECK loads into every node's keystore. Its keys are the keystore entry names
    /// verbatim — Elasticsearch looks up <c>s3.client.default.access_key</c>, so that is what the
    /// key has to be called.
    /// </summary>
    public static string BuildKeystoreSecretManifest(
        ElasticsearchCluster c, (string AccessKey, string SecretKey) credentials)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.SnapshotCredentialsSecretName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        sb.AppendLine($"  s3.client.default.access_key: {Base64(credentials.AccessKey)}");
        sb.AppendLine($"  s3.client.default.secret_key: {Base64(credentials.SecretKey)}");
        return sb.ToString();
    }

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Registers the repository and the SLM policy that writes into it.
    ///
    /// <para>The repository PUT is retried: ECK propagates the keystore to the nodes shortly after
    /// the CR is applied, and a repository registered before it lands fails with an authentication
    /// error indistinguishable from wrong credentials.</para>
    /// </summary>
    public static string BuildSnapshotSetupScript(ElasticsearchCluster c, ElasticsearchS3Settings s3)
    {
        string repoBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "s3",
            ["settings"] = new Dictionary<string, object>
            {
                ["bucket"] = s3.Bucket,
                ["base_path"] = s3.BasePath,
                ["client"] = "default"
            }
        }, JsonOpts);

        string policyBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schedule"] = c.SnapshotScheduleCron,
            ["name"] = $"<{c.Name}-snap-{{now/d}}>",
            ["repository"] = c.SnapshotRepositoryName,
            ["config"] = new Dictionary<string, object>
            {
                ["indices"] = new[] { "*" },
                // The cluster state carries the index templates, the ILM policies and the roles.
                // A snapshot without it restores data nobody has told Elasticsearch what to do with.
                ["include_global_state"] = true
            },
            ["retention"] = new Dictionary<string, object>
            {
                ["expire_after"] = $"{c.SnapshotExpireAfterDays}d",
                ["min_count"] = c.SnapshotMinCount,
                ["max_count"] = c.SnapshotMaxCount
            }
        }, JsonOpts);

        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("cat > /tmp/repo.json <<'ENTKUBE_EOF'");
        sb.AppendLine(repoBody);
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine("cat > /tmp/policy.json <<'ENTKUBE_EOF'");
        sb.AppendLine(policyBody);
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine("# The keystore reaches the nodes a moment after the CR does, and a repository");
        sb.AppendLine("# registered one second early fails as if the credentials were wrong.");
        sb.AppendLine("registered=0");
        sb.AppendLine("for i in $(seq 1 10); do");
        sb.AppendLine($"  code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X PUT \"$ES/_snapshot/{c.SnapshotRepositoryName}?verify=true\" -d @/tmp/repo.json)");
        sb.AppendLine("  if [ \"$code\" -lt 300 ]; then registered=1; break; fi");
        sb.AppendLine("  echo \"repository not accepted yet (HTTP $code), retrying\"; cat /tmp/resp; echo");
        sb.AppendLine("  sleep 15");
        sb.AppendLine("done");
        sb.AppendLine("if [ \"$registered\" != \"1\" ]; then echo \"repository registration failed\"; cat /tmp/resp; exit 1; fi");
        sb.AppendLine("echo \"repository registered and verified\"");
        sb.AppendLine();
        sb.AppendLine($"put \"/_slm/policy/{c.SnapshotPolicyName}\" /tmp/policy.json");
        return sb.ToString();
    }

    /// <summary>Removes the SLM policy. The repository and its contents are deliberately left.</summary>
    public static string BuildSnapshotDisableScript(ElasticsearchCluster c)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X DELETE \"$ES/_slm/policy/{c.SnapshotPolicyName}\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ] && [ \"$code\" != \"404\" ]; then cat /tmp/resp; exit 1; fi");
        sb.AppendLine("echo \"snapshot policy removed (HTTP $code); the repository and its snapshots are untouched\"");
        return sb.ToString();
    }

    /// <summary>Asks SLM to run the policy now, off-schedule.</summary>
    public static string BuildSnapshotExecuteScript(ElasticsearchCluster c)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X POST \"$ES/_slm/policy/{c.SnapshotPolicyName}/_execute\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then cat /tmp/resp; exit 1; fi");
        sb.AppendLine("cat /tmp/resp; echo");
        return sb.ToString();
    }

    /// <summary>
    /// Prints the policy's own record of its last run, behind a marker so it can be found in the
    /// job's log whatever else the container said.
    /// </summary>
    public static string BuildSnapshotStatusScript(ElasticsearchCluster c)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' \"$ES/_slm/policy/{c.SnapshotPolicyName}\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then cat /tmp/resp; exit 1; fi");
        sb.AppendLine("echo \"---ENTKUBE-SLM---\"");
        sb.AppendLine("cat /tmp/resp");
        return sb.ToString();
    }

    /// <summary>The Secret holding one application user's password, in the Elasticsearch namespace.</summary>
    public static string BuildUserCredentialsSecret(
        ElasticsearchCluster c, ElasticsearchUser user, string password)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {user.CredentialsSecretName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        sb.AppendLine($"  password: {Base64(password)}");
        return sb.ToString();
    }

    /// <summary>
    /// The privileges each access level grants, on the user's own index pattern and nowhere else.
    /// </summary>
    public static IReadOnlyList<string> PrivilegesFor(ElasticsearchAccess access) => access switch
    {
        ElasticsearchAccess.Viewer => ["read", "view_index_metadata"],
        // auto_configure is what lets a writer add a field to a data stream's mapping; without it
        // the first document with a new field is rejected, which reads as a broken client.
        ElasticsearchAccess.Writer => ["read", "write", "view_index_metadata", "create_index", "auto_configure"],
        _ => ["all"]
    };

    /// <summary>
    /// Creates (or updates) the role and the user. Written so re-running it is harmless: both are
    /// PUTs, and the password is re-set to whatever the Secret currently holds — which is also how a
    /// user survives the cluster being rebuilt under it.
    /// </summary>
    public static string BuildUserApplyScript(ElasticsearchCluster c, ElasticsearchUser user)
    {
        string roleBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            // A "manager" may need to see whether the cluster is healthy; a writer does not.
            ["cluster"] = user.Access == ElasticsearchAccess.Manager ? new[] { "monitor" } : [],
            ["indices"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["names"] = new[] { user.IndexPattern },
                    ["privileges"] = PrivilegesFor(user.Access),
                    ["allow_restricted_indices"] = false
                }
            }
        }, JsonOpts);

        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("cat > /tmp/role.json <<'ENTKUBE_EOF'");
        sb.AppendLine(roleBody);
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine($"put \"/_security/role/{user.RoleName}\" /tmp/role.json");
        sb.AppendLine();
        sb.AppendLine("# Unquoted heredoc so the password expands — it is the only thing in here that does,");
        sb.AppendLine("# and it never reaches an argument list, a log line or a manifest EntKube keeps.");
        sb.AppendLine("umask 077");
        sb.AppendLine("cat > /tmp/user.json <<ENTKUBE_EOF");
        sb.AppendLine("{");
        sb.AppendLine("  \"password\": \"${ES_USER_PASSWORD}\",");
        sb.AppendLine($"  \"roles\": [\"{user.RoleName}\"],");
        sb.AppendLine($"  \"full_name\": \"EntKube application user ({user.Access})\"");
        sb.AppendLine("}");
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine($"put \"/_security/user/{user.Username}\" /tmp/user.json");
        sb.AppendLine("rm -f /tmp/user.json");
        return sb.ToString();
    }

    /// <summary>Removes the user, then the role it was the only holder of.</summary>
    public static string BuildUserDeleteScript(ElasticsearchCluster c, ElasticsearchUser user)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("del() {");
        sb.AppendLine("  path=\"$1\"");
        sb.AppendLine("  code=$($CURL -o /tmp/resp -w '%{http_code}' -X DELETE \"$ES$path\")");
        sb.AppendLine("  if [ \"$code\" -ge 300 ] && [ \"$code\" != \"404\" ]; then cat /tmp/resp; exit 1; fi");
        sb.AppendLine("  echo \"DELETE $path -> HTTP $code\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"del \"/_security/user/{user.Username}\"");
        sb.AppendLine($"del \"/_security/role/{user.RoleName}\"");
        return sb.ToString();
    }

    /// <summary>The Secret written into the application's own namespace.</summary>
    public static string BuildBindingSecretManifest(
        string secretName, string ns, IReadOnlyDictionary<string, string> data)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        foreach ((string key, string value) in data.OrderBy(kv => kv.Key))
            sb.AppendLine($"  {key}: {Base64(value)}");
        return sb.ToString();
    }

    /// <summary>Prints what the repository holds, behind a marker the caller looks for.</summary>
    public static string BuildSnapshotListScript(ElasticsearchCluster c)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' \"$ES/_snapshot/{c.SnapshotRepositoryName}/_all\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then cat /tmp/resp; exit 1; fi");
        sb.AppendLine("echo \"---ENTKUBE-SNAPSHOTS---\"");
        sb.AppendLine("cat /tmp/resp");
        return sb.ToString();
    }

    /// <summary>
    /// Restores a snapshot, either alongside the live data or over it.
    ///
    /// <para>The in-place path closes the matching indices first. That is not a nicety:
    /// Elasticsearch refuses to restore into an open index, and without the close the restore fails
    /// having done nothing — which reads as a broken backup rather than a busy index.</para>
    /// </summary>
    public static string BuildRestoreScript(
        ElasticsearchCluster c, string snapshotName, string indexPattern,
        RestoreMode mode, string renamePrefix, bool includeGlobalState)
    {
        Dictionary<string, object> body = new()
        {
            ["indices"] = indexPattern,
            ["include_global_state"] = includeGlobalState,
            // Aliases come back only on an in-place restore. Side by side they would collide with
            // the live aliases still pointing at the live indices, and Elasticsearch fails the whole
            // restore over it — so the safe mode would be the one that does not work.
            ["include_aliases"] = mode == RestoreMode.InPlace
        };

        if (mode == RestoreMode.SideBySide)
        {
            body["rename_pattern"] = "(.+)";
            body["rename_replacement"] = renamePrefix + "$1";
        }

        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("cat > /tmp/restore.json <<'ENTKUBE_EOF'");
        sb.AppendLine(JsonSerializer.Serialize(body, JsonOpts));
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();

        if (mode == RestoreMode.InPlace)
        {
            sb.AppendLine("# Elasticsearch will not restore into an open index. Closing first is what");
            sb.AppendLine("# makes this a restore rather than a failure; it reopens them itself afterwards.");
            sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X POST \"$ES/{indexPattern}/_close?ignore_unavailable=true\")");
            sb.AppendLine("if [ \"$code\" -ge 300 ]; then echo \"closing the target indices failed (HTTP $code)\"; cat /tmp/resp; echo; exit 1; fi");
            sb.AppendLine("echo \"target indices closed\"");
            sb.AppendLine();
        }

        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X POST \"$ES/_snapshot/{c.SnapshotRepositoryName}/{snapshotName}/_restore?wait_for_completion=true\" -d @/tmp/restore.json)");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then echo \"restore failed (HTTP $code)\"; cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine("cat /tmp/resp; echo");
        sb.AppendLine("echo \"restore finished\"");
        return sb.ToString();
    }

    /// <summary>
    /// The opening every admin script shares: where the cluster is, how to authenticate, waiting
    /// for it to answer, and a <c>put</c> that fails the job on an HTTP error — which plain curl
    /// does not, since a 400 with a body is a perfectly successful request as far as it is
    /// concerned.
    /// </summary>
    private static void AppendScriptPreamble(StringBuilder sb, ElasticsearchCluster c)
    {
        sb.AppendLine("set -eu");
        sb.AppendLine($"ES=\"{c.HttpEndpoint}\"");
        sb.AppendLine("CURL=\"curl -sS --cacert /es-ca/ca.crt -u elastic:${ELASTIC_PASSWORD} -H Content-Type:application/json\"");
        sb.AppendLine();
        sb.AppendLine("ready=0");
        sb.AppendLine("for i in $(seq 1 60); do");
        sb.AppendLine("  if $CURL -o /dev/null \"$ES/_cluster/health?wait_for_status=yellow&timeout=10s\"; then ready=1; break; fi");
        sb.AppendLine("  sleep 5");
        sb.AppendLine("done");
        sb.AppendLine("if [ \"$ready\" != \"1\" ]; then echo \"Elasticsearch did not become available within 5 minutes\"; exit 1; fi");
        sb.AppendLine();
        sb.AppendLine("put() {");
        sb.AppendLine("  path=\"$1\"; file=\"$2\"");
        sb.AppendLine("  code=$($CURL -o /tmp/resp -w '%{http_code}' -X PUT \"$ES$path\" -d @\"$file\")");
        sb.AppendLine("  if [ \"$code\" -ge 300 ]; then echo \"PUT $path -> HTTP $code\"; cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine("  echo \"PUT $path -> HTTP $code\"");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string BuildIlmDeleteScript(ElasticsearchCluster c, ElasticsearchIlmPolicy p)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("del() {");
        sb.AppendLine("  path=\"$1\"");
        sb.AppendLine("  code=$($CURL -o /tmp/resp -w '%{http_code}' -X DELETE \"$ES$path\")");
        sb.AppendLine("  # 404 is success here: the thing we were asked to remove is not there.");
        sb.AppendLine("  if [ \"$code\" -ge 300 ] && [ \"$code\" != \"404\" ]; then echo \"DELETE $path -> HTTP $code\"; cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine("  echo \"DELETE $path -> HTTP $code\"");
        sb.AppendLine("}");
        sb.AppendLine();
        // Template first: Elasticsearch refuses to delete a policy an index template still names.
        sb.AppendLine($"del \"/_index_template/{p.Name}\"");
        sb.AppendLine($"del \"/_ilm/policy/{p.Name}\"");
        return sb.ToString();
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses topologies that Elasticsearch will accept and then be unhappy about — an even
    /// number of masters, warm data with nowhere hot to age from, a tier with no memory.
    /// </summary>
    public static void ValidateTopology(ElasticsearchCluster c)
    {
        if (string.IsNullOrWhiteSpace(c.Name))
            throw new InvalidOperationException("The cluster needs a name.");

        if (c.Name.Length > 36)
            throw new InvalidOperationException(
                "The name must be 36 characters or fewer — ECK appends the nodeSet and ordinal to it, and Kubernetes caps the result at 63.");

        if (c.MasterCount < 1)
            throw new InvalidOperationException("A cluster needs at least one master-eligible node.");

        if (c.MasterCount > 1 && c.MasterCount % 2 == 0)
            throw new InvalidOperationException(
                $"{c.MasterCount} master nodes is an even quorum: it tolerates the same single failure {c.MasterCount - 1} would, "
                + "while costing a node. Use an odd number.");

        if (!c.HasDataTiers && c.MasterCount > 1)
            throw new InvalidOperationException(
                "With no data tier, the master nodes are the cluster's only nodes and hold all the data. Either add a hot tier, "
                + "or run exactly one all-roles node.");

        if (c.HasDataTiers && c.HotCount < 1)
            throw new InvalidOperationException(
                "A hot tier is required: it holds the write indices and the content indices Kibana's own state lives in. "
                + "Warm and cold tiers only ever receive data that was first written to hot.");

        if (c.WarmCount < 0 || c.ColdCount < 0 || c.IngestCount < 0 || c.KibanaCount < 0)
            throw new InvalidOperationException("Node counts cannot be negative.");

        if (c.KibanaEnabled && c.KibanaCount < 1)
            throw new InvalidOperationException("Kibana is enabled, so it needs at least one instance.");

        foreach ((string label, string value) in RequiredQuantities(c))
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"{label} is required.");
            if (ParseMemoryBytes(value) <= 0 && ParseCpuMilli(value) <= 0)
                throw new InvalidOperationException($"{label} is not a valid Kubernetes quantity: '{value}'.");
        }

        foreach ((string tier, string memory) in TierMemories(c))
        {
            long bytes = ParseMemoryBytes(memory);
            if (bytes < 1024L * 1024 * 1024)
                throw new InvalidOperationException(
                    $"The {tier} tier is given {memory}. An Elasticsearch node below 1Gi spends its life garbage collecting; "
                    + "2Gi is the smallest figure worth running.");
        }
    }

    private static void ValidatePolicy(ElasticsearchIlmPolicy p, ElasticsearchCluster cluster)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            throw new InvalidOperationException("The policy needs a name.");

        if (!p.Name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            throw new InvalidOperationException(
                "The policy name may only contain letters, digits, '-', '_' and '.' — it is also used as a file name inside the apply Job.");

        if (string.IsNullOrWhiteSpace(p.IndexPattern))
            throw new InvalidOperationException("The policy needs an index pattern, e.g. 'logs-app-*'.");

        if (p.Shards < 1 || p.Replicas < 0)
            throw new InvalidOperationException("Shards must be at least 1 and replicas cannot be negative.");

        if (p.RolloverMaxPrimaryShardGb < 1 || p.RolloverMaxAgeDays < 1)
            throw new InvalidOperationException("Rollover needs a positive size and age.");

        // Phases have to be in order, or Elasticsearch rejects the policy outright.
        if (p.ColdAfterDays is int cold && p.WarmAfterDays is int warm && cold <= warm)
            throw new InvalidOperationException("The cold phase must come after the warm phase.");

        int? last = p.ColdAfterDays ?? p.WarmAfterDays;
        if (p.DeleteAfterDays is int del && last is int l && del <= l)
            throw new InvalidOperationException("Deletion must come after the last tier the data moves to.");

        if (p.WarmAfterDays.HasValue && cluster.WarmCount == 0)
            throw new InvalidOperationException(
                "This policy ages data into a warm tier the cluster does not have. Add warm nodes first, or drop the warm phase — "
                + "otherwise the data stays where it is and the tier move is only ever attempted.");

        if (p.ColdAfterDays.HasValue && cluster.ColdCount == 0)
            throw new InvalidOperationException(
                "This policy ages data into a cold tier the cluster does not have. Add cold nodes first, or drop the cold phase.");

        if (p.Replicas > 0 && cluster.HotCount < 2)
            throw new InvalidOperationException(
                "A replica needs a second node to live on, and the hot tier has one. Either add a hot node or set replicas to 0 "
                + "(which leaves the data with no copy).");
    }

    private static string CapacityRefusal(ElasticsearchCapacityCheck check) =>
        string.Join("\n", check.Blocking)
        + "\n\nApply it anyway only if the cluster is about to grow — e.g. an autoscaler that has not added the nodes yet.";

    // ── Quantity arithmetic ────────────────────────────────────────────────────

    /// <summary>
    /// Parses a Kubernetes memory quantity ("2Gi", "512Mi", "1G", "1073741824") into bytes.
    /// Returns 0 for anything it cannot read, which the validator treats as invalid.
    /// </summary>
    public static long ParseMemoryBytes(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return 0;
        string q = quantity.Trim();

        (string Suffix, long Multiplier)[] suffixes =
        [
            ("Ki", 1024L), ("Mi", 1024L * 1024), ("Gi", 1024L * 1024 * 1024), ("Ti", 1024L * 1024 * 1024 * 1024),
            ("K", 1000L), ("M", 1000_000L), ("G", 1000_000_000L), ("T", 1000_000_000_000L)
        ];

        foreach ((string suffix, long multiplier) in suffixes)
        {
            if (q.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(q[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return (long)(v * multiplier);
        }

        return double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain)
            ? (long)plain
            : 0;
    }

    /// <summary>Parses a Kubernetes CPU quantity ("500m", "1", "1.5") into millicores.</summary>
    public static long ParseCpuMilli(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return 0;
        string q = quantity.Trim();

        if (q.EndsWith('m'))
            return double.TryParse(q[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double milli)
                ? (long)milli
                : 0;

        return double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double cores)
            ? (long)(cores * 1000)
            : 0;
    }

    /// <summary>
    /// The heap an Elasticsearch node with this much container memory should be started with: half,
    /// leaving the other half for Lucene's file cache, and never above the ~31Gi point where the JVM
    /// drops compressed object pointers and a bigger heap starts addressing less.
    /// </summary>
    public static int HeapMbFor(string? memory)
    {
        long bytes = ParseMemoryBytes(memory);
        long mb = bytes / (1024 * 1024) / 2;
        return (int)Math.Clamp(mb, MinHeapMb, MaxHeapMb);
    }

    public static string FormatBytes(long bytes)
    {
        double gib = bytes / 1024d / 1024 / 1024;
        if (Math.Abs(gib) >= 1) return $"{gib:0.#}Gi";
        double mib = bytes / 1024d / 1024;
        return $"{mib:0}Mi";
    }

    public static string FormatCpu(long milli) =>
        milli >= 1000 ? $"{milli / 1000d:0.##} CPU" : $"{milli}m";

    // ── Kubernetes JSON parsing ────────────────────────────────────────────────

    /// <summary>Allocatable CPU (millicores) and memory (bytes) per node, schedulable nodes only.</summary>
    public static Dictionary<string, (long Cpu, long Mem)> ParseNodeAllocatable(string json)
    {
        Dictionary<string, (long, long)> result = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return result;

            foreach (JsonElement node in items.EnumerateArray())
            {
                string name = node.GetProperty("metadata").GetProperty("name").GetString() ?? "";

                // A cordoned node cannot take new pods, so counting its free memory would promise
                // room that nothing can be scheduled into.
                if (node.TryGetProperty("spec", out JsonElement spec)
                    && spec.TryGetProperty("unschedulable", out JsonElement unsched)
                    && unsched.ValueKind == JsonValueKind.True)
                    continue;

                if (!node.TryGetProperty("status", out JsonElement status)
                    || !status.TryGetProperty("allocatable", out JsonElement alloc))
                    continue;

                long cpu = alloc.TryGetProperty("cpu", out JsonElement cpuEl)
                    ? ParseCpuMilli(cpuEl.GetString()) : 0;
                long mem = alloc.TryGetProperty("memory", out JsonElement memEl)
                    ? ParseMemoryBytes(memEl.GetString()) : 0;

                if (name.Length > 0) result[name] = (cpu, mem);
            }
        }
        catch { /* an unreadable node list is reported as "capacity unknown" by the caller */ }
        return result;
    }

    /// <summary>Summed pod resource requests per node. Finished pods hold nothing and are skipped.</summary>
    public static Dictionary<string, (long Cpu, long Mem)> ParsePodRequestsByNode(string json)
    {
        Dictionary<string, (long Cpu, long Mem)> result = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return result;

            foreach (JsonElement pod in items.EnumerateArray())
            {
                if (!pod.TryGetProperty("spec", out JsonElement spec)
                    || !spec.TryGetProperty("nodeName", out JsonElement nodeEl))
                    continue;

                string node = nodeEl.GetString() ?? "";
                if (node.Length == 0) continue;

                if (pod.TryGetProperty("status", out JsonElement status)
                    && status.TryGetProperty("phase", out JsonElement phase)
                    && phase.GetString() is "Succeeded" or "Failed")
                    continue;

                long cpu = 0, mem = 0;
                foreach (string kind in new[] { "initContainers", "containers" })
                {
                    if (!spec.TryGetProperty(kind, out JsonElement containers)) continue;
                    foreach (JsonElement container in containers.EnumerateArray())
                    {
                        if (!container.TryGetProperty("resources", out JsonElement res)
                            || !res.TryGetProperty("requests", out JsonElement req))
                            continue;

                        long c = req.TryGetProperty("cpu", out JsonElement cEl) ? ParseCpuMilli(cEl.GetString()) : 0;
                        long m = req.TryGetProperty("memory", out JsonElement mEl) ? ParseMemoryBytes(mEl.GetString()) : 0;

                        // Init containers run one at a time and before the others, so their hold is
                        // the largest of them, not their sum.
                        if (kind == "initContainers") { cpu = Math.Max(cpu, c); mem = Math.Max(mem, m); }
                        else { cpu += c; mem += m; }
                    }
                }

                result.TryGetValue(node, out (long Cpu, long Mem) sofar);
                result[node] = (sofar.Cpu + cpu, sofar.Mem + mem);
            }
        }
        catch { /* same as nodes: unreadable means unknown, not zero */ }
        return result;
    }

    public static (string? Health, string? Phase, int AvailableNodes) ParseElasticsearchStatus(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out JsonElement status))
                return (null, null, 0);

            string? health = status.TryGetProperty("health", out JsonElement h) ? h.GetString() : null;
            string? phase = status.TryGetProperty("phase", out JsonElement p) ? p.GetString() : null;
            int available = status.TryGetProperty("availableNodes", out JsonElement a) && a.TryGetInt32(out int n) ? n : 0;
            return (health, phase, available);
        }
        catch { return (null, null, 0); }
    }

    public static string? ParseKibanaHealth(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out JsonElement status)
                && status.TryGetProperty("health", out JsonElement h)
                ? h.GetString()
                : null;
        }
        catch { return null; }
    }

    public static List<ElasticsearchPodInfo> ParsePodList(string json, string? forcedTier = null)
    {
        List<ElasticsearchPodInfo> pods = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return pods;

            foreach (JsonElement item in items.EnumerateArray())
            {
                JsonElement metadata = item.GetProperty("metadata");
                string name = metadata.GetProperty("name").GetString() ?? "";

                string tier = forcedTier ?? "";
                if (forcedTier is null
                    && metadata.TryGetProperty("labels", out JsonElement labels)
                    && labels.TryGetProperty("elasticsearch.k8s.elastic.co/statefulset-name", out JsonElement sts))
                {
                    // ECK names the StatefulSet "{cluster}-es-{nodeSet}", so the tier is its tail.
                    string stsName = sts.GetString() ?? "";
                    int marker = stsName.LastIndexOf("-es-", StringComparison.Ordinal);
                    tier = marker >= 0 ? stsName[(marker + 4)..] : stsName;
                }

                string podStatus = "Unknown";
                bool ready = false;
                string? node = null;
                int restarts = 0;

                if (item.TryGetProperty("spec", out JsonElement spec)
                    && spec.TryGetProperty("nodeName", out JsonElement nodeName))
                    node = nodeName.GetString();

                if (item.TryGetProperty("status", out JsonElement statusEl))
                {
                    if (statusEl.TryGetProperty("phase", out JsonElement phaseEl))
                        podStatus = phaseEl.GetString() ?? "Unknown";

                    if (statusEl.TryGetProperty("containerStatuses", out JsonElement containers))
                    {
                        ready = true;
                        foreach (JsonElement container in containers.EnumerateArray())
                        {
                            if (container.TryGetProperty("restartCount", out JsonElement rc))
                                restarts += rc.GetInt32();
                            if (container.TryGetProperty("ready", out JsonElement readyEl))
                                ready = ready && readyEl.GetBoolean();
                        }
                    }
                }

                pods.Add(new ElasticsearchPodInfo
                {
                    Name = name, Tier = tier, Status = podStatus,
                    Ready = ready, Node = node, Restarts = restarts
                });
            }
        }
        catch { }
        return [.. pods.OrderBy(p => p.Tier).ThenBy(p => p.Name)];
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Relaxed escaping so the snapshot name template reads as "<name-{now/d}>" in the file the Job
    // sends, rather than as \u003C escapes. Elasticsearch parses both; only one is debuggable, and
    // these documents are written into a quoted heredoc, never into HTML.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string IlmConfigMapName(ElasticsearchCluster c, string kind = "apply") =>
        $"{c.Name}-entkube-ilm-{kind}";

    public static string SnapshotConfigMapName(ElasticsearchCluster c, string kind = "setup") =>
        $"{c.Name}-entkube-snapshot-{kind}";

    public static string UserConfigMapName(ElasticsearchCluster c, ElasticsearchUser user, string kind = "apply") =>
        $"{c.Name}-entkube-user-{user.Username}-{kind}";

    /// <summary>
    /// The objects EntKube created beside the CRs, which ECK does not own and so will not collect.
    /// Every one of them is optional — a cluster that never had policies or snapshots has none —
    /// so deleting them is best-effort.
    /// </summary>
    private static IEnumerable<(string Kind, string Name)> Leftovers(ElasticsearchCluster c)
    {
        yield return ("configmap", IlmConfigMapName(c));
        yield return ("configmap", IlmConfigMapName(c, "delete"));
        yield return ("configmap", SnapshotConfigMapName(c));
        yield return ("configmap", SnapshotConfigMapName(c, "disable"));
        yield return ("configmap", SnapshotConfigMapName(c, "execute"));
        yield return ("configmap", SnapshotConfigMapName(c, "status"));
        yield return ("configmap", SnapshotConfigMapName(c, "list"));
        yield return ("configmap", SnapshotConfigMapName(c, "restore"));
        yield return ("secret", c.SnapshotCredentialsSecretName);
    }

    /// <summary>The password Secret and scripts of each application user on this cluster.</summary>
    private static IEnumerable<(string Kind, string Name)> UserLeftovers(ElasticsearchCluster c)
    {
        foreach (ElasticsearchUser user in c.Users)
        {
            yield return ("secret", user.CredentialsSecretName);
            yield return ("configmap", UserConfigMapName(c, user));
            yield return ("configmap", UserConfigMapName(c, user, "delete"));
        }
    }

    private static string JobName(ElasticsearchCluster c, string kind) =>
        $"{c.Name}-{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

    /// <summary>
    /// Waits for a Job to finish, and returns its logs either way. Bounded: a Job that has not
    /// finished in two minutes has found something wrong, and the caller says so rather than
    /// holding a page open.
    /// </summary>
    private async Task<(JobOutcome Outcome, string Log)> WaitForJobAsync(
        ElasticsearchCluster cluster, string jobName, string kubeconfig, CancellationToken ct)
    {
        for (int attempt = 0; attempt < JobPollAttempts; attempt++)
        {
            await Task.Delay(JobPollInterval, ct);

            string json;
            try
            {
                json = await k8s.GetJsonAsync($"job/{jobName}", cluster.Namespace, kubeconfig, ct: ct);
            }
            catch { continue; }

            (bool finished, bool succeeded) = ParseJobCompletion(json);
            if (!finished) continue;

            string log = "";
            try
            {
                log = await k8s.GetPodLogsAsync($"job/{jobName}", cluster.Namespace, kubeconfig, 100, ct);
            }
            catch { /* a Job whose pod is already gone still reported its result above */ }

            return (succeeded ? JobOutcome.Succeeded : JobOutcome.Failed, log);
        }

        return (JobOutcome.StillRunning, "");
    }

    public static (bool Finished, bool Succeeded) ParseJobCompletion(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out JsonElement status)) return (false, false);

            if (status.TryGetProperty("succeeded", out JsonElement s) && s.TryGetInt32(out int ok) && ok > 0)
                return (true, true);
            if (status.TryGetProperty("failed", out JsonElement f) && f.TryGetInt32(out int bad) && bad > 0)
                return (true, false);
        }
        catch { }
        return (false, false);
    }

    /// <summary>Writes a multi-line value as a YAML block literal under a ConfigMap key.</summary>
    private static void AppendBlockLiteral(StringBuilder sb, string key, string value)
    {
        sb.AppendLine($"  {key}: |");
        foreach (string line in value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            sb.AppendLine($"    {line}");
    }

    private static IEnumerable<(string Label, string Value)> RequiredQuantities(ElasticsearchCluster c)
    {
        yield return ("Master CPU", c.MasterCpuRequest);
        yield return ("Master memory", c.MasterMemory);
        yield return ("Master storage", c.MasterStorageSize);
        if (c.HotCount > 0)
        {
            yield return ("Hot CPU", c.HotCpuRequest);
            yield return ("Hot memory", c.HotMemory);
            yield return ("Hot storage", c.HotStorageSize);
        }
        if (c.WarmCount > 0)
        {
            yield return ("Warm CPU", c.WarmCpuRequest);
            yield return ("Warm memory", c.WarmMemory);
            yield return ("Warm storage", c.WarmStorageSize);
        }
        if (c.ColdCount > 0)
        {
            yield return ("Cold CPU", c.ColdCpuRequest);
            yield return ("Cold memory", c.ColdMemory);
            yield return ("Cold storage", c.ColdStorageSize);
        }
        if (c.IngestCount > 0)
        {
            yield return ("Ingest CPU", c.IngestCpuRequest);
            yield return ("Ingest memory", c.IngestMemory);
            yield return ("Ingest storage", c.IngestStorageSize);
        }
        if (c.KibanaEnabled)
        {
            yield return ("Kibana CPU", c.KibanaCpuRequest);
            yield return ("Kibana memory", c.KibanaMemory);
        }
    }

    private static IEnumerable<(string Tier, string Memory)> TierMemories(ElasticsearchCluster c)
    {
        yield return (c.HasDataTiers ? "master" : "single-node", c.MasterMemory);
        if (c.HotCount > 0) yield return ("hot", c.HotMemory);
        if (c.WarmCount > 0) yield return ("warm", c.WarmMemory);
        if (c.ColdCount > 0) yield return ("cold", c.ColdMemory);
        if (c.IngestCount > 0) yield return ("ingest", c.IngestMemory);
    }

    private static ElasticsearchCluster Clone(ElasticsearchCluster c)
    {
        ElasticsearchCluster copy = new() { Name = c.Name, Namespace = c.Namespace };
        CopyTopology(from: c, to: copy);
        return copy;
    }

    private static void CopyTopology(ElasticsearchCluster from, ElasticsearchCluster to)
    {
        to.Version = from.Version;
        to.MasterCount = from.MasterCount;
        to.MasterCpuRequest = from.MasterCpuRequest;
        to.MasterMemory = from.MasterMemory;
        to.MasterStorageSize = from.MasterStorageSize;
        to.HotCount = from.HotCount;
        to.HotCpuRequest = from.HotCpuRequest;
        to.HotMemory = from.HotMemory;
        to.HotStorageSize = from.HotStorageSize;
        to.WarmCount = from.WarmCount;
        to.WarmCpuRequest = from.WarmCpuRequest;
        to.WarmMemory = from.WarmMemory;
        to.WarmStorageSize = from.WarmStorageSize;
        to.ColdCount = from.ColdCount;
        to.ColdCpuRequest = from.ColdCpuRequest;
        to.ColdMemory = from.ColdMemory;
        to.ColdStorageSize = from.ColdStorageSize;
        to.IngestCount = from.IngestCount;
        to.IngestCpuRequest = from.IngestCpuRequest;
        to.IngestMemory = from.IngestMemory;
        to.IngestStorageSize = from.IngestStorageSize;
        to.StorageClass = from.StorageClass;
        to.AllowMmap = from.AllowMmap;
        to.ZoneAware = from.ZoneAware;
        to.KibanaEnabled = from.KibanaEnabled;
        to.KibanaCount = from.KibanaCount;
        to.KibanaCpuRequest = from.KibanaCpuRequest;
        to.KibanaMemory = from.KibanaMemory;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
