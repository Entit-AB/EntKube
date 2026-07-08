using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;
using LuceneDoc = Lucene.Net.Documents.Document;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The RUM manager: a <see cref="SegmentManagerBase"/> for the unified <c>rum</c> signal. Page views,
/// errors, and resource timings are all appended to one active index (kind-discriminated by
/// <see cref="RumSegmentSchema"/>); sealing/query/retention are inherited.
/// </summary>
public sealed class RumSegmentManager(
    Guid tenantId,
    IDbContextFactory<ApplicationDbContext> catalog,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<RumSegmentManager> logger)
    : SegmentManagerBase(tenantId, catalog, blobs, options, logger, RumSegmentSchema.CreateAnalyzer())
{
    protected override string Signal => "rum";

    public void WritePageViews(Guid tenantId, Guid siteId, IReadOnlyList<RumPageViewRecord> records)
        => Write(records, r => (RumSegmentSchema.ToPageViewDoc(tenantId, siteId, r), TelemetryTime.ToEpochMillis(r.Timestamp)));

    public void WriteErrors(Guid tenantId, Guid siteId, IReadOnlyList<RumErrorRecord> records)
        => Write(records, r => (RumSegmentSchema.ToErrorDoc(tenantId, siteId, r), TelemetryTime.ToEpochMillis(r.Timestamp)));

    public void WriteResources(Guid tenantId, Guid siteId, IReadOnlyList<RumResourceRecord> records)
        => Write(records, r => (RumSegmentSchema.ToResourceDoc(tenantId, siteId, r), TelemetryTime.ToEpochMillis(r.Timestamp)));

    private void Write<T>(IReadOnlyList<T> records, Func<T, (LuceneDoc, long)> map)
    {
        if (records.Count == 0) return;
        var docs = new List<(LuceneDoc, long)>(records.Count);
        foreach (T r in records) docs.Add(map(r));
        AddDocuments(docs);
    }
}
