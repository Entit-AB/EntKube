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
/// <param name="Id">The window's id.</param>
/// <param name="ClusterId">
/// The cluster it covers, or null for every cluster the tenant has — which is the clause each of
/// the four callers wrote out by hand as <c>ClusterId == null || ClusterId == clusterId</c>.
/// </param>
/// <param name="StartsAt">When it opens (UTC).</param>
/// <param name="EndsAt">When it closes (UTC).</param>
/// <remarks>
/// Four fields, because four fields are read. The entity also carries a title, a description and
/// a creator, and the first draft of this record carried two of them — for nobody: the three
/// suppression callers need only the answer and the evaluator needs only
/// <paramref name="ClusterId"/>. A field on a shared DTO has to have a reader, or the contract
/// becomes a second name for the table (see <c>DeploymentSummary.CustomerName</c>, refused for the
/// same reason). The page that renders windows still reads the entity; when it moves, it brings
/// the fields it displays with it.
/// </remarks>
public sealed record MaintenanceWindowSummary(
    Guid Id,
    Guid? ClusterId,
    DateTime StartsAt,
    DateTime EndsAt);

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
    Task<IReadOnlyList<MaintenanceWindowSummary>> GetActiveMaintenanceWindowsAsync(
        Guid tenantId, DateTime at, CancellationToken ct = default);

    /// <summary>One cluster, or null when it is not this tenant's.</summary>
    Task<ClusterSummary?> GetClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <summary>Every cluster the tenant has.</summary>
    Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>A named subset, for callers holding a list of ids.</summary>
    Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clusterIds, CancellationToken ct = default);
}
