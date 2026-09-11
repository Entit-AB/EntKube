using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Keeps each provisioned cluster's spec and its actual Cluster API objects in agreement.
///
/// <para><b>Read-only for now, by design.</b> This pass observes and records; it does not yet
/// apply. The sequencing is deliberate — a controller that can change a production cluster should
/// first have demonstrated, against real clusters, that it reads them correctly. Acting on a
/// misread is how a reconciler scales a pool to zero because a status field it did not understand
/// came back empty. The apply half lands once this half has been watched.</para>
///
/// <para>Three rules the acting version inherits, worth stating before there is any code to break
/// them: an unreachable cluster is left alone rather than treated as absent; a cluster whose
/// DesiredState is Paused or Deleting is never reconciled towards its spec; and no more than one
/// destructive operation is ever in flight for a cluster.</para>
/// </summary>
public class ClusterReconciler(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ProvisionedClusterService specs,
    ClusterStateReader reader,
    ILogger<ClusterReconciler> logger)
{
    /// <summary>
    /// Observes one cluster and records what it saw. Returns the observation so a caller that
    /// wants it fresh — a page being opened, say — does not have to read it back.
    /// </summary>
    public async Task<ClusterObservation> ReconcileAsync(Guid provisionedClusterId, CancellationToken ct = default)
    {
        ProvisionedCluster? spec;
        string? kubeconfig;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            spec = await db.ProvisionedClusters
                .Include(c => c.WorkerPools)
                .FirstOrDefaultAsync(c => c.Id == provisionedClusterId, ct);

            if (spec is null)
            {
                return ClusterObservation.Unreachable("The cluster spec no longer exists.");
            }

            kubeconfig = spec.KubernetesClusterId is Guid clusterId
                ? await db.KubernetesClusters
                    .Where(c => c.Id == clusterId)
                    .Select(c => c.Kubeconfig)
                    .FirstOrDefaultAsync(ct)
                : null;
        }

        if (spec.DesiredState == ProvisionedClusterState.Paused)
        {
            // Paused means an operator is working on it. Reading is harmless; recording an
            // observation that makes the UI look like something is watching is not.
            return ClusterObservation.Unreachable("Reconciliation is paused for this cluster.");
        }

        if (string.IsNullOrWhiteSpace(kubeconfig))
        {
            // Still being built, or registered without credentials. Neither is a fault.
            ClusterObservation building = ClusterObservation.Unreachable(
                spec.KubernetesClusterId is null
                    ? "The cluster has not finished provisioning yet."
                    : "No kubeconfig is stored for this cluster.");

            await specs.RecordObservationAsync(spec.Id, spec.ObservedGeneration, building.ToJson(), null, ct);
            return building;
        }

        ClusterObservation observation = await reader.ObserveAsync(spec.Name, kubeconfig, ct);

        // An unreachable cluster teaches us nothing about whether the spec has been applied, so the
        // observed generation is left where it was rather than being claimed as current.
        int observedGeneration = observation.Health == ClusterHealth.Unreachable
            ? spec.ObservedGeneration
            : spec.Generation;

        string? error = observation.Health is ClusterHealth.Degraded or ClusterHealth.Unreachable
            ? observation.Summary
            : null;

        await specs.RecordObservationAsync(spec.Id, observedGeneration, observation.ToJson(), error, ct);

        if (observation.Health == ClusterHealth.Degraded)
        {
            logger.LogWarning("Cluster {Cluster} is degraded: {Summary}", spec.Name, observation.Summary);
        }

        return observation;
    }

    /// <summary>Observes every cluster that is supposed to be running. Skips paused and deleting ones.</summary>
    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        List<Guid> ids;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            ids = await db.ProvisionedClusters
                .Where(c => c.DesiredState == ProvisionedClusterState.Running)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        foreach (Guid id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReconcileAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable cluster must not stop the sweep — the others are still worth
                // knowing about, and this one will be tried again next pass.
                logger.LogWarning(ex, "Reconciling cluster {Id} failed; continuing with the rest", id);
            }
        }
    }
}

/// <summary>
/// Runs the observation sweep on an interval. Slow on purpose: this exists so the UI can show a
/// cluster's state without every page load reaching into it, not to detect failures quickly —
/// that is what the cluster's own MachineHealthCheck is for, and it acts in seconds without asking
/// anybody.
/// </summary>
public class ClusterReconcilerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ClusterReconcilerService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Long enough that migrations and the rest of startup are done first.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                ClusterReconciler reconciler = scope.ServiceProvider.GetRequiredService<ClusterReconciler>();
                await reconciler.ReconcileAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cluster reconciliation sweep failed; retrying next interval");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
