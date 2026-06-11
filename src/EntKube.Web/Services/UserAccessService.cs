using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public class UserAccessService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<bool> IsGlobalAdminAsync(string userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            where ur.UserId == userId && r.Name == "Admin"
            select ur
        ).AnyAsync(ct);
    }

    public async Task<bool> HasAnyTenantMembershipAsync(string userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.TenantMemberships.AnyAsync(m => m.UserId == userId, ct);
    }

    public async Task<bool> HasTenantAccessAsync(string userId, Guid tenantId, CancellationToken ct = default)
    {
        if (await IsGlobalAdminAsync(userId, ct)) return true;
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.TenantMemberships.AnyAsync(m => m.UserId == userId && m.TenantId == tenantId, ct);
    }

    public async Task<bool> HasAnyAccessAsync(string userId, CancellationToken ct = default)
    {
        if (await IsGlobalAdminAsync(userId, ct)) return true;
        return await HasAnyTenantMembershipAsync(userId, ct);
    }

    public async Task<List<Tenant>> GetAccessibleTenantsAsync(string userId, CancellationToken ct = default)
    {
        bool isAdmin = await IsGlobalAdminAsync(userId, ct);
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (isAdmin)
            return await db.Tenants.OrderBy(t => t.Name).ToListAsync(ct);

        return await db.TenantMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.Tenant)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }
}
