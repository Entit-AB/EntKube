using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The telemetry object-storage blob store. It resolves its target at runtime (not at startup) so the
/// storage location is admin-configurable from the UI without a restart:
///   1. If a <see cref="StorageLink"/> is selected (admin sets it via <see cref="TelemetryStorageSettingService"/>),
///      sealed segments go to that provider (AWS S3 / Cleura S3 / MinIO) using the app's existing
///      credential + client abstraction (<see cref="StorageLinkClientFactory"/>). This is the primary path.
///   2. Otherwise, if the flat <c>Telemetry:ObjectStorage</c> config is set, it uses that (simple S3/MinIO).
///   3. Otherwise, sealed segments live on local disk under the engine's DataPath (single-node fallback).
///
/// The chosen storage-link client is built once (inside a DI scope — vault/EF are scoped) and cached,
/// rebuilt only when the admin changes the selection. NOTE: changing the link applies to NEW segments;
/// segments already sealed to a previous store remain there until retention ages them out.
/// </summary>
public sealed class TelemetrySegmentBlobStore : ISegmentBlobStore, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelemetryStorageSettingService _settings;
    private readonly S3SegmentBlobStore _flat;
    private readonly LocalSegmentBlobStore _local;
    private readonly ILogger<TelemetrySegmentBlobStore> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Guid? _builtLinkId;
    private IAmazonS3? _linkClient;
    private IDisposable? _linkCleanup;
    private string _linkBucket = "";

    public TelemetrySegmentBlobStore(
        IServiceScopeFactory scopeFactory,
        TelemetryStorageSettingService settings,
        IConfiguration config,
        SegmentEngineOptions options,
        ILogger<TelemetrySegmentBlobStore> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
        _flat = new S3SegmentBlobStore(config);
        string localBlobs = Path.Combine(options.DataPath, "blobs");
        Directory.CreateDirectory(localBlobs);
        _local = new LocalSegmentBlobStore(localBlobs);
    }

    public bool IsConfigured => true; // there is always at least the local-disk fallback

    public async Task PutAsync(string key, string localFilePath, CancellationToken ct = default)
    {
        (IAmazonS3? client, string bucket, ISegmentBlobStore? fallback) = await ResolveAsync(ct);
        if (client is not null)
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, FilePath = localFilePath }, ct);
        else
            await fallback!.PutAsync(key, localFilePath, ct);
    }

    public async Task GetAsync(string key, string destFilePath, CancellationToken ct = default)
    {
        (IAmazonS3? client, string bucket, ISegmentBlobStore? fallback) = await ResolveAsync(ct);
        if (client is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
            using GetObjectResponse response = await client.GetObjectAsync(bucket, key, ct);
            await response.WriteResponseStreamToFileAsync(destFilePath, append: false, ct);
        }
        else
        {
            await fallback!.GetAsync(key, destFilePath, ct);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        (IAmazonS3? client, string bucket, ISegmentBlobStore? fallback) = await ResolveAsync(ct);
        if (client is not null)
            await client.DeleteObjectAsync(bucket, key, ct);
        else
            await fallback!.DeleteAsync(key, ct);
    }

    // Picks the effective backend for the current setting: a StorageLink S3 client, or a fallback blob store.
    private async Task<(IAmazonS3? Client, string Bucket, ISegmentBlobStore? Fallback)> ResolveAsync(CancellationToken ct)
    {
        Guid? linkId = await _settings.GetStorageLinkIdAsync(ct);
        if (linkId is Guid id)
        {
            (IAmazonS3 client, string bucket) = await EnsureLinkClientAsync(id, ct);
            return (client, bucket, null);
        }
        return _flat.IsConfigured ? (null, "", _flat) : (null, "", _local);
    }

    private async Task<(IAmazonS3 Client, string Bucket)> EnsureLinkClientAsync(Guid id, CancellationToken ct)
    {
        if (_builtLinkId == id && _linkClient is not null) return (_linkClient, _linkBucket);
        await _lock.WaitAsync(ct);
        try
        {
            if (_builtLinkId == id && _linkClient is not null) return (_linkClient, _linkBucket);

            using IServiceScope scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var clientFactory = scope.ServiceProvider.GetRequiredService<StorageLinkClientFactory>();

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
            StorageLink link = await db.StorageLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new InvalidOperationException($"Telemetry storage link {id} not found.");

            (AmazonS3Client client, IDisposable? cleanup) = await clientFactory.CreateClientAsync(link.TenantId, link, ct);
            string bucket = link.BucketName
                ?? throw new InvalidOperationException($"Telemetry storage link '{link.Name}' has no bucket configured.");

            // Swap in the new client, disposing any previous one.
            _linkCleanup?.Dispose();
            _linkClient?.Dispose();
            _linkClient = client;
            _linkCleanup = cleanup;
            _linkBucket = bucket;
            _builtLinkId = id;
            _logger.LogInformation("Telemetry object storage: {Provider} storage link '{Link}' (bucket {Bucket}).",
                link.Provider, link.Name, bucket);
            return (_linkClient, _linkBucket);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _linkCleanup?.Dispose();
        _linkClient?.Dispose();
        (_flat as IDisposable)?.Dispose();
        _lock.Dispose();
    }
}
