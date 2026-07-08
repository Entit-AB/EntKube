using System.IO.Compression;
using EntKube.Web.Data;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Microsoft.EntityFrameworkCore;
using FSDirectory = Lucene.Net.Store.FSDirectory;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace EntKube.Web.Services.Telemetry;

/// <summary>Tunables for the telemetry segment engine, read from the <c>Telemetry</c> config section.</summary>
public sealed class SegmentEngineOptions
{
    /// <summary>Root directory for the engine's local state (active indexes, cache). Default /app/Data/telemetry.</summary>
    public string DataPath { get; init; } = "/app/Data/telemetry";

    /// <summary>Seal the active index into a segment once it reaches this many docs. Default 1,000,000.</summary>
    public long RollMaxDocs { get; init; } = 1_000_000;

    /// <summary>Seal the active index at least this often, even if under RollMaxDocs. Default 1 hour.</summary>
    public TimeSpan RollMaxAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Drop sealed segments whose newest event is older than this. Default 14 days.</summary>
    public int RetentionDays { get; init; } = 14;
}

/// <summary>
/// Signal-agnostic core of the telemetry segment engine, shared by the logs and spans managers. Owns the
/// single writable active index for its <see cref="Signal"/>, the sealed-segment cache, and access to the
/// segment catalog. Ingest appends prepared documents to the active index; queries fan out over the active
/// index plus the sealed segments whose time window overlaps the query, unioned into one Lucene view via a
/// <see cref="MultiReader"/>. Subclasses add only the typed write method (records → documents).
///
/// Single-instance by design (one app replica): one <see cref="IndexWriter"/> per signal, no cross-node
/// coordination. Ingest and the roll swap are serialized by a lock held only for the in-memory add /
/// pointer swap — the expensive seal work (zip, upload, catalog insert) runs outside the lock.
/// </summary>
public abstract class SegmentManagerBase : IDisposable
{
    /// <summary>The catalog signal name for this manager: "logs" or "spans".</summary>
    protected abstract string Signal { get; }

    private readonly IDbContextFactory<ApplicationDbContext> _catalog;
    private readonly ISegmentBlobStore _blobs;
    private readonly SegmentCache _cache;
    private readonly SegmentEngineOptions _options;
    private readonly ILogger _logger;
    private readonly Analyzer _analyzer;

    private readonly string _dirA;
    private readonly string _dirB;
    private readonly object _rollLock = new();

    private volatile ActiveSegmentIndex _active;
    private bool _activeIsA;
    private DateTime _activeSince = DateTime.UtcNow;

    /// <summary>The tenant this manager serves — telemetry is tenant-scoped, one manager per (tenant, signal).</summary>
    protected Guid TenantId { get; }

    protected SegmentManagerBase(
        Guid tenantId,
        IDbContextFactory<ApplicationDbContext> catalog,
        ISegmentBlobStore blobs,
        SegmentEngineOptions options,
        ILogger logger,
        Analyzer analyzer)
    {
        TenantId = tenantId;
        _catalog = catalog;
        _blobs = blobs;
        _options = options;
        _logger = logger;
        _analyzer = analyzer;
        // Per-tenant on-disk state — each tenant's active index and cache live under its own subtree.
        string tenantRoot = Path.Combine(options.DataPath, tenantId.ToString("N"));
        _dirA = Path.Combine(tenantRoot, "active", $"{Signal}.a");
        _dirB = Path.Combine(tenantRoot, "active", $"{Signal}.b");
        _cache = new SegmentCache(blobs, Path.Combine(tenantRoot, "cache", Signal));
        _active = ActiveSegmentIndex.OpenAt(_dirA, _analyzer);
        _activeIsA = true;
    }

    /// <summary>Analyzer for tokenizing query text — matches the indexing analyzer so tokens align.</summary>
    public Analyzer Analyzer => _analyzer;

    /// <summary>Documents currently in the (unsealed) active index — the seal service's roll trigger.</summary>
    public long ActiveDocCount => _active.DocCount;

    /// <summary>How long the current active index has been accumulating — the age-based roll trigger.</summary>
    public TimeSpan ActiveAge => DateTime.UtcNow - _activeSince;

    /// <summary>Appends prepared (document, epoch-ms) pairs to the active index. Serialized against a roll.</summary>
    protected void AddDocuments(IReadOnlyList<(Document Doc, long TsMs)> docs)
    {
        if (docs.Count == 0) return;
        lock (_rollLock)
            foreach ((Document doc, long ms) in docs)
                _active.Add(doc, ms);
    }

    /// <summary>
    /// Runs <paramref name="read"/> over one <see cref="IndexSearcher"/> spanning the active index and the
    /// sealed segments overlapping [<paramref name="from"/>, <paramref name="to"/>] (null bounds = all
    /// segments). Sealed readers are opened per query and closed on return.
    /// </summary>
    public async Task<T> QueryAsync<T>(DateTime? from, DateTime? to, Func<IndexSearcher, T> read, CancellationToken ct = default)
    {
        List<TelemetrySegment> segments = await SegmentsOverlappingAsync(from, to, ct);

        ActiveSegmentIndex active = _active;
        active.Refresh();
        IndexSearcher activeSearcher = active.Acquire();

        var openedDirs = new List<LuceneDirectory>();
        var readers = new List<IndexReader> { activeSearcher.IndexReader };
        try
        {
            foreach (TelemetrySegment seg in segments)
            {
                string dir = await _cache.EnsureLocalAsync(seg, ct);
                FSDirectory fsd = FSDirectory.Open(dir);
                openedDirs.Add(fsd);
                readers.Add(DirectoryReader.Open(fsd));
            }
            using var multi = new MultiReader(readers.ToArray(), closeSubReaders: false);
            var searcher = new IndexSearcher(multi);
            return read(searcher);
        }
        finally
        {
            active.Release(activeSearcher);          // index 0 — owned by the SearcherManager
            for (int i = 1; i < readers.Count; i++) readers[i].Dispose();
            foreach (LuceneDirectory d in openedDirs) d.Dispose();
        }
    }

    /// <summary>
    /// Seals the current active index into an immutable segment (uploaded to object storage + cataloged)
    /// and swaps in a fresh empty active index. No-op (returns null) when the active index is empty.
    /// </summary>
    public async Task<TelemetrySegment?> RollAndSealAsync(CancellationToken ct = default)
    {
        ActiveSegmentIndex sealing;
        lock (_rollLock)
        {
            if (!_active.HasData) return null;
            sealing = _active;
            // Ping-pong to the other on-disk directory so the new active never collides with the sealing one.
            _active = ActiveSegmentIndex.OpenAt(_activeIsA ? _dirB : _dirA, _analyzer);
            _activeIsA = !_activeIsA;
            _activeSince = DateTime.UtcNow;
        }

        var segId = Guid.NewGuid();
        DateTime min = sealing.MinTs!.Value;
        DateTime max = sealing.MaxTs!.Value;
        long docs = sealing.DocCount;
        string sealingDir = sealing.DirectoryPath!;

        sealing.Commit();
        sealing.Dispose(); // release file handles so the directory can be zipped/moved

        string key = $"{TenantId:N}/{Signal}/{min:yyyy/MM/dd}/{segId:N}.zip";
        string tmpZip = Path.Combine(_options.DataPath, "stage", segId.ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tmpZip)!);
        ZipFile.CreateFromDirectory(sealingDir, tmpZip, CompressionLevel.Optimal, includeBaseDirectory: false);
        long size = new FileInfo(tmpZip).Length;

        await _blobs.PutAsync(key, tmpZip, ct);
        File.Delete(tmpZip);

        var segment = new TelemetrySegment
        {
            Id = segId,
            TenantId = TenantId,
            Signal = Signal,
            MinTs = min,
            MaxTs = max,
            DocCount = docs,
            ObjectKey = key,
            SizeBytes = size,
            SealedAt = DateTime.UtcNow,
        };
        await using (ApplicationDbContext db = await _catalog.CreateDbContextAsync(ct))
        {
            db.TelemetrySegments.Add(segment);
            await db.SaveChangesAsync(ct);
        }

        // Keep the freshly-sealed files locally (as this segment's cache entry) — no download to query them.
        _cache.Adopt(segId, sealingDir);
        _logger.LogInformation(
            "Sealed {Signal} segment {SegId}: {Docs} docs, {Size} bytes, {Min:o}..{Max:o}", Signal, segId, docs, size, min, max);
        return segment;
    }

    /// <summary>Drops sealed segments whose newest event is older than the retention window (S3 + catalog + cache).</summary>
    public async Task<int> DropExpiredAsync(CancellationToken ct = default)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        await using ApplicationDbContext db = await _catalog.CreateDbContextAsync(ct);
        List<TelemetrySegment> expired = await db.TelemetrySegments
            .Where(s => s.TenantId == TenantId && s.Signal == Signal && s.MaxTs < cutoff)
            .ToListAsync(ct);
        if (expired.Count == 0) return 0;

        foreach (TelemetrySegment seg in expired)
        {
            try { await _blobs.DeleteAsync(seg.ObjectKey, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired segment object {Key}", seg.ObjectKey); }
            _cache.Remove(seg.Id);
        }
        db.TelemetrySegments.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Dropped {Count} expired {Signal} segment(s) older than {Cutoff:o}", expired.Count, Signal, cutoff);
        return expired.Count;
    }

    // Catalog lookup: sealed segments for this signal whose [MinTs,MaxTs] overlaps the requested window.
    // Null bounds widen to all segments (label/trace lookups). Mirrors "MaxTs >= from AND MinTs < to".
    private async Task<List<TelemetrySegment>> SegmentsOverlappingAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        await using ApplicationDbContext db = await _catalog.CreateDbContextAsync(ct);
        IQueryable<TelemetrySegment> q = db.TelemetrySegments.Where(s => s.TenantId == TenantId && s.Signal == Signal);
        if (from is DateTime f) q = q.Where(s => s.MaxTs >= f);
        if (to is DateTime t) q = q.Where(s => s.MinTs < t);
        return await q.OrderBy(s => s.MinTs).ToListAsync(ct);
    }

    public void Dispose()
    {
        _active.Dispose();
        _analyzer.Dispose();
        (_blobs as IDisposable)?.Dispose(); // per-tenant blob store owned by this manager
    }
}
