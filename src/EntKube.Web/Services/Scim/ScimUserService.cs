using System.Text.Json.Nodes;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Scim;

/// <summary>Outcome of a SCIM operation, carrying the HTTP status the endpoint should return.</summary>
public sealed record ScimResult
{
    public required int Status { get; init; }
    public JsonNode? Body { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// SCIM's own error keyword (RFC 7644 s3.12) — <c>uniqueness</c>, <c>invalidValue</c> and so
    /// on. OPTIONAL in the spec, and worth emitting anyway: the HTTP status alone cannot tell a
    /// directory administrator whether a 400 was a malformed filter or a bad value, and what they
    /// see in the portal is whatever the connector could make of our answer.
    /// </summary>
    public string? ScimType { get; init; }

    public static ScimResult Ok(JsonNode body) => new() { Status = 200, Body = body };
    public static ScimResult Created(JsonNode body) => new() { Status = 201, Body = body };
    public static ScimResult NoContent() => new() { Status = 204 };
    public static ScimResult NotFound(string detail) => new() { Status = 404, Error = detail };

    public static ScimResult BadRequest(string detail, string? scimType = null) =>
        new() { Status = 400, Error = detail, ScimType = scimType };

    public static ScimResult Conflict(string detail, string? scimType = ScimTypes.Uniqueness) =>
        new() { Status = 409, Error = detail, ScimType = scimType };
}

/// <summary>The RFC 7644 s3.12 error keywords this service emits.</summary>
public static class ScimTypes
{
    public const string InvalidFilter = "invalidFilter";
    public const string InvalidValue = "invalidValue";
    public const string InvalidSyntax = "invalidSyntax";
    public const string Uniqueness = "uniqueness";
}

/// <summary>What one PATCH operation was understood to mean.</summary>
public enum ScimPatchAction
{
    /// <summary>An attribute this service does not store. Tolerated — see <see cref="ScimUserService.Plan"/>.</summary>
    Ignore,
    SetActive,
    SetUserName,
}

/// <summary>One understood operation, ready to execute.</summary>
public readonly record struct ScimPatchStep(ScimPatchAction Action, bool Enabled, string? UserName);

/// <summary>
/// The whole PatchOp body, understood. Either a refusal or a list of steps.
///
/// <para>⚠️ Separated from execution so the DECISIONS can be tested. PATCH is the operation a
/// directory deprovisions with, and it had no test of any kind — which is how a valueless
/// <c>replace active</c> came to default to ENABLED.</para>
/// </summary>
public readonly record struct ScimPatchPlan(
    IReadOnlyList<ScimPatchStep> Steps, string? Error, string? ScimType)
{
    public bool IsRefused => Error is not null;

    public static ScimPatchPlan Refuse(string error, string scimType) => new([], error, scimType);
    public static ScimPatchPlan Of(IReadOnlyList<ScimPatchStep> steps) => new(steps, null, null);
}

/// <summary>
/// SCIM 2.0 user provisioning, backed by ASP.NET Identity.
///
/// The reason this exists alongside just-in-time provisioning at SSO login: JIT only
/// reconciles a user when they sign in. SCIM lets the directory PUSH a change, so
/// revoking someone takes effect within the directory's sync interval rather than
/// whenever that person next happens to log in — which, for someone who has just been
/// dismissed, may be never.
///
/// Deactivation is therefore the operation that matters most here, and it is
/// implemented as an Identity lockout rather than a row delete: a deleted user loses
/// their tenant memberships, audit trail and identity, so re-enabling them later
/// silently creates a different person. Lockout blocks every sign-in path, including
/// SSO, while keeping all of that intact.
/// </summary>
public class ScimUserService(
    IDbContextFactory<IdentityDbContext> dbFactory,
    UserManager<ApplicationUser> userManager,
    ILogger<ScimUserService> logger)
{
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string ListSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";

    /// <summary>
    /// Far-future lockout is Identity's idiom for "this account is disabled". It is
    /// checked by every sign-in path, so it also blocks SSO — which is the point.
    /// </summary>
    private static readonly DateTimeOffset Disabled = DateTimeOffset.MaxValue;

    /// <summary>Renders a user as a SCIM resource.</summary>
    public static JsonObject ToScim(ApplicationUser user) => new()
    {
        ["schemas"] = new JsonArray(UserSchema),
        ["id"] = user.Id,
        ["userName"] = user.UserName,
        ["active"] = !IsDisabled(user),
        ["emails"] = new JsonArray(new JsonObject
        {
            ["value"] = user.Email,
            ["primary"] = true,
        }),
        ["meta"] = new JsonObject
        {
            ["resourceType"] = "User",
            ["location"] = $"/scim/v2/Users/{user.Id}",
        },
    };

    /// <summary>An account is disabled when a lockout is in force with no realistic end.</summary>
    public static bool IsDisabled(ApplicationUser user) =>
        user.LockoutEnd is DateTimeOffset end && end > DateTimeOffset.UtcNow.AddYears(50);

    public async Task<ScimResult> ListAsync(string? filter, int startIndex, int count, CancellationToken ct)
    {
        ScimFilterResult parsed = ScimFilter.Parse(filter);
        if (!parsed.IsSupported)
        {
            // Refused rather than ignored — see ScimFilter for why returning everything
            // when a filter could not be honoured is the dangerous option.
            return ScimResult.BadRequest(parsed.Error!, ScimTypes.InvalidFilter);
        }

        await using IdentityDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<ApplicationUser> query = db.ApplicationUsers.AsNoTracking();

        if (!parsed.IsEmpty)
        {
            query = parsed.Attribute switch
            {
                "username" => query.Where(u => u.UserName == parsed.Value),
                "id" => query.Where(u => u.Id == parsed.Value),
                // externalId is not stored separately; the userName is the stable handle
                // the directory already knows the user by.
                "externalid" => query.Where(u => u.UserName == parsed.Value),
                "active" => string.Equals(parsed.Value, "true", StringComparison.OrdinalIgnoreCase)
                    ? query.Where(u => u.LockoutEnd == null || u.LockoutEnd <= DateTimeOffset.UtcNow)
                    : query.Where(u => u.LockoutEnd != null && u.LockoutEnd > DateTimeOffset.UtcNow),
                _ => query,
            };
        }

        int total = await query.CountAsync(ct);

        // SCIM's startIndex is 1-based, unlike almost everything else. Treating it as
        // 0-based silently skips the first user on every page.
        List<ApplicationUser> page = await query
            .OrderBy(u => u.UserName)
            .Skip(Math.Max(0, startIndex - 1))
            .Take(Math.Clamp(count, 1, 200))
            .ToListAsync(ct);

        return ScimResult.Ok(new JsonObject
        {
            ["schemas"] = new JsonArray(ListSchema),
            ["totalResults"] = total,
            ["startIndex"] = startIndex,
            ["itemsPerPage"] = page.Count,
            ["Resources"] = new JsonArray([.. page.Select(u => (JsonNode)ToScim(u))]),
        });
    }

    public async Task<ScimResult> GetAsync(string id, CancellationToken ct)
    {
        await using IdentityDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ApplicationUser? user = await db.ApplicationUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

        return user is null ? ScimResult.NotFound($"No user with id '{id}'.") : ScimResult.Ok(ToScim(user));
    }

    public async Task<ScimResult> CreateAsync(JsonObject resource, CancellationToken ct)
    {
        string? userName = resource["userName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return ScimResult.BadRequest("userName is required.");
        }

        ApplicationUser? existing = await userManager.FindByNameAsync(userName);
        if (existing is not null)
        {
            // 409 is what the spec calls for, and it is also what tells a provisioning
            // client to switch to an update instead of retrying the create forever.
            return ScimResult.Conflict($"A user named '{userName}' already exists.");
        }

        string email = ReadPrimaryEmail(resource) ?? userName;

        ApplicationUser user = new()
        {
            UserName = userName,
            Email = email,
            // The directory has already authenticated this person, so requiring them to
            // confirm an email they were provisioned with would block the SSO login the
            // provisioning exists to enable.
            EmailConfirmed = true,
        };

        IdentityResult created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            return ScimResult.BadRequest(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        // A resource created with active:false is provisioned already disabled, which is
        // how some directories stage an account ahead of a start date.
        if (resource["active"] is JsonNode active && !ReadBool(active, true))
        {
            await SetEnabledAsync(user, false, ct);
        }

        logger.LogInformation("SCIM provisioned user {UserName} ({UserId})", user.UserName, user.Id);
        return ScimResult.Created(ToScim(user));
    }

    /// <summary>Full replace. Absent fields are treated as unchanged rather than cleared.</summary>
    public async Task<ScimResult> ReplaceAsync(string id, JsonObject resource, CancellationToken ct)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return ScimResult.NotFound($"No user with id '{id}'.");
        }

        if (resource["userName"]?.GetValue<string>() is string userName && !string.IsNullOrWhiteSpace(userName))
        {
            user.UserName = userName;
        }

        if (ReadPrimaryEmail(resource) is string email)
        {
            user.Email = email;
        }

        IdentityResult updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return ScimResult.BadRequest(string.Join("; ", updated.Errors.Select(e => e.Description)));
        }

        if (resource["active"] is JsonNode active)
        {
            await SetEnabledAsync(user, ReadBool(active, true), ct);
        }

        return ScimResult.Ok(ToScim(user));
    }

    /// <summary>
    /// Applies a SCIM PATCH. This is the operation directories use to deprovision —
    /// Entra in particular sends <c>replace active=false</c> rather than a DELETE — so
    /// getting it wrong means someone who was removed from the directory keeps working
    /// access indefinitely.
    /// </summary>
    public async Task<ScimResult> PatchAsync(string id, JsonObject patch, CancellationToken ct)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return ScimResult.NotFound($"No user with id '{id}'.");
        }

        ScimPatchPlan plan = Plan(patch);
        if (plan.IsRefused)
        {
            return ScimResult.BadRequest(plan.Error!, plan.ScimType);
        }

        bool changed = false;

        foreach (ScimPatchStep step in plan.Steps)
        {
            switch (step.Action)
            {
                case ScimPatchAction.SetActive:
                    await SetEnabledAsync(user, step.Enabled, ct);
                    changed = true;
                    break;

                case ScimPatchAction.SetUserName:
                    user.UserName = step.UserName;
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            // ⚠️ The result was discarded here, so a rename Identity rejected — a duplicate
            // userName, a validator — answered 200 OK with the OLD name in the body. The
            // directory recorded a successful update that never happened and would not retry.
            IdentityResult updated = await userManager.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                bool duplicate = updated.Errors.Any(
                    e => e.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));

                string detail = string.Join("; ", updated.Errors.Select(e => e.Description));

                return duplicate
                    ? ScimResult.Conflict(detail)
                    : ScimResult.BadRequest(detail, ScimTypes.InvalidValue);
            }
        }

        ApplicationUser? refreshed = await userManager.FindByIdAsync(id);
        return ScimResult.Ok(ToScim(refreshed ?? user));
    }

    /// <summary>
    /// Understands a PatchOp body without touching the database, so every decision below is
    /// testable. Returns a refusal, or the steps to apply.
    ///
    /// <para><b>What a directory actually sends.</b> Entra deprovisions with
    /// <c>replace active=false</c>, sometimes with <c>path</c>, sometimes with no path and the
    /// field inside <c>value</c>, and sometimes with the attribute fully qualified as
    /// <c>urn:ietf:params:scim:schemas:core:2.0:User:active</c>. All three mean the same thing and
    /// all three must work, because the one that silently does nothing leaves a removed employee
    /// with working access while the portal reports success.</para>
    ///
    /// <para>⚠️ <b>An attribute this service does not store is IGNORED, deliberately, and that is
    /// not the same as the bug above.</b> Entra sends PATCH for <c>name.givenName</c> and
    /// <c>displayName</c> on ordinary profile edits. Refusing those with the 400 <c>invalidPath</c>
    /// the RFC would allow makes Entra mark the user as failed and retry forever, so tolerance
    /// here is the correct behaviour rather than laziness. What is NOT tolerated is guessing a
    /// value we were not given.</para>
    /// </summary>
    public static ScimPatchPlan Plan(JsonObject patch)
    {
        if (ReadMember(patch, "Operations") is not JsonArray operations)
        {
            return ScimPatchPlan.Refuse(
                "A PatchOp requires an 'Operations' array.", ScimTypes.InvalidSyntax);
        }

        List<ScimPatchStep> steps = [];

        foreach (JsonNode? operation in operations)
        {
            if (operation is not JsonObject op)
            {
                continue;
            }

            string verb = AsString(ReadMember(op, "op"))?.Trim().ToLowerInvariant() ?? "";
            string path = NormalisePatchPath(AsString(ReadMember(op, "path")));
            JsonNode? value = ReadMember(op, "value");

            if (verb is not ("replace" or "add"))
            {
                // "remove" on a single-valued user attribute has no meaning here.
                continue;
            }

            if (path == "active")
            {
                // ⚠️ No value, no guess. This read `ReadBool(value, true)`, so a
                // `replace active` that arrived without a value ENABLED the account — turning a
                // deprovision into a reactivation. Defaulting the other way would be just as
                // wrong, deactivating someone the directory never asked to remove, so the only
                // safe answer is to refuse. RFC 7644 s3.5.2 requires a value for replace anyway.
                if (value is null)
                {
                    return ScimPatchPlan.Refuse(
                        "A replace of 'active' requires a value.", ScimTypes.InvalidValue);
                }

                steps.Add(new ScimPatchStep(ScimPatchAction.SetActive, ReadBool(value, true), null));
                continue;
            }

            if (path == "username")
            {
                string? userName = AsString(value);
                if (string.IsNullOrWhiteSpace(userName))
                {
                    return ScimPatchPlan.Refuse(
                        "A replace of 'userName' requires a string value.", ScimTypes.InvalidValue);
                }

                steps.Add(new ScimPatchStep(ScimPatchAction.SetUserName, false, userName));
                continue;
            }

            // No path: the value object carries the fields, e.g. {"value":{"active":false}}
            if (path.Length == 0 && value is JsonObject fields)
            {
                foreach (KeyValuePair<string, JsonNode?> field in fields)
                {
                    if (string.Equals(field.Key, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        if (field.Value is null)
                        {
                            return ScimPatchPlan.Refuse(
                                "A replace of 'active' requires a value.", ScimTypes.InvalidValue);
                        }

                        steps.Add(new ScimPatchStep(
                            ScimPatchAction.SetActive, ReadBool(field.Value, true), null));
                    }
                    else if (string.Equals(field.Key, "userName", StringComparison.OrdinalIgnoreCase))
                    {
                        string? userName = AsString(field.Value);
                        if (string.IsNullOrWhiteSpace(userName))
                        {
                            return ScimPatchPlan.Refuse(
                                "A replace of 'userName' requires a string value.",
                                ScimTypes.InvalidValue);
                        }

                        steps.Add(new ScimPatchStep(ScimPatchAction.SetUserName, false, userName));
                    }
                }
            }
        }

        return ScimPatchPlan.Of(steps);
    }

    /// <summary>
    /// A PATCH path, reduced to the bare attribute name.
    ///
    /// <para>⚠️ A fully qualified <c>urn:ietf:params:scim:schemas:core:2.0:User:active</c> was not
    /// recognised, so that form of deprovision matched nothing and answered 200 OK. Only the CORE
    /// user prefix is stripped: doing the same to the enterprise extension URN would map
    /// <c>...:enterprise:2.0:User:manager</c> onto a core attribute name and conflate two
    /// different attributes.</para>
    /// </summary>
    public static string NormalisePatchPath(string? raw)
    {
        string path = raw?.Trim().ToLowerInvariant() ?? "";
        const string corePrefix = "urn:ietf:params:scim:schemas:core:2.0:user:";

        return path.StartsWith(corePrefix, StringComparison.Ordinal)
            ? path[corePrefix.Length..]
            : path;
    }

    /// <summary>
    /// Reads a member by name, ignoring case.
    ///
    /// <para>⚠️ SCIM attribute names are case-insensitive (RFC 7643 s2.1), and this file already
    /// went to trouble over that for paths and field keys — but <c>patch["Operations"]</c> and
    /// <c>op["op"]</c> were exact lookups, so a connector sending <c>operations</c> got
    /// "A PatchOp requires an 'Operations' array" and could never deprovision anyone.</para>
    /// </summary>
    private static JsonNode? ReadMember(JsonObject obj, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return member.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// A string, or null if the node is not one.
    ///
    /// <para>⚠️ <c>GetValue&lt;string&gt;()</c> THROWS on a JSON number or boolean, and the
    /// exception escaped the handler — so a malformed <c>"op": 1</c> produced the application's
    /// HTML error page rather than a SCIM error envelope, which is the one thing
    /// <c>Respond</c> exists to prevent.</para>
    /// </summary>
    private static string? AsString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// DELETE deactivates rather than destroys.
    ///
    /// Removing the row would take the user's tenant memberships, audit trail and
    /// identity with it, so a later re-provision would silently create a different
    /// person wearing the same name — and any record of what the original did would be
    /// gone. Deactivation achieves the thing deprovisioning is for (no more access)
    /// without destroying the thing an audit needs.
    /// </summary>
    public async Task<ScimResult> DeleteAsync(string id, CancellationToken ct)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return ScimResult.NotFound($"No user with id '{id}'.");
        }

        await SetEnabledAsync(user, false, ct);
        logger.LogInformation("SCIM deactivated user {UserName} ({UserId})", user.UserName, user.Id);

        return ScimResult.NoContent();
    }

    /// <summary>
    /// Enables or disables sign-in. Disabling also stamps the security stamp, which
    /// invalidates any session the user already holds — otherwise a deprovisioned user
    /// keeps working until their existing cookie happens to expire.
    /// </summary>
    private async Task SetEnabledAsync(ApplicationUser user, bool enabled, CancellationToken ct)
    {
        user.LockoutEnabled = true;
        user.LockoutEnd = enabled ? null : Disabled;

        await userManager.UpdateAsync(user);

        if (!enabled)
        {
            await userManager.UpdateSecurityStampAsync(user);
        }
    }

    private static string? ReadPrimaryEmail(JsonObject resource)
    {
        if (resource["emails"] is not JsonArray emails)
        {
            return null;
        }

        // Prefer the one marked primary; fall back to the first with a value, since
        // plenty of directories send a single unmarked entry.
        foreach (JsonNode? entry in emails)
        {
            if (entry is JsonObject email && email["primary"] is JsonNode p && ReadBool(p, false))
            {
                return email["value"]?.GetValue<string>();
            }
        }

        return (emails.FirstOrDefault() as JsonObject)?["value"]?.GetValue<string>();
    }

    /// <summary>
    /// Reads a boolean that may arrive as a JSON boolean or as a string.
    /// Entra sends <c>"False"</c> as a string in some connector versions, and reading
    /// that as anything other than false would leave a deprovisioned user active.
    /// </summary>
    public static bool ReadBool(JsonNode? node, bool fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (Exception)
        {
            string? text = node.ToString();
            return bool.TryParse(text, out bool parsed) ? parsed : fallback;
        }
    }
}
