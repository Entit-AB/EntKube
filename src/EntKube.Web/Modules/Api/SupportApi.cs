using EntKube.Contracts.Support;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Support's implementation of <see cref="ISupportApi"/>, over its own module context.
///
/// <para>Unlike Delivery's, this one's tenancy is a column: <c>SlaTarget.TenantId</c> is set
/// directly and its customer and app are both optional, so there is no navigation to reach the
/// tenant through. That makes the predicate simple and makes forgetting it silent, which is why
/// it is the only query in the file and the filter is the first thing in it.</para>
/// </summary>
public sealed class SupportApi(IDbContextFactory<SupportDbContext> dbFactory) : ISupportApi
{
    public async Task<IReadOnlyList<SlaTargetSummary>> GetSlaTargetsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.SlaTargets
            .Where(t => t.TenantId == tenantId)
            .Select(t => new SlaTargetSummary(
                t.CustomerId, t.AppId, t.TargetPercent, t.MeasurementWindowDays))
            .ToListAsync(ct);
    }
}
