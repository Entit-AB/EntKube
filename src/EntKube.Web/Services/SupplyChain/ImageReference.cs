namespace EntKube.Web.Services.SupplyChain;

/// <summary>
/// A parsed container image reference.
///
/// Parsing these correctly matters more than it looks: the rule that decides whether
/// the first path component is a registry host or part of the repository name is a
/// Docker convention, not a syntax rule. "myco/app" means Docker Hub's myco account,
/// while "myco.io/app" means the registry myco.io. Getting it wrong would make every
/// running image fail to match its registry, and the whole vulnerability join would
/// silently return nothing.
/// </summary>
public sealed record ImageReference
{
    /// <summary>Registry host, including port when present (e.g. "registry.example.com:5000").</summary>
    public required string Registry { get; init; }

    /// <summary>Repository path without the registry (e.g. "library/nginx").</summary>
    public required string Repository { get; init; }

    /// <summary>Tag, defaulting to "latest" when neither tag nor digest is given.</summary>
    public string? Tag { get; init; }

    /// <summary>Digest when the reference is pinned (e.g. "sha256:abc…").</summary>
    public string? Digest { get; init; }

    /// <summary>The original string exactly as it appeared in the pod spec.</summary>
    public required string Original { get; init; }

    /// <summary>
    /// The Harbor project — the first segment of the repository path. Harbor requires
    /// every repository to live under a project, so this is always the first segment.
    /// </summary>
    public string? Project =>
        Repository.Contains('/') ? Repository[..Repository.IndexOf('/')] : null;

    /// <summary>
    /// The repository name as Harbor's API expects it — everything after the project.
    /// Harbor nests deeper paths inside the project, so this can itself contain slashes.
    /// </summary>
    public string? HarborRepository =>
        Repository.Contains('/') ? Repository[(Repository.IndexOf('/') + 1)..] : null;

    /// <summary>Digest when pinned, otherwise the tag — what Harbor accepts as a reference.</summary>
    public string Reference => Digest ?? Tag ?? "latest";

    private const string DefaultRegistry = "docker.io";

    /// <summary>
    /// Parses an image reference, returning null only for blank input. Unrecognisable
    /// input still yields a best-effort parse rather than null: a weird image name is
    /// better shown as unmatched than dropped from the inventory entirely.
    /// </summary>
    public static ImageReference? Parse(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        string original = image.Trim();
        string work = original;

        string? digest = null;
        int at = work.IndexOf('@');
        if (at >= 0)
        {
            digest = work[(at + 1)..];
            work = work[..at];
        }

        string registry = DefaultRegistry;
        string repository = work;

        int firstSlash = work.IndexOf('/');
        if (firstSlash > 0)
        {
            string candidate = work[..firstSlash];
            // Docker's rule: the first component is a registry only if it looks like a
            // host — it contains a dot or a port colon, or is exactly "localhost".
            if (candidate.Contains('.')
                || candidate.Contains(':')
                || string.Equals(candidate, "localhost", StringComparison.Ordinal))
            {
                registry = candidate;
                repository = work[(firstSlash + 1)..];
            }
        }

        string? tag = null;
        // Look for the tag separator only in the last path segment, so a port in the
        // registry ("myreg:5000/app") is never mistaken for a tag.
        int lastSlash = repository.LastIndexOf('/');
        int colon = repository.LastIndexOf(':');
        if (colon > lastSlash)
        {
            tag = repository[(colon + 1)..];
            repository = repository[..colon];
        }

        // Docker Hub official images live under the implicit "library" namespace.
        if (registry == DefaultRegistry && !repository.Contains('/'))
        {
            repository = "library/" + repository;
        }

        return new ImageReference
        {
            Registry = registry,
            Repository = repository,
            Tag = digest is null ? (tag ?? "latest") : tag,
            Digest = digest,
            Original = original,
        };
    }

    /// <summary>
    /// True when this image came from the given registry URL. Compares host (and port)
    /// only, so an https:// registry URL matches the bare host a pod spec carries.
    /// </summary>
    public bool IsFromRegistry(string? registryUrl)
    {
        string? host = NormalizeRegistryHost(registryUrl);
        return host is not null && string.Equals(Registry, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reduces a configured registry URL ("https://reg.example.com/") to a bare host[:port].</summary>
    public static string? NormalizeRegistryHost(string? registryUrl)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            return null;
        }

        string work = registryUrl.Trim();

        if (Uri.TryCreate(work, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0)
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }

        // Not a full URL — strip any scheme-less path and trailing slash.
        int slash = work.IndexOf('/');
        return (slash > 0 ? work[..slash] : work).TrimEnd('/');
    }
}
