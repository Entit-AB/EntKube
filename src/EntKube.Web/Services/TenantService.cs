using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Provides CRUD operations for tenants and their directly-owned children
/// (environments, customers, groups, clusters). Each method tells a simple
/// story: load, validate, persist, return.
/// </summary>
public class TenantService(IDbContextFactory<ApplicationDbContext> dbFactory, VaultService vault)
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

    /// <summary>
    /// Removes a tenant and every app-side record tied to it — environments, customers,
    /// apps and their deployments, registered clusters and components, vault secrets
    /// (including kubeconfigs), blueprints, incidents, telemetry segments, dashboards, and
    /// all the rest.
    ///
    /// This is a PURE APP-DATABASE purge and never touches the tenant's real infrastructure:
    /// no Helm uninstall, no kubectl, no vault→cluster sync. Deleting a stored kubeconfig or
    /// secret row only makes EntKube forget it locally; the workloads keep running untouched.
    /// (All cluster-touching flows — ComponentLifecycleService uninstall, route/secret sync —
    /// are UI-triggered and decoupled from row deletion, so simply not calling them is enough.)
    ///
    /// A naive <c>Tenants.Remove()</c> that leans on cascade is NOT sufficient:
    ///   1. Many FKs are Restrict (Environment/Cluster/Role back-references,
    ///      ConnectivityRule.PeerApp) and would throw an FK violation on any non-trivial tenant.
    ///   2. A few telemetry/dashboard tables carry a TenantId with no FK to Tenant, so cascade
    ///      never reaches them and would leave them orphaned.
    /// So we delete child→parent in one transaction, clearing the Restrict boundaries and the
    /// FK-less orphans first, then let the tenant cascade sweep everything that remains.
    /// </summary>
    public async Task<bool> DeleteTenantAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (!await db.Tenants.AnyAsync(t => t.Id == id, ct))
        {
            return false;
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct);

        // 1. Orphan tables — a TenantId column with no FK to Tenant, so cascade never reaches
        //    them. Purge explicitly. Keep this list in sync with any new tenant-keyed table
        //    that deliberately opts out of a Tenant FK (see the telemetry/dashboard entities).
        await db.RumSites.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await db.TelemetrySegments.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await db.TelemetryStorageSettings.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await db.TelemetryAlertRules.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await db.Dashboards.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);

        // 2. Restrict-FK boundaries, cleared child→parent so the final tenant cascade can drop
        //    Environments / Clusters / Roles without tripping a NO ACTION constraint.

        // Connectivity rules that reference a tenant app as a peer (PeerApp is Restrict). Rules
        // an app owns cascade with it; these cross-references must go first.
        await db.ConnectivityRules.Where(r => r.PeerApp!.Customer.TenantId == id).ExecuteDeleteAsync(ct);
        // Apps → cascades deployments and every app-scoped child that Restrict-refs Env/Cluster.
        await db.Apps.Where(a => a.Customer.TenantId == id).ExecuteDeleteAsync(ct);
        // VPN tunnels → cascades local endpoints that Restrict-ref Cluster.
        await db.VpnTunnels.Where(v => v.TenantId == id).ExecuteDeleteAsync(ct);
        // Kyverno policies Restrict-ref Environment.
        await db.KyvernoPolicies.Where(k => k.TenantId == id).ExecuteDeleteAsync(ct);
        // Customers → cascades git creds/policies that Restrict-ref Environment.
        await db.Customers.Where(c => c.TenantId == id).ExecuteDeleteAsync(ct);
        // Clusters → cascades components, routes, incidents, managed DB clusters, and the
        // encrypted kubeconfig secrets. Nothing Restrict-refs Cluster any more at this point.
        await db.KubernetesClusters.Where(c => c.TenantId == id).ExecuteDeleteAsync(ct);
        // Memberships Restrict-ref Role; clear before the roles cascade with the tenant.
        await db.TenantMemberships.Where(m => m.TenantId == id).ExecuteDeleteAsync(ct);

        // 3. The tenant itself. Cascade now safely sweeps environments, roles, groups, the
        //    vault + remaining shared secrets, blueprints, git repos, notification/alert
        //    config, SLA/maintenance/on-call, advisor state, and everything else tenant-keyed.
        Tenant? tenant = await db.Tenants.FindAsync([id], ct);
        if (tenant is not null)
        {
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Counts the main things <see cref="DeleteTenantAsync"/> will remove, for a confirmation
    /// dialog. Read-only. Returns null if the tenant doesn't exist.
    /// </summary>
    public async Task<TenantPurgePreview?> GetTenantPurgePreviewAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Tenant? tenant = await db.Tenants.FindAsync([id], ct);
        if (tenant is null)
        {
            return null;
        }

        return new TenantPurgePreview(
            tenant.Name,
            Environments: await db.Environments.CountAsync(e => e.TenantId == id, ct),
            Customers: await db.Customers.CountAsync(c => c.TenantId == id, ct),
            Apps: await db.Apps.CountAsync(a => a.Customer.TenantId == id, ct),
            Deployments: await db.AppDeployments.CountAsync(d => d.App.Customer.TenantId == id, ct),
            Clusters: await db.KubernetesClusters.CountAsync(c => c.TenantId == id, ct),
            Components: await db.ClusterComponents.CountAsync(cc => cc.Cluster.TenantId == id, ct),
            Secrets: await db.VaultSecrets.CountAsync(s => s.Vault.TenantId == id, ct),
            Dashboards: await db.Dashboards.CountAsync(x => x.TenantId == id, ct));
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

        // Connectivity rules where OTHER apps reference this app as a peer are held by a
        // Restrict FK (ConnectivityRule.PeerApp), so they must be cleared first or the
        // delete fails. Rules this app owns cascade with it.
        await db.ConnectivityRules.Where(r => r.PeerAppId == id).ExecuteDeleteAsync(ct);

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
        string? contextName = null, string? kubeconfig = null,
        DateTime? kubeconfigExpiresAt = null, string? updatedBy = null, CancellationToken ct = default)
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
        };

        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        // The kubeconfig is sensitive and is stored encrypted in the tenant vault (not as a
        // plaintext column). SetClusterKubeconfigAsync writes the secret and back-references it
        // on the cluster via KubeconfigSecretId; the interceptor resolves it on future loads.
        if (!string.IsNullOrWhiteSpace(kubeconfig))
        {
            KubeconfigBundle bundle = new()
            {
                ConfigYaml = kubeconfig,
                ContextName = contextName,
                ApiServerUrl = apiServerUrl,
                ExpiresAt = kubeconfigExpiresAt,
            };

            (bool ok, string? error, Guid? secretId) =
                await vault.SetClusterKubeconfigAsync(tenantId, cluster.Id, bundle, updatedBy, ct);
            if (!ok)
            {
                throw new InvalidOperationException(error ?? "Failed to store the cluster kubeconfig.");
            }

            // Reflect the just-persisted values on the returned entity for the caller.
            cluster.KubeconfigSecretId = secretId;
            cluster.Kubeconfig = kubeconfig;
        }

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

/// <summary>Headline counts of what removing a tenant from the app will delete.</summary>
public record TenantPurgePreview(
    string TenantName,
    int Environments,
    int Customers,
    int Apps,
    int Deployments,
    int Clusters,
    int Components,
    int Secrets,
    int Dashboards)
{
    public int Total => Environments + Customers + Apps + Deployments
        + Clusters + Components + Secrets + Dashboards;
}
