namespace EntKube.Web.Services;

/// <summary>
/// The widget's two anonymous routes. Like the RUM routes there is no secret, so the guards in
/// <see cref="FeedbackIngest.ReadAsync"/> are the whole of the protection: 401 (unknown or disabled
/// key) / 403 (origin, or feedback not switched on) / 429 (rate or daily quota) / 413 / 400 / 204.
/// </summary>
public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/feedback/v1/widget.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(FeedbackWidget.Js, "application/javascript; charset=utf-8");
        });

        // The widget sends text/plain, so a browser never preflights it. This answers a caller that
        // sends application/json, with the same origin check.
        app.MapMethods("/ingest/feedback/v1/{key}", ["OPTIONS"], async (
            HttpContext ctx, string key, RumSiteService sites, CancellationToken ct) =>
        {
            RumSiteInfo? site = await sites.ResolveAsync(key, ct);
            string? origin = ctx.Request.Headers.Origin;

            if (site is { IsEnabled: true, FeedbackEnabled: true } && origin is not null
                && RumIngest.OriginAllowed(site.Origins, origin))
            {
                SetCors(ctx, origin);
                ctx.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
                ctx.Response.Headers.AccessControlAllowHeaders = "content-type";
                ctx.Response.Headers.AccessControlMaxAge = "600";
            }

            return Results.NoContent();
        }).DisableAntiforgery();

        app.MapPost("/ingest/feedback/v1/{key}", async (
            HttpContext ctx, string key, RumSiteService sites, FeedbackService feedback,
            IngestRateLimiter rateLimiter, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            ILogger log = loggerFactory.CreateLogger("FeedbackIngest");

            FeedbackIngest.Result r = await FeedbackIngest.ReadAsync(ctx, key, sites, feedback, rateLimiter, log, ct);
            if (r.AllowOrigin is not null) SetCors(ctx, r.AllowOrigin);
            if (r.Error is not null) return r.Error;

            try
            {
                await feedback.SubmitAsync(r.Site!, r.Submission!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Failed to store feedback.");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.NoContent();
        }).DisableAntiforgery();
    }

    private static void SetCors(HttpContext ctx, string origin)
    {
        ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.Vary = "Origin";
    }
}
