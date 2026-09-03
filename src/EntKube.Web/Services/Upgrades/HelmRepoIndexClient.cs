using System.Collections.Concurrent;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EntKube.Web.Services.Upgrades;

/// <summary>One published version of a chart, as listed in a repository's index.yaml.</summary>
public sealed record ChartRelease
{
    public required string Version { get; init; }

    /// <summary>Version of the application the chart packages (e.g. "v3.2.1" for Traefik 34.1.0).</summary>
    public string? AppVersion { get; init; }

    /// <summary>When the chart version was published, when the repo records it.</summary>
    public DateTime? Created { get; init; }

    /// <summary>True when the chart author marked this version deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Parsed <see cref="Version"/>, or null when it isn't valid SemVer.</summary>
    public SemVer? Parsed { get; init; }
}

/// <summary>Outcome of reading one repository index — success carries versions, failure carries a reason.</summary>
public sealed record ChartIndexResult
{
    public required bool Success { get; init; }

    /// <summary>Published versions of the requested chart, newest first. Empty on failure.</summary>
    public IReadOnlyList<ChartRelease> Releases { get; init; } = [];

    /// <summary>Operator-facing reason the index could not be read. Null on success.</summary>
    public string? Error { get; init; }

    public static ChartIndexResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Reads chart versions from a Helm repository's <c>index.yaml</c> over plain HTTPS.
///
/// Deliberately does NOT shell out to the helm binary: `helm repo add/update` mutates
/// shared on-disk repo state in $HOME, which several concurrent scans would race on,
/// and it needs no cluster access to answer "what versions exist?". A GET of the
/// index is stateless, concurrently safe, and testable without helm installed.
///
/// Indexes are cached per repository URL because one repo (e.g. the Grafana charts
/// repo) backs several catalog entries, and a fleet scan would otherwise refetch a
/// multi-megabyte index once per component per cluster.
/// </summary>
public class HelmRepoIndexClient(IHttpClientFactory httpClientFactory, ILogger<HelmRepoIndexClient> logger)
{
    /// <summary>
    /// How long a fetched index stays usable. Chart repos publish on the order of days,
    /// so an hour keeps a fleet-wide scan to one fetch per repo while still surfacing a
    /// new release the same day it lands.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(DateTime FetchedAt, IReadOnlyDictionary<string, List<ChartRelease>> Entries);

    /// <summary>
    /// Returns every published version of <paramref name="chartName"/> in the repository
    /// at <paramref name="repoUrl"/>, newest first.
    /// </summary>
    public async Task<ChartIndexResult> GetChartVersionsAsync(
        string? repoUrl, string? chartName, DateTime now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl) || string.IsNullOrWhiteSpace(chartName))
        {
            return ChartIndexResult.Failed("Component has no Helm repository or chart name.");
        }

        // Manifest components store a direct .yaml URL in the repo field; there is no index
        // to read, and treating one as a repo would 404 on every scan.
        if (repoUrl.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || repoUrl.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return ChartIndexResult.Failed("Not a Helm repository (manifest URL).");
        }

        if (repoUrl.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
        {
            return ChartIndexResult.Failed("OCI registries expose no index.yaml; version checks are unsupported.");
        }

        string key = Normalize(repoUrl);

        if (cache.TryGetValue(key, out CacheEntry? cached) && now - cached.FetchedAt < CacheTtl)
        {
            return Lookup(cached.Entries, chartName, repoUrl);
        }

        IReadOnlyDictionary<string, List<ChartRelease>>? entries;
        try
        {
            entries = await FetchIndexAsync(key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A single unreachable repo must not fail the whole fleet scan — report it as a
            // per-component "unknown" and let the other components still be checked.
            logger.LogDebug(ex, "Could not read Helm index for {RepoUrl}", key);
            return ChartIndexResult.Failed($"Could not reach repository: {ex.Message}");
        }

        if (entries is null)
        {
            return ChartIndexResult.Failed("Repository index could not be parsed.");
        }

        cache[key] = new CacheEntry(now, entries);
        return Lookup(entries, chartName, repoUrl);
    }

    private static ChartIndexResult Lookup(
        IReadOnlyDictionary<string, List<ChartRelease>> entries, string chartName, string repoUrl)
    {
        if (!entries.TryGetValue(chartName, out List<ChartRelease>? releases) || releases.Count == 0)
        {
            return ChartIndexResult.Failed($"Chart '{chartName}' is not published in {repoUrl}.");
        }

        return new ChartIndexResult { Success = true, Releases = releases };
    }

    private async Task<IReadOnlyDictionary<string, List<ChartRelease>>?> FetchIndexAsync(
        string repoUrl, CancellationToken ct)
    {
        HttpClient http = httpClientFactory.CreateClient();
        http.Timeout = RequestTimeout;

        using HttpResponseMessage response = await http.GetAsync($"{repoUrl}/index.yaml", ct);
        response.EnsureSuccessStatusCode();

        string yaml = await response.Content.ReadAsStringAsync(ct);
        return ParseIndex(yaml);
    }

    /// <summary>
    /// Parses a Helm repository index into chart name → versions, newest first.
    /// Exposed for tests so index parsing can be verified without a live repository.
    /// </summary>
    public static IReadOnlyDictionary<string, List<ChartRelease>>? ParseIndex(string yaml)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        RepoIndex? index = deserializer.Deserialize<RepoIndex>(yaml);
        if (index?.Entries is null)
        {
            return null;
        }

        Dictionary<string, List<ChartRelease>> result = new(StringComparer.Ordinal);

        foreach ((string chart, List<IndexEntry>? versions) in index.Entries)
        {
            if (versions is null)
            {
                continue;
            }

            List<ChartRelease> releases = [];
            foreach (IndexEntry entry in versions)
            {
                if (string.IsNullOrWhiteSpace(entry.Version))
                {
                    continue;
                }

                releases.Add(new ChartRelease
                {
                    Version = entry.Version,
                    AppVersion = entry.AppVersion,
                    Created = entry.Created,
                    Deprecated = entry.Deprecated,
                    Parsed = SemVer.Parse(entry.Version),
                });
            }

            // Repos are not required to publish in any order, so sort rather than trust the
            // file. Unparseable versions sink to the bottom instead of being dropped: they
            // still belong in a version list shown to an operator, they just can't be ranked.
            releases.Sort((a, b) => (a.Parsed, b.Parsed) switch
            {
                (null, null) => string.CompareOrdinal(b.Version, a.Version),
                (null, not null) => 1,
                (not null, null) => -1,
                var (x, y) => y!.CompareTo(x!),
            });

            result[chart] = releases;
        }

        return result;
    }

    /// <summary>Trims the trailing slash so "https://x/charts" and "https://x/charts/" share a cache entry.</summary>
    private static string Normalize(string repoUrl) => repoUrl.TrimEnd('/');

    private sealed class RepoIndex
    {
        public Dictionary<string, List<IndexEntry>?>? Entries { get; set; }
    }

    private sealed class IndexEntry
    {
        public string? Version { get; set; }
        public string? AppVersion { get; set; }
        public DateTime? Created { get; set; }
        public bool Deprecated { get; set; }
    }
}
