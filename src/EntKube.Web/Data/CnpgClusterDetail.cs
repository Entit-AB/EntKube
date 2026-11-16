namespace EntKube.Web.Data;

/// <summary>
/// A snapshot of a CNPG cluster's live state including pod information,
/// overall health, and timeline details queried from Kubernetes.
/// Used by the UI to show real-time status beyond what's stored in the database.
/// </summary>
public class CnpgClusterDetail
{
    /// <summary>
    /// The managed cluster record from the database.
    /// </summary>
    public required CnpgCluster Cluster { get; set; }

    /// <summary>
    /// Live pod information from Kubernetes.
    /// </summary>
    public List<CnpgPodInfo> Pods { get; set; } = [];

    /// <summary>
    /// Whether the live Cluster CR differs from the manifest EntKube would generate for it, and
    /// how.
    ///
    /// <para><b>Why this is reported rather than repaired.</b> Opening this panel used to
    /// <i>apply</i> the generated manifest, so that a configuration change shipped in a new
    /// EntKube release reached the cluster the next time somebody looked at it. An apply is a
    /// change, the change gate asks before every one, and so expanding a Postgres cluster raised
    /// an acknowledgment dialog — for a read. Worse, it was fire-and-forget, so the write
    /// outlived the request that started it and its failures went nowhere.</para>
    ///
    /// <para>So the drift is <em>detected</em> here, by server-side dry run, which writes nothing
    /// and asks nothing — and re-applying is an action the operator takes, with one dialog, when
    /// they can see what it would change.</para>
    /// </summary>
    public EntKube.Web.Services.Clusters.DiffOutcome ConfigurationDrift { get; set; }
        = EntKube.Web.Services.Clusters.DiffOutcome.Unknown;

    /// <summary>The drift itself, when <see cref="ConfigurationDrift"/> is Differs.</summary>
    public string? ConfigurationDriftText { get; set; }

    /// <summary>
    /// The storage the cluster actually has — the Cluster spec, every claim and its volume —
    /// read live. Null when the cluster could not be reached; the page then shows the stored
    /// size and says it is unverified.
    /// </summary>
    public EntKube.Web.Services.CnpgStorageSnapshot? Storage { get; set; }

    /// <summary>
    /// The CNPG cluster phase as reported by the Cluster status (e.g. "Cluster in healthy state").
    /// </summary>
    public string Phase { get; set; } = "Unknown";

    /// <summary>
    /// Number of instances that are currently ready.
    /// </summary>
    public int ReadyInstances { get; set; }

    /// <summary>
    /// Current primary pod name.
    /// </summary>
    public string? CurrentPrimary { get; set; }

    /// <summary>
    /// Current WAL timeline (indicates how many failovers/restores have occurred).
    /// </summary>
    public int? CurrentTimeline { get; set; }

    /// <summary>
    /// The current write LAG in bytes across replicas (0 = fully caught up).
    /// </summary>
    public long ReplicationLagBytes { get; set; }

    /// <summary>
    /// Live-synced backup list reconciled from K8s Backup CRs plus the DB records.
    /// Populated by GetClusterDetailAsync; falls back to Cluster.Backups if sync is skipped.
    /// </summary>
    public List<CnpgBackup> Backups { get; set; } = [];
}

/// <summary>
/// Information about a single pod in a CNPG cluster, including its role
/// (primary or replica), status, and resource usage.
/// </summary>
public class CnpgPodInfo
{
    /// <summary>
    /// The pod name (e.g. "my-cluster-1", "my-cluster-2").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The role of this pod: "primary" or "replica".
    /// </summary>
    public required string Role { get; set; }

    /// <summary>
    /// The Kubernetes pod phase (Running, Pending, Succeeded, Failed, Unknown).
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// Whether all containers in the pod are ready.
    /// </summary>
    public bool Ready { get; set; }

    /// <summary>
    /// The node this pod is scheduled on.
    /// </summary>
    public string? Node { get; set; }

    /// <summary>
    /// When the pod started.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// For replicas: replication lag in bytes behind the primary.
    /// </summary>
    public long? ReplicationLagBytes { get; set; }

    /// <summary>
    /// Pod restart count (sum of all container restarts).
    /// </summary>
    public int Restarts { get; set; }
}
