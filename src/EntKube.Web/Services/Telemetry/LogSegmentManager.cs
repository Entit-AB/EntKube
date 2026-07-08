using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The logs manager: a <see cref="SegmentManagerBase"/> for the <c>logs</c> signal. Adds only the typed
/// ingest method; all sealing/query/retention behaviour is inherited. Turns each <see cref="LogIngestRecord"/>
/// into a Lucene document via <see cref="LogSegmentSchema"/> and appends it to the active index.
/// </summary>
public sealed class LogSegmentManager(
    Guid tenantId,
    IDbContextFactory<ApplicationDbContext> catalog,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<LogSegmentManager> logger)
    : SegmentManagerBase(tenantId, catalog, blobs, options, logger, LogSegmentSchema.CreateAnalyzer())
{
    protected override string Signal => "logs";

    /// <summary>Appends a batch of log records for a tenant/cluster to the active index.</summary>
    public void WriteLogs(Guid tenantId, Guid clusterId, IReadOnlyList<LogIngestRecord> records)
    {
        if (records.Count == 0) return;
        var docs = new List<(Lucene.Net.Documents.Document, long)>(records.Count);
        foreach (LogIngestRecord r in records)
            docs.Add((LogSegmentSchema.ToDocument(tenantId, clusterId, r), LogSegmentSchema.ToEpochMillis(r.Timestamp)));
        AddDocuments(docs);
    }
}
