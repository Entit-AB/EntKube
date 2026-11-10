using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace EntKube.Web.Services;

/// <summary>
/// A snapshot of a Kubernetes pod's state — name, status, container count,
/// restarts, age. Rendered in the portal's deployment detail view.
/// </summary>
public class PodInfo
{
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public required string Status { get; set; }
    public int ReadyContainers { get; set; }
    public int TotalContainers { get; set; }
    public int Restarts { get; set; }
    public DateTime? StartTime { get; set; }
    public List<ContainerInfo> Containers { get; set; } = [];
}

/// <summary>
/// Per-container state within a pod. Lets the user pick which container
/// to view logs for in multi-container pods.
/// </summary>
public class ContainerInfo
{
    public required string Name { get; set; }
    public required string Image { get; set; }
    public bool Ready { get; set; }
    public int RestartCount { get; set; }
    public string? State { get; set; }
}

/// A Kubernetes Service port entry (name optional, port number, protocol).
/// <summary>
/// A port on a Kubernetes Service. <paramref name="AppProtocol"/> carries the Service's
/// appProtocol field — with the port name it is how we tell a TLS-serving port from a
/// plaintext one when configuring how the gateway connects to the backend.
/// </summary>
public record KubeServicePort(string? Name, int Port, string Protocol, string? AppProtocol = null);

/// <summary>A Kubernetes Service with its exposed ports.</summary>
public record KubeServiceInfo(string Name, string Type, string? ClusterIP, List<KubeServicePort> Ports);

/// <summary>Ready and not-ready endpoint IP:port strings for a Service.</summary>
public record KubeEndpointSummary(List<string> Ready, List<string> NotReady)
{
    public int TotalReady => Ready.Count;
    public int TotalNotReady => NotReady.Count;
    public bool HasEndpoints => Ready.Count + NotReady.Count > 0;
}

/// <summary>
/// Performs live Kubernetes cluster operations for the customer portal —
/// listing pods, viewing logs, restarting deployments, deleting pods, and
/// scaling. Uses the stored kubeconfig from KubernetesCluster to authenticate.
///
/// Each operation:
/// 1. Loads the deployment to find which cluster it targets
/// 2. Reads the cluster's kubeconfig
/// 3. Creates a K8s client and performs the operation
/// 4. Returns a Result so the UI can show success/failure gracefully
/// </summary>
public class KubernetesOperationsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuditService auditService,
    KyvernoPolicyService kyvernoPolicyService,
    IClusterChangeGate gate,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    EntKube.Web.Services.Rollouts.IRolloutStarter rollouts,
    ILogger<KubernetesOperationsService> logger,
    // Optional: used only to read the client certificate for outbound mTLS. Absent in unit tests,
    // which construct this service without a vault.
    VaultService? vault = null)
{
    /// <summary>
    /// Requires operator acknowledgment (interactive scopes only) for a mutating cluster op that
    /// this service performs directly via the k8s SDK or its own kubectl. Throws
    /// <see cref="OperationCanceledException"/> if the operator cancels — callers surface that as a failure.
    /// </summary>
    private Task RequireAckAsync(
        ChangeVerb verb, string kubeconfig, string clusterLabel, string? ns,
        string summary, string? patch = null, CancellationToken ct = default)
        => gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = verb,
            Kubeconfig = kubeconfig,
            ClusterLabel = string.IsNullOrWhiteSpace(clusterLabel) ? "cluster" : clusterLabel,
            Namespace = ns,
            Summary = summary,
            Patch = patch,
        }, ct);

    /// <summary>
    /// Applies the tenant+environment's enabled Kyverno policies to a deployment's namespace.
    /// Runs after a successful deploy so a newly-created customer app namespace inherits the same
    /// admission policies as the rest of the environment. Best-effort: failures are logged but never
    /// fail the deployment itself, and a cluster without Kyverno simply has the manifests rejected.
    /// </summary>
    private async Task ApplyKyvernoPoliciesAsync(AppDeployment deployment, CancellationToken ct)
    {
        try
        {
            if (deployment.Cluster is null)
                return;

            Guid tenantId;
            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                tenantId = await db.Apps
                    .Where(a => a.Id == deployment.AppId)
                    .Select(a => a.Customer.TenantId)
                    .FirstOrDefaultAsync(ct);
            }

            if (tenantId == Guid.Empty) return;

            // Rewrite the cluster-scoped policy first. Its deny-list is a literal namespace list,
            // so a namespace created by this very deploy is absent from whatever was applied
            // before — and stays uncovered until something rewrites it.
            (bool cpOk, string cpOutput) = await kyvernoPolicyService
                .ReconcileClusterRbacPolicyAsync(tenantId, deployment.ClusterId, ct);
            if (!cpOk && !string.IsNullOrWhiteSpace(cpOutput))
                logger.LogWarning(
                    "Cluster RBAC policy reconcile failed on {Cluster} after deployment {DeploymentId}: {Output}",
                    deployment.Cluster.Name, deployment.Id, cpOutput);

            List<KyvernoPolicy> policies =
                await kyvernoPolicyService.GetPoliciesAsync(tenantId, deployment.EnvironmentId, ct);
            if (policies.Count == 0) return;

            // The tenant is derived from the deployment's app's customer a few lines up, so this
            // is a cross-row check rather than a caller's word — but it is a check: a deployment
            // naming a cluster outside its own tenant now resolves to nothing instead of having
            // that cluster's credential read off the navigation property.
            (bool ok, string output) = await kyvernoPolicyService.ApplyToNamespaceAsync(
                policies, tenantId, deployment.ClusterId, deployment.Namespace, ct);

            if (ok)
                logger.LogInformation(
                    "Applied {Count} Kyverno policies to namespace {Namespace} for deployment {DeploymentId}",
                    policies.Count, deployment.Namespace, deployment.Id);
            else
                logger.LogWarning(
                    "Kyverno policy apply to namespace {Namespace} for deployment {DeploymentId} reported errors: {Output}",
                    deployment.Namespace, deployment.Id, output);
        }
        catch (Exception ex)
        {
            // Never let policy application break a deployment.
            logger.LogWarning(ex,
                "Failed to apply Kyverno policies to namespace {Namespace} for deployment {DeploymentId}",
                deployment.Namespace, deployment.Id);
        }
    }

    /// <summary>
    /// Re-applies the external routes EntKube owns for a deployment, immediately after a Helm
    /// release has landed. A chart upgrade replaces every Deployment and Service in the namespace
    /// at once, and we have repeatedly seen the gateway come out the other side with no virtual
    /// host for the app's hostname — TLS still terminates, but Envoy answers a bare 404 for the
    /// whole domain until an operator re-applies the route by hand from the External Access panel.
    /// Nothing on the deploy path deletes the HTTPRoute, so this is not us undoing our own work;
    /// re-applying is simply the known-good remedy, and it should not need a human.
    ///
    /// Re-applying also refreshes the per-backend DestinationRules, which are generated from the
    /// live Service ports at apply time. If the new chart version changed a Service's ports, the
    /// old rule would otherwise sit there stale until someone noticed the 503s.
    ///
    /// Ownership does not move: the route still comes from EntKube's database and is still applied
    /// by EntKube. Best-effort, exactly like the Kyverno step above — the release is already
    /// installed by the time we get here, so a route failure is logged and never fails the deploy.
    ///
    /// Returns a short note for the operator-facing output, or null if there was nothing to do.
    /// </summary>
    protected virtual async Task<string?> RefreshDeploymentRoutesAsync(
        Guid tenantId, AppDeployment deployment, CancellationToken ct)
    {
        try
        {
            // First, work out which routes to refresh. We want every route that belongs to this
            // deployment, is switched on, and is one EntKube actually manages — an observed-only
            // route (imported, owned by ArgoCD or Flux) is somebody else's to apply, and quietly
            // taking it over here would be exactly the ownership violation we are trying to avoid.
            List<(Guid Id, string Hostname)> candidates;

            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                candidates = (await db.AppDeploymentRoutes
                        .Where(r => r.AppDeploymentId == deployment.Id
                                 && r.IsEnabled
                                 && r.AppRoute.IsManaged)
                        .OrderBy(r => r.PathPrefix)
                        .Select(r => new { r.Id, r.AppRoute.Hostname })
                        .ToListAsync(ct))
                    .Select(r => (r.Id, r.Hostname))
                    .ToList();
            }

            // Now collapse them down to one route per hostname. A single apply rebuilds *every*
            // rule on the hostname's HTTPRoute — it queries all the enabled routes sharing that
            // hostname itself — so calling it once per route would redo identical work and churn
            // the Gateway N times over. The path ordering above just makes the pick deterministic.
            List<(Guid Id, string Hostname)> perHostname = candidates
                .GroupBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // A deployment with no managed routes is the common case for internal-only apps.
            // There is nothing to say and nothing to warn about; we just leave.
            if (perHostname.Count == 0)
                return null;

            // Apply each hostname in turn. A failure here is information for the operator, not a
            // reason to stop — the remaining hostnames are independent and still deserve a try.
            List<string> refreshed = [];
            int failed = 0;

            foreach ((Guid routeId, string hostname) in perHostname)
            {
                KubernetesOperationResult<string> routeResult =
                await ApplyDeploymentRouteAsync(tenantId, routeId, ct);

                if (routeResult.IsSuccess)
                {
                    refreshed.Add(hostname);
                    logger.LogInformation(
                        "Refreshed route {Hostname} after Helm release for deployment {DeploymentId}",
                        hostname, deployment.Id);
                }
                else
                {
                    failed++;
                    logger.LogWarning(
                        "Route refresh for {Hostname} after Helm release for deployment {DeploymentId} failed: {Error}",
                        hostname, deployment.Id, routeResult.Error);
                }
            }

            // Finally, hand back a one-line summary so the operator watching the deploy output can
            // see that the routes were reconciled — and can see it when one of them was not.
            string note = refreshed.Count > 0
                ? $"Refreshed {refreshed.Count} route(s): {string.Join(", ", refreshed)}"
                : "No routes were refreshed";

            return failed > 0 ? $"{note} ({failed} route refresh(es) failed — see logs)" : note;
        }
        catch (Exception ex)
        {
            // The Helm release has already succeeded. Whatever went wrong reconciling routes —
            // an unreachable cluster, a cancelled acknowledgment — must not turn a good deploy
            // into a failed one. We record it and let the caller report success.
            logger.LogWarning(ex,
                "Failed to refresh routes after Helm release for deployment {DeploymentId}",
                deployment.Id);
            return null;
        }
    }

    /// <summary>
    /// Loads a deployment with its cluster attached, so we know where
    /// to connect for Kubernetes API calls.
    /// </summary>
    public async Task<AppDeployment?> GetDeploymentWithClusterAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.AppDeployments
            .Include(d => d.Cluster)
            .Include(d => d.Environment)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
    }

    /// <summary>A deployment and a client for the cluster it is on.</summary>
    private sealed record DeploymentOnCluster(
        AppDeployment Deployment, EntKube.Web.Services.Clusters.IClusterClient Cluster);

    /// <summary>
    /// The replacement for this file's prologue: load the deployment, then ask the seam for its
    /// cluster <em>as this tenant</em>.
    ///
    /// <para>What it replaces was <c>GetDeploymentWithClusterAsync</c> followed by a
    /// <c>deployment.Cluster.Kubeconfig</c> check — twenty-one methods doing the same four lines,
    /// none of them with a tenant, so the deployment id was the whole authorization and the
    /// credential came out of the row it named. The <c>Include(d =&gt; d.Cluster)</c> also
    /// decrypted a kubeconfig through the vault interceptor for methods that only wanted a
    /// namespace.</para>
    ///
    /// <para>Methods move onto this one at a time; the old prologue stays until the last of them
    /// does, which is how <c>PrometheusService</c>'s twenty were converted without a step that
    /// did not compile.</para>
    /// </summary>
    private async Task<(DeploymentOnCluster? Target, string? Error)> ResolveTargetAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct)
    {
        AppDeployment? deployment;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            // Scoped at the row as well as at the seam. The ForAsync call below already refuses
            // a cluster that is not this tenant's, which made this predicate redundant rather
            // than exploitable — but an unscoped read still loads a stranger's deployment to
            // decide what to say about it, and a refactor that moves the cluster call is one
            // edit from a hole. AppDeployment has no TenantId; the scope runs through its app.
            deployment = await db.AppDeployments
                .FirstOrDefaultAsync(
                    d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);
        }

        if (deployment is null)
        {
            // One message for both cases on purpose: the row-scoped lookup above cannot tell an
            // absent deployment from one that belongs to somebody else, and it must not — saying
            // which would leak whether a stranger's row exists. The wording keeps the phrase the
            // six existing tenancy tests assert on, which is what pins this as a refusal.
            return (null, "Deployment not found, or it is not this tenant's.");
        }

        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterClients.ForAsync(tenantId, deployment.ClusterId, ct);

        return cluster is null
            ? (null, "The deployment's cluster is not this tenant's, or has no kubeconfig configured.")
            : (new DeploymentOnCluster(deployment, cluster), null);
    }

    /// <summary>
    /// Lists the namespace names on a cluster directly from its Kubernetes API. Used to
    /// populate the log browser's namespace picker so it works even when no logs have yet
    /// been discovered in the telemetry / Loki backend for the cluster.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> ListNamespacesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        // No cluster row read at all now: the seam answers "may this tenant hold that cluster's
        // credential", which is the same question this method used to answer by loading the row
        // and looking at a column.
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterClients.ForAsync(tenantId, clusterId, ct);

        if (cluster is null)
        {
            return KubernetesOperationResult<List<string>>.Failure(
                "Cluster not found for this tenant, or it has no kubeconfig configured.");
        }

        try
        {
            // Not disposed: pooled per credential and shared. See ResolveTargetAsync.
            Kubernetes client = cluster.GetPooledSdkClient();
            V1NamespaceList list = await client.CoreV1.ListNamespaceAsync(cancellationToken: ct);
            List<string> names = list.Items
                .Select(n => n.Metadata?.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return KubernetesOperationResult<List<string>>.Success(names);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<string>>.Failure($"Could not list namespaces: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the governance-locked namespace for a deployment's app+environment,
    /// or null if no namespace lock is configured.
    /// </summary>
    private async Task<string?> GetGovernanceNamespaceAsync(
        AppDeployment deployment, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e =>
                e.AppId == deployment.AppId &&
                e.EnvironmentId == deployment.EnvironmentId, ct);
        return ae?.Namespace;
    }

    /// <summary>
    /// Checks that a deployment's namespace matches the governance lock (if any),
    /// and that no manifest document contains an explicit namespace pointing elsewhere.
    /// Returns a non-null error string if the check fails.
    /// </summary>
    private static string? CheckNamespaceGovernance(
        string deploymentNamespace, string? lockedNamespace, IEnumerable<string>? manifestContents = null)
    {
        if (string.IsNullOrEmpty(lockedNamespace)) return null;

        if (!string.Equals(deploymentNamespace.Trim(), lockedNamespace, StringComparison.OrdinalIgnoreCase))
            return $"Namespace governance violation: deployment targets namespace '{deploymentNamespace}' " +
                   $"but governance policy locks this environment to '{lockedNamespace}'.";

        if (manifestContents is null) return null;

        // Scan each manifest document for explicit metadata.namespace fields that
        // differ from the locked namespace. We look for lines indented at exactly
        // 2 spaces (the standard YAML indentation for top-level metadata fields).
        foreach (string content in manifestContents)
        {
            foreach (string line in content.Split('\n'))
            {
                // Match "  namespace: <value>" — metadata-level namespace field.
                // Skip kind: Namespace documents (the name field, not a target namespace).
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("namespace:", StringComparison.Ordinal)
                    && line.Length > trimmed.Length  // has leading whitespace
                    && (line.Length - trimmed.Length) == 2) // exactly 2 spaces indent
                {
                    string value = trimmed["namespace:".Length..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(value)
                        && !string.Equals(value, lockedNamespace, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"Namespace governance violation: manifest contains 'namespace: {value}' " +
                               $"but governance policy locks this environment to '{lockedNamespace}'.";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Retrieves log output from a specific pod. Optionally targets a
    /// specific container in multi-container pods. Returns the last
    /// tailLines lines to keep output manageable.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> GetPodLogsAsync(
        Guid tenantId,
        Guid deploymentId,
        string podName,
        string? containerName = null,
        int tailLines = 500,
        CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null)
        {
            return KubernetesOperationResult<string>.Failure(error!);
        }

        AppDeployment deployment = target.Deployment;

        try
        {
            Kubernetes client = target.Cluster.GetPooledSdkClient();

            // Fetch the last N lines of logs from the pod.
            using Stream logStream = await client.CoreV1.ReadNamespacedPodLogAsync(
                podName,
                deployment.Namespace,
                container: containerName,
                tailLines: tailLines,
                cancellationToken: ct);

            using StreamReader reader = new(logStream);
            string logs = await reader.ReadToEndAsync(ct);

            return KubernetesOperationResult<string>.Success(logs);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<string>.Failure($"Failed to fetch logs: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts a Kubernetes Deployment by patching its pod template annotation
    /// with the current timestamp. This triggers a rolling restart — Kubernetes
    /// creates new pods and terminates old ones gradually.
    /// </summary>
    public async Task<KubernetesOperationResult> RestartDeploymentAsync(
        Guid tenantId,
        Guid deploymentId,
        string k8sDeploymentName,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult.Failure(error!);

        AppDeployment deployment = target.Deployment;

        try
        {
            await target.Cluster.AcknowledgeAsync(new PlannedChange
            {
                Verb = ChangeVerb.Restart,
                Resource = "deployment",
                Name = k8sDeploymentName,
                Namespace = deployment.Namespace,
                Summary = $"Restart Deployment/{k8sDeploymentName}",
            }, ct);

            Kubernetes client = target.Cluster.GetPooledSdkClient();

            // Patching the pod template annotation forces a rollout restart,
            // identical to "kubectl rollout restart deployment/<name>".
            V1Patch patch = new(
                "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":" +
                $"{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}" +
                "}}}}",
                V1Patch.PatchType.MergePatch);

            await client.AppsV1.PatchNamespacedDeploymentAsync(
                patch, k8sDeploymentName, deployment.Namespace, cancellationToken: ct);

            logger.LogInformation(
                "Deployment {K8sName} in {Namespace} restarted by {User}",
                k8sDeploymentName, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "RestartDeployment", "Deployment",
                k8sDeploymentName, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to restart deployment {K8sName} in {Namespace}",
                k8sDeploymentName, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Failed to restart deployment: {ex.Message}");
        }
    }

    // ──────── Resource tree actions ────────

    /// <summary>
    /// Returns the container names for a specific pod — used to populate the
    /// container picker in the log viewer for multi-container pods.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetPodContainersAsync(
        Guid tenantId, Guid deploymentId, string podName, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null)
            return KubernetesOperationResult<List<string>>.Failure(error!);

        try
        {
            // Not disposed: the client is pooled per credential and shared, so a `using` here
            // would tear down a connection other callers are mid-request on.
            Kubernetes client = target.Cluster.GetPooledSdkClient();
            V1Pod pod = await client.CoreV1.ReadNamespacedPodAsync(
                podName, target.Deployment.Namespace, cancellationToken: ct);
            List<string> containers = pod.Spec?.Containers?
                .Select(c => c.Name).ToList() ?? [];
            return KubernetesOperationResult<List<string>>.Success(containers);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<string>>.Failure(
                $"Failed to read pod: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves recent Kubernetes Events for a named resource. Events surface
    /// scheduling failures, image pull errors, and readiness probe results —
    /// the same information shown by "kubectl describe".
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetResourceEventsAsync(
        Guid tenantId, Guid deploymentId, string kind, string name, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null)
            return KubernetesOperationResult<List<string>>.Failure(error!);

        try
        {
            Kubernetes client = target.Cluster.GetPooledSdkClient();
            var events = await client.CoreV1.ListNamespacedEventAsync(
                target.Deployment.Namespace, cancellationToken: ct);

            List<string> lines = events.Items
                .Where(e => e.InvolvedObject?.Name == name
                         && e.InvolvedObject?.Kind == kind)
                .OrderBy(e => e.LastTimestamp ?? e.EventTime)
                .Select(e =>
                {
                    string ts = (e.LastTimestamp ?? e.EventTime)?.ToString("HH:mm:ss") ?? "?";
                    string type = e.Type ?? "Normal";
                    string reason = e.Reason ?? "";
                    string msg = e.Message ?? "";
                    return $"{ts}  {type,-8}  {reason,-24}  {msg}";
                })
                .ToList();

            return KubernetesOperationResult<List<string>>.Success(lines);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<string>>.Failure(
                $"Failed to list events: {ex.Message}");
        }
    }

    /// <summary>
    /// Scales a workload (Deployment, StatefulSet, or ReplicaSet) to the specified
    /// replica count. Unified entry point so callers don't branch on kind.
    /// </summary>
    public async Task<KubernetesOperationResult> ScaleWorkloadAsync(
        Guid tenantId, Guid deploymentId, string kind, string name, int replicas,
        string? performedBy = null, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult.Failure(error!);

        AppDeployment deployment = target.Deployment;

        try
        {
            await target.Cluster.AcknowledgeAsync(new PlannedChange
            {
                Verb = ChangeVerb.Scale,
                Namespace = deployment.Namespace,
                Summary = $"Scale {kind}/{name} to {replicas} replica(s)",
                Patch = $"spec.replicas = {replicas}",
            }, ct);

            Kubernetes client = target.Cluster.GetPooledSdkClient();
            V1Patch patch = new(
                $"{{\"spec\":{{\"replicas\":{replicas}}}}}",
                V1Patch.PatchType.MergePatch);

            switch (kind)
            {
                case "Deployment":
                    await client.AppsV1.PatchNamespacedDeploymentAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "StatefulSet":
                    await client.AppsV1.PatchNamespacedStatefulSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "ReplicaSet":
                    await client.AppsV1.PatchNamespacedReplicaSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                default:
                    return KubernetesOperationResult.Failure($"Cannot scale {kind}.");
            }

            logger.LogInformation("{Kind} {Name} scaled to {Replicas} in {Namespace} by {User}",
                kind, name, replicas, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "ScaleWorkload", kind,
                name, $"replicas={replicas}", performedBy, ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scale failed for {Kind} {Name} in {Namespace}", kind, name, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Scale failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts a workload (Deployment, StatefulSet, or DaemonSet) by patching
    /// the pod-template annotation — identical to "kubectl rollout restart".
    /// </summary>
    public async Task<KubernetesOperationResult> RestartWorkloadAsync(
        Guid tenantId, Guid deploymentId, string kind, string name,
        string? performedBy = null, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult.Failure(error!);

        AppDeployment deployment = target.Deployment;

        try
        {
            await target.Cluster.AcknowledgeAsync(new PlannedChange
            {
                Verb = ChangeVerb.Restart,
                Namespace = deployment.Namespace,
                Summary = $"Restart {kind}/{name}",
            }, ct);

            Kubernetes client = target.Cluster.GetPooledSdkClient();
            V1Patch patch = new(
                "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":" +
                $"{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}" +
                "}}}}",
                V1Patch.PatchType.MergePatch);

            switch (kind)
            {
                case "Deployment":
                    await client.AppsV1.PatchNamespacedDeploymentAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "StatefulSet":
                    await client.AppsV1.PatchNamespacedStatefulSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "DaemonSet":
                    await client.AppsV1.PatchNamespacedDaemonSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                default:
                    return KubernetesOperationResult.Failure($"Cannot restart {kind}.");
            }

            logger.LogInformation("{Kind} {Name} restarted in {Namespace} by {User}",
                kind, name, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "RestartWorkload", kind,
                name, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restart failed for {Kind} {Name} in {Namespace}", kind, name, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Restart failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a resource by kind — Pod (triggers replacement) or Job.
    /// </summary>
    public async Task<KubernetesOperationResult> DeleteResourceAsync(
        Guid tenantId, Guid deploymentId, string kind, string name,
        string? performedBy = null, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult.Failure(error!);

        AppDeployment deployment = target.Deployment;

        try
        {
            await target.Cluster.AcknowledgeAsync(new PlannedChange
            {
                Verb = ChangeVerb.Delete,
                Kind = kind,
                Name = name,
                Namespace = deployment.Namespace,
                Summary = $"Delete {kind}/{name}",
            }, ct);

            Kubernetes client = target.Cluster.GetPooledSdkClient();

            switch (kind)
            {
                case "Pod":
                    await client.CoreV1.DeleteNamespacedPodAsync(
                        name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "Job":
                    await client.BatchV1.DeleteNamespacedJobAsync(
                        name, deployment.Namespace,
                        body: new V1DeleteOptions { PropagationPolicy = "Background" },
                        cancellationToken: ct);
                    break;
                case "ReplicaSet":
                    await client.AppsV1.DeleteNamespacedReplicaSetAsync(
                        name, deployment.Namespace,
                        body: new V1DeleteOptions { PropagationPolicy = "Background" },
                        cancellationToken: ct);
                    break;
                default:
                    return KubernetesOperationResult.Failure($"Delete not supported for {kind}.");
            }

            logger.LogInformation("{Kind} {Name} deleted in {Namespace} by {User}",
                kind, name, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, $"Delete{kind}", kind,
                name, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delete failed for {Kind} {Name} in {Namespace}", kind, name, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Delete failed: {ex.Message}");
        }
    }

    // ──────── Deployment-level cluster deletion ────────

    /// <summary>
    /// Removes a deployment's resources from the cluster without touching the database.
    /// For HelmChart/GitHelm deployments this runs "helm uninstall"; for Yaml/Manual/GitSync
    /// deployments it runs "kubectl delete -f" against the stored manifests. Callers should
    /// delete the DB record separately once this succeeds (or ignore the cluster result for
    /// an unregister-only flow).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DeleteDeploymentFromClusterAsync(
        Guid tenantId, Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found, or it is not this tenant's.");

        // The tenant is the caller's, not one read off the row being authorized. The old form —
        // ForAsync(deployment.Cluster.TenantId, deployment.ClusterId) — compared a row against
        // itself, so the predicate could not fail; see VacuousTenantCheckTests.
        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, deployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The deployment's cluster is not this tenant's, or has no kubeconfig configured.");

        // Removing a deployment is one decision, not one per resource: a Yaml deployment deletes
        // every manifest it owns, and each delete is separately gated.
        using IDisposable operation = gate.BeginOperation(
            $"remove {deployment.Name} from {deployment.Namespace}");

        try
        {
            HelmExecutionResult result;

            if (deployment.Type is DeploymentType.HelmChart or DeploymentType.GitHelm)
            {
                string releaseName = ToHelmReleaseName(deployment.Name);

                result = await clusterAccess.RunHelmAsync(new HelmInvocation(
                    Verb: "uninstall",
                    ReleaseName: releaseName,
                    Namespace: deployment.Namespace,
                    Arguments: [releaseName, "--namespace", deployment.Namespace],
                    Summary: $"helm uninstall {releaseName} (removes all release resources)"),
                    ct);
            }
            else
            {
                // Load stored manifests ordered by sort order.
                using ApplicationDbContext db = dbFactory.CreateDbContext();
                List<DeploymentManifest> manifests = await db.DeploymentManifests
                    .Where(m => m.DeploymentId == deploymentId)
                    .OrderBy(m => m.SortOrder)
                    .ToListAsync(ct);

                if (manifests.Count == 0)
                    return KubernetesOperationResult<string>.Failure(
                        "No manifests found. Resources may have been applied outside of EntKube and must be removed manually.");

                string combined = string.Join("\n---\n", manifests.Select(m => m.YamlContent));

                // The acknowledgment is raised by the seam, which carries the manifest — a
                // composite delete has no single kind and name, so the set itself is the preview.
                // The summary this used to pass by hand said how many manifests were involved;
                // the gate's own description of a manifest delete does not, which is the one
                // thing lost here.
                try
                {
                    result = new HelmExecutionResult
                    {
                        Success = true,
                        Output = await clusterAccess.DeleteManifestSetAsync(combined, ct: ct),
                    };
                }
                catch (Exception ex)
                {
                    result = new HelmExecutionResult { Success = false, Output = ex.Message };
                }
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Deployment {Name} ({DeploymentId}) cluster resources deleted by {User}",
                    deployment.Name, deploymentId, performedBy ?? "system");
                await auditService.RecordAsync(deploymentId, "DeleteFromCluster", "Deployment",
                    deployment.Name, performedBy: performedBy, ct: ct);

                // Clear the applied-resource inventory so a later re-apply starts clean.
                using ApplicationDbContext cleanupDb = dbFactory.CreateDbContext();
                await cleanupDb.DeploymentAppliedResources
                    .Where(r => r.DeploymentId == deploymentId)
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                logger.LogWarning(
                    "Cluster deletion failed for deployment {DeploymentId}: {Output}",
                    deploymentId, result.Output);
            }

            return result.Success
                ? KubernetesOperationResult<string>.Success(result.Output)
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            // Nothing at this level any more: the helm branch holds no credential, and the
            // manifest branch cleans up the one it still needs.
        }
    }

    // ──────── App-level cluster deletion ────────

    /// <summary>
    /// Removes all cluster resources for every deployment belonging to an app.
    /// Best-effort: failures are collected and returned but do not stop subsequent
    /// deployments from being cleaned up. Callers should delete the DB record
    /// separately after this completes.
    /// </summary>
    public async Task<List<string>> DeleteAppFromClusterAsync(
        Guid tenantId, Guid appId, string? performedBy = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Nesting is a no-op in the gate, so wrapping here makes the whole app one decision
        // rather than one per deployment: each DeleteDeploymentFromClusterAsync below opens its
        // own operation and defers to this one.
        using IDisposable operation = gate.BeginOperation($"remove every deployment of app {appId}");

        List<Guid> deploymentIds = await db.AppDeployments
            .Where(d => d.AppId == appId && d.App.Customer.TenantId == tenantId)
            .Select(d => d.Id)
            .ToListAsync(ct);

        List<string> errors = [];

        foreach (Guid deploymentId in deploymentIds)
        {
            KubernetesOperationResult<string> result =
                await DeleteDeploymentFromClusterAsync(tenantId, deploymentId, performedBy, ct);

            if (!result.IsSuccess && result.Error is not null)
                errors.Add(result.Error);
        }

        return errors;
    }

    // ──────── YAML / Manual apply ────────

    /// <summary>
    /// Applies all manifests for a YAML or Manual AppDeployment to the cluster
    /// using "kubectl apply". Idempotent: creates resources on first run, patches
    /// them on subsequent runs (standard server-side apply semantics).
    ///
    /// A bare Namespace manifest is prepended automatically so the target namespace
    /// is created if it doesn't exist — equivalent to Helm's --create-namespace.
    ///
    /// Manifests are applied in SortOrder sequence so foundational resources
    /// (PVCs, ConfigMaps) land before workloads (Deployments, Services).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyYamlDeploymentAsync(
        Guid tenantId, Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found, or it is not this tenant's.");

        if (!deployment.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This deployment is observed only (imported / managed by ArgoCD or Flux). Enable management to let EntKube apply it.");

        // The tenant is the caller's, not one read off the row being authorized. The old form —
        // ForAsync(deployment.Cluster.TenantId, deployment.ClusterId) — compared a row against
        // itself, so the predicate could not fail; see VacuousTenantCheckTests.
        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, deployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The deployment's cluster is not this tenant's, or has no kubeconfig configured.");

        // Check namespace governance before touching the cluster.
        string? lockedNs = await GetGovernanceNamespaceAsync(deployment, ct);
        string? nsViolation = CheckNamespaceGovernance(deployment.Namespace, lockedNs);
        if (nsViolation is not null)
            return KubernetesOperationResult<string>.Failure(nsViolation);

        // Load manifests in apply order.
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        if (manifests.Count == 0)
            return KubernetesOperationResult<string>.Failure(
                "No manifests defined for this deployment. Add YAML manifests before applying.");

        // Also check manifest content for embedded namespace violations.
        string? contentViolation = CheckNamespaceGovernance(
            deployment.Namespace, lockedNs, manifests.Select(m => m.YamlContent));
        if (contentViolation is not null)
            return KubernetesOperationResult<string>.Failure(contentViolation);

        // One action, one acknowledgment. The apply itself asks; the Kyverno apply, the prune
        // (which DELETES the resources a manifest no longer names) and the route re-apply that
        // follow are the same approved act. Each is a separate gated primitive, so without this
        // a single Apply answered one dialog and then one per pruned resource and per hostname.
        using IDisposable operation = gate.BeginOperation(
            $"apply {manifests.Count} manifest(s) to {deployment.Namespace}");

        // The namespace check above only catches resources that name a namespace. Cluster-scoped
        // resources carry none, so RBAC and webhook kinds are refused by kind instead.
        string? kindViolation = ManifestKindPolicy.CheckYaml(
            manifests.Select(m => m.YamlContent), deployment.Namespace);
        if (kindViolation is not null)
            return KubernetesOperationResult<string>.Failure(kindViolation);

        // Prepend a Namespace manifest so the namespace is created automatically
        // if it doesn't exist yet — mirrors Helm's --create-namespace behaviour.
        // Shared with drift detection so the two can never render different bytes.
        string combined = EntKube.Web.Services.Upgrades.DeploymentManifestComposer.Combine(
            deployment.Namespace, manifests);

        // The acknowledgment is raised by the seam, which is why there is no longer one here:
        // two would ask the operator twice for one apply. The summary is passed through so the
        // dialog still names the deployment and counts its documents.
        //
        // The namespace is a default for documents that do not carry one of their own, which
        // is why this is the namespace-aware apply and not the plain one.
        HelmExecutionResult result;
        try
        {
            result = new HelmExecutionResult
            {
                Success = true,
                Output = await clusterAccess.ApplyManifestInNamespaceAsync(
                    combined, deployment.Namespace,
                    $"Apply '{deployment.Name}' ({manifests.Count} manifest(s)) to {deployment.Namespace}",
                    ct),
            };
        }
        catch (OperationCanceledException)
        {
            // An operator cancelling at the gate threw out of here before the seam existed,
            // and the callers are written for that; swallowing it into a failed apply would
            // report "cancelled by operator" as an error on the page.
            throw;
        }
        catch (Exception ex)
        {
            result = new HelmExecutionResult { Success = false, Output = ex.Message };
        }

        string output = result.Output;

        if (result.Success)
        {
            logger.LogInformation("YAML deployment {DeploymentId} applied to {Namespace} by {User}",
                deploymentId, deployment.Namespace, performedBy ?? "system");
            await auditService.RecordAsync(deploymentId, "ApplyYaml", "Deployment",
                deployment.Name, performedBy: performedBy, ct: ct);

            // Ensure the (possibly brand-new) namespace inherits the environment's Kyverno policies.
            await ApplyKyvernoPoliciesAsync(deployment, ct);

            // Prune resources that were applied before but are no longer in the manifest
            // set — plain `kubectl apply` doesn't, so removed manifests orphan their
            // resources. Failure here never fails the (already successful) apply.
            try
            {
                output += await PruneRemovedResourcesAsync(
                    clusterAccess, db, deployment, manifests, performedBy, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Prune after apply failed for deployment {DeploymentId}", deploymentId);
                output += $"\n\nWarning: pruning of removed resources failed: {ex.Message}";
            }

            // Open a rollout watch when the deployment has one configured. Deliberately
            // fire-and-observe: the verdict arrives minutes later from the background
            // watcher, because blocking this call — and therefore any CI job driving it —
            // for a ten-minute analysis window would make the feature unusable in exactly
            // the pipelines it exists to protect. A failure to open the watch must never
            // fail an apply that already succeeded.
            try
            {
                await rollouts.OpenAsync(deploymentId, performedBy, DateTime.UtcNow, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not open rollout watch for deployment {DeploymentId}", deploymentId);
            }
        }
        else
        {
            logger.LogWarning("YAML apply failed for deployment {DeploymentId}: {Output}",
                deploymentId, result.Output);
        }

        return result.Success
            ? KubernetesOperationResult<string>.Success(output)
            : KubernetesOperationResult<string>.Failure(result.Output);
    }

    // ──────── App route cluster sync ────────

    /// <summary>
    /// Applies the HTTPRoute (and Certificate if ClusterIssuer) for an AppDeploymentRoute
    /// to its cluster using "kubectl apply". Idempotent.
    /// </summary>
    public virtual async Task<KubernetesOperationResult<string>> ApplyDeploymentRouteAsync(
        Guid tenantId, Guid deploymentRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute? dr = await db.AppDeploymentRoutes
            // The trust anchor decides which Gateway listener the HTTPRoute names as its parent,
            // so it has to be loaded before any manifest is generated from this route.
            .Include(r => r.AppRoute)
                .ThenInclude(ar => ar.ClientCaBundle)
            .Include(r => r.AppRoute)
                .ThenInclude(ar => ar.DeploymentRoutes)
                    .ThenInclude(sibDr => sibDr.AppDeployment)
                        .ThenInclude(d => d.Cluster)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(
                // AppDeploymentRoute has no TenantId and no App of its own — it hangs off a route,
                // which has one. (A per-entity grep said otherwise; the compiler disagreed.)
                r => r.Id == deploymentRouteId
                  && r.AppRoute.App.Customer.TenantId == tenantId, ct);

        if (dr is null)
            return KubernetesOperationResult<string>.Failure("Deployment route not found.");

        if (!dr.AppRoute.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This route is observed only (imported / managed by ArgoCD or Flux). Enable management to let EntKube apply it.");

        // The tenant comes from the caller, not from dr.AppDeployment.Cluster.TenantId as it used
        // to: read off the row it was then compared against, that predicate could never fail. A
        // route whose deployment sits on another tenant's cluster is refused here now.
        IClusterClient? clusterAccess = dr.AppDeployment is null
            ? null
            : await clusterClients.ForAsync(tenantId, dr.AppDeployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The route's cluster is not this tenant's, or has no kubeconfig configured.");

        Guid clusterId = dr.AppDeployment!.ClusterId;

        // One action, one acknowledgment. Publishing a route is a single decision an operator
        // makes, and it fans out into several separately gated primitives: the Gateway, a
        // PeerAuthentication in every backend namespace, a DestinationRule per backend Service,
        // and then the HTTPRoute itself. Without this, "Re-apply to cluster" asked four or more
        // times for one button press — which is how an operator learns to click through dialogs
        // without reading them.
        //
        // Nesting is a no-op, so the callers that already open an operation (a manifest apply
        // that re-applies its hostnames, the sync dispatcher) keep their own window and this
        // changes nothing for them.
        using IDisposable operation = gate.BeginOperation(
            $"publish {dr.AppRoute.Hostname} to {dr.AppDeployment.Cluster?.Name ?? "the cluster"}");

        // Apply the Gateway first so the HTTPS listener + cert exist before the HTTPRoute attaches.
        KubernetesOperationResult<string> gatewayResult =
            await ApplyGatewayForClusterAsync(tenantId, clusterAccess, clusterId, db, ct);
        if (!gatewayResult.IsSuccess)
            return gatewayResult;

        // Query ALL enabled routes for this hostname on this cluster, across every AppRoute that
        // shares the hostname. Navigation-property siblings only covers routes in the same AppRoute
        // record; routes from a different app linking the same hostname would be missed and
        // overwritten. The HTTPRoute name is hostname-derived so all must live in one resource.
        string hostname = dr.AppRoute.Hostname;
        List<AppDeploymentRoute> enabledRoutes = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(r => r.AppRoute.Hostname == hostname
                     && r.AppDeployment.ClusterId == clusterId
                     && r.IsEnabled
                     && r.AppRoute.IsManaged)
            .OrderByDescending(r => r.PathPrefix.Length)
            .ThenBy(r => r.PathPrefix)
            .ToListAsync(ct);

        // For Istio clusters: apply the namespace's mesh mTLS posture in every backend namespace.
        // The default is PERMISSIVE, because Istio's ingress gateway uses mTLS when connecting to
        // backend pods and a pod with no sidecar cannot complete that handshake — PERMISSIVE lets
        // both kinds work. A namespace an operator has moved to STRICT keeps STRICT here: this
        // apply runs on every route change, so hardcoding PERMISSIVE would quietly undo it.
        // Apply to ALL namespaces involved — not just the primary — so cross-namespace backends work too.
        // Asked of Catalog, tenant-first. This was one of the four reads the adoption ratchet
        // recorded as unservable because "all four … have no tenant in scope" — which stopped
        // being true when #198 gave this file's methods their tenants. The resolvers take
        // IComponentNaming (#172), so the contract's DTO goes straight in.
        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> clusterComponents =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct);
        string gatewayClass = ExternalRouteService.ResolveGatewayClass(clusterComponents);
        (_, string gwNamespace) = ExternalRouteService.ResolveGateway(clusterComponents);
        if (gatewayClass == "istio")
        {
            IEnumerable<string> backendNamespaces = enabledRoutes
                .Select(r => r.AppDeployment?.Namespace)
                .Where(n => n != null)
                .Distinct()!;

            Dictionary<string, MeshMtlsMode> namespaceModes = await db.MeshMtlsPolicies
                .Where(mp => mp.ClusterId == clusterId)
                .ToDictionaryAsync(mp => mp.Namespace, mp => mp.Mode, ct);

            foreach (string backendNs in backendNamespaces)
            {
                MeshMtlsMode mode = namespaceModes.GetValueOrDefault(backendNs, MeshMtlsMode.Permissive);
                string peerAuthYaml = MeshMtlsService.BuildPeerAuthenticationYaml(backendNs, mode);
                // Ignore errors — cluster may already have the policy or not need one.
                await ApplyRawYamlAsync(clusterAccess, peerAuthYaml, ct);
            }

            // DestinationRule is placed in the gateway's namespace (istio-system / root config
            // namespace) so the ingress gateway pod picks it up. A DestinationRule in a
            // non-root namespace is only guaranteed to affect traffic originating from that
            // namespace, not from gateway pods in istio-system. The FQDN host uniquely
            // identifies the target service regardless of where the rule is stored.
            // Grouped by backend Service: there is one DestinationRule per Service, so several
            // paths on the same Service must agree on one document rather than each applying
            // its own and the last one landing.
            IEnumerable<IGrouping<(string Namespace, string Service), AppDeploymentRoute>> byService = enabledRoutes
                .GroupBy(r => (
                    Namespace: r.AppDeployment?.Namespace ?? dr.AppDeployment.Namespace,
                    Service: r.ServiceName));

            foreach (IGrouping<(string Namespace, string Service), AppDeploymentRoute> group in byService)
            {
                string svc = group.Key.Service;
                string svcNs = group.Key.Namespace;

                // Read the live Service so TLS-serving ports keep TLS. A service-wide DISABLE
                // would push plaintext into them and the backend would drop the connection.
                // An empty list (service missing, API unreachable) yields exactly the
                // service-wide rule this call site has always applied.
                List<KubeServicePort> ports = await GetServicePortsAsync(clusterAccess, svcNs, svc, ct);

                // Under STRICT the backend refuses the plaintext hop this rule normally sets up,
                // so the gateway originates mesh mTLS instead. Same resource name either way:
                // the rule is rewritten in place rather than left behind contradicting the policy.
                string backendTlsMode = MeshMtlsService.BackendTlsMode(
                    namespaceModes.GetValueOrDefault(svcNs, MeshMtlsMode.Permissive));

                string destinationRuleYaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
                    svc, svcNs, gwNamespace, ports, alwaysEmit: true, serviceWideTlsMode: backendTlsMode,
                    sessionAffinity: SessionAffinitySpec.Merge(group.Select(SessionAffinitySpec.From)));

                await ApplyRawYamlAsync(clusterAccess, destinationRuleYaml, ct);
            }
        }

        // GenerateManifestYaml includes the HTTPRoute + Certificate + ReferenceGrants for any
        // cross-namespace backendRefs (required by Gateway API when services are in other namespaces).
        string yaml = AppRouteService.GenerateManifestYaml(dr.AppRoute, enabledRoutes);
        KubernetesOperationResult<string> result = await ApplyRawYamlAsync(clusterAccess, yaml, ct);

        if (result.IsSuccess)
        {
            // Stamp all sibling routes as applied — they're all part of the same HTTPRoute resource.
            using ApplicationDbContext db2 = dbFactory.CreateDbContext();
            List<Guid> siblingIds = enabledRoutes.Select(r => r.Id).ToList();
            List<AppDeploymentRoute> toStamp = await db2.AppDeploymentRoutes
                .Where(r => siblingIds.Contains(r.Id))
                .ToListAsync(ct);
            DateTime now = DateTime.UtcNow;
            foreach (AppDeploymentRoute s in toStamp)
                s.ClusterAppliedAt = now;
            await db2.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Turns EntKube management of an AppRoute on or off. Enabling reconciles the
    /// HTTPRoute immediately (no manual apply step); disabling relinquishes ownership
    /// and removes any HTTPRoute EntKube previously applied for this hostname. Used by
    /// the External Access panel's management switch (imported routes start unmanaged).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> SetRouteManagementAsync(
        Guid tenantId, Guid appRouteId, bool managed, CancellationToken ct = default)
    {
        Guid? applyRouteId;
        bool wasApplied;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            // ⚠️ Unlike its neighbours, this one does not reach a cluster before it writes:
            // route.IsManaged is set a few lines down, so the cluster seam never gets the chance
            // to refuse. Unmanaging another tenant's route abandons the HTTPRoute EntKube
            // re-applies for them; managing one makes EntKube start overwriting a route it does
            // not own. AppRoute has no TenantId, so the scope runs route → app → customer.
            AppRoute? route = await db.AppRoutes
                .Include(r => r.DeploymentRoutes)
                .FirstOrDefaultAsync(
                    r => r.Id == appRouteId && r.App.Customer.TenantId == tenantId, ct);

            if (route is null)
                return KubernetesOperationResult<string>.Failure("Route not found.");

            if (route.IsManaged == managed)
                return KubernetesOperationResult<string>.Success("No change.");

            route.IsManaged = managed;

            // Any one enabled deployment route drives the combined HTTPRoute for the hostname.
            applyRouteId = route.DeploymentRoutes
                .Where(dr => dr.IsEnabled)
                .Select(dr => (Guid?)dr.Id)
                .FirstOrDefault();
            wasApplied = route.DeploymentRoutes.Any(dr => dr.ClusterAppliedAt != null);

            await db.SaveChangesAsync(ct);
        }

        if (applyRouteId is null)
            return KubernetesOperationResult<string>.Success(
                managed ? "Management enabled. Add a deployment route to publish it." : "Management disabled.");

        // Enabling → reconcile now (IsManaged is already true so the apply queries include it).
        if (managed)
            return await ApplyDeploymentRouteAsync(tenantId, applyRouteId.Value, ct);

        // Disabling → only remove the HTTPRoute if EntKube had actually applied one.
        if (wasApplied)
            return await DeleteDeploymentRouteFromClusterAsync(tenantId, applyRouteId.Value, ct);

        return KubernetesOperationResult<string>.Success("Management disabled.");
    }

    /// <summary>
    /// Deletes the HTTPRoute (and Certificate if ClusterIssuer) for an AppDeploymentRoute
    /// from its cluster. Should be called before deleting the route from the database.
    /// Uses --ignore-not-found so it is safe to call even if the resource was never applied.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DeleteDeploymentRouteFromClusterAsync(
        Guid tenantId, Guid deploymentRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute? dr = await db.AppDeploymentRoutes
            // The trust anchor decides which Gateway listener the HTTPRoute names as its parent,
            // so it has to be loaded before any manifest is generated from this route.
            .Include(r => r.AppRoute)
                .ThenInclude(ar => ar.ClientCaBundle)
            .Include(r => r.AppRoute)
                .ThenInclude(ar => ar.DeploymentRoutes)
                    .ThenInclude(sibDr => sibDr.AppDeployment)
                        .ThenInclude(d => d.Cluster)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(
                // AppDeploymentRoute has no TenantId and no App of its own — it hangs off a route,
                // which has one. (A per-entity grep said otherwise; the compiler disagreed.)
                r => r.Id == deploymentRouteId
                  && r.AppRoute.App.Customer.TenantId == tenantId, ct);

        if (dr is null)
            return KubernetesOperationResult<string>.Failure("Deployment route not found.");

        IClusterClient? clusterAccess = dr.AppDeployment is null
            ? null
            : await clusterClients.ForAsync(tenantId, dr.AppDeployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The route's cluster is not this tenant's, or has no kubeconfig configured.");

        string ns = dr.AppDeployment!.Namespace;
        Guid clusterId = dr.AppDeployment.ClusterId;

        // One action, one acknowledgment: withdrawing a route either re-applies the HTTPRoute
        // with the remaining rules or deletes it, and the Istio teardown around it is gated
        // separately again.
        using IDisposable operation = gate.BeginOperation(
            $"withdraw {dr.AppRoute.Hostname} from {dr.AppDeployment.Cluster?.Name ?? "the cluster"}");

        // Query remaining routes for this hostname across all AppRoutes on this cluster.
        string hostname = dr.AppRoute.Hostname;
        List<AppDeploymentRoute> remainingRoutes = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(r => r.AppRoute.Hostname == hostname
                     && r.AppDeployment.ClusterId == clusterId
                     && r.Id != deploymentRouteId
                     && r.IsEnabled
                     && r.AppRoute.IsManaged)
            .OrderByDescending(r => r.PathPrefix.Length)
            .ThenBy(r => r.PathPrefix)
            .ToListAsync(ct);

        if (remainingRoutes.Count > 0)
        {
            // Re-apply HTTPRoute + ReferenceGrants with only the remaining rules so other deployments stay live.
            // Gateway stays unchanged — the hostname listener remains for the remaining routes.
            string yaml = AppRouteService.GenerateManifestYaml(dr.AppRoute, remainingRoutes);
            return await ApplyRawYamlAsync(clusterAccess, yaml, ct);
        }

        // Last deployment route for this hostname — delete the HTTPRoute and update the Gateway
        // to remove the HTTPS listener for this hostname (and its Certificate in cert-manager).
        string routeName = ExternalRouteService.ToListenerName(dr.AppRoute.Hostname) + "-route";
        try
        {
            // The acknowledgment is raised by the cluster client, whose own description of a delete
            // is the one this used to pass by hand: "Delete HTTPRoute/{name} in {namespace}".
            await clusterAccess.DeleteManifestAsync("httproute", routeName, ns, ct);

            // Regenerate the Gateway without this hostname so the HTTPS listener is removed.
            // The Certificate in cert-manager namespace is left in place (cert-manager cleans it up
            // if needed, and deleting it here would break any in-flight ACME challenges).
            await ApplyGatewayForClusterAsync(tenantId, clusterAccess, clusterId, db, ct);

            return KubernetesOperationResult<string>.Success(
                $"HTTPRoute {routeName} deleted from {ns}.");
        }
        catch (Exception ex)
        {
            // A failed delete now stops before the Gateway is regenerated. It used to regenerate
            // regardless and then report the failure, which removed the hostname's listener while
            // the HTTPRoute was still attached to it; leaving both in place is the safer half-state.
            return KubernetesOperationResult<string>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Regenerates and applies the full Gateway manifest for a cluster, combining
    /// all ExternalRoutes and enabled AppRoutes so every hostname has an HTTPS listener
    /// with a certificateRefs entry. This must be called whenever an AppRoute hostname
    /// is added or removed so the Gateway tracks the current set of exposed hostnames.
    /// </summary>
    /// <param name="tenantId">
    /// Whose cluster this is. Both callers already had one; this method did not, and its
    /// "Cluster not found." came from <c>FirstOrDefaultAsync(c =&gt; c.Id == clusterId)</c> with no
    /// tenant predicate anywhere — so a cluster id from elsewhere had its Gateway reconciled.
    /// </param>
    private async Task<KubernetesOperationResult<string>> ApplyGatewayForClusterAsync(
        Guid tenantId, IClusterClient clusterAccess, Guid clusterId, ApplicationDbContext db,
        CancellationToken ct, Guid? excludeVmRouteId = null)
    {
        if (!await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct))
            return KubernetesOperationResult<string>.Failure("Cluster not found.");

        // The components come from Catalog. The route set used to be reached by walking
        // cluster.Components into each component's ExternalRoutes — a join THROUGH Catalog's table
        // to get at Connectivity's own rows, which are keyed by component id and can be asked for
        // directly once you know the ids.
        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct);

        (string gatewayName, string gatewayNamespace) = ExternalRouteService.ResolveGateway(components);

        Dictionary<Guid, EntKube.Contracts.Catalog.InstalledComponent> componentsById =
            components.ToDictionary(c => c.Id);
        List<Guid> componentIds = [.. componentsById.Keys];

        List<ExternalRoute> externalRoutes = componentIds.Count == 0
            ? []
            : await db.ExternalRoutes
                .Where(r => componentIds.Contains(r.ComponentId))
                .ToListAsync(ct);

        // ClientCaBundle is Included because the Gateway generator refuses to emit a route that
        // requires a client certificate without its trust anchor loaded — an unloaded navigation
        // would otherwise render as "no mTLS" and quietly stop authenticating clients.
        List<AppRoute> appRoutes = await db.AppRoutes
            .Include(r => r.ClientCaBundle!)
                .ThenInclude(b => b.Certificates)
            // DeploymentRoutes carry the namespaces the HTTPRoutes live in, which the listeners'
            // namespace selector has to admit before the Gateway starts filtering on it.
            .Include(r => r.DeploymentRoutes)
                .ThenInclude(dr => dr.AppDeployment)
            .Where(r => r.IsEnabled && r.IsManaged && r.DeploymentRoutes.Any(dr =>
                dr.IsEnabled && dr.AppDeployment.ClusterId == clusterId))
            .ToListAsync(ct);

        // Published VM ports. Only their listeners and certificates belong to the Gateway; their
        // HTTPRoutes are applied by ApplyVmRouteAsync. Without these, applying any app route would
        // regenerate the Gateway without the VM's listener and take its hostname offline.
        List<VmRoute> vmRoutes = await db.VmRoutes
            .Where(r => r.TenantId == tenantId && r.ClusterId == clusterId && r.Kind == VmRouteKind.Http
                && (excludeVmRouteId == null || r.Id != excludeVmRouteId))
            .ToListAsync(ct);
        List<ExternalRoute> vmListeners =
            [.. vmRoutes.Select(r => VmRouteService.ToGatewayRoute(r, gatewayName, gatewayNamespace))];

        if (externalRoutes.Count == 0 && appRoutes.Count == 0 && vmListeners.Count == 0)
            return KubernetesOperationResult<string>.Success("No routes to apply.");

        string gatewayClass = ExternalRouteService.ResolveGatewayClass(components);

        // Traefik has no Gateway API client-certificate validation (traefik#11975): its only knob
        // is a cluster-wide default TLSOption. Applying the Gateway anyway would leave the mTLS
        // listener serving without ever asking for a certificate, so refuse instead.
        if (gatewayClass != "istio" && appRoutes.Any(r => r is { IsEnabled: true, RequireClientCertificate: true }))
        {
            return KubernetesOperationResult<string>.Failure(
                "Client-certificate authentication requires an Istio ingress gateway. Traefik does not implement " +
                "Gateway API frontend TLS validation, so the routes requiring a client certificate on this cluster " +
                "cannot be enforced.");
        }
        // Label first, apply second. The listeners admit routes by namespace label, so a Gateway
        // that lands before the labels do detaches every route on the cluster for as long as the
        // gap lasts — the ordering here is the difference between a policy change and an outage.
        await LabelRouteNamespacesAsync(
            clusterAccess, externalRoutes, componentsById, appRoutes, clusterId, ct,
            extraNamespaces: vmRoutes.Select(r => r.Namespace));

        string yaml = ExternalRouteService.GenerateGatewayYaml(
            gatewayName, gatewayNamespace, [.. externalRoutes, .. vmListeners], appRoutes, gatewayClass: gatewayClass);
        KubernetesOperationResult<string> applyResult = await ApplyRawYamlAsync(clusterAccess, yaml, ct);
        if (!applyResult.IsSuccess) return applyResult;

        // A listener on a port the gateway Service doesn't publish is invisible from outside: the
        // Gateway reports Programmed, the route looks applied, and every mTLS client times out.
        // The gateway's Service is Helm-managed (the Gateway binds to it via spec.addresses), so
        // it does not grow a port just because a listener appeared — an existing install needs
        // `helm upgrade` with the mTLS port added to service.ports. Say so instead of returning
        // a bare success.
        MtlsService.MtlsClusterPlan mtlsPlan = MtlsService.BuildPlan(appRoutes, gatewayNamespace);
        if (mtlsPlan.BundlesByPort.Count == 0) return applyResult;

        // Verify the client-certificate config survived the API server. Gateway API added
        // spec.tls.frontend in v1.5; against an older CRD the field is not rejected, it is pruned —
        // kubectl reports success, the Gateway programs cleanly, and nothing ever asks a client for
        // a certificate. Reading it back is the only way to tell those two outcomes apart.
        if (!await GatewayHasFrontendTlsAsync(clusterAccess, gatewayNamespace, gatewayName, ct))
        {
            return KubernetesOperationResult<string>.Failure(
                $"The Gateway was applied but its client-certificate configuration was dropped by the cluster. " +
                $"Gateway API v1.5 or newer is required for spec.tls.frontend — an older CRD prunes the field " +
                $"silently, leaving the mTLS listener serving without ever requesting a certificate. Upgrade the " +
                $"Gateway API CRDs component, and note that Istio only implements this from 1.28.");
        }

        List<KubeServicePort> servicePorts = await GetServicePortsAsync(clusterAccess, gatewayNamespace, gatewayName, ct);
        List<int> unexposed = mtlsPlan.BundlesByPort.Keys
            .Where(port => servicePorts.Count > 0 && servicePorts.All(sp => sp.Port != port))
            .OrderBy(p => p)
            .ToList();

        List<string> notes = [.. mtlsPlan.Warnings];
        if (unexposed.Count > 0)
        {
            notes.Add(
                $"Gateway Service {gatewayNamespace}/{gatewayName} does not publish port(s) " +
                $"{string.Join(", ", unexposed)}. The mTLS listener is configured but unreachable until the port " +
                $"is added to the ingress gateway's service.ports (helm upgrade) — see the Istio Gateway component.");
        }

        return notes.Count > 0
            ? KubernetesOperationResult<string>.Success($"{applyResult.Data}\n\n{string.Join("\n", notes)}")
            : applyResult;
    }

    /// <summary>
    /// Reads a Gateway back and reports whether <c>spec.tls.frontend</c> is present.
    ///
    /// Returns true when the check cannot be performed at all (API unreachable, Gateway not yet
    /// visible): an unreadable cluster is not evidence that the field was pruned, and failing the
    /// apply on a transient read would be worse than the problem being detected.
    /// </summary>
    private static async Task<bool> GatewayHasFrontendTlsAsync(
        IClusterClient clusterAccess, string ns, string name, CancellationToken ct)
    {
        try
        {
            using Kubernetes client = clusterAccess.CreateSdkClient();

            object raw = await client.CustomObjects.GetNamespacedCustomObjectAsync(
                "gateway.networking.k8s.io", "v1", ns, "gateways", name, cancellationToken: ct);

            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(raw));

            return doc.RootElement.TryGetProperty("spec", out JsonElement spec)
                && spec.TryGetProperty("tls", out JsonElement tls)
                && tls.TryGetProperty("frontend", out _);
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ──────── Outbound mTLS (client certificates to partner APIs) ────────

    /// <summary>
    /// Applies an outbound mTLS credential: the Secret holding the client certificate in every
    /// namespace the app is deployed to, plus — for mesh-originated credentials — the ServiceEntry
    /// registering the partner and the DestinationRule that makes the sidecar present the
    /// certificate.
    ///
    /// Applied per namespace because the Secret must live beside the workload that uses it: Istio's
    /// SDS reads <c>credentialName</c> from the workload's own namespace, so a single copy
    /// somewhere central would never be found.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyOutboundMtlsAsync(
        Guid tenantId, Guid credentialId, CancellationToken ct = default)
    {
        if (vault is null)
            return KubernetesOperationResult<string>.Failure("Vault is unavailable in this context.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // OutboundMtlsCredential is one of the few rows here with a TenantId of its own, so
        // this is a direct comparison. The credential names the partner endpoint a workload
        // presents a client certificate to, so applying a stranger's redirects their mTLS.
        OutboundMtlsCredential? credential = await db.OutboundMtlsCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.TenantId == tenantId, ct);

        if (credential is null)
            return KubernetesOperationResult<string>.Failure("Credential not found.");

        CertificateBundle? bundle = await vault.GetCertificateBundleByIdAsync(tenantId, credential.VaultSecretId, ct);
        if (bundle is null || !bundle.HasCertificate || !bundle.HasPrivateKey)
            return KubernetesOperationResult<string>.Failure(
                "The vault certificate for this credential is missing its certificate or private key.");

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == credential.AppId
                     && (credential.EnvironmentId == null || d.EnvironmentId == credential.EnvironmentId))
            .ToListAsync(ct);

        if (deployments.Count == 0)
            return KubernetesOperationResult<string>.Failure(
                "This app has no deployment in the selected scope, so there is no namespace to place the certificate in.");

        List<string> applied = [];
        List<string> notes = [];

        // One namespace can host several deployments of the same app; the resources are identical,
        // so apply once per namespace rather than once per deployment.
        foreach (IGrouping<(Guid ClusterId, string Namespace), AppDeployment> group in
                 deployments.GroupBy(d => (d.ClusterId, d.Namespace)))
        {
            AppDeployment first = group.First();

            IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, first.ClusterId, ct);

            if (clusterAccess is null)
            {
                notes.Add($"{first.Cluster?.Name ?? "cluster"}: not this tenant's cluster, or no "
                    + "kubeconfig configured — skipped.");
                continue;
            }

            // Asked of Catalog for the cluster the seam just resolved, rather than off the
            // deployment's own cluster row.
            bool hasMesh = ExternalRouteService.ResolveGatewayClass(
                await catalog.GetComponentsForClusterAsync(tenantId, first.ClusterId, ct)) == "istio";

            // Falling back to the Secret alone is better than applying mesh resources that nothing
            // reads — but it changes who performs the handshake, so it is said out loud.
            OutboundMtlsCredential effective = credential;
            if (credential.Mode == OutboundMtlsMode.MeshOriginated && !hasMesh)
            {
                effective = new OutboundMtlsCredential
                {
                    Id = credential.Id,
                    TenantId = credential.TenantId,
                    AppId = credential.AppId,
                    EnvironmentId = credential.EnvironmentId,
                    Name = credential.Name,
                    Host = credential.Host,
                    Port = credential.Port,
                    VaultSecretId = credential.VaultSecretId,
                    Mode = OutboundMtlsMode.SecretOnly,
                    WorkloadSelectorJson = credential.WorkloadSelectorJson
                };

                notes.Add(
                    $"{first.Cluster.Name}: no Istio mesh, so only the Secret was created — the application has to " +
                    $"mount it and perform mTLS itself.");
            }

            string manifest = OutboundMtlsService.BuildManifest(effective, bundle, first.Namespace);
            KubernetesOperationResult<string> result = await ApplyRawYamlAsync(clusterAccess, manifest, ct);

            if (!result.IsSuccess)
                return KubernetesOperationResult<string>.Failure(
                    $"Failed to apply in {first.Cluster.Name}/{first.Namespace}: {result.Error}");

            applied.Add($"{first.Cluster.Name}/{first.Namespace}");
        }

        if (applied.Count == 0)
            return KubernetesOperationResult<string>.Failure(
                $"Nothing was applied. {string.Join(" ", notes)}".Trim());

        credential.AppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        string summary =
            $"Client certificate applied to {string.Join(", ", applied)}. " +
            $"The app must call {OutboundMtlsService.CallHint(credential)}.";

        return KubernetesOperationResult<string>.Success(
            notes.Count > 0 ? $"{summary}\n\n{string.Join("\n", notes)}" : summary);
    }

    // ──────── Service-to-service mTLS (mesh) ────────

    /// <summary>
    /// Reads a namespace's mesh membership: whether it runs in ambient mode, and which pods carry
    /// an Istio sidecar. Used to decide whether STRICT can be applied without cutting traffic off.
    /// </summary>
    private static async Task<MeshReadiness> GetMeshReadinessAsync(
        IClusterClient clusterAccess, string ns, CancellationToken ct = default)
    {
        using Kubernetes client = clusterAccess.CreateSdkClient();

        V1Namespace namespaceObj = await client.CoreV1.ReadNamespaceAsync(ns, cancellationToken: ct);
        bool isAmbient = namespaceObj.Metadata?.Labels is { } labels
            && labels.TryGetValue(MeshMtlsService.AmbientLabelKey, out string? dataplane)
            && string.Equals(dataplane, "ambient", StringComparison.OrdinalIgnoreCase);

        V1PodList pods = await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);

        List<MeshPodStatus> statuses = (pods.Items ?? [])
            // Completed pods (Jobs, init containers of a finished run) are not serving traffic, so
            // their lack of a sidecar says nothing about whether STRICT is safe.
            .Where(p => p.Status?.Phase is not ("Succeeded" or "Failed"))
            .Select(p => new MeshPodStatus(
                p.Metadata?.Name ?? "(unnamed)",
                (p.Spec?.Containers ?? []).Any(c =>
                    string.Equals(c.Name, MeshMtlsService.SidecarContainerName, StringComparison.Ordinal))))
            .ToList();

        return MeshMtlsService.EvaluateReadiness(isAmbient, statuses);
    }

    /// <summary>
    /// Mesh readiness for a namespace, resolving the cluster's kubeconfig itself so callers (the
    /// UI included) never have to handle it.
    /// </summary>
    public async Task<KubernetesOperationResult<MeshReadiness>> GetMeshReadinessAsync(
        Guid tenantId, Guid clusterId, string ns, CancellationToken ct = default)
    {
        // No cluster row read at all: it was loaded only to take a tenant off it and hand that
        // back to the seam, which is the shape this whole exercise exists to remove.
        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, clusterId, ct);
        if (clusterAccess is null)
            return KubernetesOperationResult<MeshReadiness>.Failure(
                "Cluster not found for this tenant, or it has no kubeconfig configured.");

        try
        {
            return KubernetesOperationResult<MeshReadiness>.Success(
                await GetMeshReadinessAsync(clusterAccess, ns, ct));
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<MeshReadiness>.Failure(
                $"Could not read namespace '{ns}': {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a namespace's mesh mTLS posture: the PeerAuthentication itself, plus the gateway's
    /// DestinationRule for every backend service published out of that namespace.
    ///
    /// Both halves must move together. The PeerAuthentication decides what the pod accepts; the
    /// DestinationRule decides what the gateway sends. Applying only the first under STRICT leaves
    /// the gateway sending plaintext into a namespace that has just started refusing it, which
    /// takes every published route in the namespace down.
    ///
    /// STRICT is refused unless every pod in the namespace is in the mesh — that check is the
    /// difference between a policy change and an outage.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyMeshMtlsAsync(
        Guid tenantId, Guid clusterId, string ns, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // The row is read to confirm the cluster is this tenant's; its COMPONENTS — which decide
        // whether PeerAuthentication means anything here — come from Catalog. The comment that
        // used to sit here said the row was read "for its COMPONENTS", which is the half-converted
        // shape: this file already asks GetComponentsForClusterAsync in three other methods.
        if (!await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct))
            return KubernetesOperationResult<string>.Failure("Cluster not found for this tenant.");

        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct);

        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, clusterId, ct);
        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The cluster has no kubeconfig configured.");

        if (ExternalRouteService.ResolveGatewayClass(components) != "istio")
            return KubernetesOperationResult<string>.Failure(
                "Service-to-service mTLS requires an Istio service mesh on this cluster. PeerAuthentication is " +
                "an Istio resource — there is no equivalent on a cluster running Traefik alone.");

        MeshMtlsPolicy? policy = await db.MeshMtlsPolicies
            .FirstOrDefaultAsync(p => p.ClusterId == clusterId && p.Namespace == ns, ct);

        MeshMtlsMode mode = policy?.Mode ?? MeshMtlsMode.Permissive;

        if (mode == MeshMtlsMode.Strict)
        {
            MeshReadiness readiness;
            try
            {
                readiness = await GetMeshReadinessAsync(clusterAccess, ns, ct);
            }
            catch (Exception ex)
            {
                // Not being able to see the namespace is not evidence that STRICT is safe.
                return KubernetesOperationResult<string>.Failure(
                    $"Could not read namespace '{ns}' to check mesh membership, so STRICT was not applied: {ex.Message}");
            }

            if (!readiness.IsReady)
            {
                return KubernetesOperationResult<string>.Failure(
                    $"{readiness.PodsOutsideMesh.Count} of {readiness.TotalPods} pods in '{ns}' have no Istio sidecar " +
                    $"and the namespace is not in ambient mode: {string.Join(", ", readiness.PodsOutsideMesh.Take(10))}" +
                    (readiness.PodsOutsideMesh.Count > 10 ? ", …" : "") +
                    ". STRICT would refuse their traffic. Enable sidecar injection (or ambient) on the namespace and " +
                    "restart these workloads first.");
            }
        }

        // One action, one acknowledgment: the PeerAuthentication and the DestinationRules that
        // go with it are one posture change, and an operator who has approved STRICT for a
        // namespace has not asked to be consulted once per backend Service.
        using IDisposable operation = gate.BeginOperation(
            $"set mesh mTLS to {mode} in {ns}");

        string peerAuthYaml = MeshMtlsService.BuildPeerAuthenticationYaml(ns, mode);
        KubernetesOperationResult<string> peerAuthResult = await ApplyRawYamlAsync(clusterAccess, peerAuthYaml, ct);
        if (!peerAuthResult.IsSuccess) return peerAuthResult;

        // Move the gateway's side of the hop to match.
        (_, string gwNamespace) = ExternalRouteService.ResolveGateway(components);
        string backendTlsMode = MeshMtlsService.BackendTlsMode(mode);

        // The routes, not just their service names: this rewrites the same DestinationRules the
        // route apply writes, so it has to carry their session affinity forward. Selecting only
        // the names would rewrite each rule without its loadBalancer block and quietly drop
        // affinity from every published service in the namespace.
        List<AppDeploymentRoute> nsRoutes = await db.AppDeploymentRoutes
            .Where(r => r.IsEnabled
                     && r.AppDeployment.ClusterId == clusterId
                     && r.AppDeployment.Namespace == ns)
            .OrderBy(r => r.PathPrefix)
            .ToListAsync(ct);

        List<IGrouping<string, AppDeploymentRoute>> services = nsRoutes
            .GroupBy(r => r.ServiceName)
            .ToList();

        foreach (IGrouping<string, AppDeploymentRoute> group in services)
        {
            string svc = group.Key;
            List<KubeServicePort> ports = await GetServicePortsAsync(clusterAccess, ns, svc, ct);
            string ruleYaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
                svc, ns, gwNamespace, ports, alwaysEmit: true, serviceWideTlsMode: backendTlsMode,
                sessionAffinity: SessionAffinitySpec.Merge(group.Select(SessionAffinitySpec.From)));
            await ApplyRawYamlAsync(clusterAccess, ruleYaml, ct);
        }

        if (policy is not null)
        {
            policy.AppliedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        string summary = mode == MeshMtlsMode.Strict
            ? $"'{ns}' now accepts mTLS only. {services.Count} published service(s) switched to mesh identity."
            : $"'{ns}' accepts both mTLS and plaintext. {services.Count} published service(s) updated.";

        return KubernetesOperationResult<string>.Success(summary);
    }

    // ──────── Raw L4 (TCP/UDP) routes (dedicated Istio L4 gateway) ────────

    /// <summary>
    /// Applies a raw L4 route (TCP or UDP): (re)generates the dedicated L4 Gateway (auto-provisions
    /// its own LoadBalancer + external IP, one listener per port), disables forced mTLS to the backend
    /// for TCP, then applies the TCPRoute/UDPRoute. On success returns the resolved external endpoint
    /// (LoadBalancer address:port).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyL4RouteAsync(
        Guid tenantId, Guid l4RouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppL4Route? route = await db.AppL4Routes
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(
                // AppL4Route has no TenantId; it reaches one through its deployment's app.
                r => r.Id == l4RouteId
                  && r.AppDeployment.App.Customer.TenantId == tenantId, ct);

        if (route is null)
            return KubernetesOperationResult<string>.Failure("L4 route not found, or it is not this tenant's.");

        if (!route.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This route is observed only. Enable management to let EntKube apply it.");

        IClusterClient? clusterAccess = route.AppDeployment is null
            ? null
            : await clusterClients.ForAsync(tenantId, route.AppDeployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The route's cluster is not this tenant's, or has no kubeconfig configured.");

        Guid clusterId = route.AppDeployment!.ClusterId;
        string backendNs = route.AppDeployment.Namespace;
        string proto = route.Protocol.ToString().ToUpperInvariant();

        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct);

        if (ExternalRouteService.ResolveGatewayClass(components) != "istio")
            return KubernetesOperationResult<string>.Failure(
                "L4 (TCP/UDP) routes require an Istio ingress gateway on this cluster.");

        (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);

        // Preflight: the TCPRoute/UDPRoute CRD ships only in the experimental channel of Gateway API.
        // Without it kubectl apply fails obscurely — surface a clear, actionable error instead.
        string crdName = AppL4RouteService.RouteCrdName(route.Protocol);
        if (!await CrdInstalledAsync(clusterAccess, crdName, ct))
            return KubernetesOperationResult<string>.Failure(
                $"The {AppL4RouteService.RouteKind(route.Protocol)} CRD ({crdName}) is not installed on this cluster. " +
                "Install the experimental channel of the Gateway API CRDs to enable L4 routing.");

        // One action, one acknowledgment: the L4 Gateway, the backend PeerAuthentication, the
        // DestinationRule and the TCPRoute/UDPRoute are one published port.
        //
        // ⚠️ Before the Gateway apply, not after. Placed after, that first apply asked on its own
        // and the operation then covered the rest — two dialogs for one button, which the count
        // test caught. An operation has to open before the action's FIRST gated change.
        using IDisposable operation = gate.BeginOperation(
            $"publish {route.Protocol} port {route.ExternalPort} for {route.ServiceName}");

        // Apply the dedicated L4 Gateway first so the listener exists before the route attaches.
        KubernetesOperationResult<string> gatewayResult =
            await ApplyL4GatewayForClusterAsync(clusterAccess, clusterId, db, components, null, ct);
        if (!gatewayResult.IsSuccess)
            return gatewayResult;

        // TCP only: the ingress gateway would otherwise force mTLS to the backend, breaking
        // sidecar-less pods. PERMISSIVE in the backend ns + a DestinationRule (tls DISABLE) in the
        // gateway's root config namespace let the gateway reach the pod plainly. Istio mTLS does not
        // apply to UDP, so this step is skipped for UDP routes.
        if (route.Protocol == L4Protocol.Tcp)
        {
            await ApplyRawYamlAsync(clusterAccess, L4PeerAuthenticationYaml(backendNs), ct);
            await ApplyRawYamlAsync(clusterAccess, L4DestinationRuleYaml(route.ServiceName, backendNs, gwNamespace), ct);
        }

        string yaml = AppL4RouteService.GenerateRouteYaml(route, backendNs);
        KubernetesOperationResult<string> result = await ApplyRawYamlAsync(clusterAccess, yaml, ct);
        if (!result.IsSuccess)
            return result;

        route.ClusterAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        string? address = await GetL4GatewayAddressAsync(clusterAccess, gwNamespace, ct);
        string endpoint = address is not null
            ? $"{address}:{route.ExternalPort}/{proto}"
            : $"(LoadBalancer address pending):{route.ExternalPort}/{proto}";
        return KubernetesOperationResult<string>.Success($"{proto} route applied. External endpoint: {endpoint}");
    }

    // ──────── Published virtual machine ports ────────

    /// <summary>
    /// Publishes a stored <see cref="VmRoute"/>: the Service in front of the VM, the Gateway listener,
    /// and the HTTPRoute or TCPRoute. One acknowledgment for all of it — the operator chose "publish
    /// this port". Returns where the port can now be reached.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyVmRouteAsync(
        Guid tenantId, Guid vmRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VmRoute? route = await db.VmRoutes.FirstOrDefaultAsync(r => r.Id == vmRouteId && r.TenantId == tenantId, ct);
        if (route is null)
            return KubernetesOperationResult<string>.Failure("Published port not found, or it is not this tenant's.");

        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, route.ClusterId, ct);
        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The VM's cluster is not this tenant's, or has no kubeconfig configured.");

        if (await VmLabelProblemAsync(clusterAccess, route, ct) is string labelProblem)
            return KubernetesOperationResult<string>.Failure(labelProblem);

        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, route.ClusterId, ct);
        string gatewayClass = ExternalRouteService.ResolveGatewayClass(components);

        // Every route of this kind on this VM shares one Service, so the Service is rebuilt from all of them.
        List<int> ports = await db.VmRoutes
            .Where(r => r.TenantId == tenantId && r.ClusterId == route.ClusterId && r.Namespace == route.Namespace
                && r.VmName == route.VmName && r.Kind == route.Kind)
            .Select(r => r.VmPort)
            .ToListAsync(ct);
        string serviceName = VmRouteService.ServiceName(route.VmName, route.Kind);
        string serviceYaml = VmRouteService.BuildServiceYaml(route.Namespace, route.VmName, route.Kind, ports);

        string endpoint;

        if (route.Kind == VmRouteKind.Http)
        {
            (string gwName, string gwNamespace) = ExternalRouteService.ResolveGateway(components);

            // Opened before the first gated change, so the whole publish is one dialog.
            using IDisposable operation = gate.BeginOperation(
                $"publish https://{route.Hostname} for virtual machine {route.Namespace}/{route.VmName}");

            KubernetesOperationResult<string> applied = await ApplyRawYamlAsync(clusterAccess, serviceYaml, ct);
            if (!applied.IsSuccess) return applied;

            // The listener and its certificate. This also labels the VM's namespace so the listener admits
            // its route, and must land before the route does.
            applied = await ApplyGatewayForClusterAsync(tenantId, clusterAccess, route.ClusterId, db, ct);
            if (!applied.IsSuccess) return applied;

            if (gatewayClass == "istio")
            {
                // The virt-launcher pod has no sidecar, so the gateway must not originate mesh mTLS to it.
                applied = await ApplyRawYamlAsync(clusterAccess, ExternalRouteService.GenerateBackendDestinationRuleYaml(
                    serviceName, route.Namespace, gwNamespace,
                    ports.Distinct().Select(p => new KubeServicePort($"http-{p}", p, "TCP")), alwaysEmit: true), ct);
                if (!applied.IsSuccess) return applied;
            }

            applied = await ApplyRawYamlAsync(clusterAccess, ExternalRouteService.GenerateHttpRouteYaml(
                VmRouteService.ToGatewayRoute(route, gwName, gwNamespace),
                new VmRouteService.RouteNamespace(route.Namespace)), ct);
            if (!applied.IsSuccess) return applied;

            endpoint = $"https://{route.Hostname} — point its DNS at the ingress gateway's address.";
        }
        else
        {
            if (gatewayClass != "istio")
                return KubernetesOperationResult<string>.Failure(
                    "TCP ports need an Istio ingress gateway on this cluster. Traefik clusters can publish HTTP hostnames only.");

            string crdName = AppL4RouteService.RouteCrdName(L4Protocol.Tcp);
            if (!await CrdInstalledAsync(clusterAccess, crdName, ct))
                return KubernetesOperationResult<string>.Failure(
                    $"The TCPRoute CRD ({crdName}) is not installed on this cluster. Install the experimental " +
                    "channel of the Gateway API CRDs to publish TCP ports.");

            (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);

            using IDisposable operation = gate.BeginOperation(
                $"publish TCP port {route.ExternalPort} for virtual machine {route.Namespace}/{route.VmName}");

            KubernetesOperationResult<string> applied = await ApplyRawYamlAsync(clusterAccess, serviceYaml, ct);
            if (!applied.IsSuccess) return applied;

            applied = await ApplyL4GatewayForClusterAsync(clusterAccess, route.ClusterId, db, components, null, ct);
            if (!applied.IsSuccess) return applied;

            await ApplyRawYamlAsync(clusterAccess, L4PeerAuthenticationYaml(route.Namespace), ct);
            await ApplyRawYamlAsync(clusterAccess, L4DestinationRuleYaml(serviceName, route.Namespace, gwNamespace), ct);

            applied = await ApplyRawYamlAsync(clusterAccess,
                AppL4RouteService.GenerateRouteYaml(VmRouteService.ToL4Route(route, gwNamespace), route.Namespace), ct);
            if (!applied.IsSuccess) return applied;

            string? address = await GetL4GatewayAddressAsync(clusterAccess, gwNamespace, ct);
            endpoint = $"{address ?? "(LoadBalancer address pending)"}:{route.ExternalPort}";
        }

        VmRoute tracked = await db.VmRoutes.FirstAsync(r => r.Id == route.Id, ct);
        tracked.ClusterAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return KubernetesOperationResult<string>.Success(endpoint);
    }

    /// <summary>
    /// Takes a published port off the cluster and deletes the stored route: its HTTPRoute or TCPRoute,
    /// its port on the shared Service (the Service itself once no port remains), and its Gateway
    /// listener. One acknowledgment for the whole withdrawal.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> WithdrawVmRouteAsync(
        Guid tenantId, Guid vmRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VmRoute? route = await db.VmRoutes.FirstOrDefaultAsync(r => r.Id == vmRouteId && r.TenantId == tenantId, ct);
        if (route is null)
            return KubernetesOperationResult<string>.Failure("Published port not found, or it is not this tenant's.");

        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, route.ClusterId, ct);
        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The VM's cluster is not this tenant's, or has no kubeconfig configured.");

        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, route.ClusterId, ct);

        List<int> remainingPorts = await db.VmRoutes
            .Where(r => r.TenantId == tenantId && r.ClusterId == route.ClusterId && r.Namespace == route.Namespace
                && r.VmName == route.VmName && r.Kind == route.Kind && r.Id != route.Id)
            .Select(r => r.VmPort)
            .ToListAsync(ct);
        string serviceName = VmRouteService.ServiceName(route.VmName, route.Kind);

        try
        {
            using IDisposable operation = gate.BeginOperation(
                $"withdraw {(route.Kind == VmRouteKind.Http ? $"https://{route.Hostname}" : $"TCP port {route.ExternalPort}")} "
                + $"from virtual machine {route.Namespace}/{route.VmName}");

            if (route.Kind == VmRouteKind.Http)
            {
                (string gwName, string gwNamespace) = ExternalRouteService.ResolveGateway(components);

                await clusterAccess.DeleteManifestAsync("httproute.gateway.networking.k8s.io",
                    ExternalRouteService.ToListenerName(route.Hostname!) + "-route", route.Namespace, ct);

                if (remainingPorts.Count == 0)
                {
                    await clusterAccess.DeleteManifestAsync("service", serviceName, route.Namespace, ct);
                    await clusterAccess.DeleteManifestAsync("destinationrule.networking.istio.io",
                        $"entkube-disable-mtls-{serviceName}", gwNamespace, ct);
                }
                else
                {
                    await ApplyRawYamlAsync(clusterAccess,
                        VmRouteService.BuildServiceYaml(route.Namespace, route.VmName, route.Kind, remainingPorts), ct);
                }

                // Rebuilt without this hostname. When it was the last route of any kind on the cluster the
                // Gateway is left as it was — the same as the app and component routes' last delete.
                KubernetesOperationResult<string> gateway = await ApplyGatewayForClusterAsync(
                    tenantId, clusterAccess, route.ClusterId, db, ct, excludeVmRouteId: route.Id);
                if (!gateway.IsSuccess) return gateway;
            }
            else
            {
                (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);

                await clusterAccess.DeleteManifestAsync(AppL4RouteService.RouteResourceType(L4Protocol.Tcp),
                    AppL4RouteService.RouteResourceName(VmRouteService.ToL4Route(route, gwNamespace)), route.Namespace, ct);

                if (remainingPorts.Count == 0)
                {
                    await clusterAccess.DeleteManifestAsync("service", serviceName, route.Namespace, ct);
                    await clusterAccess.DeleteManifestAsync("destinationrule.networking.istio.io",
                        $"entkube-disable-mtls-{serviceName}", gwNamespace, ct);
                }
                else
                {
                    await ApplyRawYamlAsync(clusterAccess,
                        VmRouteService.BuildServiceYaml(route.Namespace, route.VmName, route.Kind, remainingPorts), ct);
                }

                // Rebuilt without this port — and removed, releasing its LoadBalancer, if nothing else uses it.
                KubernetesOperationResult<string> gateway = await ApplyL4GatewayForClusterAsync(
                    clusterAccess, route.ClusterId, db, components, route.Id, ct);
                if (!gateway.IsSuccess) return gateway;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return KubernetesOperationResult<string>.Failure($"Could not withdraw the port: {ex.Message}");
        }

        db.VmRoutes.Remove(route);
        await db.SaveChangesAsync(ct);
        return KubernetesOperationResult<string>.Success("Withdrawn.");
    }

    /// <summary>
    /// Why a VM's published Service would select nothing, or null when it will select the VM. The Service
    /// selects the <c>kubevirt.io/vm</c> label, which EntKube puts on every VM it creates; a VM created
    /// elsewhere may lack it, and a Service with no endpoints publishes a port that answers nothing.
    /// </summary>
    private static async Task<string?> VmLabelProblemAsync(IClusterClient clusterAccess, VmRoute route, CancellationToken ct)
    {
        string json;
        try
        {
            json = await clusterAccess.GetJsonAsync($"virtualmachine.kubevirt.io/{route.VmName}", route.Namespace, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not read virtual machine {route.Namespace}/{route.VmName}: {ex.Message}";
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        bool labelled = doc.RootElement.TryGetProperty("spec", out JsonElement spec)
            && spec.TryGetProperty("template", out JsonElement template)
            && template.TryGetProperty("metadata", out JsonElement metadata)
            && metadata.TryGetProperty("labels", out JsonElement labels)
            && labels.TryGetProperty(VmRouteService.VmLabel, out JsonElement value)
            && value.GetString() == route.VmName;

        return labelled
            ? null
            : $"Virtual machine {route.VmName} has no '{VmRouteService.VmLabel}: {route.VmName}' label on its pod "
              + "template, so a Service cannot select it. VMs created in EntKube carry it; add it to this VM's "
              + "spec.template.metadata.labels and restart the VM.";
    }

    /// <summary>PERMISSIVE in a TCP backend's namespace, so the gateway's plaintext hop is accepted.</summary>
    private static string L4PeerAuthenticationYaml(string backendNs) =>
        $"apiVersion: security.istio.io/v1beta1\n" +
        $"kind: PeerAuthentication\n" +
        $"metadata:\n" +
        $"  name: entkube-permissive\n" +
        $"  namespace: {backendNs}\n" +
        $"spec:\n" +
        $"  mtls:\n" +
        $"    mode: PERMISSIVE\n";

    /// <summary>tls DISABLE from the gateway to a TCP backend that has no sidecar to speak mesh mTLS.</summary>
    private static string L4DestinationRuleYaml(string serviceName, string backendNs, string gwNamespace) =>
        $"apiVersion: networking.istio.io/v1beta1\n" +
        $"kind: DestinationRule\n" +
        $"metadata:\n" +
        $"  name: entkube-disable-mtls-{serviceName}\n" +
        $"  namespace: {gwNamespace}\n" +
        $"spec:\n" +
        $"  host: {serviceName}.{backendNs}.svc.cluster.local\n" +
        $"  trafficPolicy:\n" +
        $"    tls:\n" +
        $"      mode: DISABLE\n";

    /// <summary>
    /// Deletes the TCPRoute/UDPRoute for a route from its cluster and regenerates the dedicated L4
    /// Gateway without this route's listener (removing the Gateway entirely — and releasing its
    /// LoadBalancer — when no L4 routes remain). Call before removing the route from the database.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DeleteL4RouteFromClusterAsync(
        Guid tenantId, Guid l4RouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppL4Route? route = await db.AppL4Routes
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(
                // AppL4Route has no TenantId; it reaches one through its deployment's app.
                r => r.Id == l4RouteId
                  && r.AppDeployment.App.Customer.TenantId == tenantId, ct);

        if (route is null)
            return KubernetesOperationResult<string>.Failure("L4 route not found, or it is not this tenant's.");

        IClusterClient? clusterAccess = route.AppDeployment is null
            ? null
            : await clusterClients.ForAsync(tenantId, route.AppDeployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The route's cluster is not this tenant's, or has no kubeconfig configured.");

        string ns = route.AppDeployment!.Namespace;
        Guid clusterId = route.AppDeployment.ClusterId;

        await clusterAccess.DeleteManifestAsync(
            AppL4RouteService.RouteResourceType(route.Protocol),
            AppL4RouteService.RouteResourceName(route), ns, ct);

        // Regenerate the gateway from the remaining routes (this one excluded — it is still in the DB).
        return await ApplyL4GatewayForClusterAsync(
            clusterAccess, clusterId, db,
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct),
            l4RouteId, ct);
    }

    /// <summary>
    /// Regenerates and applies the dedicated L4 Gateway for a cluster from all enabled, managed L4
    /// routes (optionally excluding one being deleted). When no ports remain the Gateway is deleted so
    /// its auto-provisioned LoadBalancer is released.
    /// </summary>
    /// <param name="components">
    /// The cluster's installed components, from <c>ICatalogApi</c>. Passed in rather than queried
    /// here: this is <c>static</c> and has no tenant, and the tenant is what the contract needs —
    /// both callers have one, and the apply path had already asked for the same list.
    /// </param>
    private static async Task<KubernetesOperationResult<string>> ApplyL4GatewayForClusterAsync(
        IClusterClient clusterAccess, Guid clusterId, ApplicationDbContext db,
        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components,
        Guid? excludeRouteId, CancellationToken ct)
    {
        (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);
        string gatewayClass = ExternalRouteService.ResolveGatewayClass(components);

        List<AppL4Route> routes = await db.AppL4Routes
            .Where(r => r.IsEnabled && r.IsManaged
                     && r.AppDeployment.ClusterId == clusterId
                     && (excludeRouteId == null || r.Id != excludeRouteId))
            .ToListAsync(ct);

        // VM TCP ports share this Gateway. Leaving them out would drop their listeners whenever an app
        // L4 route changes, and delete the Gateway — and its LoadBalancer address — when the last app
        // route goes while VMs still use it. The exclusion id is shared: route ids are Guids.
        routes.AddRange((await db.VmRoutes
                .Where(r => r.ClusterId == clusterId && r.Kind == VmRouteKind.Tcp
                         && (excludeRouteId == null || r.Id != excludeRouteId))
                .ToListAsync(ct))
            .Select(r => VmRouteService.ToL4Route(r, gwNamespace)));

        string yaml = ExternalRouteService.GenerateL4GatewayYaml(gwNamespace, routes, gatewayClass);
        if (string.IsNullOrEmpty(yaml))
        {
            // No L4 ports left — remove the dedicated gateway so its LoadBalancer is freed.
            await clusterAccess.DeleteManifestAsync("gateway.gateway.networking.k8s.io",
                ExternalRouteService.L4GatewayName, gwNamespace, ct);
            return KubernetesOperationResult<string>.Success("No L4 routes remain — dedicated L4 gateway removed.");
        }

        return await ApplyRawYamlAsync(clusterAccess, yaml, ct);
    }

    /// <summary>Returns the dedicated L4 gateway's external address (LoadBalancer IP/hostname), or null if not yet assigned.</summary>
    public async Task<string?> GetL4EndpointAddressAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // The row is not needed at all now: the L4 gateway's namespace comes from what is
        // installed, which is Catalog's answer, and ForAsync below already refuses a cluster that
        // is not this tenant's — so the row load and its null check were the same question twice.
        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct);

        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, clusterId, ct);
        if (clusterAccess is null) return null;

        (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);
        return await GetL4GatewayAddressAsync(clusterAccess, gwNamespace, ct);
    }

    /// <summary>
    /// The dedicated L4 gateway's first assigned address, or null when it has none yet. Reads the
    /// Gateway as JSON and picks out <c>status.addresses[0].value</c> — the same field the kubectl
    /// jsonpath selected. Absent Gateway, empty address list, unreachable cluster: all null, which
    /// the callers render as "(LoadBalancer address pending)".
    /// </summary>
    private async Task<string?> GetL4GatewayAddressAsync(
        IClusterClient clusterAccess, string gwNamespace, CancellationToken ct)
    {
        try
        {
            string json = await clusterAccess.GetJsonAsync(
                $"gateway/{ExternalRouteService.L4GatewayName}", gwNamespace, ct: ct);

            string? addr = JsonNode.Parse(json)?["status"]?["addresses"]
                ?.AsArray().FirstOrDefault()?["value"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(addr) ? null : addr;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the L4 gateway address in {Namespace} on {Cluster}",
                gwNamespace, clusterAccess.ClusterName);
            return null;
        }
    }

    /// <summary>
    /// Whether a CRD is installed. A cluster that cannot be reached reads as "not installed", as it
    /// did when this ran kubectl directly — the caller turns either into the same advice to install
    /// the Gateway API experimental channel.
    /// </summary>
    private async Task<bool> CrdInstalledAsync(IClusterClient clusterAccess, string crdName, CancellationToken ct)
    {
        try
        {
            return await clusterAccess.ResourceExistsAsync("crd", crdName, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check whether CRD {Crd} is installed on {Cluster}",
                crdName, clusterAccess.ClusterName);
            return false;
        }
    }

    /// <summary>
    /// Diffs the just-applied manifest set against the deployment's applied-resource
    /// inventory, deletes the resources that were removed (honoring keep-annotations
    /// and never touching Namespaces), then rewrites the inventory to the new set.
    /// Returns a human-readable summary to append to the apply output ("" when nothing pruned).
    /// </summary>
    /// <param name="clusterAccess">
    /// The cluster this deployment is on, already resolved by the caller. It used to take the
    /// kubeconfig off <c>deployment.Cluster</c> and hand the string to a private kubectl runner,
    /// which is why these deletes never reached the change gate.
    /// </param>
    private async Task<string> PruneRemovedResourcesAsync(
        IClusterClient clusterAccess, ApplicationDbContext db, AppDeployment deployment,
        List<DeploymentManifest> manifests, string? performedBy, CancellationToken ct)
    {
        // Desired set from the current manifests (version-agnostic identity).
        Dictionary<(string, string, string?, string), ManifestResourceRef> newSet = new();
        foreach (DeploymentManifest m in manifests)
        {
            foreach (ManifestResourceRef r in ManifestResourceParser.Parse(m.YamlContent, deployment.Namespace))
            {
                newSet[r.Key] = r;
            }
        }

        List<DeploymentAppliedResource> previous = await db.DeploymentAppliedResources
            .Where(r => r.DeploymentId == deployment.Id)
            .ToListAsync(ct);

        // Prune what we recorded applying that is prunable, not a Namespace, and gone from the new set.
        List<DeploymentAppliedResource> toPrune = previous
            .Where(p => p.Prunable
                && !string.Equals(p.Kind, "Namespace", StringComparison.Ordinal)
                && !newSet.ContainsKey((p.Group, p.Kind, p.Namespace, p.Name)))
            .ToList();

        List<string> pruned = [];
        List<string> failed = [];

        foreach (DeploymentAppliedResource r in toPrune)
        {
            string label = $"{r.Kind}/{r.Name}" + (r.Namespace is null ? "" : $" (ns: {r.Namespace})");
            if (await DeletePrunedResourceAsync(clusterAccess, r, ct))
            {
                pruned.Add(label);
                await auditService.RecordAsync(deployment.Id, "PruneResource", r.Kind, r.Name,
                    performedBy: performedBy, ct: ct);
            }
            else
            {
                failed.Add(label);
            }
        }

        // Rewrite the inventory to reflect exactly what is now applied.
        db.DeploymentAppliedResources.RemoveRange(previous);
        foreach (ManifestResourceRef r in newSet.Values)
        {
            db.DeploymentAppliedResources.Add(new DeploymentAppliedResource
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment.Id,
                Group = r.Group,
                Version = r.Version,
                Kind = r.Kind,
                Name = r.Name,
                Namespace = r.Namespace,
                Prunable = r.Prunable
            });
        }
        await db.SaveChangesAsync(ct);

        string summary = "";
        if (pruned.Count > 0)
        {
            summary += $"\n\nPruned {pruned.Count} orphaned resource(s): {string.Join(", ", pruned)}";
        }
        if (failed.Count > 0)
        {
            summary += $"\n\nFailed to prune {failed.Count} resource(s): {string.Join(", ", failed)}";
        }
        return summary;
    }

    /// <summary>Deletes a single pruned resource by kind(.group)/name, scoping to its namespace only when it has one.</summary>
    /// <summary>
    /// Deletes one resource the manifest set no longer contains.
    ///
    /// <para>⚠️ This used to write the kubeconfig to a temp file and run its own
    /// <c>kubectl delete</c>, so <b>every pruned resource was deleted without the change gate</b> —
    /// invisible to <c>ClusterChangeGateCoverageTests</c>, because that metric treats a file which
    /// acknowledges <em>anywhere</em> as gated, and this file does. Through the seam the delete is
    /// acknowledged like any other. The seam also passes <c>--ignore-not-found</c>, so a resource
    /// that is already gone still counts as pruned, exactly as before.</para>
    ///
    /// <para>The seam <b>throws</b> where this returned false, and the difference is load-bearing:
    /// the caller counts failures into a summary and carries on pruning the rest, so letting the
    /// exception out would abandon the remaining resources on the first one that resists.</para>
    /// </summary>
    private async Task<bool> DeletePrunedResourceAsync(
        IClusterClient clusterAccess, DeploymentAppliedResource r, CancellationToken ct)
    {
        string target = string.IsNullOrEmpty(r.Group) ? r.Kind : $"{r.Kind}.{r.Group}";

        try
        {
            await clusterAccess.DeleteManifestAsync(target, r.Name, r.Namespace ?? "", ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prune {Target}/{Name} in {Namespace} on {Cluster}",
                target, r.Name, r.Namespace ?? "(cluster-scoped)", clusterAccess.ClusterName);
            return false;
        }
    }

    /// <summary>
    /// Stamps <see cref="ExternalRouteService.RouteNamespaceLabel"/> on every namespace holding an
    /// HTTPRoute for this cluster's Gateway, so the listeners' namespace selector admits them.
    ///
    /// Patches the label on rather than applying a Namespace manifest: a strategic-merge patch is
    /// purely additive, while applying a two-line Namespace object can strip labels a previous apply
    /// owned — and these namespaces belong to customer applications, not to EntKube.
    ///
    /// Failures are logged, not raised. A namespace that cannot be labelled is one hostname that
    /// keeps serving from a Gateway that already works; aborting the whole reconcile over it would
    /// hold back the other listeners for no gain. This is why each patch is caught individually.
    /// </summary>
    /// <param name="componentsById">
    /// The cluster's components, from Catalog. A route's namespace is its component's, and this
    /// used to be read as <c>r.Component?.Namespace</c> — which worked only because the caller had
    /// assigned <c>r.Component = c</c> while walking the navigation. That assignment existed for
    /// this one line; nothing else read it.
    /// </param>
    private async Task LabelRouteNamespacesAsync(
        IClusterClient clusterAccess,
        IEnumerable<ExternalRoute> externalRoutes,
        IReadOnlyDictionary<Guid, EntKube.Contracts.Catalog.InstalledComponent> componentsById,
        IEnumerable<AppRoute> appRoutes,
        Guid clusterId,
        CancellationToken ct,
        IEnumerable<string>? extraNamespaces = null)
    {
        HashSet<string> namespaces = [
            .. extraNamespaces ?? [],
            .. externalRoutes
                .Select(r => componentsById.TryGetValue(r.ComponentId,
                    out EntKube.Contracts.Catalog.InstalledComponent? c) ? c.Namespace : null)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Select(ns => ns!),
            .. appRoutes
                .SelectMany(r => r.DeploymentRoutes)
                .Where(dr => dr.IsEnabled && dr.AppDeployment?.ClusterId == clusterId)
                .Select(dr => dr.AppDeployment?.Namespace)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Select(ns => ns!),
        ];

        if (namespaces.Count == 0) return;

        string patch = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                labels = new Dictionary<string, string>
                {
                    [ExternalRouteService.RouteNamespaceLabel] = ExternalRouteService.RouteNamespaceLabelValue,
                },
            },
        });

        foreach (string ns in namespaces)
        {
            try
            {
                await clusterAccess.PatchStrategicAsync("namespace", ns, ns, patch, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not label namespace {Namespace} for gateway route attachment", ns);
            }
        }
    }

    /// <summary>
    /// Central raw-apply used by all route/gateway/Istio paths. The acknowledgment gate, the
    /// temporary credential file and the kubectl invocation all live in the cluster client now,
    /// so what remains here is the one thing these callers need that the client does not do:
    /// turn a failed apply into a <see cref="KubernetesOperationResult{T}"/> instead of an
    /// exception, because every caller on this path is written to inspect a result.
    /// </summary>
    private static async Task<KubernetesOperationResult<string>> ApplyRawYamlAsync(
        IClusterClient clusterAccess, string yaml, CancellationToken ct)
    {
        try
        {
            return KubernetesOperationResult<string>.Success(
                await clusterAccess.ApplyManifestAsync(yaml, ct));
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<string>.Failure(ex.Message);
        }
    }

    // ──────── Helm install / upgrade ────────

    /// <summary>
    /// Runs "helm upgrade --install" for a HelmChart AppDeployment.
    /// Idempotent: installs on first run, upgrades on subsequent ones.
    /// Always passes --create-namespace so the target namespace is created
    /// automatically if it doesn't exist yet.
    ///
    /// If a HelmRepoUrl is configured the repo is registered (helm repo add)
    /// and updated before the install. The release name is derived from the
    /// deployment name, sanitised to Helm's DNS-compatible format.
    ///
    /// Returns the combined stdout + stderr from the helm process so the UI
    /// can show exactly what Helm reported.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> HelmInstallOrUpgradeAsync(
        Guid tenantId, Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found, or it is not this tenant's.");

        if (!deployment.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This deployment is observed only (imported / managed by ArgoCD or Flux). Enable management to let EntKube apply it.");

        // The tenant is the caller's, not one read off the row being authorized. The old form —
        // ForAsync(deployment.Cluster.TenantId, deployment.ClusterId) — compared a row against
        // itself, so the predicate could not fail; see VacuousTenantCheckTests.
        IClusterClient? clusterAccess = await clusterClients.ForAsync(tenantId, deployment.ClusterId, ct);

        if (clusterAccess is null)
            return KubernetesOperationResult<string>.Failure(
                "The deployment's cluster is not this tenant's, or has no kubeconfig configured.");

        // Check namespace governance before touching the cluster.
        string? lockedNsHelm = await GetGovernanceNamespaceAsync(deployment, ct);
        string? nsViolationHelm = CheckNamespaceGovernance(deployment.Namespace, lockedNsHelm);
        if (nsViolationHelm is not null)
            return KubernetesOperationResult<string>.Failure(nsViolationHelm);

        if (string.IsNullOrWhiteSpace(deployment.HelmChartName))
            return KubernetesOperationResult<string>.Failure(
                "No Helm chart name configured. Set the chart name in the deployment settings before installing.");

        // One action, one acknowledgment. The release itself asks; the Kyverno apply and the
        // route re-apply that follow it are the same approved act carrying on, and each of them
        // is a separate gated primitive — so without this the operator answered one dialog for
        // the release and then one per hostname for work they had just approved.
        using IDisposable operation = gate.BeginOperation(
            $"helm upgrade --install {ToHelmReleaseName(deployment.Name)} in {deployment.Namespace}");

        string releaseName = ToHelmReleaseName(deployment.Name);
        string? tempValues = null;

        try
        {

            // Register and update the Helm repo if a URL is given.
            string chartRef = deployment.HelmChartName;

            if (!string.IsNullOrWhiteSpace(deployment.HelmRepoUrl))
            {
                if (deployment.HelmRepoUrl.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
                {
                    // OCI registries cannot be added with "helm repo add" — use the full URI directly.
                    chartRef = $"{deployment.HelmRepoUrl.TrimEnd('/')}/{deployment.HelmChartName}";
                }
                else
                {
                    string repoAlias = $"entkube-{releaseName}";

                    // No --kubeconfig: `helm repo add` and `repo update` fetch an index over HTTP
                    // and write helm's local cache. They never contact a cluster — verified by
                    // running both with a malformed kubeconfig and with a path that does not
                    // exist, which succeed. Passing it made them look like cluster operations in
                    // every census of where the credential travels.
                    await RunCliAsync("helm",
                        $"repo add {repoAlias} {deployment.HelmRepoUrl} --force-update", ct);
                    await RunCliAsync("helm", $"repo update {repoAlias}", ct);

                    chartRef = $"{repoAlias}/{deployment.HelmChartName}";
                }
            }

            // Build the main helm upgrade --install command.
            // The verb is carried on the invocation rather than here, so the gate can name it.
            List<string> args =
            [
                releaseName,
                chartRef,
                "--namespace", deployment.Namespace,
                "--create-namespace",
            ];

            if (!string.IsNullOrWhiteSpace(deployment.HelmChartVersion))
            {
                args.Add("--version");
                args.Add(deployment.HelmChartVersion);
            }

            if (!string.IsNullOrWhiteSpace(deployment.HelmValues))
            {
                tempValues = Path.Combine(Path.GetTempPath(), $"entkube-values-{Guid.NewGuid()}.yaml");
                await SecretFile.WriteAsync(tempValues, deployment.HelmValues, ct);
                args.Add("--values");
                args.Add(tempValues);
            }

            args.Add("--wait");
            args.Add("--timeout");
            args.Add("10m0s");

            // The acknowledgment moves into the seam with the call: the summary that used to be
            // passed by hand is handed over too, so the dialog says exactly what it said before.
            HelmExecutionResult result = await clusterAccess.RunHelmAsync(new HelmInvocation(
                Verb: "upgrade --install",
                ReleaseName: releaseName,
                Namespace: deployment.Namespace,
                Arguments: args,
                Summary: $"helm upgrade --install {releaseName} "
                       + $"({deployment.HelmChartName}@{deployment.HelmChartVersion}) in {deployment.Namespace}"),
                ct);

            // Anything the post-install steps want to tell the operator gets appended to Helm's
            // own output at the end, so the deploy log reads as one story.
            string? routeNote = null;

            if (result.Success)
            {
                logger.LogInformation(
                    "Helm install/upgrade {Chart} {Version} to {Namespace} succeeded for deployment {DeploymentId} by {User}",
                    deployment.HelmChartName, deployment.HelmChartVersion,
                    deployment.Namespace, deploymentId, performedBy ?? "system");
                await auditService.RecordAsync(deploymentId, "HelmInstallOrUpgrade", "HelmRelease",
                    deployment.Name, $"{deployment.HelmChartName}@{deployment.HelmChartVersion}", performedBy, ct);

                // Ensure the (possibly brand-new) namespace inherits the environment's Kyverno policies.
                await ApplyKyvernoPoliciesAsync(deployment, ct);

                // The release has just swapped out every workload and Service in the namespace,
                // which is precisely when the gateway is prone to losing this app's virtual host.
                // Re-apply the routes EntKube owns so the operator never has to do it by hand.
                routeNote = await RefreshDeploymentRoutesAsync(tenantId, deployment, ct);
            }
            else
            {
                logger.LogWarning(
                    "Helm install/upgrade failed for deployment {DeploymentId}: {Output}",
                    deploymentId, result.Output);
            }

            return result.Success
                ? KubernetesOperationResult<string>.Success(
                    string.IsNullOrWhiteSpace(routeNote) ? result.Output : $"{result.Output}\n{routeNote}")
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // ⚠️ The likeliest way to get here, and not an error. ClusterChangeGate implements
            // declare-or-refuse by THROWING: when the operator answers the acknowledgment dialog
            // with anything other than Acknowledged it records Cancelled and throws
            // OperationCanceledException. Dismissing that dialog on a Helm install therefore used
            // to terminate the circuit — a normal, deliberate operator action taking the page
            // down. It reads as a cancellation, not a failure.
            //
            // The `when` keeps a genuinely cancelled request — the operator navigating away —
            // falling through to the caller, where it belongs.
            return KubernetesOperationResult<string>.Failure(
                "Cancelled: the change was not acknowledged, so nothing was applied.");
        }
        catch (Exception ex)
        {
            // ⚠️ This method had a try/finally and no catch — the only cluster operation in the
            // file without one, while its YAML twin ApplyYamlDeploymentAsync and thirty others
            // have had one all along. Everything in the block above can throw: writing the
            // values file, spawning helm, and the post-deploy route refresh. Those escaped the
            // method, escaped the page handler (which also had try/finally and no catch) and
            // terminated the Blazor circuit — so a failed install showed as a dead page with
            // "unhandled exception on the current circuit" and no indication of what went wrong.
            //
            // The signature already promises a result rather than an exception. Honouring it
            // makes the cause visible where the operator is looking.
            logger.LogError(ex, "Helm install/upgrade threw for deployment {DeploymentId}", deploymentId);
            return KubernetesOperationResult<string>.Failure(
                $"Helm install/upgrade failed: {ex.Message}");
        }
        finally
        {
            // The credential is not ours any more — the seam writes and removes its own.
            if (tempValues is not null && File.Exists(tempValues)) File.Delete(tempValues);
        }
    }

    /// <summary>
    /// Queries the cluster live and returns a proper ownership tree of resources
    /// found in the deployment's namespace. OwnerReferences are used to wire up
    /// parent-child relationships (e.g. Deployment → ReplicaSet → Pod).
    /// Each resource type is fetched independently so a missing API group
    /// (e.g. no Ingress controller) doesn't block the others.
    /// </summary>
    public async Task<KubernetesOperationResult<List<DeploymentResource>>> GetLiveResourcesAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null)
            return KubernetesOperationResult<List<DeploymentResource>>.Failure(error!);

        AppDeployment deployment = target.Deployment;
        string ns = deployment.Namespace;

        // k8s UID → our resource node (for O(1) parent lookup during tree wiring)
        Dictionary<string, DeploymentResource> byUid = new();
        // Resources whose OwnerReferences need to be resolved after all types are fetched.
        List<(DeploymentResource Node, string[] OwnerUids)> pendingWire = [];
        // HTTPRoute → Service links parsed from spec.rules[].backendRefs, wired after all nodes exist.
        List<(DeploymentResource HttpRoute, string ServiceName)> httpRouteBackends = [];

        // Create a resource node, record its UID, and register its owner refs for later wiring.
        DeploymentResource MakeNode(string? uid, IList<V1OwnerReference>? owners,
            string group, string version, string kind, string name,
            HealthStatus health, string? message, DateTime? createdAt = null)
        {
            DeploymentResource r = new()
            {
                Id = !string.IsNullOrEmpty(uid) && Guid.TryParse(uid, out Guid k8sGuid)
                    ? k8sGuid : Guid.NewGuid(),
                DeploymentId = deploymentId,
                Group = group, Version = version, Kind = kind,
                Name = name, Namespace = ns,
                SyncStatus = SyncStatus.Synced,
                HealthStatus = health,
                StatusMessage = message,
                KubernetesCreatedAt = createdAt,
                ChildResources = []
            };

            if (!string.IsNullOrEmpty(uid))
                byUid[uid] = r;

            string[] ownerUids = owners?
                .Select(o => o.Uid).Where(u => !string.IsNullOrEmpty(u)).ToArray() ?? [];

            if (ownerUids.Length > 0)
                pendingWire.Add((r, ownerUids));

            return r;
        }

        try
        {
            Kubernetes client = target.Cluster.GetPooledSdkClient();

            // ── Deployments ───────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1DeploymentList list = await client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct);
                foreach (V1Deployment d in list.Items)
                {
                    int desired = d.Spec?.Replicas ?? 0;
                    int ready   = d.Status?.ReadyReplicas ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(d.Metadata.Uid, d.Metadata.OwnerReferences,
                        "apps", "v1", "Deployment", d.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── StatefulSets ─────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1StatefulSetList list = await client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct);
                foreach (V1StatefulSet s in list.Items)
                {
                    int desired = s.Spec?.Replicas ?? 0;
                    int ready   = s.Status?.ReadyReplicas ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(s.Metadata.Uid, s.Metadata.OwnerReferences,
                        "apps", "v1", "StatefulSet", s.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── DaemonSets ───────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1DaemonSetList list = await client.AppsV1.ListNamespacedDaemonSetAsync(ns, cancellationToken: ct);
                foreach (V1DaemonSet ds in list.Items)
                {
                    int desired = ds.Status?.DesiredNumberScheduled ?? 0;
                    int ready   = ds.Status?.NumberReady ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(ds.Metadata.Uid, ds.Metadata.OwnerReferences,
                        "apps", "v1", "DaemonSet", ds.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── ReplicaSets (ALL — including 0-replica old ones for tree completeness) ─
            await TryFetch(async () =>
            {
                V1ReplicaSetList list = await client.AppsV1.ListNamespacedReplicaSetAsync(ns, cancellationToken: ct);
                foreach (V1ReplicaSet rs in list.Items)
                {
                    int desired = rs.Spec?.Replicas ?? 0;
                    int ready   = rs.Status?.ReadyReplicas ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(rs.Metadata.Uid, rs.Metadata.OwnerReferences,
                        "apps", "v1", "ReplicaSet", rs.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── Services ────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1ServiceList list = await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct);
                foreach (V1Service svc in list.Items)
                {
                    MakeNode(svc.Metadata.Uid, svc.Metadata.OwnerReferences,
                        "", "v1", "Service", svc.Metadata.Name, HealthStatus.Healthy,
                        $"{svc.Spec?.Type ?? "ClusterIP"}  {svc.Spec?.ClusterIP}");
                }
            });

            // ── Ingresses ───────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1IngressList list = await client.NetworkingV1.ListNamespacedIngressAsync(ns, cancellationToken: ct);
                foreach (V1Ingress ing in list.Items)
                {
                    string? lb = ing.Status?.LoadBalancer?.Ingress?.FirstOrDefault()?.Ip
                              ?? ing.Status?.LoadBalancer?.Ingress?.FirstOrDefault()?.Hostname;
                    string hosts = string.Join(", ", (ing.Spec?.Rules?.Select(r => r.Host) ?? [])
                        .Where(h => !string.IsNullOrEmpty(h))!);
                    MakeNode(ing.Metadata.Uid, ing.Metadata.OwnerReferences,
                        "networking.k8s.io", "v1", "Ingress", ing.Metadata.Name, HealthStatus.Healthy,
                        lb ?? hosts);
                }
            });

            // ── HTTPRoutes (Gateway API) ─────────────────────────────────
            await TryFetch(async () =>
            {
                object raw = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                    "gateway.networking.k8s.io", "v1", ns, "httproutes", cancellationToken: ct);

                string json = JsonSerializer.Serialize(raw);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return;

                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("metadata", out JsonElement meta)) continue;
                    string? name = meta.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? uid  = meta.TryGetProperty("uid",  out JsonElement u) ? u.GetString() : null;
                    if (name is null) continue;

                    string? hostnames = null;
                    List<string> backendSvcNames = [];

                    if (item.TryGetProperty("spec", out JsonElement itemSpec))
                    {
                        if (itemSpec.TryGetProperty("hostnames", out JsonElement hh))
                            hostnames = string.Join(", ", hh.EnumerateArray()
                                .Select(x => x.GetString()).OfType<string>());

                        if (itemSpec.TryGetProperty("rules", out JsonElement rules))
                        {
                            foreach (JsonElement rule in rules.EnumerateArray())
                            {
                                if (!rule.TryGetProperty("backendRefs", out JsonElement backendRefs)) continue;
                                foreach (JsonElement backendRef in backendRefs.EnumerateArray())
                                {
                                    string? svcName = backendRef.TryGetProperty("name", out JsonElement nm)
                                        ? nm.GetString() : null;
                                    if (!string.IsNullOrEmpty(svcName))
                                        backendSvcNames.Add(svcName);
                                }
                            }
                        }
                    }

                    // Collect owner UIDs if present
                    List<V1OwnerReference>? ownerRefs = null;
                    if (meta.TryGetProperty("ownerReferences", out JsonElement ownersEl))
                    {
                        ownerRefs = ownersEl.EnumerateArray()
                            .Select(o => new V1OwnerReference
                            {
                                Uid = o.TryGetProperty("uid", out JsonElement oUid) ? oUid.GetString() : null
                            })
                            .ToList();
                    }

                    DeploymentResource routeNode = MakeNode(uid, ownerRefs,
                        "gateway.networking.k8s.io", "v1", "HTTPRoute", name,
                        HealthStatus.Healthy, hostnames);

                    foreach (string svcName in backendSvcNames)
                        httpRouteBackends.Add((routeNode, svcName));
                }
            }, "HTTPRoute");

            // ── PersistentVolumeClaims ───────────────────────────────────
            await TryFetch(async () =>
            {
                V1PersistentVolumeClaimList list = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct);
                foreach (V1PersistentVolumeClaim pvc in list.Items)
                {
                    HealthStatus h = pvc.Status?.Phase == "Bound"   ? HealthStatus.Healthy
                                   : pvc.Status?.Phase == "Pending" ? HealthStatus.Progressing
                                                                     : HealthStatus.Degraded;
                    string? size = pvc.Spec?.Resources?.Requests is { } req &&
                                   req.TryGetValue("storage", out ResourceQuantity? qty)
                                   ? qty?.ToString() : null;
                    MakeNode(pvc.Metadata.Uid, pvc.Metadata.OwnerReferences,
                        "", "v1", "PersistentVolumeClaim", pvc.Metadata.Name, h,
                        string.Join("  ", new[] { pvc.Status?.Phase, size }.Where(s => s != null)));
                }
            });

            // ── ConfigMaps (exclude system-generated and owned ones) ─────
            await TryFetch(async () =>
            {
                V1ConfigMapList list = await client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct);
                foreach (V1ConfigMap cm in list.Items.Where(c =>
                    c.Metadata.Name != "kube-root-ca.crt" &&
                    c.Metadata.OwnerReferences?.Any() != true))
                {
                    MakeNode(cm.Metadata.Uid, null,
                        "", "v1", "ConfigMap", cm.Metadata.Name, HealthStatus.Healthy, null);
                }
            });

            // ── Secrets (exclude service-account tokens and system-owned) ────────
            await TryFetch(async () =>
            {
                V1SecretList list = await client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: ct);
                foreach (V1Secret secret in list.Items.Where(s =>
                    s.Metadata.OwnerReferences?.Any() != true &&
                    s.Type != "kubernetes.io/service-account-token" &&
                    s.Type != "bootstrap.kubernetes.io/token"))
                {
                    string? msg = string.IsNullOrEmpty(secret.Type) || secret.Type == "Opaque"
                        ? null : secret.Type;
                    MakeNode(secret.Metadata.Uid, null,
                        "", "v1", "Secret", secret.Metadata.Name, HealthStatus.Healthy, msg);
                }
            });

            // ── Jobs ─────────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1JobList list = await client.BatchV1.ListNamespacedJobAsync(ns, cancellationToken: ct);
                foreach (V1Job job in list.Items)
                {
                    bool active    = (job.Status?.Active    ?? 0) > 0;
                    bool succeeded = (job.Status?.Succeeded ?? 0) > 0;
                    bool failed    = (job.Status?.Failed    ?? 0) > 0;
                    HealthStatus h = failed    ? HealthStatus.Degraded
                                   : active    ? HealthStatus.Progressing
                                   : succeeded ? HealthStatus.Healthy
                                               : HealthStatus.Suspended;
                    MakeNode(job.Metadata.Uid, job.Metadata.OwnerReferences,
                        "batch", "v1", "Job", job.Metadata.Name, h,
                        $"✓{job.Status?.Succeeded ?? 0}  ✗{job.Status?.Failed ?? 0}");
                }
            });

            // ── CronJobs ──────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1CronJobList list = await client.BatchV1.ListNamespacedCronJobAsync(ns, cancellationToken: ct);
                foreach (V1CronJob cj in list.Items)
                {
                    MakeNode(cj.Metadata.Uid, null,
                        "batch", "v1", "CronJob", cj.Metadata.Name,
                        cj.Spec?.Suspend == true ? HealthStatus.Suspended : HealthStatus.Healthy,
                        cj.Spec?.Schedule);
                }
            });

            // ── Pods ──────────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1PodList list = await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
                foreach (V1Pod pod in list.Items)
                {
                    int ready = pod.Status?.ContainerStatuses?.Count(cs => cs.Ready) ?? 0;
                    int total = pod.Spec?.Containers?.Count ?? 0;
                    HealthStatus h = pod.Status?.Phase switch
                    {
                        "Running"   => pod.Status.ContainerStatuses?.All(cs => cs.Ready) == true
                                        ? HealthStatus.Healthy : HealthStatus.Degraded,
                        "Pending"   => HealthStatus.Progressing,
                        "Succeeded" => HealthStatus.Healthy,
                        "Failed"    => HealthStatus.Degraded,
                        _           => HealthStatus.Unknown
                    };
                    MakeNode(pod.Metadata.Uid, pod.Metadata.OwnerReferences,
                        "", "v1", "Pod", pod.Metadata.Name, h,
                        $"{pod.Status?.Phase}  {ready}/{total}",
                        pod.Metadata?.CreationTimestamp);
                }
            });

            // ── Wire parent-child using OwnerReferences ───────────────────
            foreach ((DeploymentResource node, string[] ownerUids) in pendingWire)
            {
                foreach (string ownerUid in ownerUids)
                {
                    if (byUid.TryGetValue(ownerUid, out DeploymentResource? parent))
                    {
                        node.ParentResourceId = parent.Id;
                        parent.ChildResources.Add(node);
                        break;
                    }
                }
            }

            // ── Wire HTTPRoute → Service links via backendRefs ────────────
            // Services have no OwnerReferences to HTTPRoutes; we derive the link
            // from the HTTPRoute's spec.rules[].backendRefs parsed during fetch.
            Dictionary<string, DeploymentResource> servicesByName = byUid.Values
                .Where(r => r.Kind == "Service")
                .ToDictionary(r => r.Name, r => r);

            foreach ((DeploymentResource httpRoute, string svcName) in httpRouteBackends)
            {
                if (servicesByName.TryGetValue(svcName, out DeploymentResource? svc)
                    && svc.ParentResourceId == null)
                {
                    svc.ParentResourceId = httpRoute.Id;
                    httpRoute.ChildResources.Add(svc);
                }
            }

            // ── Build full root list ──────────────────────────────────────
            List<DeploymentResource> roots = byUid.Values
                .Where(r => r.ParentResourceId == null)
                .OrderBy(r => KindDisplayOrder(r.Kind))
                .ThenBy(r => r.Name)
                .ToList();

            // ── Filter roots to this specific deployment ──────────────────
            // If the deployment has manifests (Yaml/Manual/Git types), use
            // (Kind, Name) pairs from those manifests as an allow-list so that
            // resources from other workloads sharing the same namespace are excluded.
            // For HelmChart/GitHelm deployments (no manifest rows), fall back to
            // release-name prefix matching since Helm names resources after the release.
            HashSet<(string Kind, string Name)>? manifestFilter =
                await LoadManifestFilterAsync(deploymentId, ct);

            // Route-associated Services and HTTPRoutes are not in the manifest store but
            // should always appear in the tree alongside the workload they expose.
            HashSet<(string Kind, string Name)> routeFilter =
                await LoadRouteFilterAsync(deploymentId, ct);

            bool RouteMatch(DeploymentResource r) =>
                routeFilter.Count > 0 && routeFilter.Contains((r.Kind, r.Name));

            if (manifestFilter is { Count: > 0 })
            {
                roots = roots.Where(r => manifestFilter.Contains((r.Kind, r.Name)) || RouteMatch(r)).ToList();
            }
            else if (deployment.Type is DeploymentType.HelmChart or DeploymentType.GitHelm)
            {
                string release = deployment.Name;
                roots = roots
                    .Where(r => r.Name == release
                             || r.Name.StartsWith(release + "-", StringComparison.Ordinal)
                             || RouteMatch(r))
                    .ToList();
            }
            else if (deployment.Type != DeploymentType.GitAppOfApps)
            {
                string namePrefix = deployment.Name;
                roots = roots
                    .Where(r => r.Name == namePrefix
                             || r.Name.StartsWith(namePrefix + "-", StringComparison.Ordinal)
                             || RouteMatch(r))
                    .ToList();
            }
            // else (GitAppOfApps): manages the entire namespace — show everything.

            SortChildResources(roots);

            return KubernetesOperationResult<List<DeploymentResource>>.Success(roots);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<DeploymentResource>>.Failure(
                $"Failed to query cluster: {ex.Message}");
        }
    }

    /// <summary>
    /// Derives the overall sync + health status from a live resource tree.
    /// Called after GetLiveResourcesAsync so the UI can persist and display
    /// up-to-date status badges without a second cluster round-trip.
    ///
    /// Rules:
    /// - Empty tree → Unknown / Unknown
    /// - Any resources present → Synced
    /// - Health = worst-case across all root workload nodes
    ///   (Degraded > Progressing > Suspended > Healthy > Unknown)
    /// </summary>
    public static (SyncStatus Sync, HealthStatus Health) ComputeStatusFromResources(
        List<DeploymentResource> roots)
    {
        if (roots.Count == 0)
            return (SyncStatus.Unknown, HealthStatus.Unknown);

        // Prefer workload kinds for the aggregate; fall back to all roots.
        List<DeploymentResource> workloads = roots
            .Where(r => r.Kind is "Deployment" or "StatefulSet" or "DaemonSet")
            .ToList();

        if (workloads.Count == 0)
            workloads = roots;

        static int HealthSeverity(HealthStatus h) => h switch
        {
            HealthStatus.Degraded    => 4,
            HealthStatus.Missing     => 4,
            HealthStatus.Progressing => 3,
            HealthStatus.Suspended   => 2,
            HealthStatus.Healthy     => 1,
            _                        => 0,
        };

        HealthStatus worst = workloads
            .OrderByDescending(w => HealthSeverity(w.HealthStatus))
            .First()
            .HealthStatus;

        // Map Missing → Degraded for the top-level badge (Missing is a resource-level concept).
        if (worst == HealthStatus.Missing)
            worst = HealthStatus.Degraded;

        return (SyncStatus.Synced, worst);
    }

    /// <summary>
    /// Derives sync + health status from a list of live pods.
    /// Used in portal views that already have the pod list available.
    /// </summary>
    public static (SyncStatus Sync, HealthStatus Health) ComputeStatusFromPods(
        List<PodInfo> pods)
    {
        if (pods.Count == 0)
            return (SyncStatus.Unknown, HealthStatus.Unknown);

        bool allReady  = pods.All(p =>
            p.Status == "Running" && p.ReadyContainers == p.TotalContainers);
        bool anyFailed  = pods.Any(p => p.Status == "Failed");
        bool anyPending = pods.Any(p => p.Status == "Pending");
        bool anyRunning = pods.Any(p => p.Status == "Running");

        HealthStatus health =
            anyFailed            ? HealthStatus.Degraded
            : allReady           ? HealthStatus.Healthy
            : anyPending         ? HealthStatus.Progressing
            : anyRunning         ? HealthStatus.Degraded   // running but not all ready
                                 : HealthStatus.Unknown;

        return (SyncStatus.Synced, health);
    }

    /// <summary>
    /// Returns a set of (Kind, Name) pairs from the deployment's stored manifests,
    /// used to filter live namespace resources to just those belonging to this deployment.
    /// Returns null if the deployment has no manifests (Helm or new Manual deployment).
    ///
    /// A single manifest row may contain multiple YAML documents (separated by "---"),
    /// so we parse the YamlContent of each row to extract ALL resources, not just the
    /// stored Kind/Name columns which only capture the first resource in the row.
    /// </summary>
    private async Task<HashSet<(string Kind, string Name)>?> LoadManifestFilterAsync(
        Guid deploymentId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId)
            .ToListAsync(ct);

        if (manifests.Count == 0) return null;

        HashSet<(string Kind, string Name)> filter = [];

        foreach (DeploymentManifest manifest in manifests)
        {
            // Always include the stored Kind/Name (fast path, no parsing needed).
            filter.Add((manifest.Kind, manifest.Name));

            // Also parse the full YAML content — a single manifest row can contain
            // multiple documents separated by "---" (e.g. when pasted manually).
            foreach (string doc in manifest.YamlContent.Split("\n---", StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = doc.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                (string kind, string name) = ExtractKindAndName(trimmed);
                if (kind != "Unknown" && name != "unnamed")
                    filter.Add((kind, name));
            }
        }

        return filter;
    }

    private static (string Kind, string Name) ExtractKindAndName(string yaml)
    {
        string kind = "Unknown";
        string name = "unnamed";

        foreach (string line in yaml.Split('\n'))
        {
            if (line.StartsWith("kind:", StringComparison.Ordinal))
                kind = line["kind:".Length..].Trim();
            else if (line.TrimStart().StartsWith("name:", StringComparison.Ordinal) && name == "unnamed")
                name = line.Split(':')[1].Trim().Trim('"');
        }

        return (kind, name);
    }

    /// <summary>
    /// Returns (Kind, Name) pairs for Services and HTTPRoutes created by the AppRoute
    /// system for this deployment. These are never in the manifest store but should
    /// still appear in the live resource tree next to the workload they expose.
    /// </summary>
    private async Task<HashSet<(string Kind, string Name)>> LoadRouteFilterAsync(
        Guid deploymentId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AppDeploymentRoute> routes = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
            .Where(r => r.AppDeploymentId == deploymentId && r.IsEnabled)
            .ToListAsync(ct);

        HashSet<(string, string)> filter = [];

        foreach (AppDeploymentRoute route in routes)
        {
            if (!string.IsNullOrEmpty(route.ServiceName))
                filter.Add(("Service", route.ServiceName));

            if (route.AppRoute?.Hostname is { Length: > 0 } hostname)
            {
                string routeName = ExternalRouteService.ToListenerName(hostname) + "-route";
                filter.Add(("HTTPRoute", routeName));
            }
        }

        return filter;
    }

    private static void SortChildResources(IEnumerable<DeploymentResource> nodes)
    {
        foreach (DeploymentResource node in nodes)
        {
            if (node.ChildResources is List<DeploymentResource> kids && kids.Count > 1)
            {
                kids.Sort((a, b) =>
                {
                    int ko = KindDisplayOrder(a.Kind).CompareTo(KindDisplayOrder(b.Kind));
                    return ko != 0 ? ko : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
                SortChildResources(kids);
            }
        }
    }

    private static int KindDisplayOrder(string kind) => kind switch
    {
        "Deployment"            => 0,
        "StatefulSet"           => 1,
        "DaemonSet"             => 2,
        "CronJob"               => 3,
        "Job"                   => 4,
        "Service"               => 5,
        "Ingress"               => 6,
        "HTTPRoute"             => 7,
        "PersistentVolumeClaim" => 8,
        "ConfigMap"             => 9,
        "Secret"                => 10,
        "ReplicaSet"            => 11,
        "Pod"                   => 12,
        _                      => 99
    };

    /// Runs a fetch action and swallows errors — a missing API group
    /// (e.g. no Ingress controller) should not abort the entire resource list.
    private async Task TryFetch(Func<Task> fetch, string resourceType = "")
    {
        try { await fetch(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Non-fatal: could not fetch {ResourceType} from cluster", resourceType);
        }
    }

    // ──────── CronJob operations ────────

    /// <summary>
    /// Creates a Job from a CronJob immediately — equivalent to
    /// "kubectl create job --from=cronjob/&lt;name&gt;". The job name is derived
    /// from the CronJob name and the current Unix timestamp to stay unique.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> TriggerCronJobAsync(
        Guid tenantId, Guid deploymentId, string cronJobName,
        string? performedBy = null, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult<string>.Failure(error!);

        AppDeployment deployment = target.Deployment;

        // Build a unique, K8s-safe job name (max 63 chars).
        string raw = $"{cronJobName}-manual-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        string jobName = System.Text.RegularExpressions.Regex.Replace(
            raw.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (jobName.Length > 63) jobName = jobName[..63].TrimEnd('-');

        // Through the seam, which raises the acknowledgment and supplies the credential. "Run
        // this CronJob now" is its own verb there: the template lives on the cluster, so there is
        // no manifest for EntKube to apply and nothing to reconstruct.
        HelmExecutionResult result = await target.Cluster.CreateJobFromCronJobAsync(
            jobName, cronJobName, deployment.Namespace,
            $"Create Job/{jobName} from CronJob/{cronJobName}", ct);

        if (result.Success)
        {
            logger.LogInformation("CronJob {CronJob} triggered as Job {Job} in {Namespace} by {User}",
                cronJobName, jobName, deployment.Namespace, performedBy ?? "system");
            await auditService.RecordAsync(deploymentId, "TriggerCronJob", "CronJob",
                cronJobName, $"job={jobName}", performedBy, ct);
        }

        return result.Success
            ? KubernetesOperationResult<string>.Success($"Job '{jobName}' created.")
            : KubernetesOperationResult<string>.Failure(result.Output);
    }

    /// <summary>
    /// Patches spec.suspend on a CronJob to pause or resume its schedule.
    /// </summary>
    public async Task<KubernetesOperationResult> SetCronJobSuspendedAsync(
        Guid tenantId, Guid deploymentId, string name, bool suspend,
        string? performedBy = null, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult.Failure(error!);

        AppDeployment deployment = target.Deployment;

        try
        {
            string suspendValue = suspend ? "true" : "false";

            await target.Cluster.AcknowledgeAsync(new PlannedChange
            {
                Verb = ChangeVerb.Patch,
                Resource = "cronjob",
                Name = name,
                Namespace = deployment.Namespace,
                Summary = $"{(suspend ? "Suspend" : "Resume")} CronJob/{name}",
                Patch = $"spec.suspend = {suspendValue}",
            }, ct);

            Kubernetes client = target.Cluster.GetPooledSdkClient();
            V1Patch patch = new(
                $"{{\"spec\":{{\"suspend\":{suspendValue}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.BatchV1.PatchNamespacedCronJobAsync(
                patch, name, deployment.Namespace, cancellationToken: ct);

            string action = suspend ? "SuspendCronJob" : "ResumeCronJob";
            logger.LogInformation("CronJob {Name} {Action} in {Namespace} by {User}",
                name, action, deployment.Namespace, performedBy ?? "system");
            await auditService.RecordAsync(deploymentId, action, "CronJob",
                name, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult.Failure(
                $"Failed to {(suspend ? "suspend" : "resume")} CronJob: {ex.Message}");
        }
    }

    // ──────── Resource YAML ────────

    /// <summary>
    /// Turns EntKube management of a deployment on or off. Enabling first refreshes the
    /// stored manifests from the live cluster — so EntKube adopts whatever ArgoCD/Flux
    /// last applied rather than the (possibly stale) import-time snapshot — then marks
    /// the deployment managed so it can be applied. Disabling only flips the flag and
    /// never touches the cluster (the workload keeps running under its current owner).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> SetDeploymentManagementAsync(
        Guid tenantId, Guid deploymentId, bool managed, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult<string>.Failure(error!);

        AppDeployment deployment = target.Deployment;

        if (deployment.IsManaged == managed)
            return KubernetesOperationResult<string>.Success("No change.");

        if (!managed)
        {
            await UpdateDeploymentIsManagedAsync(deploymentId, false, ct);
            return KubernetesOperationResult<string>.Success(
                "Management disabled — EntKube will no longer apply this deployment.");
        }

        // Enabling → adopt the current live spec before allowing apply. The credential check this
        // used to make is the seam's answer above: there is a reachable cluster or there is not.
        (int refreshed, int missing) =
            await RefreshManifestsFromLiveAsync(target.Cluster, deployment, ct);

        await UpdateDeploymentIsManagedAsync(deploymentId, true, ct);

        string note = $"Management enabled. Refreshed {refreshed} manifest(s) from the live cluster";
        note += missing > 0 ? $"; {missing} not found live (kept as imported)." : ".";
        return KubernetesOperationResult<string>.Success(note);
    }

    private async Task UpdateDeploymentIsManagedAsync(Guid deploymentId, bool managed, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppDeployment? d = await db.AppDeployments.FirstOrDefaultAsync(x => x.Id == deploymentId, ct);
        if (d is null) return;
        d.IsManaged = managed;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Re-reads each stored manifest from the cluster (kubectl get -o json), sanitizes
    /// it the same way the importer does, and overwrites the stored YAML — so the
    /// deployment reflects the current live spec. Best-effort: a resource that no longer
    /// exists live is left as-is and counted as missing.
    /// </summary>
    private async Task<(int refreshed, int missing)> RefreshManifestsFromLiveAsync(
        IClusterClient cluster, AppDeployment deployment, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deployment.Id)
            .ToListAsync(ct);

        if (manifests.Count == 0)
            return (0, 0);

        int refreshed = 0, missing = 0;
        {
            foreach (DeploymentManifest m in manifests)
            {
                // One named resource as JSON, through the seam. A resource the cluster no longer
                // has comes back unsuccessful, which is counted rather than raised — the whole
                // method is best-effort adoption of whatever is live.
                HelmExecutionResult res = await cluster.GetResourceJsonAsync(
                    m.Kind.ToLowerInvariant(), m.Name, deployment.Namespace, ct);

                if (!res.Success)
                {
                    missing++;
                    continue;
                }

                try
                {
                    JsonNode? node = JsonNode.Parse(res.Output);
                    if (node is null) { missing++; continue; }
                    m.YamlContent = ImportManifestSanitizer.ToYaml(node);
                    m.UpdatedAt = DateTime.UtcNow;
                    refreshed++;
                }
                catch
                {
                    missing++;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return (refreshed, missing);
    }

    /// <summary>
    /// Returns the raw YAML for a single namespaced resource via "kubectl get -o yaml".
    /// Useful for inspecting live resource state directly from the UI.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> GetResourceYamlAsync(
        Guid tenantId, Guid deploymentId, string kind, string name, CancellationToken ct = default)
    {
        var (target, error) = await ResolveTargetAsync(tenantId, deploymentId, ct);
        if (target is null) return KubernetesOperationResult<string>.Failure(error!);

        try
        {
            // A read, so no acknowledgment: the gate exists for changes. It used to write the
            // kubeconfig to a temp file and shell out; the seam owns both now.
            return KubernetesOperationResult<string>.Success(
                await target.Cluster.GetResourceYamlAsync(
                    kind.ToLowerInvariant(), name, target.Deployment.Namespace, ct));
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<string>.Failure(ex.Message);
        }
    }

    // ──────── Route discovery helpers ────────

    /// <summary>
    /// Lists all Services in a namespace with their ports.
    /// Used to populate the service/port selector when configuring app routes.
    /// Returns an empty list rather than throwing if the cluster is unreachable.
    /// </summary>
    public async Task<List<KubeServiceInfo>> GetServicesInNamespaceAsync(
        Guid tenantId, Guid clusterId, string ns, CancellationToken ct = default)
    {
        IClusterClient? cluster = await clusterClients.ForAsync(tenantId, clusterId, ct);
        if (cluster is null) return [];

        try
        {
            Kubernetes client = cluster.GetPooledSdkClient();
            V1ServiceList list = await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct);

            return list.Items
                .Where(svc => svc.Spec?.Type != "ExternalName")
                .Select(svc => new KubeServiceInfo(
                    svc.Metadata.Name,
                    svc.Spec?.Type ?? "ClusterIP",
                    svc.Spec?.ClusterIP,
                    (svc.Spec?.Ports ?? [])
                        .Select(p => new KubeServicePort(p.Name, p.Port, p.Protocol ?? "TCP", p.AppProtocol))
                        .ToList()))
                .OrderBy(s => s.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list services in {Namespace} on cluster {ClusterId}", ns, clusterId);
            return [];
        }
    }

    /// <summary>
    /// Returns the ready and not-ready endpoint addresses for a specific Service.
    /// Useful for showing operators whether the service has live pods behind it.
    /// </summary>
    public async Task<KubeEndpointSummary> GetEndpointsForServiceAsync(
        Guid tenantId, Guid clusterId, string ns, string serviceName, CancellationToken ct = default)
    {
        IClusterClient? cluster = await clusterClients.ForAsync(tenantId, clusterId, ct);
        if (cluster is null) return new KubeEndpointSummary([], []);

        try
        {
            Kubernetes client = cluster.GetPooledSdkClient();
            V1Endpoints ep = await client.CoreV1.ReadNamespacedEndpointsAsync(serviceName, ns, cancellationToken: ct);

            List<string> ready = [];
            List<string> notReady = [];

            foreach (V1EndpointSubset subset in ep.Subsets ?? [])
            {
                List<int> ports = (subset.Ports ?? []).Select(p => p.Port).ToList();
                string portSuffix = ports.Count > 0 ? $":{string.Join(",", ports)}" : "";

                foreach (V1EndpointAddress addr in subset.Addresses ?? [])
                    ready.Add(addr.Ip + portSuffix);

                foreach (V1EndpointAddress addr in subset.NotReadyAddresses ?? [])
                    notReady.Add(addr.Ip + portSuffix);
            }

            return new KubeEndpointSummary(ready, notReady);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not get endpoints for {Service} in {Namespace}", serviceName, ns);
            return new KubeEndpointSummary([], []);
        }
    }

    // ──────── Internal ────────

    /// <summary>
    /// Creates a Kubernetes client from a raw kubeconfig YAML string.
    /// The kubeconfig is parsed in-memory — never written to disk.
    /// </summary>
    /// <summary>
    /// Reads one Service's ports straight from the cluster, for deciding how the gateway should
    /// connect to each of them. Returns an empty list when the service can't be read — callers
    /// must treat that as "nothing known about the ports", never as "no TLS ports".
    /// </summary>
    private async Task<List<KubeServicePort>> GetServicePortsAsync(
        IClusterClient clusterAccess, string ns, string serviceName, CancellationToken ct)
    {
        try
        {
            using Kubernetes client = clusterAccess.CreateSdkClient();
            V1Service svc = await client.CoreV1.ReadNamespacedServiceAsync(serviceName, ns, cancellationToken: ct);

            return (svc.Spec?.Ports ?? [])
                .Select(p => new KubeServicePort(p.Name, p.Port, p.Protocol ?? "TCP", p.AppProtocol))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read service {Service} in {Namespace} for backend TLS detection",
                serviceName, ns);
            return [];
        }
    }

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }

    /// <summary>
    /// Converts an arbitrary deployment name into a Helm-compatible release name:
    /// lowercase, alphanumeric + hyphens only, max 53 characters.
    /// </summary>
    private static string ToHelmReleaseName(string name)
    {
        string s = name.ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9]+", "-");
        s = s.Trim('-');
        return s.Length > 53 ? s[..53].TrimEnd('-') : s;
    }

    /// <summary>
    /// Runs an external CLI process (helm, kubectl) and returns the combined
    /// stdout + stderr. Exit code 0 → Success; non-zero → Failure.
    /// </summary>
    private static async Task<HelmExecutionResult> RunCliAsync(
        string program, string arguments, CancellationToken ct)
    {
        System.Diagnostics.ProcessStartInfo psi = new()
        {
            FileName = program,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["HOME"] = "/tmp";

        using System.Diagnostics.Process process = new() { StartInfo = psi };

        try
        {
            process.Start();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            string combined = (await stdout).Trim();
            string err = (await stderr).Trim();
            if (!string.IsNullOrEmpty(err)) combined = string.IsNullOrEmpty(combined) ? err : combined + "\n" + err;

            return new HelmExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = HelmExecutionResult.DescribeLaunchFailure(program, ex)
            };
        }
    }
}
