using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Upgrades;

/// <summary>
/// Holds the most recent drift sweep per tenant.
///
/// Drift results are cached rather than persisted, matching how the Operations
/// Advisor already treats findings: they are an observation of live cluster state,
/// not a record worth surviving a restart. Keeping them out of the database also
/// keeps this feature migration-free.
///
/// The trade-off is deliberate and worth stating: the cache is per-process, so a
/// multi-instance deployment gives each instance its own view until its own sweep
/// runs, and a restart shows "not scanned yet" until the first sweep completes.
/// That is acceptable for an advisory signal; it would not be for anything that
/// gates a mutation.
/// </summary>
public class DriftScanCache
{
    private readonly ConcurrentDictionary<Guid, DriftReport> reports = new();

    /// <summary>The latest sweep for a tenant, or null when none has completed yet.</summary>
    public DriftReport? Get(Guid tenantId) => reports.GetValueOrDefault(tenantId);

    public void Set(Guid tenantId, DriftReport report) => reports[tenantId] = report;

    /// <summary>
    /// Replaces one deployment's row in the cached sweep, leaving the rest untouched.
    ///
    /// Acting on a single deployment must not discard everything else the last sweep
    /// found — re-running the whole sweep to refresh one row would walk every cluster,
    /// and dropping the cache entirely would make the advisor briefly report that
    /// nothing has drifted, which is a false all-clear.
    /// </summary>
    public void Replace(Guid tenantId, DriftResult result)
    {
        reports.AddOrUpdate(
            tenantId,
            _ => new DriftReport { Results = [result], GeneratedAt = result.CheckedAt },
            (_, existing) => existing with
            {
                Results = [.. existing.Results
                    .Where(r => r.DeploymentId != result.DeploymentId)
                    .Append(result)
                    .OrderBy(r => r.State)
                    .ThenByDescending(r => r.ChangedLines)
                    .ThenBy(r => r.AppName, StringComparer.OrdinalIgnoreCase)],
            });
    }

    /// <summary>Drops a tenant's cached sweep — used when a tenant is removed.</summary>
    public void Clear(Guid tenantId) => reports.TryRemove(tenantId, out _);
}

/// <summary>
/// Periodically sweeps every tenant for configuration drift so the Advisor and the
/// Drift tab have an answer without either of them paying for a fan-out of kubectl
/// subprocesses on a page render.
/// </summary>
public class DriftScanService(
    IServiceScopeFactory scopeFactory,
    DriftScanCache cache,
    ILogger<DriftScanService> logger) : BackgroundService
{
    /// <summary>
    /// Drift is caused by humans editing clusters, which happens on a timescale of hours,
    /// not seconds. A sweep costs one server-side dry-run per managed deployment, so
    /// running it more often would spend real API-server budget to learn nothing new.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    /// <summary>
    /// Deliberately later than the advisor's own startup delay: a fresh process should
    /// finish booting and serve pages before it starts forking kubectl at every cluster.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Drift scan cycle failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var drift = scope.ServiceProvider.GetRequiredService<DriftDetectionService>();

        List<Guid> tenantIds;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                DriftReport report = await drift.GetTenantDriftAsync(tenantId, DateTime.UtcNow, ct);
                cache.Set(tenantId, report);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One tenant's unreachable clusters must not stop the others being swept.
                logger.LogWarning(ex, "Drift sweep failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
