using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The management plane's <see cref="ISegmentCatalog"/>: segment rows in the EntKube database, indexed by
/// <c>(TenantId, Signal, MaxTs, MinTs)</c> so the overlap query is a range scan rather than a table scan.
///
/// A short-lived context per call is intentional. The catalog is touched once per query (to prune) and once
/// per seal, never per event, so there is no connection pressure here — and holding a context open across a
/// query would pin a connection for the whole Lucene search. This is the property that keeps the segment
/// engine clear of the Postgres connection exhaustion that motivated replacing the old row-per-event store.
/// </summary>
public sealed class EfSegmentCatalog(IDbContextFactory<ApplicationDbContext> dbFactory) : ISegmentCatalog
{
    public async Task AddAsync(TelemetrySegment segment, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.TelemetrySegments.Add(segment);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TelemetrySegment>> ListOverlappingAsync(
        Guid tenantId, string signal, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<TelemetrySegment> q = db.TelemetrySegments.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Signal == signal);
        // "Overlaps [from, to)" — a segment is in scope unless it ends before the window or starts after it.
        if (from is DateTime f) q = q.Where(s => s.MaxTs >= f);
        if (to is DateTime t) q = q.Where(s => s.MinTs < t);
        return await q.OrderBy(s => s.MinTs).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TelemetrySegment>> RemoveExpiredAsync(
        Guid tenantId, string signal, DateTime cutoff, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<TelemetrySegment> expired = await db.TelemetrySegments
            .Where(s => s.TenantId == tenantId && s.Signal == signal && s.MaxTs < cutoff)
            .ToListAsync(ct);
        if (expired.Count == 0) return [];

        db.TelemetrySegments.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired;
    }

    public async Task<DateTime?> GetMinTsAsync(Guid tenantId, string signal, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TelemetrySegments.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Signal == signal)
            .MinAsync(s => (DateTime?)s.MinTs, ct);
    }
}
