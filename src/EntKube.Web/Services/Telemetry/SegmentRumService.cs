using EntKube.Web.Services;
using Lucene.Net.Documents;
using Lucene.Net.Search;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Lucene/S3 segment-engine implementation of <see cref="IRumQueryService"/> — the drop-in replacement for
/// <see cref="PgRumService"/>, returning the same DTOs. Filters spans the RUM segment via the inverted
/// index (tenant/site/kind/window) and computes the dashboard aggregates (Web-Vitals p75, top pages/errors,
/// page-view series, sessions, session detail) in C# over the materialized rows — replacing the SQL
/// percentile_cont / GROUP BY / session join. Web Vitals use p75 (the RUM convention), interpolated.
/// </summary>
public sealed class SegmentRumService(RumSegmentManager segments, ILogger<SegmentRumService> logger) : IRumQueryService
{
    private const int MaxRumRows = 200_000;

    public async Task<bool> HasDataAsync(Guid tenantId, Guid siteId, CancellationToken ct = default)
    {
        Query scope = RumSegmentSchema.BuildScope(tenantId, siteId, RumSegmentSchema.PageView, null, null);
        return await segments.QueryAsync(null, null, s => s.Search(scope, 1).TotalHits > 0, ct);
    }

    public async Task<RumSiteOverview?> GetOverviewAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            List<RumRow> rows = await LoadRowsAsync(tenantId, siteId, null, from, to, ct);
            List<RumRow> pv = rows.Where(r => r.Kind == RumSegmentSchema.PageView).ToList();
            long errors = rows.Count(r => r.Kind == RumSegmentSchema.Error);

            return new RumSiteOverview(
                PageViews: pv.Count,
                Sessions: pv.Select(r => r.SessionId).Distinct().Count(),
                Errors: errors,
                LcpP75: P75(pv.Select(r => r.LcpMs)),
                ClsP75: P75(pv.Select(r => r.Cls)),
                InpP75: P75(pv.Select(r => r.InpMs)),
                FcpP75: P75(pv.Select(r => r.FcpMs)),
                AvgLoadMs: Avg(pv.Select(r => r.LoadMs)),
                AvgTtfbMs: Avg(pv.Select(r => r.TtfbMs)));
        }
        catch (Exception ex) { logger.LogWarning(ex, "Segment RUM overview failed (site {Site})", siteId); return null; }
    }

    public async Task<List<RumTopPage>> GetTopPagesAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default)
    {
        try
        {
            List<RumRow> pv = await LoadRowsAsync(tenantId, siteId, RumSegmentSchema.PageView, from, to, ct);
            return pv.GroupBy(r => r.Path)
                .Select(g => new RumTopPage(g.Key, g.Count(), P75(g.Select(r => r.LcpMs))))
                .OrderByDescending(p => p.Views).Take(limit).ToList();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Segment RUM top pages failed (site {Site})", siteId); return []; }
    }

    public async Task<List<RumTopError>> GetTopErrorsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default)
    {
        try
        {
            List<RumRow> err = await LoadRowsAsync(tenantId, siteId, RumSegmentSchema.Error, from, to, ct);
            return err.GroupBy(r => r.Message ?? "")
                .Select(g => new RumTopError(g.Key, g.Count(), TelemetryTime.FromEpochMillis(g.Max(r => r.TsMs))))
                .OrderByDescending(e => e.Count).Take(limit).ToList();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Segment RUM top errors failed (site {Site})", siteId); return []; }
    }

    public async Task<List<TimeSeriesDataPoint>> GetPageViewSeriesAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int buckets = 60, CancellationToken ct = default)
    {
        try
        {
            long fromMs = TelemetryTime.ToEpochMillis(from);
            long toMs = TelemetryTime.ToEpochMillis(to);
            long bucketMs = Math.Max(1000, (long)Math.Ceiling((toMs - fromMs) / (double)Math.Max(1, buckets)));

            List<RumRow> pv = await LoadRowsAsync(tenantId, siteId, RumSegmentSchema.PageView, from, to, ct);
            return pv.GroupBy(r => (r.TsMs - fromMs) / bucketMs)
                .OrderBy(g => g.Key)
                .Select(g => new TimeSeriesDataPoint
                {
                    Timestamp = TelemetryTime.FromEpochMillis(fromMs + g.Key * bucketMs),
                    Value = g.Count(),
                }).ToList();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Segment RUM page-view series failed (site {Site})", siteId); return []; }
    }

    public async Task<List<RumSessionSummary>> GetSessionsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 50, CancellationToken ct = default)
    {
        try
        {
            List<RumRow> rows = await LoadRowsAsync(tenantId, siteId, null, from, to, ct);
            Dictionary<string, int> errsBySession = rows
                .Where(r => r.Kind == RumSegmentSchema.Error)
                .GroupBy(r => r.SessionId)
                .ToDictionary(g => g.Key, g => g.Count());

            return rows.Where(r => r.Kind == RumSegmentSchema.PageView)
                .GroupBy(r => r.SessionId)
                .Select(g => new RumSessionSummary(
                    SessionId: g.Key,
                    Started: TelemetryTime.FromEpochMillis(g.Min(r => r.TsMs)),
                    LastSeen: TelemetryTime.FromEpochMillis(g.Max(r => r.TsMs)),
                    Views: g.Count(),
                    LastPath: g.OrderByDescending(r => r.TsMs).First().Path,
                    Errors: errsBySession.GetValueOrDefault(g.Key)))
                .OrderByDescending(s => s.LastSeen).Take(limit).ToList();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Segment RUM sessions failed (site {Site})", siteId); return []; }
    }

    public async Task<RumSessionDetail?> GetSessionDetailAsync(Guid tenantId, Guid siteId, string sessionId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            Query q = RumSegmentSchema.BuildSession(tenantId, siteId, sessionId, from, to);
            List<RumRow> rows = await MaterializeAsync(q, from, to, ct);

            List<RumViewRow> views = rows.Where(r => r.Kind == RumSegmentSchema.PageView).OrderBy(r => r.TsMs)
                .Select(r => new RumViewRow(TelemetryTime.FromEpochMillis(r.TsMs), r.Path, r.LoadMs, r.LcpMs, r.Cls, r.InpMs)).ToList();
            List<RumErrorRow> errors = rows.Where(r => r.Kind == RumSegmentSchema.Error).OrderBy(r => r.TsMs)
                .Select(r => new RumErrorRow(TelemetryTime.FromEpochMillis(r.TsMs), NullIfEmpty(r.Path), r.Message ?? "", r.Source)).ToList();
            List<RumResourceRow> resources = rows.Where(r => r.Kind == RumSegmentSchema.Resource).OrderBy(r => r.TsMs)
                .Select(r => new RumResourceRow(TelemetryTime.FromEpochMillis(r.TsMs), NullIfEmpty(r.Path), r.Name ?? "", r.ResKind, r.DurationMs, r.Status, r.TraceId)).ToList();
            return new RumSessionDetail(views, errors, resources);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Segment RUM session detail failed (site {Site}, session {Session})", siteId, sessionId); return null; }
    }

    // ──────── internal ────────

    private Task<List<RumRow>> LoadRowsAsync(Guid tenantId, Guid siteId, string? kind, DateTime from, DateTime to, CancellationToken ct)
        => MaterializeAsync(RumSegmentSchema.BuildScope(tenantId, siteId, kind, from, to), from, to, ct);

    private async Task<List<RumRow>> MaterializeAsync(Query q, DateTime? from, DateTime? to, CancellationToken ct)
    {
        return await segments.QueryAsync(from, to, s =>
        {
            TopDocs hits = s.Search(q, MaxRumRows);
            if (hits.TotalHits > MaxRumRows)
                logger.LogWarning("RUM query matched {Total} rows; truncated to {Cap}.", hits.TotalHits, MaxRumRows);
            var rows = new List<RumRow>(hits.ScoreDocs.Length);
            foreach (ScoreDoc sd in hits.ScoreDocs)
                rows.Add(RumSegmentSchema.ReadRow(s.Doc(sd.Doc)));
            return rows;
        }, ct);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static double? Avg(IEnumerable<double?> values)
    {
        List<double> v = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    private static double? P75(IEnumerable<double?> values)
        => PercentileCont(values.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList(), 0.75);

    // Interpolated (continuous) percentile, matching Postgres percentile_cont.
    private static double? PercentileCont(List<double> sortedAsc, double p)
    {
        int n = sortedAsc.Count;
        if (n == 0) return null;
        if (n == 1) return sortedAsc[0];
        double rank = p * (n - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return lo == hi ? sortedAsc[lo] : sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (rank - lo);
    }
}
