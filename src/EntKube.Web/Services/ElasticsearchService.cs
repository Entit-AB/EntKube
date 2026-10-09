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

/// <summary>One remote cluster as the Elasticsearch CR names it.</summary>
/// <param name="Alias">What a query calls it, as in <c>{alias}:logs-*</c>.</param>
/// <param name="Name">The remote Elasticsearch resource's name.</param>
/// <param name="Namespace">Its namespace.</param>
/// <param name="SearchPatterns">The index patterns the API key grants search on.</param>
public sealed record ElasticsearchRemoteRef(
    string Alias, string Name, string Namespace, IReadOnlyList<string> SearchPatterns);

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
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    VaultService vaultService,
    AuditService auditService,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    ILogger<ElasticsearchService> logger)
{
    /// <summary>
    /// A client for one of the tenant's clusters, or a refusal that says why. Keeps the
    /// cluster credential inside Fleet instead of handing it to every call here — see
    /// docs/decomposition.md §4.0.1.
    /// </summary>
    private async Task<Clusters.IClusterClient> ClusterFor(
        Guid tenantId, Guid clusterId, CancellationToken ct)
        => await clusterAccess.ForAsync(tenantId, clusterId, ct)
           ?? throw new InvalidOperationException(
               "The cluster has no stored kubeconfig, or does not belong to this tenant, "
               + "so nothing can be applied to it.");

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

        // Asked of Catalog rather than of its table. The predicate is unchanged and
        // applied here: these alias lists are not symmetrical across Name,
        // ReleaseName and HelmChartName, so a contract method matching every key
        // against every column would answer about installations these never accepted.
        EntKube.Contracts.Catalog.InstalledComponent? op =
            (await catalog.GetComponentsForTenantAsync(tenantId, ct))
            .FirstOrDefault(c => c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed
                && (c.Name == "eck-operator"
                    || c.ReleaseName == "elastic-operator"
                    || (c.HelmChartName ?? "") == "eck-operator"));

        return new ElasticsearchOperatorStatus
        {
            OperatorAvailable = op is not null,
            // The contract carries the cluster's name inline, which is why it does: of the
            // cross-module reads of this table, 56 were .Include(c => c.Cluster) purely to get it.
            OperatorClusterName = op?.ClusterName
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

        Clusters.IClusterClient? reachable = k8sCluster is null
            ? null
            : await clusterAccess.ForAsync(k8sCluster.TenantId, k8sCluster.Id, ct);

        if (reachable is null)
        {
            check.Warnings.Add("The Kubernetes cluster has no kubeconfig, so its free capacity could not be read. The topology below is what will be requested.");
            return check;
        }

        try
        {
            string nodesJson = await (await ClusterFor(k8sCluster.TenantId, k8sCluster.Id, ct)).GetJsonAllNamespacesAsync("nodes", ct: ct);
            string podsJson = await (await ClusterFor(k8sCluster.TenantId, k8sCluster.Id, ct)).GetJsonAllNamespacesAsync("pods", ct: ct);

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
            Clusters.IClusterClient client =
                await ClusterFor(k8sCluster.TenantId, k8sCluster.Id, ct);

            await client.EnsureNamespaceAsync(cluster.Namespace, ct);
            await ApplyClusterAsync(db, cluster, client, ct);
            if (cluster.KibanaEnabled)
                await client.ApplyManifestAsync(BuildKibanaManifest(cluster), ct);
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
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
            await ApplyClusterAsync(db, cluster, client, ct);

            if (cluster.KibanaEnabled)
                await client.ApplyManifestAsync(BuildKibanaManifest(cluster), ct);
            else if (before.KibanaEnabled)
                await client.DeleteManifestAsync("kibana", cluster.Name, cluster.Namespace, ct);

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
                await (await ClusterFor(cluster.TenantId, binding.AppDeployment.ClusterId, ct)).DeleteManifestAsync("secret", binding.KubernetesSecretName,
                    binding.AppDeployment.Namespace, ct);
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
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
            if (cluster.KibanaEnabled)
                await client.DeleteManifestAsync("kibana", cluster.Name, cluster.Namespace, ct);
            await client.DeleteManifestAsync("elasticsearch", cluster.Name, cluster.Namespace, ct);

            // The lifecycle ConfigMaps only exist if policies were ever applied, so their absence
            // is normal and must not fail a delete that has already removed the cluster itself.
            foreach ((string kindLabel, string name) in Leftovers(cluster).Concat(UserLeftovers(cluster)))
            {
                try
                {
                    await client.DeleteManifestAsync(kindLabel, name, cluster.Namespace, ct);
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
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);

            string crJson = await client.GetJsonAsync(
                $"elasticsearch.elasticsearch.k8s.elastic.co/{cluster.Name}", cluster.Namespace, ct: ct);

            (string? health, string? phase, int available) = ParseElasticsearchStatus(crJson);
            detail.Health = health;
            detail.AvailableNodes = available;
            detail.Ready = string.Equals(phase, "Ready", StringComparison.OrdinalIgnoreCase);

            string podsJson = await client.GetJsonAsync(
                "pods", cluster.Namespace,
                $"elasticsearch.k8s.elastic.co/cluster-name={cluster.Name}", ct);
            detail.Pods = ParsePodList(podsJson);

            if (cluster.KibanaEnabled)
            {
                try
                {
                    string kbJson = await client.GetJsonAsync(
                        $"kibana.kibana.k8s.elastic.co/{cluster.Name}", cluster.Namespace, ct: ct);
                    detail.KibanaHealth = ParseKibanaHealth(kbJson);

                    string kbPodsJson = await client.GetJsonAsync(
                        "pods", cluster.Namespace,
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
            Clusters.IClusterClient? reachable =
                await clusterAccess.ForAsync(cluster.TenantId, cluster.KubernetesClusterId, ct);

            if (reachable is null) continue;

            bool ready = false;
            try
            {
                string json = await (await ClusterFor(cluster.TenantId, cluster.KubernetesClusterId, ct)).GetJsonAsync(
                    $"elasticsearch.elasticsearch.k8s.elastic.co/{cluster.Name}", cluster.Namespace,
                     ct: ct);
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

        return await (await ClusterFor(cluster.TenantId, cluster.KubernetesClusterId, ct)).GetSecretValueAsync(
            cluster.ElasticUserSecretName, "elastic", cluster.Namespace,
             ct);
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
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
            string jobName = JobName(cluster, "ilm-delete");
            string script = BuildIlmDeleteScript(cluster, policy);

            await client.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, IlmConfigMapName(cluster, "delete"), script), ct);
            await client.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, IlmConfigMapName(cluster, "delete")), ct);
            await WaitForJobAsync(cluster, jobName, client, ct);
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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = IlmConfigMapName(cluster);
        string jobName = JobName(cluster, "ilm-apply");

        JobOutcome outcome;
        string log;

        try
        {
            await client.ApplyManifestAsync(BuildIlmConfigMapManifest(cluster, policies), ct);
            await client.ApplyManifestAsync(BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);
            (outcome, log) = await WaitForJobAsync(cluster, jobName, client, ct);
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

    // ── Kibana data views ──────────────────────────────────────────────────────

    public async Task<List<ElasticsearchDataView>> GetDataViewsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchDataViews
            .Where(v => v.TenantId == tenantId && v.ElasticsearchClusterId == clusterId)
            .OrderBy(v => v.SpaceId).ThenBy(v => v.Title)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a data view, so an account with a Kibana login and an index pattern has something to
    /// open rather than an empty Discover.
    /// </summary>
    public async Task<ElasticsearchDataView> CreateDataViewAsync(
        Guid tenantId, Guid clusterId, string? spaceId, string title, string? name, string timeFieldName,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (!cluster.KibanaEnabled)
            throw new InvalidOperationException("This cluster has no Kibana, so a data view has nothing to appear in.");

        title = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("A data view needs an index pattern, e.g. \"logs-orders-*\".");

        spaceId = string.IsNullOrWhiteSpace(spaceId) ? null : spaceId.Trim();

        if (spaceId is not null && !await db.ElasticsearchKibanaSpaces.AnyAsync(
                sp => sp.ElasticsearchClusterId == clusterId && sp.SpaceId == spaceId, ct))
            throw new InvalidOperationException(
                $"There is no '{spaceId}' space on this cluster. Create the space first — a data view in a space that "
                + "does not exist is a saved object nobody can reach.");

        if (await db.ElasticsearchDataViews.AnyAsync(
                v => v.ElasticsearchClusterId == clusterId && v.SpaceId == spaceId && v.Title == title, ct))
            throw new InvalidOperationException(
                $"'{title}' already has a data view in {(spaceId is null ? "the default space" : $"the '{spaceId}' space")}.");

        ElasticsearchDataView view = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ElasticsearchClusterId = clusterId,
            SpaceId = spaceId,
            Title = title,
            Name = string.IsNullOrWhiteSpace(name) ? title : name!.Trim(),
            TimeFieldName = (timeFieldName ?? "").Trim()
        };

        db.ElasticsearchDataViews.Add(view);
        await db.SaveChangesAsync(ct);

        await ApplyDataViewInternalAsync(db, cluster, view, ct);
        return view;
    }

    /// <summary>Re-applies a data view, e.g. after the space or the cluster was rebuilt.</summary>
    public async Task ApplyDataViewAsync(Guid tenantId, Guid dataViewId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchDataView view = await db.ElasticsearchDataViews
            .Include(v => v.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(v => v.Id == dataViewId && v.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Data view not found.");

        await ApplyDataViewInternalAsync(db, view.ElasticsearchCluster, view, ct);
    }

    public async Task DeleteDataViewAsync(Guid tenantId, Guid dataViewId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchDataView view = await db.ElasticsearchDataViews
            .Include(v => v.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(v => v.Id == dataViewId && v.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Data view not found.");

        ElasticsearchCluster cluster = view.ElasticsearchCluster;

        try
        {
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
            string configMap = SnapshotConfigMapName(cluster, $"dataview-del-{view.Id:N}");
            string jobName = JobName(cluster, $"dataview-del-{view.Id:N}"[..Math.Min(40, $"dataview-del-{view.Id:N}".Length)]);

            await client.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildDataViewDeleteScript(cluster, view)), ct);
            await client.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);
            await WaitForJobAsync(cluster, jobName, client, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing data view {Title} failed", view.Title);
        }

        db.ElasticsearchDataViews.Remove(view);
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyDataViewInternalAsync(
        ApplicationDbContext db, ElasticsearchCluster cluster, ElasticsearchDataView view, CancellationToken ct)
    {
        Clusters.IClusterClient client = await ClusterFor(cluster.TenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, $"dataview-{view.Id:N}");
        string jobName = JobName(cluster, "dataview");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildDataViewApplyScript(cluster, view)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        view.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) view.LastAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException("Kibana rejected the data view:\n" + Truncate(log, 1200));
        if (outcome == JobOutcome.StillRunning)
            throw new InvalidOperationException(
                "Kibana did not answer in time — it starts well after Elasticsearch does. The data view is recorded "
                + "here and can be re-applied once it is up.");
    }

    // ── Ingest pipelines ───────────────────────────────────────────────────────

    public async Task<List<ElasticsearchIngestPipeline>> GetPipelinesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchIngestPipelines
            .Where(p => p.TenantId == tenantId && p.ElasticsearchClusterId == clusterId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<ElasticsearchIngestPipeline> SavePipelineAsync(
        ElasticsearchIngestPipeline pipeline, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == pipeline.ElasticsearchClusterId && c.TenantId == pipeline.TenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        ValidatePipeline(pipeline);

        ElasticsearchIngestPipeline? existing = pipeline.Id == Guid.Empty
            ? null
            : await db.ElasticsearchIngestPipelines.FirstOrDefaultAsync(p => p.Id == pipeline.Id, ct);

        if (existing is null)
        {
            if (await db.ElasticsearchIngestPipelines.AnyAsync(
                    p => p.ElasticsearchClusterId == pipeline.ElasticsearchClusterId && p.Name == pipeline.Name, ct))
                throw new InvalidOperationException($"A pipeline named '{pipeline.Name}' already exists on this cluster.");

            pipeline.Id = Guid.NewGuid();
            pipeline.CreatedAt = DateTime.UtcNow;
            db.ElasticsearchIngestPipelines.Add(pipeline);
            existing = pipeline;
        }
        else
        {
            existing.Description = pipeline.Description;
            existing.TimestampField = pipeline.TimestampField;
            existing.TimestampFormats = pipeline.TimestampFormats;
            existing.GrokField = pipeline.GrokField;
            existing.GrokPattern = pipeline.GrokPattern;
            existing.RenameFields = pipeline.RenameFields;
            existing.RemoveFields = pipeline.RemoveFields;
            existing.SetFields = pipeline.SetFields;
            existing.CustomProcessorsJson = pipeline.CustomProcessorsJson;
        }

        await db.SaveChangesAsync(ct);

        Clusters.IClusterClient client = await ClusterFor(cluster.TenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, $"pipeline-{existing.Name}");
        string jobName = JobName(cluster, $"pipeline-{existing.Name}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildPipelineApplyScript(cluster, existing)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        existing.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) existing.LastAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException(
                "Elasticsearch rejected the pipeline:\n" + Truncate(log, 1200));

        return existing;
    }

    /// <summary>
    /// Removes a pipeline. Refused while an index template still names it: the template would keep
    /// pointing at a pipeline that is gone, and every write through it would fail — not quietly, but
    /// not anywhere near this screen either.
    /// </summary>
    public async Task DeletePipelineAsync(Guid tenantId, Guid pipelineId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchIngestPipeline pipeline = await db.ElasticsearchIngestPipelines
            .Include(p => p.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(p => p.Id == pipelineId && p.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Pipeline not found.");

        List<string> users = await db.ElasticsearchIlmPolicies
            .Where(p => p.ElasticsearchClusterId == pipeline.ElasticsearchClusterId
                && p.DefaultPipelineName == pipeline.Name)
            .Select(p => p.Name)
            .ToListAsync(ct);

        if (users.Count > 0)
            throw new InvalidOperationException(
                $"The index template(s) {string.Join(", ", users)} still send documents through '{pipeline.Name}'. "
                + "Point them elsewhere first — a template naming a pipeline that does not exist fails every write.");

        ElasticsearchCluster cluster = pipeline.ElasticsearchCluster;

        try
        {
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
            string configMap = SnapshotConfigMapName(cluster, $"pipeline-del-{pipeline.Name}");
            string jobName = JobName(cluster, $"pipeline-del-{pipeline.Name}");

            await client.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildPipelineDeleteScript(cluster, pipeline)), ct);
            await client.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);
            await WaitForJobAsync(cluster, jobName, client, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing ingest pipeline {Pipeline} failed", pipeline.Name);
        }

        db.ElasticsearchIngestPipelines.Remove(pipeline);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs one document through a pipeline without indexing it, and returns what came out.
    ///
    /// <para>This is the difference between a pipeline somebody believes works and one they have
    /// seen work. A grok pattern that does not match produces no error at index time — it produces
    /// documents missing the fields everything downstream was written against.</para>
    /// </summary>
    public async Task<string> SimulatePipelineAsync(
        Guid tenantId, Guid pipelineId, string sampleDocumentJson, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchIngestPipeline pipeline = await db.ElasticsearchIngestPipelines
            .Include(p => p.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(p => p.Id == pipelineId && p.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Pipeline not found.");

        if (string.IsNullOrWhiteSpace(sampleDocumentJson))
            throw new InvalidOperationException("Paste a sample document to run through it.");

        try
        {
            using JsonDocument _ = JsonDocument.Parse(sampleDocumentJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"That is not valid JSON: {ex.Message}");
        }

        ElasticsearchCluster cluster = pipeline.ElasticsearchCluster;
        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, $"pipeline-sim-{pipeline.Name}");
        string jobName = JobName(cluster, $"pipeline-sim-{pipeline.Name}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap,
                BuildPipelineSimulateScript(cluster, pipeline, sampleDocumentJson)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        if (outcome != JobOutcome.Succeeded)
            throw new InvalidOperationException("The simulation failed:\n" + Truncate(log, 1200));

        const string marker = "---ENTKUBE-SIMULATE---";
        int at = log.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? log : log[(at + marker.Length)..].Trim();
    }

    private static void ValidatePipeline(ElasticsearchIngestPipeline p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            throw new InvalidOperationException("The pipeline needs a name — index templates point at it by name.");

        if (!p.Name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            throw new InvalidOperationException(
                "A pipeline name may only contain letters, digits, '-', '_' and '.' — it is also a file name inside the Job.");

        if (!string.IsNullOrWhiteSpace(p.GrokPattern) && string.IsNullOrWhiteSpace(p.GrokField))
            throw new InvalidOperationException("A grok pattern needs a field to read from, usually 'message'.");

        if (!string.IsNullOrWhiteSpace(p.TimestampField) && string.IsNullOrWhiteSpace(p.TimestampFormats))
            throw new InvalidOperationException("A timestamp field needs at least one format to parse it with.");

        foreach (string pair in Split(p.RenameFields))
        {
            if (pair.Split(':').Length != 2)
                throw new InvalidOperationException(
                    $"'{pair}' is not a rename — write them as \"from:to\", separated by commas.");
        }

        foreach (string pair in Split(p.SetFields))
        {
            if (pair.Split('=').Length != 2)
                throw new InvalidOperationException(
                    $"'{pair}' is not a field to set — write them as \"key=value\", separated by commas.");
        }

        if (!string.IsNullOrWhiteSpace(p.CustomProcessorsJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(p.CustomProcessorsJson!);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException(
                        "The custom processors must be a JSON array, e.g. [{\"lowercase\": {\"field\": \"level\"}}].");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"The custom processors are not valid JSON: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── Cross-cluster search ───────────────────────────────────────────────────

    /// <summary>Cross-cluster API keys, and therefore this whole path, need at least this version.</summary>
    public static readonly (int Major, int Minor, int Patch) MinimumRemoteClusterVersion = (8, 14, 0);

    public async Task<List<ElasticsearchRemoteLink>> GetRemoteLinksAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchRemoteLinks
            .Include(l => l.RemoteCluster)
            .Where(l => l.TenantId == tenantId && l.LocalClusterId == clusterId)
            .OrderBy(l => l.Alias)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Links a cluster to another one it may search.
    ///
    /// <para>Both CRs change: the remote one opens its remote cluster server, and the local one
    /// gains the connection and an API key scoped to the named patterns. Opening that server is a
    /// transport change, so ECK restarts the remote cluster's nodes — which is why this returns a
    /// note saying so rather than leaving somebody to notice a rolling restart they did not ask
    /// for.</para>
    /// </summary>
    public async Task<string> CreateRemoteLinkAsync(
        Guid tenantId, Guid localClusterId, Guid remoteClusterId, string alias, string searchPatterns,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster local = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == localClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("The searching cluster was not found.");

        ElasticsearchCluster remote = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == remoteClusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("The cluster to be searched was not found.");

        alias = (alias ?? "").Trim().ToLowerInvariant();
        ValidateRemoteLink(local, remote, alias);

        if (await db.ElasticsearchRemoteLinks.AnyAsync(
                l => l.LocalClusterId == localClusterId && l.Alias == alias, ct))
            throw new InvalidOperationException($"'{local.Name}' already searches something under the alias '{alias}'.");

        ElasticsearchRemoteLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LocalClusterId = localClusterId,
            RemoteClusterId = remoteClusterId,
            Alias = alias,
            SearchIndexPatterns = string.IsNullOrWhiteSpace(searchPatterns) ? "*" : searchPatterns.Trim()
        };

        db.ElasticsearchRemoteLinks.Add(link);
        await db.SaveChangesAsync(ct);

        try
        {
            Clusters.IClusterClient client = await ClusterFor(tenantId, local.KubernetesClusterId, ct);

            // The remote first: the local cluster's connection has nothing to land on until the
            // remote cluster server is open.
            await ApplyClusterAsync(db, remote, client, ct);
            await ApplyClusterAsync(db, local, client, ct);

            link.LastAppliedAt = DateTime.UtcNow;
            link.LastError = null;
        }
        catch (Exception ex)
        {
            link.LastError = Truncate(ex.Message, 2000);
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);

        return $"'{local.Name}' can now search '{remote.Name}' as \"{alias}:\". "
            + "Opening the remote cluster server is a transport change, so ECK is restarting the searched cluster's "
            + "nodes one at a time; queries against it will be answered throughout by the nodes that are up.";
    }

    /// <summary>
    /// Removes a link. The remote cluster server is deliberately left open: closing it is a second
    /// rolling restart, and an open server with no API key granted to anybody reaches nothing.
    /// </summary>
    public async Task DeleteRemoteLinkAsync(Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchRemoteLink link = await db.ElasticsearchRemoteLinks
            .Include(l => l.LocalCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Link not found.");

        ElasticsearchCluster local = link.LocalCluster;

        db.ElasticsearchRemoteLinks.Remove(link);
        await db.SaveChangesAsync(ct);

        await ApplyClusterAsync(
            db, local, await ClusterFor(local.TenantId, local.KubernetesClusterId, ct), ct);
    }

    /// <summary>The clusters a given one could be linked to — same Kubernetes cluster, new enough, not itself.</summary>
    public async Task<List<ElasticsearchCluster>> GetRemoteCandidatesAsync(
        Guid tenantId, Guid localClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster? local = await db.ElasticsearchClusters
            .FirstOrDefaultAsync(c => c.Id == localClusterId && c.TenantId == tenantId, ct);
        if (local is null) return [];

        List<ElasticsearchCluster> candidates = await db.ElasticsearchClusters
            .Where(c => c.TenantId == tenantId
                && c.Id != localClusterId
                && c.KubernetesClusterId == local.KubernetesClusterId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return [.. candidates.Where(c => IsNewEnoughForRemoteClusters(c.Version))];
    }

    public static bool IsNewEnoughForRemoteClusters(string? version) =>
        TryParseVersion(version, out (int Major, int Minor, int Patch) v)
        && (v.Major > MinimumRemoteClusterVersion.Major
            || (v.Major == MinimumRemoteClusterVersion.Major && v.Minor >= MinimumRemoteClusterVersion.Minor));

    private static void ValidateRemoteLink(ElasticsearchCluster local, ElasticsearchCluster remote, string alias)
    {
        if (local.Id == remote.Id)
            throw new InvalidOperationException("A cluster cannot search itself remotely — it already can, locally.");

        if (local.KubernetesClusterId != remote.KubernetesClusterId)
            throw new InvalidOperationException(
                $"'{local.Name}' and '{remote.Name}' are on different Kubernetes clusters. ECK can only wire a remote "
                + "connection between clusters it manages together; across Kubernetes clusters the addresses and trust "
                + "have to be arranged by hand, which EntKube does not do here.");

        if (!IsNewEnoughForRemoteClusters(local.Version) || !IsNewEnoughForRemoteClusters(remote.Version))
            throw new InvalidOperationException(
                $"Both clusters must be on {MinimumRemoteClusterVersion.Major}.{MinimumRemoteClusterVersion.Minor} or "
                + $"later for cross-cluster API keys — '{local.Name}' is {local.Version} and '{remote.Name}' is "
                + $"{remote.Version}.");

        if (string.IsNullOrWhiteSpace(alias))
            throw new InvalidOperationException("The remote needs an alias — it is what a query calls it.");

        if (alias.Length > 63 || !alias.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new InvalidOperationException(
                "An alias may only contain lowercase letters, digits, '-' and '_' — it is used in index expressions "
                + "like \"alias:logs-*\".");
    }

    /// <summary>Re-applies one cluster's CR with everything currently true of it.</summary>
    private async Task ApplyClusterAsync(
        ApplicationDbContext db, ElasticsearchCluster cluster,
        Clusters.IClusterClient client, CancellationToken ct)
    {
        await client.ApplyManifestAsync(
            BuildElasticsearchManifest(
                cluster,
                await ResolveS3Async(db, cluster, ct),
                await ResolveRemotesAsync(db, cluster, ct),
                await IsSearchedRemotelyAsync(db, cluster, ct)), ct);
    }

    private static async Task<List<ElasticsearchRemoteRef>> ResolveRemotesAsync(
        ApplicationDbContext db, ElasticsearchCluster cluster, CancellationToken ct)
    {
        List<ElasticsearchRemoteLink> links = await db.ElasticsearchRemoteLinks
            .Include(l => l.RemoteCluster)
            .Where(l => l.LocalClusterId == cluster.Id)
            .OrderBy(l => l.Alias)
            .ToListAsync(ct);

        return [.. links.Select(l => new ElasticsearchRemoteRef(
            l.Alias, l.RemoteCluster.Name, l.RemoteCluster.Namespace, l.Patterns))];
    }

    private static Task<bool> IsSearchedRemotelyAsync(
        ApplicationDbContext db, ElasticsearchCluster cluster, CancellationToken ct) =>
        db.ElasticsearchRemoteLinks.AnyAsync(l => l.RemoteClusterId == cluster.Id, ct);

    // ── Kibana spaces ──────────────────────────────────────────────────────────

    public async Task<List<ElasticsearchKibanaSpace>> GetSpacesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ElasticsearchKibanaSpaces
            .Where(s => s.TenantId == tenantId && s.ElasticsearchClusterId == clusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a Kibana space, so several teams can share one cluster without sharing a dashboard
    /// list. Scoping a user to it (see <see cref="ElasticsearchUser.KibanaSpaceId"/>) is what turns
    /// that from tidiness into isolation.
    /// </summary>
    public async Task<ElasticsearchKibanaSpace> CreateSpaceAsync(
        Guid tenantId, Guid clusterId, string spaceId, string name, string? description,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        if (!cluster.KibanaEnabled)
            throw new InvalidOperationException("This cluster has no Kibana, so it has nowhere to put a space.");

        spaceId = (spaceId ?? "").Trim().ToLowerInvariant();
        ValidateSpaceId(spaceId);

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("The space needs a name for the space menu.");

        if (await db.ElasticsearchKibanaSpaces.AnyAsync(
                s => s.ElasticsearchClusterId == clusterId && s.SpaceId == spaceId, ct))
            throw new InvalidOperationException($"A space with the id '{spaceId}' already exists on this cluster.");

        ElasticsearchKibanaSpace space = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ElasticsearchClusterId = clusterId,
            SpaceId = spaceId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description!.Trim()
        };

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, $"space-{spaceId}");
        string jobName = JobName(cluster, $"space-{spaceId}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSpaceApplyScript(cluster, space)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        space.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) space.LastAppliedAt = DateTime.UtcNow;

        db.ElasticsearchKibanaSpaces.Add(space);
        await db.SaveChangesAsync(ct);

        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException("Creating the space failed. Kibana said:\n" + Truncate(log, 1200));
        if (outcome == JobOutcome.StillRunning)
            throw new InvalidOperationException(
                "Kibana did not answer in time — it starts well after Elasticsearch does. The space is recorded here "
                + "and can be re-applied once it is up.");

        return space;
    }

    /// <summary>Re-applies a space to Kibana, e.g. after the cluster was rebuilt.</summary>
    public async Task ApplySpaceAsync(Guid tenantId, Guid spaceId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchKibanaSpace space = await db.ElasticsearchKibanaSpaces
            .Include(s => s.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(s => s.Id == spaceId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Space not found.");

        ElasticsearchCluster cluster = space.ElasticsearchCluster;
        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, $"space-{space.SpaceId}");
        string jobName = JobName(cluster, $"space-{space.SpaceId}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSpaceApplyScript(cluster, space)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        space.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) space.LastAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (outcome != JobOutcome.Succeeded)
            throw new InvalidOperationException("Applying the space failed:\n" + Truncate(log, 1200));
    }

    /// <summary>
    /// Deletes a space and everything saved in it. Refused while an account is still confined to it:
    /// that account would sign in to a space that no longer exists and simply see nothing, with no
    /// message saying why.
    /// </summary>
    public async Task DeleteSpaceAsync(
        Guid tenantId, Guid spaceId, string performedBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchKibanaSpace space = await db.ElasticsearchKibanaSpaces
            .Include(s => s.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(s => s.Id == spaceId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Space not found.");

        ElasticsearchCluster cluster = space.ElasticsearchCluster;

        List<string> confined = await db.ElasticsearchUsers
            .Where(u => u.ElasticsearchClusterId == cluster.Id && u.KibanaSpaceId == space.SpaceId)
            .Select(u => u.Username)
            .ToListAsync(ct);

        if (confined.Count > 0)
            throw new InvalidOperationException(
                $"{string.Join(", ", confined)} can only see this space. Move them to another space, or give them access "
                + "to all of them, before deleting it — otherwise they sign in to nothing.");

        await auditService.RecordAsync(
            deploymentId: null,
            action: "elasticsearch.kibana-space.delete",
            resourceKind: "Elasticsearch",
            resourceName: $"{cluster.Namespace}/{cluster.Name}",
            details: $"space '{space.SpaceId}' and everything saved in it",
            performedBy: performedBy,
            ct: ct);

        try
        {
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
            string configMap = SnapshotConfigMapName(cluster, $"space-del-{space.SpaceId}");
            string jobName = JobName(cluster, $"space-del-{space.SpaceId}");

            await client.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildSpaceDeleteScript(cluster, space)), ct);
            await client.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);
            await WaitForJobAsync(cluster, jobName, client, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing Kibana space {Space} failed", space.SpaceId);
        }

        db.ElasticsearchKibanaSpaces.Remove(space);
        await db.SaveChangesAsync(ct);
    }

    private static void ValidateSpaceId(string spaceId)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
            throw new InvalidOperationException("The space needs an id — it appears in Kibana's URLs.");

        if (spaceId == "default")
            throw new InvalidOperationException(
                "'default' is Kibana's own space and already exists. Give this one an id of its own.");

        if (spaceId.Length > 63)
            throw new InvalidOperationException("A space id is 63 characters or fewer.");

        if (!spaceId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new InvalidOperationException(
                "A space id may only contain lowercase letters, digits, '-' and '_' — Kibana puts it in a URL.");
    }

    // ── Metrics ────────────────────────────────────────────────────────────────

    /// <summary>The exporter image. Pinned, like every other image EntKube puts on a cluster.</summary>
    public const string ExporterImage = "quay.io/prometheuscommunity/elasticsearch-exporter:v1.11.0";

    /// <summary>The port the exporter serves /metrics on.</summary>
    public const int ExporterPort = 9114;

    /// <summary>What Prometheus wants on a ServiceMonitor before it will scrape it.</summary>
    /// <param name="MatchLabels">
    /// The labels from the live Prometheus resource's <c>serviceMonitorSelector</c>. Empty means it
    /// selects every ServiceMonitor.
    /// </param>
    /// <param name="WatchesOtherNamespaces">
    /// False when Prometheus has no <c>serviceMonitorNamespaceSelector</c> at all, which confines it
    /// to its own namespace — where an Elasticsearch exporter will never be.
    /// </param>
    /// <param name="Found">Whether a Prometheus resource was found to read any of this from.</param>
    /// <param name="RuleMatchLabels">
    /// The same, for <c>ruleSelector</c> — a PrometheusRule is selected by its own selector, and
    /// kube-prometheus-stack sets both from the same release label, so getting one right and the
    /// other wrong is entirely possible.
    /// </param>
    public sealed record PrometheusSelector(
        IReadOnlyDictionary<string, string> MatchLabels,
        bool WatchesOtherNamespaces,
        bool Found,
        IReadOnlyDictionary<string, string>? RuleMatchLabels = null)
    {
        /// <summary>Labels a PrometheusRule needs, falling back to the ServiceMonitor's when unset.</summary>
        public IReadOnlyDictionary<string, string> RuleLabels => RuleMatchLabels ?? MatchLabels;
    }

    /// <summary>
    /// Reads what the cluster's Prometheus will actually select, so the ServiceMonitor can be
    /// labelled to match instead of being diagnosed afterwards.
    ///
    /// <para>This is the failure everyone meets once: kube-prometheus-stack ships
    /// <c>serviceMonitorSelectorNilUsesHelmValues: true</c>, which turns into a selector of
    /// <c>release: &lt;its release name&gt;</c>. A monitor created by anything else carries no such
    /// label, is silently ignored, and the metrics simply never appear — with nothing anywhere
    /// reporting an error.</para>
    /// </summary>
    public async Task<PrometheusSelector> ReadPrometheusSelectorAsync(
        Clusters.IClusterClient client, CancellationToken ct = default)
    {
        try
        {
            string json = await client.GetJsonAllNamespacesAsync(
                "prometheuses.monitoring.coreos.com", ct: ct);
            return ParsePrometheusSelector(json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No Prometheus resource could be read; the ServiceMonitor will be left unlabelled");
            return new PrometheusSelector(new Dictionary<string, string>(), true, false);
        }
    }

    public static PrometheusSelector ParsePrometheusSelector(string json)
    {
        Dictionary<string, string> labels = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return new(labels, true, false);

            foreach (JsonElement prom in items.EnumerateArray())
            {
                if (!prom.TryGetProperty("spec", out JsonElement spec)) continue;

                if (spec.TryGetProperty("serviceMonitorSelector", out JsonElement selector)
                    && selector.TryGetProperty("matchLabels", out JsonElement matchLabels))
                {
                    foreach (JsonProperty label in matchLabels.EnumerateObject())
                        labels[label.Name] = label.Value.GetString() ?? "";
                }

                Dictionary<string, string>? ruleLabels = null;
                if (spec.TryGetProperty("ruleSelector", out JsonElement ruleSelector)
                    && ruleSelector.TryGetProperty("matchLabels", out JsonElement ruleMatchLabels))
                {
                    ruleLabels = [];
                    foreach (JsonProperty label in ruleMatchLabels.EnumerateObject())
                        ruleLabels[label.Name] = label.Value.GetString() ?? "";
                }

                // Absent (rather than empty) confines Prometheus to its own namespace.
                bool watchesOthers = spec.TryGetProperty("serviceMonitorNamespaceSelector", out _);
                return new PrometheusSelector(labels, watchesOthers, true, ruleLabels);
            }
        }
        catch { }
        return new PrometheusSelector(labels, true, false);
    }

    /// <summary>
    /// Puts an Elasticsearch exporter beside the cluster and wires it to Prometheus: a read-only
    /// account of its own, a Deployment, a Service, and a ServiceMonitor labelled with whatever the
    /// live Prometheus selects on.
    /// </summary>
    public async Task<string> EnableMonitoringAsync(
        Guid tenantId, Guid clusterId, bool indexMetrics, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string password = GeneratePassword();

        // The exporter gets its own account with cluster monitor and nothing else — the metrics it
        // reads are all read-only operations, and this credential sits in a pod for years.
        await client.ApplyManifestAsync(
            BuildExporterCredentialsSecret(cluster, password), ct);

        string configMap = SnapshotConfigMapName(cluster, "exporter-user");
        string jobName = JobName(cluster, "exporter-user");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildExporterUserScript(cluster)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap, cluster.ExporterSecretName), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);
        if (outcome == JobOutcome.Failed)
            throw new InvalidOperationException(
                "Creating the exporter's Elasticsearch account failed:\n" + Truncate(log, 1200));

        PrometheusSelector selector = await ReadPrometheusSelectorAsync(client, ct);

        cluster.MonitoringEnabled = true;
        cluster.MonitoringIndexMetrics = indexMetrics;
        cluster.MonitoringSelectorNote = DescribeSelector(selector, cluster.Namespace);

        await client.ApplyManifestAsync(BuildExporterDeployment(cluster), ct);
        await client.ApplyManifestAsync(BuildExporterService(cluster), ct);
        await client.ApplyManifestAsync(BuildExporterServiceMonitor(cluster, selector.MatchLabels), ct);
        await client.ApplyManifestAsync(BuildExporterPrometheusRule(cluster, selector.RuleLabels), ct);

        await db.SaveChangesAsync(ct);
        return cluster.MonitoringSelectorNote!;
    }

    /// <summary>Removes the exporter and its account.</summary>
    public async Task DisableMonitoringAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchCluster cluster = await db.ElasticsearchClusters
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Elasticsearch cluster not found.");

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);

        foreach ((string kind, string name) in new[]
        {
            ("prometheusrule", cluster.ExporterName),
            ("servicemonitor", cluster.ExporterName),
            ("service", cluster.ExporterName),
            ("deployment", cluster.ExporterName),
            ("secret", cluster.ExporterSecretName)
        })
        {
            try
            {
                await client.DeleteManifestAsync(kind, name, cluster.Namespace, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No {Kind}/{Name} to remove", kind, name);
            }
        }

        cluster.MonitoringEnabled = false;
        cluster.MonitoringSelectorNote = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Says plainly whether these metrics will be scraped, and what to do when they will not.</summary>
    public static string DescribeSelector(PrometheusSelector selector, string ns)
    {
        if (!selector.Found)
            return "No Prometheus was found on this cluster, so the ServiceMonitor is unlabelled. "
                + "Nothing will scrape it until one is installed.";

        List<string> notes = [];

        notes.Add(selector.MatchLabels.Count == 0
            ? "Prometheus selects every ServiceMonitor, so no labels were needed."
            : "The ServiceMonitor was labelled "
                + string.Join(", ", selector.MatchLabels.Select(kv => $"{kv.Key}={kv.Value}"))
                + " to match what Prometheus selects on.");

        notes.Add(selector.RuleLabels.Count == 0
            ? "Alerting rules were installed and Prometheus selects every PrometheusRule."
            : "Alerting rules were installed, labelled "
                + string.Join(", ", selector.RuleLabels.Select(kv => $"{kv.Key}={kv.Value}"))
                + " — they reach Alertmanager, and from there EntKube's incidents.");

        if (!selector.WatchesOtherNamespaces)
            notes.Add($"Prometheus has no serviceMonitorNamespaceSelector, so it only looks in its own namespace "
                + $"and will not see anything in {ns}. Set serviceMonitorNamespaceSelector: {{}} on the "
                + "kube-prometheus-stack values to let it look everywhere.");

        return string.Join(" ", notes);
    }

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
            Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);

            // Elasticsearch first. ECK holds Kibana at its current version until the cluster it is
            // associated with can serve it, so applying both is safe and saves a second visit.
            await ApplyClusterAsync(db, cluster, client, ct);

            if (cluster.KibanaEnabled)
                await client.ApplyManifestAsync(BuildKibanaManifest(cluster), ct);

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
        Clusters.IClusterClient client =
            await ClusterFor(cluster.TenantId, cluster.KubernetesClusterId, ct);

        string podsJson = await client.GetJsonAsync(
            "pods", cluster.Namespace,
            $"elasticsearch.k8s.elastic.co/cluster-name={cluster.Name}", ct);

        string pod = FirstReadyPodName(podsJson)
            ?? throw new InvalidOperationException(
                $"No Ready Elasticsearch pod in '{cluster.Namespace}' to ask — the cluster may still be starting.");

        string password = await client.GetSecretValueAsync(
            cluster.ElasticUserSecretName, "elastic", cluster.Namespace, ct)
            ?? throw new InvalidOperationException(
                $"The '{cluster.ElasticUserSecretName}' Secret is not readable, so there is no way to authenticate.");

        return await client.RunCommandOnPodWithStdinAsync(
            pod, cluster.Namespace, ["sh", "-c", BuildQueryCommand(path)], password + "\n", ct, "elasticsearch");
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

        if (cluster is null) return;

        Clusters.IClusterClient? reachable =
            await clusterAccess.ForAsync(cluster.TenantId, cluster.KubernetesClusterId, ct);

        if (reachable is null) return;

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
        ElasticsearchKibanaAccess kibanaAccess = ElasticsearchKibanaAccess.None,
        string? kibanaSpaceId = null,
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
            Access = access,
            KibanaAccess = kibanaAccess,
            KibanaSpaceId = string.IsNullOrWhiteSpace(kibanaSpaceId) ? null : kibanaSpaceId.Trim(),
            PasswordSetAt = DateTime.UtcNow
        };

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string password = GeneratePassword();

        await client.ApplyManifestAsync(BuildUserCredentialsSecret(cluster, user, password), ct);

        string configMap = UserConfigMapName(cluster, user);
        string jobName = JobName(cluster, $"user-{username}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildUserApplyScript(cluster, user)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap, user.CredentialsSecretName), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

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
        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = UserConfigMapName(cluster, user);
        string jobName = JobName(cluster, $"user-{user.Username}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildUserApplyScript(cluster, user)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap, user.CredentialsSecretName), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        user.LastError = outcome == JobOutcome.Succeeded ? null : Truncate(log, 2000);
        if (outcome == JobOutcome.Succeeded) user.LastAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (outcome != JobOutcome.Succeeded)
            throw new InvalidOperationException("Applying the user failed:\n" + Truncate(log, 1200));
    }

    /// <summary>
    /// Changes a user's access without touching its password, so a person can be promoted from
    /// reading Kibana to building in it — or an application narrowed — without a redeploy.
    /// </summary>
    public async Task UpdateUserAccessAsync(
        Guid tenantId, Guid userId, string indexPattern, ElasticsearchAccess access,
        ElasticsearchKibanaAccess kibanaAccess, string? kibanaSpaceId = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchUser user = await db.ElasticsearchUsers
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("User not found.");

        if (string.IsNullOrWhiteSpace(indexPattern))
            throw new InvalidOperationException("An index pattern is required.");

        user.IndexPattern = indexPattern.Trim();
        user.Access = access;
        user.KibanaAccess = kibanaAccess;
        user.KibanaSpaceId = string.IsNullOrWhiteSpace(kibanaSpaceId) ? null : kibanaSpaceId.Trim();
        await db.SaveChangesAsync(ct);

        await ApplyUserAsync(tenantId, userId, ct);
    }

    /// <summary>
    /// Generates a new password, tells Elasticsearch about it, and pushes it to every application
    /// bound to the account — in that order, because an application holding the old password is
    /// broken for exactly as long as the gap between the second step and the third.
    ///
    /// <para>Returns the new password once, for handing to a person. It is not stored here; the
    /// Secret in the cluster is where it lives afterwards.</para>
    /// </summary>
    public async Task<string> ResetPasswordAsync(
        Guid tenantId, Guid userId, string performedBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchUser user = await db.ElasticsearchUsers
            .Include(u => u.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("User not found.");

        ElasticsearchCluster cluster = user.ElasticsearchCluster;
        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string password = GeneratePassword();

        await auditService.RecordAsync(
            deploymentId: null,
            action: "elasticsearch.user.password-reset",
            resourceKind: "Elasticsearch",
            resourceName: $"{cluster.Namespace}/{cluster.Name}",
            details: $"user '{user.Username}'",
            performedBy: performedBy,
            ct: ct);

        await client.ApplyManifestAsync(BuildUserCredentialsSecret(cluster, user, password), ct);

        string configMap = UserConfigMapName(cluster, user, "password");
        string jobName = JobName(cluster, $"user-pw-{user.Username}");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildPasswordResetScript(cluster, user)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap, user.CredentialsSecretName), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

        if (outcome != JobOutcome.Succeeded)
        {
            user.LastError = Truncate(log, 2000);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException(
                "Setting the new password failed, so the old one is still in force. Elasticsearch said:\n"
                + Truncate(log, 1200));
        }

        user.PasswordSetAt = DateTime.UtcNow;
        user.LastError = null;
        await db.SaveChangesAsync(ct);

        // Every bound application is holding the old password from this moment on, so push the new
        // one straight away rather than waiting for somebody to notice.
        List<Guid> bindingIds = await db.ElasticsearchBindings
            .Where(b => b.ElasticsearchUserId == userId && b.SyncEnabled)
            .Select(b => b.Id)
            .ToListAsync(ct);

        foreach (Guid bindingId in bindingIds)
        {
            try
            {
                await SyncBindingAsync(tenantId, bindingId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Re-syncing binding {Binding} after a password reset failed", bindingId);
            }
        }

        return password;
    }

    /// <summary>Reads a user's current password back, for handing to the person who signs in with it.</summary>
    public async Task<string?> GetUserPasswordAsync(
        Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ElasticsearchUser? user = await db.ElasticsearchUsers
            .Include(u => u.ElasticsearchCluster).ThenInclude(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);

        if (user is null) return null;

        return await (await ClusterFor(user.ElasticsearchCluster.TenantId, user.ElasticsearchCluster.KubernetesClusterId, ct)).GetSecretValueAsync(
            user.CredentialsSecretName, "password", user.ElasticsearchCluster.Namespace,
             ct);
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
        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);

        try
        {
            string configMap = UserConfigMapName(cluster, user, "delete");
            string jobName = JobName(cluster, $"user-del-{user.Username}");

            await client.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildUserDeleteScript(cluster, user)), ct);
            await client.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);
            await WaitForJobAsync(cluster, jobName, client, ct);

            await client.DeleteManifestAsync("secret", user.CredentialsSecretName, cluster.Namespace, ct);
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
        Clusters.IClusterClient esCluster = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);

        string password = await esCluster.GetSecretValueAsync(
            binding.ElasticsearchUser.CredentialsSecretName, "password", cluster.Namespace, ct)
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
        string? caCert = await esCluster.GetSecretValueAsync(
            cluster.HttpCertsSecretName, "ca.crt", cluster.Namespace, ct);
        if (!string.IsNullOrEmpty(caCert)) data["ELASTICSEARCH_CA_CRT"] = caCert;

        Clusters.IClusterClient appCluster = await ClusterFor(tenantId, binding.AppDeployment.ClusterId, ct);
        string appNs = binding.AppDeployment.Namespace;

        await appCluster.EnsureNamespaceAsync(appNs, ct);
        await appCluster.ApplyManifestAsync(
            BuildBindingSecretManifest(binding.KubernetesSecretName, appNs, data), ct);

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
            await (await ClusterFor(tenantId, binding.AppDeployment.ClusterId, ct))
                .DeleteManifestAsync(
                    "secret", binding.KubernetesSecretName, binding.AppDeployment.Namespace, ct);
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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        ElasticsearchS3Settings s3 = await BuildS3SettingsAsync(tenantId, cluster, link, ct);

        await client.ApplyManifestAsync(BuildKeystoreSecretManifest(cluster, await ReadS3CredentialsAsync(tenantId, link, ct)), ct);
        await client.ApplyManifestAsync(BuildElasticsearchManifest(cluster, s3), ct);

        string configMap = SnapshotConfigMapName(cluster);
        string jobName = JobName(cluster, "snapshot-setup");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotSetupScript(cluster, s3)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, "disable");
        string jobName = JobName(cluster, "snapshot-disable");

        try
        {
            await client.ApplyManifestAsync(
                BuildScriptConfigMap(cluster, configMap, BuildSnapshotDisableScript(cluster)), ct);
            await client.ApplyManifestAsync(
                BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);
            await WaitForJobAsync(cluster, jobName, client, ct);
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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, "execute");
        string jobName = JobName(cluster, "snapshot-run");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotExecuteScript(cluster)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, "status");
        string jobName = JobName(cluster, "snapshot-status");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotStatusScript(cluster)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);
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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
        string configMap = SnapshotConfigMapName(cluster, "list");
        string jobName = JobName(cluster, "snapshot-list");

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap, BuildSnapshotListScript(cluster)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

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

        Clusters.IClusterClient client = await ClusterFor(tenantId, cluster.KubernetesClusterId, ct);
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

        await client.ApplyManifestAsync(
            BuildScriptConfigMap(cluster, configMap,
                BuildRestoreScript(cluster, snapshotName, indexPattern, mode, renamePrefix, includeGlobalState)), ct);
        await client.ApplyManifestAsync(
            BuildElasticsearchJobManifest(cluster, jobName, configMap), ct);

        (JobOutcome outcome, string log) = await WaitForJobAsync(cluster, jobName, client, ct);

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
    /// <param name="remotes">Remote clusters this one may search, if any.</param>
    /// <param name="serveAsRemote">
    /// Whether another cluster searches this one. Turning it on opens the remote cluster server,
    /// which changes the transport configuration and therefore restarts the nodes.
    /// </param>
    public static string BuildElasticsearchManifest(
        ElasticsearchCluster c,
        ElasticsearchS3Settings? s3 = null,
        IReadOnlyList<ElasticsearchRemoteRef>? remotes = null,
        bool serveAsRemote = false)
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

        if (serveAsRemote)
        {
            // The remote cluster server is what an API-key connection lands on. It is a transport
            // change, so ECK rolls the nodes when it is switched on — the caller is told that
            // before the link is created rather than after the restart starts.
            sb.AppendLine("  remoteClusterServer:");
            sb.AppendLine("    enabled: true");
        }

        if (remotes is { Count: > 0 })
        {
            sb.AppendLine("  remoteClusters:");
            foreach (ElasticsearchRemoteRef remote in remotes)
            {
                sb.AppendLine($"    - name: {remote.Alias}");
                sb.AppendLine("      elasticsearchRef:");
                sb.AppendLine($"        name: {remote.Name}");
                sb.AppendLine($"        namespace: {remote.Namespace}");
                // A cross-cluster API key scoped to these patterns: the searching cluster reaches
                // exactly them, and ECK creates and rotates the key itself.
                sb.AppendLine("      apiKey:");
                sb.AppendLine("        access:");
                sb.AppendLine("          search:");
                sb.AppendLine("            names:");
                foreach (string pattern in remote.SearchPatterns)
                    sb.AppendLine($"              - \"{pattern}\"");
            }
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

        if (!string.IsNullOrWhiteSpace(p.DefaultPipelineName))
            settings["index.default_pipeline"] = p.DefaultPipelineName!;

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
        if (c.KibanaEnabled)
        {
            // Kibana serves its own certificate, so a script that talks to it needs a second CA.
            // Optional, because the Secret exists only once ECK has created the Kibana it belongs to.
            sb.AppendLine("        - name: kb-ca");
            sb.AppendLine("          secret:");
            sb.AppendLine($"            secretName: {c.KibanaCertsSecretName}");
            sb.AppendLine("            optional: true");
        }
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
        if (c.KibanaEnabled)
        {
            sb.AppendLine("            - name: kb-ca");
            sb.AppendLine("              mountPath: /kb-ca");
            sb.AppendLine("              readOnly: true");
        }
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
    /// The application Kibana registers its privileges under. The suffix is Kibana's index name,
    /// which is <c>.kibana</c> unless somebody has renamed it — nothing here renames it.
    /// </summary>
    public const string KibanaApplication = "kibana-.kibana";

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
        Dictionary<string, object> role = new()
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
        };

        if (user.KibanaAccess != ElasticsearchKibanaAccess.None)
        {
            // Kibana access as an application privilege on this user's own role, not the built-in
            // viewer/editor roles: those carry read (or write) on every index, which would undo the
            // index scoping in the same breath as granting a login.
            role["applications"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["application"] = KibanaApplication,
                    ["privileges"] = new[] { user.KibanaAccess == ElasticsearchKibanaAccess.All ? "all" : "read" },
                    // "*" is every space. Naming one confines this account to it: the space menu
                    // shows nothing else, and neither do the dashboards.
                    ["resources"] = new[]
                    {
                        string.IsNullOrWhiteSpace(user.KibanaSpaceId) ? "*" : $"space:{user.KibanaSpaceId}"
                    }
                }
            };
        }

        string roleBody = JsonSerializer.Serialize(role, JsonOpts);

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

    /// <summary>
    /// Sets a new password on an existing user. A separate endpoint from the user document, so the
    /// account's roles are left exactly as they are rather than re-asserted from here.
    /// </summary>
    public static string BuildPasswordResetScript(ElasticsearchCluster c, ElasticsearchUser user)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("umask 077");
        sb.AppendLine("cat > /tmp/pw.json <<ENTKUBE_EOF");
        sb.AppendLine("{ \"password\": \"${ES_USER_PASSWORD}\" }");
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X POST \"$ES/_security/user/{user.Username}/_password\" -d @/tmp/pw.json)");
        sb.AppendLine("rm -f /tmp/pw.json");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then echo \"password change -> HTTP $code\"; cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine($"echo \"password changed for {user.Username}\"");
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

    /// <summary>
    /// Creates or updates a Kibana space through Kibana's own API.
    ///
    /// <para>Two things are specific to Kibana rather than Elasticsearch: it is a different service
    /// with a different certificate, and every write needs the <c>kbn-xsrf</c> header — without it
    /// Kibana answers 400 with a message about cross-site request forgery, which reads as a bug in
    /// the request body.</para>
    /// </summary>
    public static string BuildSpaceApplyScript(ElasticsearchCluster c, ElasticsearchKibanaSpace space)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = space.SpaceId,
            ["name"] = space.Name,
            ["description"] = space.Description
        }, JsonOpts);

        StringBuilder sb = new();
        AppendKibanaPreamble(sb, c);
        sb.AppendLine("cat > /tmp/space.json <<'ENTKUBE_EOF'");
        sb.AppendLine(body);
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine("# Create, or update the one that is already there — re-applying must be harmless.");
        sb.AppendLine($"code=$($KCURL -o /tmp/resp -w '%{{http_code}}' -X POST \"$KB/api/spaces/space\" -d @/tmp/space.json)");
        sb.AppendLine("if [ \"$code\" = \"409\" ]; then");
        sb.AppendLine($"  code=$($KCURL -o /tmp/resp -w '%{{http_code}}' -X PUT \"$KB/api/spaces/space/{space.SpaceId}\" -d @/tmp/space.json)");
        sb.AppendLine("fi");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then echo \"space apply -> HTTP $code\"; cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine($"echo \"space {space.SpaceId} applied (HTTP $code)\"");
        return sb.ToString();
    }

    /// <summary>
    /// Creates or replaces a data view in Kibana.
    ///
    /// <para>The id is EntKube's own, which is what makes this idempotent: Kibana would otherwise
    /// generate one, and a second apply would leave two data views over the same pattern with no way
    /// to tell which the dashboards were built on.</para>
    /// </summary>
    public static string BuildDataViewApplyScript(ElasticsearchCluster c, ElasticsearchDataView view)
    {
        Dictionary<string, object> dataView = new()
        {
            ["id"] = view.KibanaObjectId,
            ["title"] = view.Title,
            ["name"] = string.IsNullOrWhiteSpace(view.Name) ? view.Title : view.Name!
        };

        // An empty time field is a valid choice — reference data has no time axis — but it has to be
        // omitted rather than sent empty, which Kibana rejects.
        if (!string.IsNullOrWhiteSpace(view.TimeFieldName))
            dataView["timeFieldName"] = view.TimeFieldName;

        string body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data_view"] = dataView,
            // Replace the object with this id rather than failing on the second apply.
            ["override"] = true
        }, JsonOpts);

        StringBuilder sb = new();
        AppendKibanaPreamble(sb, c);
        sb.AppendLine("cat > /tmp/dataview.json <<'ENTKUBE_EOF'");
        sb.AppendLine(body);
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine($"code=$($KCURL -o /tmp/resp -w '%{{http_code}}' -X POST \"$KB{SpacePath(view.SpaceId)}/api/data_views/data_view\" -d @/tmp/dataview.json)");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then echo \"data view -> HTTP $code\"; cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine($"echo \"data view {view.Title} applied (HTTP $code)\"");
        return sb.ToString();
    }

    public static string BuildDataViewDeleteScript(ElasticsearchCluster c, ElasticsearchDataView view)
    {
        StringBuilder sb = new();
        AppendKibanaPreamble(sb, c);
        sb.AppendLine($"code=$($KCURL -o /tmp/resp -w '%{{http_code}}' -X DELETE \"$KB{SpacePath(view.SpaceId)}/api/data_views/data_view/{view.KibanaObjectId}\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ] && [ \"$code\" != \"404\" ]; then cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine($"echo \"data view {view.Title} removed (HTTP $code)\"");
        return sb.ToString();
    }

    /// <summary>
    /// The path prefix that puts a Kibana API call inside a space. The default space has no prefix —
    /// "/s/default" is not the same URL, and saved objects created through it land elsewhere.
    /// </summary>
    public static string SpacePath(string? spaceId) =>
        string.IsNullOrWhiteSpace(spaceId) ? "" : $"/s/{spaceId}";

    /// <summary>Deletes a space, and with it everything saved inside it.</summary>
    public static string BuildSpaceDeleteScript(ElasticsearchCluster c, ElasticsearchKibanaSpace space)
    {
        StringBuilder sb = new();
        AppendKibanaPreamble(sb, c);
        sb.AppendLine($"code=$($KCURL -o /tmp/resp -w '%{{http_code}}' -X DELETE \"$KB/api/spaces/space/{space.SpaceId}\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ] && [ \"$code\" != \"404\" ]; then cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine($"echo \"space {space.SpaceId} removed (HTTP $code)\"");
        return sb.ToString();
    }

    /// <summary>
    /// The opening for a script that talks to Kibana rather than Elasticsearch: Kibana's own
    /// endpoint and CA, the xsrf header every write needs, and a wait — Kibana becomes available
    /// well after the cluster it connects to does, so a space applied at the wrong moment fails for
    /// reasons that have nothing to do with the space.
    /// </summary>
    private static void AppendKibanaPreamble(StringBuilder sb, ElasticsearchCluster c)
    {
        sb.AppendLine("set -eu");
        sb.AppendLine($"KB=\"{c.KibanaEndpoint}\"");
        sb.AppendLine("KCURL=\"curl -sS --cacert /kb-ca/ca.crt -u elastic:${ELASTIC_PASSWORD} "
            + "-H Content-Type:application/json -H kbn-xsrf:true\"");
        sb.AppendLine();
        sb.AppendLine("ready=0");
        sb.AppendLine("for i in $(seq 1 60); do");
        sb.AppendLine("  if $KCURL -o /dev/null \"$KB/api/status\"; then ready=1; break; fi");
        sb.AppendLine("  sleep 5");
        sb.AppendLine("done");
        sb.AppendLine("if [ \"$ready\" != \"1\" ]; then echo \"Kibana did not become available within 5 minutes\"; exit 1; fi");
        sb.AppendLine();
    }

    /// <summary>
    /// Renders the pipeline document Elasticsearch takes at <c>_ingest/pipeline/{id}</c>.
    ///
    /// <para>Every pipeline gets an <c>on_failure</c> handler that records the error on the document
    /// instead of letting it fail. Without one, a processor that throws — a grok pattern that does
    /// not match, a date that will not parse — rejects the whole document, and the line is simply
    /// gone. With one, it is indexed with <c>ingest.failure</c> set, which is both searchable and
    /// fixable later.</para>
    /// </summary>
    public static string BuildPipelineJson(ElasticsearchIngestPipeline p)
    {
        List<object> processors = [];

        if (!string.IsNullOrWhiteSpace(p.GrokField) && !string.IsNullOrWhiteSpace(p.GrokPattern))
        {
            processors.Add(new Dictionary<string, object>
            {
                ["grok"] = new Dictionary<string, object>
                {
                    ["field"] = p.GrokField!,
                    ["patterns"] = new[] { p.GrokPattern! },
                    ["ignore_missing"] = true
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(p.TimestampField))
        {
            processors.Add(new Dictionary<string, object>
            {
                ["date"] = new Dictionary<string, object>
                {
                    ["field"] = p.TimestampField!,
                    ["target_field"] = "@timestamp",
                    ["formats"] = Split(p.TimestampFormats).ToArray()
                }
            });
        }

        foreach (string pair in Split(p.RenameFields))
        {
            string[] parts = pair.Split(':');
            processors.Add(new Dictionary<string, object>
            {
                ["rename"] = new Dictionary<string, object>
                {
                    ["field"] = parts[0].Trim(),
                    ["target_field"] = parts[1].Trim(),
                    // A rename of a field this particular document does not have is not an error.
                    ["ignore_missing"] = true
                }
            });
        }

        foreach (string pair in Split(p.SetFields))
        {
            string[] parts = pair.Split('=', 2);
            processors.Add(new Dictionary<string, object>
            {
                ["set"] = new Dictionary<string, object>
                {
                    ["field"] = parts[0].Trim(),
                    ["value"] = parts[1].Trim()
                }
            });
        }

        string[] removals = [.. Split(p.RemoveFields)];
        if (removals.Length > 0)
        {
            processors.Add(new Dictionary<string, object>
            {
                ["remove"] = new Dictionary<string, object>
                {
                    ["field"] = removals,
                    ["ignore_missing"] = true
                }
            });
        }

        // Custom processors are parsed and appended as elements, not spliced into the rendered text:
        // string surgery on JSON is the kind of thing that works until somebody's pattern contains a
        // bracket. The validator has already established this is an array.
        if (!string.IsNullOrWhiteSpace(p.CustomProcessorsJson))
        {
            using JsonDocument custom = JsonDocument.Parse(p.CustomProcessorsJson!);
            foreach (JsonElement processor in custom.RootElement.EnumerateArray())
                processors.Add(processor.Clone());
        }

        Dictionary<string, object> document = new()
        {
            ["description"] = string.IsNullOrWhiteSpace(p.Description)
                ? $"Managed by EntKube ({p.Name})"
                : p.Description!,
            ["processors"] = processors,
            ["on_failure"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["set"] = new Dictionary<string, object>
                    {
                        ["field"] = "ingest.failure",
                        ["value"] = "{{ _ingest.on_failure_message }}"
                    }
                }
            }
        };

        return JsonSerializer.Serialize(document, JsonOpts);
    }

    public static string BuildPipelineApplyScript(ElasticsearchCluster c, ElasticsearchIngestPipeline p)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("cat > /tmp/pipeline.json <<'ENTKUBE_EOF'");
        sb.AppendLine(BuildPipelineJson(p));
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine($"put \"/_ingest/pipeline/{p.Name}\" /tmp/pipeline.json");
        return sb.ToString();
    }

    public static string BuildPipelineDeleteScript(ElasticsearchCluster c, ElasticsearchIngestPipeline p)
    {
        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine($"code=$($CURL -o /tmp/resp -w '%{{http_code}}' -X DELETE \"$ES/_ingest/pipeline/{p.Name}\")");
        sb.AppendLine("if [ \"$code\" -ge 300 ] && [ \"$code\" != \"404\" ]; then cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine($"echo \"pipeline {p.Name} removed (HTTP $code)\"");
        return sb.ToString();
    }

    /// <summary>
    /// Runs one document through the pipeline and prints the result behind a marker. The pipeline is
    /// sent in the request rather than referenced by name, so an unsaved edit can be tried before it
    /// is applied to anything.
    /// </summary>
    public static string BuildPipelineSimulateScript(
        ElasticsearchCluster c, ElasticsearchIngestPipeline p, string sampleDocumentJson)
    {
        string body = "{\n  \"pipeline\": " + BuildPipelineJson(p) + ",\n"
            + "  \"docs\": [ { \"_source\": " + sampleDocumentJson.Trim() + " } ]\n}";

        StringBuilder sb = new();
        AppendScriptPreamble(sb, c);
        sb.AppendLine("cat > /tmp/simulate.json <<'ENTKUBE_EOF'");
        sb.AppendLine(body);
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine("code=$($CURL -o /tmp/resp -w '%{http_code}' -X POST \"$ES/_ingest/pipeline/_simulate\" -d @/tmp/simulate.json)");
        sb.AppendLine("if [ \"$code\" -ge 300 ]; then cat /tmp/resp; echo; exit 1; fi");
        sb.AppendLine("echo \"---ENTKUBE-SIMULATE---\"");
        sb.AppendLine("cat /tmp/resp");
        return sb.ToString();
    }

    /// <summary>Heap above this for a sustained period is the shape of a cluster about to stall.</summary>
    public const int HeapPressurePercent = 85;

    /// <summary>
    /// Alerting rules for the metrics the exporter publishes.
    ///
    /// <para>These are not a second opinion on the Operations Advisor — they are the same facts
    /// arriving somewhere else, and that matters. The advisor is a page somebody opens; a
    /// Prometheus alert reaches Alertmanager, and from there EntKube's own alert sync turns it into
    /// an incident with routing and an on-call rota behind it. Red at 03:00 should wake somebody,
    /// not wait to be noticed.</para>
    ///
    /// <para>Every alert carries the cluster's name and namespace as labels, because an incident
    /// that says "Elasticsearch is red" without saying which one is a page nobody can act on.</para>
    /// </summary>
    public static string BuildExporterPrometheusRule(
        ElasticsearchCluster c, IReadOnlyDictionary<string, string> selectorLabels)
    {
        // The exporter labels every series with the cluster it read them from, which is what makes
        // one rule group per EntKube-managed cluster safe on a shared Prometheus.
        string scope = $"cluster=\"{c.Name}\"";

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: monitoring.coreos.com/v1");
        sb.AppendLine("kind: PrometheusRule");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.ExporterName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        foreach ((string key, string value) in selectorLabels.OrderBy(kv => kv.Key))
            sb.AppendLine($"    {key}: \"{value}\"");
        sb.AppendLine("spec:");
        sb.AppendLine("  groups:");
        sb.AppendLine($"    - name: entkube-elasticsearch-{c.Name}");
        sb.AppendLine("      rules:");

        AppendRule(sb, c,
            alert: "ElasticsearchRed",
            expr: $"max(elasticsearch_cluster_health_status{{{scope},color=\"red\"}}) == 1",
            forDuration: "5m",
            severity: "critical",
            summary: $"Elasticsearch \"{c.Name}\" is red",
            description: "At least one primary shard is unavailable, so some data cannot be read or written. "
                + "Usually a node that left or a disk that filled.");

        AppendRule(sb, c,
            alert: "ElasticsearchYellow",
            expr: $"max(elasticsearch_cluster_health_status{{{scope},color=\"yellow\"}}) == 1",
            // Long enough to ignore the yellow that every rolling restart and every rollover causes.
            forDuration: "1h",
            severity: "warning",
            summary: $"Elasticsearch \"{c.Name}\" has been yellow for an hour",
            description: "Replica shards have nowhere to live, so some data has no second copy. "
                + "A restart or a rollover explains a few minutes of this; an hour does not.");

        AppendRule(sb, c,
            alert: "ElasticsearchDiskHigh",
            expr: "max(1 - (elasticsearch_filesystem_data_available_bytes{" + scope + "} "
                + "/ elasticsearch_filesystem_data_size_bytes{" + scope + "})) "
                + "> " + Threshold(DiskHighWatermarkPercent),
            forDuration: "15m",
            severity: "warning",
            summary: $"A node of \"{c.Name}\" is past the disk watermark",
            description: $"Elasticsearch stops allocating shards to a node at {DiskHighWatermarkPercent}% and makes "
                + $"its indices read-only at {DiskFloodStagePercent}%, which it does not undo when space is freed.");

        AppendRule(sb, c,
            alert: "ElasticsearchHeapPressure",
            expr: "max(elasticsearch_jvm_memory_used_bytes{" + scope + ",area=\"heap\"} "
                + "/ elasticsearch_jvm_memory_max_bytes{" + scope + ",area=\"heap\"}) "
                + "> " + Threshold(HeapPressurePercent),
            forDuration: "30m",
            severity: "warning",
            summary: $"A node of \"{c.Name}\" is low on heap",
            description: "Sustained heap pressure means the node spends its time collecting garbage rather than "
                + "answering queries, and is the step before it drops out of the cluster.");

        AppendRule(sb, c,
            alert: "ElasticsearchExporterDown",
            expr: $"up{{job=~\".*{c.ExporterName}.*\"}} == 0",
            forDuration: "15m",
            severity: "warning",
            summary: $"No metrics from \"{c.Name}\"",
            description: "The exporter is not being scraped, so every other rule here is quiet for the wrong "
                + "reason. Silence from a monitor is not the same as health.");

        return sb.ToString();
    }

    private static void AppendRule(
        StringBuilder sb, ElasticsearchCluster c, string alert, string expr, string forDuration,
        string severity, string summary, string description)
    {
        sb.AppendLine($"        - alert: {alert}");
        sb.AppendLine($"          expr: {expr}");
        sb.AppendLine($"          for: {forDuration}");
        sb.AppendLine("          labels:");
        sb.AppendLine($"            severity: {severity}");
        // Which cluster, in labels rather than only in prose: an incident that cannot be traced back
        // to one cluster is a page nobody can act on.
        sb.AppendLine($"            elasticsearch_cluster: {c.Name}");
        sb.AppendLine($"            namespace: {c.Namespace}");
        sb.AppendLine("          annotations:");
        sb.AppendLine($"            summary: \"{YamlQuoted(summary)}\"");
        sb.AppendLine($"            description: \"{YamlQuoted(description)}\"");
    }

    /// <summary>
    /// A percentage as PromQL wants it. Explicitly invariant: on a machine with a Swedish or German
    /// locale the default rendering is "0,85", which Prometheus rejects as a syntax error at rule
    /// load time — on that machine only, which is the worst way to find out.
    /// </summary>
    private static string Threshold(int percent) =>
        (percent / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Escapes a value for a double-quoted YAML scalar. The alert prose names the cluster in quotes,
    /// and an unescaped quote ends the scalar early — producing a manifest that parses as something
    /// else entirely, or not at all.
    /// </summary>
    private static string YamlQuoted(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>The exporter account's password Secret.</summary>
    public static string BuildExporterCredentialsSecret(ElasticsearchCluster c, string password)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.ExporterSecretName}");
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
    /// The exporter's Elasticsearch account: cluster <c>monitor</c>, and <c>monitor</c> on all
    /// indices so index-level collectors work if they are turned on. Everything it reads is a
    /// read-only operation, which is the whole of what this credential can ever do.
    /// </summary>
    public static string BuildExporterUserScript(ElasticsearchCluster c)
    {
        string roleBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cluster"] = new[] { "monitor" },
            ["indices"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["names"] = new[] { "*" },
                    ["privileges"] = new[] { "monitor" },
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
        sb.AppendLine($"put \"/_security/role/entkube-exporter\" /tmp/role.json");
        sb.AppendLine();
        sb.AppendLine("umask 077");
        sb.AppendLine("cat > /tmp/user.json <<ENTKUBE_EOF");
        sb.AppendLine("{");
        sb.AppendLine("  \"password\": \"${ES_USER_PASSWORD}\",");
        sb.AppendLine("  \"roles\": [\"entkube-exporter\"],");
        sb.AppendLine("  \"full_name\": \"EntKube metrics exporter\"");
        sb.AppendLine("}");
        sb.AppendLine("ENTKUBE_EOF");
        sb.AppendLine();
        sb.AppendLine($"put \"/_security/user/{c.ExporterUsername}\" /tmp/user.json");
        sb.AppendLine("rm -f /tmp/user.json");
        return sb.ToString();
    }

    /// <summary>
    /// The exporter itself. It verifies the cluster's certificate against the operator's CA — it is
    /// talking across the network like any other client — and is given explicit, small resources,
    /// because a metrics sidecar that competes with the data nodes it measures is worse than none.
    /// </summary>
    public static string BuildExporterDeployment(ElasticsearchCluster c)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.ExporterName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        sb.AppendLine($"    app.kubernetes.io/name: {c.ExporterName}");
        sb.AppendLine("spec:");
        sb.AppendLine("  replicas: 1");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine($"      app.kubernetes.io/name: {c.ExporterName}");
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.AppendLine($"        app.kubernetes.io/name: {c.ExporterName}");
        sb.AppendLine("    spec:");
        sb.AppendLine("      securityContext:");
        sb.AppendLine("        runAsNonRoot: true");
        sb.AppendLine("        runAsUser: 1000");
        sb.AppendLine("        seccompProfile:");
        sb.AppendLine("          type: RuntimeDefault");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: es-ca");
        sb.AppendLine("          secret:");
        sb.AppendLine($"            secretName: {c.HttpCertsSecretName}");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: exporter");
        sb.AppendLine($"          image: {ExporterImage}");
        sb.AppendLine("          args:");
        sb.AppendLine($"            - \"--es.uri={c.HttpEndpoint}\"");
        sb.AppendLine("            - \"--es.ca=/es-ca/ca.crt\"");
        sb.AppendLine("            - \"--es.all\"");
        if (c.MonitoringIndexMetrics)
        {
            // Opt-in: every index becomes its own set of series, and the exporter's own README warns
            // that this asks the master nodes for /_all/_stats on every scrape.
            sb.AppendLine("            - \"--es.indices\"");
            sb.AppendLine("            - \"--es.indices_settings\"");
        }
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: ES_USERNAME");
        sb.AppendLine($"              value: {c.ExporterUsername}");
        sb.AppendLine("            - name: ES_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine($"                  name: {c.ExporterSecretName}");
        sb.AppendLine("                  key: password");
        sb.AppendLine("          ports:");
        sb.AppendLine($"            - name: metrics");
        sb.AppendLine($"              containerPort: {ExporterPort}");
        sb.AppendLine("          resources:");
        sb.AppendLine("            requests:");
        sb.AppendLine($"              cpu: {c.MonitoringCpuRequest}");
        sb.AppendLine($"              memory: {c.MonitoringMemory}");
        sb.AppendLine("            limits:");
        sb.AppendLine($"              memory: {c.MonitoringMemory}");
        sb.AppendLine("          securityContext:");
        sb.AppendLine("            allowPrivilegeEscalation: false");
        sb.AppendLine("            readOnlyRootFilesystem: true");
        sb.AppendLine("            capabilities:");
        sb.AppendLine("              drop: [\"ALL\"]");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: es-ca");
        sb.AppendLine("              mountPath: /es-ca");
        sb.AppendLine("              readOnly: true");
        return sb.ToString();
    }

    public static string BuildExporterService(ElasticsearchCluster c)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.ExporterName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    app.kubernetes.io/name: {c.ExporterName}");
        sb.AppendLine("spec:");
        sb.AppendLine("  selector:");
        sb.AppendLine($"    app.kubernetes.io/name: {c.ExporterName}");
        sb.AppendLine("  ports:");
        sb.AppendLine("    - name: metrics");
        sb.AppendLine($"      port: {ExporterPort}");
        sb.AppendLine($"      targetPort: {ExporterPort}");
        return sb.ToString();
    }

    /// <summary>
    /// The ServiceMonitor, carrying whatever labels the live Prometheus selects on. Stamping them
    /// here is the difference between metrics that appear and metrics that silently do not: a
    /// monitor Prometheus does not select is not an error anywhere, it is just never scraped.
    /// </summary>
    public static string BuildExporterServiceMonitor(
        ElasticsearchCluster c, IReadOnlyDictionary<string, string> selectorLabels)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: monitoring.coreos.com/v1");
        sb.AppendLine("kind: ServiceMonitor");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {c.ExporterName}");
        sb.AppendLine($"  namespace: {c.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine($"    entkube.io/elasticsearch: {c.Name}");
        foreach ((string key, string value) in selectorLabels.OrderBy(kv => kv.Key))
            sb.AppendLine($"    {key}: \"{value}\"");
        sb.AppendLine("spec:");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine($"      app.kubernetes.io/name: {c.ExporterName}");
        sb.AppendLine("  endpoints:");
        sb.AppendLine("    - port: metrics");
        // The exporter asks Elasticsearch for everything on each scrape, so a short interval turns
        // monitoring into load on the master nodes — its own README says as much.
        sb.AppendLine("      interval: 60s");
        sb.AppendLine("      scrapeTimeout: 30s");
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
        yield return ("secret", c.ExporterSecretName);
        yield return ("configmap", SnapshotConfigMapName(c, "exporter-user"));
        yield return ("prometheusrule", c.ExporterName);
        yield return ("servicemonitor", c.ExporterName);
        yield return ("service", c.ExporterName);
        yield return ("deployment", c.ExporterName);
    }

    /// <summary>The password Secret and scripts of each application user on this cluster.</summary>
    private static IEnumerable<(string Kind, string Name)> UserLeftovers(ElasticsearchCluster c)
    {
        foreach (ElasticsearchUser user in c.Users)
        {
            yield return ("secret", user.CredentialsSecretName);
            yield return ("configmap", UserConfigMapName(c, user));
            yield return ("configmap", UserConfigMapName(c, user, "delete"));
            yield return ("configmap", UserConfigMapName(c, user, "password"));
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
        ElasticsearchCluster cluster, string jobName, Clusters.IClusterClient client,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < JobPollAttempts; attempt++)
        {
            await Task.Delay(JobPollInterval, ct);

            string json;
            try
            {
                json = await client.GetJsonAsync($"job/{jobName}", cluster.Namespace, ct: ct);
            }
            catch { continue; }

            (bool finished, bool succeeded) = ParseJobCompletion(json);
            if (!finished) continue;

            string log = "";
            try
            {
                log = await client.GetPodLogsAsync($"job/{jobName}", cluster.Namespace, 100, ct);
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
