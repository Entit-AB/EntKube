using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Object storage for sealed telemetry segments backed by an existing <see cref="StorageLink"/> — the
/// app's own AWS S3 / Cleura S3 / MinIO abstraction (credentials in the vault). This is the primary,
/// production storage path: point telemetry at a registered storage link via <c>Telemetry:StorageLinkId</c>
/// and sealed segments go to that provider through the same <see cref="StorageLinkClientFactory"/> the file
/// browser uses. (The flat <c>Telemetry:ObjectStorage</c> config + <see cref="S3SegmentBlobStore"/> and the
/// local-disk fallback remain for simple/dev setups without a storage link.)
///
/// The engine is process-wide (single replica), so the client is built once — inside a DI scope, since the
/// vault/EF services are scoped — from the configured link and cached (AmazonS3Client is thread-safe).
/// </summary>
public sealed class StorageLinkSegmentBlobStore : ISegmentBlobStore, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StorageLinkSegmentBlobStore> _logger;
    private readonly Guid? _linkId;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IAmazonS3? _client;
    private IDisposable? _cleanup;
    private string _bucket = "";

    public StorageLinkSegmentBlobStore(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<StorageLinkSegmentBlobStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _linkId = Guid.TryParse(config.GetValue<string>("Telemetry:StorageLinkId"), out Guid g) ? g : null;
    }

    public bool IsConfigured => _linkId is not null;

    public async Task PutAsync(string key, string localFilePath, CancellationToken ct = default)
    {
        (IAmazonS3 client, string bucket) = await EnsureClientAsync(ct);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, FilePath = localFilePath }, ct);
    }

    public async Task GetAsync(string key, string destFilePath, CancellationToken ct = default)
    {
        (IAmazonS3 client, string bucket) = await EnsureClientAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
        using GetObjectResponse response = await client.GetObjectAsync(bucket, key, ct);
        await response.WriteResponseStreamToFileAsync(destFilePath, append: false, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        (IAmazonS3 client, string bucket) = await EnsureClientAsync(ct);
        await client.DeleteObjectAsync(bucket, key, ct);
    }

    // Lazily resolves the configured storage link and builds its S3 client once, caching both. The build
    // needs the scoped vault/EF services, so it runs inside a temporary DI scope.
    private async Task<(IAmazonS3 Client, string Bucket)> EnsureClientAsync(CancellationToken ct)
    {
        if (_client is not null) return (_client, _bucket);
        await _initLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return (_client, _bucket);
            if (_linkId is null)
                throw new InvalidOperationException("Telemetry:StorageLinkId is not configured.");

            using IServiceScope scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var clientFactory = scope.ServiceProvider.GetRequiredService<StorageLinkClientFactory>();

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
            StorageLink link = await db.StorageLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == _linkId, ct)
                ?? throw new InvalidOperationException($"Telemetry storage link {_linkId} not found.");

            (AmazonS3Client client, IDisposable? cleanup) = await clientFactory.CreateClientAsync(link.TenantId, link, ct);
            _bucket = link.BucketName
                ?? throw new InvalidOperationException($"Telemetry storage link {_linkId} has no bucket configured.");
            _cleanup = cleanup;
            _client = client;
            _logger.LogInformation("Telemetry object storage: using {Provider} storage link {Link} (bucket {Bucket}).",
                link.Provider, link.Name, _bucket);
            return (_client, _bucket);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _cleanup?.Dispose();
        _client?.Dispose();
        _initLock.Dispose();
    }
}
