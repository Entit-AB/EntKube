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

    public IngestRateLimiter(IConfiguration config)
        => _maxPerWindow = config.GetValue<int?>("Telemetry:IngestMaxRequestsPerMinute") ?? 0;

    /// <summary>True to allow the request; false when the cluster exceeded its per-minute budget.</summary>
    public bool TryAcquire(Guid clusterId)
    {
        if (_maxPerWindow <= 0) return true;   // unlimited

        DateTime now = DateTime.UtcNow;
        while (true)
        {
            (int Count, DateTime WindowStart) current = _buckets.GetOrAdd(clusterId, _ => (0, now));

            // Window elapsed → start a fresh one (this request is the first in it).
            if (now - current.WindowStart >= Window)
            {
                if (_buckets.TryUpdate(clusterId, (1, now), current)) return true;
                continue;   // lost the race, re-read
            }

            if (current.Count >= _maxPerWindow) return false;
            if (_buckets.TryUpdate(clusterId, (current.Count + 1, current.WindowStart), current)) return true;
            // lost the race, re-read
        }
    }
}
