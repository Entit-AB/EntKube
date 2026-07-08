using Cronos;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Runs scheduled Keycloak realm backups in-app. CNPG/Mongo/RabbitMQ delegate their
/// cron to a Kubernetes operator or CronJob, but a Keycloak realm export is a pure
/// application operation (Admin REST API → S3), so there is nothing to delegate to —
/// this hosted service is the executor. Each cycle it finds realms whose cron boundary
/// has passed since their last backup and triggers a fresh export. Staleness of these
/// backups is then surfaced by the Operations Advisor, exactly like the other datastores.
/// </summary>
public class KeycloakBackupSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<KeycloakBackupSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueBackupsAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Keycloak backup scheduler cycle failed"); }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunDueBackupsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        List<ScheduledRealm> realms;
        Dictionary<Guid, KeycloakBackup> latestByRealm;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            realms = await db.KeycloakRealms
                .Where(r => r.BackupSchedule != null && r.BackupSchedule != "" && r.StorageLinkId != null)
                .Select(r => new ScheduledRealm(r.Id, r.TenantId, r.RealmName, r.BackupSchedule!, r.StorageLinkId!.Value, r.CreatedAt))
                .ToListAsync(ct);

            if (realms.Count == 0) return;

            List<Guid> ids = realms.Select(r => r.Id).ToList();
            latestByRealm = (await db.KeycloakBackups
                    .Where(b => ids.Contains(b.KeycloakRealmId))
                    .ToListAsync(ct))
                .GroupBy(b => b.KeycloakRealmId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.CreatedAt).First());
        }

        DateTime now = DateTime.UtcNow;
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakService>();

        foreach (ScheduledRealm realm in realms)
        {
            CronExpression? cron = TryParseCron(realm.Schedule);
            if (cron is null)
            {
                logger.LogWarning("Realm {Realm} has an invalid backup schedule '{Schedule}' — skipping.",
                    realm.RealmName, realm.Schedule);
                continue;
            }

            latestByRealm.TryGetValue(realm.Id, out KeycloakBackup? latest);

            // Don't stack backups while one is still running.
            if (latest?.Status == KeycloakBackupStatus.Creating) continue;

            DateTime anchor = DateTime.SpecifyKind(latest?.CreatedAt ?? realm.CreatedAt, DateTimeKind.Utc);
            DateTime? nextDue = cron.GetNextOccurrence(anchor, TimeZoneInfo.Utc, inclusive: false);
            if (nextDue is null || nextDue.Value > now) continue;   // not due yet

            try
            {
                await keycloak.CreateBackupAsync(realm.TenantId, realm.Id, realm.StorageLinkId, ct);
                logger.LogInformation("Ran scheduled Keycloak backup for realm {Realm} (tenant {Tenant}).",
                    realm.RealmName, realm.TenantId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled Keycloak backup failed for realm {Realm}", realm.RealmName);
            }
        }
    }

    /// <summary>Accepts standard 5-field cron, or 6-field with a leading seconds column.</summary>
    private static CronExpression? TryParseCron(string schedule)
    {
        string[] parts = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return CronExpression.Parse(schedule, parts.Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    private readonly record struct ScheduledRealm(
        Guid Id, Guid TenantId, string RealmName, string Schedule, Guid StorageLinkId, DateTime CreatedAt);
}
