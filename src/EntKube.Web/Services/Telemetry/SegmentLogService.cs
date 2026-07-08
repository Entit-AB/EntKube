using EntKube.Web.Services;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Lucene/S3 segment-engine implementation of <see cref="ILogBackend"/> — the drop-in replacement for
/// <see cref="PgLogService"/>. Returns the exact same <see cref="LokiLogStream"/>/<see cref="LogHistogramBucket"/>
/// DTOs, so <see cref="LogQueryService"/> and the log viewers are unchanged. Every query runs through
/// <see cref="LogSegmentManager"/>, which unions the active index with the sealed segments overlapping the
/// requested window into one <see cref="IndexSearcher"/> — so a query touches only the segments that can
/// hold rows in its time range. Filtering/sorting use the inverted index; the volume histogram and counts
/// aggregate DocValues in C#.
/// </summary>
public sealed class SegmentLogService(
    SegmentManagerRegistry<LogSegmentManager> logs,
    ClusterTenantResolver tenants,
    ILogger<SegmentLogService> logger) : ILogBackend
{
    public bool IsEnabled => true;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return false;
        LogSegmentManager segments = logs.For(tenantId.Value);
        return await segments.QueryAsync(null, null,
            s => s.Search(ScopeQuery(tenantId.Value, clusterId), 1).TotalHits > 0, ct);
    }

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(Guid clusterId, CancellationToken ct = default)
        => DistinctLabelAsync(clusterId, LogSegmentSchema.Namespace, null, ct);

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
        => DistinctLabelAsync(clusterId, LogSegmentSchema.Pod, namespaceName, ct);

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
        => DistinctLabelAsync(clusterId, LogSegmentSchema.Container, namespaceName, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
    {
        if (filter.Namespaces.Count == 0)
            return KubernetesOperationResult<List<LokiLogStream>>.Success([]);

        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<LokiLogStream>>();

        try
        {
            LogSegmentManager segments = logs.For(tenantId.Value);
            Query q = LogSegmentSchema.BuildQuery(tenantId.Value, clusterId, filter, from, to, segments.Analyzer);
            var sort = new Sort(new SortField(LogSegmentSchema.Ts, SortFieldType.INT64, reverse: true)); // ts DESC
            List<LokiLogStream> streams = await segments.QueryAsync(
                from, to, s => MapStreams(s, s.Search(q, limit, sort)), ct);
            return KubernetesOperationResult<List<LokiLogStream>>.Success(streams);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment log range query failed (cluster {Cluster})", clusterId);
            return Fail<List<LokiLogStream>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<LokiLogStream>>();

        try
        {
            LogSegmentManager segments = logs.For(tenantId.Value);
            Query q = LogSegmentSchema.BuildTraceQuery(tenantId.Value, clusterId, traceId);
            var sort = new Sort(new SortField(LogSegmentSchema.Ts, SortFieldType.INT64, reverse: true));
            // A trace can span any time; search all segments.
            List<LokiLogStream> streams = await segments.QueryAsync(
                null, null, s => MapStreams(s, s.Search(q, limit, sort)), ct);
            return KubernetesOperationResult<List<LokiLogStream>>.Success(streams);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment trace-logs query failed (cluster {Cluster}, trace {Trace})", clusterId, traceId);
            return Fail<List<LokiLogStream>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
    {
        if (filter.Namespaces.Count == 0)
            return KubernetesOperationResult<List<LogHistogramBucket>>.Success([]);

        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<LogHistogramBucket>>();

        long fromMs = LogSegmentSchema.ToEpochMillis(from);
        long toMs = LogSegmentSchema.ToEpochMillis(to);
        long bucketMs = Math.Max(1000, (long)Math.Ceiling((toMs - fromMs) / (double)Math.Max(1, buckets)));

        try
        {
            LogSegmentManager segments = logs.For(tenantId.Value);
            Query q = LogSegmentSchema.BuildQuery(tenantId.Value, clusterId, filter, from, to, segments.Analyzer);
            var collector = new HistogramCollector(fromMs, bucketMs);
            await segments.QueryAsync(from, to, s => { s.Search(q, collector); return 0; }, ct);

            List<LogHistogramBucket> result = collector.Buckets
                .OrderBy(kv => kv.Key)
                .Select(kv => new LogHistogramBucket(
                    LogSegmentSchema.FromEpochMillis(fromMs + kv.Key * bucketMs), kv.Value.Total, kv.Value.Errors))
                .ToList();
            return KubernetesOperationResult<List<LogHistogramBucket>>.Success(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment histogram query failed (cluster {Cluster})", clusterId);
            return Fail<List<LogHistogramBucket>>(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<long>();

        try
        {
            LogSegmentManager segments = logs.For(tenantId.Value);
            var filter = new LogQueryFilter
            {
                Namespaces = string.IsNullOrEmpty(ns) ? [] : [ns],
                Text = matchText,
                MinLevel = minLevel,
            };
            Query q = LogSegmentSchema.BuildQuery(tenantId.Value, clusterId, filter, from, to, segments.Analyzer);
            long total = await segments.QueryAsync(from, to, s =>
            {
                var counter = new TotalHitCountCollector();
                s.Search(q, counter);
                return (long)counter.TotalHits;
            }, ct);
            return KubernetesOperationResult<long>.Success(total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment log count failed (cluster {Cluster})", clusterId);
            return Fail<long>(ex.Message);
        }
    }

    // ──────── internal ────────

    private async Task<KubernetesOperationResult<List<string>>> DistinctLabelAsync(
        Guid clusterId, string field, string? namespaceName, CancellationToken ct)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<string>>();

        try
        {
            LogSegmentManager segments = logs.For(tenantId.Value);
            var scope = new BooleanQuery
            {
                { new TermQuery(new Term(LogSegmentSchema.TenantId, tenantId.Value.ToString("N"))), Occur.MUST },
                { new TermQuery(new Term(LogSegmentSchema.ClusterId, clusterId.ToString("N"))), Occur.MUST },
            };
            if (field != LogSegmentSchema.Namespace && !string.IsNullOrEmpty(namespaceName))
                scope.Add(new TermQuery(new Term(LogSegmentSchema.Namespace, namespaceName)), Occur.MUST);

            var sink = new HashSet<string>(StringComparer.Ordinal);
            await segments.QueryAsync(null, null, s => { s.Search(scope, new DistinctCollector(field, sink)); return 0; }, ct);

            List<string> values = sink.Where(v => v.Length > 0).OrderBy(v => v, StringComparer.Ordinal).ToList();
            return KubernetesOperationResult<List<string>>.Success(values);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment label query failed ({Field}, cluster {Cluster})", field, clusterId);
            return Fail<List<string>>(ex.Message);
        }
    }

    private static Query ScopeQuery(Guid tenantId, Guid clusterId) => new BooleanQuery
    {
        { new TermQuery(new Term(LogSegmentSchema.TenantId, tenantId.ToString("N"))), Occur.MUST },
        { new TermQuery(new Term(LogSegmentSchema.ClusterId, clusterId.ToString("N"))), Occur.MUST },
    };

    // Groups hits into streams by (namespace, pod, container) — the shape the viewers expect. Mirrors
    // PgLogService.MapStreamsAsync. Works over a MultiReader (active + sealed) transparently.
    private static List<LokiLogStream> MapStreams(IndexSearcher searcher, TopDocs hits)
    {
        Dictionary<(string, string, string), LokiLogStream> streams = [];
        foreach (ScoreDoc sd in hits.ScoreDocs)
        {
            Document d = searcher.Doc(sd.Doc);
            string ns = d.Get(LogSegmentSchema.Namespace) ?? "";
            string pod = d.Get(LogSegmentSchema.Pod) ?? "";
            string container = d.Get(LogSegmentSchema.Container) ?? "";
            short sev = (short)(d.GetField(LogSegmentSchema.Severity)?.GetInt32Value() ?? 0);
            string body = d.Get(LogSegmentSchema.Body) ?? "";
            long tsMs = d.GetField(LogSegmentSchema.Ts)?.GetInt64Value() ?? 0;
            string? traceId = d.Get(LogSegmentSchema.TraceId);

            (string, string, string) key = (ns, pod, container);
            if (!streams.TryGetValue(key, out LokiLogStream? stream))
            {
                stream = new LokiLogStream
                {
                    Labels = new() { ["namespace"] = ns, ["pod"] = pod, ["container"] = container },
                };
                streams[key] = stream;
            }
            stream.Entries.Add(new LokiLogEntry
            {
                Timestamp = LogSegmentSchema.FromEpochMillis(tsMs),
                Line = body,
                DetectedLevel = (LogLevel)sev,
                TraceId = traceId,
            });
        }
        return [.. streams.Values];
    }

    private static KubernetesOperationResult<T> Fail<T>(string message = "Segment telemetry store error.")
        => KubernetesOperationResult<T>.Failure(message);

    // Collects distinct SortedDocValues of a field over matched docs (per leaf), resolving each ordinal to
    // its string — a cheap, index-native distinct for the label dropdowns.
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

    // Buckets matched docs by (ts - from) / bucketMs using ts + severity DocValues; counts total and the
    // error+fatal (severity >= Error) subcount — the C# equivalent of the old date_bin + FILTER SQL.
    private sealed class HistogramCollector(long fromMs, long bucketMs) : ICollector
    {
        private NumericDocValues? _ts;
        private NumericDocValues? _sev;
        public Dictionary<long, (long Total, long Errors)> Buckets { get; } = [];

        public void SetScorer(Scorer scorer) { }
        public bool AcceptsDocsOutOfOrder => true;
        public void SetNextReader(AtomicReaderContext context)
        {
            _ts = context.AtomicReader.GetNumericDocValues(LogSegmentSchema.Ts);
            _sev = context.AtomicReader.GetNumericDocValues(LogSegmentSchema.Severity);
        }

        public void Collect(int doc)
        {
            if (_ts is null) return;
            long b = (_ts.Get(doc) - fromMs) / bucketMs;
            bool isError = _sev is not null && _sev.Get(doc) >= (long)LogLevel.Error;
            (long Total, long Errors) cur = Buckets.TryGetValue(b, out var v) ? v : (0, 0);
            Buckets[b] = (cur.Total + 1, cur.Errors + (isError ? 1 : 0));
        }
    }
}
