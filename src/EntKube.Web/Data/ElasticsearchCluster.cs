namespace EntKube.Web.Data;

public enum ElasticsearchClusterStatus
{
    Creating,
    Running,
    Updating,
    Failed,
    Deleting
}

/// <summary>
/// A managed Elasticsearch cluster (plus its Kibana) provisioned through the ECK operator.
/// EntKube applies an <c>Elasticsearch</c> CR whose nodeSets are one per role tier, and a
/// <c>Kibana</c> CR pointed at it.
///
/// <para><b>Why the topology is modelled as tiers rather than "a node count".</b> An
/// Elasticsearch node that is master, data and ingest at once is the default the quickstart
/// gives you, and it is also the one that takes the cluster down: a heavy ingest pipeline or a
/// runaway aggregation on a node that also holds the cluster state stalls master elections, and
/// the cluster goes red for reasons that have nothing to do with the data. Splitting the roles
/// across nodeSets — dedicated master-eligible nodes, data_hot/data_warm/data_cold tiers, and
/// optionally dedicated ingest+coordinating nodes — is what makes the failure local.</para>
///
/// <para><b>Why every tier carries explicit resources.</b> ECK will happily start a nodeSet with
/// no resources declared, and the JVM then sizes its heap from whatever the container was given.
/// Every tier here has a memory figure, the heap is derived from it (half, never above the 31Gi
/// compressed-oops boundary), and the total is checked against the Kubernetes cluster's free
/// allocatable capacity before anything is applied. A search cluster is the single easiest way to
/// eat a Kubernetes cluster whole, and it usually does it by being scheduled successfully.</para>
/// </summary>
public class ElasticsearchCluster
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KubernetesClusterId { get; set; }

    /// <summary>The Elasticsearch resource name (metadata.name). Lowercase, DNS-safe.</summary>
    public required string Name { get; set; }

    public required string Namespace { get; set; }

    /// <summary>Elastic Stack version applied to both Elasticsearch and Kibana (e.g. "9.5.0").</summary>
    public string Version { get; set; } = "9.5.0";

    // ── Master tier ────────────────────────────────────────────────────────────
    // Also the all-roles node set when no data tier is enabled (the single-node case).

    /// <summary>Master-eligible node count. Must be odd — an even count buys no extra quorum.</summary>
    public int MasterCount { get; set; } = 3;

    public string MasterCpuRequest { get; set; } = "500m";

    /// <summary>Memory request and limit for each master node. Heap is derived from it.</summary>
    public string MasterMemory { get; set; } = "2Gi";

    /// <summary>PVC size per master. Masters hold cluster state, not data — small is correct.</summary>
    public string MasterStorageSize { get; set; } = "5Gi";

    // ── Hot tier (data_hot + data_content) ─────────────────────────────────────

    public int HotCount { get; set; } = 2;

    public string HotCpuRequest { get; set; } = "1";

    public string HotMemory { get; set; } = "4Gi";

    public string HotStorageSize { get; set; } = "100Gi";

    // ── Warm tier (data_warm). Count 0 disables it. ────────────────────────────

    public int WarmCount { get; set; }

    public string WarmCpuRequest { get; set; } = "500m";

    public string WarmMemory { get; set; } = "4Gi";

    public string WarmStorageSize { get; set; } = "200Gi";

    // ── Cold tier (data_cold). Count 0 disables it. ────────────────────────────

    public int ColdCount { get; set; }

    public string ColdCpuRequest { get; set; } = "500m";

    public string ColdMemory { get; set; } = "2Gi";

    public string ColdStorageSize { get; set; } = "500Gi";

    // ── Dedicated ingest + coordinating tier. Count 0 keeps ingest on the hot tier. ──

    /// <summary>
    /// Dedicated ingest/coordinating node count. Zero leaves the <c>ingest</c> role on the hot
    /// tier, which is the right answer until ingest pipelines are heavy enough to compete with
    /// indexing for the same CPU.
    /// </summary>
    public int IngestCount { get; set; }

    public string IngestCpuRequest { get; set; } = "1";

    public string IngestMemory { get; set; } = "2Gi";

    /// <summary>
    /// PVC size for ingest/coordinating nodes. They hold no shards, but Elasticsearch still wants
    /// a data path, so this stays small.
    /// </summary>
    public string IngestStorageSize { get; set; } = "1Gi";

    // ── Shared placement / storage settings ────────────────────────────────────

    /// <summary>Optional StorageClass for every tier's PVCs. Null uses the cluster default.</summary>
    public string? StorageClass { get; set; }

    /// <summary>
    /// Whether the nodes may use memory-mapped directories. Elasticsearch needs
    /// <c>vm.max_map_count=262144</c> on the host for that; where the host cannot be tuned
    /// (managed node pools, hardened images) turning this off sets <c>node.store.allow_mmap:
    /// false</c> so the pod starts at all, at some cost to search performance.
    /// </summary>
    public bool AllowMmap { get; set; } = true;

    /// <summary>
    /// Spread each tier's nodes across topology zones and make Elasticsearch shard-aware of them.
    /// Only meaningful on a cluster whose nodes actually carry <c>topology.kubernetes.io/zone</c>.
    /// </summary>
    public bool ZoneAware { get; set; }

    // ── Kibana ─────────────────────────────────────────────────────────────────

    public bool KibanaEnabled { get; set; } = true;

    public int KibanaCount { get; set; } = 1;

    public string KibanaCpuRequest { get; set; } = "500m";

    public string KibanaMemory { get; set; } = "1Gi";

    // ── Snapshots (S3 repository + SLM policy) ─────────────────────────────────

    /// <summary>
    /// Whether this cluster keeps snapshots in object storage. Off by default, and worth turning on
    /// before the lifecycle policies start deleting anything: ILM's delete phase is not a mistake it
    /// can take back.
    /// </summary>
    public bool SnapshotsEnabled { get; set; }

    /// <summary>The tenant storage link (S3 bucket + credentials) snapshots are written to.</summary>
    public Guid? SnapshotStorageLinkId { get; set; }

    /// <summary>Prefix inside the bucket. Defaults to the cluster name, so two clusters can share one.</summary>
    public string? SnapshotBasePath { get; set; }

    /// <summary>
    /// When snapshots are taken, as an Elasticsearch (Quartz-style, six field) cron expression.
    /// The default is 01:30 daily.
    /// </summary>
    public string SnapshotScheduleCron { get; set; } = "0 30 1 * * ?";

    /// <summary>Delete snapshots older than this many days, subject to the counts below.</summary>
    public int SnapshotExpireAfterDays { get; set; } = 30;

    /// <summary>Keep at least this many snapshots even once they are older than the expiry.</summary>
    public int SnapshotMinCount { get; set; } = 5;

    /// <summary>Never keep more than this many.</summary>
    public int SnapshotMaxCount { get; set; } = 50;

    /// <summary>When the last successful snapshot completed, as Elasticsearch last reported it.</summary>
    public DateTime? SnapshotLastSuccessAt { get; set; }

    /// <summary>The name of that snapshot.</summary>
    public string? SnapshotLastSuccessName { get; set; }

    /// <summary>What the last failed snapshot said, when the last attempt failed.</summary>
    public string? SnapshotLastFailure { get; set; }

    /// <summary>When EntKube last read the snapshot state out of the cluster.</summary>
    public DateTime? SnapshotLastCheckedAt { get; set; }

    // ── Last live reading (written by the poller, read by the advisor) ─────────

    /// <summary>
    /// The fullest node's disk usage, as a percentage. Elasticsearch stops allocating shards to a
    /// node at 85% and makes every index with a shard on it read-only at 95%, so this number going
    /// up is the cluster's most common way of stopping.
    /// </summary>
    public int? HighestNodeDiskPercent { get; set; }

    /// <summary>Shards Elasticsearch cannot place. Non-zero is why a cluster is yellow or red.</summary>
    public int? UnassignedShards { get; set; }

    /// <summary>
    /// When the two figures above were last read. It not moving is what tells the advisor they are
    /// stale rather than good news.
    /// </summary>
    public DateTime? InsightCheckedAt { get; set; }

    // ── Metrics (Prometheus exporter) ──────────────────────────────────────────

    /// <summary>
    /// Whether an Elasticsearch exporter runs beside the cluster, so its metrics land in the
    /// Prometheus already scraping everything else rather than only in Kibana.
    /// </summary>
    public bool MonitoringEnabled { get; set; }

    /// <summary>
    /// Also export per-index metrics. Off by default and deliberately so: every index becomes its
    /// own set of series, and a cluster that rolls an index over daily turns that into unbounded
    /// growth in Prometheus rather than in Elasticsearch.
    /// </summary>
    public bool MonitoringIndexMetrics { get; set; }

    public string MonitoringCpuRequest { get; set; } = "50m";

    public string MonitoringMemory { get; set; } = "128Mi";

    /// <summary>What the ServiceMonitor was last labelled with, and whether Prometheus accepted it.</summary>
    public string? MonitoringSelectorNote { get; set; }

    // ── State ──────────────────────────────────────────────────────────────────

    public ElasticsearchClusterStatus Status { get; set; } = ElasticsearchClusterStatus.Creating;

    /// <summary>Cluster health as the ECK operator last reported it: green, yellow, red or unknown.</summary>
    public string? Health { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public ICollection<ElasticsearchIlmPolicy> IlmPolicies { get; set; } = [];
    public ICollection<ElasticsearchUser> Users { get; set; } = [];
    public ICollection<ElasticsearchKibanaSpace> KibanaSpaces { get; set; } = [];
    public ICollection<ElasticsearchIngestPipeline> IngestPipelines { get; set; } = [];

    // ── Derived names (all fixed by ECK's own conventions) ─────────────────────

    public const int HttpPort = 9200;
    public const int KibanaPort = 5601;

    /// <summary>The Service ECK creates for the HTTP layer.</summary>
    public string HttpServiceName => $"{Name}-es-http";

    /// <summary>In-cluster HTTPS endpoint clients should use.</summary>
    public string HttpEndpoint => $"https://{HttpServiceName}.{Namespace}.svc:{HttpPort}";

    /// <summary>Secret ECK generates for the built-in <c>elastic</c> superuser (key: "elastic").</summary>
    public string ElasticUserSecretName => $"{Name}-es-elastic-user";

    /// <summary>Secret carrying the public CA of the HTTP layer (key: "ca.crt").</summary>
    public string HttpCertsSecretName => $"{Name}-es-http-certs-public";

    /// <summary>The exporter's own Elasticsearch account — read-only, and nothing to do with the apps'.</summary>
    public string ExporterUsername => $"{Name}-exporter";

    /// <summary>Secret holding the exporter account's password.</summary>
    public string ExporterSecretName => $"{Name}-es-exporter";

    /// <summary>Deployment, Service and ServiceMonitor all share this name.</summary>
    public string ExporterName => $"{Name}-es-exporter";

    /// <summary>The Kibana CR shares the Elasticsearch name; ECK suffixes its Service itself.</summary>
    public string KibanaServiceName => $"{Name}-kb-http";

    /// <summary>In-cluster HTTPS endpoint for Kibana's own API.</summary>
    public string KibanaEndpoint => $"https://{KibanaServiceName}.{Namespace}.svc:{KibanaPort}";

    /// <summary>Secret carrying the public CA of Kibana's HTTP layer (key: "ca.crt").</summary>
    public string KibanaCertsSecretName => $"{Name}-kb-http-certs-public";

    /// <summary>True when at least one data tier is enabled — i.e. this is not a single all-roles node.</summary>
    public bool HasDataTiers => HotCount > 0 || WarmCount > 0 || ColdCount > 0;

    /// <summary>Secret holding the S3 keystore entries ECK loads into every node.</summary>
    public string SnapshotCredentialsSecretName => $"{Name}-es-snapshot-s3";

    /// <summary>The snapshot repository registered in Elasticsearch.</summary>
    public string SnapshotRepositoryName => "entkube-s3";

    /// <summary>The SLM policy that fills that repository.</summary>
    public string SnapshotPolicyName => $"{Name}-entkube-snapshots";
}
