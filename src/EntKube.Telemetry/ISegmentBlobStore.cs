namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Object storage for sealed telemetry segment archives — the durable tier of the Lucene/S3 engine.
/// Abstracted so the engine can run against real S3/MinIO in production (<see cref="S3SegmentBlobStore"/>)
/// and against a local directory in tests and no-object-storage dev (<see cref="LocalSegmentBlobStore"/>).
/// Keys are opaque paths (e.g. <c>logs/2026/07/07/&lt;guid&gt;.tar.zst</c>); values are whole files (a
/// zstd-compressed tar of the Lucene index directory — see <see cref="SegmentArchive"/>), uploaded and
/// downloaded as complete objects. Legacy <c>.zip</c> archives from older deploys are read transparently.
/// </summary>
public interface ISegmentBlobStore
{
    /// <summary>True when a backing bucket/location is configured; false disables sealing.</summary>
    bool IsConfigured { get; }

    /// <summary>Uploads a local file to <paramref name="key"/> (overwriting any existing object).</summary>
    Task PutAsync(string key, string localFilePath, CancellationToken ct = default);

    /// <summary>Downloads the object at <paramref name="key"/> to <paramref name="destFilePath"/>.</summary>
    Task GetAsync(string key, string destFilePath, CancellationToken ct = default);

    /// <summary>Deletes the object at <paramref name="key"/> (no-op if absent) — used by retention.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
