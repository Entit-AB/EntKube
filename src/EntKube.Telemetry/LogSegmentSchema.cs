using System.Text.Json;
using EntKube.Web.Services;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The Lucene document schema for the <c>logs</c> signal, plus the translation of a
/// <see cref="LogQueryFilter"/> into a Lucene <see cref="Query"/>. This is the single source of truth
/// for how a <see cref="LogIngestRecord"/> becomes an indexed+stored document and how the log viewer's
/// filters map onto that index — mirroring the columns and WHERE clauses of the old <c>PgLogService</c>.
///
/// Field design (see the plan): <c>ts</c> is a range-searchable point plus a DocValue for sort/aggregation;
/// scope/label fields are non-analyzed <see cref="StringField"/>s (exact terms) with a SortedDocValue for
/// fast per-doc columnar reads; <c>body</c> is analyzed for token full-text search and stored for
/// retrieval; attributes are flattened to <c>attr_key</c>/<c>attr_kv</c> terms so the structured filter
/// is an index lookup rather than a JSON scan.
/// </summary>
public static class LogSegmentSchema
{
    public const LuceneVersion Version = LuceneVersion.LUCENE_48;

    // Field names — kept as constants so the writer and the query builder can never drift apart.
    public const string Ts = "ts";                 // epoch milliseconds (UTC)
    public const string TenantId = "tenant_id";
    public const string ClusterId = "cluster_id";
    public const string Namespace = "namespace";
    public const string Pod = "pod";
    public const string Container = "container";
    public const string Severity = "severity";
    public const string Body = "body";
    public const string TraceId = "trace_id";
    public const string Attributes = "attributes";  // raw JSON, stored only
    public const string AttrKey = "attr_key";        // one per attribute key (existence filter)
    public const string AttrKv = "attr_kv";          // keyvalue (equality filter)

    private const char KvSep = '\u001F'; // ASCII unit separator; cannot appear in a JSON key/value token

    /// <summary>A fresh analyzer for the body full-text field. Standard tokenizer + lowercase.</summary>
    public static Analyzer CreateAnalyzer() => new StandardAnalyzer(Version, CharArraySet.Empty);

    public static long ToEpochMillis(DateTime ts) =>
        new DateTimeOffset(ts.ToUniversalTime()).ToUnixTimeMilliseconds();

    public static DateTime FromEpochMillis(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>Builds the indexed+stored Lucene document for one log record under a tenant/cluster.</summary>
    public static Document ToDocument(Guid tenantId, Guid clusterId, LogIngestRecord r)
    {
        long ms = ToEpochMillis(r.Timestamp);
        var doc = new Document
        {
            new Int64Field(Ts, ms, Field.Store.YES),
            new NumericDocValuesField(Ts, ms),
            new StringField(TenantId, tenantId.ToString("N"), Field.Store.NO),
            new StringField(ClusterId, clusterId.ToString("N"), Field.Store.NO),
            new StringField(Namespace, r.Namespace, Field.Store.YES),
            new SortedDocValuesField(Namespace, new BytesRef(r.Namespace)),
            new StringField(Pod, r.Pod, Field.Store.YES),
            new SortedDocValuesField(Pod, new BytesRef(r.Pod)),
            new StringField(Container, r.Container, Field.Store.YES),
            new SortedDocValuesField(Container, new BytesRef(r.Container)),
            new Int32Field(Severity, r.Severity, Field.Store.YES),
            new NumericDocValuesField(Severity, r.Severity),
            new TextField(Body, r.Body, Field.Store.YES),
        };
        if (!string.IsNullOrEmpty(r.TraceId))
            doc.Add(new StringField(TraceId, r.TraceId, Field.Store.YES));
        if (!string.IsNullOrEmpty(r.AttributesJson))
        {
            doc.Add(new StoredField(Attributes, r.AttributesJson));
            AddAttributeTerms(doc, r.AttributesJson);
        }
        return doc;
    }

    // Flattens a top-level JSON object into attr_key (existence) + attr_kv (equality) index terms.
    // Non-object JSON or parse failures are ignored — attributes are best-effort filter accelerators.
    private static void AddAttributeTerms(Document doc, string attributesJson)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(attributesJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty p in parsed.RootElement.EnumerateObject())
            {
                doc.Add(new StringField(AttrKey, p.Name, Field.Store.NO));
                string? value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                    _ => null,
                };
                if (value is not null)
                    doc.Add(new StringField(AttrKv, p.Name + KvSep + value, Field.Store.NO));
            }
        }
        catch (JsonException) { /* malformed attributes never break ingest */ }
    }

    /// <summary>
    /// Translates the viewer's <see cref="LogQueryFilter"/> + time window into a Lucene query, scoped
    /// (defence in depth) by tenant AND cluster. Mirrors PgLogService.ApplyFilters one-for-one.
    /// </summary>
    public static Query BuildQuery(
        Guid tenantId, Guid clusterId, LogQueryFilter f, DateTime from, DateTime to, Analyzer analyzer)
    {
        var q = new BooleanQuery
        {
            { new TermQuery(new Term(TenantId, tenantId.ToString("N"))), Occur.MUST },
            { new TermQuery(new Term(ClusterId, clusterId.ToString("N"))), Occur.MUST },
            // ts >= from AND ts < to  (upper bound exclusive, matching the SQL "ts < @to")
            { NumericRangeQuery.NewInt64Range(Ts, ToEpochMillis(from), ToEpochMillis(to), true, false), Occur.MUST },
        };

        // namespace = ANY(@ns)  → at least one of the namespace terms
        if (f.Namespaces.Count > 0)
        {
            var ns = new BooleanQuery();
            foreach (string n in f.Namespaces)
                ns.Add(new TermQuery(new Term(Namespace, n)), Occur.SHOULD);
            ns.MinimumNumberShouldMatch = 1;
            q.Add(ns, Occur.MUST);
        }

        // pod: a plain name or a "(w1|w2)" alternation of workload prefixes (pods are "<workload>-<hash>").
        // The old store matched with an anchored-start POSIX regex; we preserve "starts-with" semantics
        // with prefix queries — no regex engine, and it rides the term index.
        if (!string.IsNullOrEmpty(f.Pod))
            q.Add(BuildPodQuery(f.Pod), Occur.MUST);

        if (!string.IsNullOrEmpty(f.Container))
            q.Add(new TermQuery(new Term(Container, f.Container)), Occur.MUST);

        // Body text: analyzed token match (all tokens MUST occur). This is token full-text — faster and
        // usually better than the old case-sensitive substring, at the cost of exact-substring fidelity.
        if (!string.IsNullOrEmpty(f.Text))
        {
            BooleanQuery? body = BuildBodyQuery(f.Text, analyzer);
            if (body is not null) q.Add(body, Occur.MUST);
        }

        if (f.MinLevel > LogLevel.None)
            q.Add(NumericRangeQuery.NewInt32Range(Severity, (int)f.MinLevel, int.MaxValue, true, true), Occur.MUST);

        // Structured attribute filter: key+value → equality term, key-only → existence term.
        if (!string.IsNullOrEmpty(f.AttrKey))
        {
            q.Add(!string.IsNullOrEmpty(f.AttrValue)
                ? new TermQuery(new Term(AttrKv, f.AttrKey + KvSep + f.AttrValue))
                : new TermQuery(new Term(AttrKey, f.AttrKey)), Occur.MUST);
        }

        return q;
    }

    /// <summary>Trace-correlation query: every log line carrying <paramref name="traceId"/> for the scope.</summary>
    public static Query BuildTraceQuery(Guid tenantId, Guid clusterId, string traceId) => new BooleanQuery
    {
        { new TermQuery(new Term(TenantId, tenantId.ToString("N"))), Occur.MUST },
        { new TermQuery(new Term(ClusterId, clusterId.ToString("N"))), Occur.MUST },
        { new TermQuery(new Term(TraceId, traceId)), Occur.MUST },
    };

    private static Query BuildPodQuery(string pattern)
    {
        // Accept "^(a|b)", "(a|b)", or a plain "name"; extract the alternatives and prefix-match each.
        string p = pattern.TrimStart('^');
        if (p.StartsWith('(') && p.EndsWith(')')) p = p[1..^1];
        string[] alts = p.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (alts.Length <= 1)
            return new PrefixQuery(new Term(Pod, alts.Length == 1 ? alts[0] : p));

        var pod = new BooleanQuery { MinimumNumberShouldMatch = 1 };
        foreach (string a in alts)
            pod.Add(new PrefixQuery(new Term(Pod, a)), Occur.SHOULD);
        return pod;
    }

    private static BooleanQuery? BuildBodyQuery(string text, Analyzer analyzer)
    {
        var tokens = new List<string>();
        using (TokenStream ts = analyzer.GetTokenStream(Body, text))
        {
            var termAttr = ts.AddAttribute<Lucene.Net.Analysis.TokenAttributes.ICharTermAttribute>();
            ts.Reset();
            while (ts.IncrementToken()) tokens.Add(termAttr.ToString());
            ts.End();
        }
        if (tokens.Count == 0) return null;

        var body = new BooleanQuery();
        foreach (string tok in tokens)
            body.Add(new TermQuery(new Term(Body, tok)), Occur.MUST);
        return body;
    }
}
