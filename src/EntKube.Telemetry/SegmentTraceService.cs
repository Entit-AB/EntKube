using System.Collections.Concurrent;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using Microsoft.EntityFrameworkCore;

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
    SegmentManagerRegistry<TraceSummarySegmentManager> traceSummaries,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ClusterTenantResolver tenants,
    ILogger<SegmentTraceService> logger) : ITraceQueryService
{
    // Safety cap on how many spans an aggregation will materialize from one window. Well above a normal
    // trace-search window; if exceeded we log and truncate rather than risk unbounded memory.
    private const int MaxSpansPerQuery = 200_000;
    private const int MaxPartialsPerQuery = 200_000;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return false;
        Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, null, null);
        return await spans.For(tenantId.Value).QueryAsync(null, null, s => s.Search(scope, 1).TotalHits > 0, ct);
    }

    // The distinct-service scan touches every span in the window, so it's the page's most expensive
    // load-time call. Cache it briefly per (cluster, namespaces, pod, window) — the set changes slowly,
    // and this makes re-renders / deep-links / tab revisits instant. Static so it's shared across
    // circuits (this service is scoped). Bounded by cluster count; entries expire by TTL.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<string> Services)> ServiceListCache = new();
    private static readonly TimeSpan ServiceListTtl = TimeSpan.FromSeconds(30);

    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<string>>();

        // Scan only the window the viewer is actually searching (default 1h) instead of a fixed 24h —
        // the dropdown then reflects the selected range and, by default, opens ~24× fewer segments.
        if (windowMinutes <= 0) windowMinutes = 60;
        string cacheKey = $"{clusterId:N}|{windowMinutes}|{string.Join(",", namespaces ?? [])}|{podPattern}";
        if (ServiceListCache.TryGetValue(cacheKey, out (DateTime At, List<string> Services) hit)
            && DateTime.UtcNow - hit.At < ServiceListTtl)
        {
            return KubernetesOperationResult<List<string>>.Success(hit.Services);
        }

        DateTime from = DateTime.UtcNow.AddMinutes(-windowMinutes);
        try
        {
            Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId.Value, clusterId, from, null, namespaces, podPattern);
            var sink = new HashSet<string>(StringComparer.Ordinal);
            await spans.For(tenantId.Value).QueryAsync(from, null,
                s => { s.Search(scope, new DistinctCollector(SpanSegmentSchema.Service, sink)); return 0; }, ct);
            List<string> result = sink.Where(v => v.Length > 0).OrderBy(v => v, StringComparer.Ordinal).ToList();
            ServiceListCache[cacheKey] = (DateTime.UtcNow, result);
            return KubernetesOperationResult<List<string>>.Success(result);
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

        // The pre-aggregated trace-summary index answers the list cheaply — EXCEPT when a pod-pattern is
        // filtered (exact only from spans) or the window reaches back before the index had partials. Those
        // fall back to scanning spans directly.
        if (string.IsNullOrEmpty(podPattern) && from >= await TracesIndexCutoffAsync(tenantId.Value, ct))
            return await ListTracesFromIndexAsync(
                tenantId.Value, clusterId, service, from, to, minDurationMs, errorsOnly, limit, namespaces, ct);

        return await ListTracesFromSpansAsync(
            tenantId.Value, clusterId, service, from, to, minDurationMs, errorsOnly, limit, namespaces, podPattern, ct);
    }

    // ── Fast path: merge pre-aggregated per-(trace,namespace,batch) partials from the trace-summary index ──
    private async Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesFromIndexAsync(
        Guid tenantId, Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs, bool errorsOnly, int limit, IReadOnlyList<string>? namespaces, CancellationToken ct)
    {
        try
        {
            Query scope = TraceSummarySchema.BuildWindowScope(tenantId, clusterId, from, to, namespaces);
            List<TraceSummaryPartial> partials = await traceSummaries.For(tenantId).QueryAsync(from, to, s =>
            {
                TopDocs hits = s.Search(scope, MaxPartialsPerQuery);
                if (hits.TotalHits > MaxPartialsPerQuery)
                    logger.LogWarning("Trace-summary query matched {Total} partials; truncated to {Cap}.", hits.TotalHits, MaxPartialsPerQuery);
                var list = new List<TraceSummaryPartial>(hits.ScoreDocs.Length);
                foreach (ScoreDoc sd in hits.ScoreDocs)
                    list.Add(TraceSummarySchema.ReadPartial(s.Doc(sd.Doc)));
                return list;
            }, ct);

            // DISTINCT by partial_id (collapses OTLP whole-batch retries) → group by trace → merge.
            var seenPartials = new HashSet<string>(StringComparer.Ordinal);
            var byTrace = new Dictionary<string, List<TraceSummaryPartial>>(StringComparer.Ordinal);
            foreach (TraceSummaryPartial p in partials)
            {
                if (p.PartialId.Length > 0 && !seenPartials.Add(p.PartialId)) continue;
                if (!byTrace.TryGetValue(p.TraceId, out List<TraceSummaryPartial>? list))
                    byTrace[p.TraceId] = list = [];
                list.Add(p);
            }

            List<TraceSummary> summaries = byTrace.Values
                .Select(MergePartials)
                .Where(x => x.Summary.DurationMs >= minDurationMs
                            && (!errorsOnly || x.Summary.ErrorCount > 0)
                            && (string.IsNullOrEmpty(service) || x.Services.Contains(service)))
                .Select(x => x.Summary)
                .OrderByDescending(t => t.Start)
                .Take(limit)
                .ToList();
            return KubernetesOperationResult<List<TraceSummary>>.Success(summaries);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment trace-summary list failed (cluster {Cluster})", clusterId);
            return Fail<List<TraceSummary>>(ex.Message);
        }
    }

    // Reassembles a trace from its partials: start=min, end=max, counts summed, root = earliest partial that
    // saw a root span, else the earliest partial's earliest-start span. Services unioned for the filter.
    private static (TraceSummary Summary, HashSet<string> Services) MergePartials(List<TraceSummaryPartial> ps)
    {
        long start = ps.Min(p => p.StartMs);
        double end = ps.Max(p => p.EndMs);
        int spanCount = ps.Sum(p => p.SpanCount);
        int errorCount = ps.Sum(p => p.ErrorCount);
        var services = new HashSet<string>(ps.SelectMany(p => p.Services), StringComparer.Ordinal);

        string rootService, rootName;
        TraceSummaryPartial? rooted = ps.Where(p => p.RootService is not null)
            .OrderBy(p => p.RootTs ?? p.StartMs).FirstOrDefault();
        if (rooted is not null)
        {
            rootService = rooted.RootService!;
            rootName = rooted.RootName ?? "";
        }
        else
        {
            TraceSummaryPartial e = ps.OrderBy(p => p.StartMs).First();
            rootService = e.EarliestService;
            rootName = e.EarliestName;
        }

        var summary = new TraceSummary(
            TraceId: ps[0].TraceId,
            Start: TelemetryTime.FromEpochMillis(start),
            DurationMs: end - start,
            SpanCount: spanCount,
            ErrorCount: errorCount,
            RootService: rootService,
            RootName: rootName);
        return (summary, services);
    }

    // Earliest time the trace-summary index holds partials for this tenant (sealed catalog MIN + active
    // index MIN). Windows starting before it must use the span path (pre-index history). Cached briefly.
    private static readonly ConcurrentDictionary<Guid, (DateTime Cutoff, DateTime At)> CutoffCache = new();
    private static readonly TimeSpan CutoffTtl = TimeSpan.FromSeconds(60);

    private async Task<DateTime> TracesIndexCutoffAsync(Guid tenantId, CancellationToken ct)
    {
        if (CutoffCache.TryGetValue(tenantId, out (DateTime Cutoff, DateTime At) c) && DateTime.UtcNow - c.At < CutoffTtl)
            return c.Cutoff;

        DateTime? sealedMin;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
            sealedMin = await db.TelemetrySegments
                .Where(seg => seg.TenantId == tenantId && seg.Signal == "traces")
                .MinAsync(seg => (DateTime?)seg.MinTs, ct);

        DateTime? activeMin = traceSummaries.For(tenantId).ActiveMinTs;
        DateTime cutoff = (sealedMin, activeMin) switch
        {
            (null, null) => DateTime.MaxValue,                 // no partials yet → always use the span path
            (DateTime s, null) => s,
            (null, DateTime a) => a,
            (DateTime s, DateTime a) => s < a ? s : a,
        };
        CutoffCache[tenantId] = (cutoff, DateTime.UtcNow);
        return cutoff;
    }

    // ── Fallback path: aggregate raw spans (used for pod-pattern filters and pre-index windows) ──
    private async Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesFromSpansAsync(
        Guid tenantId, Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs, bool errorsOnly, int limit, IReadOnlyList<string>? namespaces, string? podPattern, CancellationToken ct)
    {
        try
        {
            SpanSegmentManager mgr = spans.For(tenantId);
            List<SpanRow> rows;

            // A service is selected with no per-trace filter → two-phase (find recent involving traces, then
            // load just those). Per-trace filters (minDuration/errorsOnly) fall back to the full scan.
            if (!string.IsNullOrEmpty(service) && minDurationMs <= 0 && !errorsOnly)
            {
                List<string> traceIds = await RecentServiceTraceIdsAsync(
                    mgr, tenantId, clusterId, from, to, namespaces, podPattern, service, limit, ct);
                if (traceIds.Count == 0)
                    return KubernetesOperationResult<List<TraceSummary>>.Success([]);

                Query tracesScope = BuildTracesScope(tenantId, clusterId, from, to, namespaces, podPattern, traceIds);
                rows = await LoadRowsAsync(mgr, tracesScope, from, to, ct, AggregationFields);
            }
            else
            {
                Query scope = SpanSegmentSchema.BuildScopeQuery(tenantId, clusterId, from, to, namespaces, podPattern);
                rows = await LoadRowsAsync(mgr, scope, from, to, ct, AggregationFields);
            }

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
            logger.LogWarning(ex, "Segment trace list (span path) failed (cluster {Cluster})", clusterId);
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
            List<SpanRow> rows = await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct, AggregationFields);

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
            List<SpanRow> rows = await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct, AggregationFields);

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
            List<SpanRow> rows = (await LoadRowsAsync(spans.For(tenantId.Value), scope, from, to, ct, AggregationFields)).Where(r => r.IsInbound).ToList();
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

    private static readonly ISet<string> TraceIdFieldOnly = new HashSet<string> { SpanSegmentSchema.TraceId };

    // Phase 1 of the service-filtered trace list: the ids of the most-recently-active traces that
    // involve the service. Searches the service's spans sorted newest-first and reads only trace ids,
    // stopping once `limit` distinct traces are collected — so it touches a bounded number of docs.
    private async Task<List<string>> RecentServiceTraceIdsAsync(
        SpanSegmentManager mgr, Guid tenantId, Guid clusterId, DateTime from, DateTime to,
        IReadOnlyList<string>? namespaces, string? podPattern, string service, int limit, CancellationToken ct)
    {
        Query svcScope = SpanSegmentSchema.BuildScopeQuery(tenantId, clusterId, from, to, namespaces, podPattern, service);
        var sort = new Sort(new SortField(SpanSegmentSchema.Ts, SortFieldType.INT64, reverse: true)); // newest first
        int fetch = Math.Max(2000, limit * 100);
        return await mgr.QueryAsync(from, to, s =>
        {
            TopDocs hits = s.Search(svcScope, fetch, sort);
            var ids = new List<string>(limit);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScoreDoc sd in hits.ScoreDocs)
            {
                string tid = s.Doc(sd.Doc, TraceIdFieldOnly).Get(SpanSegmentSchema.TraceId) ?? "";
                if (tid.Length > 0 && seen.Add(tid))
                {
                    ids.Add(tid);
                    if (ids.Count >= limit) break;
                }
            }
            return ids;
        }, ct);
    }

    // Phase 2 scope: the same (cluster/ns/pod/window) filter restricted to a specific set of trace ids.
    private static Query BuildTracesScope(
        Guid tenantId, Guid clusterId, DateTime from, DateTime to,
        IReadOnlyList<string>? namespaces, string? podPattern, IReadOnlyList<string> traceIds)
    {
        Query baseScope = SpanSegmentSchema.BuildScopeQuery(tenantId, clusterId, from, to, namespaces, podPattern);
        var traceSet = new BooleanQuery { MinimumNumberShouldMatch = 1 };
        foreach (string tid in traceIds)
            traceSet.Add(new TermQuery(new Term(SpanSegmentSchema.TraceId, tid)), Occur.SHOULD);
        return new BooleanQuery { { baseScope, Occur.MUST }, { traceSet, Occur.MUST } };
    }

    // The fields the C# aggregations (summaries, RED, service map, stats) actually read. Loading only
    // these — NOT the whole stored document — skips the potentially-large `attributes` JSON blob per
    // span, which none of the aggregations use. Cuts the per-span materialization cost dramatically.
    private static readonly ISet<string> AggregationFields = new HashSet<string>
    {
        SpanSegmentSchema.TraceId, SpanSegmentSchema.SpanId, SpanSegmentSchema.ParentSpanId,
        SpanSegmentSchema.Name, SpanSegmentSchema.Service, SpanSegmentSchema.Kind,
        SpanSegmentSchema.DurationMs, SpanSegmentSchema.StatusCode, SpanSegmentSchema.Ts,
    };

    // Runs a scope query and materializes matched spans (capped) into SpanRows for C# aggregation, over
    // the given tenant's manager. Pass <paramref name="fields"/> to load only a subset of stored fields
    // (aggregations); null loads the full document (the waterfall, which needs attributes).
    private async Task<List<SpanRow>> LoadRowsAsync(
        SpanSegmentManager segments, Query q, DateTime? from, DateTime? to, CancellationToken ct, ISet<string>? fields = null)
    {
        return await segments.QueryAsync(from, to, s =>
        {
            TopDocs hits = s.Search(q, MaxSpansPerQuery);
            if (hits.TotalHits > MaxSpansPerQuery)
                logger.LogWarning("Trace query matched {Total} spans; truncated to {Cap}.", hits.TotalHits, MaxSpansPerQuery);
            var rows = new List<SpanRow>(hits.ScoreDocs.Length);
            foreach (ScoreDoc sd in hits.ScoreDocs)
                rows.Add(SpanSegmentSchema.ReadRow(fields is null ? s.Doc(sd.Doc) : s.Doc(sd.Doc, fields)));
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
