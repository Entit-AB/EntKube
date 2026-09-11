using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>What was found still standing after a teardown claimed to be finished.</summary>
public sealed record LeftoverResources(
    IReadOnlyList<string> Servers,
    IReadOnlyList<string> FloatingIps,
    IReadOnlyList<string> LoadBalancers,
    IReadOnlyList<string> Volumes)
{
    public bool Any => Servers.Count + FloatingIps.Count + LoadBalancers.Count + Volumes.Count > 0;

    public static LeftoverResources None { get; } = new([], [], [], []);

    public string Describe()
    {
        List<string> parts = [];
        if (Servers.Count > 0) parts.Add($"{Servers.Count} server(s): {string.Join(", ", Servers)}");
        if (FloatingIps.Count > 0) parts.Add($"{FloatingIps.Count} floating IP(s): {string.Join(", ", FloatingIps)}");
        if (LoadBalancers.Count > 0) parts.Add($"{LoadBalancers.Count} load balancer(s): {string.Join(", ", LoadBalancers)}");
        if (Volumes.Count > 0) parts.Add($"{Volumes.Count} volume(s): {string.Join(", ", Volumes)}");
        return parts.Count == 0 ? "nothing" : string.Join("; ", parts);
    }
}

/// <summary>
/// Deletes a provisioned cluster, and everything it created on the cloud with it.
///
/// <para><b>Why this needs a management plane.</b> A cluster that manages itself cannot delete
/// itself: the controllers that would tear down its OpenStack resources are running on the
/// machines being torn down. The moment the first control-plane node goes, nothing is left to
/// remove the load balancer, the floating IPs, the volumes or the security groups — which is
/// exactly how a "deleted" cluster keeps billing.</para>
///
/// <para>So a management plane is summoned: the same ephemeral kubeadm VM the initial bootstrap
/// uses, CAPO installed on it, the cluster's CAPI state moved out to it with
/// <c>clusterctl move</c>, the Cluster deleted there — CAPO then tears the cloud down properly —
/// and the VM destroyed. It exists for the length of one operation. That is the price of having no
/// permanent seed, and it is paid here rather than by every cluster all the time.</para>
///
/// <para>Afterwards the cloud is swept for anything still carrying the cluster's name, and what is
/// found is <b>reported, not deleted</b>. A sweep that deletes is a sweep that can delete the wrong
/// thing when a name is reused; a sweep that reports is a receipt.</para>
/// </summary>
public class ClusterTeardownService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ProvisionedClusterService specs,
    OpenStackKeystoneClient keystone,
    OpenStackComputeService compute,
    OpenStackDiscoveryService discovery,
    ClusterProvisioningService provisioning,
    ILogger<ClusterTeardownService> logger)
{
    /// <summary>
    /// Tears a cluster down. <paramref name="force"/> is the escape hatch for a cloud that cannot
    /// be reached at all: it forgets the cluster on EntKube's side and reports what will have to be
    /// removed by hand, rather than pretending the resources are gone.
    /// </summary>
    public async Task<ClusterOperationResult> DeleteAsync(
        Guid tenantId, Guid clusterId, bool force, Action<string> log, CancellationToken ct = default)
    {
        ProvisionedCluster? spec = await specs.GetAsync(tenantId, clusterId, ct);
        if (spec is null)
        {
            return ClusterOperationResult.Failed("Cluster spec not found for this tenant.");
        }

        // Recorded before anything is destroyed, so a crash mid-teardown resumes the delete rather
        // than letting the reconciler build the cluster back towards its spec.
        await specs.UpdateAsync(tenantId, clusterId, c => c.DesiredState = ProvisionedClusterState.Deleting, ct);

        KeystoneSession session;
        try
        {
            session = await keystone.AuthenticateAsync(tenantId, spec.OpenStackConnectionId, ct);
        }
        catch (Exception ex)
        {
            if (!force)
            {
                await specs.UpdateAsync(tenantId, clusterId, c => c.DesiredState = ProvisionedClusterState.Running, ct);
                return ClusterOperationResult.Failed(
                    $"The cloud could not be reached ({ex.Message}), so nothing can be deleted from it. "
                    + "Retry when it is back, or delete with force to forget the cluster here and clean "
                    + "the cloud up by hand.");
            }

            log("The cloud is unreachable. Forgetting the cluster on this side only.");
            await ForgetAsync(tenantId, clusterId, ct);
            return ClusterOperationResult.Ok(
                "Cluster forgotten. Its OpenStack resources were NOT deleted — they are still running and "
                + $"still billing. Look for anything named '{spec.Name}-*' in the project.");
        }

        try
        {
            log($"Summoning an ephemeral management plane to delete {spec.Name}…");
            await provisioning.DeleteProvisionedClusterAsync(tenantId, spec, session, log, ct);

            log("Sweeping the project for anything left behind…");
            LeftoverResources leftovers = await SweepAsync(session, spec.Name, ct);

            await ForgetAsync(tenantId, clusterId, ct);

            if (leftovers.Any)
            {
                // Reported rather than deleted: a sweep that deletes by name can delete the wrong
                // thing when a name has been reused, and that mistake is unrecoverable.
                logger.LogWarning(
                    "Teardown of {Cluster} left resources behind: {Leftovers}", spec.Name, leftovers.Describe());

                return ClusterOperationResult.Ok(
                    $"Cluster {spec.Name} deleted, but some resources are still present and were left alone "
                    + $"rather than guessed at: {leftovers.Describe()}. Check them before removing them — "
                    + "they are matched by name, and a name can be reused.");
            }

            return ClusterOperationResult.Ok($"Cluster {spec.Name} deleted. Nothing was left behind.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Teardown of cluster {Cluster} failed", spec.Name);

            // Deliberately left in Deleting: it is half-gone, and reconciling it back towards its
            // spec would rebuild machines whose siblings have already been destroyed.
            return ClusterOperationResult.Failed(
                $"Teardown failed partway through: {ex.Message}. The cluster is left marked for deletion "
                + "rather than repaired — retry the delete once the cause is dealt with.");
        }
    }

    /// <summary>
    /// Looks for anything in the project still carrying the cluster's name. Best-effort per
    /// service: an unreadable service means "cannot tell", and that is reported as nothing found
    /// rather than as an empty project.
    /// </summary>
    public async Task<LeftoverResources> SweepAsync(
        KeystoneSession session, string clusterName, CancellationToken ct = default)
    {
        string prefix = clusterName + "-";

        List<string> servers = await NamedResourcesAsync(session, "compute", "/servers", "servers", prefix, ct);
        List<string> loadBalancers = session.GetEndpoint("load-balancer") is null
            ? []
            : await NamedResourcesAsync(session, "load-balancer", "/v2.0/lbaas/loadbalancers", "loadbalancers", prefix, ct);
        List<string> volumes = await NamedResourcesAsync(session, "volumev3", "/volumes", "volumes", prefix, ct);

        // Floating IPs have no name to match on, so they are found by description — which CAPO
        // sets — and otherwise left out rather than guessed at from addresses.
        List<string> floatingIps = await NamedResourcesAsync(
            session, "network", "/v2.0/floatingips", "floatingips", prefix, ct, nameProperty: "description");

        return new LeftoverResources(servers, floatingIps, loadBalancers, volumes);
    }

    private async Task<List<string>> NamedResourcesAsync(
        KeystoneSession session, string service, string path, string collection, string prefix,
        CancellationToken ct, string nameProperty = "name")
    {
        try
        {
            return await discovery.ListNamedAsync(session, service, path, collection, nameProperty, prefix, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sweep {Service} for leftovers of {Prefix}", service, prefix);
            return [];
        }
    }

    /// <summary>
    /// Removes EntKube's record of the cluster. The registered cluster row is left alone — it is
    /// the operator's to remove, and unregistering is a separate decision from destroying.
    /// </summary>
    private async Task ForgetAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ProvisionedCluster? spec = await db.ProvisionedClusters
            .Include(c => c.WorkerPools)
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        if (spec is null)
        {
            return;
        }

        db.ProvisionedWorkerPools.RemoveRange(spec.WorkerPools);
        db.ProvisionedClusters.Remove(spec);
        await db.SaveChangesAsync(ct);
    }
}
