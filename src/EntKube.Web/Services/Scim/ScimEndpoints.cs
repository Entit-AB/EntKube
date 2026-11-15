using System.Text.Json.Nodes;
using EntKube.Web.Services.PublicApi;

namespace EntKube.Web.Services.Scim;

/// <summary>
/// SCIM 2.0 provisioning endpoints, for a directory connector (Entra, Okta, OneLogin)
/// to push user lifecycle into EntKube.
///
/// Authenticated by the same scoped API tokens as the rest of the API, requiring
/// <c>scim:provision</c>. That scope exists on its own because the token goes into a
/// directory connector: it needs to disable accounts and nothing else, and a connector
/// holding a token that could also sync deployments is a much worse thing to leak.
///
/// Users only. Groups are intentionally not implemented — tenant access is already
/// derived from the OIDC group claims at login, and a second, disagreeing source of
/// group membership is how people end up with access nobody can explain.
/// </summary>
public static class ScimEndpoints
{
    private const string BasePath = "/scim/v2";

    public static void MapScim(this WebApplication app)
    {
        RouteGroupBuilder scim = app.MapGroup(BasePath);

        // Capability discovery. Connectors fetch this first to learn what is supported,
        // and answering honestly here avoids them attempting operations that will fail.
        scim.MapGet("/ServiceProviderConfig",
            ScimOk<ScimServiceProviderConfigResponse> () =>
                ScimOk<ScimServiceProviderConfigResponse>.Success(ServiceProviderConfigBody()))
            .RequireApiScope("ScimGetServiceProviderConfig", ApiScopes.ScimProvision);

        scim.MapGet("/Users", async Task<ScimOk<ScimListResponse>> (
            HttpContext ctx, ScimUserService users, string? filter,
            int? startIndex, int? count, CancellationToken ct) =>
        {
            ScimResult result = await users.ListAsync(filter, startIndex ?? 1, count ?? 100, ct);
            return Ok<ScimListResponse>(result);
        }).RequireApiScope("ScimListUsers", ApiScopes.ScimProvision);

        scim.MapGet("/Users/{id}", async Task<ScimOk<ScimUserResponse>> (
            string id, ScimUserService users, CancellationToken ct) =>
            Ok<ScimUserResponse>(await users.GetAsync(id, ct)))
            .RequireApiScope("ScimGetUser", ApiScopes.ScimProvision);

        scim.MapPost("/Users", async Task<ScimCreated<ScimUserResponse>> (
            HttpContext ctx, ScimUserService users, CancellationToken ct) =>
        {
            JsonObject? body = await ReadJsonAsync(ctx, ct);
            return Created<ScimUserResponse>(body is null
                ? ScimResult.BadRequest("A JSON body is required.")
                : await users.CreateAsync(body, ct));
        }).RequireApiScope("ScimCreateUser", ApiScopes.ScimProvision);

        scim.MapPut("/Users/{id}", async Task<ScimOk<ScimUserResponse>> (
            string id, HttpContext ctx, ScimUserService users, CancellationToken ct) =>
        {
            JsonObject? body = await ReadJsonAsync(ctx, ct);
            return Ok<ScimUserResponse>(body is null
                ? ScimResult.BadRequest("A JSON body is required.")
                : await users.ReplaceAsync(id, body, ct));
        }).RequireApiScope("ScimReplaceUser", ApiScopes.ScimProvision);

        // The deprovisioning path in practice: Entra sends replace active=false here
        // rather than issuing a DELETE.
        scim.MapPatch("/Users/{id}", async Task<ScimOk<ScimUserResponse>> (
            string id, HttpContext ctx, ScimUserService users, CancellationToken ct) =>
        {
            JsonObject? body = await ReadJsonAsync(ctx, ct);
            return Ok<ScimUserResponse>(body is null
                ? ScimResult.BadRequest("A JSON body is required.")
                : await users.PatchAsync(id, body, ct));
        }).RequireApiScope("ScimPatchUser", ApiScopes.ScimProvision);

        scim.MapDelete("/Users/{id}", async Task<ScimNoContent> (
            string id, ScimUserService users, CancellationToken ct) =>
            NoContent(await users.DeleteAsync(id, ct)))
            .RequireApiScope("ScimDeleteUser", ApiScopes.ScimProvision);
    }

    /// <summary>
    /// The discovery document a connector reads once and then obeys.
    ///
    /// <para>⚠️ Extracted from the endpoint lambda deliberately. While it was inline there was no
    /// seam to call, so the one body that decides whether a directory ever sends a deprovisioning
    /// PATCH could only be pinned as SOURCE TEXT — and a source scan proves the code was written,
    /// never that it runs.</para>
    /// </summary>
    public static ScimServiceProviderConfigResponse ServiceProviderConfigBody() => new(
        Schemas: ["urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"],
        Patch: new ScimSupportedResponse(Supported: true),
        Bulk: new ScimBulkResponse(Supported: false, MaxOperations: 0),
        // Declared as supported because a single `eq` filter is, which is what connectors send;
        // unsupported forms come back as a 400 rather than being quietly ignored.
        Filter: new ScimFilterSupportResponse(Supported: true, MaxResults: 200),
        ChangePassword: new ScimSupportedResponse(Supported: false),
        Sort: new ScimSupportedResponse(Supported: false),
        Etag: new ScimSupportedResponse(Supported: false),
        AuthenticationSchemes: [new ScimAuthenticationSchemeResponse(
            Type: "oauthbearertoken",
            Name: "OAuth Bearer Token",
            Description: "An EntKube API token carrying the scim:provision scope.")]);

    private static async Task<JsonObject?> ReadJsonAsync(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ct);
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders a result, using SCIM's own error envelope for failures. A connector parses that
    /// envelope; a plain ProblemDetails body would be reported to the directory administrator as
    /// an unhelpful generic failure.
    ///
    /// <para>⚠️ Three near-identical renderers rather than one, because the DECLARED success
    /// status differs per operation and <c>PopulateMetadata</c> is static — see
    /// <see cref="ScimJsonResult"/>. Collapsing them loses the 201 on creation and the 204 on
    /// deletion from the document.</para>
    /// </summary>
    private static ScimOk<TBody> Ok<TBody>(ScimResult result) where TBody : notnull =>
        result.Error is not null
            ? ScimOk<TBody>.Failure(result.Status, ErrorBody(result))
            : ScimOk<TBody>.Success((TBody)result.Body!);

    private static ScimCreated<TBody> Created<TBody>(ScimResult result) where TBody : notnull =>
        result.Error is not null
            ? ScimCreated<TBody>.Failure(result.Status, ErrorBody(result))
            : ScimCreated<TBody>.Success((TBody)result.Body!);

    private static ScimNoContent NoContent(ScimResult result) =>
        result.Error is not null
            ? ScimNoContent.Failure(result.Status, ErrorBody(result))
            : ScimNoContent.Success();

    /// <summary>
    /// SCIM's error envelope (RFC 7644 s3.12).
    ///
    /// <para>Public so it can be asserted. It was inline in <see cref="Respond"/>, which is
    /// private, so the one thing a connector parses on every failure had no test — and
    /// <c>scimType</c> could have been added, or later dropped, without anything noticing.</para>
    ///
    /// <para><c>status</c> is a STRING, which the spec is explicit about and which is easy to
    /// "tidy" into a number.</para>
    /// </summary>
    public static ScimErrorResponse ErrorBody(ScimResult result) => new(
        Schemas: [ScimUserService.ErrorSchema],
        // ⚠️ ToString(). RFC 7644 §3.12 requires a JSON string, and the member's type is what
        // holds that now — an int here emits 404 rather than "404".
        Status: result.Status.ToString(),
        Detail: result.Error,
        // The error keyword. OPTIONAL in the spec, and emitted because the status code alone
        // cannot tell a directory administrator whether a 400 was a malformed filter or a value
        // we refused to guess at — and the portal shows them whatever the connector could make
        // of this body. ⚠️ Null here must be ABSENT on the wire, which ScimErrorResponse carries.
        ScimType: result.ScimType);
}
