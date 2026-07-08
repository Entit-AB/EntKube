using System.IO.Compression;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Local disk cache of sealed segment index directories. A query needs a segment's Lucene files on the
/// local filesystem to open a reader; this downloads the segment archive from object storage on a cache
/// miss, unzips it once, and thereafter serves it locally — so object storage is off the query hot path
/// for anything recently touched. Segment archives are immutable, so a cached copy never goes stale.
///
/// Readers are opened and closed per query (see <see cref="LogSegmentManager"/>), so nothing holds a
/// cached directory's files open between queries; retention (and any future LRU disk cap) can remove a
/// directory safely between queries via <see cref="Remove"/>.
/// </summary>
public sealed class SegmentCache(ISegmentBlobStore blobs, string cacheRoot)
{
    private string DirFor(Guid id) => Path.Combine(cacheRoot, id.ToString("N"));

    /// <summary>Ensures the segment's unzipped index dir exists locally (download+extract on miss); returns its path.</summary>
    public async Task<string> EnsureLocalAsync(TelemetrySegment seg, CancellationToken ct = default)
    {
        string dir = DirFor(seg.Id);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            return dir;

        Directory.CreateDirectory(cacheRoot);
        string tmpZip = Path.Combine(cacheRoot, seg.Id.ToString("N") + ".zip");
        await blobs.GetAsync(seg.ObjectKey, tmpZip, ct);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        ZipFile.ExtractToDirectory(tmpZip, dir);
        File.Delete(tmpZip);
        return dir;
    }

    /// <summary>Adopts an already-local index directory (the just-sealed active dir) as this segment's cache
    /// entry, avoiding a download of what we just wrote. Moves <paramref name="sourceDir"/> into the cache.</summary>
    public void Adopt(Guid id, string sourceDir)
    {
        string dir = DirFor(id);
        Directory.CreateDirectory(cacheRoot);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.Move(sourceDir, dir);
    }

    /// <summary>Removes the local copy of a segment (retention). Safe between queries; best-effort.</summary>
    public void Remove(Guid id)
    {
        string dir = DirFor(id);
        if (Directory.Exists(dir))
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* reclaimed next pass */ }
    }
}
