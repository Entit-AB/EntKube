namespace EntKube.Web.Services;

/// <summary>
/// Helpers for turning a <see cref="Data.StorageLink"/> endpoint into the shape
/// Mimir and Tempo expect. Both want a bare <c>host[:port]</c> (not a URL) plus
/// an explicit "insecure" flag for plain-HTTP endpoints — unlike Loki, which
/// accepts the raw endpoint string directly.
/// </summary>
internal static class S3EndpointUtil
{
    /// <summary>
    /// Splits a storage-link endpoint into a scheme-less host and an
    /// <c>insecure</c> (plain HTTP) flag. When no endpoint is configured
    /// (e.g. plain AWS S3) it falls back to the regional AWS endpoint.
    /// </summary>
    public static (string Host, bool Insecure) Normalize(string? endpoint, string region)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return ($"s3.{region}.amazonaws.com", false);
        }

        string trimmed = endpoint.Trim();
        bool insecure = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        string host = trimmed
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        return (host, insecure);
    }
}
