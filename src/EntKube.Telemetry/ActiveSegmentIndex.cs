using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using FSDirectory = Lucene.Net.Store.FSDirectory;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace EntKube.Web.Services.Telemetry;

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
    private long _count;
    private long _minTsMs = long.MaxValue;
    private long _maxTsMs = long.MinValue;

    /// <summary>Opens (or creates) an active index at on-disk <paramref name="path"/> using the shared analyzer.</summary>
    public static ActiveSegmentIndex OpenAt(string path, Analyzer analyzer)
    {
        System.IO.Directory.CreateDirectory(path);
        return new ActiveSegmentIndex(FSDirectory.Open(path), analyzer, ownsDirectory: true, directoryPath: path);
    }

    /// <summary>On-disk path of this index (set when opened via <see cref="OpenAt"/>); null for in-memory tests.</summary>
    public string? DirectoryPath { get; }

    public ActiveSegmentIndex(LuceneDirectory dir, Analyzer analyzer, bool ownsDirectory = true, string? directoryPath = null)
    {
        _dir = dir;
        _ownsDirectory = ownsDirectory;
        DirectoryPath = directoryPath;
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND,
        };
        _writer = new IndexWriter(_dir, config);
        _writer.Commit(); // materialize an empty commit so a searcher can open before the first write
        _searcherManager = new SearcherManager(_writer, applyAllDeletes: true, new SearcherFactory());
    }

    /// <summary>Number of documents written since this active index was created (its future segment size).</summary>
    public long DocCount => Interlocked.Read(ref _count);

    /// <summary>Whether anything has been written (nothing to seal when false).</summary>
    public bool HasData => DocCount > 0;

    /// <summary>Earliest event time written, or null when empty — becomes the sealed segment's MinTs.</summary>
    public DateTime? MinTs => HasData ? TelemetryTime.FromEpochMillis(Interlocked.Read(ref _minTsMs)) : null;

    /// <summary>Latest event time written, or null when empty — becomes the sealed segment's MaxTs.</summary>
    public DateTime? MaxTs => HasData ? TelemetryTime.FromEpochMillis(Interlocked.Read(ref _maxTsMs)) : null;

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
