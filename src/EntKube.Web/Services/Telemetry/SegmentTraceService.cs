using EntKube.Web.Services;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Lucene/S3 segment-engine implementation of <see cref="ITraceQueryService"/> — the drop-in replacement
/// for <see cref="PgTraceService"/>, returning the same DTOs. Spans are filtered via the inverted index
/// (tenant/cluster/service/namespace/window); the trace summaries, RED percentiles, and service-map join
/// that were SQL GROUP BY / percentile_disc / self-join are computed in C# over the materialized spans of
/// the (time-bounded) match set. Every query is unioned across the active index and the sealed span
/// segments overlapping the window by <see cref="SpanSegmentManager"/>.
/// </summary>
public sealed class SegmentTraceService(
    SegmentManagerRegistry<SpanSegmentManager> spans,
    ClusterTenantResolver tenants,
    ILogger<SegmentTraceService> logger) : ITraceQueryService
{
    // Safety cap on how many spans an aggregation will materialize from one window. Well above a normal
    // trace-search window; if exceeded we log and truncate rather than risk unbounded memory.
    private const int MaxSpansPerQuery = 200_000;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return false;
        Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, null, null);
        return await spans.For(tenantId.Value).QueryAsync(null, null, s => s.Search(scope, 1).TotalHits > 0, ct);
    }

    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<string>>();

        // Bound to the last 24h (the trace search's max range), like PgTraceService.
        DateTime from = DateTime.UtcNow.AddHours(-24);
        try
        {
            Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, from, null, namespaces, podPattern);
            var sink = new HashSet<string>(StringComparer.Ordinal);
            await spans.For(tenantId.Value).QueryAsync(from, null,
                s => { s.Search(scope, new DistinctCollector(SpanSegmentSchema.Service, sink)); return 0; }, ct);
            return KubernetesOperationResult<List<string>>.Success(
                sink.Where(v => v.Length > 0).OrderBy(v => v, StringComparer.Ordinal).ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment trace service list failed (cluster {Cluster})", clusterId);
            return Fail<List<string>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<TraceSummary>>();

        try
        {
            // Load all in-window scope spans, then group by trace in C# (a trace "involves" the service if
            // any of its spans is that service — matches the old subquery).
            Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, from, to, namespaces, podPattern);
            List<SpanRow> rows = await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct);

            List<TraceSummary> summaries = rows
                .GroupBy(r => r.TraceId)
                .Where(g => string.IsNullOrEmpty(service) || g.Any(r => r.Service == service))
                .Select(Summarize)
                .Where(t => t.DurationMs >= minDurationMs && (!errorsOnly || t.ErrorCount > 0))
                .OrderByDescending(t => t.Start)
                .Take(limit)
                .ToList();
            return KubernetesOperationResult<List<TraceSummary>>.Success(summaries);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment trace list failed (cluster {Cluster})", clusterId);
            return Fail<List<TraceSummary>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<SpanRecord>>();

        try
        {
            Query q = SpanSegmentSchema.BuildTraceQuery(tenantId.Value, clusterId, traceId, namespaces);
            List<SpanRow> rows = await LoadRowsAsync(spans.For(tenantId.Value), q, null, null, ct);
            List<SpanRecord> spanRecords = rows
                .OrderBy(r => r.TsMs)
                .Select(r => new SpanRecord(
                    Start: TelemetryTime.FromEpochMillis(r.TsMs),
                    SpanId: r.SpanId,
                    ParentSpanId: r.ParentSpanId,
                    Name: r.Name,
                    Service: r.Service,
                    Kind: r.Kind,
                    DurationMs: r.DurationMs,
                    StatusCode: r.StatusCode,
                    AttributesJson: r.AttributesJson))
                .ToList();
            return KubernetesOperationResult<List<SpanRecord>>.Success(spanRecords);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment trace fetch failed (cluster {Cluster}, trace {Trace})", clusterId, traceId);
            return Fail<List<SpanRecord>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<RedBucket>>();

        long fromMs = TelemetryTime.ToEpochMillis(from);
        long toMs = TelemetryTime.ToEpochMillis(to);
        long bucketMs = Math.Max(1000, (long)Math.Ceiling((toMs - fromMs) / (double)Math.Max(1, buckets)));

        try
        {
            Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, from, to, namespaces, podPattern, service);
            List<SpanRow> rows = await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct);

            var byBucket = new SortedDictionary<long, List<SpanRow>>();
            foreach (SpanRow r in rows.Where(r => r.IsInbound))
                (byBucket.TryGetValue((r.TsMs - fromMs) / bucketMs, out var list)
                    ? list : byBucket[(r.TsMs - fromMs) / bucketMs] = []).Add(r);

            List<RedBucket> series = byBucket.Select(kv =>
            {
                List<double> durations = kv.Value.Select(r => r.DurationMs).OrderBy(d => d).ToList();
                return new RedBucket(
                    Start: TelemetryTime.FromEpochMillis(fromMs + kv.Key * bucketMs),
                    Count: kv.Value.Count,
                    Errors: kv.Value.Count(r => r.IsError),
                    AvgMs: durations.Average(),
                    P50Ms: PercentileDisc(durations, 0.50),
                    P95Ms: PercentileDisc(durations, 0.95));
            }).ToList();
            return KubernetesOperationResult<List<RedBucket>>.Success(series);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment service RED failed (cluster {Cluster}, service {Service})", clusterId, service);
            return Fail<List<RedBucket>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<ServiceEdge>>();

        try
        {
            Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, from, to, namespaces, podPattern);
            List<SpanRow> rows = await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct);

            // Index spans by (trace, span_id) so a child can find its parent within the same trace — the
            // in-memory equivalent of the old self-join on p.span_id = c.parent_span_id.
            var byId = new Dictionary<(string, string), SpanRow>();
            foreach (SpanRow r in rows) byId[(r.TraceId, r.SpanId)] = r;

            var edges = new Dictionary<(string From, string To), (long Calls, long Errors, double SumMs)>();
            foreach (SpanRow child in rows)
            {
                if (child.ParentSpanId is null) continue;
                if (!byId.TryGetValue((child.TraceId, child.ParentSpanId), out SpanRow? parent)) continue;
                if (parent.Service == child.Service) continue; // cross-service edges only
                var key = (parent.Service, child.Service);
                (long calls, long errs, double sum) = edges.TryGetValue(key, out var cur) ? cur : (0, 0, 0);
                edges[key] = (calls + 1, errs + (child.IsError ? 1 : 0), sum + child.DurationMs);
            }

            List<ServiceEdge> result = edges
                .Select(e => new ServiceEdge(e.Key.From, e.Key.To, e.Value.Calls, e.Value.Errors,
                    e.Value.Calls > 0 ? e.Value.SumMs / e.Value.Calls : 0))
                .OrderByDescending(e => e.Calls)
                .ToList();
            return KubernetesOperationResult<List<ServiceEdge>>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment service map failed (cluster {Cluster})", clusterId);
            return Fail<List<ServiceEdge>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<ServiceStats>();

        try
        {
            Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, from, to, service: service);
            List<SpanRow> rows = (await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct)).Where(r => r.IsInbound).ToList();
            List<double> durations = rows.Select(r => r.DurationMs).OrderBy(d => d).ToList();
            var stats = new ServiceStats(rows.Count, rows.Count(r => r.IsError), PercentileDisc(durations, 0.95));
            return KubernetesOperationResult<ServiceStats>.Success(stats);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment service stats failed (cluster {Cluster}, service {Service})", clusterId, service);
            return Fail<ServiceStats>(ex.Message);
        }
    }

    // ──────── internal ────────

    // Trace duration = wall-clock extent (latest span end − earliest start), not max single-span duration.
    // Root service/name come from the root span (no parent) if present, else the earliest span.
    private static TraceSummary Summarize(IGrouping<string, SpanRow> g)
    {
        long minStart = g.Min(r => r.TsMs);
        double maxEnd = g.Max(r => r.TsMs + r.DurationMs);
        SpanRow root = g.Where(r => r.ParentSpanId is null).OrderBy(r => r.TsMs).FirstOrDefault()
                       ?? g.OrderBy(r => r.TsMs).First();
        return new TraceSummary(
            TraceId: g.Key,
            Start: TelemetryTime.FromEpochMillis(minStart),
            DurationMs: maxEnd - minStart,
            SpanCount: g.Count(),
            ErrorCount: g.Count(r => r.IsError),
            RootService: root.Service,
            RootName: root.Name);
    }

    // Discrete percentile matching Postgres percentile_disc: the value at 1-based row ceil(p*N).
    private static double PercentileDisc(List<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return 0;
        int idx = Math.Clamp((int)Math.Ceiling(p * sortedAsc.Count) - 1, 0, sortedAsc.Count - 1);
        return sortedAsc[idx];
    }

    // Runs a scope query and materializes matched spans (capped) into SpanRows for C# aggregation, over
    // the given tenant's manager.
    private async Task<List<SpanRow>> LoadRowsAsync(SpanSegmentManager segments, Query q, DateTime? from, DateTime? to, CancellationToken ct)
    {
        return await segments.QueryAsync(from, to, s =>
        {
            TopDocs hits = s.Search(q, MaxSpansPerQuery);
            if (hits.TotalHits > MaxSpansPerQuery)
                logger.LogWarning("Trace query matched {Total} spans; truncated to {Cap}.", hits.TotalHits, MaxSpansPerQuery);
            var rows = new List<SpanRow>(hits.ScoreDocs.Length);
            foreach (ScoreDoc sd in hits.ScoreDocs)
                rows.Add(SpanSegmentSchema.ReadRow(s.Doc(sd.Doc)));
            return rows;
        }, ct);
    }

    private static KubernetesOperationResult<T> Fail<T>(string message = "Segment telemetry store error.")
        => KubernetesOperationResult<T>.Failure(message);

    // Distinct SortedDocValues of a field over matched docs (per leaf) — used for the service dropdown.
    private sealed class DistinctCollector(string field, HashSet<string> sink) : ICollector
    {
        private SortedDocValues? _dv;
        private readonly BytesRef _scratch = new();

        public void SetScorer(Scorer scorer) { }
        public void SetNextReader(AtomicReaderContext context) => _dv = context.AtomicReader.GetSortedDocValues(field);
        public bool AcceptsDocsOutOfOrder => true;

        public void Collect(int doc)
        {
            if (_dv is null) return;
            int ord = _dv.GetOrd(doc);
            if (ord < 0) return;
            _dv.LookupOrd(ord, _scratch);
            sink.Add(_scratch.Utf8ToString());
        }
    }
}
