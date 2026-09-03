using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Scim;

/// <summary>Outcome of a SCIM operation, carrying the HTTP status the endpoint should return.</summary>
public sealed record ScimResult
{
    public required int Status { get; init; }
    public JsonNode? Body { get; init; }
    public string? Error { get; init; }

    public static ScimResult Ok(JsonNode body) => new() { Status = 200, Body = body };
    public static ScimResult Created(JsonNode body) => new() { Status = 201, Body = body };
    public static ScimResult NoContent() => new() { Status = 204 };
    public static ScimResult NotFound(string detail) => new() { Status = 404, Error = detail };
    public static ScimResult BadRequest(string detail) => new() { Status = 400, Error = detail };
    public static ScimResult Conflict(string detail) => new() { Status = 409, Error = detail };
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
    IDbContextFactory<ApplicationDbContext> dbFactory,
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
            return ScimResult.BadRequest(parsed.Error!);
        }

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<ApplicationUser> query = db.Users.AsNoTracking();

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
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ApplicationUser? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

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

        if (patch["Operations"] is not JsonArray operations)
        {
            return ScimResult.BadRequest("A PatchOp requires an 'Operations' array.");
        }

        bool changed = false;

        foreach (JsonNode? operation in operations)
        {
            if (operation is not JsonObject op)
            {
                continue;
            }

            string verb = op["op"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
            // Directories disagree on capitalisation ("active" vs "Active") and some send
            // no path at all, putting the field inside the value object instead.
            string path = op["path"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
            JsonNode? value = op["value"];

            if (verb is not ("replace" or "add"))
            {
                // "remove" on a user attribute has no meaning here, and silently accepting
                // it would report success for something that did not happen.
                continue;
            }

            if (path == "active")
            {
                await SetEnabledAsync(user, ReadBool(value, true), ct);
                changed = true;
                continue;
            }

            if (path == "username" && value is not null)
            {
                user.UserName = value.GetValue<string>();
                changed = true;
                continue;
            }

            // No path: the value object carries the fields, e.g. {"value":{"active":false}}
            if (path.Length == 0 && value is JsonObject fields)
            {
                foreach (KeyValuePair<string, JsonNode?> field in fields)
                {
                    if (string.Equals(field.Key, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        await SetEnabledAsync(user, ReadBool(field.Value, true), ct);
                        changed = true;
                    }
                    else if (string.Equals(field.Key, "userName", StringComparison.OrdinalIgnoreCase)
                             && field.Value is not null)
                    {
                        user.UserName = field.Value.GetValue<string>();
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            await userManager.UpdateAsync(user);
        }

        ApplicationUser? refreshed = await userManager.FindByIdAsync(id);
        return ScimResult.Ok(ToScim(refreshed ?? user));
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
