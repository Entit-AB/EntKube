using Cronos;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;

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
        using IDisposable unattended = scope.DeclareUnattended("keycloak-backup-scheduler");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<IdentityDbContext>>();

        // One tenant at a time, outermost (docs/decomposition.md §4.0.1.2). This used to select every
        // scheduled realm in the installation and carry `r.TenantId` out of the row into
        // CreateBackupAsync, which is the call that resolves the realm's cluster credential and its
        // storage link: the tenant a backup runs as was supplied by the row the backup was for.
        // Asking per tenant makes it an input, so a realm can only ever be backed up as its owner.
        List<Guid> tenants;
        using (IdentityDbContext db = dbFactory.CreateDbContext())
            tenants = await db.Tenants.Select(x => x.Id).ToListAsync(ct);

        DateTime now = DateTime.UtcNow;
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakService>();

        foreach (Guid tenantId in tenants)
        {
            List<ScheduledRealm> realms;
            Dictionary<Guid, KeycloakBackup> latestByRealm;
            using (IdentityDbContext db = dbFactory.CreateDbContext())
            {
                realms = await ScheduledFor(db, tenantId)
                    .Select(r => new ScheduledRealm(r.Id, r.RealmName, r.BackupSchedule!, r.StorageLinkId!.Value, r.CreatedAt))
                    .ToListAsync(ct);

                if (realms.Count == 0) continue;

                List<Guid> ids = realms.Select(r => r.Id).ToList();
                latestByRealm = (await db.KeycloakBackups
                        .Where(b => ids.Contains(b.KeycloakRealmId))
                        .ToListAsync(ct))
                    .GroupBy(b => b.KeycloakRealmId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.CreatedAt).First());
            }

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
                    await keycloak.CreateBackupAsync(tenantId, realm.Id, realm.StorageLinkId, ct);
                    logger.LogInformation("Ran scheduled Keycloak backup for realm {Realm} (tenant {Tenant}).",
                        realm.RealmName, tenantId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Scheduled Keycloak backup failed for realm {Realm}", realm.RealmName);
                }
            }
        }
    }

    /// <summary>
    /// One tenant's realms that have a backup schedule and somewhere to put a backup.
    ///
    /// <para>Public and static so the scoping can be asserted without running the scheduler. It is
    /// the predicate that changed: it used to be every scheduled realm in the installation, with the
    /// tenant to run the backup as taken out of each row.</para>
    /// </summary>
    public static IQueryable<KeycloakRealm> ScheduledFor(IdentityDbContext db, Guid tenantId)
        => db.KeycloakRealms
            .Where(r => r.TenantId == tenantId
                        && r.BackupSchedule != null && r.BackupSchedule != "" && r.StorageLinkId != null);

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

    /// <summary>
    /// A realm due for a scheduled backup. No tenant on it: the tenant is the loop's, and a record that
    /// carried its own would be the row's answer travelling on under a new name.
    /// </summary>
    private readonly record struct ScheduledRealm(
        Guid Id, string RealmName, string Schedule, Guid StorageLinkId, DateTime CreatedAt);
}
