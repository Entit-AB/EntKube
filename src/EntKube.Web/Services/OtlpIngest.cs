using System.Text.Json;
using EntKube.Web.Services.Telemetry;

namespace EntKube.Web.Services;

/// <summary>
/// Shared front-half of the OTLP/JSON ingest endpoints (logs and traces): validates the per-cluster
/// ingest token, decompresses gzip with a decompressed-size cap (zip-bomb guard), and parses the JSON
/// document — leaving each MapPost handler to just "parse records + write". Returns an <see cref="IResult"/>
/// error to short-circuit on, or a parsed document plus the token-bound tenant/cluster identity.
/// </summary>
public static class OtlpIngest
{
    // Far above a real OTLP batch; caps the DECOMPRESSED payload (Kestrel only bounds compressed bytes).
    public const int MaxDecompressedBytes = 64 * 1024 * 1024;

    public sealed record Result(IResult? Error, JsonDocument? Doc, Guid TenantId, Guid ClusterId);

    // A misconfigured collector retries every few seconds forever, so the rejection can't be logged per
    // request. Throttle to one line per minute per (reason, caller) — enough to make a silent 401/503 show
    // up in the app log while a flood stays a trickle.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastRejectLog = new();
    private static readonly TimeSpan RejectLogInterval = TimeSpan.FromMinutes(1);

    private static void LogRejection(ILogger logger, HttpContext ctx, string reason)
    {
        string caller = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string key = $"{reason}|{caller}";
        DateTime now = DateTime.UtcNow;

        // The caller is a (possibly forwarded) client address, so the key space is attacker-influenced.
        // Drop the whole table rather than let it grow without bound; at worst one extra line is logged.
        if (LastRejectLog.Count > 512) LastRejectLog.Clear();

        DateTime last = LastRejectLog.GetOrAdd(key, DateTime.MinValue);
        if (now - last < RejectLogInterval) return;
        LastRejectLog[key] = now;

        logger.LogWarning(
            "Rejected OTLP ingest from {Caller} on {Path}: {Reason}. Further identical rejections are "
            + "suppressed for {Interval}.", caller, ctx.Request.Path.Value, reason, RejectLogInterval);
    }

    public static async Task<Result> ReadAsync(
        HttpContext ctx, ITelemetryIngest telemetry, IngestTokenService tokens, IngestRateLimiter rateLimiter,
        ILogger logger, CancellationToken ct)
    {
        if (!telemetry.IsEnabled)
        {
            LogRejection(logger, ctx, "telemetry ingest is disabled");
            return new Result(Results.StatusCode(StatusCodes.Status503ServiceUnavailable), null, default, default);
        }

        string? token = IngestTokenService.ExtractToken(ctx.Request);
        if (!tokens.TryValidate(token, out Guid tenantId, out Guid clusterId))
        {
            // Nothing here reaches the collector's operator, and an unauthenticated push is otherwise
            // indistinguishable from no push at all — which is exactly how a placeholder token presents.
            LogRejection(logger, ctx, token is null
                ? "no ingest token presented (Authorization: Bearer or X-EntKube-Ingest-Key)"
                : "ingest token is invalid or was signed with a different key — re-copy it from the "
                  + "tenant's Logs tab into the collector's \"Ingest Token\" field");
            return new Result(Results.Unauthorized(), null, default, default);
        }

        // Per-cluster backpressure: 429 (retryable) so a flood can't overwhelm the shared store.
        if (!rateLimiter.TryAcquire(clusterId))
        {
            LogRejection(logger, ctx, $"rate limit exceeded for cluster {clusterId}");
            return new Result(Results.StatusCode(StatusCodes.Status429TooManyRequests), null, default, default);
        }

        // The otlphttp exporter gzip-compresses by default.
        Stream body = ctx.Request.Body;
        System.IO.Compression.GZipStream? gz = null;
        if (ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            gz = new System.IO.Compression.GZipStream(
                body, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            body = gz;
        }

        try
        {
            await using MemoryStream buffered = new();
            byte[] rent = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await body.ReadAsync(rent.AsMemory(), ct)) > 0)
                {
                    if (buffered.Length + read > MaxDecompressedBytes)
                        return new Result(Results.StatusCode(StatusCodes.Status413PayloadTooLarge), null, default, default);
                    buffered.Write(rent, 0, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rent);
            }

            buffered.Position = 0;
            JsonDocument doc = await JsonDocument.ParseAsync(buffered, default, ct);
            return new Result(null, doc, tenantId, clusterId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read/parse OTLP payload.");
            return new Result(Results.BadRequest(), null, default, default);
        }
        finally
        {
            if (gz is not null) await gz.DisposeAsync();
        }
    }
}
