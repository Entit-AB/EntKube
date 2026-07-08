using System.Collections.Concurrent;

namespace EntKube.Web.Services.Telemetry;

/// <summary>Non-generic view of a per-signal manager registry, so the seal service can iterate any signal's
/// already-created (data-bearing) tenant managers without knowing the concrete manager type.</summary>
public interface ISegmentManagerRegistry
{
    /// <summary>The tenant managers that have actually been created (touched by ingest/query) — never forces creation.</summary>
    IReadOnlyCollection<SegmentManagerBase> ActiveManagers { get; }
}

/// <summary>
/// Holds one segment manager per tenant for a given signal (logs / spans / rum), created lazily the first
/// time a tenant is ingested to or queried. Telemetry is tenant-scoped, so every tenant gets its own active
/// index, cache, catalog partition, and object storage — nothing is shared across tenants. Registered as a
/// singleton per signal; the seal service iterates <see cref="ActiveManagers"/> to roll/retain each tenant.
/// </summary>
public sealed class SegmentManagerRegistry<TManager>(Func<Guid, TManager> create)
    : ISegmentManagerRegistry, IDisposable
    where TManager : SegmentManagerBase
{
    private readonly ConcurrentDictionary<Guid, Lazy<TManager>> _managers = new();

    /// <summary>The tenant's manager, created on first use (Lazy ensures the Lucene index is opened once).</summary>
    public TManager For(Guid tenantId)
        => _managers.GetOrAdd(tenantId, id => new Lazy<TManager>(() => create(id))).Value;

    public IReadOnlyCollection<SegmentManagerBase> ActiveManagers =>
        _managers.Values.Where(l => l.IsValueCreated).Select(l => (SegmentManagerBase)l.Value).ToArray();

    public void Dispose()
    {
        foreach (Lazy<TManager> m in _managers.Values)
            if (m.IsValueCreated)
                m.Value.Dispose();
    }
}

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
