using EntKube.Contracts.Delivery;
using EntKube.Web.Data.Modules;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ContractDeploymentType = EntKube.Contracts.Delivery.DeploymentType;
using ContractHealthStatus = EntKube.Contracts.Delivery.HealthStatus;
using ContractSyncStatus = EntKube.Contracts.Delivery.SyncStatus;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Delivery's implementation of <see cref="IDeliveryApi"/>, over its own module context.
///
/// <para>Tenancy reaches apps through their customer, which is why every projection here
/// goes <c>App.Customer.TenantId</c> rather than taking a tenant column at face value.
/// Getting that wrong is how one customer's deployments would show up under another.</para>
/// </summary>
public sealed class DeliveryApi(IDbContextFactory<DeliveryDbContext> dbFactory) : IDeliveryApi
{
    public async Task<AppSummary?> GetAppAsync(Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await ScopedApps(db, tenantId).Where(a => a.Id == appId).Select(ToApp).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<AppSummary>> GetAppsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await ScopedApps(db, tenantId).Select(ToApp).ToListAsync(ct);
    }

    public async Task<DeploymentSummary?> GetDeploymentAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Row? row = await ScopedDeployments(db, tenantId)
            .Where(d => d.Id == deploymentId).Select(ToRow).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDeployment(row);
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return (await ScopedDeployments(db, tenantId).Select(ToRow).ToListAsync(ct))
            .Select(ToDeployment).ToList();
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForAppEnvironmentAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return (await ScopedDeployments(db, tenantId)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .Select(ToRow).ToListAsync(ct))
            .Select(ToDeployment).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetAllowedCacheClusterIdsAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.AppAllowedCaches.AsNoTracking()
            .Where(a => a.AppId == appId
                && a.EnvironmentId == environmentId
                && a.App.Customer.TenantId == tenantId)
            .Select(a => a.RedisClusterId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return (await ScopedDeployments(db, tenantId)
            .Where(d => d.ClusterId == clusterId).Select(ToRow).ToListAsync(ct))
            .Select(ToDeployment).ToList();
    }

    /// <summary>
    /// The tenant's most recent watched releases. The predicate walks rollout → deployment → app →
    /// customer, which is the reason the public API had been querying this table itself.
    /// </summary>
    public async Task<IReadOnlyList<RolloutSummary>> GetRolloutsAsync(
        Guid tenantId, int limit = 100, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Same shape as ToRow/ToDeployment below and for the same reason: the row carries the
        // ENTITY enum and the mapping to the contract's copy happens in memory. Casting an enum
        // inside the expression is what produced the Cost page's 22P02 — see
        // project_enum_storage_casts / §4.0.1. This one is stored as an int rather than a string,
        // so the cast would not throw; it would just make the next string-stored enum here a
        // silent wrong answer, which is worse.
        var rows = await db.DeploymentRollouts
            .AsNoTracking()
            .Where(r => r.Deployment.App.Customer.TenantId == tenantId)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(r => new
            {
                r.Id,
                AppName = r.Deployment.App.Name,
                DeploymentName = r.Deployment.Name,
                EnvironmentName = r.Deployment.Environment.Name,
                ClusterName = r.Deployment.Cluster.Name,
                r.Status,
                r.StartedAt,
                r.FinishedAt,
                r.Verdict,
                r.TriggeredBy,
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new RolloutSummary(
            r.Id, r.AppName, r.DeploymentName, r.EnvironmentName, r.ClusterName,
            ToVerdictStatus(r.Status), r.StartedAt, r.FinishedAt, r.Verdict, r.TriggeredBy))];
    }

    /// <summary>
    /// Delivery's rollout status as the contract's. Explicit rather than a cast: the two enums
    /// agree today and <c>ApiRolloutStatusTests</c> keeps them agreeing, but the public API
    /// serialises the name, so a silent renumbering must not become a silent relabelling.
    /// </summary>
    private static RolloutVerdictStatus ToVerdictStatus(Data.DeploymentRolloutStatus status)
        => status switch
        {
            Data.DeploymentRolloutStatus.Watching => RolloutVerdictStatus.Watching,
            Data.DeploymentRolloutStatus.Promoted => RolloutVerdictStatus.Promoted,
            Data.DeploymentRolloutStatus.RolledBack => RolloutVerdictStatus.RolledBack,
            Data.DeploymentRolloutStatus.Alerted => RolloutVerdictStatus.Alerted,
            Data.DeploymentRolloutStatus.Inconclusive => RolloutVerdictStatus.Inconclusive,
            Data.DeploymentRolloutStatus.RollbackFailed => RolloutVerdictStatus.RollbackFailed,
            Data.DeploymentRolloutStatus.Superseded => RolloutVerdictStatus.Superseded,
            _ => RolloutVerdictStatus.Watching,
        };

    public async Task<IReadOnlyList<DeploymentHealthSample>> GetHealthSamplesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> deploymentIds, DateTime from,
        DateTime? to = null, CancellationToken ct = default)
    {
        if (deploymentIds.Count == 0) return [];

        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // The tenant predicate is the deployment's, reached the way every other read here reaches
        // it — so a deployment id from another tenant contributes no samples rather than throwing.
        // The table's only index is (DeploymentId, SnapshotAt), which is exactly this shape.
        IQueryable<Data.DeploymentHealthSnapshot> q = db.DeploymentHealthSnapshots
            .AsNoTracking()
            .Where(s => deploymentIds.Contains(s.DeploymentId)
                && s.Deployment.App.Customer.TenantId == tenantId
                && s.SnapshotAt >= from);

        if (to is DateTime upper)
        {
            q = q.Where(s => s.SnapshotAt < upper);
        }

        // Entity enum in, contract enum out, cast IN MEMORY after materialising — the same
        // convention ToDeployment uses for this exact pair below, and for the same reason: these
        // are string-stored columns, so a cast inside the expression becomes
        // CAST('Healthy' AS integer) on Postgres and a silent 0 on SQLite.
        var rows = await q
            .OrderBy(s => s.SnapshotAt)
            .Select(s => new { s.DeploymentId, s.HealthStatus, s.SnapshotAt })
            .ToListAsync(ct);

        return [.. rows.Select(r => new DeploymentHealthSample(
            r.DeploymentId, (ContractHealthStatus)(int)r.HealthStatus, r.SnapshotAt))];
    }

    public async Task<IReadOnlyList<DeploymentHealthRatio>> GetHealthRatiosAsync(
        Guid tenantId, IReadOnlyCollection<Guid> deploymentIds, DateTime from,
        DateTime? to = null, CancellationToken ct = default)
    {
        if (deploymentIds.Count == 0) return [];

        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Same predicate and same index as GetHealthSamplesAsync — (DeploymentId, SnapshotAt) —
        // so this is the identical scan with the aggregate left in the database.
        IQueryable<Data.DeploymentHealthSnapshot> q = db.DeploymentHealthSnapshots
            .AsNoTracking()
            .Where(s => deploymentIds.Contains(s.DeploymentId)
                && s.Deployment.App.Customer.TenantId == tenantId
                && s.SnapshotAt >= from);

        if (to is DateTime upper)
        {
            q = q.Where(s => s.SnapshotAt < upper);
        }

        // The conditional count translates to COUNT(CASE WHEN "HealthStatus" = 'Healthy' THEN 1
        // END), so the whole aggregate stays in the database — which is the entire point of this
        // method over GetHealthSamplesAsync.
        //
        // ⚠️ A note against the obvious worry, because it was checked rather than assumed: the
        // string-stored-enum trap recorded on Delivery_carries_the_enums_rather_than_casting_them
        // does NOT apply to this comparison. That bug is a PROJECTION converting to a different
        // enum type — `(ContractHealthStatus)(int)r.HealthStatus` inside a Select, which EF emits
        // as CAST("HealthStatus" AS integer): 22P02 on PostgreSQL, a silent 0 on SQLite. Here the
        // comparison is against a constant of the SAME enum, and EF folds `(int)x == (int)const`
        // back to `= 'Healthy'`: both spellings were compiled and ToQueryString returned byte-
        // identical SQL. Writing the value comparison is still the right habit, but it is a habit
        // and not a fix, and claiming otherwise would leave a false reason for the next reader.
        var rows = await q
            .GroupBy(s => s.DeploymentId)
            .Select(g => new
            {
                DeploymentId = g.Key,
                Total = g.Count(),
                Healthy = g.Count(s => s.HealthStatus == Data.HealthStatus.Healthy),
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new DeploymentHealthRatio(r.DeploymentId, r.Healthy, r.Total))];
    }

    private static IQueryable<Data.App> ScopedApps(DeliveryDbContext db, Guid tenantId)
        => db.Apps.AsNoTracking().Where(a => a.Customer.TenantId == tenantId);

    public async Task<IReadOnlyList<string>> GetTenantNamespacesOnClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var deployments = await ScopedDeployments(db, tenantId)
            .Where(d => d.ClusterId == clusterId)
            .Select(d => new { d.AppId, d.EnvironmentId, d.Namespace })
            .ToListAsync(ct);

        if (deployments.Count == 0)
        {
            return [];
        }

        // Only the app/environment pairs that actually deploy here. The hand-written version
        // loaded every AppEnvironment row belonging to every app in the tenant and then looked up
        // a handful of them; the result is identical because nothing outside these pairs can be
        // consulted below.
        HashSet<Guid> appIds = [.. deployments.Select(d => d.AppId)];

        Dictionary<(Guid, Guid), string?> locked = (await db.AppEnvironments
                .AsNoTracking()
                .Where(ae => appIds.Contains(ae.AppId))
                .Select(ae => new { ae.AppId, ae.EnvironmentId, ae.Namespace })
                .ToListAsync(ct))
            .ToDictionary(x => (x.AppId, x.EnvironmentId), x => x.Namespace);

        return
        [
            .. deployments
                .Select(d =>
                    locked.TryGetValue((d.AppId, d.EnvironmentId), out string? pinned)
                    && !string.IsNullOrWhiteSpace(pinned)
                        ? pinned!
                        : d.Namespace)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Select(ns => ns!)
                .Distinct()
                .Order()
        ];
    }

    private static IQueryable<Data.AppDeployment> ScopedDeployments(DeliveryDbContext db, Guid tenantId)
        => db.AppDeployments.AsNoTracking().Where(d => d.App.Customer.TenantId == tenantId);

    /// <summary>Applied after any filter — see CatalogApi for why the order matters.</summary>
    private static readonly Expression<Func<Data.App, AppSummary>> ToApp =
        a => new AppSummary(
            a.Id, a.Customer.TenantId, a.CustomerId, a.Customer.Name, a.Name, a.Namespace);

    /// <summary>
    /// The deployment row as the database hands it over, carrying the <em>entity</em> enums.
    ///
    /// <para><b>Why this intermediate exists.</b> The projection used to build
    /// <see cref="DeploymentSummary"/> directly with <c>(ContractDeploymentType)(int)d.Type</c>, and
    /// <c>Type</c>, <c>SyncStatus</c> and <c>HealthStatus</c> are all persisted as <b>text</b>
    /// (<c>HasConversion&lt;string&gt;()</c> in <c>DeliveryModel</c>). Inside an
    /// <see cref="Expression"/> that cast is not a C# cast — it becomes
    /// <c>CAST("Type" AS integer)</c> in SQL, which PostgreSQL rejects outright:
    /// <c>22P02: invalid input syntax for type integer: "HelmChart"</c>. That is the error the cost
    /// report raised the first time anything called this contract.</para>
    ///
    /// <para>⚠️ <b>On SQLite the same cast silently returns 0</b>, so every deployment came back as
    /// <c>Manual</c> with status <c>0</c> and no error at all — which is why the unit tests passed
    /// while the feature was broken. A projection that casts an enum has to be split like this: the
    /// database converts text to the entity enum (it owns the converter), and the contract enum is
    /// mapped afterwards, in memory, where a cast means what it says.</para>
    /// </summary>
    private sealed record Row(
        Guid Id,
        Guid TenantId,
        Guid AppId,
        string AppName,
        Guid EnvironmentId,
        string EnvironmentName,
        Guid ClusterId,
        string ClusterName,
        string Name,
        Data.DeploymentType Type,
        string Namespace,
        Data.SyncStatus SyncStatus,
        Data.HealthStatus HealthStatus,
        string? StatusMessage,
        DateTime? LastSyncedAt,
        bool IsManaged);

    private static readonly Expression<Func<Data.AppDeployment, Row>> ToRow =
        d => new Row(
            d.Id,
            d.App.Customer.TenantId,
            d.AppId,
            d.App.Name,
            d.EnvironmentId,
            d.Environment.Name,
            d.ClusterId,
            d.Cluster.Name,
            d.Name,
            d.Type,
            d.Namespace,
            d.SyncStatus,
            d.HealthStatus,
            d.StatusMessage,
            d.LastSyncedAt,
            d.IsManaged);

    /// <summary>
    /// Entity enums to contract enums, by ordinal — which is only sound while the two declarations
    /// agree member for member, so <c>ModuleApiTests</c> checks that they do rather than trusting it.
    /// </summary>
    private static DeploymentSummary ToDeployment(Row r) => new(
        r.Id,
        r.TenantId,
        r.AppId,
        r.AppName,
        r.EnvironmentId,
        r.EnvironmentName,
        r.ClusterId,
        r.ClusterName,
        r.Name,
        (ContractDeploymentType)(int)r.Type,
        r.Namespace,
        (ContractSyncStatus)(int)r.SyncStatus,
        (ContractHealthStatus)(int)r.HealthStatus,
        r.StatusMessage,
        r.LastSyncedAt,
        r.IsManaged);
}
