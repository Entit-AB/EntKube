namespace EntKube.Contracts.Fleet;

/// <summary>How far along a cluster's provisioning is.</summary>
public enum ClusterProvisioningStatus
{
    Registered,
    Provisioning,
    Provisioned,
    Failed,
    Deleting,
}

/// <summary>
/// A registered cluster, as everyone outside Fleet sees it.
///
/// <para><b>No kubeconfig.</b> The entity carries one, resolved from the vault on
/// materialisation; this record deliberately does not. Talking to a cluster is Fleet's job,
/// and a contract that handed out credentials would make every caller a potential route to
/// the API server.</para>
/// </summary>
/// <param name="Id">The cluster's id.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="EnvironmentId">Which environment it belongs to.</param>
/// <param name="EnvironmentName">That environment's name, to save a second lookup.</param>
/// <param name="Name">The cluster's name.</param>
/// <param name="ApiServerUrl">Its API server address — identifying, not a credential.</param>
/// <param name="ProvisioningStatus">How far along provisioning is.</param>
public sealed record ClusterSummary(
    Guid Id,
    Guid TenantId,
    Guid EnvironmentId,
    string EnvironmentName,
    string Name,
    string ApiServerUrl,
    ClusterProvisioningStatus ProvisioningStatus);

/// <summary>
/// What Fleet offers the rest of EntKube.
///
/// <para>Lookup only. Thirty-one services read this table across module boundaries and the
/// single write was tenant deletion, which belongs to the tenant purge rather than here.
/// Provisioning, applying and deleting clusters stay inside Fleet.</para>
/// </summary>
/// <summary>
/// A window during which a cluster's alerts are expected to be noisy, as everyone outside Fleet
/// sees it.
/// </summary>
/// <summary>
/// Whether a maintenance window owed the customer notice.
///
/// <para>Mirrors <c>Data.MaintenanceKind</c> member for member. <c>ApiMaintenanceKindTests</c>
/// holds the two together: the entity's copy is stored as an <b>int</b>, so inserting a member in
/// the middle of one enum and not the other would silently relabel every stored row — a planned
/// window reported as an emergency owes no notice, and the short-notice list would quietly empty
/// itself.</para>
/// </summary>
public enum MaintenanceKind
{
    /// <summary>Arranged in advance, and therefore owed notice.</summary>
    Planned = 0,

    /// <summary>Something had to be done at once. No notice is owed.</summary>
    Emergency = 1,
}

/// <param name="Id">The window's id.</param>
/// <param name="ClusterId">
/// The cluster it covers, or null for every cluster the tenant has — which is the clause each of
/// the four callers wrote out by hand as <c>ClusterId == null || ClusterId == clusterId</c>.
/// </param>
/// <param name="StartsAt">When it opens (UTC).</param>
/// <param name="EndsAt">When it closes (UTC).</param>
/// <param name="Title">What the window is for. Read by the monthly report.</param>
/// <param name="AnnouncedAt">
/// When the customer was told, <b>already resolved</b>: the entity's <c>AnnouncedAt</c> when
/// someone recorded one — for a window entered after the customer was already told — and otherwise
/// the moment the window was created, which is the only announcement the system can vouch for.
///
/// <para>That <c>?? CreatedAt</c> fallback is a rule about what counts as an announcement, and it
/// is applied once here rather than carried as two nullable fields for every consumer to combine.
/// Two fields would let a caller read <c>CreatedAt</c> and call it the announcement, which is the
/// same value by a different name only when nobody recorded one.</para>
/// </param>
/// <param name="CreatedBy">Who recorded it. Read by the monthly report's short-notice list.</param>
/// <param name="Kind">Emergency maintenance owes no notice; planned maintenance does.</param>
/// <remarks>
/// It carried four fields, because four were read. It carries eight now, because the monthly
/// report reads eight — and the four it gained are the four the first draft was <em>refused</em>
/// for carrying "for nobody". That is the rule working in both directions rather than being
/// bent: a field needs a reader, and these now have one. The three suppression callers and the
/// alert evaluator still read only the original four and simply receive a wider row.
///
/// <para>The entity's <c>Description</c> is still absent. Nothing reads it.</para>
/// </remarks>
public sealed record MaintenanceWindowSummary(
    Guid Id,
    Guid? ClusterId,
    DateTime StartsAt,
    DateTime EndsAt,
    string Title,
    DateTime AnnouncedAt,
    string CreatedBy,
    MaintenanceKind Kind);

public interface IFleetApi
{
    /// <summary>
    /// Whether this cluster's alerting is inside a maintenance window at <paramref name="at"/>.
    ///
    /// <para><b>Why this is on the contract.</b> Four services outside Fleet asked this question by
    /// querying <c>MaintenanceWindows</c> directly — Telemetry's alert sync, its incident
    /// dispatcher, its rule evaluator and its incident service — and three of them wrote the
    /// identical predicate, including the <c>ClusterId == null</c> clause that makes a window
    /// tenant-wide. A table read in four places with one meaning is the shape that belongs behind a
    /// method.</para>
    ///
    /// <para><paramref name="at"/> is a parameter rather than <c>UtcNow</c> so the decision is
    /// testable and so a caller holding one timestamp for a whole sweep cannot drift mid-pass.</para>
    /// </summary>
    Task<bool> IsInMaintenanceAsync(
        Guid tenantId, Guid clusterId, DateTime at, CancellationToken ct = default);

    /// <summary>
    /// The tenant's windows open at <paramref name="at"/>.
    ///
    /// <para>For the one caller that tests many clusters against the same instant: the alert
    /// evaluator holds the set for a whole pass rather than asking per cluster. It is per tenant
    /// on purpose — the evaluator loads it <em>inside</em> its tenant loop, so the tenant stays an
    /// input to the question rather than a filter applied to everyone's rows afterwards
    /// (§4.0.1.2).</para>
    /// </summary>
    /// <summary>
    /// Every window of a tenant's that <b>overlaps</b> an interval, oldest first.
    ///
    /// <para>The two methods beside this one answer about an <em>instant</em> — "is this cluster
    /// in maintenance now" — which is the question a suppression check asks. A report over a month
    /// cannot be assembled from those: a window that opened before the month began and closed
    /// inside it is not active at any instant the report cares to name, and it still excludes the
    /// downtime it covered. Overlap is <c>StartsAt &lt; to &amp;&amp; EndsAt &gt; from</c>, and
    /// stating it once here is better than the four hand-written copies that preceded
    /// <see cref="IsInMaintenanceAsync"/>.</para>
    ///
    /// <para>It does not take a cluster filter. A window with a null cluster covers the whole
    /// tenant, so a caller interested in some clusters still needs those rows, and the set for one
    /// month is small enough that narrowing it is the caller's business — the alternative is a
    /// parameter whose only correct value is "and also the nulls".</para>
    /// </summary>
    Task<IReadOnlyList<MaintenanceWindowSummary>> GetMaintenanceWindowsOverlappingAsync(
        Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default);

    Task<IReadOnlyList<MaintenanceWindowSummary>> GetActiveMaintenanceWindowsAsync(
        Guid tenantId, DateTime at, CancellationToken ct = default);

    /// <summary>One cluster, or null when it is not this tenant's.</summary>
    Task<ClusterSummary?> GetClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <summary>Every cluster the tenant has.</summary>
    Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>A named subset, for callers holding a list of ids.</summary>
    Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clusterIds, CancellationToken ct = default);

    /// <summary>
    /// The KubeVirt VMs on one cluster, read live. Null when the cluster is not this tenant's — a list
    /// with <see cref="VirtualMachineList.Error"/> set means the cluster is but could not be read.
    /// </summary>
    Task<VirtualMachineList?> GetVirtualMachinesAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <summary>
    /// Starts, stops, restarts, pauses, unpauses or live-migrates one VM, attributed to
    /// <paramref name="performedBy"/>. The caller has declared how the change is acknowledged.
    /// </summary>
    Task<VirtualMachineActionOutcome> ActOnVirtualMachineAsync(
        Guid tenantId, Guid clusterId, string ns, string name, VirtualMachineAction action, string performedBy,
        CancellationToken ct = default);
}
