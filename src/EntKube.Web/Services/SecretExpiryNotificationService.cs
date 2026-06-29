using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Background scanner that periodically checks every tenant's vault for certificates
/// and OAuth client secrets approaching expiry and notifies operations through the
/// tenant's configured channels. Only tenants with an enabled
/// <see cref="SecretExpiryNotificationConfig"/> are scanned. The per-tenant work
/// (threshold evaluation, dedupe, dispatch) lives in <see cref="SecretExpiryService"/>.
/// </summary>
public class SecretExpiryNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<SecretExpiryNotificationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(365);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ScanAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var expiry = scope.ServiceProvider.GetRequiredService<SecretExpiryService>();

            List<(Guid TenantId, Guid? CustomerId)> scopes;
            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                // Prune very old history to bound table growth.
                DateTime cutoff = DateTime.UtcNow - HistoryRetention;
                await db.SecretExpiryNotifications.Where(n => n.SentAt < cutoff).ExecuteDeleteAsync(ct);

                scopes = (await db.SecretExpiryNotificationConfigs
                        .Where(c => c.IsEnabled)
                        .Select(c => new { c.TenantId, c.CustomerId })
                        .ToListAsync(ct))
                    .Select(c => (c.TenantId, c.CustomerId))
                    .ToList();
            }

            int total = 0;
            foreach ((Guid tenantId, Guid? customerId) in scopes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    total += await expiry.RunScanAsync(tenantId, customerId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Secret-expiry scan failed for tenant {Tenant} customer {Customer}", tenantId, customerId);
                }
            }

            if (total > 0)
                logger.LogInformation("Secret-expiry scan sent {Count} notification(s) across {Scopes} scope(s)", total, scopes.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Secret-expiry scan pass failed");
        }
    }
}
