using EntKube.Web.Services.ScrapeMonitors;

namespace EntKube.Web.Services;

/// <summary>One of the scrapes of a doubled endpoint: the pool it is in, and the job label it stamps.</summary>
/// <param name="Pool">The scrape pool, which is the job's configuration.</param>
/// <param name="Job">The <c>job</c> label of the target, or null when it has none.</param>
/// <param name="Path">
/// The path and query it scrapes, where the scrapes of one endpoint do not all scrape the same one;
/// null where they do.
/// </param>
public sealed record EndpointScrape(string Pool, string? Job, string? Path = null)
{
    /// <summary>
    /// How a person tells it from the others. The job is said only where it differs from the pool:
    /// one ServiceMonitor that reaches a pod through two Services is one pool, and only the job
    /// (one per Service) tells its two scrapes apart.
    /// </summary>
    public override string ToString()
    {
        string named = string.IsNullOrEmpty(Job) || Job == Pool ? Pool : $"{Pool} (job {Job})";
        return Path is null ? named : $"{named} on {Path}";
    }
}

/// <summary>One endpoint that Prometheus scrapes more than once.</summary>
/// <param name="Url">The endpoint as one of its scrapes has it.</param>
/// <param name="Scrapes">Every scrape of it, by pool and job.</param>
public sealed record DuplicateScrape(string Url, IReadOnlyList<EndpointScrape> Scrapes)
{
    /// <summary>The pools among the scrapes, each once. One pool may scrape the endpoint twice.</summary>
    public IReadOnlyList<string> Pools =>
        [.. Scrapes.Select(s => s.Pool).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Finds endpoints that Prometheus scrapes more than once.
///
/// <para>Each target is its own set of series, so the same endpoint scraped twice is two sets and
/// every <c>sum()</c> over them doubles, on a panel that otherwise looks right. This is read from
/// what Prometheus says it scrapes. It needs no reasoning about which monitor selects what, and it
/// sees jobs EntKube knows nothing about.</para>
///
/// <para>Two targets in one pool count as well. Prometheus merges targets whose label sets are the
/// same, so two that it lists for one endpoint have different labels and are two scrapes. A
/// ServiceMonitor that reaches one pod port through two Services gives one target per Service, each
/// with that Service as its <c>job</c>, all in <c>serviceMonitor/ns/name/0</c>. Counting pools hid it.</para>
/// </summary>
public static class DuplicateScrapes
{
    /// <summary>
    /// Endpoints with more than one active target, by the rule the monitor planner decides by
    /// (<see cref="ScrapedEndpoints.ServesItsOwnMetrics"/>): a port's own series are one endpoint,
    /// whatever path each job asks for them on, and a scrape that asks for something else (a probe, a
    /// query, the kubelet's cAdvisor set) is an endpoint of its own, by its path and query. A blackbox
    /// exporter serves every probe on one path, and <c>?target=a</c> and <c>?target=b</c> are two.
    /// The scheme is not part of it: one port speaks one scheme, and the job that has it wrong still
    /// scrapes that endpoint.
    /// </summary>
    public static List<DuplicateScrape> Find(IEnumerable<ScrapeTarget> targets) =>
    [
        .. targets
            .GroupBy(t => Endpoint(t.ScrapeUrl), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                // Where the scrapes ask for the port's series on different paths, each says its own.
                bool onePath = g.Select(t => PathOf(t.ScrapeUrl)).Distinct(StringComparer.Ordinal).Count() == 1;

                return new DuplicateScrape(
                    g.First().ScrapeUrl,
                    [
                        .. g.Select(t => new EndpointScrape(
                                t.Pool, t.Labels.GetValueOrDefault("job"), onePath ? null : PathOf(t.ScrapeUrl)))
                            .OrderBy(s => s.Pool, StringComparer.Ordinal)
                            .ThenBy(s => s.Job, StringComparer.Ordinal)
                            .ThenBy(s => s.Path, StringComparer.Ordinal),
                    ]);
            })
            .OrderBy(d => d.Url, StringComparer.Ordinal),
    ];

    private static string Endpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return url;

        string port = $"{uri.Host}:{uri.Port}";
        return ScrapedEndpoints.ServesItsOwnMetrics(uri.AbsolutePath, uri.Query.Length > 0)
            ? port
            : port + uri.PathAndQuery;
    }

    private static string PathOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.PathAndQuery : url;
}
