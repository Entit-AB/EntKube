using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Runs what is due, tenant by tenant. The tenant is an input to every read and write below it, the
/// same shape as the other sweeps, and one tenant's failure ends that tenant's turn and no one else's.
/// </summary>
public sealed class SyntheticCheckRunner(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SyntheticCheckRunner> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    /// <summary>Every check gets a slot before a slow target can hold up the rest.</summary>
    private const int MaxConcurrentRequests = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using HttpClient client = new(
            SyntheticProbe.CreateHandler(config.GetValue("Synthetics:AllowPrivateTargets", false)))
        {
            // The probe enforces its own deadline, and a default here would mask which one fired.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using PeriodicTimer timer = new(Tick);
        DateTime nextPrune = DateTime.MinValue;

        do
        {
            try
            {
                await RunCycleAsync(client, stoppingToken);

                if (DateTime.UtcNow >= nextPrune)
                {
                    nextPrune = DateTime.UtcNow + PruneInterval;
                    using IServiceScope scope = scopeFactory.CreateScope();
                    using IDisposable unattended = scope.DeclareUnattended("synthetic-check-retention");
                    await scope.ServiceProvider.GetRequiredService<SyntheticCheckService>().PruneAsync(DateTime.UtcNow, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A cycle that throws ends the cycle, not the host.
                logger.LogWarning(ex, "Synthetic check cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(HttpClient client, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("synthetic-check-runner");
        IServiceProvider services = scope.ServiceProvider;

        await using TelemetryDbContext db = await services
            .GetRequiredService<IDbContextFactory<TelemetryDbContext>>().CreateDbContextAsync(ct);

        List<Guid> tenants = await db.Tenants.Select(t => t.Id).ToListAsync(ct);

        SyntheticCheckService checks = services.GetRequiredService<SyntheticCheckService>();

        foreach (Guid tenantId in tenants)
        {
            try
            {
                await RunTenantAsync(client, checks, tenantId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Synthetic checks failed for tenant {Tenant}.", tenantId);
            }
        }
    }

    private static async Task RunTenantAsync(
        HttpClient client, SyntheticCheckService checks, Guid tenantId, CancellationToken ct)
    {
        List<SyntheticCheck> due = await checks.DueAsync(tenantId, DateTime.UtcNow, ct);
        if (due.Count == 0) return;

        // The requests run together and the writes one at a time: a DbContext is opened per write,
        // but nothing here needs them to overlap.
        List<(Guid Id, SyntheticProbeResult Result)> results = [];
        object gate = new();

        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRequests, CancellationToken = ct },
            async (check, token) =>
            {
                SyntheticProbeResult result = await SyntheticProbe.RunAsync(client, check, RequestTimeout, token);
                lock (gate) results.Add((check.Id, result));
            });

        foreach ((Guid id, SyntheticProbeResult result) in results)
        {
            await checks.RecordAsync(tenantId, id, result, DateTime.UtcNow, ct);
        }
    }
}
