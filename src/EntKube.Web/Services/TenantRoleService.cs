using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class TenantRoleService(IDbContextFactory<ApplicationDbContext> dbFactory)
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
    ];

    // ── Role CRUD ──────────────────────────────────────────────────────────────

    public async Task<List<TenantRole>> GetRolesAsync(Guid tenantId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.TenantRoles
            .Include(r => r.Memberships)
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<TenantRole?> GetRoleAsync(Guid roleId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.TenantRoles.FindAsync(roleId);
    }

    public async Task<TenantRole> CreateRoleAsync(Guid tenantId, string name)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        TenantRole? role = await db.TenantRoles.FindAsync(roleId);
        if (role is null) return false;
        db.TenantRoles.Remove(role);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task RenameRoleAsync(Guid roleId, string newName)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();
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
