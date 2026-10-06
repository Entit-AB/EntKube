using EntKube.Contracts.Fleet;
using EntKube.Web.Data.Modules;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Fleet's implementation of <see cref="IFleetApi"/>, over its own module context.
///
/// <para>The projection deliberately never touches <c>Kubeconfig</c>. It is resolved from
/// the vault on materialisation, so merely selecting it would decrypt a credential for a
/// caller that only wanted a cluster's name.</para>
/// </summary>
public sealed class FleetApi(IDbContextFactory<FleetDbContext> dbFactory) : IFleetApi
{
    public async Task<ClusterSummary?> GetClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Where(c => c.Id == clusterId).Select(ToContract).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Select(ToContract).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClusterSummary>> GetClustersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clusterIds, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Where(c => clusterIds.Contains(c.Id)).Select(ToContract).ToListAsync(ct);
    }

    private static IQueryable<Data.KubernetesCluster> Scoped(FleetDbContext db, Guid tenantId)
        => db.KubernetesClusters.AsNoTracking().Where(c => c.TenantId == tenantId);

    /// <summary>Applied after any filter — see CatalogApi for why the order matters.</summary>
    private static readonly Expression<Func<Data.KubernetesCluster, ClusterSummary>> ToContract =
        c => new ClusterSummary(
            c.Id,
            c.TenantId,
            c.EnvironmentId,
            c.Environment.Name,
            c.Name,
            c.ApiServerUrl,
            (ClusterProvisioningStatus)(int)c.ProvisioningStatus);
}
