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

    /// <summary>SCIM's own content type. Some connectors check it and reject application/json.</summary>
    private const string ScimContentType = "application/scim+json";

    public static void MapScim(this WebApplication app)
    {
        RouteGroupBuilder scim = app.MapGroup(BasePath);

        // Capability discovery. Connectors fetch this first to learn what is supported,
        // and answering honestly here avoids them attempting operations that will fail.
        scim.MapGet("/ServiceProviderConfig", () => Results.Content(
            new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
                ["patch"] = new JsonObject { ["supported"] = true },
                ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0 },
                // Declared as supported because a single `eq` filter is, which is what
                // connectors send; unsupported forms come back as a 400 rather than
                // being quietly ignored.
                ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = 200 },
                ["changePassword"] = new JsonObject { ["supported"] = false },
                ["sort"] = new JsonObject { ["supported"] = false },
                ["etag"] = new JsonObject { ["supported"] = false },
                ["authenticationSchemes"] = new JsonArray(new JsonObject
                {
                    ["type"] = "oauthbearertoken",
                    ["name"] = "OAuth Bearer Token",
                    ["description"] = "An EntKube API token carrying the scim:provision scope.",
                }),
            }.ToJsonString(), ScimContentType))
            .RequireApiScope(ApiScopes.ScimProvision);

        scim.MapGet("/Users", async (
            HttpContext ctx, ScimUserService users, string? filter,
            int? startIndex, int? count, CancellationToken ct) =>
        {
            ScimResult result = await users.ListAsync(filter, startIndex ?? 1, count ?? 100, ct);
            return Respond(result);
        }).RequireApiScope(ApiScopes.ScimProvision);

        scim.MapGet("/Users/{id}", async (string id, ScimUserService users, CancellationToken ct) =>
            Respond(await users.GetAsync(id, ct)))
            .RequireApiScope(ApiScopes.ScimProvision);

        scim.MapPost("/Users", async (HttpContext ctx, ScimUserService users, CancellationToken ct) =>
        {
            JsonObject? body = await ReadJsonAsync(ctx, ct);
            return body is null
                ? Respond(ScimResult.BadRequest("A JSON body is required."))
                : Respond(await users.CreateAsync(body, ct));
        }).RequireApiScope(ApiScopes.ScimProvision);

        scim.MapPut("/Users/{id}", async (
            string id, HttpContext ctx, ScimUserService users, CancellationToken ct) =>
        {
            JsonObject? body = await ReadJsonAsync(ctx, ct);
            return body is null
                ? Respond(ScimResult.BadRequest("A JSON body is required."))
                : Respond(await users.ReplaceAsync(id, body, ct));
        }).RequireApiScope(ApiScopes.ScimProvision);

        // The deprovisioning path in practice: Entra sends replace active=false here
        // rather than issuing a DELETE.
        scim.MapPatch("/Users/{id}", async (
            string id, HttpContext ctx, ScimUserService users, CancellationToken ct) =>
        {
            JsonObject? body = await ReadJsonAsync(ctx, ct);
            return body is null
                ? Respond(ScimResult.BadRequest("A JSON body is required."))
                : Respond(await users.PatchAsync(id, body, ct));
        }).RequireApiScope(ApiScopes.ScimProvision);

        scim.MapDelete("/Users/{id}", async (string id, ScimUserService users, CancellationToken ct) =>
            Respond(await users.DeleteAsync(id, ct)))
            .RequireApiScope(ApiScopes.ScimProvision);
    }

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
    /// Renders a result, using SCIM's own error envelope for failures. A connector
    /// parses that envelope; a plain ProblemDetails body would be reported to the
    /// directory administrator as an unhelpful generic failure.
    /// </summary>
    private static IResult Respond(ScimResult result)
    {
        if (result.Status == 204)
        {
            return Results.NoContent();
        }

        if (result.Error is not null)
        {
            JsonObject error = new()
            {
                ["schemas"] = new JsonArray(ScimUserService.ErrorSchema),
                ["status"] = result.Status.ToString(),
                ["detail"] = result.Error,
            };

            return Results.Content(error.ToJsonString(), ScimContentType, statusCode: result.Status);
        }

        return Results.Content(result.Body?.ToJsonString() ?? "{}", ScimContentType, statusCode: result.Status);
    }
}
