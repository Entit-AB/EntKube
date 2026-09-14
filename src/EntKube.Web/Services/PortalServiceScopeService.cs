using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Works out which backing services (databases, RabbitMQ vhosts, Redis caches) a
/// customer's app is entitled to see and manage in one environment.
///
/// The portal must never show a customer the whole tenant's estate, so every portal
/// panel asks this service first and then acts only inside the returned scope. Scope
/// comes from two places, unioned:
///
///   • what the app is already wired to — the DatabaseBinding / MessagingBinding /
///     CacheBinding rows for the app's deployments in this environment. An operator
///     attaching a service is what puts it in the customer's hands.
///   • what an operator explicitly allow-listed — the AppAllowedDatabase /
///     AppAllowedCache rows managed from the Governance tab.
///
/// Nothing else is reachable: a database in a cluster the app has no binding or
/// allow-list entry for is invisible, and the Is*InScope guards let a panel re-check
/// a target before mutating so a stale page can't act outside the scope it rendered.
/// </summary>
public class PortalServiceScopeService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    // ── Databases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the database servers and logical databases an app may work with in
    /// one environment, along with the deployments those can be bound to.
    /// </summary>
    public async Task<PortalDatabaseScope> GetDatabaseScopeAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        List<Guid> deploymentIds = deployments.Select(d => d.Id).ToList();

        List<DatabaseBinding> bindings = await db.DatabaseBindings
            .Include(b => b.CnpgDatabase).ThenInclude(d => d!.CnpgCluster)
            .Include(b => b.MongoDatabase).ThenInclude(d => d!.MongoCluster)
            .Include(b => b.RegisteredPostgresDatabase).ThenInclude(d => d!.RegisteredPostgresInstance)
            .Where(b => deploymentIds.Contains(b.AppDeploymentId))
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        List<AppAllowedDatabase> allowed = await db.AppAllowedDatabases
            .Include(a => a.CnpgDatabase).ThenInclude(d => d!.CnpgCluster)
            .Include(a => a.MongoDatabase).ThenInclude(d => d!.MongoCluster)
            .Include(a => a.RegisteredPostgresDatabase).ThenInclude(d => d!.RegisteredPostgresInstance)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .ToListAsync(ct);

        // The union of both sources, de-duplicated. A tenant guard is applied on the
        // owning server so a stale binding can never drag in another tenant's data.
        List<CnpgDatabase> cnpgDatabases = Distinct(
            bindings.Select(b => b.CnpgDatabase).Concat(allowed.Select(a => a.CnpgDatabase))
                .Where(d => d?.CnpgCluster is not null && d.CnpgCluster.TenantId == tenantId)!,
            d => d.Id);

        List<MongoDatabase> mongoDatabases = Distinct(
            bindings.Select(b => b.MongoDatabase).Concat(allowed.Select(a => a.MongoDatabase))
                .Where(d => d?.MongoCluster is not null && d.MongoCluster.TenantId == tenantId)!,
            d => d.Id);

        List<RegisteredPostgresDatabase> registeredDatabases = Distinct(
            bindings.Select(b => b.RegisteredPostgresDatabase)
                .Concat(allowed.Select(a => a.RegisteredPostgresDatabase))
                .Where(d => d?.RegisteredPostgresInstance is not null
                            && d.RegisteredPostgresInstance.TenantId == tenantId)!,
            d => d.Id);

        return new PortalDatabaseScope
        {
            Deployments = deployments,
            Bindings = bindings,
            CnpgDatabases = cnpgDatabases,
            MongoDatabases = mongoDatabases,
            RegisteredDatabases = registeredDatabases,
            CnpgClusters = Distinct(cnpgDatabases.Select(d => d.CnpgCluster), c => c.Id),
            MongoClusters = Distinct(mongoDatabases.Select(d => d.MongoCluster), c => c.Id),
            RegisteredInstances = Distinct(
                registeredDatabases.Select(d => d.RegisteredPostgresInstance), i => i.Id),
        };
    }

    /// <summary>
    /// Records a database the portal just created as allow-listed for this app and
    /// environment, so it stays in scope before anything has been bound to it.
    /// Idempotent — a second call for the same database is a no-op.
    /// </summary>
    public async Task GrantDatabaseAsync(
        Guid appId, Guid environmentId,
        Guid? cnpgDatabaseId = null, Guid? mongoDatabaseId = null,
        Guid? registeredPostgresDatabaseId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool exists = await db.AppAllowedDatabases.AnyAsync(a =>
            a.AppId == appId && a.EnvironmentId == environmentId
            && a.CnpgDatabaseId == cnpgDatabaseId
            && a.MongoDatabaseId == mongoDatabaseId
            && a.RegisteredPostgresDatabaseId == registeredPostgresDatabaseId, ct);

        if (exists) return;

        db.AppAllowedDatabases.Add(new AppAllowedDatabase
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = environmentId,
            CnpgDatabaseId = cnpgDatabaseId,
            MongoDatabaseId = mongoDatabaseId,
            RegisteredPostgresDatabaseId = registeredPostgresDatabaseId,
        });
        await db.SaveChangesAsync(ct);
    }

    // ── Messaging ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the RabbitMQ cluster+vhost pairs an app may manage topology inside.
    /// A vhost is in scope only because a MessagingBinding already points the app at
    /// it, which is why the portal can offer queue and exchange management but never
    /// vhost creation — the vhost is the boundary, not something the customer picks.
    /// </summary>
    public async Task<PortalMessagingScope> GetMessagingScopeAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        List<Guid> deploymentIds = deployments.Select(d => d.Id).ToList();

        List<MessagingBinding> bindings = await db.MessagingBindings
            .Include(b => b.Cluster)
            .Where(b => b.TenantId == tenantId && deploymentIds.Contains(b.AppDeploymentId))
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        List<PortalVhostScope> vhosts = bindings
            .Where(b => b.Cluster is not null)
            .GroupBy(b => (b.RabbitMQClusterId, b.Vhost))
            .Select(g => new PortalVhostScope
            {
                Cluster = g.First().Cluster,
                Vhost = g.Key.Vhost,
                Bindings = g.OrderBy(b => b.CreatedAt).ToList(),
            })
            .OrderBy(v => v.Cluster.Name)
            .ThenBy(v => v.Vhost)
            .ToList();

        return new PortalMessagingScope
        {
            Deployments = deployments,
            Bindings = bindings,
            Vhosts = vhosts,
        };
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the Redis clusters attached to an app in one environment. The portal
    /// presents these read-only: a customer sees what backs their cache and can
    /// re-sync the credentials secret, but cannot attach, detach or reshape a cluster.
    /// </summary>
    public async Task<PortalCacheScope> GetCacheScopeAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        List<Guid> deploymentIds = deployments.Select(d => d.Id).ToList();

        List<CacheBinding> bindings = await db.CacheBindings
            .Include(b => b.RedisCluster).ThenInclude(c => c.KubernetesCluster)
            .Where(b => b.TenantId == tenantId && deploymentIds.Contains(b.AppDeploymentId))
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        List<AppAllowedCache> allowed = await db.AppAllowedCaches
            .Include(a => a.RedisCluster).ThenInclude(c => c.KubernetesCluster)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .ToListAsync(ct);

        List<RedisCluster> clusters = Distinct(
            bindings.Select(b => b.RedisCluster)
                .Concat(allowed.Select(a => a.RedisCluster))
                .Where(c => c is not null && c.TenantId == tenantId),
            c => c.Id);

        return new PortalCacheScope
        {
            Deployments = deployments,
            Bindings = bindings,
            Clusters = clusters,
        };
    }

    private static List<T> Distinct<T, TKey>(IEnumerable<T> source, Func<T, TKey> key)
        where TKey : notnull =>
        source.GroupBy(key).Select(g => g.First()).ToList();
}

/// <summary>What an app may see and do with databases in one environment.</summary>
public sealed class PortalDatabaseScope
{
    public List<AppDeployment> Deployments { get; init; } = [];
    public List<DatabaseBinding> Bindings { get; init; } = [];

    /// <summary>Servers the app may create new databases in.</summary>
    public List<CnpgCluster> CnpgClusters { get; init; } = [];
    public List<MongoCluster> MongoClusters { get; init; } = [];
    public List<RegisteredPostgresInstance> RegisteredInstances { get; init; } = [];

    /// <summary>Logical databases the app may use, sync and rotate.</summary>
    public List<CnpgDatabase> CnpgDatabases { get; init; } = [];
    public List<MongoDatabase> MongoDatabases { get; init; } = [];
    public List<RegisteredPostgresDatabase> RegisteredDatabases { get; init; } = [];

    public bool IsEmpty =>
        CnpgClusters.Count == 0 && MongoClusters.Count == 0 && RegisteredInstances.Count == 0;

    public bool AllowsCnpgCluster(Guid id) => CnpgClusters.Any(c => c.Id == id);
    public bool AllowsMongoCluster(Guid id) => MongoClusters.Any(c => c.Id == id);
    public bool AllowsCnpgDatabase(Guid id) => CnpgDatabases.Any(d => d.Id == id);
    public bool AllowsMongoDatabase(Guid id) => MongoDatabases.Any(d => d.Id == id);
    public bool AllowsRegisteredDatabase(Guid id) => RegisteredDatabases.Any(d => d.Id == id);
    public bool AllowsDeployment(Guid id) => Deployments.Any(d => d.Id == id);

    /// <summary>Deployments a given binding-less database is not yet attached to.</summary>
    public List<AppDeployment> DeploymentsWithout(Func<DatabaseBinding, bool> matches) =>
        Deployments.Where(d => !Bindings.Any(b => b.AppDeploymentId == d.Id && matches(b))).ToList();
}

/// <summary>What an app may see and do with RabbitMQ in one environment.</summary>
public sealed class PortalMessagingScope
{
    public List<AppDeployment> Deployments { get; init; } = [];
    public List<MessagingBinding> Bindings { get; init; } = [];
    public List<PortalVhostScope> Vhosts { get; init; } = [];

    public bool IsEmpty => Vhosts.Count == 0;

    /// <summary>True when the app is bound to this exact cluster+vhost pair.</summary>
    public bool AllowsVhost(Guid clusterId, string vhost) =>
        Vhosts.Any(v => v.Cluster.Id == clusterId && v.Vhost == vhost);

    public bool AllowsDeployment(Guid id) => Deployments.Any(d => d.Id == id);
}

/// <summary>One RabbitMQ vhost an app owns, with the bindings that granted it.</summary>
public sealed class PortalVhostScope
{
    public required RabbitMQCluster Cluster { get; init; }
    public required string Vhost { get; init; }
    public List<MessagingBinding> Bindings { get; init; } = [];

    public string Key => $"{Cluster.Id}|{Vhost}";
}

/// <summary>What an app may see of its caches in one environment. Read-only.</summary>
public sealed class PortalCacheScope
{
    public List<AppDeployment> Deployments { get; init; } = [];
    public List<CacheBinding> Bindings { get; init; } = [];
    public List<RedisCluster> Clusters { get; init; } = [];

    public bool IsEmpty => Clusters.Count == 0;
}
