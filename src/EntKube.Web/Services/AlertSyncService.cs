using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

public class AlertSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<AlertSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncAllClustersAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private static async Task DispatchAsync(
        AlertIncident incident,
        bool isFiring,
        List<NotificationChannel> allChannels,
        Dictionary<Guid, NotificationChannel> channelsById,
        List<AlertRoutingRule> routingRules,
        NotificationService notificationService,
        CancellationToken ct)
    {
        // Extract namespace from LabelsJson for routing rule matching
        string? ns = null;
        Dictionary<string, string>? labels = null;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(incident.LabelsJson);
            labels = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            labels.TryGetValue("namespace", out ns);
        }
        catch { /* labels unavailable — routing falls through to default */ }

        // Try routing rules first (first match wins); skip suppression-only and cross-cluster rules
        foreach (AlertRoutingRule rule in routingRules)
        {
            if (rule.SuppressIncident) continue;
            if (rule.MatchClusterId.HasValue && rule.MatchClusterId != incident.ClusterId) continue;

            if (!string.IsNullOrEmpty(rule.MatchAlertName)
                && !incident.AlertName.Contains(rule.MatchAlertName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchSeverity)
                && !string.Equals(incident.Severity, rule.MatchSeverity, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchNamespace)
                && !string.Equals(ns, rule.MatchNamespace, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchLabelKey) && labels is not null)
            {
                if (!labels.TryGetValue(rule.MatchLabelKey, out string? labelVal)) continue;
                if (!string.IsNullOrEmpty(rule.MatchLabelValue)
                    && !string.Equals(labelVal, rule.MatchLabelValue, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // Rule matched — send only to the rule's channel
            if (rule.ChannelId.HasValue)
            {
                NotificationChannel? ruleChannel = channelsById.GetValueOrDefault(rule.ChannelId.Value) ?? rule.Channel;
                await notificationService.SendAsync(incident, ruleChannel, isFiring, ct);
            }
            return;
        }

        // No routing rule matched — fall back to all channels (each filtered by SeverityFilter)
        foreach (NotificationChannel channel in allChannels)
            await notificationService.SendAsync(incident, channel, isFiring, ct);
    }

    private static bool IsAlertSuppressed(
        string alertName, string severity, Dictionary<string, string>? labels,
        Guid clusterId, List<AlertRoutingRule> rules)
    {
        string? ns = labels?.GetValueOrDefault("namespace");

        foreach (AlertRoutingRule rule in rules.Where(r => r.IsEnabled && r.SuppressIncident))
        {
            if (rule.MatchClusterId.HasValue && rule.MatchClusterId != clusterId) continue;

            if (!string.IsNullOrEmpty(rule.MatchAlertName)
                && !alertName.Contains(rule.MatchAlertName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchSeverity)
                && !string.Equals(severity, rule.MatchSeverity, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchNamespace)
                && !string.Equals(ns, rule.MatchNamespace, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchLabelKey) && labels is not null)
            {
                if (!labels.TryGetValue(rule.MatchLabelKey, out string? labelVal)) continue;
                if (!string.IsNullOrEmpty(rule.MatchLabelValue)
                    && !string.Equals(labelVal, rule.MatchLabelValue, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return true;
        }
        return false;
    }

    private async Task SyncAllClustersAsync(CancellationToken ct)
    {
        // One tenant at a time (docs/decomposition.md §4.0.1.2). This used to select every
        // cluster in the installation running kube-prometheus-stack and then derive each one's
        // tenant from the cluster row — so the rule and maintenance-window lookups downstream
        // were filtered by a tenant the cluster itself had supplied.
        Dictionary<Guid, List<Guid>> clustersByTenant = [];
        using (IServiceScope scope = scopeFactory.CreateScope())
        {
            using IDisposable unattended = scope.DeclareUnattended("alert-sync");
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var catalog = scope.ServiceProvider.GetRequiredService<EntKube.Contracts.Catalog.ICatalogApi>();
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            foreach (Guid tenantId in await db.Tenants.Select(t => t.Id).ToListAsync(ct))
            {
                // Only the tenant's own clusters running kube-prometheus-stack.
                List<Guid> ids =
                [
                    .. (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                        .Where(c => c.HelmChartName == "kube-prometheus-stack"
                                 && c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed)
                        .Select(c => c.ClusterId)
                        .Distinct(),
                ];

                if (ids.Count > 0) clustersByTenant[tenantId] = ids;
            }
        }

        foreach ((Guid tenantId, List<Guid> clusterIds) in clustersByTenant)
        {
            foreach (Guid clusterId in clusterIds)
            {
                try
                {
                    await SyncClusterAsync(tenantId, clusterId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Alert sync failed for cluster {ClusterId} of tenant {TenantId}",
                        clusterId, tenantId);
                }
            }
        }
    }

    private async Task SyncClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("alert-sync");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var prometheusService = scope.ServiceProvider.GetRequiredService<PrometheusService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var stormSuppression = scope.ServiceProvider.GetRequiredService<StormSuppressionService>();
        var alertTickets = scope.ServiceProvider.GetRequiredService<Tickets.AlertTicketBridge>();

        // Fetch current alerts from Alertmanager
        KubernetesOperationResult<List<AlertInfo>> alertsResult =
            await prometheusService.GetAlertsAsync(tenantId, clusterId, ct);

        if (!alertsResult.IsSuccess)
        {
            logger.LogDebug("Could not fetch alerts for cluster {ClusterId}: {Error}",
                clusterId, alertsResult.Error);
            return;
        }

        // Deduplicate by fingerprint in case Alertmanager returns the same alert twice
        Dictionary<string, AlertInfo> liveAlertsByFingerprint = alertsResult.Data!
            .Where(a => !string.IsNullOrEmpty(a.Fingerprint))
            .GroupBy(a => a.Fingerprint)
            .ToDictionary(g => g.Key, g => g.First());

        List<AlertInfo> liveAlerts = [.. liveAlertsByFingerprint.Values];
        HashSet<string> liveFingerprints = liveAlertsByFingerprint.Keys.ToHashSet();

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Scoped by tenant as well as id. The row is still needed — an incident carries the
        // cluster as a navigation — but it no longer *supplies* the tenant, so a cluster that is
        // not this tenant's simply does not resolve.
        KubernetesCluster? clusterEarly = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
        if (clusterEarly is null) return;

        // Load routing rules up front so suppression can be checked before incident creation
        List<AlertRoutingRule> allRoutingRules = await db.AlertRoutingRules
            .Include(r => r.Channel)
            .Where(r => r.TenantId == tenantId && r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        // Load ALL incidents for this cluster — including Resolved — so that re-fires
        // can reactivate an existing row rather than insert a duplicate and violate
        // the unique (ClusterId, Fingerprint) constraint.
        List<AlertIncident> allIncidents = await db.AlertIncidents
            .Where(i => i.ClusterId == clusterId)
            .ToListAsync(ct);

        Dictionary<string, AlertIncident> byFingerprint = allIncidents
            .ToDictionary(i => i.Fingerprint);

        List<AlertIncident> openIncidents = allIncidents
            .Where(i => i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Acknowledged)
            .ToList();

        HashSet<string> openFingerprints = openIncidents
            .Select(i => i.Fingerprint)
            .ToHashSet();

        DateTime now = DateTime.UtcNow;
        List<AlertIncident> newIncidents = [];

        // Create or reactivate incidents for alerts not already tracked as open
        foreach (AlertInfo alert in liveAlerts)
        {
            if (string.IsNullOrEmpty(alert.Fingerprint)) continue;
            if (openFingerprints.Contains(alert.Fingerprint)) continue;

            // Drop alerts that match a suppression rule for this cluster
            if (IsAlertSuppressed(alert.Name, alert.Severity, alert.Labels, clusterId, allRoutingRules))
            {
                logger.LogDebug("Alert {AlertName} suppressed for cluster {ClusterId}", alert.Name, clusterId);
                continue;
            }

            if (byFingerprint.TryGetValue(alert.Fingerprint, out AlertIncident? existing))
            {
                // Re-fire of a previously resolved alert — reactivate in place
                existing.Status = IncidentStatus.Active;
                existing.StartsAt = alert.StartsAt == default ? now : alert.StartsAt;
                existing.EndsAt = null;
                existing.ResolvedAt = null;
                existing.AcknowledgedBy = null;
                existing.AcknowledgedAt = null;
                existing.AlertName = alert.Name;
                existing.Severity = alert.Severity;
                existing.Summary = alert.Summary;
                existing.Description = alert.Description;
                existing.RunbookUrl = alert.RunbookUrl;
                existing.LabelsJson = System.Text.Json.JsonSerializer.Serialize(alert.Labels);
                existing.UpdatedAt = now;
                newIncidents.Add(existing);
            }
            else
            {
                AlertIncident incident = new()
                {
                    ClusterId = clusterId,
                    Fingerprint = alert.Fingerprint,
                    AlertName = alert.Name,
                    Severity = alert.Severity,
                    Summary = alert.Summary,
                    Description = alert.Description,
                    RunbookUrl = alert.RunbookUrl,
                    LabelsJson = System.Text.Json.JsonSerializer.Serialize(alert.Labels),
                    StartsAt = alert.StartsAt == default ? now : alert.StartsAt,
                    Status = IncidentStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.AlertIncidents.Add(incident);
                newIncidents.Add(incident);
            }
        }

        // Resolve incidents no longer in Alertmanager
        List<AlertIncident> resolvedIncidents = [];
        foreach (AlertIncident incident in openIncidents)
        {
            if (liveFingerprints.Contains(incident.Fingerprint))
            {
                // Still firing — update timestamp
                incident.UpdatedAt = now;
            }
            else
            {
                // No longer in Alertmanager — resolve
                incident.Status = IncidentStatus.Resolved;
                incident.EndsAt ??= now;
                incident.ResolvedAt = now;
                incident.UpdatedAt = now;
                resolvedIncidents.Add(incident);
            }
        }

        await db.SaveChangesAsync(ct);

        KubernetesCluster cluster = clusterEarly;

        // §14.3: an issue our monitoring finds is registered by us, timed at the alarm.
        // Done before the maintenance check below, which suppresses notifications rather
        // than the record — but only for alerts that map to an application under Annex A.
        await alertTickets.OpenForIncidentsAsync(tenantId, newIncidents, ct);
        await alertTickets.NoteIncidentResolvedAsync(resolvedIncidents, now, ct);

        // Dispatch notifications (need the saved incidents to have IDs)
        List<NotificationChannel> channels = await db.NotificationChannels
            .Where(c => c.TenantId == tenantId && c.CustomerId == null && c.IsEnabled)
            .ToListAsync(ct);

        if (channels.Count == 0) return;

        // Skip notifications while a maintenance window is active for this cluster/tenant
        bool inMaintenance = await db.MaintenanceWindows.AnyAsync(w =>
            w.TenantId == tenantId
            && w.StartsAt <= now
            && w.EndsAt >= now
            && (w.ClusterId == null || w.ClusterId == clusterId), ct);

        if (inMaintenance)
        {
            logger.LogInformation(
                "Cluster {ClusterId} is in a maintenance window — skipping notifications for {New} new / {Resolved} resolved alerts",
                clusterId, newIncidents.Count, resolvedIncidents.Count);
            return;
        }

        // Use the routing rules already loaded above; filter to those with an enabled channel
        List<AlertRoutingRule> routingRules = allRoutingRules
            .Where(r => r.SuppressIncident || (r.Channel?.IsEnabled == true))
            .ToList();

        Dictionary<Guid, NotificationChannel> channelsById =
            channels.ToDictionary(c => c.Id);

        // Collapse a storm to one notification per root cause (throttled across cycles).
        foreach (AlertIncident incident in newIncidents)
            incident.Cluster = cluster;
        HashSet<Guid> notifiable = await stormSuppression.SelectNotifiableAsync(newIncidents, ct);
        int suppressed = newIncidents.Count - newIncidents.Count(i => notifiable.Contains(i.Id));
        if (suppressed > 0)
            logger.LogInformation(
                "Storm suppression: notifying {Notify} of {Total} new alerts on cluster {Cluster} ({Suppressed} correlated/throttled).",
                newIncidents.Count - suppressed, newIncidents.Count, clusterId, suppressed);

        foreach (AlertIncident incident in newIncidents)
        {
            if (!notifiable.Contains(incident.Id)) continue;
            await DispatchAsync(incident, isFiring: true, channels, channelsById, routingRules, notificationService, ct);
        }

        foreach (AlertIncident incident in resolvedIncidents)
        {
            incident.Cluster = cluster;
            await DispatchAsync(incident, isFiring: false, channels, channelsById, routingRules, notificationService, ct);
        }
    }
}
