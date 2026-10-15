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
