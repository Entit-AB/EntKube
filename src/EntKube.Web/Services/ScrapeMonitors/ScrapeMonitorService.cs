using System.Security.Cryptography;
using System.Text;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Keeps a Prometheus monitor for every data service and infrastructure component on the clusters
/// that run EntKube's Prometheus. There is nothing to configure: a service that exists and exposes
/// metrics is scraped, and one that goes away takes its monitor with it.
///
/// <para><b>It writes ServiceMonitors and PodMonitors and nothing else.</b> Not a custom resource, a
/// Deployment, a Secret or a NetworkPolicy, so it can restart nothing and cut nothing off. What is
/// missing (an endpoint that is off, a policy that keeps Prometheus out) is reported, not changed.</para>
///
/// <para><b>It compares before it writes.</b> Every unattended write is an audit row that nothing
/// deletes, so a pass that finds everything in order makes no call that would leave one.</para>
/// </summary>
public class ScrapeMonitorService(
    IDbContextFactory<TelemetryDbContext> dbFactory,
    ICatalogApi catalog,
    IDataServicesApi dataServices,
    IClusterClientFactory clusterClients,
    ILiveScrapeStateReader reader,
    IClusterChangeGate gate,
    ILogger<ScrapeMonitorService> logger) : IScrapeMonitors
{
    /// <summary>
    /// How long reading one cluster, or writing to it with nobody waiting, may take. One that does
    /// not answer must not hold up every other tenant's.
    /// </summary>
    private static readonly TimeSpan ClusterTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How many monitors a summary names before it counts the rest.</summary>
    private const int NamedInSummary = 10;

    /// <summary>
    /// What the monitors on one cluster are and what applying would change. Reads the cluster and
    /// changes nothing.
    /// </summary>
    public async Task<KubernetesOperationResult<ScrapeMonitorPlan>> PlanAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        try
        {
            (_, ScrapeMonitorPlan plan) = await ReadAsync(tenantId, clusterId, ct);
            return KubernetesOperationResult<ScrapeMonitorPlan>.Success(plan);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not plan the monitors on cluster {ClusterId}", clusterId);
            return KubernetesOperationResult<ScrapeMonitorPlan>.Failure(Explain(ex));
        }
    }

    /// <summary>
    /// Makes the monitors on one cluster what they should be, now, with the operator asked once. The
    /// result is the plan as it stands afterwards.
    /// </summary>
    public async Task<KubernetesOperationResult<ScrapeMonitorPlan>> ApplyAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        // Up to two writes, and the operator chose one thing: opened before the first of them.
        using IDisposable operation = gate.BeginOperation("apply monitors");

        try
        {
            (IClusterClient? cluster, ScrapeMonitorPlan plan) = await ReadAsync(tenantId, clusterId, ct);

            if (cluster is null || !plan.HasChanges)
            {
                return KubernetesOperationResult<ScrapeMonitorPlan>.Success(plan);
            }

            try
            {
                // No deadline of its own: the clock would run while the operator reads the dialog.
                await WriteAsync(cluster, plan, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return KubernetesOperationResult<ScrapeMonitorPlan>.Failure("Cancelled. Nothing was changed.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not apply the monitors on cluster {ClusterId}", clusterId);
            return KubernetesOperationResult<ScrapeMonitorPlan>.Failure(Explain(ex));
        }

        // What was written is written, whether or not it can be read back.
        KubernetesOperationResult<ScrapeMonitorPlan> after = await PlanAsync(tenantId, clusterId, ct);

        return after.IsSuccess
            ? after
            : KubernetesOperationResult<ScrapeMonitorPlan>.Failure(
                $"The monitors were applied, but the cluster could not be read again afterwards: {after.Error}");
    }

    /// <summary>
    /// One pass over every tenant's clusters, acting as each tenant in turn: the tenant is an input
    /// to every lookup below it, never read off a row (docs/decomposition.md §4.0.1.2).
    ///
    /// <para>One tenant failing does not end the pass for the others, and one cluster failing does
    /// not end it for the tenant.</para>
    /// </summary>
    public async Task ReconcileAllAsync(ScrapeMonitorBackoff backoff, CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenants = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenants)
        {
            try
            {
                foreach (Guid clusterId in await ClustersWithPrometheusAsync(tenantId, ct))
                {
                    await ReconcileClusterAsync(tenantId, clusterId, backoff, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Monitors of tenant {TenantId} were not reconciled", tenantId);
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> ClustersWithPrometheusAsync(Guid tenantId, CancellationToken ct)
        =>
        [
            .. (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                .Where(PrometheusPresence.Answers)
                .Select(c => c.ClusterId)
                .Distinct()
        ];

    private async Task ReconcileClusterAsync(
        Guid tenantId, Guid clusterId, ScrapeMonitorBackoff backoff, CancellationToken ct)
    {
        try
        {
            (IClusterClient? cluster, ScrapeMonitorPlan plan) = await ReadAsync(tenantId, clusterId, ct);

            if (cluster is null || !plan.HasChanges)
            {
                // Nothing left to fail at. A later set of changes starts with a clean slate.
                backoff.Succeeded(clusterId);
                return;
            }

            string changes = Fingerprint(plan);
            if (!backoff.MayAttempt(clusterId, changes)) return;

            try
            {
                using CancellationTokenSource limited = Limited(ct);
                await WriteAsync(cluster, plan, limited.Token);
                backoff.Succeeded(clusterId);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                backoff.Failed(clusterId, changes);
                throw;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Monitors on cluster {ClusterId} of tenant {TenantId} were not reconciled",
                clusterId, tenantId);
        }
    }

    /// <summary>
    /// One cluster, read in the order that makes pruning safe.
    ///
    /// <para><b>The cluster first, the database second.</b> A monitor seen on the cluster whose owner
    /// is then missing from the database is a true orphan. The other way round, a service created
    /// between the two reads would be missing from the older list and present in the newer, and its
    /// monitor would be deleted the moment it was made.</para>
    ///
    /// <para>Any part failing is an exception out of here, so there is no plan to act on.</para>
    /// </summary>
    private async Task<(IClusterClient? Cluster, ScrapeMonitorPlan Plan)> ReadAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        using CancellationTokenSource limited = Limited(ct);
        CancellationToken token = limited.Token;

        InstalledComponent? prometheus = await catalog.FindPrometheusAsync(tenantId, clusterId, token);
        if (prometheus is null) return (null, ScrapeMonitorPlan.Nothing(MonitorPlanScope.NoPrometheus));

        IClusterClient cluster = await clusterClients.ForAsync(tenantId, clusterId, token)
            ?? throw new InvalidOperationException(
                "Cluster not found for this tenant, or it has no kubeconfig configured.");

        LiveMonitoring? monitoring = await reader.ReadMonitoringAsync(tenantId, cluster, token);
        if (monitoring is null) return (cluster, ScrapeMonitorPlan.Nothing(MonitorPlanScope.NoMonitorKinds));

        IReadOnlyList<InstalledComponent> components =
            await catalog.GetComponentsForClusterAsync(tenantId, clusterId, token);
        IReadOnlyList<ManagedServiceSummary> services =
            await dataServices.GetManagedServicesForTenantAsync(tenantId, token);

        IReadOnlyList<ScrapeTargetSpec> targets = ScrapeTargetCatalog.For(
            components, [.. services.Where(s => s.ClusterId == clusterId)]);

        PrometheusRelease release = ReleaseOf(prometheus);

        LiveWorkloads workloads = await reader.ReadWorkloadsAsync(
            cluster,
            [
                release.Namespace,
                .. monitoring.Prometheuses.Select(p => p.Namespace),
                .. targets.Where(t => t.Monitor is not null).Select(t => t.Namespace),
            ],
            token);

        return (cluster, ScrapeMonitorPlanner.Plan(
            targets, new LiveScrapeState(clusterId, release, monitoring, workloads)));
    }

    /// <summary>
    /// The cluster's Prometheus as the release Catalog records. The namespace is the one its queries
    /// go to, which a component's configuration can move.
    /// </summary>
    private static PrometheusRelease ReleaseOf(InstalledComponent prometheus)
        => new(
            PrometheusService.GetPrometheusConfig(prometheus).Namespace,
            string.IsNullOrWhiteSpace(prometheus.ReleaseName) ? prometheus.Name : prometheus.ReleaseName);

    /// <summary>
    /// Deletes, then applies: one call each, and only for what differs.
    ///
    /// <para><b>Deletes first.</b> What is removed may be what stands in the way of what is written
    /// (a monitor that has to be created again), and where one takes over from the other a scrape
    /// missed is harmless while two at once double every sum.</para>
    ///
    /// <para>A delete that fails does not keep the rest from being applied, and is reported after.
    /// One the operator declined does: the answer was about the whole of it.</para>
    /// </summary>
    private async Task WriteAsync(IClusterClient cluster, ScrapeMonitorPlan plan, CancellationToken ct)
    {
        string summary = Summary(cluster, plan);
        Exception? deleteFailed = null;

        if (plan.Delete.Count > 0)
        {
            try
            {
                await cluster.DeleteManifestSetAsync(
                    ScrapeMonitorManifest.RenderNames(plan.Delete), "", summary, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                deleteFailed = ex;
            }
        }

        if (plan.Apply.Count > 0)
        {
            await cluster.ApplyManifestInNamespaceAsync(
                ScrapeMonitorManifest.Render(plan.Apply.Select(m => m.Manifest)), "", summary, ct);
        }

        if (deleteFailed is not null)
        {
            throw new InvalidOperationException(
                $"The monitors that should go could not be removed: {deleteFailed.Message}", deleteFailed);
        }

        logger.LogInformation("{Summary}", summary);
    }

    /// <summary>
    /// What is about to happen, in the words the operator and the audit trail get. It names the
    /// cluster because the audit row has no field that does.
    /// </summary>
    private static string Summary(IClusterClient cluster, ScrapeMonitorPlan plan)
    {
        List<string> parts = [];

        if (plan.Apply.Count > 0) parts.Add($"apply {Named(plan.Apply.Select(m => m.Identity))}");
        if (plan.Prune.Count > 0) parts.Add($"remove {Named(plan.Prune)}");

        return $"Prometheus monitors on cluster {cluster.ClusterName}: {string.Join("; ", parts)}";
    }

    private static string Named(IEnumerable<MonitorIdentity> monitors)
    {
        List<MonitorIdentity> all = [.. monitors];
        string named = string.Join(", ", all.Take(NamedInSummary).Select(m => $"{m.Namespace}/{m.Name}"));

        return all.Count > NamedInSummary ? $"{named} and {all.Count - NamedInSummary} more" : named;
    }

    /// <summary>The same for the same set of changes, so that a failure is remembered for those alone.</summary>
    private static string Fingerprint(ScrapeMonitorPlan plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ScrapeMonitorManifest.RenderNames(plan.Delete) + "\n===\n"
            + ScrapeMonitorManifest.Render(plan.Apply.Select(m => m.Manifest)))));

    private static CancellationTokenSource Limited(CancellationToken ct)
    {
        CancellationTokenSource limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limited.CancelAfter(ClusterTimeout);
        return limited;
    }

    /// <summary>Why it did not work, for the person looking at the panel.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        OperationCanceledException => "The cluster did not answer in time.",
        k8s.Autorest.HttpOperationException { Response: { } response } http => $"{response.StatusCode}: {http.Message}",
        _ => ex.Message,
    };
}
