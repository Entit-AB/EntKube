using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The logs manager: a <see cref="SegmentManagerBase"/> for the <c>logs</c> signal — the WARN-and-above
/// (important) tier when tiered log retention is on, or all logs when it is off. Adds only the typed ingest
/// method; all sealing/query/retention behaviour is inherited. Turns each <see cref="LogIngestRecord"/> into
/// a Lucene document via <see cref="LogSegmentSchema"/> and appends it to the active index.
/// Not sealed: <see cref="VerboseLogSegmentManager"/> subclasses it to be the short-retention DEBUG/INFO tier.
/// </summary>
public class LogSegmentManager(
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

/// <summary>
/// The DEBUG/INFO log tier — a <see cref="LogSegmentManager"/> that seals under the distinct <c>logs_debug</c>
/// signal so its segments are cataloged and expired independently of the important (<c>logs</c>) tier, on the
/// shorter <see cref="SegmentEngineOptions.VerboseLogRetentionDays"/> window. Same document schema and write
/// path; only the signal (catalog partition + object-key prefix) and retention differ.
/// </summary>
public sealed class VerboseLogSegmentManager(
    Guid tenantId,
    IDbContextFactory<ApplicationDbContext> catalog,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<LogSegmentManager> logger)
    : LogSegmentManager(tenantId, catalog, blobs, options, logger)
{
    protected override string Signal => "logs_debug";

    protected override int RetentionDays => Math.Min(Options.VerboseLogRetentionDays, Options.RetentionDays);
}
