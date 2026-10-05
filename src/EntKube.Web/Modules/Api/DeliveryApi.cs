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

        return await ScopedDeployments(db, tenantId).Where(d => d.Id == deploymentId).Select(ToDeployment).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await ScopedDeployments(db, tenantId).Select(ToDeployment).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForAppEnvironmentAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await ScopedDeployments(db, tenantId)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .Select(ToDeployment).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using DeliveryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await ScopedDeployments(db, tenantId).Where(d => d.ClusterId == clusterId).Select(ToDeployment).ToListAsync(ct);
    }

    private static IQueryable<Data.App> ScopedApps(DeliveryDbContext db, Guid tenantId)
        => db.Apps.AsNoTracking().Where(a => a.Customer.TenantId == tenantId);

    private static IQueryable<Data.AppDeployment> ScopedDeployments(DeliveryDbContext db, Guid tenantId)
        => db.AppDeployments.AsNoTracking().Where(d => d.App.Customer.TenantId == tenantId);

    /// <summary>Applied after any filter — see CatalogApi for why the order matters.</summary>
    private static readonly Expression<Func<Data.App, AppSummary>> ToApp =
        a => new AppSummary(
            a.Id, a.Customer.TenantId, a.CustomerId, a.Customer.Name, a.Name, a.Namespace);

    private static readonly Expression<Func<Data.AppDeployment, DeploymentSummary>> ToDeployment =
        d => new DeploymentSummary(
            d.Id,
            d.App.Customer.TenantId,
            d.AppId,
            d.App.Name,
            d.EnvironmentId,
            d.ClusterId,
            d.Name,
            (ContractDeploymentType)(int)d.Type,
            d.Namespace,
            (ContractSyncStatus)(int)d.SyncStatus,
            (ContractHealthStatus)(int)d.HealthStatus,
            d.StatusMessage,
            d.LastSyncedAt,
            d.IsManaged);
}
