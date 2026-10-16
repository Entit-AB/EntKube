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

        List<WindowRow> rows = await db.MaintenanceWindows
            .AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.StartsAt <= at && w.EndsAt >= at)
            .OrderBy(w => w.StartsAt)
            .Select(ToRow)
            .ToListAsync(ct);

        return [.. rows.Select(ToWindow)];
    }

    public async Task<IReadOnlyList<MaintenanceWindowSummary>> GetMaintenanceWindowsOverlappingAsync(
        Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Overlap, not containment: a window that opened before the interval and closed inside it
        // covered downtime the interval has to account for.
        List<WindowRow> rows = await db.MaintenanceWindows
            .AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.StartsAt < to && w.EndsAt > from)
            .OrderBy(w => w.StartsAt)
            .Select(ToRow)
            .ToListAsync(ct);

        return [.. rows.Select(ToWindow)];
    }

    /// <summary>
    /// The window's columns as the database has them, before the contract's two rules are applied.
    /// </summary>
    private sealed record WindowRow(
        Guid Id, Guid? ClusterId, DateTime StartsAt, DateTime EndsAt, string Title,
        DateTime? AnnouncedAt, DateTime CreatedAt, string CreatedBy, Data.MaintenanceKind Kind);

    private static readonly Expression<Func<Data.MaintenanceWindow, WindowRow>> ToRow =
        w => new WindowRow(
            w.Id, w.ClusterId, w.StartsAt, w.EndsAt, w.Title,
            w.AnnouncedAt, w.CreatedAt, w.CreatedBy, w.Kind);

    /// <summary>
    /// Applies the two things the contract promises that the row does not: the announcement is
    /// <c>AnnouncedAt ?? CreatedAt</c>, and the kind is the contract's enum.
    /// </summary>
    private static MaintenanceWindowSummary ToWindow(WindowRow r) =>
        new(r.Id, r.ClusterId, r.StartsAt, r.EndsAt, r.Title,
            r.AnnouncedAt ?? r.CreatedAt, r.CreatedBy, ToKind(r.Kind));

    /// <summary>
    /// Entity enum in, contract enum out, by an explicit switch rather than a cast.
    ///
    /// <para>This column is stored as an <c>int</c>, so a cast would work today and would keep
    /// working silently if a member were inserted into one enum and not the other — relabelling
    /// every stored row. The switch stops compiling instead, and
    /// <c>ApiMaintenanceKindTests</c> fails if the two drift. Same reason
    /// <c>RolloutVerdictStatus</c> is switched and not cast.</para>
    /// </summary>
    private static MaintenanceKind ToKind(Data.MaintenanceKind kind) => kind switch
    {
        Data.MaintenanceKind.Planned => MaintenanceKind.Planned,
        Data.MaintenanceKind.Emergency => MaintenanceKind.Emergency,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unmapped MaintenanceKind — add it to the contract enum too."),
    };

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
