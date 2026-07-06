using System.Text.Json;

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

    public static async Task<Result> ReadAsync(
        HttpContext ctx, TelemetryStore telemetry, IngestTokenService tokens, IngestRateLimiter rateLimiter,
        ILogger logger, CancellationToken ct)
    {
        if (!telemetry.IsEnabled)
            return new Result(Results.StatusCode(StatusCodes.Status503ServiceUnavailable), null, default, default);

        string? token = IngestTokenService.ExtractToken(ctx.Request);
        if (!tokens.TryValidate(token, out Guid tenantId, out Guid clusterId))
            return new Result(Results.Unauthorized(), null, default, default);

        // Per-cluster backpressure: 429 (retryable) so a flood can't overwhelm the shared store.
        if (!rateLimiter.TryAcquire(clusterId))
            return new Result(Results.StatusCode(StatusCodes.Status429TooManyRequests), null, default, default);

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
