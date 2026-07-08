namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// A filesystem-backed <see cref="ISegmentBlobStore"/> — segment archives are stored under a local root
/// directory, keyed by their object key. Used by tests and as a zero-dependency fallback when no S3/MinIO
/// object storage is configured (e.g. a single-node dev box), so the segment engine still functions
/// end-to-end (seal, retention, query) without an external bucket.
/// </summary>
public sealed class LocalSegmentBlobStore(string rootPath) : ISegmentBlobStore
{
    public bool IsConfigured => true;

    private string Resolve(string key) => Path.Combine(rootPath, key.Replace('/', Path.DirectorySeparatorChar));

    public Task PutAsync(string key, string localFilePath, CancellationToken ct = default)
    {
        string dest = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(localFilePath, dest, overwrite: true);
        return Task.CompletedTask;
    }

    public Task GetAsync(string key, string destFilePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
        File.Copy(Resolve(key), destFilePath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        string path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
