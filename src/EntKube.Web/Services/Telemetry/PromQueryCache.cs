using System.Collections.Concurrent;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// A short-lived cache in front of PromQL, with single-flight.
///
/// Two things make metrics feel slow that are not Prometheus being slow. First, a dashboard renders many
/// panels at once and several of them routinely ask the same question — the same range query for the same
/// service, or the same label-values lookup to populate a dropdown — so the same work is done repeatedly
/// within one render. Second, every one of those goes over the WAN through the API server's proxy, where
/// the round-trip dominates the query itself.
///
/// <b>Single-flight is the more important half.</b> Concurrent callers asking the same question share one
/// in-flight request rather than each starting their own, so a render that previously issued eight
/// identical queries issues one. A plain TTL cache would not help there at all: on a cold cache all eight
/// start before any finishes, and all eight miss.
///
/// The TTL is deliberately short — well under a scrape interval — so a chart is never showing data older
/// than the next scrape would have produced anyway. Only successes are cached: a failure held for even a
/// few seconds turns a transient blip into a sticky one, and makes a fixed misconfiguration look unfixed.
/// </summary>
public sealed class PromQueryCache(TimeSpan? ttl = null)
{
    /// <summary>How long a result stays usable. Shorter than a typical 15–30s scrape interval, so caching
    /// costs no freshness a fresh query would actually have gained.</summary>
    public TimeSpan Ttl { get; } = ttl ?? TimeSpan.FromSeconds(10);

    /// <summary>Bound on distinct cached queries. The key space is user-driven (any PromQL, any selector),
    /// so it needs a ceiling; past it the whole table is dropped rather than evicted one by one, which
    /// costs at most one extra round of queries and keeps this off the hot path.</summary>
    public const int MaxEntries = 512;

    private sealed record Entry(DateTime At, object Value);

    /// <summary>Type-erased outcome of one shared fetch, so a single dictionary can serve any result type.</summary>
    private sealed record Outcome(object? Data, string? Error);

    private readonly ConcurrentDictionary<string, Lazy<Task<Outcome>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Entry> _done = new();

    /// <summary>
    /// Returns a cached result for <paramref name="key"/>, or runs <paramref name="fetch"/> — once, however
    /// many callers arrive together. Callers that join a failing fetch receive its error rather than
    /// starting their own retry, so one bad query costs one round-trip, not one per caller.
    /// </summary>
    public async Task<KubernetesOperationResult<T>> GetOrFetchAsync<T>(
        string key, Func<Task<KubernetesOperationResult<T>>> fetch)
    {
        if (_done.TryGetValue(key, out Entry? hit) && DateTime.UtcNow - hit.At < Ttl)
            return KubernetesOperationResult<T>.Success((T)hit.Value);

        // Lazy, so a burst of concurrent callers creates exactly one task; the losers await the winner's.
        Lazy<Task<Outcome>> lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<Outcome>>(async () =>
        {
            KubernetesOperationResult<T> result = await fetch();
            if (!result.IsSuccess || result.Data is null)
                return new Outcome(null, result.Error ?? "The query returned no data.");

            // Successes only. A cached failure would make a transient blip sticky and a just-fixed
            // misconfiguration still look broken.
            if (_done.Count > MaxEntries) _done.Clear();
            _done[key] = new Entry(DateTime.UtcNow, result.Data);
            return new Outcome(result.Data, null);
        }));

        try
        {
            Outcome outcome = await lazy.Value;
            return outcome.Data is not null
                ? KubernetesOperationResult<T>.Success((T)outcome.Data)
                : KubernetesOperationResult<T>.Failure(outcome.Error!);
        }
        finally
        {
            // Clear the in-flight marker either way, so the next miss starts a fresh attempt rather than
            // re-awaiting a completed failure.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<Outcome>>>(key, lazy));
        }
    }

    /// <summary>Drops everything for one cluster — used when its Prometheus component changes.</summary>
    public void InvalidateCluster(Guid clusterId)
    {
        string prefix = clusterId.ToString("N") + "|";
        foreach (string key in _done.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _done.TryRemove(key, out _);
    }

    /// <summary>Builds a cache key. The cluster comes first so <see cref="InvalidateCluster"/> can prefix-match.</summary>
    public static string Key(Guid clusterId, params object?[] parts) =>
        clusterId.ToString("N") + "|" + string.Join('|', parts);
}
