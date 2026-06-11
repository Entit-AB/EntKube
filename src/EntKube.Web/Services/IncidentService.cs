using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

public class IncidentService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// Returns incidents that are relevant to a specific set of deployments,
    /// matched by cluster ID and namespace label in the alert's LabelsJson.
    /// Used by the customer portal to show incidents scoped to the customer's apps.
    /// </summary>
    public async Task<List<AlertIncident>> GetIncidentsForDeploymentsAsync(
        List<AppDeployment> deployments,
        int windowDays = 30,
        IncidentStatus? status = null,
        CancellationToken ct = default)
    {
        if (deployments.Count == 0) return [];

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        HashSet<Guid> clusterIds = deployments.Select(d => d.ClusterId).ToHashSet();
        HashSet<string> namespaces = deployments.Select(d => d.Namespace).ToHashSet();

        IQueryable<AlertIncident> query = db.AlertIncidents
            .Include(i => i.Cluster)
            .Include(i => i.Notes.OrderBy(n => n.CreatedAt))
            .Where(i => clusterIds.Contains(i.ClusterId) && i.StartsAt >= from)
            .OrderByDescending(i => i.StartsAt);

        if (status.HasValue)
            query = query.Where(i => i.Status == status.Value);

        List<AlertIncident> all = await query.ToListAsync(ct);

        // Filter client-side by namespace label (JSON parsing can't run in EF)
        return all.Where(i =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(i.LabelsJson);
                return !doc.RootElement.TryGetProperty("namespace", out JsonElement ns)
                       || namespaces.Contains(ns.GetString() ?? "");
            }
            catch { return true; }
        }).ToList();
    }

    public async Task<List<MaintenanceWindow>> GetUpcomingMaintenanceAsync(
        Guid tenantId, HashSet<Guid>? clusterIds = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;
        DateTime lookahead = now.AddDays(7);

        List<MaintenanceWindow> windows = await db.MaintenanceWindows
            .Include(w => w.Cluster)
            .Where(w => w.TenantId == tenantId
                     && w.EndsAt >= now
                     && w.StartsAt <= lookahead)
            .OrderBy(w => w.StartsAt)
            .ToListAsync(ct);

        if (clusterIds is not null && clusterIds.Count > 0)
            windows = windows.Where(w => w.ClusterId == null || clusterIds.Contains(w.ClusterId.Value)).ToList();

        return windows;
    }


    public async Task<List<AlertIncident>> GetIncidentsForTenantAsync(
        Guid tenantId,
        IncidentStatus? status = null,
        string? severity = null,
        Guid? clusterId = null,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        IQueryable<AlertIncident> query = db.AlertIncidents
            .Include(i => i.Cluster)
            .Include(i => i.Notes.OrderBy(n => n.CreatedAt))
            .Where(i => i.Cluster.TenantId == tenantId)
            .OrderByDescending(i => i.StartsAt);

        if (status.HasValue)
            query = query.Where(i => i.Status == status.Value);
        if (!string.IsNullOrEmpty(severity))
            query = query.Where(i => i.Severity == severity);
        if (clusterId.HasValue)
            query = query.Where(i => i.ClusterId == clusterId.Value);
        if (from.HasValue)
            query = query.Where(i => i.StartsAt >= from.Value);
        if (to.HasValue)
            query = query.Where(i => i.StartsAt <= to.Value);

        return await query.ToListAsync(ct);
    }

    public async Task<AlertIncident?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AlertIncidents
            .Include(i => i.Cluster)
            .Include(i => i.Notes.OrderBy(n => n.CreatedAt))
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task AcknowledgeAsync(Guid id, string acknowledgedBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AlertIncident? incident = await db.AlertIncidents.FindAsync([id], ct);
        if (incident is null || incident.Status != IncidentStatus.Active) return;

        incident.Status = IncidentStatus.Acknowledged;
        incident.AcknowledgedBy = acknowledgedBy;
        incident.AcknowledgedAt = DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveAsync(Guid id, string resolvedBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AlertIncident? incident = await db.AlertIncidents.FindAsync([id], ct);
        if (incident is null || incident.Status == IncidentStatus.Resolved) return;

        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = DateTime.UtcNow;
        incident.EndsAt ??= DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task AssignAsync(Guid id, string assignee, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AlertIncident? incident = await db.AlertIncidents.FindAsync([id], ct);
        if (incident is null) return;

        incident.AssignedTo = assignee;
        incident.AssignedAt = DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task BulkAcknowledgeAsync(IEnumerable<Guid> ids, string acknowledgedBy, CancellationToken ct = default)
    {
        List<Guid> idList = ids.ToList();
        if (idList.Count == 0) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<AlertIncident> incidents = await db.AlertIncidents
            .Where(i => idList.Contains(i.Id) && i.Status == IncidentStatus.Active)
            .ToListAsync(ct);

        DateTime now = DateTime.UtcNow;
        foreach (AlertIncident incident in incidents)
        {
            incident.Status = IncidentStatus.Acknowledged;
            incident.AcknowledgedBy = acknowledgedBy;
            incident.AcknowledgedAt = now;
            incident.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<NotificationDelivery>> GetDeliveriesAsync(Guid incidentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.NotificationDeliveries
            .Include(d => d.Channel)
            .Where(d => d.IncidentId == incidentId)
            .OrderByDescending(d => d.SentAt)
            .Take(30)
            .ToListAsync(ct);
    }

    public async Task BulkResolveAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        List<Guid> idList = ids.ToList();
        if (idList.Count == 0) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<AlertIncident> incidents = await db.AlertIncidents
            .Where(i => idList.Contains(i.Id) && i.Status != IncidentStatus.Resolved)
            .ToListAsync(ct);

        DateTime now = DateTime.UtcNow;
        foreach (AlertIncident incident in incidents)
        {
            incident.Status = IncidentStatus.Resolved;
            incident.ResolvedAt = now;
            incident.EndsAt ??= now;
            incident.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task AddNoteAsync(Guid incidentId, string author, string content, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.IncidentNotes.Add(new IncidentNote
        {
            IncidentId = incidentId,
            Author = author,
            Content = content
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<UptimeResult> GetUptimeAsync(
        Guid deploymentId,
        int windowDays,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        List<DeploymentHealthSnapshot> snapshots = await db.DeploymentHealthSnapshots
            .Where(s => s.DeploymentId == deploymentId && s.SnapshotAt >= from)
            .OrderBy(s => s.SnapshotAt)
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return new UptimeResult(windowDays, null);

        int healthy = snapshots.Count(s => s.HealthStatus == HealthStatus.Healthy);
        double percent = (double)healthy / snapshots.Count * 100.0;
        return new UptimeResult(windowDays, Math.Round(percent, 2));
    }

    public async Task<int> GetActiveAlertCountForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AlertIncidents
            .CountAsync(i => i.Cluster.TenantId == tenantId
                && (i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Acknowledged), ct);
    }

    public async Task<Dictionary<Guid, int>> GetActiveCountsPerClusterAsync(
        IEnumerable<Guid> clusterIds, CancellationToken ct = default)
    {
        List<Guid> ids = clusterIds.ToList();
        if (ids.Count == 0) return new();

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        var rows = await db.AlertIncidents
            .Where(i => ids.Contains(i.ClusterId)
                     && (i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Acknowledged))
            .GroupBy(i => i.ClusterId)
            .Select(g => new { ClusterId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ClusterId, r => r.Count);
    }

    public async Task<List<AlertIncident>> GetActiveIncidentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AlertIncidents
            .Include(i => i.Cluster)
            .Where(i => i.Cluster.TenantId == tenantId
                     && (i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Acknowledged))
            .OrderByDescending(i => i.StartsAt)
            .ToListAsync(ct);
    }

    public async Task<AlertStats> GetStatsForTenantAsync(
        Guid tenantId, int windowDays = 30, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        // Historical window — for totals, charts, MTTR/MTTA
        List<AlertIncident> incidents = await db.AlertIncidents
            .Where(i => i.Cluster.TenantId == tenantId && i.StartsAt >= from)
            .ToListAsync(ct);

        // Open incidents regardless of start date — ensures long-running alerts are counted
        List<AlertIncident> openIncidents = await db.AlertIncidents
            .Where(i => i.Cluster.TenantId == tenantId
                && (i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Acknowledged))
            .ToListAsync(ct);

        int total = incidents.Count;
        int active = openIncidents.Count(i => i.Status == IncidentStatus.Active);
        int acked = openIncidents.Count(i => i.Status == IncidentStatus.Acknowledged);
        int resolved = incidents.Count(i => i.Status == IncidentStatus.Resolved);
        // Severity badges show open incidents so they match Active+severity filter results
        int critical = openIncidents.Count(i => i.Severity == "critical");
        int warning = openIncidents.Count(i => i.Severity == "warning");

        List<AlertIncident> resolvedWithTime = incidents
            .Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt.HasValue)
            .ToList();

        double? mttrMinutes = resolvedWithTime.Count > 0
            ? resolvedWithTime.Average(i => (i.ResolvedAt!.Value - i.StartsAt).TotalMinutes)
            : null;

        List<AlertIncident> ackedWithTime = incidents
            .Where(i => i.AcknowledgedAt.HasValue)
            .ToList();

        double? mttaMinutes = ackedWithTime.Count > 0
            ? ackedWithTime.Average(i => (i.AcknowledgedAt!.Value - i.StartsAt).TotalMinutes)
            : null;

        // Daily buckets for sparkline
        List<DailyAlertCount> daily = incidents
            .GroupBy(i => i.StartsAt.Date)
            .Select(g => new DailyAlertCount(g.Key, g.Count()))
            .OrderBy(d => d.Date)
            .ToList();

        return new AlertStats(
            WindowDays: windowDays,
            Total: total,
            Active: active,
            Acknowledged: acked,
            Resolved: resolved,
            Critical: critical,
            Warning: warning,
            MttrMinutes: mttrMinutes,
            MttaMinutes: mttaMinutes,
            Daily: daily);
    }

    public async Task<List<AlertFrequency>> GetTopAlertsAsync(
        Guid tenantId, int windowDays = 30, int topN = 10, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        var rows = await db.AlertIncidents
            .Where(i => i.Cluster.TenantId == tenantId && i.StartsAt >= from)
            .GroupBy(i => new { i.AlertName, i.Severity })
            .Select(g => new { g.Key.AlertName, g.Key.Severity, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .Take(topN)
            .ToListAsync(ct);

        return rows.Select(r => new AlertFrequency(r.AlertName, r.Severity, r.Count)).ToList();
    }

    public async Task<List<AlertFrequency>> GetTopNamespacesAsync(
        Guid tenantId, int windowDays = 30, int topN = 8, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        // Namespace is embedded in LabelsJson — load and parse client-side
        List<string> labelsJsonList = await db.AlertIncidents
            .Where(i => i.Cluster.TenantId == tenantId && i.StartsAt >= from)
            .Select(i => i.LabelsJson)
            .ToListAsync(ct);

        return labelsJsonList
            .Select(labelsJson =>
            {
                try
                {
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(labelsJson);
                    return doc.RootElement.TryGetProperty("namespace", out System.Text.Json.JsonElement ns)
                        ? ns.GetString() ?? "unknown"
                        : "unknown";
                }
                catch { return "unknown"; }
            })
            .Where(ns => ns != "unknown")
            .GroupBy(ns => ns)
            .Select(g => new AlertFrequency(g.Key, "", g.Count()))
            .OrderByDescending(f => f.Count)
            .Take(topN)
            .ToList();
    }

    public async Task<List<AlertFrequency>> GetAlertsByClusterAsync(
        Guid tenantId, int windowDays = 30, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        var clusterRows = await db.AlertIncidents
            .Where(i => i.Cluster.TenantId == tenantId && i.StartsAt >= from)
            .GroupBy(i => i.Cluster.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .ToListAsync(ct);

        return clusterRows.Select(r => new AlertFrequency(r.Name, "", r.Count)).ToList();
    }

    public async Task<bool> IsInMaintenanceAsync(Guid tenantId, Guid? clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;
        return await db.MaintenanceWindows.AnyAsync(w =>
            w.TenantId == tenantId
            && w.StartsAt <= now
            && w.EndsAt >= now
            && (w.ClusterId == null || w.ClusterId == clusterId), ct);
    }

    public async Task<List<MaintenanceWindow>> GetMaintenanceWindowsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.MaintenanceWindows
            .Include(w => w.Cluster)
            .Where(w => w.TenantId == tenantId)
            .OrderByDescending(w => w.StartsAt)
            .ToListAsync(ct);
    }

    public async Task CreateMaintenanceWindowAsync(
        Guid tenantId, string title, string? description, Guid? clusterId,
        DateTime startsAt, DateTime endsAt, string createdBy,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            Title = title,
            Description = description,
            StartsAt = startsAt,
            EndsAt = endsAt,
            CreatedBy = createdBy
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteMaintenanceWindowAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        MaintenanceWindow? w = await db.MaintenanceWindows.FindAsync([id], ct);
        if (w is not null) db.MaintenanceWindows.Remove(w);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SlaTarget?> GetSlaTargetAsync(
        Guid tenantId, Guid? customerId, Guid? appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        // Most specific target wins: app > customer > tenant
        return await db.SlaTargets
            .Where(s => s.TenantId == tenantId
                && (appId == null || s.AppId == appId || s.AppId == null)
                && (customerId == null || s.CustomerId == customerId || s.CustomerId == null))
            .OrderByDescending(s => s.AppId.HasValue)
            .ThenByDescending(s => s.CustomerId.HasValue)
            .FirstOrDefaultAsync(ct);
    }

    public async Task UpsertSlaTargetAsync(
        Guid tenantId, Guid? customerId, Guid? appId,
        double targetPercent, int windowDays,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        SlaTarget? existing = await db.SlaTargets
            .FirstOrDefaultAsync(s => s.TenantId == tenantId
                && s.CustomerId == customerId && s.AppId == appId, ct);

        if (existing is null)
        {
            db.SlaTargets.Add(new SlaTarget
            {
                TenantId = tenantId,
                CustomerId = customerId,
                AppId = appId,
                TargetPercent = targetPercent,
                MeasurementWindowDays = windowDays
            });
        }
        else
        {
            existing.TargetPercent = targetPercent;
            existing.MeasurementWindowDays = windowDays;
        }
        await db.SaveChangesAsync(ct);
    }
}

public record UptimeResult(int WindowDays, double? Percent)
{
    public string Display => Percent.HasValue ? $"{Percent:F2}%" : "No data";
}

public record AlertStats(
    int WindowDays,
    int Total,
    int Active,
    int Acknowledged,
    int Resolved,
    int Critical,
    int Warning,
    double? MttrMinutes,
    double? MttaMinutes,
    List<DailyAlertCount> Daily)
{
    public string FormatMttr() => MttrMinutes.HasValue
        ? MttrMinutes.Value >= 60
            ? $"{MttrMinutes.Value / 60:F1}h"
            : $"{(int)MttrMinutes.Value}m"
        : "—";

    public string FormatMtta() => MttaMinutes.HasValue
        ? MttaMinutes.Value >= 60
            ? $"{MttaMinutes.Value / 60:F1}h"
            : $"{(int)MttaMinutes.Value}m"
        : "—";
}

public record DailyAlertCount(DateTime Date, int Count);
public record AlertFrequency(string Name, string Severity, int Count);
