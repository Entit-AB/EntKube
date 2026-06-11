using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Provides CRUD operations for tenants and their directly-owned children
/// (environments, customers, groups, clusters). Each method tells a simple
/// story: load, validate, persist, return.
/// </summary>
public class TenantService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    // --- Tenant CRUD ---

    public async Task<List<Tenant>> GetAllTenantsAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Tenants.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
    }

    public async Task<Tenant> CreateTenantAsync(string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // The slug is auto-generated from the name — users shouldn't have to think about URL encoding.
        // We take the name, lowercase it, replace spaces and special chars with hyphens,
        // collapse runs of hyphens, and trim leading/trailing hyphens.

        string slug = GenerateSlug(name);

        Guid tenantId = Guid.NewGuid();
        Tenant tenant = new() { Id = tenantId, Name = name, Slug = slug };
        db.Tenants.Add(tenant);

        // Seed default roles so tenant memberships can be assigned immediately.
        db.TenantRoles.AddRange(
            new TenantRole { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Administrator" },
            new TenantRole { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Member" },
            new TenantRole { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Viewer" }
        );

        await db.SaveChangesAsync(ct);
        return tenant;
    }

    /// <summary>
    /// Converts a human-friendly name into a URL-safe slug.
    /// "Acme Corp!" → "acme-corp", "My Cool Tenant" → "my-cool-tenant"
    /// </summary>
    public static string GenerateSlug(string name)
    {
        string slug = name.ToLowerInvariant();

        // Replace any character that isn't a letter, digit, or hyphen with a hyphen.
        char[] chars = new char[slug.Length];

        for (int i = 0; i < slug.Length; i++)
        {
            char c = slug[i];
            chars[i] = char.IsLetterOrDigit(c) ? c : '-';
        }

        slug = new string(chars);

        // Collapse multiple consecutive hyphens into one.
        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        // Trim leading/trailing hyphens.
        return slug.Trim('-');
    }

    public async Task<Tenant?> UpdateTenantAsync(Guid id, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Tenant? tenant = await db.Tenants.FindAsync([id], ct);

        if (tenant is null)
        {
            return null;
        }

        tenant.Name = name;
        tenant.Slug = GenerateSlug(name);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    public async Task<bool> DeleteTenantAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Tenant? tenant = await db.Tenants.FindAsync([id], ct);

        if (tenant is null)
        {
            return false;
        }

        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // --- Environment CRUD ---

    public async Task<List<Data.Environment>> GetEnvironmentsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Environments
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Name)
            .ToListAsync(ct);
    }

    public async Task<Data.Environment> CreateEnvironmentAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Data.Environment environment = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = name };
        db.Environments.Add(environment);
        await db.SaveChangesAsync(ct);
        return environment;
    }

    public async Task<bool> DeleteEnvironmentAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Data.Environment? environment = await db.Environments.FindAsync([id], ct);

        if (environment is null)
        {
            return false;
        }

        db.Environments.Remove(environment);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // --- Customer CRUD ---

    public async Task<List<Customer>> GetCustomersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Customers
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Customers.FindAsync([id], ct);
    }

    public async Task<Customer> CreateCustomerAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = name };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<bool> DeleteCustomerAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Customer? customer = await db.Customers.FindAsync([id], ct);

        if (customer is null)
        {
            return false;
        }

        db.Customers.Remove(customer);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // --- App CRUD ---

    public async Task<List<App>> GetAppsAsync(Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Apps
            .Where(a => a.CustomerId == customerId)
            .Include(a => a.AppEnvironments)
                .ThenInclude(ae => ae.Environment)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
    }

    public async Task<App> CreateAppAsync(Guid customerId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        App app = new() { Id = Guid.NewGuid(), CustomerId = customerId, Name = name };
        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);
        return app;
    }

    public async Task<bool> RenameAppAsync(Guid id, string newName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        App? app = await db.Apps.FindAsync([id], ct);
        if (app is null) return false;

        app.Name = newName.Trim();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAppAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        App? app = await db.Apps.FindAsync([id], ct);

        if (app is null)
        {
            return false;
        }

        db.Apps.Remove(app);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task LinkAppToEnvironmentAsync(Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool exists = await db.AppEnvironments
            .AnyAsync(ae => ae.AppId == appId && ae.EnvironmentId == environmentId, ct);

        if (!exists)
        {
            db.AppEnvironments.Add(new AppEnvironment { AppId = appId, EnvironmentId = environmentId });
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task UnlinkAppFromEnvironmentAsync(Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppEnvironment? link = await db.AppEnvironments
            .FirstOrDefaultAsync(ae => ae.AppId == appId && ae.EnvironmentId == environmentId, ct);

        if (link is not null)
        {
            db.AppEnvironments.Remove(link);
            await db.SaveChangesAsync(ct);
        }
    }

    // --- Group CRUD ---

    public async Task<List<Group>> GetGroupsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Groups
            .Where(g => g.TenantId == tenantId)
            .Include(g => g.Memberships)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<Group> CreateGroupAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Group group = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = name };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<bool> DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Group? group = await db.Groups.FindAsync([id], ct);

        if (group is null)
        {
            return false;
        }

        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Returns all apps across all customers of a tenant, each with its Customer included.</summary>
    public async Task<List<App>> GetAppsWithCustomersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Apps
            .Include(a => a.Customer)
            .Where(a => a.Customer.TenantId == tenantId)
            .OrderBy(a => a.Customer.Name).ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    // --- Kubernetes Cluster CRUD ---

    public async Task<List<KubernetesCluster>> GetClustersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId)
            .Include(c => c.Environment)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<KubernetesCluster> CreateClusterAsync(
        Guid tenantId, Guid environmentId, string name, string apiServerUrl,
        string? contextName = null, string? kubeconfig = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Name = name,
            ApiServerUrl = apiServerUrl,
            ContextName = contextName,
            Kubeconfig = kubeconfig
        };

        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync(ct);
        return cluster;
    }

    public async Task<bool> DeleteClusterAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters.FindAsync([id], ct);

        if (cluster is null)
        {
            return false;
        }

        db.KubernetesClusters.Remove(cluster);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // --- User lookup ---

    /// <summary>
    /// Finds an application user by their email address. Used when granting
    /// portal access — the admin types an email and we look up the user.
    /// </summary>
    public async Task<ApplicationUser?> FindUserByEmailAsync(
        string email, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Users
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }
}
