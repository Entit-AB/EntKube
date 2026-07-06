using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Resolves (and process-wide caches) a cluster's owning tenant from the operational DB. A cluster's
/// tenant is immutable, so caching is safe and spares the control-plane DB repeated lookups on the
/// telemetry query hot path. Shared by the native log and trace query services.
/// </summary>
public sealed class ClusterTenantResolver(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly ConcurrentDictionary<Guid, Guid> Cache = new();

    public async Task<Guid?> ResolveAsync(Guid clusterId, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(clusterId, out Guid cached)) return cached;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        Guid? tenantId = await db.KubernetesClusters
            .Where(c => c.Id == clusterId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct);

        if (tenantId is Guid t) Cache[clusterId] = t;
        return tenantId;
    }
}
