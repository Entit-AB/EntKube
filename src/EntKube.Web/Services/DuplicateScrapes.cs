namespace EntKube.Web.Services;

/// <summary>One of the scrapes of a doubled endpoint: the pool it is in, and the job label it stamps.</summary>
/// <param name="Pool">The scrape pool, which is the job's configuration.</param>
/// <param name="Job">The <c>job</c> label of the target, or null when it has none.</param>
public sealed record EndpointScrape(string Pool, string? Job)
{
    /// <summary>
    /// How a person tells it from the others. The job is said only where it differs from the pool:
    /// one ServiceMonitor that reaches a pod through two Services is one pool, and only the job
    /// (one per Service) tells its two scrapes apart.
    /// </summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Job) || Job == Pool ? Pool : $"{Pool} (job {Job})";
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
    /// Endpoints with more than one active target, by host, port and path. The scheme is not part of
    /// it: one port speaks one scheme, and the job that has it wrong still scrapes that endpoint.
    /// The query string is: a blackbox exporter serves every probe on one path, and
    /// <c>?target=a</c> and <c>?target=b</c> are two endpoints.
    /// </summary>
    public static List<DuplicateScrape> Find(IEnumerable<ScrapeTarget> targets) =>
    [
        .. targets
            .GroupBy(t => Endpoint(t.ScrapeUrl), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateScrape(
                g.First().ScrapeUrl,
                [
                    .. g.Select(t => new EndpointScrape(t.Pool, t.Labels.GetValueOrDefault("job")))
                        .OrderBy(s => s.Pool, StringComparer.Ordinal)
                        .ThenBy(s => s.Job, StringComparer.Ordinal),
                ]))
            .OrderBy(d => d.Url, StringComparer.Ordinal),
    ];

    private static string Endpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? $"{uri.Host}:{uri.Port}{uri.PathAndQuery}"
            : url;
}
