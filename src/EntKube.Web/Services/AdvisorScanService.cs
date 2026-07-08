using System.Text;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Periodically computes the Operations Advisor report for every tenant so the feed
/// works even when no one is looking: it reconciles finding first/last-seen state
/// (driving aging and escalation) and, once a day, pushes a digest of what needs
/// doing to each tenant's notification channels. Findings themselves stay
/// compute-on-read; this only persists lifecycle state and sends summaries.
/// </summary>
public class AdvisorScanService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AdvisorScanService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    // Guards against sending more than one digest per UTC day (per process).
    private DateOnly _lastDigestDate = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Advisor scan cycle failed"); }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool digestEnabled = configuration.GetValue("Advisor:Digest:Enabled", true);
        int digestHour = configuration.GetValue("Advisor:Digest:HourUtc", 7);
        DateTime now = DateTime.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now);

        bool sendDigestNow = digestEnabled && now.Hour == digestHour && _lastDigestDate != today;

        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var advisor = scope.ServiceProvider.GetRequiredService<OperationsAdvisorService>();
        var state = scope.ServiceProvider.GetRequiredService<AdvisorStateService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        List<Tenant> tenants;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
            tenants = await db.Tenants.ToListAsync(ct);

        foreach (Tenant tenant in tenants)
        {
            try
            {
                AdvisorReport report = await advisor.GetReportAsync(tenant.Id, ct);
                await state.ReconcileAsync(tenant.Id, report.Findings.Select(f => f.Id).ToHashSet(), ct);

                if (sendDigestNow)
                    await MaybeSendDigestAsync(notifications, tenant, report, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Advisor scan failed for tenant {Tenant}", tenant.Id);
            }
        }

        if (sendDigestNow)
            _lastDigestDate = today;
    }

    private async Task MaybeSendDigestAsync(
        NotificationService notifications, Tenant tenant, AdvisorReport report, CancellationToken ct)
    {
        // Only actionable, not-dismissed/snoozed items count toward a digest.
        List<OperationsFinding> actionable = report.Findings
            .Where(f => f.State is not AdvisorFindingStatus.Dismissed and not AdvisorFindingStatus.Snoozed)
            .Where(f => f.Horizon is AdvisorHorizon.Overdue or AdvisorHorizon.Today)
            .ToList();
        if (actionable.Count == 0) return;   // nothing urgent — stay quiet

        int overdue = actionable.Count(f => f.Horizon == AdvisorHorizon.Overdue);
        int todayCount = actionable.Count(f => f.Horizon == AdvisorHorizon.Today);
        string severity = actionable.Any(f => f.Severity == AdvisorSeverity.Critical) ? "critical"
                        : actionable.Any(f => f.Severity == AdvisorSeverity.Warning) ? "warning" : "info";

        var body = new StringBuilder();
        body.Append($"{overdue} overdue, {todayCount} due today across security, reliability and data protection.\n\n");
        foreach (OperationsFinding f in actionable.Take(10))
        {
            string bucket = f.Horizon == AdvisorHorizon.Overdue ? "OVERDUE" : "TODAY";
            body.Append($"• [{bucket}] {f.Title} — {f.ScopeLabel}\n");
        }
        if (actionable.Count > 10)
            body.Append($"\n…and {actionable.Count - 10} more. Open the Operations Advisor for the full list.");

        (int notified, bool ok, string? err) = await notifications.DispatchDigestAsync(
            tenant.Id, $"Operations Advisor — {tenant.Name}: {overdue} overdue, {todayCount} due today",
            body.ToString(), severity, ct);

        if (ok)
            logger.LogInformation("Advisor digest sent for tenant {Tenant} to {N} channel(s).", tenant.Id, notified);
        else if (err is not null && !err.StartsWith("No enabled"))
            logger.LogWarning("Advisor digest for tenant {Tenant} had issues: {Error}", tenant.Id, err);
    }
}
