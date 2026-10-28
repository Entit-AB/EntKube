using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EntKube.Web.Services.PublicApi;

/// <summary>
/// Makes the public API describe itself.
///
/// <para><b>Why this exists.</b> Twenty-three routes were documented nowhere a machine could read,
/// and three first-party clients — <c>EntKube.ApiClient</c>, the CLI and the MCP server — are
/// hand-maintained against them. docs/decomposition.md Phase 1 item 8 wants those generated from
/// the API rather than written three times; a document is the prerequisite, not the whole job.</para>
///
/// <para>⚠️ <b>THE SCOPES ARE THE PART WORTH HAVING.</b> A list of routes is mildly useful; a list
/// of routes that says <c>cost-rates</c> needs <c>config:write</c> is the difference between an
/// operator minting the right token and finding out from a 403. The scope lived in a private field
/// of an endpoint filter, so nothing could read it — <see cref="ApiScopeMetadata"/> now records it
/// alongside, and this writes it into each operation.</para>
/// </summary>
public static class PublicApiOpenApi
{
    public const string DocumentName = "v1";

    /// <summary>
    /// The route prefixes this document describes, without a leading slash — the form
    /// <see cref="ApiDescription.RelativePath"/> uses.
    ///
    /// <para>⚠️ <b>A new token-authenticated surface must be added here.</b> Without this filter the
    /// document described every endpoint the application has an API description for: ASP.NET
    /// Identity's own <c>/Account/*</c> form posts, the OTLP and RUM ingest endpoints, the git and
    /// ticket webhooks, and admin backup/restore — 16 operations that are not this API, each of
    /// them stamped "UNAUTHENTICATED" because they are guarded by signatures, cookies or
    /// antiforgery rather than by an API scope. A generated client would have grown a
    /// <c>LogoutAsync</c>, and the warning that is supposed to flag a genuinely unguarded API
    /// route was crying wolf sixteen times.</para>
    ///
    /// <para>Filtering by path rather than by "has a scope" is deliberate: it keeps the
    /// UNAUTHENTICATED marker able to fire. Selecting on the metadata would make an API route
    /// that forgot <c>RequireApiScope</c> vanish from the document silently, which is the
    /// opposite of what that marker is for. <c>OpenApiDocumentTests</c> asserts that nothing
    /// carrying <see cref="ApiScopeMetadata"/> falls outside these prefixes.</para>
    /// </summary>
    public static readonly string[] ApiPathPrefixes = ["api/v1/", "scim/v2/"];

    /// <summary>True when a route belongs to the documented API surface.</summary>
    public static bool Describes(string? relativePath) =>
        relativePath is not null
        && ApiPathPrefixes.Any(prefix => relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers the document. Call before <c>builder.Build()</c>.</summary>
    public static IServiceCollection AddPublicApiOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description => Describes(description.RelativePath);

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "EntKube API",
                    Version = "v1",
                    Description =
                        "The management plane's public API. Every route takes a scoped API token "
                        + "as a bearer credential; the scope each one needs is stated on the "
                        + "operation. Tokens are created per tenant under the tenant's API tokens "
                        + "tab, and every call is answered in that tenant's scope only.",
                };

                // ⚠️ BOTH of these were null, and the second one is why this feature shipped
                // broken: in OpenAPI.NET v2 `SecuritySchemes` is an unmaterialised dictionary,
                // not an empty one, so indexing into it threw a NullReferenceException inside the
                // transformer. The only visible symptom was GET /api/v1/openapi.json answering a
                // bare 500 with an empty body — no stack, nothing in the response — which is
                // precisely why it was mistaken for a hosting problem rather than a bug here.
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description =
                        "A scoped EntKube API token. ⚠️ The token carries the tenant — there is no "
                        + "tenant parameter on any route, and a token cannot reach another "
                        + "tenant's rows.",
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                ApiScopeMetadata? scope = context.Description.ActionDescriptor
                    .EndpointMetadata.OfType<ApiScopeMetadata>().FirstOrDefault();

                // ⚠️ A route with no ApiScopeMetadata has not been through RequireApiScope, which
                // means it has no authentication either — ApiTokenAuth's own comment says such a
                // route "simply does not exist on the API". Saying so in the document is better
                // than omitting the operation, because an undocumented route is invisible and an
                // alarming one gets fixed.
                if (scope is null)
                {
                    operation.Description = Join(operation.Description,
                        "⚠️ UNAUTHENTICATED: this route is not guarded by RequireApiScope.");
                    return Task.CompletedTask;
                }

                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(BearerScheme)] = [],
                }];

                operation.Description = Join(operation.Description, scope.Scope is null
                    ? "Requires any live API token."
                    : $"Requires the `{scope.Scope}` scope.");

                return Task.CompletedTask;
            });
        });

    private const string BearerScheme = "apiToken";

    private static string Join(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing}\n\n{addition}";
}
