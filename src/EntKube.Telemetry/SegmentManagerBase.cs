using System.Collections.Concurrent;
using EntKube.Web.Data;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using FSDirectory = Lucene.Net.Store.FSDirectory;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace EntKube.Telemetry;

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

    /// <summary>
    /// How long a sealed segment stays on local disk after sealing — the WARM tier. Within this window a
    /// query reads the segment straight off the PersistentVolume; past it the local copy is evicted and the
    /// segment is served COLD, downloaded from object storage on demand and re-cached.
    ///
    /// Nothing is lost by evicting: the archive is durable in object storage for the full
    /// <see cref="RetentionDays"/> and the catalog row is untouched, so the segment stays queryable — just
    /// slower. Before this existed the local copy was only ever removed when retention deleted the segment
    /// outright, so "cache" silently grew to the entire retention window and could fill the volume.
    ///
    /// Default 3 days, which covers the overwhelming majority of queries. Clamped to at most
    /// <see cref="RetentionDays"/>. Set 0 to keep nothing locally (always cold), or a value at
    /// RetentionDays to restore the old keep-everything behaviour.</summary>
    public int WarmRetentionDays { get; init; } = 3;

    /// <summary>
    /// Hard ceiling on the bytes of unpacked segments held on local disk per (tenant, signal), enforced by
    /// evicting least-recently-used segments first. This is the backstop that keeps the volume from filling
    /// when a burst seals far more data than <see cref="WarmRetentionDays"/> anticipated — age decides what
    /// is worth keeping, size decides how much fits. Default 8 GiB. Set 0 for no size cap (age only).</summary>
    public long WarmMaxBytes { get; init; } = 8L * 1024 * 1024 * 1024;

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

    private readonly ISegmentCatalog _catalog;
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
    private int _warmTierRehydrated;   // 0/1 — see EnsureWarmTierRehydratedAsync

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
        ISegmentCatalog catalog,
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
        (_active, _activeIsA) = AdoptExistingActive();

        // Age the segment by the data in it, not by when this process happened to start. The roll trigger
        // asks "how long has unsealed data been sitting here", and answering it from process start means a
        // pod that restarts more often than the roll interval can never satisfy it — the clock returns to
        // zero every time, and the segment is never sealed by age again.
        _activeSince = _active.MinTs ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Opens whichever of the two active directories a previous process left its unsealed index in.
    ///
    /// <para>A roll ping-pongs between A and B and MOVES the sealed directory into the segment cache, so
    /// normally at most one of them holds an unsealed index — but which one depends on how many rolls
    /// happened before the process last stopped. Opening A unconditionally therefore orphaned anything
    /// left in B: still on disk, still charged against the volume, invisible to every query, and never
    /// sealed.</para>
    ///
    /// <para>Nothing is deleted when both hold data — the case where a crash landed between the roll's
    /// directory swap and the move of the sealed files. The newer one becomes active; the older is left
    /// exactly where it is, and the next roll re-opens that directory in <c>CREATE_OR_APPEND</c> mode and
    /// absorbs its documents into the following segment.</para>
    /// </summary>
    private (ActiveSegmentIndex Active, bool IsA) AdoptExistingActive()
    {
        ActiveSegmentIndex a = ActiveSegmentIndex.OpenAt(_dirA, _analyzer);
        ActiveSegmentIndex b = ActiveSegmentIndex.OpenAt(_dirB, _analyzer);

        bool useA = (a.HasData, b.HasData) switch
        {
            (true, true) => a.MaxTs >= b.MaxTs,
            (false, true) => false,
            _ => true,   // A has data, or neither does and A is the conventional start
        };

        if (a.HasData && b.HasData)
        {
            _logger.LogWarning(
                "Both {Signal} active directories hold unsealed data ({DocsA} in {DirA}, {DocsB} in {DirB}) — "
                + "a previous process stopped between rolling and sealing. Adopting the newer as active; the "
                + "other is retained and will be absorbed into the next segment.",
                Signal, a.DocCount, _dirA, b.DocCount, _dirB);
        }

        ActiveSegmentIndex chosen = useA ? a : b;
        (useA ? b : a).Dispose();   // closes the handle only — the files stay on disk

        if (chosen.HasData)
        {
            _logger.LogInformation(
                "Recovered {Signal} active index from disk: {Docs} unsealed documents from {Min:o}, awaiting seal.",
                Signal, chosen.DocCount, chosen.MinTs);
        }

        return (chosen, useA);
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
    public Task<T> QueryAsync<T>(DateTime? from, DateTime? to, Func<IndexSearcher, T> read, CancellationToken ct = default)
        => QueryAsync(SegmentScope.All, from, to, read, ct);

    /// <summary>
    /// As <see cref="QueryAsync{T}(DateTime?,DateTime?,Func{IndexSearcher,T},CancellationToken)"/>, but
    /// restricted to one tier of the index.
    ///
    /// The scope exists so the read path can be split across two pods. The hot active index lives only on
    /// the indexer that is writing it, while sealed segments are in object storage and readable by anyone;
    /// so a querier searches <see cref="SegmentScope.Sealed"/> locally and asks the indexer for
    /// <see cref="SegmentScope.Hot"/>, and the two result sets are merged. Everything above this method —
    /// log search, histograms, label discovery, traces, RUM — inherits the split without knowing about it.
    /// </summary>
    public async Task<T> QueryAsync<T>(
        SegmentScope scope, DateTime? from, DateTime? to, Func<IndexSearcher, T> read, CancellationToken ct = default)
    {
        IReadOnlyList<TelemetrySegment> segments = scope == SegmentScope.Hot
            ? []
            : await SegmentsOverlappingAsync(from, to, ct);

        ActiveSegmentIndex active = _active;
        IndexSearcher? activeSearcher = null;
        if (scope != SegmentScope.Sealed)
        {
            active.Refresh();
            activeSearcher = active.Acquire();
        }

        // Cached, immutable sealed-segment readers — reused across queries (opened once). Each is
        // IncRef'd for the life of this query so retention eviction can't close it mid-search.
        var sealedReaders = new List<DirectoryReader>(segments.Count);
        try
        {
            foreach (TelemetrySegment seg in segments)
                sealedReaders.Add(await AcquireSegmentReaderAsync(seg, ct));

            var readers = new List<IndexReader>(1 + sealedReaders.Count);
            if (activeSearcher is not null) readers.Add(activeSearcher.IndexReader);
            readers.AddRange(sealedReaders);

            using var multi = new MultiReader([.. readers], closeSubReaders: false);
            var searcher = new IndexSearcher(multi);
            // Offload the CPU-bound Lucene search + doc materialization off the caller's thread. On a
            // Blazor Server circuit this yields the synchronization context so the UI stays responsive
            // (renders spinners, handles clicks) instead of freezing the whole app while a wide scan
            // runs. Readers stay valid — we await before the finally releases them.
            return await Task.Run(() => read(searcher), ct);
        }
        finally
        {
            if (activeSearcher is not null) active.Release(activeSearcher);   // owned by the SearcherManager
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

    /// <summary>Deletes a segment's warm-tier files at the only moment it is safe to: when its reader has
    /// truly closed, meaning no query is still reading them.</summary>
    private sealed class CacheEvictor(SegmentCache cache, Guid segId) : IndexReader.IReaderClosedListener
    {
        public void OnClose(IndexReader reader) => cache.Remove(segId);
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
        await _catalog.AddAsync(segment, ct);

        // Keep the freshly-sealed files locally (as this segment's cache entry) — no download to query them.
        _cache.Adopt(segId, sealingDir, max);
        _logger.LogInformation(
            "Sealed {Signal} segment {SegId}: {Docs} docs, {Size} bytes, {Min:o}..{Max:o}", Signal, segId, docs, size, min, max);
        return segment;
    }

    /// <summary>Drops sealed segments whose newest event is older than the retention window (S3 + catalog + cache).</summary>
    public async Task<int> DropExpiredAsync(CancellationToken ct = default)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        // The catalog removes its rows FIRST, so no new query can resolve these segments by the time we
        // start freeing their storage below.
        IReadOnlyList<TelemetrySegment> expired = await _catalog.RemoveExpiredAsync(TenantId, Signal, cutoff, ct);
        if (expired.Count == 0) return 0;

        foreach (TelemetrySegment seg in expired)
        {
            await ReleaseLocallyAsync(seg.Id);
            try { await _blobs.DeleteAsync(seg.ObjectKey, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired segment object {Key}", seg.ObjectKey); }
        }
        _logger.LogInformation("Dropped {Count} expired {Signal} segment(s) older than {Cutoff:o}", expired.Count, Signal, cutoff);
        return expired.Count;
    }

    /// <summary>
    /// Evicts local copies of segments that have aged out of the warm window, or that push the tier past
    /// its size ceiling. The segments stay in object storage and stay cataloged, so nothing becomes
    /// unqueryable — a later query for one simply pays a download again. Returns how many were evicted.
    /// </summary>
    public async Task<int> TrimWarmTierAsync(CancellationToken ct = default)
    {
        await EnsureWarmTierRehydratedAsync(ct);

        IReadOnlyList<Guid> victims = _cache.SelectEvictions(_options, DateTime.UtcNow);
        if (victims.Count == 0) return 0;

        foreach (Guid id in victims)
        {
            ct.ThrowIfCancellationRequested();
            await ReleaseLocallyAsync(id);
        }
        _logger.LogInformation(
            "Warm tier for {Signal}: evicted {Count} segment(s) to local storage; {Bytes} bytes over {Resident} segment(s) remain.",
            Signal, victims.Count, _cache.LocalBytes, _cache.LocalCount);
        return victims.Count;
    }

    /// <summary>
    /// Teaches the warm tier, once per process, what the volume already holds. A restart otherwise starts
    /// believing the tier is empty, so neither bound applies to anything left behind — and a segment that
    /// has already aged out is exactly the one no query will touch again, so it would never be measured
    /// and never be reclaimed. Segments on disk with no catalog row are orphans (dropped while we were
    /// down) and the age rule collects them on this same pass.
    /// </summary>
    private async Task EnsureWarmTierRehydratedAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _warmTierRehydrated, 1) == 1) return;
        try
        {
            IReadOnlyList<TelemetrySegment> all = await _catalog.ListOverlappingAsync(TenantId, Signal, null, null, ct);
            _cache.Rehydrate(all.ToDictionary(seg => seg.Id, seg => seg.MaxTs));
        }
        catch (Exception ex)
        {
            // Leave it unrehydrated rather than half-rehydrated: a partial view would under-count the tier
            // and could evict the wrong segments. The next pass retries.
            Interlocked.Exchange(ref _warmTierRehydrated, 0);
            _logger.LogWarning(ex, "Could not rehydrate the {Signal} warm tier; retrying next cycle.", Signal);
        }
    }

    /// <summary>
    /// Drops a segment's local footprint: first the cached reader, then the files.
    ///
    /// The order matters and the delete is deliberately deferred. DecRef only releases the CACHE's
    /// reference — a query already searching this segment holds its own — so the reader closes when the
    /// last in-flight query finishes, and only then does the closed-listener delete the directory. Deleting
    /// the files while a reader still had them open would fault that query (and on Windows, fail outright).
    /// </summary>
    private async Task ReleaseLocallyAsync(Guid segId)
    {
        _readerLastUsed.TryRemove(segId, out _);

        if (_readerCache.TryRemove(segId, out Lazy<Task<DirectoryReader>>? lz) && lz.IsValueCreated)
        {
            try
            {
                DirectoryReader reader = await lz.Value;
                // Fires once the refcount reaches zero — after every in-flight query has let go.
                reader.AddReaderClosedListener(new CacheEvictor(_cache, segId));
                reader.DecRef();
                return;
            }
            catch { /* the open never succeeded — nothing holds the files, fall through and delete now */ }
        }

        _cache.Remove(segId);
    }

    // Catalog lookup: sealed segments for this signal whose [MinTs,MaxTs] overlaps the requested window.
    // Null bounds widen to all segments (label/trace lookups). Mirrors "MaxTs >= from AND MinTs < to".
    private Task<IReadOnlyList<TelemetrySegment>> SegmentsOverlappingAsync(DateTime? from, DateTime? to, CancellationToken ct)
        => _catalog.ListOverlappingAsync(TenantId, Signal, from, to, ct);

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
