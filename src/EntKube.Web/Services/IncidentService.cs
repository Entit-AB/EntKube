using EntKube.Contracts.Delivery;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

public class IncidentService(IDbContextFactory<ApplicationDbContext> dbFactory, IDeliveryApi delivery)
{
    /// <summary>
    /// Returns incidents that are relevant to a specific set of deployments,
    /// matched by cluster ID and namespace label in the alert's LabelsJson.
    /// Used by the customer portal to show incidents scoped to the customer's apps.
    /// </summary>
    /// <param name="tenantId">
    /// Whose incidents. This method takes <em>entities</em> and derives its cluster set from
    /// them, so without a tenant it trusts the caller's list completely — and the predicate below
    /// is on <c>ClusterId</c>, which says nothing about who owns the cluster. The portal builds
    /// that list for one customer today; the clause means the service no longer depends on that
    /// being true.
    /// </param>
    public async Task<List<AlertIncident>> GetIncidentsForDeploymentsAsync(
        Guid tenantId,
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
            .Where(i => clusterIds.Contains(i.ClusterId) && i.StartsAt >= from
                     && i.Cluster.TenantId == tenantId)
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
            .Include(i => i.Cluster).ThenInclude(c => c.Environment)
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

    /// <param name="tenantId">
    /// Whose incident. ⚠️ <c>AlertIncident</c> carries no <c>TenantId</c> — only a
    /// <c>ClusterId</c> — so the scope travels through the cluster, exactly as
    /// <see cref="GetIncidentsForTenantAsync"/> already does with
    /// <c>i.Cluster.TenantId == tenantId</c>. The missing column is very likely why these by-id
    /// methods never gained the parameter while every <c>*ForTenantAsync</c> beside them has one.
    /// </param>
    public async Task<AlertIncident?> GetByIdAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AlertIncidents
            .Include(i => i.Cluster)
            .Include(i => i.Notes.OrderBy(n => n.CreatedAt))
            .FirstOrDefaultAsync(i => i.Id == id && i.Cluster.TenantId == tenantId, ct);
    }

    public async Task AcknowledgeAsync(
        Guid tenantId, Guid id, string acknowledgedBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AlertIncident? incident = await db.AlertIncidents
            .FirstOrDefaultAsync(i => i.Id == id && i.Cluster.TenantId == tenantId, ct);
        if (incident is null || incident.Status != IncidentStatus.Active) return;

        incident.Status = IncidentStatus.Acknowledged;
        incident.AcknowledgedBy = acknowledgedBy;
        incident.AcknowledgedAt = DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveAsync(
        Guid tenantId, Guid id, string resolvedBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AlertIncident? incident = await db.AlertIncidents
            .FirstOrDefaultAsync(i => i.Id == id && i.Cluster.TenantId == tenantId, ct);
        if (incident is null || incident.Status == IncidentStatus.Resolved) return;

        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = DateTime.UtcNow;
        incident.EndsAt ??= DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task AssignAsync(
        Guid tenantId, Guid id, string assignee, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AlertIncident? incident = await db.AlertIncidents
            .FirstOrDefaultAsync(i => i.Id == id && i.Cluster.TenantId == tenantId, ct);
        if (incident is null) return;

        incident.AssignedTo = assignee;
        incident.AssignedAt = DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task BulkAcknowledgeAsync(
        Guid tenantId, IEnumerable<Guid> ids, string acknowledgedBy,
        CancellationToken ct = default)
    {
        List<Guid> idList = ids.ToList();
        if (idList.Count == 0) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<AlertIncident> incidents = await db.AlertIncidents
            // The tenant clause applies to the SET, not per id: a bulk call carrying a mix
            // acknowledges only this tenant's and leaves the rest alone.
            .Where(i => idList.Contains(i.Id) && i.Cluster.TenantId == tenantId
                     && i.Status == IncidentStatus.Active)
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

    public async Task<List<NotificationDelivery>> GetDeliveriesAsync(
        Guid tenantId, Guid incidentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.NotificationDeliveries
            .Include(d => d.Channel)
            .Where(d => d.IncidentId == incidentId
                     && d.Incident.Cluster.TenantId == tenantId)
            .OrderByDescending(d => d.SentAt)
            .Take(30)
            .ToListAsync(ct);
    }

    public async Task BulkResolveAsync(
        Guid tenantId, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        List<Guid> idList = ids.ToList();
        if (idList.Count == 0) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<AlertIncident> incidents = await db.AlertIncidents
            .Where(i => idList.Contains(i.Id) && i.Cluster.TenantId == tenantId
                     && i.Status != IncidentStatus.Resolved)
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

    /// <param name="tenantId">
    /// Whose incident. ⚠️ This WRITES WITHOUT READING — it inserts the note for the id it was
    /// handed, so there was no lookup here to be missing a predicate, and a scan for unscoped
    /// by-id lookups could not have found it. An incident note is operator-visible commentary on
    /// somebody's outage, so it needs an existence check rather than a scoped lookup.
    /// </param>
    public async Task AddNoteAsync(
        Guid tenantId, Guid incidentId, string author, string content,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool incidentIsTenants = await db.AlertIncidents
            .AnyAsync(i => i.Id == incidentId && i.Cluster.TenantId == tenantId, ct);

        if (!incidentIsTenants)
        {
            return;
        }

        db.IncidentNotes.Add(new IncidentNote
        {
            IncidentId = incidentId,
            Author = author,
            Content = content
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<UptimeResult> GetUptimeAsync(
        Guid tenantId,
        Guid deploymentId,
        int windowDays,
        CancellationToken ct = default)
    {
        // Delivery's table, asked for through Delivery's contract — and asked for as the AGGREGATE,
        // because the ratio is the only thing this method has ever returned. It used to load every
        // sample in the window (five-minute sampling, so ~8,600 a month per deployment) and count
        // them here.
        //
        // It also used to reach the tenant by walking s.Deployment.App.Customer.TenantId: three
        // navigations out of Telemetry, across two other modules, to check a predicate. The
        // contract applies the same predicate on Delivery's own side of the boundary.
        DateTime from = DateTime.UtcNow.AddDays(-windowDays);

        IReadOnlyList<DeploymentHealthRatio> ratios =
            await delivery.GetHealthRatiosAsync(tenantId, [deploymentId], from, null, ct);

        // Absent means no samples were observed, which is not the same as none being healthy —
        // the caller shows no figure rather than 0%.
        if (ratios.FirstOrDefault() is not DeploymentHealthRatio r)
            return new UptimeResult(windowDays, null);

        double percent = (double)r.HealthySamples / r.TotalSamples * 100.0;
        return new UptimeResult(windowDays, Math.Round(percent, 2));
    }

    public async Task<int> GetActiveAlertCountForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AlertIncidents
            .CountAsync(i => i.Cluster.TenantId == tenantId
                && (i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Acknowledged), ct);
    }

    /// <summary>
    /// What is firing on each of these clusters, split by severity.
    ///
    /// <para>⭐ <b>This counted <c>Status == Active &amp;&amp; Severity == "critical"</c>, and both
    /// halves of that were wrong for what the caller renders.</b></para>
    ///
    /// <para><b>Acknowledged is not resolved.</b> <see cref="AlertStats.Open"/>, in this same
    /// file, already states the rule: <c>Active + Acknowledged</c>. The two sibling methods ten
    /// lines above and below this one both filter <c>Active || Acknowledged</c>. This one did
    /// not, so an on-call engineer acknowledging the only critical alert dropped the count to
    /// zero — and <c>TenantMonitoringOverview</c> renders zero as a green <b>"Healthy"</b>. The
    /// act of a person taking ownership of an incident made the dashboard report that there was
    /// nothing wrong.</para>
    ///
    /// <para><b>Warnings existed and were never shown.</b> Restricting to <c>critical</c> under a
    /// card captioned "Active Alerts" meant a cluster with six firing warnings read as healthy.
    /// The severity is kept here rather than discarded, because flattening a warning into the
    /// red count would be the opposite error — the distinction is real and the caller needs it to
    /// colour the badge.</para>
    ///
    /// <para>⚠️ <see cref="GetActiveAlertCountForTenantAsync"/> is the correct tenant-wide
    /// version of this and has no callers. The overview computes its headline by summing these
    /// counts instead, which is why that headline was wrong in exactly the same two ways.</para>
    /// </summary>
    public async Task<Dictionary<Guid, FiringCounts>> GetActiveCountsPerClusterAsync(
        IEnumerable<Guid> clusterIds, CancellationToken ct = default)
    {
        List<Guid> ids = clusterIds.ToList();
        if (ids.Count == 0) return new();

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        var rows = await db.AlertIncidents
            .Where(i => ids.Contains(i.ClusterId)
                     && (i.Status == IncidentStatus.Active
                         || i.Status == IncidentStatus.Acknowledged))
            .GroupBy(i => i.ClusterId)
            .Select(g => new
            {
                ClusterId = g.Key,

                // Compared in the database, so the severity vocabulary stays in one place. The
                // stored values seen in this codebase are "critical", "warning", "none" and "";
                // anything that is not critical counts as Other rather than being dropped, so a
                // severity nobody anticipated still reaches somebody's screen.
                Critical = g.Count(i => i.Severity == "critical"),
                Other = g.Count(i => i.Severity != "critical"),
            })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ClusterId, r => new FiringCounts(r.Critical, r.Other));
    }

    public async Task<List<AlertIncident>> GetActiveIncidentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AlertIncidents
            .Include(i => i.Cluster).ThenInclude(c => c.Environment)
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

    /// <param name="kind">
    /// Whether the agreement's notice period applies. Planned by default: a window
    /// recorded without saying which kind it is has not earned the exemption.
    /// </param>
    /// <param name="announcedAt">
    /// When the customer was told, when that is not now — for a window entered here after
    /// the mail already went out. Null counts the notice from this moment.
    /// </param>
    public async Task CreateMaintenanceWindowAsync(
        Guid tenantId, string title, string? description, Guid? clusterId,
        DateTime startsAt, DateTime endsAt, string createdBy,
        MaintenanceKind kind = MaintenanceKind.Planned,
        DateTime? announcedAt = null,
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
            CreatedBy = createdBy,
            Kind = kind,
            AnnouncedAt = announcedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteMaintenanceWindowAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        MaintenanceWindow? w = await db.MaintenanceWindows
            .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId, ct);
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

/// <summary>
/// What is firing on one cluster, kept split by severity.
///
/// <para>⚠️ <b><see cref="Total"/> is what "is anything wrong" asks, and it is not
/// <see cref="Critical"/>.</b> The caller renders a green "Healthy" badge on zero, so reading
/// only the critical count there is what made a cluster with six firing warnings — and a
/// cluster whose critical alert had just been acknowledged by the person dealing with it —
/// claim to be healthy.</para>
///
/// <para>Both numbers count <c>Active</c> and <c>Acknowledged</c> incidents, after
/// <see cref="AlertStats.Open"/>: acknowledging an alert records that somebody has taken it on,
/// not that it stopped firing.</para>
/// </summary>
/// <param name="Critical">Firing incidents whose severity is <c>critical</c>.</param>
/// <param name="Other">
/// Firing incidents of every other severity — <c>warning</c> and anything unrecognised, which
/// is counted here rather than dropped so an unanticipated severity still reaches a screen.
/// </param>
public readonly record struct FiringCounts(int Critical, int Other)
{
    /// <summary>Everything firing, which is what a "healthy or not" question means.</summary>
    public int Total => Critical + Other;
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
    /// <summary>
    /// Incidents open right now: <see cref="Active"/> plus <see cref="Acknowledged"/>, which is
    /// exactly the set <see cref="Critical"/> and <see cref="Warning"/> are counted from —
    /// <c>GetStatsForTenantAsync</c> builds those four from one query whose only predicate is
    /// <c>Status == Active || Status == Acknowledged</c>.
    ///
    /// <para>⚠️ <b>This is not <see cref="Total"/>, and the difference is a trap.</b> Total counts
    /// incidents that STARTED inside the window; the severity counts deliberately ignore the
    /// window so a long-running alert still shows, which its own comment explains. Both are right
    /// as counts. Dividing one by the other is not a share of anything, and it produced severity
    /// bars wider than 100% and a negative "Other" — three open criticals that began before the
    /// window, against one incident inside it, gave 300%.</para>
    ///
    /// <para>So anything expressing a severity count as a proportion divides by this.</para>
    /// </summary>
    public int Open => Active + Acknowledged;

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
