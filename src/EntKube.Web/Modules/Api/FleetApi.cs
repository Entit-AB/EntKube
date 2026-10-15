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

    public async Task<bool> IsInMaintenanceAsync(
        Guid tenantId, Guid clusterId, DateTime at, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // The predicate its four callers each wrote out, including the null-cluster clause that
        // makes a window cover everything the tenant has.
        return await db.MaintenanceWindows
            .AsNoTracking()
            .AnyAsync(w => w.TenantId == tenantId
                && w.StartsAt <= at
                && w.EndsAt >= at
                && (w.ClusterId == null || w.ClusterId == clusterId), ct);
    }

    public async Task<IReadOnlyList<MaintenanceWindowSummary>> GetActiveMaintenanceWindowsAsync(
        Guid tenantId, DateTime at, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.MaintenanceWindows
            .AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.StartsAt <= at && w.EndsAt >= at)
            .OrderBy(w => w.StartsAt)
            .Select(ToWindow)
            .ToListAsync(ct);
    }

    private static readonly Expression<Func<Data.MaintenanceWindow, MaintenanceWindowSummary>> ToWindow =
        w => new MaintenanceWindowSummary(w.Id, w.ClusterId, w.StartsAt, w.EndsAt);

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
            // ⚠️ The (int) cast is safe HERE and only here: ProvisioningStatus has no
            // HasConversion, so the column really is an integer. Copying this onto a
            // string-stored enum is what produced the Cost page's 22P02 — Postgres is asked to
            // CAST('HelmChart' AS integer) while SQLite silently answers 0. Project the entity
            // enum and map in memory instead; see DeliveryApi.ToRow.
            (ClusterProvisioningStatus)(int)c.ProvisioningStatus);
}
