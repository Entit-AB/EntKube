using EntKube.Web.Services;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The Lucene document schema for the <c>spans</c> signal plus the queries the trace views need — the
/// span-side counterpart to <see cref="LogSegmentSchema"/>. Spans have no free-text field, so filtering is
/// exact-term + range only; the trace aggregations (trace summaries, RED percentiles, service-map join)
/// materialize the matched spans and compute in C#, replacing the old SQL GROUP BY / percentile_disc /
/// self-join. A lightweight <see cref="SpanRow"/> is read from stored fields for those aggregations.
/// </summary>
public static class SpanSegmentSchema
{
    public const string Ts = "ts";                 // epoch milliseconds (UTC)
    public const string TenantId = "tenant_id";
    public const string ClusterId = "cluster_id";
    public const string TraceId = "trace_id";
    public const string SpanId = "span_id";
    public const string ParentSpanId = "parent_span_id";
    public const string Name = "name";
    public const string Service = "service";
    public const string Kind = "kind";
    public const string DurationMs = "duration_ms";
    public const string StatusCode = "status_code";
    public const string Namespace = "namespace";
    public const string Pod = "pod";
    public const string Attributes = "attributes";

    /// <summary>Spans have no analyzed field; a keyword analyzer keeps everything exact.</summary>
    public static Analyzer CreateAnalyzer() => new KeywordAnalyzer();

    public static Document ToDocument(Guid tenantId, Guid clusterId, SpanIngestRecord r)
    {
        long ms = TelemetryTime.ToEpochMillis(r.Start);
        var doc = new Document
        {
            new Int64Field(Ts, ms, Field.Store.YES),
            new NumericDocValuesField(Ts, ms),
            new StringField(TenantId, tenantId.ToString("N"), Field.Store.NO),
            new StringField(ClusterId, clusterId.ToString("N"), Field.Store.NO),
            new StringField(TraceId, r.TraceId, Field.Store.YES),
            new StringField(SpanId, r.SpanId, Field.Store.YES),
            new StringField(Name, r.Name, Field.Store.YES),
            new StringField(Service, r.Service, Field.Store.YES),
            new SortedDocValuesField(Service, new BytesRef(r.Service)),
            new Int32Field(Kind, r.Kind, Field.Store.YES),
            new DoubleField(DurationMs, r.DurationMs, Field.Store.YES),
            new Int32Field(StatusCode, r.StatusCode, Field.Store.YES),
        };
        if (!string.IsNullOrEmpty(r.ParentSpanId))
            doc.Add(new StringField(ParentSpanId, r.ParentSpanId, Field.Store.YES));
        if (!string.IsNullOrEmpty(r.Namespace))
        {
            doc.Add(new StringField(Namespace, r.Namespace, Field.Store.YES));
            doc.Add(new SortedDocValuesField(Namespace, new BytesRef(r.Namespace)));
        }
        if (!string.IsNullOrEmpty(r.Pod))
            doc.Add(new StringField(Pod, r.Pod, Field.Store.YES));
        if (!string.IsNullOrEmpty(r.AttributesJson))
            doc.Add(new StoredField(Attributes, r.AttributesJson));
        return doc;
    }

    /// <summary>
    /// Scope query: tenant + cluster (mandatory), optional time window, namespace ANY, pod prefix, and
    /// service term. Mirrors PgTraceService's NsScope/PodScope + ts + service filters.
    /// </summary>
    public static Query BuildScopeQuery(
        Guid tenantId, Guid clusterId, DateTime? from, DateTime? to,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null, string? service = null)
    {
        var q = new BooleanQuery
        {
            { new TermQuery(new Term(TenantId, tenantId.ToString("N"))), Occur.MUST },
            { new TermQuery(new Term(ClusterId, clusterId.ToString("N"))), Occur.MUST },
        };
        if (from is DateTime f || to is DateTime)
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
        if (!string.IsNullOrEmpty(podPattern))
            q.Add(BuildPodQuery(podPattern), Occur.MUST);
        if (!string.IsNullOrEmpty(service))
            q.Add(new TermQuery(new Term(Service, service)), Occur.MUST);
        return q;
    }

    /// <summary>All spans of one trace (optionally namespace-scoped) — the waterfall input.</summary>
    public static Query BuildTraceQuery(Guid tenantId, Guid clusterId, string traceId, IReadOnlyList<string>? namespaces)
    {
        var q = new BooleanQuery
        {
            { new TermQuery(new Term(TenantId, tenantId.ToString("N"))), Occur.MUST },
            { new TermQuery(new Term(ClusterId, clusterId.ToString("N"))), Occur.MUST },
            { new TermQuery(new Term(TraceId, traceId)), Occur.MUST },
        };
        if (namespaces is { Count: > 0 })
        {
            var ns = new BooleanQuery { MinimumNumberShouldMatch = 1 };
            foreach (string n in namespaces) ns.Add(new TermQuery(new Term(Namespace, n)), Occur.SHOULD);
            q.Add(ns, Occur.MUST);
        }
        return q;
    }

    // Pod filter: accept "^(a|b)", "(a|b)", or a plain name; prefix-match each alternative (pods are
    // "<workload>-<hash>"), preserving the old anchored-start regex's "starts-with" semantics.
    private static Query BuildPodQuery(string pattern)
    {
        string p = pattern.TrimStart('^');
        if (p.StartsWith('(') && p.EndsWith(')')) p = p[1..^1];
        string[] alts = p.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (alts.Length <= 1)
            return new PrefixQuery(new Term(Pod, alts.Length == 1 ? alts[0] : p));
        var pod = new BooleanQuery { MinimumNumberShouldMatch = 1 };
        foreach (string a in alts) pod.Add(new PrefixQuery(new Term(Pod, a)), Occur.SHOULD);
        return pod;
    }

    /// <summary>Materializes a span's stored fields into a <see cref="SpanRow"/> for C# aggregation.</summary>
    public static SpanRow ReadRow(Document d) => new(
        TraceId: d.Get(TraceId) ?? "",
        SpanId: d.Get(SpanId) ?? "",
        ParentSpanId: d.Get(ParentSpanId),
        Name: d.Get(Name) ?? "",
        Service: d.Get(Service) ?? "",
        Kind: (short)(d.GetField(Kind)?.GetInt32Value() ?? 0),
        DurationMs: d.GetField(DurationMs)?.GetDoubleValue() ?? 0,
        StatusCode: (short)(d.GetField(StatusCode)?.GetInt32Value() ?? 0),
        Namespace: d.Get(Namespace),
        Pod: d.Get(Pod),
        TsMs: d.GetField(Ts)?.GetInt64Value() ?? 0,
        AttributesJson: d.Get(Attributes));
}

/// <summary>A materialized span used by the trace-service C# aggregations (summaries, RED, service map).</summary>
public sealed record SpanRow(
    string TraceId, string SpanId, string? ParentSpanId, string Name, string Service,
    short Kind, double DurationMs, short StatusCode, string? Namespace, string? Pod, long TsMs, string? AttributesJson)
{
    /// <summary>SERVER(2)/CONSUMER(5) or a root span — the "inbound request" set for RED/stats.</summary>
    public bool IsInbound => Kind is 2 or 5 || ParentSpanId is null;
    public bool IsError => StatusCode == 2;
}
