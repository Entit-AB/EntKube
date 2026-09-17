using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using FSDirectory = Lucene.Net.Store.FSDirectory;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace EntKube.Telemetry;

/// <summary>
/// The single writable Lucene index for one telemetry signal (logs or spans) — the "active segment".
/// Batches are appended via binary-fast <see cref="IndexWriter"/> adds; a <see cref="SearcherManager"/>
/// exposes near-real-time searchers so queries see freshly-ingested events without a full reader reopen.
/// It is signal-agnostic: the caller (a <see cref="SegmentManagerBase"/> subclass) turns records into
/// Lucene <see cref="Document"/>s and passes each with its event timestamp, and this class tracks the
/// doc count and min/max time that become the sealed segment's catalog bounds.
///
/// Backed by an on-disk <c>FSDirectory</c> in production (so its files can be zipped into a segment
/// archive) or a <c>RAMDirectory</c> in tests. The <see cref="Analyzer"/> is owned by the manager and
/// shared across rolled indexes, so this class never disposes it. <see cref="IndexWriter"/> and
/// <see cref="SearcherManager"/> are internally thread-safe.
/// </summary>
public sealed class ActiveSegmentIndex : IDisposable
{
    private readonly LuceneDirectory _dir;
    private readonly IndexWriter _writer;
    private readonly SearcherManager _searcherManager;
    private readonly bool _ownsDirectory;
    private readonly ILogger? _logger;
    private long _count;
    private long _minTsMs = long.MaxValue;
    private long _maxTsMs = long.MinValue;

    /// <summary>Opens (or creates) an active index at on-disk <paramref name="path"/> using the shared analyzer.</summary>
    public static ActiveSegmentIndex OpenAt(string path, Analyzer analyzer, ILogger? logger = null)
    {
        System.IO.Directory.CreateDirectory(path);
        return new ActiveSegmentIndex(
            FSDirectory.Open(path), analyzer, ownsDirectory: true, directoryPath: path, logger: logger);
    }

    /// <summary>On-disk path of this index (set when opened via <see cref="OpenAt"/>); null for in-memory tests.</summary>
    public string? DirectoryPath { get; }

    public ActiveSegmentIndex(
        LuceneDirectory dir, Analyzer analyzer, bool ownsDirectory = true, string? directoryPath = null,
        ILogger? logger = null)
    {
        _dir = dir;
        _ownsDirectory = ownsDirectory;
        DirectoryPath = directoryPath;
        _logger = logger;
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND,
        };
        _writer = new IndexWriter(_dir, config);
        _writer.Commit(); // materialize an empty commit so a searcher can open before the first write
        _searcherManager = new SearcherManager(_writer, applyAllDeletes: true, new SearcherFactory());
        RecoverFromDisk();
    }

    /// <summary>
    /// Timestamp field, in epoch milliseconds. Every signal's schema names it the same and indexes it as an
    /// <c>Int64Field</c> (range-searchable + stored) and a <c>NumericDocValuesField</c> (columnar) — the
    /// second of which is what <see cref="BoundaryTs"/> reads. <c>SegmentSchemaInvariantTests</c> pins that
    /// for every schema, because a signal missing the DocValue used to take the whole node down.
    /// </summary>
    private const string TsField = "ts";

    /// <summary>
    /// Restores the doc count and time bounds from the index already on disk.
    ///
    /// <para>These three values are the seal triggers, and they used to live only in memory: a counter
    /// incremented per <see cref="Add"/> and two interlocked timestamps. A restart therefore reopened an
    /// index holding millions of documents and reported <see cref="DocCount"/> as zero — which made
    /// <see cref="HasData"/> false, which made <c>RollAndSealAsync</c> a no-op that returned before it
    /// looked at anything. The active index could then never be sealed, by any trigger, for the rest of
    /// its life: it simply grew, unsealed and unarchived, and every subsequent restart reset the clock
    /// again. An indexer that restarts more often than its roll interval — for any reason at all, a node
    /// drain or an upgrade included — accumulates an index it can never let go of.</para>
    ///
    /// <para>Cheap enough to do unconditionally: an empty index short-circuits on the doc count, and a
    /// populated one pays one columnar pass over <c>ts</c> once per open.</para>
    /// </summary>
    private void RecoverFromDisk()
    {
        int onDisk = _writer.NumDocs;
        if (onDisk == 0) return;

        Interlocked.Exchange(ref _count, onDisk);

        IndexSearcher searcher = _searcherManager.Acquire();
        try
        {
            (long? min, long? max) = BoundaryTs(searcher);
            if (min is long lo) Interlocked.Exchange(ref _minTsMs, lo);
            if (max is long hi) Interlocked.Exchange(ref _maxTsMs, hi);
        }
        finally
        {
            _searcherManager.Release(searcher);
        }
    }

    /// <summary>
    /// The oldest and newest <c>ts</c> in the index, read straight from the field's NumericDocValues — one
    /// sequential pass over a columnar long per document, allocating nothing.
    ///
    /// <para>This used to be two top-1 searches sorted on <c>ts</c>, which is the natural way to ask and is
    /// a trap. A Lucene <see cref="Sort"/> on a numeric field reads DocValues when they exist and otherwise
    /// falls back to the legacy FieldCache, which <i>uninverts the entire term dictionary</i> into a single
    /// packed array sized by the doc count. One signal's schema — trace summaries — was missing its
    /// <c>NumericDocValuesField</c>, so on a large unsealed index that fallback asked for hundreds of
    /// megabytes in one contiguous block and threw <see cref="OutOfMemoryException"/> from inside a manager
    /// constructor, at 1.2 GB of a 2 GB container. Reading the DocValues directly means a schema that lacks
    /// them degrades to "bounds unknown" and a warning, instead of taking the process with it.</para>
    ///
    /// <para>Returns nulls when the index holds no live document, or when the field has no columnar copy in
    /// some segment. Bounds-unknown costs age-based sealing for that signal until the next roll; size-based
    /// sealing still works off the doc count, which is read from the writer.</para>
    /// </summary>
    private (long? Min, long? Max) BoundaryTs(IndexSearcher searcher)
    {
        long min = long.MaxValue, max = long.MinValue;
        bool any = false;

        foreach (AtomicReaderContext leaf in searcher.IndexReader.Leaves)
        {
            AtomicReader reader = leaf.AtomicReader;
            NumericDocValues values = reader.GetNumericDocValues(TsField);
            if (values is null)
            {
                // Recorded, not merely logged. This index was written by a schema with no columnar ts,
                // and the danger is that Lucene does NOT refuse to keep using it: append documents that do
                // carry the DocValue and the merge backfills these older ones with zero, so they read back
                // as 1970 from every columnar path while their stored ts still looks right. Sealing that
                // writes a catalog row retention deletes on sight. The manager discards the directory on
                // this signal — see SegmentManagerBase.OpenAndRepair.
                TsDocValuesMissing = true;
                _logger?.LogWarning(
                    "The active index at {Path} has no '{Field}' DocValues in one of its segments, so its "
                    + "unsealed time bounds cannot be recovered and nothing can be appended to it. A schema "
                    + "is not writing NumericDocValuesField({Field}).",
                    DirectoryPath ?? "(in-memory)", TsField, TsField);
                return (null, null);
            }

            IBits liveDocs = reader.LiveDocs;
            for (int doc = 0; doc < reader.MaxDoc; doc++)
            {
                if (liveDocs is not null && !liveDocs.Get(doc)) continue;
                long ts = values.Get(doc);
                if (ts < min) min = ts;
                if (ts > max) max = ts;
                any = true;
            }
        }

        return any ? (min, max) : (null, null);
    }

    /// <summary>
    /// True when this index holds documents written without a <c>ts</c> DocValue — a schema older than the
    /// one this build writes. Such an index can be read, but not appended to and not sealed (its time
    /// bounds, which the catalog row needs, are unrecoverable). The owning manager discards and recreates
    /// the directory on this signal; nothing else can, because the data is unsealed and no operator has a
    /// way to reach a PersistentVolume.
    /// </summary>
    public bool TsDocValuesMissing { get; private set; }

    /// <summary>Documents in the active index, recovered from disk on open — its future segment size.</summary>
    public long DocCount => Interlocked.Read(ref _count);

    /// <summary>Whether anything has been written (nothing to seal when false).</summary>
    public bool HasData => DocCount > 0;

    /// <summary>Earliest event time written, or null when unknown — becomes the sealed segment's MinTs.</summary>
    /// <remarks>
    /// Gated on the sentinel rather than on <see cref="HasData"/>. Documents present and bounds unknown is a
    /// real state — a recovered index whose schema carries no columnar <c>ts</c> — and reading the doc count
    /// to decide whether a timestamp exists then handed <c>long.MaxValue</c> to the epoch converter, which
    /// throws <see cref="ArgumentOutOfRangeException"/>. Null is the honest answer, and every caller already
    /// treats these as nullable.
    /// </remarks>
    public DateTime? MinTs => Interlocked.Read(ref _minTsMs) is long min && min != long.MaxValue
        ? TelemetryTime.FromEpochMillis(min)
        : null;

    /// <summary>Latest event time written, or null when unknown — becomes the sealed segment's MaxTs.</summary>
    public DateTime? MaxTs => Interlocked.Read(ref _maxTsMs) is long max && max != long.MinValue
        ? TelemetryTime.FromEpochMillis(max)
        : null;

    /// <summary>Adds one prepared document with its event timestamp (epoch ms). Does not commit.</summary>
    public void Add(Document doc, long tsMs)
    {
        _writer.AddDocument(doc);
        Interlocked.Increment(ref _count);
        UpdateMin(ref _minTsMs, tsMs);
        UpdateMax(ref _maxTsMs, tsMs);
    }

    private static void UpdateMin(ref long target, long value)
    {
        long cur;
        while (value < (cur = Interlocked.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, cur) == cur) return;
    }

    private static void UpdateMax(ref long target, long value)
    {
        long cur;
        while (value > (cur = Interlocked.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, cur) == cur) return;
    }

    /// <summary>Makes recently-written docs visible to new searchers (near-real-time refresh).</summary>
    public void Refresh() => _searcherManager.MaybeRefresh();

    /// <summary>Flushes the in-memory buffer to the directory (durability point).</summary>
    public void Commit() => _writer.Commit();

    /// <summary>Borrow a searcher over the current view. MUST be returned via <see cref="Release"/>.</summary>
    public IndexSearcher Acquire() => _searcherManager.Acquire();

    public void Release(IndexSearcher searcher) => _searcherManager.Release(searcher);

    public void Dispose()
    {
        _searcherManager.Dispose();
        _writer.Dispose();
        if (_ownsDirectory) _dir.Dispose();
    }
}

/// <summary>Epoch-millisecond conversions shared by the segment schemas and the active index.</summary>
public static class TelemetryTime
{
    public static long ToEpochMillis(DateTime ts) =>
        new DateTimeOffset(ts.ToUniversalTime()).ToUnixTimeMilliseconds();

    public static DateTime FromEpochMillis(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
