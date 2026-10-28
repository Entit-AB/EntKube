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
    /// <summary>
    /// The scope this filter enforces, or null when any live token may call the route.
    ///
    /// <para>Exposed so <see cref="ApiTokenAuthExtensions.RequireApiScope"/> can build the
    /// describing metadata FROM the enforcing filter instead of from a second copy of the same
    /// argument. Enforcement stays here — reading the scope back out of the metadata at request
    /// time would fail open, turning a missing <see cref="ApiScopeMetadata"/> into "any token
    /// will do" rather than a refusal.</para>
    /// </summary>
    public string? RequiredScope => requiredScope;

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

/// <summary>
/// The scope a route requires, as endpoint metadata so the OpenAPI document can state it.
///
/// <para>⚠️ This is a DESCRIPTION, never the enforcement. <see cref="ApiTokenAuthFilter"/> is what
/// refuses the request; this only lets the document say so. The two cannot disagree, because
/// <see cref="ApiTokenAuthExtensions.RequireApiScope"/> builds this from the filter it just
/// constructed — and <c>GeneratedOpenApiDocumentTests</c> generates the real document and compares
/// it, operation by operation, against the routes the router actually serves.</para>
/// </summary>
/// <param name="Scope">The required scope, or null when any live token may call the route.</param>
public sealed record ApiScopeMetadata(string? Scope);

public static class ApiTokenAuthExtensions
{
    /// <summary>
    /// Requires a valid API token, and optionally a specific scope. A route with no scope
    /// argument still requires authentication — it is readable by any live token.
    /// </summary>
    /// <remarks>
    /// Generic over the builder because <c>MapOpenApi</c> returns an
    /// <see cref="IEndpointConventionBuilder"/> rather than a <see cref="RouteHandlerBuilder"/>,
    /// and the document deserves the same guard as the routes it describes. Route call sites are
    /// unchanged — they still get their own builder type back and still chain.
    /// </remarks>
    public static TBuilder RequireApiScope<TBuilder>(
        this TBuilder builder, string? scope = null)
        where TBuilder : IEndpointConventionBuilder
    {
        // ⚠️ The same fact in two places, but derived ONCE. The filter ENFORCES the scope; the
        // metadata DESCRIBES it, and nothing outside the filter could see which scope a route
        // needed — it lived in a filter instance's field — so the generated OpenAPI document,
        // and any client built from it, could not tell an operator that `cost-rates` needs
        // `config:write` until the call came back 403.
        //
        // The metadata reads the scope back off the filter rather than taking `scope` a second
        // time, so the describing copy cannot drift from the enforcing one: there is no edit to
        // this method that changes one and leaves the other.
        ApiTokenAuthFilter filter = new(scope);

        return builder
            .AddEndpointFilter(filter)
            .WithMetadata(new ApiScopeMetadata(filter.RequiredScope));
    }

    /// <summary>The authenticated API principal. Non-null inside any route guarded by the filter.</summary>
    public static ApiTokenPrincipal? GetApiPrincipal(this HttpContext http) => ApiTokenAuthFilter.Get(http);
}
