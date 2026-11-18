using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class TenantRoleService(IDbContextFactory<IdentityDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // Feature display metadata — order determines row order in the permission matrix.
    public static readonly IReadOnlyList<(TenantFeature Feature, string Label, string Icon)> Features =
    [
        (TenantFeature.Clusters,           "Clusters",           "bi-hdd-network"),
        (TenantFeature.Environments,       "Environments",       "bi-layers"),
        (TenantFeature.Customers,          "Customers",          "bi-building"),
        (TenantFeature.Apps,               "Apps",               "bi-box-seam"),
        (TenantFeature.Deployments,        "Deployments",        "bi-rocket"),
        (TenantFeature.Groups,             "Groups",             "bi-diagram-3"),
        (TenantFeature.Databases,          "Databases",          "bi-database"),
        (TenantFeature.Storage,            "Storage",            "bi-cloud-arrow-up"),
        (TenantFeature.GitRepositories,    "Git Repositories",   "bi-git"),
        (TenantFeature.Keycloak,           "Keycloak / Identity","bi-shield-lock"),
        (TenantFeature.ContainerRegistry,  "Container Registry", "bi-box2-heart"),
        (TenantFeature.Messaging,          "Messaging",          "bi-send"),
        (TenantFeature.Cache,              "Cache",              "bi-lightning"),
        (TenantFeature.VPN,                "VPN",                "bi-shield-shaded"),
        (TenantFeature.Monitoring,         "Monitoring",         "bi-activity"),
        (TenantFeature.Audit,              "Audit Log",          "bi-journal-text"),
        (TenantFeature.JitAccess,          "JIT cluster access", "bi-terminal"),
        (TenantFeature.KnowledgeBase,      "Support knowledge base", "bi-book"),
    ];

    /// <summary>
    /// Whether <paramref name="userId"/>'s role in the tenant grants <paramref name="feature"/> at
    /// <paramref name="atLeast"/> — and whether any role there does at all, for a permission that
    /// only binds once somebody has set it (see <see cref="TenantFeature.KnowledgeBase"/>).
    /// </summary>
    public async Task<(bool AnyRoleGrants, bool UserGranted)> FeatureGrantAsync(
        Guid tenantId, string? userId, TenantFeature feature, AccessLevel atLeast, CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        List<(Guid Id, string? Json)> roles = [.. (await db.TenantRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Select(r => new { r.Id, r.PermissionsJson })
            .ToListAsync(ct)).Select(r => (r.Id, r.PermissionsJson))];

        HashSet<Guid> granting = [.. roles.Where(r => GetPermission(DecodePermissions(r.Json), feature) >= atLeast).Select(r => r.Id)];

        if (granting.Count == 0 || userId is null)
        {
            return (granting.Count > 0, false);
        }

        bool member = await db.TenantMemberships.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId && granting.Contains(m.RoleId), ct);

        return (true, member);
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
