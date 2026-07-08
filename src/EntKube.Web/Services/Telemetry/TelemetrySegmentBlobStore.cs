using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// A single tenant's telemetry object-storage blob store. Telemetry is tenant-scoped, so one of these
/// backs each tenant's segment managers and resolves that tenant's storage target at runtime:
///   1. The tenant's selected <see cref="StorageLink"/> (AWS S3 / Cleura S3 / MinIO, vault credentials) —
///      the primary path, so a tenant's data lives in its own bucket.
///   2. Otherwise the shared flat <c>Telemetry:ObjectStorage</c> config, keyed by the tenant-prefixed
///      object key so tenants never share a segment file.
///   3. Otherwise local disk (single-node), likewise per-tenant-prefixed.
///
/// The chosen storage-link client is built once (inside a DI scope — vault/EF are scoped) and cached,
/// rebuilt only when the tenant's admin changes the selection. Object keys already carry the tenant id
/// (see <see cref="SegmentManagerBase"/>), so the shared fallbacks never mix tenants.
/// </summary>
public sealed class TelemetrySegmentBlobStore(
    Guid tenantId,
    IServiceScopeFactory scopeFactory,
    TelemetryStorageSettingService settings,
    ISegmentBlobStore flatFallback,
    ISegmentBlobStore localFallback,
    ILogger logger) : ISegmentBlobStore, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Guid? _builtLinkId;
    private IAmazonS3? _linkClient;
    private IDisposable? _linkCleanup;
    private string _linkBucket = "";

    public bool IsConfigured => true; // always at least the local fallback

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

    private async Task<(IAmazonS3? Client, string Bucket, ISegmentBlobStore? Fallback)> ResolveAsync(CancellationToken ct)
    {
        Guid? linkId = await settings.GetStorageLinkIdAsync(tenantId, ct);
        if (linkId is Guid id)
        {
            (IAmazonS3 client, string bucket) = await EnsureLinkClientAsync(id, ct);
            return (client, bucket, null);
        }
        return flatFallback.IsConfigured ? (null, "", flatFallback) : (null, "", localFallback);
    }

    private async Task<(IAmazonS3 Client, string Bucket)> EnsureLinkClientAsync(Guid id, CancellationToken ct)
    {
        if (_builtLinkId == id && _linkClient is not null) return (_linkClient, _linkBucket);
        await _lock.WaitAsync(ct);
        try
        {
            if (_builtLinkId == id && _linkClient is not null) return (_linkClient, _linkBucket);

            using IServiceScope scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var clientFactory = scope.ServiceProvider.GetRequiredService<StorageLinkClientFactory>();

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
            StorageLink link = await db.StorageLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new InvalidOperationException($"Telemetry storage link {id} (tenant {tenantId}) not found.");
            if (link.TenantId != tenantId)
                throw new InvalidOperationException(
                    $"Telemetry storage link {id} belongs to tenant {link.TenantId}, not {tenantId} — refusing to cross tenants.");

            (AmazonS3Client client, IDisposable? cleanup) = await clientFactory.CreateClientAsync(link.TenantId, link, ct);
            string bucket = link.BucketName
                ?? throw new InvalidOperationException($"Telemetry storage link '{link.Name}' has no bucket configured.");

            _linkCleanup?.Dispose();
            _linkClient?.Dispose();
            _linkClient = client;
            _linkCleanup = cleanup;
            _linkBucket = bucket;
            _builtLinkId = id;
            logger.LogInformation("Tenant {Tenant} telemetry storage: {Provider} link '{Link}' (bucket {Bucket}).",
                tenantId, link.Provider, link.Name, bucket);
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
        _lock.Dispose();
        // flatFallback / localFallback are shared and owned by the factory — not disposed here.
    }
}
