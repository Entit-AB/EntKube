using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>The outcome of a day-2 operation, in the form a UI can show without interpreting it.</summary>
public sealed record ClusterOperationResult(bool Success, string Message)
{
    public static ClusterOperationResult Ok(string message) => new(true, message);
    public static ClusterOperationResult Failed(string message) => new(false, message);
}

/// <summary>
/// Day-2 operations on a provisioned cluster: scaling, adding and removing pools, reshaping them,
/// and moving versions.
///
/// <para><b>How each one works.</b> The spec is edited first and the cluster's own Cluster API
/// objects are changed second. That order matters: the spec is the record of intent, so if the
/// apply fails the cluster is left with a spec it has not caught up to — which the reconciler will
/// show as an unapplied generation and someone can retry. The other order loses the intent
/// entirely when the process dies between the two.</para>
///
/// <para>Everything routine goes through the cluster's own CAPI, which is running inside it. No
/// management plane is summoned for any of this — that is only needed for the two things a cluster
/// cannot do to itself, which are deleting itself and repairing a control plane that is already
/// down.</para>
/// </summary>
public class ClusterOperationsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ProvisionedClusterService specs,
    ClusterStateReader reader,
    IKubernetesClientFactory k8s,
    ILogger<ClusterOperationsService> logger)
{
    // ──────── Scale ────────

    /// <summary>
    /// Changes a pool's replica count. The cheapest operation there is: CAPI adds or removes
    /// machines and nothing existing is touched, so it is allowed even while something else is
    /// rolling.
    /// </summary>
    public async Task<ClusterOperationResult> ScalePoolAsync(
        Guid tenantId, Guid clusterId, string poolName, int replicas, CancellationToken ct = default)
    {
        if (replicas < 0)
        {
            return ClusterOperationResult.Failed("A pool cannot have a negative number of nodes.");
        }

        return await ApplyAsync(tenantId, clusterId, ClusterOperation.ScalePool, poolName, async context =>
        {
            ProvisionedWorkerPool pool = context.RequirePool(poolName);

            if (pool.MinCount is int min && replicas < min)
            {
                return ClusterOperationResult.Failed(
                    $"Pool '{poolName}' autoscales with a minimum of {min}. Scaling below that would be "
                    + "undone by the autoscaler within the minute — change the bounds instead.");
            }

            await specs.UpdateAsync(tenantId, clusterId, spec =>
            {
                ProvisionedWorkerPool target = spec.WorkerPools.First(p => p.Name == poolName);
                target.Count = replicas;
            }, ct);

            await PatchAsync(context, "machinedeployments.cluster.x-k8s.io",
                $"{context.Spec.Name}-{poolName}",
                $"{{\"spec\":{{\"replicas\":{replicas}}}}}", ct);

            return ClusterOperationResult.Ok($"Pool '{poolName}' is scaling to {replicas} node(s).");
        }, ct);
    }

    // ──────── Pools ────────

    /// <summary>Adds a pool. Additive, so it is allowed alongside a rollout.</summary>
    public async Task<ClusterOperationResult> AddPoolAsync(
        Guid tenantId, Guid clusterId, ProvisionedWorkerPool pool, CancellationToken ct = default)
    {
        return await ApplyAsync(tenantId, clusterId, ClusterOperation.AddPool, pool.Name, async context =>
        {
            await specs.AddPoolAsync(tenantId, clusterId, pool, ct);

            ProvisionedCluster refreshed = await RequireSpecAsync(tenantId, clusterId, ct);
            OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(refreshed);
            WorkerPool added = ProvisionedClusterService.ToPool(pool);

            await k8s.ApplyManifestAsync(
                CapiManifestBuilder.BuildPool(config, ManifestInputs(refreshed), added), context.Kubeconfig, ct);

            return ClusterOperationResult.Ok($"Pool '{pool.Name}' added with {pool.Count} node(s).");
        }, ct);
    }

    /// <summary>
    /// Removes a pool: deletes its MachineDeployment, which makes CAPI cordon, drain and delete
    /// every machine in it, then removes the templates behind it.
    ///
    /// <para>The spec refuses to remove the last pool, so a cluster is never left with nowhere to
    /// schedule anything.</para>
    /// </summary>
    public async Task<ClusterOperationResult> RemovePoolAsync(
        Guid tenantId, Guid clusterId, string poolName, CancellationToken ct = default)
    {
        return await ApplyAsync(tenantId, clusterId, ClusterOperation.RemovePool, poolName, async context =>
        {
            ProvisionedWorkerPool pool = context.RequirePool(poolName);
            string resourceName = $"{context.Spec.Name}-{poolName}";

            // The deployment first: it is what owns the machines, and CAPI drains them on its way
            // out. Removing the templates first would leave it unable to replace anything mid-drain.
            await k8s.DeleteManifestAsync("machinedeployment", resourceName,
                CapiManifestBuilder.Namespace, context.Kubeconfig, ct);
            await k8s.DeleteManifestAsync("kubeadmconfigtemplate", resourceName,
                CapiManifestBuilder.Namespace, context.Kubeconfig, ct);

            OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(context.Spec);
            string templateName = CapiManifestBuilder.MachineTemplateName(
                config, ProvisionedClusterService.ToPool(pool), ManifestInputs(context.Spec));

            await k8s.DeleteManifestAsync("openstackmachinetemplate", templateName,
                CapiManifestBuilder.Namespace, context.Kubeconfig, ct);

            await specs.RemovePoolAsync(tenantId, clusterId, pool.Id, ct);

            return ClusterOperationResult.Ok(
                $"Pool '{poolName}' is being drained and removed. Its machines go once their workloads have moved.");
        }, ct);
    }

    /// <summary>
    /// Changes a pool's machine shape — flavor, disk, or the image behind it. Every machine in the
    /// pool is replaced, so it is refused while anything else is in flight.
    /// </summary>
    public async Task<ClusterOperationResult> ReshapePoolAsync(
        Guid tenantId, Guid clusterId, string poolName, string flavor, int diskGb, CancellationToken ct = default)
    {
        return await ApplyAsync(tenantId, clusterId, ClusterOperation.ReshapePool, poolName, async context =>
        {
            ProvisionedWorkerPool pool = context.RequirePool(poolName);

            if (pool.Flavor == flavor && pool.DiskGb == diskGb)
            {
                return ClusterOperationResult.Ok($"Pool '{poolName}' already has that shape; nothing to do.");
            }

            await specs.UpdateAsync(tenantId, clusterId, spec =>
            {
                ProvisionedWorkerPool target = spec.WorkerPools.First(p => p.Name == poolName);
                target.Flavor = flavor;
                target.DiskGb = diskGb;
            }, ct);

            // A machine template is immutable to CAPI, so the new shape arrives as a new template
            // and the deployment is pointed at it. That repointing is what starts the rollout.
            ProvisionedCluster refreshed = await RequireSpecAsync(tenantId, clusterId, ct);
            OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(refreshed);
            WorkerPool reshaped = ProvisionedClusterService.ToPool(
                refreshed.WorkerPools.First(p => p.Name == poolName));

            await k8s.ApplyManifestAsync(
                CapiManifestBuilder.BuildPool(config, ManifestInputs(refreshed), reshaped), context.Kubeconfig, ct);

            return ClusterOperationResult.Ok(
                $"Pool '{poolName}' is rolling onto {flavor}. Machines are replaced a few at a time.");
        }, ct);
    }

    // ──────── Versions ────────

    /// <summary>
    /// Moves the control plane to a new Kubernetes version. CAPI replaces its machines one at a
    /// time; the pools follow afterwards, separately and deliberately.
    /// </summary>
    public async Task<ClusterOperationResult> UpgradeControlPlaneAsync(
        Guid tenantId, Guid clusterId, string targetVersion, CancellationToken ct = default)
    {
        return await ApplyAsync(tenantId, clusterId, ClusterOperation.UpgradeControlPlane, null, async context =>
        {
            string current = context.Observation.ControlPlaneVersion ?? context.Spec.KubernetesVersion;
            OperationVerdict verdict = ClusterUpgradeRules.CanUpgrade(current, targetVersion);
            if (!verdict.Allowed)
            {
                return ClusterOperationResult.Failed(verdict.Reason!);
            }

            string version = MachineImageNaming.Normalize(targetVersion);

            await specs.UpdateAsync(tenantId, clusterId, spec => spec.KubernetesVersion = version, ct);

            await PatchAsync(context, "kubeadmcontrolplanes.controlplane.cluster.x-k8s.io",
                $"{context.Spec.Name}-control-plane",
                $"{{\"spec\":{{\"version\":\"{version}\"}}}}", ct);

            return ClusterOperationResult.Ok(
                $"Control plane is upgrading to {version}, one node at a time. Upgrade the worker pools "
                + "once it has settled.");
        }, ct);
    }

    /// <summary>
    /// Moves one pool to a new version. Refused if it would put workers ahead of the control plane,
    /// which kubelet does not support and which presents as nodes that will not register.
    /// </summary>
    public async Task<ClusterOperationResult> UpgradePoolAsync(
        Guid tenantId, Guid clusterId, string poolName, string targetVersion, CancellationToken ct = default)
    {
        return await ApplyAsync(tenantId, clusterId, ClusterOperation.UpgradePool, poolName, async context =>
        {
            ProvisionedWorkerPool pool = context.RequirePool(poolName);

            string controlPlane = context.Observation.ControlPlaneVersion ?? context.Spec.KubernetesVersion;
            string poolCurrent = pool.KubernetesVersion ?? context.Spec.KubernetesVersion;

            OperationVerdict verdict = ClusterUpgradeRules.CanUpgradePool(controlPlane, poolCurrent, targetVersion);
            if (!verdict.Allowed)
            {
                return ClusterOperationResult.Failed(verdict.Reason!);
            }

            string version = MachineImageNaming.Normalize(targetVersion);

            await specs.UpdateAsync(tenantId, clusterId, spec =>
            {
                ProvisionedWorkerPool target = spec.WorkerPools.First(p => p.Name == poolName);
                target.KubernetesVersion = version;
            }, ct);

            await PatchAsync(context, "machinedeployments.cluster.x-k8s.io",
                $"{context.Spec.Name}-{poolName}",
                $"{{\"spec\":{{\"template\":{{\"spec\":{{\"version\":\"{version}\"}}}}}}}}", ct);

            return ClusterOperationResult.Ok($"Pool '{poolName}' is upgrading to {version}.");
        }, ct);
    }

    // ──────── Shared machinery ────────

    private sealed record OperationContext(
        ProvisionedCluster Spec, string Kubeconfig, ClusterObservation Observation)
    {
        public ProvisionedWorkerPool RequirePool(string name) =>
            Spec.WorkerPools.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This cluster has no pool called '{name}'.");
    }

    /// <summary>
    /// The shape every operation shares: load the spec and its kubeconfig, observe the cluster, ask
    /// whether the operation may start, then do it. The guard is here rather than in each operation
    /// so no future one can be added without it.
    /// </summary>
    private async Task<ClusterOperationResult> ApplyAsync(
        Guid tenantId,
        Guid clusterId,
        ClusterOperation operation,
        string? poolName,
        Func<OperationContext, Task<ClusterOperationResult>> act,
        CancellationToken ct)
    {
        try
        {
            ProvisionedCluster spec = await RequireSpecAsync(tenantId, clusterId, ct);

            if (spec.DesiredState != ProvisionedClusterState.Running)
            {
                return ClusterOperationResult.Failed(
                    $"This cluster is {spec.DesiredState.ToString().ToLowerInvariant()}, so it is not accepting changes.");
            }

            string? kubeconfig = await LoadKubeconfigAsync(spec, ct);
            if (string.IsNullOrWhiteSpace(kubeconfig))
            {
                return ClusterOperationResult.Failed(
                    "No kubeconfig is stored for this cluster yet, so there is nothing to apply against.");
            }

            ClusterObservation observation = await reader.ObserveAsync(spec.Name, kubeconfig, ct);
            OperationVerdict verdict = ClusterUpgradeRules.CanStart(observation, operation);
            if (!verdict.Allowed)
            {
                return ClusterOperationResult.Failed(verdict.Reason!);
            }

            ClusterOperationResult result = await act(new OperationContext(spec, kubeconfig, observation));

            logger.LogInformation(
                "{Operation} on cluster {Cluster}{Pool}: {Message}",
                operation, spec.Name, poolName is null ? "" : $" pool {poolName}", result.Message);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Operation} failed on cluster {ClusterId}", operation, clusterId);
            return ClusterOperationResult.Failed(ex.Message);
        }
    }

    private async Task PatchAsync(
        OperationContext context, string resource, string name, string patch, CancellationToken ct)
    {
        await k8s.PatchStrategicAsync(resource, name, CapiManifestBuilder.Namespace, patch, context.Kubeconfig, ct);
    }

    private async Task<ProvisionedCluster> RequireSpecAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
        => await specs.GetAsync(tenantId, clusterId, ct)
           ?? throw new InvalidOperationException("Cluster spec not found for this tenant.");

    private async Task<string?> LoadKubeconfigAsync(ProvisionedCluster spec, CancellationToken ct)
    {
        if (spec.KubernetesClusterId is not Guid clusterId)
        {
            return null;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KubernetesClusters
            .Where(c => c.Id == clusterId)
            .Select(c => c.Kubeconfig)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The cloud-shaped facts the manifests need. The API endpoint comes from the spec rather than
    /// being rediscovered: it was settled when the cluster was created, and changing it under a
    /// running cluster is not something a pool edit should do.
    /// </summary>
    private static ClusterManifestInputs ManifestInputs(ProvisionedCluster spec) => new()
    {
        NodeImageName = spec.NodeImageName,
        CloudSecretName = CapiTemplateInputs.CloudSecretName,
        CloudName = CapiTemplateInputs.CloudName,
        ApiEndpoint = spec.ApiEndpoint == ClusterApiEndpoint.Octavia
            ? ApiEndpointStrategy.Octavia
            : ApiEndpointStrategy.FloatingIp,
        ControlPlaneDiskGb = spec.ControlPlaneDiskGb
    };
}
