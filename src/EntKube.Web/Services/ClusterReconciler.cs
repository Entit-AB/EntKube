using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Keeps each provisioned cluster's spec and its actual Cluster API objects in agreement.
///
/// <para><b>What it will and will not do.</b> It converges the two things where a difference is
/// unambiguous and cheap to fix: a pool whose replica count has drifted from the spec, and a pool
/// in the spec that does not exist on the cluster. Both are additive or a single number, both are
/// safe to repeat, and both are the difference between an edit that was made and one that took.</para>
///
/// <para>It deliberately does <b>not</b> reshape, upgrade, or remove anything. Those replace
/// machines, and a controller that decides on its own to replace machines is one misread status
/// field away from rolling a production cluster at three in the morning. They stay operator-driven
/// through <see cref="ClusterOperationsService"/>, which is the same code path with a person
/// behind it. Drift of that kind is reported, not corrected.</para>
///
/// <para>Four rules, each of which exists because the opposite is a way to lose a cluster: an
/// unreachable cluster is left alone rather than treated as absent; a Paused or Deleting cluster is
/// never reconciled towards its spec; nothing is applied while something is already in flight; and
/// nothing that removes a machine is ever done without a person asking.</para>
/// </summary>
public class ClusterReconciler(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ProvisionedClusterService specs,
    ClusterStateReader reader,
    IKubernetesClientFactory k8s,
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

            // The whole entity, not a projection: Kubeconfig is [NotMapped] and is filled from
            // the vault by the materialization interceptor, which only runs when an entity is
            // materialized. Selecting the property alone cannot even be translated.
            kubeconfig = spec.KubernetesClusterId is Guid clusterId
                ? (await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct))?.Kubeconfig
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

        if (observation.Health != ClusterHealth.Unreachable)
        {
            IReadOnlyList<string> applied = await ConvergeAsync(spec, observation, kubeconfig, ct);
            if (applied.Count > 0)
            {
                logger.LogInformation(
                    "Cluster {Cluster}: brought {Count} difference(s) back to the spec — {Applied}",
                    spec.Name, applied.Count, string.Join("; ", applied));

                // Re-read rather than reporting the state that prompted the change: the counts that
                // were just applied are stale by definition.
                observation = await reader.ObserveAsync(spec.Name, kubeconfig, ct);
            }
        }

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

    /// <summary>
    /// Applies the differences that are safe to apply unattended, and returns what it did.
    ///
    /// <para>Only two kinds. A replica count that does not match the spec is one number and is
    /// idempotent. A pool in the spec with no MachineDeployment on the cluster is additive — it is
    /// what an edit that failed halfway leaves behind, and re-applying it is exactly the retry
    /// somebody would do by hand.</para>
    ///
    /// <para>Everything else is left to a person. A pool on the cluster that is missing from the
    /// spec looks like a removal that did not finish, and also looks exactly like a pool somebody
    /// added with kubectl — deleting it unattended gets that wrong in the expensive direction.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> ConvergeAsync(
        ProvisionedCluster spec, ClusterObservation observation, string kubeconfig, CancellationToken ct)
    {
        List<string> applied = [];

        // Nothing is applied on top of work already in flight, for the same reason day-2 refuses:
        // a second change during a rollout replaces more machines than either intended.
        if (observation.Health != ClusterHealth.Healthy)
        {
            return applied;
        }

        foreach (ProvisionedWorkerPool pool in spec.WorkerPools)
        {
            ct.ThrowIfCancellationRequested();

            PoolObservation? actual = observation.Pools
                .FirstOrDefault(p => string.Equals(p.Name, pool.Name, StringComparison.Ordinal));

            if (actual is null)
            {
                // The spec has a pool the cluster does not. Applying it is additive and is the
                // retry a person would perform after an edit that failed partway.
                await ApplyMissingPoolAsync(spec, pool, kubeconfig, ct);
                applied.Add($"created pool {pool.Name}");
                continue;
            }

            // An autoscaled pool's replica count belongs to the autoscaler, not to the spec.
            // Correcting it here would fight the autoscaler every five minutes.
            if (pool.Autoscale)
            {
                continue;
            }

            if (actual.Desired != pool.Count)
            {
                await k8s.PatchStrategicAsync(
                    "machinedeployments.cluster.x-k8s.io",
                    $"{spec.Name}-{pool.Name}",
                    CapiManifestBuilder.Namespace,
                    // Built with concatenation: a raw literal cannot end on the closing braces this
                    // JSON needs, which is the third time that has bitten in this feature.
                    "{\"spec\":{\"replicas\":" + pool.Count + "}}",
                    kubeconfig, ct);

                applied.Add($"pool {pool.Name} {actual.Desired} → {pool.Count}");
            }
        }

        return applied;
    }

    private async Task ApplyMissingPoolAsync(
        ProvisionedCluster spec, ProvisionedWorkerPool pool, string kubeconfig, CancellationToken ct)
    {
        OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(spec);

        ClusterManifestInputs inputs = new()
        {
            NodeImageName = spec.NodeImageName,
            CloudSecretName = CapiTemplateInputs.CloudSecretName,
            CloudName = CapiTemplateInputs.CloudName,
            ApiEndpoint = spec.ApiEndpoint == ClusterApiEndpoint.Octavia
                ? ApiEndpointStrategy.Octavia
                : ApiEndpointStrategy.FloatingIp,
            ControlPlaneDiskGb = spec.ControlPlaneDiskGb
        };

        await k8s.ApplyManifestAsync(
            CapiManifestBuilder.BuildPool(config, inputs, ProvisionedClusterService.ToPool(pool)), kubeconfig, ct);
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
