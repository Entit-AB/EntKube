using System.Security.Cryptography;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Front-half of the public RUM ingest endpoint: resolves the site from its public key, enforces the
/// per-site Origin allow-list, applies the shared rate limiter and per-site sampling, then decompresses
/// (bounded) and parses the JSON beacon — leaving the handler to just "parse records + write". The public
/// key is NOT a secret (it ships in the browser); the origin allow-list + rate limit + sampling are what
/// bound abuse. Returns an error <see cref="IResult"/> to short-circuit on, a null <c>Doc</c> when the batch
/// was sampled out (handler should 204), or a parsed document plus the site-bound tenant/site identity.
/// </summary>
public static class RumIngest
{
    // Browser beacons are small; well below the OTLP cap. Bounds the DECOMPRESSED payload (zip-bomb guard).
    public const int MaxDecompressedBytes = 2 * 1024 * 1024;

    // Always-on per-IP backstop for this public, secretless endpoint — independent of the config-driven
    // per-site limiter (which defaults to unlimited), so an unknown-key spray is throttled before it can
    // cost a DB lookup, and no single client can flood the shared store even when the operator sets no limit.
    public const int MaxRequestsPerIpPerMinute = 1200;

    public sealed record Result(IResult? Error, JsonDocument? Doc, Guid TenantId, Guid SiteId, string? AllowOrigin);

    /// <summary>Whether a request Origin passes a site's allow-list, normalizing a trailing slash and case
    /// on both sides. An empty allow-list means unrestricted (documented on <see cref="Data.RumSite"/>).</summary>
    public static bool OriginAllowed(IReadOnlyList<string> allowed, string? origin)
    {
        if (allowed.Count == 0) return true;
        if (string.IsNullOrEmpty(origin)) return false;
        string o = origin.TrimEnd('/');
        return allowed.Any(a => string.Equals(a.TrimEnd('/'), o, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<Result> ReadAsync(
        HttpContext ctx, string publicKey, TelemetryStore telemetry, RumSiteService sites,
        IngestRateLimiter rateLimiter, ILogger logger, CancellationToken ct)
    {
        if (!telemetry.IsEnabled)
            return Fail(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        // Per-IP throttle FIRST, before the key is resolved, so a fresh-key-per-request spray can't cost a
        // DB lookup + cache entry each time.
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!rateLimiter.TryAcquire("rum-ip:" + ip, MaxRequestsPerIpPerMinute))
            return Fail(Results.StatusCode(StatusCodes.Status429TooManyRequests));

        RumSiteInfo? site = await sites.ResolveAsync(publicKey, ct);
        if (site is null || !site.IsEnabled)
            return Fail(Results.Unauthorized());

        // Origin allow-list — the primary abuse guard for a public, secretless endpoint.
        string? origin = ctx.Request.Headers.Origin;
        if (!OriginAllowed(site.Origins, origin))
            return Fail(Results.StatusCode(StatusCodes.Status403Forbidden));

        string? allowOrigin = origin;   // echoed back as Access-Control-Allow-Origin

        // Per-site backpressure (429, retryable) so one site can't flood the shared store.
        if (!rateLimiter.TryAcquire(site.SiteId))
            return new Result(Results.StatusCode(StatusCodes.Status429TooManyRequests), null, default, default, allowOrigin);

        // Server-side sampling: accept-but-drop with probability (1 - SampleRate).
        if (site.SampleRate < 1.0 && RandomNextDouble() >= site.SampleRate)
            return new Result(null, null, site.TenantId, site.SiteId, allowOrigin);   // sampled out → 204

        Stream body = ctx.Request.Body;
        System.IO.Compression.GZipStream? gz = null;
        if (ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            gz = new System.IO.Compression.GZipStream(body, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            body = gz;
        }

        try
        {
            await using MemoryStream buffered = new();
            byte[] rent = System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                int read;
                while ((read = await body.ReadAsync(rent.AsMemory(), ct)) > 0)
                {
                    if (buffered.Length + read > MaxDecompressedBytes)
                        return new Result(Results.StatusCode(StatusCodes.Status413PayloadTooLarge), null, default, default, allowOrigin);
                    buffered.Write(rent, 0, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rent);
            }

            buffered.Position = 0;
            JsonDocument doc = await JsonDocument.ParseAsync(buffered, default, ct);
            return new Result(null, doc, site.TenantId, site.SiteId, allowOrigin);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read/parse RUM payload.");
            return new Result(Results.BadRequest(), null, default, default, allowOrigin);
        }
        finally
        {
            if (gz is not null) await gz.DisposeAsync();
        }
    }

    private static Result Fail(IResult error) => new(error, null, default, default, null);

    private static double RandomNextDouble() => RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0;
}
