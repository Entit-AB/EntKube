using System.Formats.Tar;
using System.IO.Compression;
using ZstdSharp;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Packs and unpacks a sealed segment's Lucene index directory as a single object-storage archive. This is
/// the dominant lever on at-rest telemetry size: the whole segment directory (stored fields + inverted
/// index + DocValues) is tarred and compressed with <b>zstd</b>, which roughly halves the archive versus the
/// old <c>ZipFile</c>/Deflate seal — a global high-ratio pass beats Lucene's per-chunk LZ4 stored-fields
/// compression re-zipped. zstd also decompresses substantially faster than Deflate, so the cold-query path
/// (download → unpack → open reader) speeds up too.
///
/// <para><b>Back-compat / no migration:</b> <see cref="UnpackAsync"/> sniffs the archive's magic bytes, so
/// segments an older deploy sealed as a Deflate <c>.zip</c> keep extracting via the zip path while new seals
/// write <c>.tar.zst</c>. Nothing needs rewriting; the two formats coexist for the whole retention window.</para>
/// </summary>
public static class SegmentArchive
{
    /// <summary>zstd frame magic (little-endian 0xFD2FB528) — the first four bytes of every zstd archive.</summary>
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>The object-key suffix for archives this class writes.</summary>
    public const string Extension = ".tar.zst";

    /// <summary>Tars <paramref name="sourceDir"/> and zstd-compresses it (at <paramref name="zstdLevel"/>,
    /// clamped to zstd's 1..22) into <paramref name="destFile"/>.</summary>
    public static async Task PackAsync(string sourceDir, string destFile, int zstdLevel, CancellationToken ct = default)
    {
        int level = Math.Clamp(zstdLevel, Compressor.MinCompressionLevel, Compressor.MaxCompressionLevel);
        await using FileStream fs = File.Create(destFile);
        // Dispose order (reverse of declaration): the zstd stream flushes its final frame before fs closes.
        await using var zstd = new CompressionStream(fs, level);
        await TarFile.CreateFromDirectoryAsync(sourceDir, zstd, includeBaseDirectory: false, ct);
    }

    /// <summary>Extracts an archive (zstd-tar or a legacy Deflate zip, auto-detected) into
    /// <paramref name="destDir"/>, which is created if absent.</summary>
    public static async Task UnpackAsync(string archiveFile, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        if (await IsZstdAsync(archiveFile, ct))
        {
            await using FileStream fs = File.OpenRead(archiveFile);
            await using var zstd = new DecompressionStream(fs);
            await TarFile.ExtractToDirectoryAsync(zstd, destDir, overwriteFiles: true, ct);
        }
        else
        {
            // Legacy segment sealed as a Deflate zip by an older deploy.
            ZipFile.ExtractToDirectory(archiveFile, destDir, overwriteFiles: true);
        }
    }

    private static async Task<bool> IsZstdAsync(string file, CancellationToken ct)
    {
        await using FileStream fs = File.OpenRead(file);
        var head = new byte[4];
        int read = await fs.ReadAsync(head.AsMemory(0, 4), ct);
        return read == 4 && head.AsSpan().SequenceEqual(ZstdMagic);
    }
}
