using System.Collections.Concurrent;

namespace EntKube.Telemetry;

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
    /// <remarks>
    /// A failed open is forgotten rather than remembered. <see cref="Lazy{T}"/> caches its factory's
    /// exception for the lifetime of the instance, so a manager that failed to open once could never be
    /// retried: every later ingest and query for that tenant rethrew the same error instantly, with no path
    /// back even after the cause was gone.
    ///
    /// <para>Worse, it was silent and it compounded. <see cref="ActiveManagers"/> filters on
    /// <c>IsValueCreated</c>, which stays false for a faulted <see cref="Lazy{T}"/> — so the seal service
    /// never saw the tenant, never rolled its active index, and the index grew without bound. The one
    /// failure mode that makes an index hard to open is an index that is too large, so each restart
    /// recovered a bigger one than the last and the node could only get worse. Dropping the faulted entry
    /// makes the next call try again, which is what lets a restart actually be a remedy.</para>
    /// </remarks>
    public TManager For(Guid tenantId)
    {
        Lazy<TManager> lazy = _managers.GetOrAdd(tenantId, id => new Lazy<TManager>(() => create(id)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Remove this exact entry: another thread may already have replaced it with a fresh attempt.
            _managers.TryRemove(new KeyValuePair<Guid, Lazy<TManager>>(tenantId, lazy));
            throw;
        }
    }

    public IReadOnlyCollection<SegmentManagerBase> ActiveManagers =>
        _managers.Values.Where(l => l.IsValueCreated).Select(l => (SegmentManagerBase)l.Value).ToArray();

    public void Dispose()
    {
        foreach (Lazy<TManager> m in _managers.Values)
            if (m.IsValueCreated)
                m.Value.Dispose();
    }
}
