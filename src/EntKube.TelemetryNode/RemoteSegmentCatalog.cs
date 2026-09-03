using System.Net.Http.Headers;
using System.Net.Http.Json;
using EntKube.Telemetry;
using EntKube.Web.Data;

namespace EntKube.TelemetryNode;

/// <summary>
/// The querier's <see cref="ISegmentCatalog"/>: reads the segment list from the indexer over HTTP.
///
/// The catalog is a SQLite file on the indexer's own volume, which a querier pod cannot open — but it does
/// not need to write it, only to know which segments exist and what time range each covers. Everything
/// after that is object storage, which any pod with the bucket credentials can read. So the querier borrows
/// the map and fetches the territory itself.
///
/// Writes are refused rather than silently dropped: only the indexer seals, and a querier that appeared to
/// catalog a segment would be describing an archive nobody wrote.
/// </summary>
public sealed class RemoteSegmentCatalog(
    IHttpClientFactory clients, string clientName, NodeOptions options, ILogger<RemoteSegmentCatalog> logger)
    : ISegmentCatalog
{
    /// <summary>
    /// Segment lists change only when the indexer seals (at most every few minutes) but are read on every
    /// query, so a short cache turns a per-query HTTP round-trip into a per-window one. Kept brief so a
    /// freshly sealed segment becomes visible quickly.
    /// </summary>
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(15);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, IReadOnlyList<TelemetrySegment> Segments)>
        _cache = new();

    public Task AddAsync(TelemetrySegment segment, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "A querier does not seal segments — only the indexer writes the catalog. This call means a " +
            "write path was wired into the query role by mistake.");

    public Task<IReadOnlyList<TelemetrySegment>> RemoveExpiredAsync(
        Guid tenantId, string signal, DateTime cutoff, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Retention runs on the indexer, which owns the catalog and the objects. A querier deleting " +
            "either would race the indexer over shared state.");

    public async Task<IReadOnlyList<TelemetrySegment>> ListOverlappingAsync(
        Guid tenantId, string signal, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        string key = $"{tenantId:N}|{signal}|{from:O}|{to:O}";
        if (_cache.TryGetValue(key, out (DateTime At, IReadOnlyList<TelemetrySegment> Segments) hit)
            && DateTime.UtcNow - hit.At < ListTtl)
            return hit.Segments;

        var url = $"internal/segments?signal={Uri.EscapeDataString(signal)}";
        if (from is DateTime f) url += $"&from={Uri.EscapeDataString(f.ToString("O"))}";
        if (to is DateTime t) url += $"&to={Uri.EscapeDataString(t.ToString("O"))}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.QueryToken);

        using HttpClient http = clients.CreateClient(clientName);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        IReadOnlyList<TelemetrySegment> segments =
            await response.Content.ReadFromJsonAsync<List<TelemetrySegment>>(ct) ?? [];

        // Bound the key space: the cache is keyed partly by the caller's time window, which varies freely.
        if (_cache.Count > 256) _cache.Clear();
        _cache[key] = (DateTime.UtcNow, segments);

        logger.LogDebug("Fetched {Count} {Signal} segment(s) from the indexer for {From}..{To}",
            segments.Count, signal, from, to);
        return segments;
    }

    public async Task<DateTime?> GetMinTsAsync(Guid tenantId, string signal, CancellationToken ct = default)
    {
        IReadOnlyList<TelemetrySegment> all = await ListOverlappingAsync(tenantId, signal, null, null, ct);
        return all.Count == 0 ? null : all.Min(s => s.MinTs);
    }
}
