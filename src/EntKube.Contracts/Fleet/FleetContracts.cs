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
public interface IFleetApi
{
    /// <summary>One cluster, or null when it is not this tenant's.</summary>
    Task<ClusterSummary?> GetClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <summary>Every cluster the tenant has.</summary>
    Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>A named subset, for callers holding a list of ids.</summary>
    Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clusterIds, CancellationToken ct = default);
}
