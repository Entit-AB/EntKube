namespace EntKube.Telemetry;

/// <summary>
/// Maps a cluster to the tenant that owns it. The engine's query surfaces are addressed by cluster
/// (that is what the UI knows) but its storage is partitioned by tenant, so every read starts here.
///
/// The second seam, alongside <see cref="ISegmentCatalog"/>, between the engine and its host. In the
/// management plane this is a cached lookup against the operational database. Inside a managed cluster
/// there is exactly one cluster and one tenant, both known from configuration at startup, so the
/// implementation is a constant — see <see cref="FixedClusterTenantResolver"/>.
/// </summary>
public interface IClusterTenantResolver
{
    /// <summary>The cluster's owning tenant, or null when the cluster is unknown.</summary>
    Task<Guid?> ResolveAsync(Guid clusterId, CancellationToken ct = default);
}

/// <summary>
/// The single-tenant resolver used when the engine runs inside a managed cluster: the pod serves exactly
/// one cluster for exactly one tenant, both injected at startup. Any other cluster id is not "unknown" so
/// much as "not mine", and answering null makes the query return empty rather than quietly reading
/// another cluster's data.
/// </summary>
public sealed class FixedClusterTenantResolver(Guid clusterId, Guid tenantId) : IClusterTenantResolver
{
    public Task<Guid?> ResolveAsync(Guid requestedClusterId, CancellationToken ct = default)
        => Task.FromResult(requestedClusterId == clusterId ? tenantId : (Guid?)null);
}
