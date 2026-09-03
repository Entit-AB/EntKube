using EntKube.Telemetry;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Builds a per-tenant telemetry blob store: the tenant's chosen <see cref="Data.StorageLink"/> when set,
/// else shared flat-config / local-disk fallbacks (segment keys carry the tenant id, so the shared
/// fallbacks never mix tenants). The flat + local fallbacks are created once and shared; each tenant's
/// link client is built and cached inside the returned <see cref="TelemetrySegmentBlobStore"/>.
/// </summary>
public sealed class TenantBlobStoreFactory(
    IServiceScopeFactory scopeFactory,
    TelemetryStorageSettingService settings,
    IConfiguration config,
    SegmentEngineOptions options,
    ILoggerFactory loggerFactory) : IDisposable
{
    private readonly S3SegmentBlobStore _flat = new(config);
    private readonly LocalSegmentBlobStore _local = CreateLocal(options);

    public ISegmentBlobStore CreateFor(Guid tenantId)
        => new TelemetrySegmentBlobStore(
            tenantId, scopeFactory, settings, _flat, _local,
            loggerFactory.CreateLogger<TelemetrySegmentBlobStore>());

    private static LocalSegmentBlobStore CreateLocal(SegmentEngineOptions options)
    {
        string dir = Path.Combine(options.DataPath, "blobs");
        Directory.CreateDirectory(dir);
        return new LocalSegmentBlobStore(dir);
    }

    public void Dispose() => _flat.Dispose();
}
