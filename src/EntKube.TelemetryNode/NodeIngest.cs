using System.Buffers;
using System.IO.Compression;
using System.Text.Json;

namespace EntKube.TelemetryNode;

/// <summary>
/// Front-half of the node's OTLP/JSON ingest endpoints: checks the bearer token, decompresses gzip under a
/// size cap, and parses the document — leaving each handler to just "parse records + write".
///
/// This is the in-cluster twin of the management plane's <c>OtlpIngest</c>, and it is deliberately
/// simpler. There, the token had to *carry* identity, because one endpoint served every tenant and every
/// cluster on the internet. Here the pod serves exactly one cluster for one tenant, both fixed in
/// configuration, so the token only has to prove the caller is allowed to write — it cannot select whose
/// data it writes, because there is only one answer.
/// </summary>
public static class NodeIngest
{
    /// <summary>Cap on the DECOMPRESSED payload — Kestrel only bounds compressed bytes, so a gzip bomb
    /// would otherwise be decompressed in full before anything rejected it.</summary>
    public const int MaxDecompressedBytes = 64 * 1024 * 1024;

    public sealed record Result(IResult? Error, JsonDocument? Doc);

    public static async Task<Result> ReadAsync(
        HttpContext ctx, NodeOptions options, ILogger logger, CancellationToken ct)
    {
        if (!IsAuthorized(ctx, options.IngestToken))
        {
            // A collector whose token is wrong retries silently forever and simply looks like "no data",
            // so this is worth a log line even though it is a client error.
            logger.LogWarning("Rejected OTLP ingest from {Caller}: missing or invalid ingest token.",
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return new Result(Results.Unauthorized(), null);
        }

        Stream body = ctx.Request.Body;
        GZipStream? gz = null;
        // The otlphttp exporter gzip-compresses by default.
        if (ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            gz = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true);
            body = gz;
        }

        try
        {
            await using MemoryStream buffered = new();
            byte[] rent = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await body.ReadAsync(rent.AsMemory(), ct)) > 0)
                {
                    if (buffered.Length + read > MaxDecompressedBytes)
                        return new Result(Results.StatusCode(StatusCodes.Status413PayloadTooLarge), null);
                    buffered.Write(rent, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rent);
            }

            buffered.Position = 0;
            return new Result(null, await JsonDocument.ParseAsync(buffered, default, ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read/parse OTLP payload.");
            return new Result(Results.BadRequest(), null);
        }
        finally
        {
            if (gz is not null) await gz.DisposeAsync();
        }
    }

    /// <summary>
    /// Constant-time comparison of the presented bearer token against the configured one. Ordinary string
    /// equality leaks the length of the matching prefix through timing, which is enough to recover a token
    /// byte by byte from a caller that can retry freely — and any pod on the cluster network can.
    /// </summary>
    public static bool IsAuthorized(HttpContext ctx, string expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;

        string? presented = null;
        string authorization = ctx.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = authorization["Bearer ".Length..].Trim();
        else if (ctx.Request.Headers.TryGetValue("X-EntKube-Ingest-Key", out Microsoft.Extensions.Primitives.StringValues key))
            presented = key.ToString();

        if (string.IsNullOrEmpty(presented)) return false;

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
