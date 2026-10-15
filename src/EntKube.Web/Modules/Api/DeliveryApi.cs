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

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return (await ScopedDeployments(db, tenantId)
            .Where(d => d.ClusterId == clusterId).Select(ToRow).ToListAsync(ct))
            .Select(ToDeployment).ToList();
    }

    private static IQueryable<Data.App> ScopedApps(DeliveryDbContext db, Guid tenantId)
        => db.Apps.AsNoTracking().Where(a => a.Customer.TenantId == tenantId);

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
