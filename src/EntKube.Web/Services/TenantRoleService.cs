using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class TenantRoleService(IDbContextFactory<IdentityDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// What <paramref name="userId"/> may do in the tenant: everything for a global administrator,
    /// what their role grants for a member, and nothing for anybody else — including a member of
    /// some other tenant, whose role there says nothing about here.
    /// </summary>
    public async Task<EffectivePermissions> PermissionsForAsync(Guid tenantId, string? userId, CancellationToken ct = default)
    {
        // Nobody signed in holds nothing.
        if (userId is null)
        {
            return EffectivePermissions.Nothing;
        }

        using IdentityDbContext db = dbFactory.CreateDbContext();

        // A global administrator runs the installation, and every tenant in it, member or not.
        bool globalAdmin = await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            where ur.UserId == userId && r.Name == "Admin"
            select ur).AnyAsync(ct);

        if (globalAdmin)
        {
            return EffectivePermissions.Everything;
        }

        // Anybody else holds what the role they hold in this tenant grants. A role is chosen per
        // tenant, so the lookup is by both.
        MembershipGrants? membership = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.UserId == userId)
            .Select(m => new MembershipGrants(m.Role.PermissionsJson))
            .FirstOrDefaultAsync(ct);

        if (membership is null)
        {
            return EffectivePermissions.Nothing;
        }

        return EffectivePermissions.Granting(DecodePermissions(membership.PermissionsJson));
    }

    /// <summary>
    /// The tenants where <paramref name="userId"/> holds something in <paramref name="area"/>:
    /// every tenant for a global administrator, otherwise those whose role grants at least View on
    /// one of the area's permissions. What the Support menu offers, for instance.
    /// </summary>
    public async Task<List<TenantPermissions>> TenantsHoldingAsync(string userId, PermissionArea area, CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        bool globalAdmin = await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            where ur.UserId == userId && r.Name == "Admin"
            select ur).AnyAsync(ct);

        if (globalAdmin)
        {
            return [.. (await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct))
                .Select(t => new TenantPermissions(t, EffectivePermissions.Everything))];
        }

        // One read for every membership, then the grants are judged here: they are stored as
        // JSON, which the database cannot be asked about portably.
        List<TenantGrants> memberships = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new TenantGrants(m.Tenant, m.Role.PermissionsJson))
            .ToListAsync(ct);

        return [.. memberships
            .Select(m => new TenantPermissions(m.Tenant, EffectivePermissions.Granting(DecodePermissions(m.PermissionsJson))))
            .Where(m => m.Permissions.HoldsAnyIn(area))
            .OrderBy(m => m.Tenant.Name)];
    }

    private sealed record TenantGrants(Tenant Tenant, string? PermissionsJson);

    // A membership's role grants, as stored. A record rather than the bare string, so that a member
    // whose role grants nothing (null) can be told apart from somebody who is not a member at all.
    private sealed record MembershipGrants(string? PermissionsJson);

    /// <summary>
    /// Carries the roles that existed before permissions were enforced across to the rules this
    /// build enforces, so that nobody loses what they could already do (see
    /// <see cref="PermissionCatalog.CarryAcross"/>). Each role is carried across once; a role
    /// made since never is.
    ///
    /// <para>Safe on several instances starting together: a tenant is done in one transaction,
    /// and each role is only written while it is still at the old version, so two instances
    /// cannot both write it — and would write the same thing if they did.</para>
    /// </summary>
    public async Task<IReadOnlyList<RolePermissionUpgrade>> CarryRolesAcrossAsync(CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        // The tenants with at least one role still waiting.
        List<Guid> tenantIds = await db.TenantRoles.AsNoTracking()
            .Where(r => r.PermissionSchema < TenantRole.CurrentPermissionSchema)
            .Select(r => r.TenantId)
            .Distinct()
            .ToListAsync(ct);

        List<RolePermissionUpgrade> upgrades = [];

        foreach (Guid tenantId in tenantIds)
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

            List<TenantRole> waiting = await db.TenantRoles.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.PermissionSchema < TenantRole.CurrentPermissionSchema)
                .ToListAsync(ct);

            // The knowledge base's opt-in was judged per tenant, from the roles as they stood
            // before enforcement — which are exactly the ones still waiting.
            bool editorsChosen = waiting.Any(r =>
                GetPermission(DecodePermissions(r.PermissionsJson), TenantFeature.KnowledgeBase) >= AccessLevel.Edit);

            int carried = 0;

            foreach (TenantRole role in waiting)
            {
                string json = EncodePermissions(PermissionCatalog.CarryAcross(DecodePermissions(role.PermissionsJson), editorsChosen));

                // Only while it is still waiting: another instance may have got here first.
                carried += await db.TenantRoles
                    .Where(r => r.Id == role.Id && r.PermissionSchema < TenantRole.CurrentPermissionSchema)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.PermissionsJson, json)
                        .SetProperty(r => r.PermissionSchema, TenantRole.CurrentPermissionSchema), ct);
            }

            await tx.CommitAsync(ct);

            if (carried > 0)
            {
                string name = await db.Tenants.AsNoTracking()
                    .Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? tenantId.ToString();

                upgrades.Add(new RolePermissionUpgrade(tenantId, name, carried, editorsChosen));
            }
        }

        return upgrades;
    }

    // ── Role CRUD ──────────────────────────────────────────────────────────────

    public async Task<List<TenantRole>> GetRolesAsync(Guid tenantId)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();
        return await db.TenantRoles
            .Include(r => r.Memberships)
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<TenantRole?> GetRoleAsync(Guid roleId)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();
        return await db.TenantRoles.FindAsync(roleId);
    }

    public async Task<TenantRole> CreateRoleAsync(Guid tenantId, string name)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();
        TenantRole role = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            PermissionsJson = null
        };
        db.TenantRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    public async Task<bool> DeleteRoleAsync(Guid roleId)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();
        TenantRole? role = await db.TenantRoles.FindAsync(roleId);
        if (role is null) return false;
        db.TenantRoles.Remove(role);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task RenameRoleAsync(Guid roleId, string newName)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();
        TenantRole? role = await db.TenantRoles.FindAsync(roleId);
        if (role is null) return;
        role.Name = newName;
        await db.SaveChangesAsync();
    }

    // ── Permissions ────────────────────────────────────────────────────────────

    public static Dictionary<TenantFeature, AccessLevel> DecodePermissions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<TenantFeature, AccessLevel>>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string EncodePermissions(Dictionary<TenantFeature, AccessLevel> permissions)
        => JsonSerializer.Serialize(permissions, JsonOpts);

    public async Task SavePermissionsAsync(Guid roleId, Dictionary<TenantFeature, AccessLevel> permissions)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();
        TenantRole? role = await db.TenantRoles.FindAsync(roleId);
        if (role is null) return;

        // Strip out None entries — they're the default anyway and keep the JSON compact.
        Dictionary<TenantFeature, AccessLevel> nonDefault = permissions
            .Where(kv => kv.Value != AccessLevel.None)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        role.PermissionsJson = nonDefault.Count == 0 ? null : EncodePermissions(nonDefault);
        await db.SaveChangesAsync();
    }

    public static AccessLevel GetPermission(Dictionary<TenantFeature, AccessLevel> permissions, TenantFeature feature)
        => permissions.TryGetValue(feature, out AccessLevel level) ? level : AccessLevel.None;
}
