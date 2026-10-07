using EntKube.Contracts.Catalog;
using ContractStatus = EntKube.Contracts.Catalog.ComponentStatus;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Catalog's implementation of <see cref="ICatalogApi"/>, over its own module context.
///
/// <para>Every query is tenant-scoped through the component's cluster, which is also where
/// the tenant lives in the schema. Callers cannot forget to do that any more, because they
/// no longer write the query.</para>
/// </summary>
public sealed class CatalogApi(IDbContextFactory<CatalogDbContext> dbFactory) : ICatalogApi
{
    public async Task<InstalledComponent?> GetComponentAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        await using CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Where(c => c.Id == componentId).Select(ToContract).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<InstalledComponent>> GetComponentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Where(c => c.ClusterId == clusterId).Select(ToContract).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<InstalledComponent>> GetComponentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Select(ToContract).ToListAsync(ct);
    }

    public async Task<InstalledComponent?> FindComponentAsync(
        Guid tenantId, Guid clusterId, string catalogKey, CancellationToken ct = default)
    {
        await using CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId)
            .Where(c => c.ClusterId == clusterId && c.Name == catalogKey)
            .Select(ToContract).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<InstalledComponent>> FindComponentsAsync(
        Guid tenantId, string catalogKey, CancellationToken ct = default)
    {
        await using CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId).Where(c => c.Name == catalogKey).Select(ToContract).ToListAsync(ct);
    }

    public Task<InstalledComponent?> MergeHelmValuesAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> formValues,
        string? configuration = null, CancellationToken ct = default)
        => ConfigureAsync(tenantId, componentId, configuration,
            existing => Services.YamlFormMerger.MergeFormValues(existing ?? "", formValues), ct);

    public Task<InstalledComponent?> SetHelmValuesAsync(
        Guid tenantId, Guid componentId, string helmValues,
        string? configuration = null, CancellationToken ct = default)
        => ConfigureAsync(tenantId, componentId, configuration, _ => helmValues, ct);

    /// <summary>
    /// The one write path, shared by both configure methods so the tenant scoping and the
    /// "leave configuration alone when null" rule exist once.
    /// </summary>
    private async Task<InstalledComponent?> ConfigureAsync(
        Guid tenantId, Guid componentId, string? configuration,
        Func<string?, string> nextHelmValues, CancellationToken ct)
    {
        await using CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Tracked, unlike every read above — this one is going to be saved. Same tenant predicate
        // as `Scoped`, which is why a component belonging to someone else reads as absent rather
        // than as a forbidden write.
        ClusterComponent? component = await db.ClusterComponents
            .Where(c => c.Id == componentId && c.Cluster.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (component is null) return null;

        component.HelmValues = nextHelmValues(component.HelmValues);

        if (configuration is not null)
        {
            component.Configuration = configuration;
        }

        await db.SaveChangesAsync(ct);

        // Read back through the shared projection rather than assembling the record here, so what
        // a write returns cannot drift from what a read returns.
        return await Scoped(db, tenantId)
            .Where(c => c.Id == componentId)
            .Select(ToContract)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>This tenant's components, unprojected, so callers can filter before projecting.</summary>
    private static IQueryable<ClusterComponent> Scoped(CatalogDbContext db, Guid tenantId)
        => db.ClusterComponents.AsNoTracking().Where(c => c.Cluster.TenantId == tenantId);

    /// <summary>
    /// One projection shared by every query, so the field list cannot drift between them.
    ///
    /// <para>Applied last, after any filter. Projecting first and filtering on the record
    /// afterwards does not translate — EF cannot see through the constructor — and the whole
    /// lookup fails at runtime rather than compile time.</para>
    /// </summary>
    private static readonly Expression<Func<ClusterComponent, InstalledComponent>> ToContract =
        c => new InstalledComponent(
            c.Id,
            c.Cluster.TenantId,
            c.ClusterId,
            c.Cluster.Name,
            c.Cluster.EnvironmentId,
            c.Name,
            c.ComponentType,
            (ContractStatus)(int)c.Status,
            c.Namespace,
            c.ReleaseName,
            c.HelmChartName,
            c.HelmChartVersion,
            c.HelmValues,
            c.Configuration,
            c.LastError,
            c.InstalledAt);
}
