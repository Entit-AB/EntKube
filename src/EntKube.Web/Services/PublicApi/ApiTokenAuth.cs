namespace EntKube.Web.Services.PublicApi;

/// <summary>
/// Endpoint filter that authenticates a public API request by bearer token and
/// enforces the route's required scope.
///
/// Implemented as a filter applied per route rather than as global middleware so that
/// a route added without <c>RequireApiScope</c> simply does not exist on the API —
/// there is no path where forgetting to opt in leaves a route publicly readable.
/// </summary>
public sealed class ApiTokenAuthFilter(string? requiredScope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;

        var tokenService = http.RequestServices.GetRequiredService<ApiTokenService>();
        string? presented = ApiTokenService.ExtractToken(http.Request);

        ApiTokenPrincipal? principal = await tokenService.ValidateAsync(presented, http.RequestAborted);
        if (principal is null)
        {
            // Same response for missing, malformed, unknown, expired and revoked tokens:
            // distinguishing them tells an attacker which guesses were closer.
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Problem(
                "A valid API token is required.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (requiredScope is not null && !principal.HasScope(requiredScope))
        {
            // 403, not 404: the token is valid and the caller is known, so hiding the
            // route's existence buys nothing and makes a scope mistake hard to debug.
            return Results.Problem(
                $"This token is missing the required scope “{requiredScope}”.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[PrincipalKey] = principal;
        return await next(context);
    }

    private const string PrincipalKey = "EntKube.ApiPrincipal";

    /// <summary>The authenticated principal for the current API request.</summary>
    public static ApiTokenPrincipal? Get(HttpContext http) =>
        http.Items.TryGetValue(PrincipalKey, out object? value) ? value as ApiTokenPrincipal : null;
}

public static class ApiTokenAuthExtensions
{
    /// <summary>
    /// Requires a valid API token, and optionally a specific scope. A route with no scope
    /// argument still requires authentication — it is readable by any live token.
    /// </summary>
    public static RouteHandlerBuilder RequireApiScope(
        this RouteHandlerBuilder builder, string? scope = null) =>
        builder.AddEndpointFilter(new ApiTokenAuthFilter(scope));

    /// <summary>The authenticated API principal. Non-null inside any route guarded by the filter.</summary>
    public static ApiTokenPrincipal? GetApiPrincipal(this HttpContext http) => ApiTokenAuthFilter.Get(http);
}
