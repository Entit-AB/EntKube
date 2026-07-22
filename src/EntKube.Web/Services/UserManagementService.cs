using EntKube.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class UserManagementService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPresenceTracker presence)
{
    public async Task<List<ApplicationUser>> GetAllUsersAsync()
    {
        return await userManager.Users.OrderBy(u => u.Email).ToListAsync();
    }

    /// <summary>
    /// Loads all users together with their security posture — online status,
    /// two-factor state, and registered passkey count — for the admin list.
    /// </summary>
    public async Task<List<UserSecurityInfo>> GetAllUsersWithSecurityAsync()
    {
        List<ApplicationUser> users = await GetAllUsersAsync();
        IReadOnlySet<string> online = await presence.GetOnlineUsersAsync();

        List<UserSecurityInfo> result = new(users.Count);
        foreach (ApplicationUser user in users)
        {
            // GetPasskeysAsync uses the same schema-v3 context the UserManager
            // owns, so it works consistently across all database providers.
            int passkeys = (await userManager.GetPasskeysAsync(user)).Count;
            result.Add(new UserSecurityInfo(
                user,
                IsOnline: online.Contains(user.Id),
                TwoFactorEnabled: user.TwoFactorEnabled,
                PasskeyCount: passkeys,
                HasAuthenticator: await userManager.GetAuthenticatorKeyAsync(user) is not null));
        }
        return result;
    }

    /// <summary>Security posture for a single user (used by the detail page).</summary>
    public async Task<UserSecurityInfo?> GetUserSecurityAsync(string userId)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId);
        if (user is null) return null;
        int passkeys = (await userManager.GetPasskeysAsync(user)).Count;
        return new UserSecurityInfo(
            user,
            IsOnline: await presence.IsOnlineAsync(user.Id),
            TwoFactorEnabled: user.TwoFactorEnabled,
            PasskeyCount: passkeys,
            HasAuthenticator: await userManager.GetAuthenticatorKeyAsync(user) is not null);
    }

    // Identity's internal token coordinates for the authenticator key + recovery codes.
    // UserManager stores both under this fixed pseudo-provider; deleting the token rows is the
    // only way back to a truly "no authenticator configured" state — ResetAuthenticatorKeyAsync
    // merely regenerates a key, which the user's 2FA page still reads as configured.
    private const string InternalLoginProvider = "[AspNetUserStore]";
    private const string AuthenticatorKeyTokenName = "AuthenticatorKey";
    private const string RecoveryCodesTokenName = "RecoveryCodes";

    /// <summary>
    /// Forcibly clears ALL multi-factor state for a user, whatever half-finished state it is in:
    /// disables the two-factor flag, deletes the authenticator-key and recovery-code tokens, and
    /// optionally removes every registered passkey. Rotates the security stamp so existing
    /// sessions and remembered-browser 2FA are invalidated. Idempotent — safe to run even when
    /// nothing is configured. This resolves the mismatch where the admin flag reads "off" but the
    /// user's own page still sees a leftover authenticator key from an abandoned setup.
    /// </summary>
    public async Task<(bool Success, string? Error, int PasskeysRemoved)> ResetAndDisableMfaAsync(
        string userId, bool alsoRemovePasskeys = false)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId);
        if (user is null) return (false, "User not found.", 0);

        // Disable the persisted two-factor flag (what the admin views read).
        await userManager.SetTwoFactorEnabledAsync(user, false);

        // Delete the authenticator-key and recovery-code token rows outright, so the user's own
        // 2FA page (which keys off GetAuthenticatorKeyAsync) returns to a clean "not configured".
        await userManager.RemoveAuthenticationTokenAsync(user, InternalLoginProvider, AuthenticatorKeyTokenName);
        await userManager.RemoveAuthenticationTokenAsync(user, InternalLoginProvider, RecoveryCodesTokenName);

        int passkeysRemoved = 0;
        if (alsoRemovePasskeys)
        {
            foreach (UserPasskeyInfo passkey in await userManager.GetPasskeysAsync(user))
            {
                IdentityResult removed = await userManager.RemovePasskeyAsync(user, passkey.CredentialId);
                if (removed.Succeeded) passkeysRemoved++;
            }
        }

        // Rotate the security stamp → invalidates auth cookies and remembered-browser 2FA.
        await userManager.UpdateSecurityStampAsync(user);
        return (true, null, passkeysRemoved);
    }

    public async Task<ApplicationUser?> GetUserByIdAsync(string userId)
    {
        return await userManager.FindByIdAsync(userId);
    }

    public async Task<IdentityResult> CreateUserAsync(string email, string password)
    {
        ApplicationUser user = new();
        await userManager.SetUserNameAsync(user, email);
        await userManager.SetEmailAsync(user, email);

        IdentityResult result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded) return result;

        // Admin-created accounts are pre-confirmed — no email flow needed.
        string token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        await userManager.ConfirmEmailAsync(user, token);

        return result;
    }

    public async Task<IdentityResult> DeleteUserAsync(string userId)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "User not found." });
        return await userManager.DeleteAsync(user);
    }

    public async Task<List<IdentityRole>> GetAllRolesAsync()
    {
        return await roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<IList<string>> GetUserRolesAsync(string userId)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId);
        if (user is null) return [];
        return await userManager.GetRolesAsync(user);
    }

    public async Task<IdentityResult> AddToRoleAsync(string userId, string role)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "User not found." });
        return await userManager.AddToRoleAsync(user, role);
    }

    public async Task<IdentityResult> RemoveFromRoleAsync(string userId, string role)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "User not found." });
        return await userManager.RemoveFromRoleAsync(user, role);
    }

    public async Task<List<TenantMembership>> GetUserTenantMembershipsAsync(string userId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.TenantMemberships
            .Include(m => m.Tenant)
            .Include(m => m.Role)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Tenant.Name)
            .ToListAsync();
    }

    public async Task<List<Tenant>> GetAllTenantsAsync()
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Tenants
            .Include(t => t.Roles)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task AddTenantMembershipAsync(string userId, Guid tenantId, Guid roleId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = userId,
            TenantId = tenantId,
            RoleId = roleId,
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task RemoveTenantMembershipAsync(string userId, Guid tenantId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        TenantMembership? m = await db.TenantMemberships
            .FirstOrDefaultAsync(x => x.UserId == userId && x.TenantId == tenantId);
        if (m is not null)
        {
            db.TenantMemberships.Remove(m);
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<GroupMembership>> GetUserGroupMembershipsAsync(string userId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.GroupMemberships
            .Include(gm => gm.Group)
                .ThenInclude(g => g.Tenant)
            .Where(gm => gm.UserId == userId)
            .OrderBy(gm => gm.Group.Tenant.Name)
            .ThenBy(gm => gm.Group.Name)
            .ToListAsync();
    }

    public async Task<List<Group>> GetGroupsForTenantAsync(Guid tenantId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Groups
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task AddGroupMembershipAsync(string userId, Guid groupId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.GroupMemberships.Add(new GroupMembership
        {
            UserId = userId,
            GroupId = groupId,
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task RemoveGroupMembershipAsync(string userId, Guid groupId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        GroupMembership? gm = await db.GroupMemberships
            .FirstOrDefaultAsync(x => x.UserId == userId && x.GroupId == groupId);
        if (gm is not null)
        {
            db.GroupMemberships.Remove(gm);
            await db.SaveChangesAsync();
        }
    }
}

/// <summary>
/// A user paired with its security posture for the admin views: whether they
/// currently have an active session, whether MFA (two-factor) is enabled, and
/// how many passkeys they have registered.
/// </summary>
public record UserSecurityInfo(
    ApplicationUser User,
    bool IsOnline,
    bool TwoFactorEnabled,
    int PasskeyCount,
    bool HasAuthenticator = false)
{
    /// <summary>
    /// An authenticator key exists but two-factor was never switched on — an abandoned/
    /// half-finished TOTP setup. This is exactly the state where the admin flag says "off"
    /// yet the user's own 2FA page insists it is "configured".
    /// </summary>
    public bool MfaSetupIncomplete => HasAuthenticator && !TwoFactorEnabled;
}
