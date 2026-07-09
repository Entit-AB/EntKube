using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Lucene.Net.Documents;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The <c>traces</c> signal manager: the trace-summary index. At span ingest it turns each batch into
/// per-(trace, namespace) <see cref="TraceSummaryPartial"/> documents so the trace LIST can be answered by
/// merging a handful of tiny partials per trace instead of materializing every span (see
/// <see cref="TraceSummarySchema"/> and the merge in <see cref="SegmentTraceService"/>). All sealing,
/// querying, and retention are inherited from <see cref="SegmentManagerBase"/>.
/// </summary>
public sealed class TraceSummarySegmentManager(
    Guid tenantId,
    IDbContextFactory<ApplicationDbContext> catalog,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<TraceSummarySegmentManager> logger)
    : SegmentManagerBase(tenantId, catalog, blobs, options, logger, TraceSummarySchema.CreateAnalyzer())
{
    protected override string Signal => "traces";

    /// <summary>
    /// Aggregates a span batch into per-(trace, namespace) partial summaries and appends them. A trace's
    /// spans may span batches/namespaces — each partial covers only what's in THIS batch for that
    /// (trace, namespace); the query-time merge reassembles the whole trace.
    /// </summary>
    public void WriteFromSpanBatch(Guid tenantId, Guid clusterId, IReadOnlyList<SpanIngestRecord> records)
    {
        if (records.Count == 0) return;

        // Group the batch by (trace, namespace). A namespace-less span groups under "".
        var groups = new Dictionary<(string Trace, string Ns), List<SpanIngestRecord>>();
        foreach (SpanIngestRecord r in records)
        {
            var key = (r.TraceId, r.Namespace ?? "");
            if (!groups.TryGetValue(key, out List<SpanIngestRecord>? list))
                groups[key] = list = [];
            list.Add(r);
        }

        var docs = new List<(Document, long)>(groups.Count);
        foreach (((string trace, string ns), List<SpanIngestRecord> spans) in groups)
        {
            TraceSummaryPartial p = BuildPartial(trace, ns, spans);
            docs.Add((TraceSummarySchema.ToDocument(tenantId, clusterId, p), p.StartMs));
        }
        AddDocuments(docs);
    }

    private static TraceSummaryPartial BuildPartial(string traceId, string ns, List<SpanIngestRecord> spans)
    {
        long minStart = long.MaxValue;
        double maxEnd = double.MinValue;
        int errorCount = 0;
        var services = new HashSet<string>(StringComparer.Ordinal);

        SpanIngestRecord? earliest = null;   // earliest-start span (root fallback)
        SpanIngestRecord? root = null;       // earliest root span (ParentSpanId == null), if any
        long earliestStart = long.MaxValue, rootStart = long.MaxValue;

        foreach (SpanIngestRecord r in spans)
        {
            long start = TelemetryTime.ToEpochMillis(r.Start);
            if (start < minStart) minStart = start;
            double end = start + r.DurationMs;
            if (end > maxEnd) maxEnd = end;
            if (r.StatusCode == 2) errorCount++;
            if (!string.IsNullOrEmpty(r.Service)) services.Add(r.Service);

            if (start < earliestStart) { earliestStart = start; earliest = r; }
            if (r.ParentSpanId is null && start < rootStart) { rootStart = start; root = r; }
        }

        // Deterministic dedup key: identical (retried) batches hash identically → merge de-dupes them.
        string partialId = HashPartial(traceId, ns, spans, minStart, maxEnd, spans.Count, errorCount);

        return new TraceSummaryPartial(
            TraceId: traceId,
            Namespace: ns,
            StartMs: minStart,
            EndMs: maxEnd,
            SpanCount: spans.Count,
            ErrorCount: errorCount,
            Services: [.. services],
            EarliestService: earliest?.Service ?? "",
            EarliestName: earliest?.Name ?? "",
            RootService: root?.Service,
            RootName: root?.Name,
            RootTs: root is null ? null : rootStart,
            PartialId: partialId);
    }

    private static string HashPartial(
        string traceId, string ns, List<SpanIngestRecord> spans, long minStart, double maxEnd, int count, int errors)
    {
        var sb = new StringBuilder(traceId).Append('|').Append(ns).Append('|');
        foreach (string spanId in spans.Select(s => s.SpanId).OrderBy(s => s, StringComparer.Ordinal))
            sb.Append(spanId).Append(',');
        sb.Append('|').Append(minStart).Append('|').Append(maxEnd.ToString("R"))
          .Append('|').Append(count).Append('|').Append(errors);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
