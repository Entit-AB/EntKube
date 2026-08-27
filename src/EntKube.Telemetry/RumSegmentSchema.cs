using EntKube.Web.Services;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The Lucene document schema for Real User Monitoring. RUM's three sub-signals (page views, JS errors,
/// resource timings) share one <c>rum</c> segment, discriminated by a <see cref="Kind"/> term — so the
/// engine keeps a single active index / seal loop for RUM rather than three. Scoped by tenant_id + site_id
/// (RUM is site-scoped, not cluster-scoped). The RUM dashboards' aggregates (Web-Vitals p75, top pages,
/// sessions) are computed in C# over the materialized <see cref="RumRow"/>s, replacing the SQL
/// percentile_cont / GROUP BY / session join.
/// </summary>
public static class RumSegmentSchema
{
    public const string PageView = "pv";
    public const string Error = "err";
    public const string Resource = "res";

    public const string Ts = "ts";
    public const string TenantId = "tenant_id";
    public const string SiteId = "site_id";
    public const string Kind = "kind";
    public const string SessionId = "session_id";
    public const string ViewId = "view_id";
    public const string Path = "path";
    // page-view fields
    public const string LoadMs = "load_ms";
    public const string TtfbMs = "ttfb_ms";
    public const string LcpMs = "lcp_ms";
    public const string Cls = "cls";
    public const string InpMs = "inp_ms";
    public const string FcpMs = "fcp_ms";
    public const string Browser = "browser";
    public const string Os = "os";
    public const string Device = "device";
    // error fields
    public const string Message = "message";
    public const string Stack = "stack";
    public const string Source = "source";
    // resource fields
    public const string Name = "name";
    public const string ResKind = "res_kind";
    public const string DurationMs = "duration_ms";
    public const string Status = "status";
    public const string TraceId = "trace_id";

    public static Analyzer CreateAnalyzer() => new KeywordAnalyzer();

    public static Document ToPageViewDoc(Guid tenantId, Guid siteId, RumPageViewRecord r)
    {
        Document d = Base(tenantId, siteId, PageView, r.Timestamp, r.SessionId, r.ViewId, r.Path);
        AddDouble(d, LoadMs, r.LoadMs);
        AddDouble(d, TtfbMs, r.TtfbMs);
        AddDouble(d, LcpMs, r.LcpMs);
        AddDouble(d, Cls, r.Cls);
        AddDouble(d, InpMs, r.InpMs);
        AddDouble(d, FcpMs, r.FcpMs);
        AddStored(d, Browser, r.Browser);
        AddStored(d, Os, r.Os);
        AddStored(d, Device, r.Device);
        return d;
    }

    public static Document ToErrorDoc(Guid tenantId, Guid siteId, RumErrorRecord r)
    {
        Document d = Base(tenantId, siteId, Error, r.Timestamp, r.SessionId, r.ViewId, r.Path ?? "");
        d.Add(new StoredField(Message, r.Message));
        AddStored(d, Stack, r.Stack);
        AddStored(d, Source, r.Source);
        return d;
    }

    public static Document ToResourceDoc(Guid tenantId, Guid siteId, RumResourceRecord r)
    {
        Document d = Base(tenantId, siteId, Resource, r.Timestamp, r.SessionId, r.ViewId, r.Path ?? "");
        d.Add(new StoredField(Name, r.Name));
        AddStored(d, ResKind, r.Kind);
        AddDouble(d, DurationMs, r.DurationMs);
        if (r.Status is int st) d.Add(new StoredField(Status, st));
        AddStored(d, TraceId, r.TraceId);
        return d;
    }

    private static Document Base(Guid tenantId, Guid siteId, string kind, DateTime ts, string sessionId, string viewId, string path)
    {
        long ms = TelemetryTime.ToEpochMillis(ts);
        return new Document
        {
            new Int64Field(Ts, ms, Field.Store.YES),
            new NumericDocValuesField(Ts, ms),
            new StringField(TenantId, tenantId.ToString("N"), Field.Store.NO),
            new StringField(SiteId, siteId.ToString("N"), Field.Store.NO),
            new StringField(Kind, kind, Field.Store.NO),
            new StringField(SessionId, sessionId, Field.Store.YES),
            new StoredField(ViewId, viewId),
            new StoredField(Path, path),
        };
    }

    private static void AddDouble(Document d, string field, double? value)
    {
        if (value is double v) d.Add(new DoubleField(field, v, Field.Store.YES));
    }

    private static void AddStored(Document d, string field, string? value)
    {
        if (!string.IsNullOrEmpty(value)) d.Add(new StoredField(field, value));
    }

    /// <summary>Scope query: tenant + site (mandatory), optional kind, optional time window.</summary>
    public static Query BuildScope(Guid tenantId, Guid siteId, string? kind, DateTime? from, DateTime? to)
    {
        var q = new BooleanQuery
        {
            { new TermQuery(new Term(TenantId, tenantId.ToString("N"))), Occur.MUST },
            { new TermQuery(new Term(SiteId, siteId.ToString("N"))), Occur.MUST },
        };
        if (kind is not null)
            q.Add(new TermQuery(new Term(Kind, kind)), Occur.MUST);
        if (from is DateTime || to is DateTime)
            q.Add(NumericRangeQuery.NewInt64Range(Ts,
                from is DateTime f ? TelemetryTime.ToEpochMillis(f) : long.MinValue,
                to is DateTime t ? TelemetryTime.ToEpochMillis(t) : long.MaxValue, true, to is null), Occur.MUST);
        return q;
    }

    /// <summary>Scope query for one session's events (all kinds) within a window.</summary>
    public static Query BuildSession(Guid tenantId, Guid siteId, string sessionId, DateTime from, DateTime to)
    {
        var q = (BooleanQuery)BuildScope(tenantId, siteId, null, from, to);
        q.Add(new TermQuery(new Term(SessionId, sessionId)), Occur.MUST);
        return q;
    }

    /// <summary>Materializes a RUM document's stored fields into a <see cref="RumRow"/>.</summary>
    public static RumRow ReadRow(Document d) => new(
        Kind: d.GetField(Ts) is null ? "" : InferKind(d),
        TsMs: d.GetField(Ts)?.GetInt64Value() ?? 0,
        SessionId: d.Get(SessionId) ?? "",
        ViewId: d.Get(ViewId) ?? "",
        Path: d.Get(Path) ?? "",
        LoadMs: Dbl(d, LoadMs), TtfbMs: Dbl(d, TtfbMs), LcpMs: Dbl(d, LcpMs),
        Cls: Dbl(d, Cls), InpMs: Dbl(d, InpMs), FcpMs: Dbl(d, FcpMs),
        Browser: d.Get(Browser), Os: d.Get(Os), Device: d.Get(Device),
        Message: d.Get(Message), Stack: d.Get(Stack), Source: d.Get(Source),
        Name: d.Get(Name), ResKind: d.Get(ResKind), DurationMs: Dbl(d, DurationMs),
        Status: d.GetField(Status)?.GetInt32Value(), TraceId: d.Get(TraceId));

    // Kind isn't stored (indexed only); infer from which kind-specific field is present.
    private static string InferKind(Document d) =>
        d.GetField(Message) is not null ? Error : d.GetField(Name) is not null ? Resource : PageView;

    private static double? Dbl(Document d, string field) => d.GetField(field)?.GetDoubleValue();
}

/// <summary>A materialized RUM event for the C# aggregations. Fields are populated per <see cref="Kind"/>.</summary>
public sealed record RumRow(
    string Kind, long TsMs, string SessionId, string ViewId, string Path,
    double? LoadMs, double? TtfbMs, double? LcpMs, double? Cls, double? InpMs, double? FcpMs,
    string? Browser, string? Os, string? Device,
    string? Message, string? Stack, string? Source,
    string? Name, string? ResKind, double? DurationMs, int? Status, string? TraceId);
