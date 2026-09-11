using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Reads and edits the cluster spec — the record of what a cluster is supposed to be, which day-2
/// operations change and the reconciler acts on.
///
/// <para>Every write that alters the shape of the cluster bumps <see cref="ProvisionedCluster.Generation"/>.
/// That is what makes "has this been applied yet" answerable without diffing the whole world, and
/// what stops a reconciler from reporting success against a spec that has moved since.</para>
/// </summary>
public class ProvisionedClusterService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ProvisionedClusterService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ──────── Reads ────────

    public async Task<List<ProvisionedCluster>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<ProvisionedCluster?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
    }

    /// <summary>The spec behind a registered cluster, if EntKube built it rather than adopted it.</summary>
    public async Task<ProvisionedCluster?> GetByClusterAsync(Guid tenantId, Guid kubernetesClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .FirstOrDefaultAsync(c => c.KubernetesClusterId == kubernetesClusterId && c.TenantId == tenantId, ct);
    }

    // ──────── Writes ────────

    public async Task<ProvisionedCluster> CreateAsync(
        ProvisionedCluster spec, IEnumerable<ProvisionedWorkerPool> pools, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        spec.Id = spec.Id == Guid.Empty ? Guid.NewGuid() : spec.Id;
        spec.CreatedAt = DateTime.UtcNow;
        spec.UpdatedAt = spec.CreatedAt;
        spec.Generation = 1;
        spec.ObservedGeneration = 0;

        db.ProvisionedClusters.Add(spec);

        foreach (ProvisionedWorkerPool pool in pools)
        {
            pool.Id = pool.Id == Guid.Empty ? Guid.NewGuid() : pool.Id;
            pool.ProvisionedClusterId = spec.Id;
            db.ProvisionedWorkerPools.Add(pool);
        }

        await db.SaveChangesAsync(ct);

        IReadOnlyList<string> errors = Validate(spec, pools.ToList());
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Cluster spec {Name} was saved with {Count} validation problem(s): {Errors}",
                spec.Name, errors.Count, string.Join("; ", errors));
        }

        return spec;
    }

    /// <summary>
    /// Applies an edit and bumps the generation. Takes a callback rather than a detached entity so
    /// a partial edit cannot silently blank the fields it did not mention.
    /// </summary>
    public async Task<ProvisionedCluster> UpdateAsync(
        Guid tenantId, Guid id, Action<ProvisionedCluster> edit, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ProvisionedCluster spec = await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cluster spec not found for this tenant.");

        edit(spec);
        spec.Generation++;
        spec.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return spec;
    }

    /// <summary>Adds a pool. The generation moves because the cluster's shape has changed.</summary>
    public async Task<ProvisionedWorkerPool> AddPoolAsync(
        Guid tenantId, Guid clusterId, ProvisionedWorkerPool pool, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ProvisionedCluster spec = await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cluster spec not found for this tenant.");

        if (spec.WorkerPools.Any(p => string.Equals(p.Name, pool.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"This cluster already has a pool called '{pool.Name}'. Pool names become resource names, "
                + "so two of them would collapse into one.");
        }

        pool.Id = pool.Id == Guid.Empty ? Guid.NewGuid() : pool.Id;
        pool.ProvisionedClusterId = clusterId;
        db.ProvisionedWorkerPools.Add(pool);

        spec.Generation++;
        spec.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return pool;
    }

    /// <summary>
    /// Removes a pool. Refuses the last one: a cluster whose only worker pool is gone has nowhere
    /// to run anything, and that is a mistake worth catching before the machines are deleted.
    /// </summary>
    public async Task RemovePoolAsync(Guid tenantId, Guid clusterId, Guid poolId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ProvisionedCluster spec = await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Cluster spec not found for this tenant.");

        ProvisionedWorkerPool? pool = spec.WorkerPools.FirstOrDefault(p => p.Id == poolId);
        if (pool is null)
        {
            return;
        }

        if (spec.WorkerPools.Count == 1)
        {
            throw new InvalidOperationException(
                "This is the cluster's only worker pool. Removing it would leave nowhere to schedule "
                + "workloads — add a replacement pool first.");
        }

        db.ProvisionedWorkerPools.Remove(pool);
        spec.Generation++;
        spec.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records what the reconciler saw, without touching the spec's own generation.</summary>
    public async Task RecordObservationAsync(
        Guid clusterId, int observedGeneration, string? observedStateJson, string? error, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ProvisionedCluster? spec = await db.ProvisionedClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        if (spec is null)
        {
            return;
        }

        spec.ObservedGeneration = observedGeneration;
        spec.ObservedStateJson = observedStateJson;
        spec.LastError = error;
        spec.LastReconciledAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    // ──────── Translation ────────

    /// <summary>
    /// Projects the spec into the shape provisioning and the manifest builder consume. One
    /// direction only, deliberately: the row is the source, and a config that could write back
    /// would give two places to change a cluster from.
    /// </summary>
    public static OpenStackProvisioningConfig ToConfig(ProvisionedCluster spec)
    {
        return new OpenStackProvisioningConfig
        {
            OpenStackConnectionId = spec.OpenStackConnectionId,
            ClusterName = spec.Name,
            KubernetesVersion = spec.KubernetesVersion,
            NodeImageName = spec.NodeImageName,
            BaseImageName = spec.BaseImageName,
            ControlPlaneCount = spec.ControlPlaneCount,
            ControlPlaneFlavor = spec.ControlPlaneFlavor,
            ControlPlaneDiskGb = spec.ControlPlaneDiskGb,
            ExternalNetworkId = spec.ExternalNetworkId,
            NodeNetworkId = spec.NetworkMode == ClusterNetworkMode.Existing ? spec.NodeNetworkId : null,
            PodCidr = spec.PodCidr,
            ServiceCidr = spec.ServiceCidr,
            DnsNameservers = spec.DnsNameservers,
            FailureDomain = spec.FailureDomain,
            BootstrapFlavor = spec.BootstrapFlavor,
            BootstrapNetworkId = spec.BootstrapNetworkId,
            BootstrapSshUser = spec.BootstrapSshUser,
            WorkerPools = spec.WorkerPools
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(ToPool)
                .ToList()
        };
    }

    public static WorkerPool ToPool(ProvisionedWorkerPool pool) => new()
    {
        Name = pool.Name,
        Count = pool.Count,
        Flavor = pool.Flavor,
        DiskGb = pool.DiskGb,
        FailureDomain = pool.FailureDomain,
        KubernetesVersion = pool.KubernetesVersion,
        MinCount = pool.MinCount,
        MaxCount = pool.MaxCount,
        Labels = Deserialize<Dictionary<string, string>>(pool.LabelsJson) ?? [],
        Taints = Deserialize<List<NodeTaint>>(pool.TaintsJson) ?? []
    };

    /// <summary>
    /// Builds a spec from the config a blueprint carried, so clusters authored before this existed
    /// can be operated rather than only rebuilt.
    /// </summary>
    public static (ProvisionedCluster Spec, List<ProvisionedWorkerPool> Pools) FromConfig(
        Guid tenantId, Guid environmentId, OpenStackProvisioningConfig config)
    {
        ProvisionedCluster spec = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            OpenStackConnectionId = config.OpenStackConnectionId,
            Name = config.ClusterName,
            KubernetesVersion = config.KubernetesVersion,
            NodeImageName = config.NodeImageName,
            BaseImageName = config.BaseImageName,
            ControlPlaneCount = config.ControlPlaneCount,
            ControlPlaneFlavor = config.ControlPlaneFlavor,
            ControlPlaneDiskGb = config.ControlPlaneDiskGb,
            ExternalNetworkId = config.ExternalNetworkId,
            NodeNetworkId = config.NodeNetworkId,
            NetworkMode = string.IsNullOrWhiteSpace(config.NodeNetworkId)
                ? ClusterNetworkMode.Managed
                : ClusterNetworkMode.Existing,
            PodCidr = config.PodCidr,
            ServiceCidr = config.ServiceCidr,
            DnsNameservers = config.DnsNameservers,
            FailureDomain = config.FailureDomain,
            BootstrapFlavor = config.BootstrapFlavor,
            BootstrapNetworkId = config.BootstrapNetworkId,
            BootstrapSshUser = config.BootstrapSshUser
        };

        List<ProvisionedWorkerPool> pools = config.WorkerPools.Select(p => new ProvisionedWorkerPool
        {
            Id = Guid.NewGuid(),
            ProvisionedClusterId = spec.Id,
            Name = p.Name,
            Flavor = p.Flavor,
            DiskGb = p.DiskGb,
            FailureDomain = p.FailureDomain,
            Count = p.Count,
            MinCount = p.MinCount,
            MaxCount = p.MaxCount,
            KubernetesVersion = p.KubernetesVersion,
            LabelsJson = p.Labels.Count > 0 ? JsonSerializer.Serialize(p.Labels, Json) : null,
            TaintsJson = p.Taints.Count > 0 ? JsonSerializer.Serialize(p.Taints, Json) : null
        }).ToList();

        return (spec, pools);
    }

    // ──────── Validation ────────

    /// <summary>
    /// Problems that would produce a cluster nobody wants, checked before the machines exist rather
    /// than after. Returns rather than throws: the caller decides whether a half-finished spec is
    /// allowed to be saved, and it usually is.
    /// </summary>
    public static IReadOnlyList<string> Validate(ProvisionedCluster spec, IReadOnlyList<ProvisionedWorkerPool> pools)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(spec.Name)) errors.Add("The cluster needs a name.");
        if (spec.ControlPlaneCount < 1) errors.Add("A cluster needs at least one control-plane node.");
        if (spec.ControlPlaneCount % 2 == 0)
            errors.Add("Control-plane count must be odd — etcd needs a majority to keep quorum.");
        if (string.IsNullOrWhiteSpace(spec.ControlPlaneFlavor)) errors.Add("The control plane needs a flavor.");
        if (string.IsNullOrWhiteSpace(spec.ExternalNetworkId)) errors.Add("An external network is required.");
        if (string.IsNullOrWhiteSpace(spec.NodeImageName) && string.IsNullOrWhiteSpace(spec.BaseImageName))
            errors.Add("Either a node image, or a base image to bake one from, is required.");
        if (spec.NetworkMode == ClusterNetworkMode.Existing && string.IsNullOrWhiteSpace(spec.NodeNetworkId))
            errors.Add("An existing-network cluster needs the network to use.");

        if (pools.Count == 0) errors.Add("A cluster needs at least one worker pool.");
        if (pools.Any(p => string.IsNullOrWhiteSpace(p.Flavor))) errors.Add("Every worker pool needs a flavor.");
        if (pools.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pools.Count)
            errors.Add("Worker pool names must be unique — they become resource names.");

        foreach (ProvisionedWorkerPool pool in pools.Where(p => p.MinCount is not null || p.MaxCount is not null))
        {
            if (pool.MinCount is null || pool.MaxCount is null)
            {
                errors.Add($"Pool '{pool.Name}' needs both a minimum and a maximum to autoscale.");
            }
            else if (pool.MaxCount < pool.MinCount)
            {
                errors.Add($"Pool '{pool.Name}' has a maximum below its minimum.");
            }
        }

        return errors;
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }
}
