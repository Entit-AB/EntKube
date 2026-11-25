namespace EntKube.Web.Services;

/// <summary>One endpoint that more than one scrape pool collects.</summary>
/// <param name="Url">The endpoint as one of the pools scrapes it.</param>
/// <param name="Pools">The pools, by name.</param>
public sealed record DuplicateScrape(string Url, IReadOnlyList<string> Pools);

/// <summary>
/// Finds endpoints scraped by more than one job.
///
/// <para>Each job stamps its own <c>job</c> label, so the same endpoint becomes two sets of series
/// and every <c>sum()</c> over them doubles — on a panel that otherwise looks right. This is read
/// from what Prometheus says it scrapes, so it needs no reasoning about which monitor selects
/// what, and it sees jobs EntKube knows nothing about.</para>
/// </summary>
public static class DuplicateScrapes
{
    /// <summary>
    /// Endpoints with more than one scrape pool, by host, port and path. The scheme is not part of
    /// it: one port speaks one scheme, and the job that has it wrong still scrapes that endpoint.
    /// The query string is: a blackbox exporter serves every probe on one path, and
    /// <c>?target=a</c> and <c>?target=b</c> are two endpoints.
    /// </summary>
    public static List<DuplicateScrape> Find(IEnumerable<ScrapeTarget> targets) =>
    [
        .. targets
            .GroupBy(t => Endpoint(t.ScrapeUrl), StringComparer.Ordinal)
            .Select(g => new DuplicateScrape(
                g.First().ScrapeUrl,
                [.. g.Select(t => t.Pool).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .Where(d => d.Pools.Count > 1)
            .OrderBy(d => d.Url, StringComparer.Ordinal),
    ];

    private static string Endpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? $"{uri.Host}:{uri.Port}{uri.PathAndQuery}"
            : url;
}
