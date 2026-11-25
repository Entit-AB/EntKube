using EntKube.Contracts.DataServices;
using EntKube.Web.Data.Modules;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// DataServices' implementation of <see cref="IDataServicesApi"/>, over its own module context.
///
/// <para>The tenant is on the row here, so the scope is a plain predicate rather than a walk
/// through a navigation — but it is still written once, in <see cref="Scoped"/>, because the
/// four callers this contract was written for had each spelled it out themselves and a fifth
/// would have had to remember to.</para>
/// </summary>
public sealed class DataServicesApi(IDbContextFactory<DataServicesDbContext> dbFactory) : IDataServicesApi
{
    public async Task<StorageLinkSummary?> GetStorageLinkAsync(
        Guid tenantId, Guid storageLinkId, CancellationToken ct = default)
    {
        await using DataServicesDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId)
            .Where(s => s.Id == storageLinkId)
            .Select(ToContract)
            .FirstOrDefaultAsync(ct);
    }

    /// <remarks>
    /// Six queries in a row, with no <c>try</c> around any of them, and that is the point: the
    /// contract promises the whole list or an exception, because a caller prunes by what is absent.
    /// The status is mapped after the rows are read rather than inside the query — an enum cast in a
    /// projection runs in SQL, where it depends on how the column happens to be stored.
    /// </remarks>
    public async Task<IReadOnlyList<ManagedServiceSummary>> GetManagedServicesForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using DataServicesDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<ManagedServiceSummary> services = [];

        services.AddRange((await db.CnpgClusters.AsNoTracking().Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.KubernetesClusterId, c.Name, c.Namespace, c.Status })
            .ToListAsync(ct))
            .Select(c => new ManagedServiceSummary(
                c.Id, tenantId, c.KubernetesClusterId, ManagedServiceKind.Postgres, c.Name, c.Namespace,
                ToContractStatus(c.Status), IsOperatorManaged: true, IsInCluster: true, HasExporter: false)));

        services.AddRange((await db.MongoClusters.AsNoTracking().Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.KubernetesClusterId, c.Name, c.Namespace, c.Status, c.ExternalUri })
            .ToListAsync(ct))
            .Select(c => new ManagedServiceSummary(
                c.Id, tenantId, c.KubernetesClusterId, ManagedServiceKind.MongoDb, c.Name, c.Namespace,
                ToContractStatus(c.Status), IsOperatorManaged: true,
                IsInCluster: string.IsNullOrEmpty(c.ExternalUri), HasExporter: false)));

        services.AddRange((await db.RedisClusters.AsNoTracking().Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.KubernetesClusterId, c.Name, c.Namespace, c.Status })
            .ToListAsync(ct))
            .Select(c => new ManagedServiceSummary(
                c.Id, tenantId, c.KubernetesClusterId, ManagedServiceKind.Redis, c.Name, c.Namespace,
                ToContractStatus(c.Status), IsOperatorManaged: true, IsInCluster: true, HasExporter: false)));

        services.AddRange((await db.KafkaClusters.AsNoTracking().Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.KubernetesClusterId, c.Name, c.Namespace, c.Status })
            .ToListAsync(ct))
            .Select(c => new ManagedServiceSummary(
                c.Id, tenantId, c.KubernetesClusterId, ManagedServiceKind.Kafka, c.Name, c.Namespace,
                ToContractStatus(c.Status), IsOperatorManaged: true, IsInCluster: true, HasExporter: false)));

        services.AddRange((await db.RabbitMQClusters.AsNoTracking().Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.KubernetesClusterId, c.Name, c.Namespace, c.Status, c.IsOperatorManaged })
            .ToListAsync(ct))
            .Select(c => new ManagedServiceSummary(
                c.Id, tenantId, c.KubernetesClusterId, ManagedServiceKind.RabbitMq, c.Name, c.Namespace,
                ToContractStatus(c.Status), c.IsOperatorManaged, IsInCluster: true, HasExporter: false)));

        services.AddRange((await db.ElasticsearchClusters.AsNoTracking().Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.KubernetesClusterId, c.Name, c.Namespace, c.Status, c.MonitoringEnabled })
            .ToListAsync(ct))
            .Select(c => new ManagedServiceSummary(
                c.Id, tenantId, c.KubernetesClusterId, ManagedServiceKind.Elasticsearch, c.Name, c.Namespace,
                ToContractStatus(c.Status), IsOperatorManaged: true, IsInCluster: true,
                HasExporter: c.MonitoringEnabled)));

        return services;
    }

    /// <summary>
    /// One of the six status enums as the contract's. By name, and it throws on a name the contract
    /// does not have: a status read as the wrong one could make a running database look like one
    /// being deleted.
    /// </summary>
    private static ManagedServiceStatus ToContractStatus<TStatus>(TStatus status) where TStatus : struct, Enum
        => Enum.Parse<ManagedServiceStatus>(status.ToString());

    private static IQueryable<Data.StorageLink> Scoped(DataServicesDbContext db, Guid tenantId)
        => db.StorageLinks.AsNoTracking().Where(s => s.TenantId == tenantId);

    /// <summary>Applied after any filter — see CatalogApi for why the order matters.</summary>
    private static readonly Expression<Func<Data.StorageLink, StorageLinkSummary>> ToContract =
        s => new StorageLinkSummary(
            s.Id,
            s.TenantId,
            s.EnvironmentId,
            (StorageProviderKind)(int)s.Provider,
            s.Name,
            s.Endpoint,
            s.BucketName,
            s.Region);
}
