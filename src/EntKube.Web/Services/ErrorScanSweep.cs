using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Reads what went wrong since the last pass and folds it into issues, tenant by tenant. The tenant is
/// an input to every read and write below, and one tenant's failure ends that tenant's turn alone.
///
/// <para>A window stops short of now, because a line reaches the store a little after it happened and
/// a window once scanned is never read again. A read that fails leaves the cursor where it was, so the
/// same window is tried again rather than lost.</para>
/// </summary>
public sealed class ErrorScanSweep(IServiceScopeFactory scopeFactory, ILogger<ErrorScanSweep> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    /// <summary>How long a line may take to be searchable.</summary>
    public static readonly TimeSpan IngestLag = TimeSpan.FromSeconds(30);

    /// <summary>The most one pass reads, so catching up after downtime stays many small passes.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(30);

    /// <summary>How far back a scope scanned for the first time looks.</summary>
    public static readonly TimeSpan FirstScanLookBack = TimeSpan.FromMinutes(30);

    public const int MaxLinesPerScan = 5000;
    public const int MaxBrowserGroups = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        DateTime nextPrune = DateTime.MinValue;

        do
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                using IDisposable unattended = scope.DeclareUnattended("error-scan");

                await RunCycleAsync(scope.ServiceProvider, stoppingToken);

                if (DateTime.UtcNow >= nextPrune)
                {
                    nextPrune = DateTime.UtcNow + PruneInterval;
                    await scope.ServiceProvider.GetRequiredService<ErrorTrackingService>().PruneAsync(DateTime.UtcNow, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A cycle that throws ends the cycle, not the host.
                logger.LogWarning(ex, "Error scan cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(IServiceProvider services, CancellationToken ct)
    {
        await using TelemetryDbContext db = await services
            .GetRequiredService<IDbContextFactory<TelemetryDbContext>>().CreateDbContextAsync(ct);

        List<Guid> tenants = await db.Tenants.Select(t => t.Id).ToListAsync(ct);

        ErrorTrackingService issues = services.GetRequiredService<ErrorTrackingService>();
        ITelemetryBackends backends = services.GetRequiredService<ITelemetryBackends>();
        IRumQueryService rum = services.GetRequiredService<IRumQueryService>();

        foreach (Guid tenantId in tenants)
        {
            try
            {
                ILogBackend log = backends.LogsFor(tenantId);

                var clusters = await db.KubernetesClusters.AsNoTracking()
                    .Where(c => c.TenantId == tenantId).Select(c => new { c.Id, c.Name }).ToListAsync(ct);

                foreach (var cluster in clusters)
                {
                    if (await log.HasDataAsync(cluster.Id, ct))
                    {
                        await ScanClusterAsync(issues, log, tenantId, cluster.Id, cluster.Name, DateTime.UtcNow, ct);
                    }
                }

                var sites = await db.RumSites.AsNoTracking()
                    .Where(s => s.TenantId == tenantId && s.IsEnabled).Select(s => new { s.Id, s.Name }).ToListAsync(ct);

                foreach (var site in sites)
                {
                    await ScanSiteAsync(issues, rum, tenantId, site.Id, site.Name, DateTime.UtcNow, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Error scan failed for tenant {Tenant}.", tenantId);
            }
        }
    }

    /// <summary>The window to read next: from the cursor to just before now, at most <see cref="MaxWindow"/>.</summary>
    public static (DateTime From, DateTime To) Window(DateTime? cursor, DateTime now)
    {
        DateTime end = now - IngestLag;
        DateTime from = cursor ?? end - FirstScanLookBack;
        DateTime to = from + MaxWindow < end ? from + MaxWindow : end;
        return (from, to);
    }

    /// <summary>One pass over a cluster's error lines. Returns how many issues were new.</summary>
    public static async Task<int> ScanClusterAsync(
        ErrorTrackingService issues, ILogBackend log, Guid tenantId, Guid clusterId, string clusterName,
        DateTime now, CancellationToken ct)
    {
        DateTime? cursor = await issues.CursorAsync(tenantId, clusterId, ct);
        (DateTime from, DateTime to) = Window(cursor, now);
        if (to <= from) return 0;

        int minutes = Math.Max(1, (int)Math.Ceiling((to - from).TotalMinutes));

        KubernetesOperationResult<List<string>> namespaces = await log.GetNamespacesAsync(clusterId, minutes, ct);
        if (!namespaces.IsSuccess) return 0;

        List<LokiLogStream> streams = [];

        if (namespaces.Data is { Count: > 0 } names)
        {
            KubernetesOperationResult<List<LokiLogStream>> read = await log.QueryAsync(
                clusterId, new LogQueryFilter { Namespaces = names, MinLevel = LogLevel.Error }, from, to, MaxLinesPerScan, ct);

            if (!read.IsSuccess) return 0;

            streams = read.Data ?? [];
        }

        if (!await issues.TryClaimAsync(tenantId, clusterId, cursor, to, ct)) return 0;

        bool partial = streams.Sum(s => s.Entries.Count) >= MaxLinesPerScan;

        return await issues.ApplyAsync(
            tenantId, ErrorIssueSource.Log, clusterId, clusterName,
            ErrorTrackingService.ObserveLogs(streams), partial, now, ct);
    }

    /// <summary>One pass over a RUM site's errors. Returns how many issues were new.</summary>
    public static async Task<int> ScanSiteAsync(
        ErrorTrackingService issues, IRumQueryService rum, Guid tenantId, Guid siteId, string siteName,
        DateTime now, CancellationToken ct)
    {
        DateTime? cursor = await issues.CursorAsync(tenantId, siteId, ct);
        (DateTime from, DateTime to) = Window(cursor, now);
        if (to <= from) return 0;

        // Null is a failed read, and the cursor must stay put so the same window is tried again.
        List<RumTopError>? errors = await rum.TryGetTopErrorsAsync(tenantId, siteId, from, to, MaxBrowserGroups, ct);
        if (errors is null) return 0;

        if (!await issues.TryClaimAsync(tenantId, siteId, cursor, to, ct)) return 0;

        return await issues.ApplyAsync(
            tenantId, ErrorIssueSource.Browser, siteId, siteName,
            ErrorTrackingService.ObserveBrowser(errors), errors.Count >= MaxBrowserGroups, now, ct);
    }
}
