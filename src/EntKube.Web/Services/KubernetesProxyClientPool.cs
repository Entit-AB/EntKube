using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using k8s;

namespace EntKube.Web.Services;

/// <summary>
/// Keeps one long-lived <see cref="Kubernetes"/> client per distinct kubeconfig, for the read paths that
/// reach in-cluster HTTP APIs through the API server's pod/service proxy — Prometheus, Loki, and the
/// in-cluster telemetry querier.
///
/// Those services used to build a client per call, and a <see cref="Kubernetes"/> client owns its own
/// <see cref="HttpClient"/>, so every query opened a new connection and paid a full TLS handshake to a
/// possibly-distant API server. Rendering one dashboard is a dozen PromQL queries, so it was a dozen
/// handshakes before any data moved. Sharing one client per cluster collapses them onto a warm connection
/// pool, which is the cheapest available win on telemetry query latency.
///
/// Entries are keyed by a hash of the kubeconfig text, so a rotated kubeconfig produces a new entry
/// instead of reusing stale credentials. An entry is also rebuilt once it passes <see cref="MaxAge"/>,
/// bounding how long an expiring client certificate or a moved API-server endpoint keeps being used.
/// A replaced client is disposed only after <see cref="DisposeGrace"/>, so requests already in flight on
/// it finish normally rather than failing on a disposed handler.
///
/// Registered as a singleton — the pool is shared by every scoped service and every circuit.
/// </summary>
public sealed class KubernetesProxyClientPool(ILogger<KubernetesProxyClientPool> logger) : IDisposable
{
    /// <summary>Rebuild a pooled client once it is this old, so credential rotation is picked up.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>How long a replaced client stays alive for in-flight requests before disposal.</summary>
    public static readonly TimeSpan DisposeGrace = TimeSpan.FromMinutes(1);

    /// <summary>Cap on distinct cached clusters. Beyond this the least-recently-used entry is retired.</summary>
    public const int MaxEntries = 64;

    private sealed record Entry(Kubernetes Client, DateTime CreatedUtc)
    {
        public long LastUsedTick;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private long _clock;
    private bool _disposed;

    /// <summary>
    /// Returns a shared client for <paramref name="kubeconfig"/>. The caller MUST NOT dispose it — the
    /// pool owns its lifetime, and disposing it would break every other caller on the same cluster.
    /// </summary>
    public Kubernetes Get(string kubeconfig)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(kubeconfig))
            throw new ArgumentException("Kubeconfig is empty.", nameof(kubeconfig));

        string key = KeyFor(kubeconfig);

        // Bounded retry: each iteration either wins a compare-and-swap against the exact entry it read,
        // or loses to a concurrent caller and re-reads. Losing is self-limiting — the winner's entry is
        // fresh, so the next iteration takes the fast path.
        for (int attempt = 0; ; attempt++)
        {
            if (_entries.TryGetValue(key, out Entry? existing) && !IsStale(existing))
                return Touch(existing);

            // Built outside any dictionary delegate: ConcurrentDictionary may invoke a factory more than
            // once under contention, and every spare invocation would leak a client with an open
            // connection pool behind it.
            Entry fresh = new(Build(kubeconfig), DateTime.UtcNow)
            {
                LastUsedTick = Interlocked.Increment(ref _clock),
            };

            // TryUpdate/TryAdd give a real CAS against the instance we read, so we can only ever retire
            // the entry we actually displaced — never one a concurrent caller already replaced.
            bool won = existing is null
                ? _entries.TryAdd(key, fresh)
                : _entries.TryUpdate(key, fresh, existing);

            if (won)
            {
                // Let the displaced client's in-flight requests drain before it is disposed.
                if (existing is not null) RetireAfterGrace(existing.Client);
                TrimToCap();
                return fresh.Client;
            }

            if (attempt >= 3)
            {
                // Pathological contention. Stop trying to displace a stale entry and settle for whatever is
                // pooled — at most a moment past MaxAge, and still perfectly usable. GetOrAdd guarantees we
                // return something the pool owns, so the caller never receives a client nobody will dispose.
                logger.LogDebug("Kubernetes client pool contended {Attempts} times; accepting the pooled client.", attempt);
                Entry settled = _entries.GetOrAdd(key, fresh);
                if (!ReferenceEquals(settled, fresh)) fresh.Client.Dispose();
                return Touch(settled);
            }

            // Lost the race. Ours was never handed out, so it needs no grace period.
            fresh.Client.Dispose();
        }
    }

    /// <summary>Marks an entry as just-used so <see cref="TrimToCap"/> evicts genuinely idle clusters.</summary>
    private Kubernetes Touch(Entry e)
    {
        Interlocked.Exchange(ref e.LastUsedTick, Interlocked.Increment(ref _clock));
        return e.Client;
    }

    private static bool IsStale(Entry e) => DateTime.UtcNow - e.CreatedUtc > MaxAge;

    private static Kubernetes Build(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }

    /// <summary>Hashes the kubeconfig so the key carries no credential material into memory dumps or logs.</summary>
    private static string KeyFor(string kubeconfig) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kubeconfig)));

    /// <summary>Retires least-recently-used entries once the pool exceeds <see cref="MaxEntries"/>.</summary>
    private void TrimToCap()
    {
        if (_entries.Count <= MaxEntries) return;

        foreach (KeyValuePair<string, Entry> victim in _entries
                     .OrderBy(kv => Interlocked.Read(ref kv.Value.LastUsedTick))
                     .Take(_entries.Count - MaxEntries))
        {
            if (_entries.TryRemove(victim.Key, out Entry? removed))
                RetireAfterGrace(removed.Client);
        }
    }

    private void RetireAfterGrace(Kubernetes client) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(DisposeGrace);
            client.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Retiring a pooled Kubernetes client failed; it will be collected instead.");
        }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Entry e in _entries.Values) e.Client.Dispose();
        _entries.Clear();
    }
}
