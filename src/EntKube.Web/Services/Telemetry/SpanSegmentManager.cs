using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The spans manager: a <see cref="SegmentManagerBase"/> for the <c>spans</c> signal. Adds only the typed
/// ingest method; all sealing/query/retention behaviour is inherited. Turns each <see cref="SpanIngestRecord"/>
/// into a Lucene document via <see cref="SpanSegmentSchema"/> and appends it to the active index.
/// </summary>
public sealed class SpanSegmentManager(
    Guid tenantId,
    IDbContextFactory<ApplicationDbContext> catalog,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<SpanSegmentManager> logger)
    : SegmentManagerBase(tenantId, catalog, blobs, options, logger, SpanSegmentSchema.CreateAnalyzer())
{
    protected override string Signal => "spans";

    // Raw spans age out on the shorter raw-span window (never longer than the global retention); the
    // per-trace summary index keeps the trace list alive for the full retention independently.
    protected override int RetentionDays => Math.Min(Options.RawSpanRetentionDays, Options.RetentionDays);

    /// <summary>Appends a batch of span records for a tenant/cluster to the active index.</summary>
    public void WriteSpans(Guid tenantId, Guid clusterId, IReadOnlyList<SpanIngestRecord> records)
    {
        if (records.Count == 0) return;
        var docs = new List<(Lucene.Net.Documents.Document, long)>(records.Count);
        foreach (SpanIngestRecord r in records)
            docs.Add((SpanSegmentSchema.ToDocument(tenantId, clusterId, r), TelemetryTime.ToEpochMillis(r.Start)));
        AddDocuments(docs);
    }
}
