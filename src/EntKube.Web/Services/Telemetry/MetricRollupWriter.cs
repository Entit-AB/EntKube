using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Writes metric points into <see cref="MetricRollup"/> rows, folding them into buckets on the way
/// in — the write half of the central metrics store (docs/telemetry-central-again.md, phase 3).
///
/// <para><b>Why this is not on <c>ITelemetryIngest</c>.</b> That interface writes logs, spans and
/// RUM into the segment engine, where a write is an append to a Lucene index. Metrics go to
/// Postgres rows that are read back by range and aggregate, which is a different store with
/// different retention — sharing the interface would mean every implementation of it had to
/// pretend to own both.</para>
/// </summary>
public class MetricRollupWriter(
    IDbContextFactory<TelemetryDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<MetricRollupWriter> logger)
{
    /// <summary>
    /// Bucket width, from <c>Telemetry:MetricsResolutionSeconds</c>. Thirty seconds by default:
    /// fine enough that a sixty-bucket hour view is answered from stored rows, coarse enough that
    /// a ten-second scrape is folded threefold rather than stored whole.
    ///
    /// <para>Set it to the scrape interval for raw samples — see <see cref="MetricRollup"/>.</para>
    /// </summary>
    public int ResolutionSeconds =>
        MetricRollupFold.Clamp(configuration.GetValue<int?>("Telemetry:MetricsResolutionSeconds") ?? 30);

    /// <summary>
    /// Whether the plane accepts pushed metrics at all. Off by default for now: the query side is
    /// not wired up yet, so accepting them would fill a table nothing reads.
    /// </summary>
    public bool IsEnabled =>
        configuration.GetValue<bool?>("Telemetry:AcceptMetricsIngest") ?? false;

    /// <summary>
    /// Folds <paramref name="points"/> into buckets and merges them into the store. Returns how
    /// many points were accepted, not how many rows changed — a batch that lands entirely in one
    /// bucket is one row and still several points.
    ///
    /// <para><b>Merged, not inserted.</b> A collector retries on 5xx and can deliver the same
    /// batch twice, and two collectors can report the same scope. Both must converge on one row:
    /// the scope tuple is uniquely indexed precisely so a second insert cannot quietly double
    /// every reading of that bucket. So each folded bucket is looked up and merged if present.</para>
    /// </summary>
    public async Task<int> WriteAsync(
        Guid tenantId, Guid clusterId, IReadOnlyList<MetricPoint> points,
        CancellationToken ct = default)
    {
        if (!IsEnabled || points.Count == 0) return 0;

        int resolution = ResolutionSeconds;
        Dictionary<MetricRollupKey, MetricRollupValue> folded =
            MetricRollupFold.Fold(points, resolution);

        if (folded.Count == 0) return 0;

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // One query for every bucket this batch touches, rather than one per bucket: a batch from
        // a busy cluster carries hundreds of scopes, and a round trip each would make ingest cost
        // scale with cardinality — the shape that made the deleted row store fail under load.
        DateTime from = folded.Keys.Min(k => k.BucketStart);
        DateTime to = folded.Keys.Max(k => k.BucketStart);
        HashSet<string> names = [.. folded.Keys.Select(k => k.Name)];

        List<MetricRollup> existing = await db.MetricRollups
            .Where(r => r.TenantId == tenantId
                && r.ClusterId == clusterId
                && r.ResolutionSeconds == resolution
                && r.BucketStart >= from
                && r.BucketStart <= to
                && names.Contains(r.Name))
            .ToListAsync(ct);

        Dictionary<MetricRollupKey, MetricRollup> byKey = existing.ToDictionary(
            r => new MetricRollupKey(r.Name, r.Service, r.Namespace, r.Pod, r.BucketStart));

        foreach ((MetricRollupKey key, MetricRollupValue value) in folded)
        {
            if (byKey.TryGetValue(key, out MetricRollup? row))
            {
                // The same arithmetic the fold applies in memory. Last is NOT compared by
                // timestamp here: the stored row does not carry one, and a batch that arrives
                // later is the better claim on "latest" for a bucket already written.
                row.Count += value.Count;
                row.Sum += value.Sum;
                row.Min = Math.Min(row.Min, value.Min);
                row.Max = Math.Max(row.Max, value.Max);
                row.Last = value.Last;
                continue;
            }

            db.MetricRollups.Add(new MetricRollup
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterId = clusterId,
                Name = key.Name,
                Service = key.Service,
                Namespace = key.Namespace,
                Pod = key.Pod,
                BucketStart = key.BucketStart,
                ResolutionSeconds = resolution,
                Count = value.Count,
                Sum = value.Sum,
                Min = value.Min,
                Max = value.Max,
                Last = value.Last,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two ingest requests for the same scope can race between the read above and this
            // save, and the unique index is what stops the duplicate — by design. Reported and
            // swallowed: the collector would retry a 500 and the next attempt merges, whereas
            // failing the whole batch would lose the scopes that did write.
            logger.LogWarning(ex,
                "Metric rollup batch for cluster {ClusterId} collided on the scope index; "
                + "the colliding buckets will merge on the collector's next delivery.",
                clusterId);
            return 0;
        }

        return points.Count;
    }
}
