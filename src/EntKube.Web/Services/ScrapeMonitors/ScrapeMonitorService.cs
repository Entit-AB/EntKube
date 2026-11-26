using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Web.Data;
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
    ///
    /// <para>Not while another instance's sweep is writing them (<see cref="ScrapeMonitorClaim"/>):
    /// that is a failure that changed nothing, and pressing again a minute later works.</para>
    /// </summary>
    public async Task<KubernetesOperationResult<ScrapeMonitorPlan>> ApplyAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        // Up to two writes, and the operator chose one thing: opened before the first of them.
        using IDisposable operation = gate.BeginOperation("apply monitors");

        try
        {
            // Held while the dialog is open, too: what the operator is asked about is what is written.
            await using IAsyncDisposable? claim = await TryClaimAsync(clusterId, ct);
            if (claim is null)
            {
                return KubernetesOperationResult<ScrapeMonitorPlan>.Failure(
                    "Another EntKube instance is writing the monitors on this cluster right now. "
                    + "Nothing was changed; try again in a minute.");
            }

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
            // From before the read until after the write: another instance's write in between would
            // make this one's plan out of date.
            await using IAsyncDisposable? claim = await TryClaimAsync(clusterId, ct);
            if (claim is null)
            {
                // Its pass, not a failure: nothing to back off from, and the next pass may be ours.
                logger.LogDebug("Monitors on cluster {ClusterId} are being reconciled by another instance", clusterId);
                return;
            }

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

    /// <summary>
    /// The same for the same set of changes, so that a failure is remembered for those alone.
    ///
    /// <para><b>Whatever order they were read in.</b> The plan lists monitors in the order of its
    /// targets, which is the order the database returned its rows, and no query below orders them:
    /// Postgres hands an updated row back in another place. Taken in that order, a status change on
    /// one database would start a refusing cluster's wait over, and every pass would be an attempt
    /// and an audit row again.</para>
    /// </summary>
    private static string Fingerprint(ScrapeMonitorPlan plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ScrapeMonitorManifest.RenderNames(plan.Delete.Order(ByIdentity)) + "\n===\n"
            + ScrapeMonitorManifest.Render(plan.Apply.OrderBy(m => m.Identity, ByIdentity).Select(m => m.Manifest)))));

    /// <summary>By kind, then namespace, then name: one order for the same monitors, however they came.</summary>
    private static readonly IComparer<MonitorIdentity> ByIdentity = Comparer<MonitorIdentity>.Create((a, b) =>
    {
        int kind = a.Kind.CompareTo(b.Kind);
        if (kind != 0) return kind;

        int ns = string.CompareOrdinal(a.Namespace, b.Namespace);
        return ns != 0 ? ns : string.CompareOrdinal(a.Name, b.Name);
    });

    /// <summary>
    /// This instance's claim on one cluster's monitors, or null while another instance has it
    /// (<see cref="ScrapeMonitorClaim"/>). Virtual so the tests can be two instances at once.
    /// </summary>
    protected virtual Task<IAsyncDisposable?> TryClaimAsync(Guid clusterId, CancellationToken ct)
        => ScrapeMonitorClaim.TryTakeAsync(dbFactory, clusterId, logger, ct);

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

/// <summary>
/// Which instance may read and write one cluster's monitors now: a database advisory lock keyed by
/// the cluster, taken before the read and given back after the write.
///
/// <para><b>Why there has to be one.</b> Every instance runs the sweep (the singleton-work flag is
/// placement, not a lease), and a panel's button can be pressed on any of them. Two that read the
/// same cluster before either writes plan the same changes, and both carry them out: two audit
/// rows where one would do, and where a monitor is made again, the second one's delete, planned
/// from a read before the first one's apply, removes the monitor the first one has just made.</para>
///
/// <para><b>Why this lock.</b> It is <see cref="AdvisoryLock"/>, the one the migrations and the
/// singleton witness take, with a key of its own for each cluster. It is released when the
/// connection closes, so an instance that dies holding it holds nothing. No table and no
/// migration, and no staleness rule to get wrong.</para>
///
/// <para><b>What it does not share.</b> <see cref="ScrapeMonitorBackoff"/> stays in each
/// instance's memory. With the lock, two instances never write one cluster at once; without a
/// shared backoff, each of them still waits on its own clock after a refusal.</para>
/// </summary>
public static class ScrapeMonitorClaim
{
    /// <summary>What SQL Server's lock is called, and what the Postgres key is derived from.</summary>
    public static string Resource(Guid clusterId) => $"EntKube:ScrapeMonitors:{clusterId:D}";

    /// <summary>
    /// The Postgres key for one cluster. Derived and not stored, so every instance asks for the same
    /// number; from SHA-256 and not <c>GetHashCode</c>, which is different in every process.
    ///
    /// <para><b>Never negative.</b> <see cref="AdvisoryLock"/> writes the key into the statement in
    /// the current culture, and Swedish writes a minus as U+2212, which Postgres reads as the name of
    /// a column: half of all clusters could never be claimed. A key without a sign reads the same in
    /// every culture.</para>
    /// </summary>
    public static long Key(Guid clusterId)
        => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(Resource(clusterId))))
           & long.MaxValue;

    /// <summary>The statement that asks for it without waiting, or null where the provider takes no lock.</summary>
    public static string? AcquireSql(string? provider, Guid clusterId)
        => AdvisoryLock.TryAcquireSql(provider, Key(clusterId), Resource(clusterId));

    /// <summary>
    /// The claim on one cluster, or null when another instance holds it. A database that cannot be
    /// asked is an exception: whether somebody else is writing is not known, so nothing is.
    ///
    /// <para>SQLite takes no lock and needs none: one file, one process. A provider nobody has
    /// taught a lock goes on without one and says so, as the migrations do.</para>
    /// </summary>
    public static async Task<IAsyncDisposable?> TryTakeAsync(
        IDbContextFactory<TelemetryDbContext> dbFactory, Guid clusterId, ILogger logger, CancellationToken ct)
    {
        TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        try
        {
            string? provider = db.Database.ProviderName;
            string? acquire = AcquireSql(provider, clusterId);

            if (acquire is null)
            {
                if (!AdvisoryLock.NeedsNoLock(provider))
                {
                    logger.LogWarning(
                        "No lock is implemented for provider {Provider}; the monitors on cluster {ClusterId} "
                        + "are written unclaimed, and two instances could write them at once.", provider, clusterId);
                }

                await db.DisposeAsync();
                return NoLock.Instance;
            }

            DbConnection connection = db.Database.GetDbConnection();
            await connection.OpenAsync(ct);

            bool granted;
            await using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = acquire;
                granted = AdvisoryLock.WasGranted(await command.ExecuteScalarAsync(ct));
            }

            if (!granted)
            {
                await db.DisposeAsync();
                return null;
            }

            return new Held(db, provider, clusterId, logger);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>Where there is nothing to hold.</summary>
    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The lock, on the connection that took it. Given back explicitly before the connection goes
    /// back to the pool: a pooled connection is not reset until it is next used, and until then it
    /// would go on holding the cluster for an instance that has finished with it.
    /// </summary>
    private sealed class Held(TelemetryDbContext db, string? provider, Guid clusterId, ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (AdvisoryLock.ReleaseSql(provider, Key(clusterId), Resource(clusterId)) is { } release)
                {
                    await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = release;
                    await command.ExecuteScalarAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                // Closing the connection gives it back too, once the pool lets the connection go.
                logger.LogWarning(ex, "Could not give back the claim on the monitors of cluster {ClusterId}", clusterId);
            }
            finally
            {
                await db.DisposeAsync();
            }
        }
    }
}
