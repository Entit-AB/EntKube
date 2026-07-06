using System.Collections.Concurrent;

namespace EntKube.Web.Services;

/// <summary>
/// Per-cluster fixed-window rate limiter for the OTLP ingest endpoints — a chatty or misbehaving
/// cluster can't flood the shared telemetry store. Configured by <c>Telemetry:IngestMaxRequestsPerMinute</c>
/// (0 or unset = unlimited). In-memory and per management-plane instance; approximate by design (a
/// coarse backpressure guard, not a billing meter). Over-limit requests get 429 so the collector backs off.
/// </summary>
public sealed class IngestRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly int _maxPerWindow;
    private readonly ConcurrentDictionary<Guid, (int Count, DateTime WindowStart)> _buckets = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _stringBuckets = new();

    public IngestRateLimiter(IConfiguration config)
        => _maxPerWindow = config.GetValue<int?>("Telemetry:IngestMaxRequestsPerMinute") ?? 0;

    /// <summary>True to allow the request; false when the cluster exceeded its per-minute budget (config-driven; 0 = unlimited).</summary>
    public bool TryAcquire(Guid clusterId)
        => _maxPerWindow <= 0 || Acquire(_buckets, clusterId, _maxPerWindow);

    /// <summary>
    /// True to allow the request; false over the given per-minute budget for an arbitrary string key. Used by
    /// the public RUM endpoint to always throttle by client IP (an explicit, non-zero limit) BEFORE resolving
    /// the site key, so an unknown-key spray can't cost an unbounded DB lookup per request.
    /// </summary>
    public bool TryAcquire(string key, int maxPerWindow)
        => maxPerWindow <= 0 || Acquire(_stringBuckets, key, maxPerWindow);

    private static bool Acquire<TKey>(ConcurrentDictionary<TKey, (int Count, DateTime WindowStart)> buckets, TKey key, int max)
        where TKey : notnull
    {
        DateTime now = DateTime.UtcNow;
        while (true)
        {
            (int Count, DateTime WindowStart) current = buckets.GetOrAdd(key, _ => (0, now));

            // Window elapsed → start a fresh one (this request is the first in it).
            if (now - current.WindowStart >= Window)
            {
                if (buckets.TryUpdate(key, (1, now), current)) return true;
                continue;   // lost the race, re-read
            }

            if (current.Count >= max) return false;
            if (buckets.TryUpdate(key, (current.Count + 1, current.WindowStart), current)) return true;
            // lost the race, re-read
        }
    }
}
