using System.Collections.Concurrent;
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

    /// <summary>Drop sealed segments whose newest event is older than this. Default 90 days.</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>
    /// Retention for the <c>spans</c> signal — the raw per-span waterfall data, which is by far the largest
    /// telemetry volume (eBPF instruments everything). Raw spans are dropped after this window while the
    /// per-trace SUMMARY index (which powers the trace list) follows the full <see cref="RetentionDays"/>,
    /// so you keep 90 days of "what traces happened" but only this window of deep waterfalls + RED +
    /// service-map. Cutting raw spans from 90→30 days roughly thirds the dominant span store. Clamped to at
    /// most <see cref="RetentionDays"/>. Default 30. Raise toward RetentionDays for more waterfall history,
    /// lower for less disk.</summary>
    public int RawSpanRetentionDays { get; init; } = 30;

    /// <summary>
    /// Head-sampling rate (percent, 1..100) for "uninteresting" traces' raw spans: a deterministic
    /// per-trace-id hash keeps this fraction. Traces with any error span or any span at/above
    /// <see cref="TraceKeepMinDurationMs"/> are ALWAYS kept regardless. The trace list is unaffected — every
    /// trace still gets a summary; only raw-span retention is thinned. 100 (default) = no sampling / current
    /// behaviour. Set e.g. 10 to keep all errors+slow traces plus 10% of the rest.</summary>
    public int TraceSampleRatePercent { get; init; } = 100;

    /// <summary>A trace with any span whose duration is at least this (ms) is always kept when sampling is on
    /// (see <see cref="TraceSampleRatePercent"/>) — slow traces are the ones worth a waterfall. Default 500.</summary>
    public double TraceKeepMinDurationMs { get; init; } = 500;

    /// <summary>
    /// Split logs into two retention tiers by severity: WARN and above ("logs" signal) keep the full
    /// <see cref="RetentionDays"/>; DEBUG/INFO ("logs_debug" signal) keep only
    /// <see cref="VerboseLogRetentionDays"/>. Most log VOLUME is low-severity noise, so aging it out early is
    /// a large disk win with nothing important lost — warnings and errors stay the full window. Writes are
    /// routed by severity and queries union both tiers (skipping the verbose tier when a query's min level is
    /// WARN+). Default true. Set false to keep the single-tier behaviour (all logs at RetentionDays).</summary>
    public bool TieredLogRetention { get; init; } = true;

    /// <summary>Retention (days) for the DEBUG/INFO log tier when <see cref="TieredLogRetention"/> is on.
    /// Clamped to at most <see cref="RetentionDays"/>. Default 14.</summary>
    public int VerboseLogRetentionDays { get; init; } = 14;

    /// <summary>Max number of sealed-segment readers kept open in memory per (tenant, signal). Least-
    /// recently-used readers beyond this are closed (they reopen on demand). Bounds file handles /
    /// heap so a long-running app with 90-day retention doesn't accumulate a reader per segment.
    /// Default 256.</summary>
    public int MaxCachedReaders { get; init; } = 256;

    /// <summary>
    /// zstd level used to compress a sealed segment's archive before it is uploaded to object storage
    /// (see <see cref="SegmentArchive"/>). This is the dominant lever on at-rest telemetry size: sealing the
    /// whole Lucene segment directory with zstd-19 instead of Deflate roughly halves the stored archive
    /// versus the old zip, and zstd decompresses faster on the cold-query path too. 19 is the ratio/speed
    /// sweet spot (22 is barely smaller but far slower); seal runs on a background timer so the CPU cost is
    /// off the ingest/query path. Reads auto-detect the archive format, so already-sealed <c>.zip</c>
    /// segments keep working — this is a forward-only change with no migration. Range 1..22, default 19.</summary>
    public int ArchiveZstdLevel { get; init; } = 19;
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

    // Sealed segments are immutable, so their opened Lucene readers are reusable across queries.
    // Cache them (keyed by segment id) so a query doesn't re-open a DirectoryReader — and, on a cold
    // local cache, re-download from S3 — for every overlapping segment on every call. This is the
    // dominant repeat/wide-query cost. Bounded by the number of live segments (retention). Lucene's
    // own reference count keeps a reader (and its Directory, via the close listener) alive through any
    // in-flight query that races the segment's retention eviction.
    private readonly ConcurrentDictionary<Guid, Lazy<Task<DirectoryReader>>> _readerCache = new();
    private readonly ConcurrentDictionary<Guid, long> _readerLastUsed = new();  // seg id → monotonic tick (LRU order)
    private long _accessClock;

    /// <summary>The tenant this manager serves — telemetry is tenant-scoped, one manager per (tenant, signal).</summary>
    protected Guid TenantId { get; }

    /// <summary>Engine tunables — exposed so a signal-specific subclass can vary behaviour (e.g. retention).</summary>
    protected SegmentEngineOptions Options => _options;

    /// <summary>Retention window (days) for this signal's sealed segments. Base default is the global
    /// <see cref="SegmentEngineOptions.RetentionDays"/>; <see cref="SpanSegmentManager"/> shortens it so raw
    /// spans age out before the long-lived trace summaries.</summary>
    protected virtual int RetentionDays => _options.RetentionDays;

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

    /// <summary>Earliest event timestamp in the (unsealed) active index, or null when empty.</summary>
    public DateTime? ActiveMinTs => _active.MinTs;

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

        // Cached, immutable sealed-segment readers — reused across queries (opened once). Each is
        // IncRef'd for the life of this query so retention eviction can't close it mid-search.
        var sealedReaders = new List<DirectoryReader>(segments.Count);
        try
        {
            foreach (TelemetrySegment seg in segments)
                sealedReaders.Add(await AcquireSegmentReaderAsync(seg, ct));

            var readers = new IndexReader[1 + sealedReaders.Count];
            readers[0] = activeSearcher.IndexReader;
            for (int i = 0; i < sealedReaders.Count; i++) readers[i + 1] = sealedReaders[i];

            using var multi = new MultiReader(readers, closeSubReaders: false);
            var searcher = new IndexSearcher(multi);
            // Offload the CPU-bound Lucene search + doc materialization off the caller's thread. On a
            // Blazor Server circuit this yields the synchronization context so the UI stays responsive
            // (renders spinners, handles clicks) instead of freezing the whole app while a wide scan
            // runs. Readers stay valid — we await before the finally releases them.
            return await Task.Run(() => read(searcher), ct);
        }
        finally
        {
            active.Release(activeSearcher);                 // index 0 — owned by the SearcherManager
            foreach (DirectoryReader r in sealedReaders) r.DecRef();  // release this query's hold; cached readers stay open
        }
    }

    // Get-or-open the cached reader for a sealed segment, then IncRef it for the caller's query.
    private async Task<DirectoryReader> AcquireSegmentReaderAsync(TelemetrySegment seg, CancellationToken ct)
    {
        // Lazy (not a bare Task) so the reader is opened EXACTLY once even if GetOrAdd's factory races —
        // a duplicated open would leak an undisposed reader + Directory.
        Lazy<Task<DirectoryReader>> lazy = _readerCache.GetOrAdd(
            seg.Id, _ => new Lazy<Task<DirectoryReader>>(() => OpenSegmentReaderAsync(seg, ct)));
        // Mark used up-front so a concurrent trim can't pick this (freshly-requested) entry as the LRU
        // victim before we've IncRef'd it.
        _readerLastUsed[seg.Id] = Interlocked.Increment(ref _accessClock);

        DirectoryReader reader;
        try
        {
            reader = await lazy.Value;
        }
        catch
        {
            // Don't cache a failed open (e.g. a transient S3 download error) — let the next query retry.
            _readerCache.TryRemove(new KeyValuePair<Guid, Lazy<Task<DirectoryReader>>>(seg.Id, lazy));
            _readerLastUsed.TryRemove(seg.Id, out _);
            throw;
        }

        // TryIncRef (not IncRef) closes the race with LRU/retention eviction: if the reader was closed
        // between GetOrAdd and here, drop the stale entry and reopen.
        if (!reader.TryIncRef())
        {
            _readerCache.TryRemove(new KeyValuePair<Guid, Lazy<Task<DirectoryReader>>>(seg.Id, lazy));
            return await AcquireSegmentReaderAsync(seg, ct);
        }

        TrimReaderCache();   // safe now: this reader is IncRef'd and has the newest access tick
        return reader;
    }

    private async Task<DirectoryReader> OpenSegmentReaderAsync(TelemetrySegment seg, CancellationToken ct)
    {
        string dir = await _cache.EnsureLocalAsync(seg, ct);
        FSDirectory fsd = FSDirectory.Open(dir);
        DirectoryReader reader = DirectoryReader.Open(fsd);
        // Dispose the Directory exactly when the reader is truly closed (refcount → 0), never while an
        // in-flight query still holds it. Reader starts at refcount 1 — the cache's own reference.
        reader.AddReaderClosedListener(new DirectoryCloser(fsd));
        return reader;
    }

    // Close least-recently-used cached readers beyond the cap. DecRef only releases the cache's own
    // reference — a reader in use by an in-flight query stays open until that query DecRef's it, and
    // the DirectoryCloser then disposes its Directory. Reopened on demand.
    private void TrimReaderCache()
    {
        int over = _readerCache.Count - _options.MaxCachedReaders;
        if (over <= 0) return;

        foreach (Guid id in _readerCache.Keys
                     .OrderBy(k => _readerLastUsed.GetValueOrDefault(k, 0))
                     .Take(over)
                     .ToList())
        {
            if (_readerCache.TryRemove(id, out Lazy<Task<DirectoryReader>>? lz))
            {
                _readerLastUsed.TryRemove(id, out _);
                if (lz.IsValueCreated && lz.Value.IsCompletedSuccessfully)
                {
                    try { lz.Value.Result.DecRef(); } catch { /* already closed */ }
                }
            }
        }
    }

    private sealed class DirectoryCloser(FSDirectory dir) : IndexReader.IReaderClosedListener
    {
        public void OnClose(IndexReader reader) => dir.Dispose();
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

        string key = $"{TenantId:N}/{Signal}/{min:yyyy/MM/dd}/{segId:N}{SegmentArchive.Extension}";
        string tmpArchive = Path.Combine(_options.DataPath, "stage", segId.ToString("N") + SegmentArchive.Extension);
        Directory.CreateDirectory(Path.GetDirectoryName(tmpArchive)!);
        await SegmentArchive.PackAsync(sealingDir, tmpArchive, _options.ArchiveZstdLevel, ct);
        long size = new FileInfo(tmpArchive).Length;

        await _blobs.PutAsync(key, tmpArchive, ct);
        File.Delete(tmpArchive);

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
        DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        await using ApplicationDbContext db = await _catalog.CreateDbContextAsync(ct);
        List<TelemetrySegment> expired = await db.TelemetrySegments
            .Where(s => s.TenantId == TenantId && s.Signal == Signal && s.MaxTs < cutoff)
            .ToListAsync(ct);
        if (expired.Count == 0) return 0;

        // Remove from the catalog FIRST so no new query resolves these segments, then free storage.
        db.TelemetrySegments.RemoveRange(expired);
        await db.SaveChangesAsync(ct);

        foreach (TelemetrySegment seg in expired)
        {
            // Release the cache's reference — the reader (and its Directory) closes once any in-flight
            // query that IncRef'd it finishes; the DirectoryCloser then disposes the FSDirectory.
            _readerLastUsed.TryRemove(seg.Id, out _);
            if (_readerCache.TryRemove(seg.Id, out Lazy<Task<DirectoryReader>>? lz) && lz.IsValueCreated)
            {
                try { (await lz.Value).DecRef(); }
                catch { /* open never succeeded — nothing to release */ }
            }
            try { await _blobs.DeleteAsync(seg.ObjectKey, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired segment object {Key}", seg.ObjectKey); }
            _cache.Remove(seg.Id);
        }
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
        // Release the cache's reference on every open sealed reader (closes it → DirectoryCloser
        // disposes the FSDirectory). Only completed opens hold a reference.
        foreach (Lazy<Task<DirectoryReader>> lz in _readerCache.Values)
        {
            if (lz.IsValueCreated && lz.Value.IsCompletedSuccessfully)
            {
                try { lz.Value.Result.DecRef(); } catch { /* already closed */ }
            }
        }
        _readerCache.Clear();
        _analyzer.Dispose();
        (_blobs as IDisposable)?.Dispose(); // per-tenant blob store owned by this manager
    }
}
