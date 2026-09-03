using System.Security.Claims;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Sso;

/// <summary>What a group reconciliation did, for logging and for the operator-facing result.</summary>
public sealed record GroupSyncResult
{
    /// <summary>Groups the identity provider asserted for this user.</summary>
    public required IReadOnlyList<string> AssertedGroups { get; init; }

    /// <summary>Tenants the user gained access to in this sync.</summary>
    public IReadOnlyList<Guid> Granted { get; init; } = [];

    /// <summary>Tenants the user lost access to because the provider no longer asserts the group.</summary>
    public IReadOnlyList<Guid> Revoked { get; init; } = [];

    /// <summary>Tenants whose role changed.</summary>
    public IReadOnlyList<Guid> Updated { get; init; } = [];

    /// <summary>True when the user ends the sync with no tenant access at all.</summary>
    public bool HasNoAccess { get; init; }
}

/// <summary>
/// Reconciles a user's tenant memberships from the groups their identity provider
/// asserts at login.
///
/// The provider is authoritative: memberships that came from a mapped group are added,
/// changed and <em>removed</em> to match what the token says. That is what makes
/// offboarding work — dropping someone from a group in the directory drops their EntKube
/// access without anyone having to remember to do it here as well.
///
/// Memberships that were granted by hand are left completely alone. Deleting an
/// operator's manually-granted access because an unrelated SSO login did not mention it
/// would be a spectacular way to lock people out of their own platform, so the two kinds
/// of grant never touch each other.
/// </summary>
public class ExternalGroupSync(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ExternalGroupSync> logger)
{
    /// <summary>
    /// Reads the group values from a principal.
    ///
    /// Pure and static so the claim-shape handling — providers emit groups as repeated
    /// claims, and some as a single JSON array — is testable without an identity provider.
    /// </summary>
    public static IReadOnlyList<string> ReadGroups(ClaimsPrincipal principal, string groupsClaim)
    {
        List<string> groups = [];

        foreach (Claim claim in principal.FindAll(groupsClaim))
        {
            string value = claim.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            // Some providers pack the groups into one claim as a JSON array rather than
            // emitting repeated claims. Accept both rather than silently reading zero groups.
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                try
                {
                    string[]? parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(value);
                    if (parsed is not null)
                    {
                        groups.AddRange(parsed.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()));
                        continue;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not JSON after all — fall through and treat it as a literal value.
                }
            }

            groups.Add(value);
        }

        return [.. groups.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reconciles the user's SSO-derived memberships against the asserted groups.
    /// </summary>
    public async Task<GroupSyncResult> SyncAsync(
        string userId, ClaimsPrincipal principal, string groupsClaim, CancellationToken ct = default)
    {
        IReadOnlyList<string> asserted = ReadGroups(principal, groupsClaim);

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<ExternalGroupMapping> mappings = await db.ExternalGroupMappings
            .AsNoTracking()
            .ToListAsync(ct);

        // Every tenant that SSO governs at all. A tenant with no mapping is outside SSO's
        // authority entirely, so this sync must not touch memberships there.
        HashSet<Guid> ssoGovernedTenants = [.. mappings.Select(m => m.TenantId)];

        // What the provider says the user should have, keyed by tenant. Where a user is in
        // two groups mapping to the same tenant, the first by group name wins — deterministic
        // rather than dependent on claim order, so the same token always yields the same access.
        Dictionary<Guid, Guid> desired = [];
        foreach (ExternalGroupMapping mapping in mappings
            .Where(m => asserted.Contains(m.ExternalGroup, StringComparer.Ordinal))
            .OrderBy(m => m.ExternalGroup, StringComparer.Ordinal))
        {
            desired.TryAdd(mapping.TenantId, mapping.RoleId);
        }

        List<TenantMembership> existing = await db.TenantMemberships
            .Where(m => m.UserId == userId)
            .ToListAsync(ct);

        List<Guid> granted = [];
        List<Guid> revoked = [];
        List<Guid> updated = [];

        foreach ((Guid tenantId, Guid roleId) in desired)
        {
            TenantMembership? current = existing.FirstOrDefault(m => m.TenantId == tenantId);
            if (current is null)
            {
                db.TenantMemberships.Add(new TenantMembership
                {
                    UserId = userId,
                    TenantId = tenantId,
                    RoleId = roleId,
                    JoinedAt = DateTime.UtcNow,
                });
                granted.Add(tenantId);
            }
            else if (current.RoleId != roleId)
            {
                current.RoleId = roleId;
                updated.Add(tenantId);
            }
        }

        foreach (TenantMembership membership in existing)
        {
            // Only revoke inside tenants SSO governs. A membership in a tenant with no group
            // mapping was granted by hand and is none of SSO's business.
            if (ssoGovernedTenants.Contains(membership.TenantId) && !desired.ContainsKey(membership.TenantId))
            {
                db.TenantMemberships.Remove(membership);
                revoked.Add(membership.TenantId);
            }
        }

        if (granted.Count > 0 || revoked.Count > 0 || updated.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "SSO group sync for user {UserId}: +{Granted} -{Revoked} ~{Updated} from {GroupCount} asserted group(s)",
                userId, granted.Count, revoked.Count, updated.Count, asserted.Count);
        }

        int remaining = existing.Count - revoked.Count + granted.Count;

        return new GroupSyncResult
        {
            AssertedGroups = asserted,
            Granted = granted,
            Revoked = revoked,
            Updated = updated,
            HasNoAccess = remaining <= 0,
        };
    }
}
