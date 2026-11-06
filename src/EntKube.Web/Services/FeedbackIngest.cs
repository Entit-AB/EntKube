using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>The bounds on one submission. The model's column lengths come from here too.</summary>
public static class FeedbackLimits
{
    public const int MaxMessageLength = 4000;
    public const int MaxEmailLength = 254;
    public const int MaxPathLength = 1024;
    public const int MaxSessionIdLength = 64;
    public const int MaxErrors = 5;
    public const int MaxErrorLength = 300;
    public const int MaxErrorsLength = (MaxErrorLength + 1) * MaxErrors;
    public const int MaxScreenshotBytes = 3 * 1024 * 1024;

    /// <summary>Base64 is 4/3 of the image, plus the rest of the fields.</summary>
    public const int MaxRequestBytes = (MaxScreenshotBytes / 3 * 4) + (64 * 1024);

    /// <summary>A person does not send twenty reports a minute, and each can carry 3 MiB.</summary>
    public const int MaxRequestsPerIpPerMinute = 20;
}

/// <summary>A submission that has passed <see cref="FeedbackIngest.Parse"/>, so every field is bounded.</summary>
public sealed record FeedbackSubmission(
    FeedbackKind Kind,
    int? Rating,
    string Message,
    string? ContactEmail,
    string PagePath,
    string? Browser,
    string? Os,
    string? Device,
    int? ViewportWidth,
    int? ViewportHeight,
    string? RumSessionId,
    string? RecentErrors,
    byte[]? Screenshot,
    string? ScreenshotContentType);

/// <summary>
/// Front half of the public feedback endpoint: the same guards as <see cref="RumIngest"/>, in the
/// same order, then a parse that turns an anonymous document into bounded fields.
///
/// <para>The Origin allow-list keeps other people's <em>web pages</em> out, and nothing else: a
/// script sets any header it likes. What bounds a script is the per-IP limit and the per-site daily
/// quota, which is why that quota exists here although RUM has none — a RUM beacon lands in
/// self-expiring segments, whereas this writes rows and images into the operational database.</para>
/// </summary>
public static partial class FeedbackIngest
{
    public sealed record Result(IResult? Error, FeedbackSubmission? Submission, RumSiteInfo? Site, string? AllowOrigin);

    public static async Task<Result> ReadAsync(
        HttpContext ctx, string publicKey, RumSiteService sites, FeedbackService feedback,
        IngestRateLimiter rateLimiter, ILogger logger, CancellationToken ct)
    {
        // Before the key is resolved, so a fresh-key-per-request spray cannot cost a lookup each.
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!rateLimiter.TryAcquire("feedback-ip:" + ip, FeedbackLimits.MaxRequestsPerIpPerMinute))
            return Fail(StatusCodes.Status429TooManyRequests, null);

        RumSiteInfo? site = await sites.ResolveAsync(publicKey, ct);
        if (site is null || !site.IsEnabled)
            return new Result(Results.Unauthorized(), null, null, null);

        string? origin = ctx.Request.Headers.Origin;
        if (!RumIngest.OriginAllowed(site.Origins, origin))
            return Fail(StatusCodes.Status403Forbidden, null);

        // After the origin check, so the answer only ever reaches a page the site allows.
        string? allowOrigin = origin;

        if (!site.FeedbackEnabled)
            return Fail(StatusCodes.Status403Forbidden, allowOrigin);

        if (!rateLimiter.TryAcquire(site.SiteId) || !await feedback.WithinDailyQuotaAsync(site.SiteId, ct))
            return Fail(StatusCodes.Status429TooManyRequests, allowOrigin);

        try
        {
            byte[]? body = await ReadBoundedAsync(ctx.Request.Body, FeedbackLimits.MaxRequestBytes, ct);
            if (body is null)
                return Fail(StatusCodes.Status413PayloadTooLarge, allowOrigin);

            using JsonDocument doc = JsonDocument.Parse(body);
            FeedbackSubmission? submission = Parse(doc.RootElement);

            return submission is null
                ? Fail(StatusCodes.Status400BadRequest, allowOrigin)
                : new Result(null, submission, site, allowOrigin);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Unparseable feedback submission.");
            return Fail(StatusCodes.Status400BadRequest, allowOrigin);
        }
    }

    /// <summary>
    /// Turns a document into a submission, or null when it says nothing or names an unknown kind.
    /// Oversized optional fields are cut or dropped rather than refused: a person who has just
    /// written a bug report should not lose it because their browser sent a long path.
    /// </summary>
    public static FeedbackSubmission? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!Enum.TryParse(Text(root, "kind"), ignoreCase: true, out FeedbackKind kind)
            || !Enum.IsDefined(kind)
            // TryParse accepts "2"; the widget only ever sends names.
            || int.TryParse(Text(root, "kind"), out _))
        {
            return null;
        }

        int? rating = Int(root, "rating") is >= 1 and <= 5 and int r ? r : null;
        string message = Cut(Text(root, "message")?.Trim(), FeedbackLimits.MaxMessageLength) ?? "";

        if (message.Length == 0 && rating is null) return null;

        (byte[]? image, string? contentType) = DecodeScreenshot(Text(root, "screenshot"));

        // A screenshot that was sent but did not survive is dropped; the words still arrive.
        return new FeedbackSubmission(
            kind, rating, message,
            Email(Text(root, "email")),
            PathOnly(Text(root, "path")),
            Cut(Text(root, "browser"), 40),
            Cut(Text(root, "os"), 40),
            Cut(Text(root, "device"), 40),
            Dimension(Int(root, "viewportWidth")),
            Dimension(Int(root, "viewportHeight")),
            SessionId(Text(root, "sessionId")),
            Errors(root),
            image,
            contentType);
    }

    /// <summary>
    /// Reduces a client-supplied location to its path. The widget sends only the path already; this
    /// is what makes that true of a caller who is not the widget, because a query string is where
    /// reset tokens and session ids live and this field ends up in tickets.
    /// </summary>
    public static string PathOnly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        string p = path.Trim();

        if (Uri.TryCreate(p, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            p = absolute.AbsolutePath;
        }

        int cut = p.IndexOfAny(['?', '#']);
        if (cut >= 0) p = p[..cut];

        return Cut(p, FeedbackLimits.MaxPathLength) is { Length: > 0 } kept ? kept : "/";
    }

    /// <summary>
    /// Decodes a data URL into an image, deciding the type from the bytes. The claimed type is
    /// ignored, because this ends up in an <c>img</c> in an operator's session: SVG is script, so
    /// the allow-list is the two formats the widget produces and nothing else.
    /// </summary>
    public static (byte[]? Bytes, string? ContentType) DecodeScreenshot(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return (null, null);

        int comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        int encoded = dataUrl.Length - comma - 1;
        byte[] buffer = new byte[(encoded / 4 * 3) + 3];
        if (!Convert.TryFromBase64Chars(dataUrl.AsSpan(comma + 1), buffer, out int written)
            || written > FeedbackLimits.MaxScreenshotBytes)
        {
            return (null, null);
        }

        byte[] bytes = buffer[..written];
        string? type = SniffImage(bytes);
        return type is null ? (null, null) : (bytes, type);
    }

    /// <summary>PNG or JPEG by magic number, else null.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF])) return "image/jpeg";
        return null;
    }

    private static string? Email(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > FeedbackLimits.MaxEmailLength) return null;

        // Bare addresses only. MailAddress also accepts "Name <a@b>", which is a header-shaped
        // value nobody should be handed later.
        return MailAddress.TryCreate(trimmed, out MailAddress? parsed) && parsed.Address == trimmed ? trimmed : null;
    }

    private static string? SessionId(string? value) =>
        value is { Length: > 0 and <= FeedbackLimits.MaxSessionIdLength } && SessionIdShape().IsMatch(value)
            ? value
            : null;

    private static string? Errors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out JsonElement errors) || errors.ValueKind != JsonValueKind.Array)
            return null;

        string[] kept = errors.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => Cut(e.GetString()!.ReplaceLineEndings(" ").Trim(), FeedbackLimits.MaxErrorLength) ?? "")
            .Where(e => e.Length > 0)
            .Take(FeedbackLimits.MaxErrors)
            .ToArray();

        return kept.Length == 0 ? null : string.Join('\n', kept);
    }

    private static int? Dimension(int? value) => value is > 0 and <= 100_000 ? value : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int i)
            ? i
            : null;

    private static string? Cut(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>Reads at most <paramref name="max"/> bytes; null if the body is longer.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int max, CancellationToken ct)
    {
        await using MemoryStream buffered = new();
        byte[] chunk = new byte[16384];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffered.Length + read > max) return null;
            buffered.Write(chunk, 0, read);
        }
        return buffered.ToArray();
    }

    private static Result Fail(int status, string? allowOrigin) =>
        new(Results.StatusCode(status), null, null, allowOrigin);

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SessionIdShape();
}
