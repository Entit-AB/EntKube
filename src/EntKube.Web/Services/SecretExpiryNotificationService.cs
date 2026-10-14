using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

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

    /// <summary>
    /// One tenant's enabled notification scopes. Public and static so the scoping is assertable
    /// without a notification channel.
    /// </summary>
    public static IQueryable<SecretExpiryNotificationConfig> EnabledScopesFor(
        SecretsDbContext db, Guid tenantId)
        => db.SecretExpiryNotificationConfigs.Where(c => c.TenantId == tenantId && c.IsEnabled);

    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            using IDisposable unattended = scope.DeclareUnattended("secret-expiry-notification");
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SecretsDbContext>>();
            var expiry = scope.ServiceProvider.GetRequiredService<SecretExpiryService>();

            // One tenant at a time (docs/decomposition.md §4.0.1.2). The scopes used to be every
            // enabled config row in the installation with c.TenantId taken off each one — and that
            // tenant is who the scan then NOTIFIES as, so a config row carrying the wrong tenant
            // would have sent one tenant's expiring-secret list to another's channels. The retention
            // prune stays installation-wide, because old history is old history.
            List<(Guid TenantId, Guid? CustomerId)> scopes = [];
            using (SecretsDbContext db = dbFactory.CreateDbContext())
            {
                DateTime cutoff = DateTime.UtcNow - HistoryRetention;
                await db.SecretExpiryNotifications.Where(n => n.SentAt < cutoff).ExecuteDeleteAsync(ct);

                foreach (Guid tenantId in await db.Tenants.Select(x => x.Id).ToListAsync(ct))
                {
                    scopes.AddRange(
                        (await EnabledScopesFor(db, tenantId).Select(c => c.CustomerId).ToListAsync(ct))
                            .Select(customerId => (tenantId, customerId)));
                }
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
