using EntKube.Web.Services;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Lucene schema for the <c>traces</c> signal — the trace-SUMMARY index. Each document is a <b>partial</b>
/// summary: the aggregate of one trace's spans in one namespace within a single ingest batch (spans of a
/// trace arrive across batches, and the engine is append-only, so we emit partials and merge them at query
/// time in <see cref="SegmentTraceService"/>). Partials are namespace-scoped so a namespace-filtered trace
/// list merges only that namespace's partials (exact + fast); an unfiltered list merges all of a trace's
/// partials. Only fields the merge filters on (cluster/namespace/ts) are indexed; the rest are stored-only.
/// </summary>
public static class TraceSummarySchema
{
    public const string Ts = "ts";                     // epoch ms of the earliest span start in this partial
    public const string TenantId = "tenant_id";
    public const string ClusterId = "cluster_id";
    public const string Namespace = "namespace";       // this partial's namespace ("" when the spans had none)
    public const string TraceId = "trace_id";
    public const string EndMs = "end_ms";              // epoch ms of the latest span end in this partial
    public const string SpanCount = "span_count";
    public const string ErrorCount = "error_count";
    public const string Service = "service";           // multi-valued: distinct services in this partial
    public const string EarliestService = "earliest_service";
    public const string EarliestName = "earliest_name";
    public const string RootService = "root_service";  // present only when this partial holds a root span
    public const string RootName = "root_name";
    public const string RootTs = "root_ts";
    public const string PartialId = "partial_id";      // dedup key (content hash) — see SegmentTraceService merge

    public static Analyzer CreateAnalyzer() => new KeywordAnalyzer();

    public static Document ToDocument(Guid tenantId, Guid clusterId, TraceSummaryPartial p)
    {
        var doc = new Document
        {
            new Int64Field(Ts, p.StartMs, Field.Store.YES),                       // indexed (range) + stored (read)
            new StringField(TenantId, tenantId.ToString("N"), Field.Store.NO),
            new StringField(ClusterId, clusterId.ToString("N"), Field.Store.NO),
            new StringField(Namespace, p.Namespace, Field.Store.YES),             // indexed (ns filter) + stored
            new StoredField(TraceId, p.TraceId),
            new StoredField(EndMs, p.EndMs),
            new StoredField(SpanCount, p.SpanCount),
            new StoredField(ErrorCount, p.ErrorCount),
            new StoredField(EarliestService, p.EarliestService),
            new StoredField(EarliestName, p.EarliestName),
            new StoredField(PartialId, p.PartialId),
        };
        foreach (string svc in p.Services)
            doc.Add(new StoredField(Service, svc));                               // multi-valued, read via GetValues
        if (p.RootService is not null)
        {
            doc.Add(new StoredField(RootService, p.RootService));
            doc.Add(new StoredField(RootName, p.RootName ?? ""));
            doc.Add(new StoredField(RootTs, p.RootTs ?? p.StartMs));
        }
        return doc;
    }

    /// <summary>
    /// The partial-selection query: tenant + cluster (mandatory), the time window, and — when supplied — a
    /// namespace ANY clause. No service/pod filtering here: the single-phase merge needs ALL of a trace's
    /// in-scope partials, and applies the service/errors/duration filters on the merged summary.
    /// </summary>
    public static Query BuildWindowScope(
        Guid tenantId, Guid clusterId, DateTime? from, DateTime? to, IReadOnlyList<string>? namespaces)
    {
        var q = new BooleanQuery
        {
            { new TermQuery(new Term(TenantId, tenantId.ToString("N"))), Occur.MUST },
            { new TermQuery(new Term(ClusterId, clusterId.ToString("N"))), Occur.MUST },
        };
        if (from is DateTime || to is DateTime)
            q.Add(NumericRangeQuery.NewInt64Range(Ts,
                from is DateTime ff ? TelemetryTime.ToEpochMillis(ff) : long.MinValue,
                to is DateTime tt ? TelemetryTime.ToEpochMillis(tt) : long.MaxValue,
                minInclusive: true, maxInclusive: to is null), Occur.MUST);

        if (namespaces is { Count: > 0 })
        {
            var ns = new BooleanQuery { MinimumNumberShouldMatch = 1 };
            foreach (string n in namespaces) ns.Add(new TermQuery(new Term(Namespace, n)), Occur.SHOULD);
            q.Add(ns, Occur.MUST);
        }
        return q;
    }

    public static TraceSummaryPartial ReadPartial(Document d) => new(
        TraceId: d.Get(TraceId) ?? "",
        Namespace: d.Get(Namespace) ?? "",
        StartMs: d.GetField(Ts)?.GetInt64Value() ?? 0,
        EndMs: d.GetField(EndMs)?.GetDoubleValue() ?? 0,
        SpanCount: d.GetField(SpanCount)?.GetInt32Value() ?? 0,
        ErrorCount: d.GetField(ErrorCount)?.GetInt32Value() ?? 0,
        Services: d.GetValues(Service),
        EarliestService: d.Get(EarliestService) ?? "",
        EarliestName: d.Get(EarliestName) ?? "",
        RootService: d.Get(RootService),
        RootName: d.Get(RootName),
        RootTs: d.GetField(RootTs)?.GetInt64Value(),
        PartialId: d.Get(PartialId) ?? "");
}

/// <summary>One partial trace summary: a trace's spans in one namespace within one ingest batch.</summary>
public sealed record TraceSummaryPartial(
    string TraceId, string Namespace, long StartMs, double EndMs, int SpanCount, int ErrorCount,
    string[] Services, string EarliestService, string EarliestName,
    string? RootService, string? RootName, long? RootTs, string PartialId);
