using System.Collections.Concurrent;
using EntKube.Web.Data;

namespace EntKube.Telemetry;

/// <summary>
/// The WARM tier: sealed segment index directories held on local disk, between the HOT active index and
/// the COLD archives in object storage.
///
/// A query needs a segment's Lucene files on a local filesystem to open a reader. This keeps them there —
/// downloading and unpacking the archive on a miss, adopting the files directly when the segment was just
/// sealed here (no download at all), and serving every later query off the volume. Segment archives are
/// immutable, so a local copy can never go stale.
///
/// Unlike an unbounded cache, the tier is <b>bounded on two axes</b>, because a telemetry volume that
/// grows without limit eventually stops the pod rather than degrading it:
/// <list type="bullet">
/// <item>by <b>age</b> — <see cref="SegmentEngineOptions.WarmRetentionDays"/> past the segment's newest
///   event, which is the "recent data is what gets queried" assumption made explicit; and</item>
/// <item>by <b>size</b> — <see cref="SegmentEngineOptions.WarmMaxBytes"/>, evicting least-recently-used
///   segments first, as the backstop for a burst that seals more than the age window anticipated.</item>
/// </list>
/// Eviction is never data loss: the archive stays in object storage for the full retention window and the
/// catalog row is untouched, so an evicted segment is still queryable — it just costs a download again.
///
/// This class only <i>decides and accounts</i>; it never deletes a directory that a reader may still have
/// open. <see cref="SegmentManagerBase"/> owns that ordering — it releases the reader first and deletes
/// the files once Lucene reports it truly closed.
/// </summary>
public sealed class SegmentCache(ISegmentBlobStore blobs, string cacheRoot)
{
    /// <summary>What the tier knows about one locally-held segment: its footprint and when it was last read.</summary>
    private sealed class Entry
    {
        public long Bytes;
        public long LastUsedTick;
        public DateTime MaxTs;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private long _clock;

    private string DirFor(Guid id) => Path.Combine(cacheRoot, id.ToString("N"));

    /// <summary>Total bytes currently held on local disk by this tier.</summary>
    public long LocalBytes => _entries.Values.Sum(e => e.Bytes);

    /// <summary>How many segments are currently resident locally.</summary>
    public int LocalCount => _entries.Count;

    /// <summary>Ensures the segment's unpacked index dir exists locally (download+extract on miss); returns its path.</summary>
    public async Task<string> EnsureLocalAsync(TelemetrySegment seg, CancellationToken ct = default)
    {
        string dir = DirFor(seg.Id);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Track(seg.Id, dir, seg.MaxTs);
            return dir;
        }

        Directory.CreateDirectory(cacheRoot);
        // Keep the downloaded archive's real extension so the object key's format is obvious on disk; the
        // unpacker sniffs the content anyway (new zstd-tar vs a legacy Deflate zip).
        string tmpArchive = Path.Combine(cacheRoot, seg.Id.ToString("N") + Path.GetExtension(seg.ObjectKey));
        await blobs.GetAsync(seg.ObjectKey, tmpArchive, ct);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        await SegmentArchive.UnpackAsync(tmpArchive, dir, ct);
        File.Delete(tmpArchive);
        Track(seg.Id, dir, seg.MaxTs);
        return dir;
    }

    /// <summary>Adopts an already-local index directory (the just-sealed active dir) as this segment's cache
    /// entry, avoiding a download of what we just wrote. Moves <paramref name="sourceDir"/> into the cache.</summary>
    public void Adopt(Guid id, string sourceDir, DateTime maxTs)
    {
        string dir = DirFor(id);
        Directory.CreateDirectory(cacheRoot);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.Move(sourceDir, dir);
        Track(id, dir, maxTs);
    }

    /// <summary>Removes the local copy of a segment. The caller must already have released any reader on
    /// it — see <see cref="SegmentManagerBase"/>. Best-effort: a directory still held open is retried on a
    /// later pass rather than throwing.</summary>
    public void Remove(Guid id)
    {
        _entries.TryRemove(id, out _);
        string dir = DirFor(id);
        if (Directory.Exists(dir))
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* reclaimed next pass */ }
    }

    /// <summary>
    /// The segments that should no longer be held locally, most-evictable first: everything past the age
    /// window, then least-recently-used entries until the tier fits inside its size ceiling.
    ///
    /// Returned ids are candidates, not commands — the caller decides when it is safe to delete each one.
    /// </summary>
    public IReadOnlyList<Guid> SelectEvictions(SegmentEngineOptions options, DateTime utcNow)
    {
        var evict = new List<Guid>();
        var survivors = new List<KeyValuePair<Guid, Entry>>();

        int warmDays = Math.Clamp(options.WarmRetentionDays, 0, options.RetentionDays);
        DateTime ageCutoff = utcNow.AddDays(-warmDays);

        foreach (KeyValuePair<Guid, Entry> kv in _entries)
        {
            if (kv.Value.MaxTs < ageCutoff) evict.Add(kv.Key);
            else survivors.Add(kv);
        }

        if (options.WarmMaxBytes <= 0) return evict;

        long remaining = survivors.Sum(kv => kv.Value.Bytes);
        if (remaining <= options.WarmMaxBytes) return evict;

        // Over the ceiling even after ageing out: give up the coldest reads first.
        foreach (KeyValuePair<Guid, Entry> kv in survivors.OrderBy(kv => Interlocked.Read(ref kv.Value.LastUsedTick)))
        {
            if (remaining <= options.WarmMaxBytes) break;
            evict.Add(kv.Key);
            remaining -= kv.Value.Bytes;
        }
        return evict;
    }

    /// <summary>
    /// Rebuilds the tier's accounting from what is already on disk. A restart otherwise starts believing it
    /// holds nothing, so neither bound would apply until every resident segment happened to be read again —
    /// which for aged-out segments is precisely never, and they would sit on the volume forever.
    /// </summary>
    public void Rehydrate(IReadOnlyDictionary<Guid, DateTime> knownMaxTs)
    {
        if (!Directory.Exists(cacheRoot)) return;

        foreach (string dir in Directory.EnumerateDirectories(cacheRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out Guid id)) continue;
            // A directory with no catalog row is an orphan — the segment was dropped while we were down.
            // Date it to the epoch so the age rule collects it on the next pass.
            DateTime maxTs = knownMaxTs.TryGetValue(id, out DateTime ts) ? ts : DateTime.MinValue;
            Track(id, dir, maxTs);
        }
    }

    /// <summary>Records (or refreshes) an entry, measuring its on-disk footprint and stamping it as just used.</summary>
    private void Track(Guid id, string dir, DateTime maxTs)
    {
        Entry entry = _entries.GetOrAdd(id, _ => new Entry { Bytes = DirectorySize(dir), MaxTs = maxTs });
        entry.MaxTs = maxTs;
        Interlocked.Exchange(ref entry.LastUsedTick, Interlocked.Increment(ref _clock));
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (IOException) { return 0; }          // raced with a delete; it will be re-measured or dropped
        catch (UnauthorizedAccessException) { return 0; }
    }
}
