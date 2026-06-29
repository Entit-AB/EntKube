using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages expiring-secret notifications for a notification scope: either a whole
/// tenant (ops, <c>customerId == null</c>) or a single customer (portal). Covers the
/// per-scope configuration (enabled + lead-time thresholds), listing the secrets that
/// carry an expiry, the history of notices sent, manual "send now" dispatch, and the
/// scan logic the background scanner runs. Notices are delivered through the scope's
/// notification channels via <see cref="NotificationService.DispatchSecretExpiryAsync"/>.
/// </summary>
public class SecretExpiryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    NotificationService notifications,
    ILogger<SecretExpiryService> logger)
{
    /// <summary>
    /// Returns the scope's config, or an unsaved default (disabled, "30,14,7,1") when
    /// none has been saved yet.
    /// </summary>
    public async Task<SecretExpiryNotificationConfig> GetConfigAsync(
        Guid tenantId, Guid? customerId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        SecretExpiryNotificationConfig? config = await db.SecretExpiryNotificationConfigs
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CustomerId == customerId, ct);
        return config ?? new SecretExpiryNotificationConfig { TenantId = tenantId, CustomerId = customerId };
    }

    /// <summary>Creates or updates the scope's expiry-notification config.</summary>
    public async Task SaveConfigAsync(
        Guid tenantId, Guid? customerId, bool enabled, string thresholdDaysCsv, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        SecretExpiryNotificationConfig? config = await db.SecretExpiryNotificationConfigs
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CustomerId == customerId, ct);

        if (config is null)
        {
            config = new SecretExpiryNotificationConfig { Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId };
            db.SecretExpiryNotificationConfigs.Add(config);
        }

        config.IsEnabled = enabled;
        config.ThresholdDaysCsv = string.IsNullOrWhiteSpace(thresholdDaysCsv) ? "30,14,7,1" : thresholdDaysCsv.Trim();
        config.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Lists every certificate and OAuth client secret in scope, soonest-expiring first
    /// (secrets with no expiry set sort last).
    /// </summary>
    public async Task<List<ExpiringSecretInfo>> GetExpiringSecretsAsync(
        Guid tenantId, Guid? customerId = null, CancellationToken ct = default)
    {
        List<ExpiringSecretInfo> candidates = await vault.GetExpiringSecretCandidatesAsync(tenantId, customerId, ct);
        return candidates
            .OrderBy(s => s.ExpiresAt ?? DateTime.MaxValue)
            .ThenBy(s => s.Name)
            .ToList();
    }

    /// <summary>Recent expiry notifications sent for the scope, newest first.</summary>
    public async Task<List<SecretExpiryNotification>> GetHistoryAsync(
        Guid tenantId, Guid? customerId = null, int take = 50, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.SecretExpiryNotifications
            .Where(n => n.TenantId == tenantId && n.CustomerId == customerId)
            .OrderByDescending(n => n.SentAt)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Manually sends an expiry notice for one secret right now, bypassing dedupe. The
    /// secret must be in scope (a customer can only send for their own secrets).
    /// </summary>
    public async Task<(int Notified, bool Success, string? Error)> SendNowAsync(
        Guid tenantId, Guid secretId, Guid? customerId = null, CancellationToken ct = default)
    {
        List<ExpiringSecretInfo> candidates = await vault.GetExpiringSecretCandidatesAsync(tenantId, customerId, ct);
        ExpiringSecretInfo? secret = candidates.FirstOrDefault(s => s.SecretId == secretId);
        if (secret is null)
            return (0, false, "Secret not found");

        int threshold = Math.Max(0, secret.DaysUntilExpiry ?? 0);
        return await NotifyAndRecordAsync(tenantId, customerId, secret, threshold, manual: true, ct);
    }

    /// <summary>
    /// Runs one scan pass for a scope: for each secret whose days-to-expiry has crossed a
    /// configured threshold, sends the notice for the tightest newly-crossed band and
    /// records it. De-dupes on (secret, threshold, expiry) so each band fires once per
    /// expiry cycle; a renewed secret (new expiry) starts a fresh cycle. Failed sends are
    /// retried on the next pass. Does nothing when the scope's config is disabled.
    /// </summary>
    public async Task<int> RunScanAsync(Guid tenantId, Guid? customerId = null, CancellationToken ct = default)
    {
        SecretExpiryNotificationConfig config = await GetConfigAsync(tenantId, customerId, ct);
        if (!config.IsEnabled)
            return 0;

        IReadOnlyList<int> thresholds = config.Thresholds(); // descending, includes 0
        List<ExpiringSecretInfo> candidates = await vault.GetExpiringSecretCandidatesAsync(tenantId, customerId, ct);

        // Successful notices already sent for this scope, for dedupe.
        HashSet<(Guid, int, DateTime)> alreadySent;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            alreadySent = (await db.SecretExpiryNotifications
                    .Where(n => n.TenantId == tenantId && n.CustomerId == customerId && n.Success)
                    .Select(n => new { n.SecretId, n.ThresholdDays, n.ExpiresAt })
                    .ToListAsync(ct))
                .Select(n => (n.SecretId, n.ThresholdDays, n.ExpiresAt))
                .ToHashSet();
        }

        int sent = 0;
        foreach (ExpiringSecretInfo secret in candidates)
        {
            if (!secret.ExpiresAt.HasValue || secret.DaysUntilExpiry is not int days)
                continue; // no tracked expiry (e.g. OAuth client with no date entered)

            // Tightest threshold the secret has crossed (smallest t with days <= t).
            int? band = thresholds.Where(t => days <= t).Cast<int?>().Min();
            if (band is not int threshold)
                continue;

            if (alreadySent.Contains((secret.SecretId, threshold, secret.ExpiresAt.Value)))
                continue;

            (int notified, bool success, string? error) = await NotifyAndRecordAsync(tenantId, customerId, secret, threshold, manual: false, ct);
            if (success) sent++;
            else logger.LogWarning("Secret-expiry notice for {Secret} (tenant {Tenant}, customer {Customer}) failed: {Error}",
                secret.Name, tenantId, customerId, error);
        }

        return sent;
    }

    private async Task<(int Notified, bool Success, string? Error)> NotifyAndRecordAsync(
        Guid tenantId, Guid? customerId, ExpiringSecretInfo secret, int thresholdDays, bool manual, CancellationToken ct)
    {
        (int notified, bool success, string? error) = await notifications.DispatchSecretExpiryAsync(tenantId, secret, thresholdDays, customerId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.SecretExpiryNotifications.Add(new SecretExpiryNotification
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            SecretId = secret.SecretId,
            SecretName = secret.Name,
            ThresholdDays = thresholdDays,
            ExpiresAt = secret.ExpiresAt ?? DateTime.UtcNow,
            DaysUntilExpiry = secret.DaysUntilExpiry ?? 0,
            Manual = manual,
            SentAt = DateTime.UtcNow,
            ChannelsNotified = notified,
            Success = success,
            Error = error
        });
        await db.SaveChangesAsync(ct);

        return (notified, success, error);
    }
}
