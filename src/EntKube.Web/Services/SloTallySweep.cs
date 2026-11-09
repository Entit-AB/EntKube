using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Recomputes each objective's daily tallies, tenant by tenant.
///
/// <para>Recomputed rather than added to, so a pass that runs twice, or on two instances, writes the
/// same numbers twice. A read that fails writes nothing, leaving the last good figure in place.</para>
/// </summary>
public sealed class SloTallySweep(
    IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<SloTallySweep> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        DateTime nextPrune = DateTime.MinValue;

        do
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                using IDisposable unattended = scope.DeclareUnattended("slo-tally");

                await RunCycleAsync(scope.ServiceProvider, DateTime.UtcNow, stoppingToken);

                if (DateTime.UtcNow >= nextPrune)
                {
                    nextPrune = DateTime.UtcNow + PruneInterval;
                    await scope.ServiceProvider.GetRequiredService<SloService>().PruneAsync(DateTime.UtcNow, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SLO tally cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(IServiceProvider services, DateTime now, CancellationToken ct)
    {
        await using TelemetryDbContext db = await services
            .GetRequiredService<IDbContextFactory<TelemetryDbContext>>().CreateDbContextAsync(ct);

        List<Guid> tenants = await db.Tenants.Select(t => t.Id).ToListAsync(ct);

        SloService slos = services.GetRequiredService<SloService>();
        ITelemetryBackends backends = services.GetRequiredService<ITelemetryBackends>();

        int syntheticRetention = Math.Max(1, config.GetValue<int?>("Synthetics:ResultRetentionDays") ?? 14);
        int spanRetention = Math.Max(1, config.GetValue<int?>("Telemetry:RawSpanRetentionDays") ?? 30);

        foreach (Guid tenantId in tenants)
        {
            try
            {
                ITraceQueryService traces = backends.TracesFor(tenantId);

                foreach (ServiceLevelObjective slo in await slos.ForTenantAsync(tenantId, ct))
                {
                    await TallyAsync(slos, traces, slo, now, syntheticRetention, spanRetention, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SLO tallies failed for tenant {Tenant}.", tenantId);
            }
        }
    }

    /// <summary>One objective's due days. Returns how many were written.</summary>
    public static async Task<int> TallyAsync(
        SloService slos, ITraceQueryService traces, ServiceLevelObjective slo, DateTime now,
        int syntheticRetentionDays, int spanRetentionDays, CancellationToken ct)
    {
        DateOnly today = DateOnly.FromDateTime(now);
        int retention = slo.Kind == SloKind.SyntheticUptime ? syntheticRetentionDays : spanRetentionDays;

        List<DateOnly> days = SloService.DaysToCompute(today, slo.WindowDays, retention, await slos.ComputedDaysAsync(slo.Id, ct));
        int written = 0;

        foreach (DateOnly day in days)
        {
            (long Good, long Total)? tally = await DayAsync(slos, traces, slo, day, ct);
            if (tally is not { } t) continue;

            await slos.SetTallyAsync(slo.TenantId, slo.Id, day, t.Good, t.Total, ct);
            written++;
        }

        return written;
    }

    /// <summary>A day's good and total, or null when the source could not be read.</summary>
    private static async Task<(long Good, long Total)?> DayAsync(
        SloService slos, ITraceQueryService traces, ServiceLevelObjective slo, DateOnly day, CancellationToken ct)
    {
        if (slo.Kind == SloKind.SyntheticUptime)
        {
            return slo.CheckId is { } checkId ? await slos.SyntheticDayAsync(slo.TenantId, checkId, day, ct) : null;
        }

        DateTime from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime to = from.AddDays(1);

        int buckets = slo.Kind == SloKind.ServiceLatency ? (int)(TimeSpan.FromDays(1) / SloService.LatencySlice) : 24;

        KubernetesOperationResult<List<RedBucket>> red =
            await traces.GetServiceRedAsync(slo.ClusterId, slo.Service ?? "", from, to, buckets, ct);

        if (!red.IsSuccess) return null;

        List<RedBucket> data = red.Data ?? [];

        return slo.Kind == SloKind.ServiceLatency
            ? SloService.Latency(data, slo.LatencyThresholdMs ?? 0)
            : SloService.Availability(data);
    }
}
