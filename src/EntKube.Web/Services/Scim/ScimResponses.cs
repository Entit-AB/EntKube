using System.Text.Json.Serialization;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Scim;

/// <summary>
/// The SCIM bodies as types, so the generated document describes them and a generated client
/// returns <c>ScimUserResponse</c> rather than <c>object</c>.
///
/// <para>⚠️ <b>These were hand-built <see cref="System.Text.Json.Nodes.JsonObject"/>s, and the
/// reason they were left until last is that transcribing them breaks things in ways that answer
/// <c>200 OK</c>.</b> Each hazard below is carried by an attribute rather than by a convention,
/// because a convention is what a later reader changes without seeing what it was holding:</para>
/// <list type="bullet">
///   <item><description><b>capital-R <c>Resources</c></b> — RFC 7644 §3.4.2 names it with a
///   capital R, the one member in the specification that is not lowerCamelCase. Under
///   <c>JsonSerializerDefaults.Web</c> a property <c>Resources</c> serialises as
///   <c>resources</c>, every connector reads an empty page, and Entra concludes the directory
///   contains nobody and starts creating duplicates of people who already exist. Nothing fails.
///   Hence <c>[JsonPropertyName]</c>;</description></item>
///   <item><description><b><c>status</c> is a STRING</b> — RFC 7644 §3.12 is explicit, and an
///   <c>int Status</c> is the obvious transcription. Hence the member's type;</description></item>
///   <item><description><b><c>scimType</c> must be ABSENT</b>, not null, when there is nothing to
///   say. A record emits <c>"scimType": null</c> by default, and a connector that treats a
///   present-but-null attribute as "the directory cleared this" acts on it. Hence
///   <c>WhenWritingNull</c>;</description></item>
///   <item><description><b>every member declared is a member emitted.</b> There is no
///   <c>externalId</c>, <c>name</c> or <c>displayName</c> here even though SCIM defines them,
///   because adding one "for completeness" ships it as null and means "cleared"
///   upstream.</description></item>
/// </list>
///
/// <para>The media type is the fifth hazard and cannot be solved here: <c>TypedResults.Ok</c>
/// would answer <c>application/json</c>, which some connectors reject outright. See
/// <see cref="ScimOk{TBody}"/>.</para>
/// </summary>
public sealed record ScimUserResponse(
    IReadOnlyList<string> Schemas,
    string Id,
    string? UserName,
    bool Active,
    IReadOnlyList<ScimEmailResponse> Emails,
    ScimMetaResponse Meta)
{
    public static ScimUserResponse From(ApplicationUser user) => new(
        Schemas: [ScimUserService.UserSchema],
        Id: user.Id,
        UserName: user.UserName,
        // ⚠️ By ABSENCE of a lockout, not by a stored flag. `active` is the member a directory
        // deprovisions with, and inverting it leaves every disabled account signing in.
        Active: !ScimUserService.IsDisabled(user),
        Emails: [new ScimEmailResponse(Value: user.Email, Primary: true)],
        Meta: new ScimMetaResponse(
            ResourceType: "User", Location: $"/scim/v2/Users/{user.Id}"));
}

/// <summary>One address. <c>primary</c> is always true because only one is ever emitted.</summary>
public sealed record ScimEmailResponse(string? Value, bool Primary);

/// <summary>Where the resource lives. A connector follows <c>location</c> on a later read.</summary>
public sealed record ScimMetaResponse(string ResourceType, string Location);

/// <summary>
/// The ListResponse envelope (RFC 7644 §3.4.2).
///
/// <para>⚠️ <c>Resources</c> keeps its capital R through <c>[JsonPropertyName]</c>. Removing that
/// attribute is a one-word edit that answers 200 OK with an empty page forever — see
/// <see cref="ScimUserResponse"/>.</para>
///
/// <para>⚠️ Three adjacent <c>int</c>s, so every factory call names its arguments: a positional
/// transposition of <c>totalResults</c> and <c>startIndex</c> compiles, and a connector paging on
/// the result walks off the end of the directory.</para>
/// </summary>
public sealed record ScimListResponse(
    IReadOnlyList<string> Schemas,
    int TotalResults,
    int StartIndex,
    int ItemsPerPage,
    [property: JsonPropertyName("Resources")]
    IReadOnlyList<ScimUserResponse> Resources);

/// <summary>
/// SCIM's error envelope (RFC 7644 §3.12) — the one body a connector parses on every failure, and
/// what a directory administrator is shown instead of a generic error.
///
/// <para>⚠️ <c>Status</c> is a <c>string</c> and <c>ScimType</c> is omitted when null. Both are
/// load-bearing; see <see cref="ScimUserResponse"/>.</para>
/// </summary>
public sealed record ScimErrorResponse(
    IReadOnlyList<string> Schemas,
    string Status,
    string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScimType);

/// <summary>
/// The discovery document a connector reads ONCE and then obeys.
///
/// <para>⚠️ Every capability is a nested object carrying <c>supported</c>, never a bare boolean.
/// Flattening <c>patch</c> to <c>true</c> is the obvious simplification and produces a document no
/// connector can read.</para>
///
/// <para>⚠️ <c>patch.supported</c> must stay true: Entra deprovisions with
/// <c>PATCH replace active=false</c>, and a connector told patch is unsupported never sends it —
/// so flipping that flag disables deprovisioning across every directory at once, with every
/// endpoint still working and nothing failing anywhere.</para>
/// </summary>
public sealed record ScimServiceProviderConfigResponse(
    IReadOnlyList<string> Schemas,
    ScimSupportedResponse Patch,
    ScimBulkResponse Bulk,
    ScimFilterSupportResponse Filter,
    ScimSupportedResponse ChangePassword,
    ScimSupportedResponse Sort,
    ScimSupportedResponse Etag,
    IReadOnlyList<ScimAuthenticationSchemeResponse> AuthenticationSchemes);

public sealed record ScimSupportedResponse(bool Supported);

public sealed record ScimBulkResponse(bool Supported, int MaxOperations);

/// <summary>
/// ⚠️ <c>MaxResults</c> must match the clamp <c>ScimUserService.ListAsync</c> applies, or a
/// connector pages past the end of what the server will ever return.
/// </summary>
public sealed record ScimFilterSupportResponse(bool Supported, int MaxResults);

public sealed record ScimAuthenticationSchemeResponse(string Type, string Name, string Description);
