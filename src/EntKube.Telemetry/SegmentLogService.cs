using System.Collections.Concurrent;
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
    LogTierRegistries tiers,
    ClusterTenantResolver tenants,
    ILogger<SegmentLogService> logger) : ILogBackend
{
    public bool IsEnabled => true;

    private const int DefaultDiscoveryWindowMinutes = 60;

    // Label discovery (namespaces/pods/containers) scans a distinct-collector over the window — the
    // dropdown-population cost. Cache briefly per (tenant, cluster, field, ns, window): the set changes
    // slowly, so re-renders / reopens are instant. Static (shared across scopes/circuits).
    private static readonly ConcurrentDictionary<string, (DateTime At, List<string> Values)> LabelCache = new();
    private static readonly TimeSpan LabelTtl = TimeSpan.FromSeconds(30);

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return false;
        // "Has recent data" is the routing signal — bound to a week so this doesn't open a reader for
        // every segment across the (up to 90-day) retention window just to check existence. Either tier
        // having data counts.
        DateTime from = DateTime.UtcNow.AddDays(-7);
        foreach (LogSegmentManager segments in tiers.QueryManagers(tenantId.Value))
        {
            bool any = await segments.QueryAsync(from, null,
                s => s.Search(ScopeQuery(tenantId.Value, clusterId), 1).TotalHits > 0, ct);
            if (any) return true;
        }
        return false;
    }

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = DefaultDiscoveryWindowMinutes, CancellationToken ct = default)
        => DistinctLabelAsync(clusterId, LogSegmentSchema.Namespace, null, windowMinutes, ct);

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = DefaultDiscoveryWindowMinutes, CancellationToken ct = default)
        => DistinctLabelAsync(clusterId, LogSegmentSchema.Pod, namespaceName, windowMinutes, ct);

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = DefaultDiscoveryWindowMinutes, CancellationToken ct = default)
        => DistinctLabelAsync(clusterId, LogSegmentSchema.Container, namespaceName, windowMinutes, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
    {
        if (filter.Namespaces.Count == 0)
            return KubernetesOperationResult<List<LokiLogStream>>.Success([]);

        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<LokiLogStream>>();

        try
        {
            var all = new List<LokiLogStream>();
            var sort = new Sort(new SortField(LogSegmentSchema.Ts, SortFieldType.INT64, reverse: true)); // ts DESC
            // Union the retention tiers that can match this min level, each returning its newest `limit`.
            foreach (LogSegmentManager segments in tiers.QueryManagers(tenantId.Value, filter.MinLevel))
            {
                Query q = LogSegmentSchema.BuildQuery(tenantId.Value, clusterId, filter, from, to, segments.Analyzer);
                all.AddRange(await segments.QueryAsync(
                    from, to, s => MapStreams(s, s.Search(q, limit, sort)), ct));
            }
            return KubernetesOperationResult<List<LokiLogStream>>.Success(MergeStreams(all, limit));
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
            var all = new List<LokiLogStream>();
            Query q = LogSegmentSchema.BuildTraceQuery(tenantId.Value, clusterId, traceId);
            var sort = new Sort(new SortField(LogSegmentSchema.Ts, SortFieldType.INT64, reverse: true));
            // A trace's lines can be any severity, so search both tiers; a trace can span any time (all segments).
            foreach (LogSegmentManager segments in tiers.QueryManagers(tenantId.Value))
                all.AddRange(await segments.QueryAsync(
                    null, null, s => MapStreams(s, s.Search(q, limit, sort)), ct));
            return KubernetesOperationResult<List<LokiLogStream>>.Success(MergeStreams(all, limit));
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
            // One collector accumulates across both tiers (each search adds into the same buckets).
            var collector = new HistogramCollector(fromMs, bucketMs);
            foreach (LogSegmentManager segments in tiers.QueryManagers(tenantId.Value, filter.MinLevel))
            {
                Query q = LogSegmentSchema.BuildQuery(tenantId.Value, clusterId, filter, from, to, segments.Analyzer);
                await segments.QueryAsync(from, to, s => { s.Search(q, collector); return 0; }, ct);
            }

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
            var filter = new LogQueryFilter
            {
                Namespaces = string.IsNullOrEmpty(ns) ? [] : [ns],
                Text = matchText,
                MinLevel = minLevel,
            };
            long total = 0;
            foreach (LogSegmentManager segments in tiers.QueryManagers(tenantId.Value, minLevel))
            {
                Query q = LogSegmentSchema.BuildQuery(tenantId.Value, clusterId, filter, from, to, segments.Analyzer);
                total += await segments.QueryAsync(from, to, s =>
                {
                    var counter = new TotalHitCountCollector();
                    s.Search(q, counter);
                    return (long)counter.TotalHits;
                }, ct);
            }
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
        Guid clusterId, string field, string? namespaceName, int windowMinutes, CancellationToken ct)
    {
        Guid? tenantId = await tenants.ResolveAsync(clusterId, ct);
        if (tenantId is null) return Fail<List<string>>();

        if (windowMinutes <= 0) windowMinutes = DefaultDiscoveryWindowMinutes;
        string cacheKey = $"{tenantId.Value:N}|{clusterId:N}|{field}|{namespaceName}|{windowMinutes}";
        if (LabelCache.TryGetValue(cacheKey, out (DateTime At, List<string> Values) c)
            && DateTime.UtcNow - c.At < LabelTtl)
        {
            return KubernetesOperationResult<List<string>>.Success(c.Values);
        }

        try
        {
            var scope = new BooleanQuery
            {
                { new TermQuery(new Term(LogSegmentSchema.TenantId, tenantId.Value.ToString("N"))), Occur.MUST },
                { new TermQuery(new Term(LogSegmentSchema.ClusterId, clusterId.ToString("N"))), Occur.MUST },
            };
            if (field != LogSegmentSchema.Namespace && !string.IsNullOrEmpty(namespaceName))
                scope.Add(new TermQuery(new Term(LogSegmentSchema.Namespace, namespaceName)), Occur.MUST);

            // Scan only the requested window (default 1h) instead of ALL segments — with 90-day
            // retention, unbounded discovery opened a reader per segment and visited every doc. A label may
            // exist in only one tier (e.g. a namespace that logs only INFO), so union both into one sink.
            DateTime from = DateTime.UtcNow.AddMinutes(-windowMinutes);
            var sink = new HashSet<string>(StringComparer.Ordinal);
            foreach (LogSegmentManager segments in tiers.QueryManagers(tenantId.Value))
                await segments.QueryAsync(from, null, s => { s.Search(scope, new DistinctCollector(field, sink)); return 0; }, ct);

            List<string> values = sink.Where(v => v.Length > 0).OrderBy(v => v, StringComparer.Ordinal).ToList();
            LabelCache[cacheKey] = (DateTime.UtcNow, values);
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

    // Merges per-tier stream lists into one: keeps the globally newest `limit` entries across both tiers
    // (each tier already returned its own newest `limit`, so the union is correct), then regroups them by
    // (namespace, pod, container). A single-tier result passes through unchanged apart from the truncation.
    private static List<LokiLogStream> MergeStreams(List<LokiLogStream> streams, int limit)
    {
        if (streams.Count <= 1) return streams;

        var flat = streams
            .SelectMany(s => s.Entries.Select(e => (Labels: s.Labels, Entry: e)))
            .OrderByDescending(x => x.Entry.Timestamp)
            .Take(limit);

        Dictionary<(string, string, string), LokiLogStream> merged = [];
        foreach ((Dictionary<string, string> labels, LokiLogEntry entry) in flat)
        {
            (string, string, string) key = (
                labels.GetValueOrDefault("namespace", ""),
                labels.GetValueOrDefault("pod", ""),
                labels.GetValueOrDefault("container", ""));
            if (!merged.TryGetValue(key, out LokiLogStream? stream))
            {
                stream = new LokiLogStream { Labels = new Dictionary<string, string>(labels) };
                merged[key] = stream;
            }
            stream.Entries.Add(entry);
        }
        return [.. merged.Values];
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
