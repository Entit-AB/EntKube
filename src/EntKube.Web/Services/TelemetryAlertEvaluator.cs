using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Evaluates user-defined <see cref="TelemetryAlertRule"/>s against the native store (logs/spans) on a
/// schedule and raises/resolves <see cref="AlertIncident"/>s (deduped per rule+cluster by fingerprint)
/// through the existing incident + notification pipeline. Same shape as AlertSyncService, but the source
/// of truth is our own telemetry rather than Alertmanager. No-op when the telemetry store is disabled.
/// </summary>
public sealed class TelemetryAlertEvaluator(
    IServiceScopeFactory scopeFactory, ILogger<TelemetryAlertEvaluator> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EvaluateAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Telemetry alert evaluation cycle failed."); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task EvaluateAllAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        TelemetryStore store = scope.ServiceProvider.GetRequiredService<TelemetryStore>();
        if (!store.IsEnabled) return;

        using ApplicationDbContext db = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        List<TelemetryAlertRule> rules = await db.TelemetryAlertRules.Where(r => r.IsEnabled).ToListAsync(ct);
        if (rules.Count == 0) return;

        PgTraceService trace = scope.ServiceProvider.GetRequiredService<PgTraceService>();
        PgLogService log = scope.ServiceProvider.GetRequiredService<PgLogService>();
        IncidentDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IncidentDispatcher>();

        DateTime now = DateTime.UtcNow;

        // Active maintenance windows: hold incident state for covered (tenant, cluster) rather than
        // advancing it in the DB and dropping the notification in the dispatcher — otherwise an alert that
        // fires (or clears) during a window is recorded but never notified, even after the window ends.
        List<MaintenanceWindow> windows = await db.MaintenanceWindows
            .Where(w => w.StartsAt <= now && w.EndsAt >= now).ToListAsync(ct);
        bool InMaintenance(Guid tenant, Guid cluster) =>
            windows.Any(w => w.TenantId == tenant && (w.ClusterId == null || w.ClusterId == cluster));

        // Accumulate firing/resolved incidents per (tenant, cluster) to dispatch after the single save.
        Dictionary<(Guid Tenant, Guid Cluster), (List<AlertIncident> Firing, List<AlertIncident> Resolved)> byCluster = [];

        foreach (TelemetryAlertRule rule in rules)
        {
            List<Guid> clusters = await ResolveClustersAsync(db, rule, trace, log, ct);

            foreach (Guid clusterId in clusters)
            {
                if (InMaintenance(rule.TenantId, clusterId)) continue;   // hold state during maintenance

                Evaluation ev = await EvaluateRuleAsync(rule, clusterId, now, trace, log, ct);
                if (ev.Status == EvalStatus.Indeterminate) continue;     // transient failure / no data — hold state

                string fingerprint = $"telemetry:{rule.Id:N}:{clusterId:N}";
                AlertIncident? inc = await db.AlertIncidents
                    .FirstOrDefaultAsync(i => i.ClusterId == clusterId && i.Fingerprint == fingerprint, ct);

                (List<AlertIncident> firing, List<AlertIncident> resolved) = Bucket(byCluster, rule.TenantId, clusterId);

                if (ev.Status == EvalStatus.Firing)
                {
                    if (inc is null)
                    {
                        inc = NewIncident(rule, clusterId, fingerprint, ev, now);
                        db.AlertIncidents.Add(inc);
                        firing.Add(inc);
                    }
                    else if (inc.Status == IncidentStatus.Resolved)
                    {
                        Reactivate(inc, rule, ev, now);
                        firing.Add(inc);
                    }
                    else
                    {
                        // still firing — refresh fields, do not re-notify
                        inc.Summary = Truncate(ev.Summary, 500); inc.Description = Truncate(ev.Description, 2000);
                        inc.Severity = Truncate(rule.Severity, 20); inc.UpdatedAt = now;
                    }
                }
                else if (inc is not null && inc.Status != IncidentStatus.Resolved)
                {
                    inc.Status = IncidentStatus.Resolved;
                    inc.EndsAt ??= now;
                    inc.ResolvedAt = now;
                    inc.UpdatedAt = now;
                    resolved.Add(inc);
                }
            }
        }

        await db.SaveChangesAsync(ct);   // before dispatch so new incidents have IDs

        foreach (KeyValuePair<(Guid Tenant, Guid Cluster), (List<AlertIncident> Firing, List<AlertIncident> Resolved)> kv in byCluster)
            await dispatcher.DispatchAsync(kv.Key.Tenant, kv.Key.Cluster, kv.Value.Firing, kv.Value.Resolved, ct);
    }

    private static (List<AlertIncident> Firing, List<AlertIncident> Resolved) Bucket(
        Dictionary<(Guid, Guid), (List<AlertIncident>, List<AlertIncident>)> map, Guid tenant, Guid cluster)
    {
        if (!map.TryGetValue((tenant, cluster), out (List<AlertIncident>, List<AlertIncident>) b))
        {
            b = ([], []);
            map[(tenant, cluster)] = b;
        }
        return b;
    }

    /// <summary>Clusters to evaluate a rule against: the pinned cluster, or (for a tenant-wide rule)
    /// only the clusters that actually carry telemetry for this signal — idle clusters are skipped so
    /// they don't cost an aggregate scan every cycle.</summary>
    private static async Task<List<Guid>> ResolveClustersAsync(
        ApplicationDbContext db, TelemetryAlertRule rule, PgTraceService trace, PgLogService log, CancellationToken ct)
    {
        if (rule.ClusterId is Guid cid) return [cid];

        List<Guid> all = await db.KubernetesClusters
            .Where(c => c.TenantId == rule.TenantId).Select(c => c.Id).ToListAsync(ct);
        List<Guid> withData = [];
        foreach (Guid c in all)
            if (await HasSignalAsync(rule.Kind, c, trace, log, ct)) withData.Add(c);
        return withData;
    }

    private static Task<bool> HasSignalAsync(
        TelemetryAlertKind kind, Guid clusterId, PgTraceService trace, PgLogService log, CancellationToken ct)
        => kind == TelemetryAlertKind.LogErrorRate ? log.HasDataAsync(clusterId, ct) : trace.HasDataAsync(clusterId, ct);

    private enum EvalStatus { Firing, Clear, Indeterminate }

    // Clear = data present and below threshold (safe to resolve). Indeterminate = query failed or no data
    // to judge on (hold the current incident state rather than declaring an all-clear).
    private sealed record Evaluation(EvalStatus Status, string Summary, string Description)
    {
        public static readonly Evaluation Clear = new(EvalStatus.Clear, "", "");
        public static readonly Evaluation Unknown = new(EvalStatus.Indeterminate, "", "");
        public static Evaluation From(bool firing, string summary, string description)
            => new(firing ? EvalStatus.Firing : EvalStatus.Clear, summary, description);
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private static async Task<Evaluation> EvaluateRuleAsync(
        TelemetryAlertRule rule, Guid clusterId, DateTime now, PgTraceService trace, PgLogService log, CancellationToken ct)
    {
        DateTime from = now.AddMinutes(-Math.Max(1, rule.WindowMinutes));
        int w = rule.WindowMinutes;

        switch (rule.Kind)
        {
            case TelemetryAlertKind.TraceLatencyP95:
            {
                if (string.IsNullOrEmpty(rule.Service)) return Evaluation.Clear;   // misconfigured rule can't fire
                KubernetesOperationResult<ServiceStats> res = await trace.GetServiceStatsAsync(clusterId, rule.Service, from, now, ct);
                if (!res.IsSuccess) return Evaluation.Unknown;                     // transient store failure — hold
                if (res.Data is null || res.Data.Count == 0) return Evaluation.Unknown;   // no spans — can't judge; don't all-clear a down service
                double p95 = res.Data.P95Ms;
                return Evaluation.From(p95 > rule.Threshold,
                    $"{rule.Service} p95 latency {p95:F0}ms > {rule.Threshold:F0}ms over {w}m",
                    $"p95 latency for service '{rule.Service}' was {p95:F0}ms (threshold {rule.Threshold:F0}ms) over the last {w} minutes.");
            }
            case TelemetryAlertKind.TraceErrorRate:
            {
                if (string.IsNullOrEmpty(rule.Service)) return Evaluation.Clear;   // misconfigured rule can't fire
                KubernetesOperationResult<ServiceStats> res = await trace.GetServiceStatsAsync(clusterId, rule.Service, from, now, ct);
                if (!res.IsSuccess) return Evaluation.Unknown;                     // transient store failure — hold
                if (res.Data is null || res.Data.Count == 0) return Evaluation.Unknown;   // no spans — can't judge; don't all-clear a down service
                double rate = res.Data.Errors * 100.0 / res.Data.Count;
                return Evaluation.From(rate > rule.Threshold,
                    $"{rule.Service} error rate {rate:F1}% > {rule.Threshold:F1}% over {w}m",
                    $"Error rate for '{rule.Service}' was {rate:F1}% ({res.Data.Errors}/{res.Data.Count} inbound spans) over the last {w} minutes.");
            }
            case TelemetryAlertKind.LogErrorRate:
            {
                KubernetesOperationResult<long> res = await log.CountAsync(clusterId, rule.Namespace, rule.MatchText, LogLevel.Error, from, now, ct);
                if (!res.IsSuccess) return Evaluation.Unknown;                     // transient store failure — hold
                double perMin = res.Data / (double)Math.Max(1, w);
                string ns = rule.Namespace is null ? "all namespaces" : $"namespace {rule.Namespace}";
                return Evaluation.From(perMin > rule.Threshold,
                    $"error logs {perMin:F1}/min > {rule.Threshold:F1}/min ({ns})",
                    $"Error/fatal log rate was {perMin:F1}/min ({res.Data} lines) in {ns} over the last {w} minutes.");
            }
            default:
                return Evaluation.Clear;
        }
    }

    private static AlertIncident NewIncident(TelemetryAlertRule rule, Guid clusterId, string fingerprint, Evaluation ev, DateTime now)
        => new()
        {
            ClusterId = clusterId,
            Fingerprint = fingerprint,
            // Truncate to the AlertIncident column caps so one over-long rule field can't throw and roll
            // back the whole cycle's single SaveChanges (freezing alerting for every tenant).
            AlertName = Truncate(rule.Name, 200),
            Severity = Truncate(rule.Severity, 20),
            Summary = Truncate(ev.Summary, 500),
            Description = Truncate(ev.Description, 2000),
            RunbookUrl = Truncate(rule.RunbookUrl, 500),
            LabelsJson = Labels(rule),
            StartsAt = now,
            Status = IncidentStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static void Reactivate(AlertIncident inc, TelemetryAlertRule rule, Evaluation ev, DateTime now)
    {
        inc.Status = IncidentStatus.Active;
        inc.StartsAt = now;
        inc.EndsAt = null;
        inc.ResolvedAt = null;
        inc.AcknowledgedBy = null;
        inc.AcknowledgedAt = null;
        inc.Severity = Truncate(rule.Severity, 20);
        inc.Summary = Truncate(ev.Summary, 500);
        inc.Description = Truncate(ev.Description, 2000);
        inc.UpdatedAt = now;
    }

    private static string Labels(TelemetryAlertRule rule)
    {
        Dictionary<string, string> labels = new()
        {
            ["source"] = "telemetry",
            ["rule"] = rule.Name,
            ["kind"] = rule.Kind.ToString()
        };
        if (!string.IsNullOrEmpty(rule.Service)) labels["service"] = rule.Service;
        if (!string.IsNullOrEmpty(rule.Namespace)) labels["namespace"] = rule.Namespace;
        return JsonSerializer.Serialize(labels);
    }
}
