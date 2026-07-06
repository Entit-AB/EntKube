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
        // Accumulate firing/resolved incidents per (tenant, cluster) to dispatch after the single save.
        Dictionary<(Guid Tenant, Guid Cluster), (List<AlertIncident> Firing, List<AlertIncident> Resolved)> byCluster = [];

        foreach (TelemetryAlertRule rule in rules)
        {
            List<Guid> clusters = rule.ClusterId is Guid cid
                ? [cid]
                : await db.KubernetesClusters.Where(c => c.TenantId == rule.TenantId).Select(c => c.Id).ToListAsync(ct);

            foreach (Guid clusterId in clusters)
            {
                Evaluation ev = await EvaluateRuleAsync(rule, clusterId, now, trace, log, ct);
                string fingerprint = $"telemetry:{rule.Id:N}:{clusterId:N}";
                AlertIncident? inc = await db.AlertIncidents
                    .FirstOrDefaultAsync(i => i.ClusterId == clusterId && i.Fingerprint == fingerprint, ct);

                (List<AlertIncident> firing, List<AlertIncident> resolved) = Bucket(byCluster, rule.TenantId, clusterId);

                if (ev.Firing)
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
                        inc.Summary = ev.Summary; inc.Description = ev.Description;
                        inc.Severity = rule.Severity; inc.UpdatedAt = now;
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

    private sealed record Evaluation(bool Firing, string Summary, string Description);

    private static async Task<Evaluation> EvaluateRuleAsync(
        TelemetryAlertRule rule, Guid clusterId, DateTime now, PgTraceService trace, PgLogService log, CancellationToken ct)
    {
        DateTime from = now.AddMinutes(-Math.Max(1, rule.WindowMinutes));
        int w = rule.WindowMinutes;

        switch (rule.Kind)
        {
            case TelemetryAlertKind.TraceLatencyP95:
            {
                if (string.IsNullOrEmpty(rule.Service)) return new(false, "", "");
                KubernetesOperationResult<ServiceStats> res = await trace.GetServiceStatsAsync(clusterId, rule.Service, from, now, ct);
                if (!res.IsSuccess || res.Data is null || res.Data.Count == 0) return new(false, "", "");
                double p95 = res.Data.P95Ms;
                return new(p95 > rule.Threshold,
                    $"{rule.Service} p95 latency {p95:F0}ms > {rule.Threshold:F0}ms over {w}m",
                    $"p95 latency for service '{rule.Service}' was {p95:F0}ms (threshold {rule.Threshold:F0}ms) over the last {w} minutes.");
            }
            case TelemetryAlertKind.TraceErrorRate:
            {
                if (string.IsNullOrEmpty(rule.Service)) return new(false, "", "");
                KubernetesOperationResult<ServiceStats> res = await trace.GetServiceStatsAsync(clusterId, rule.Service, from, now, ct);
                if (!res.IsSuccess || res.Data is null || res.Data.Count == 0) return new(false, "", "");
                double rate = res.Data.Errors * 100.0 / res.Data.Count;
                return new(rate > rule.Threshold,
                    $"{rule.Service} error rate {rate:F1}% > {rule.Threshold:F1}% over {w}m",
                    $"Error rate for '{rule.Service}' was {rate:F1}% ({res.Data.Errors}/{res.Data.Count} inbound spans) over the last {w} minutes.");
            }
            case TelemetryAlertKind.LogErrorRate:
            {
                KubernetesOperationResult<long> res = await log.CountAsync(clusterId, rule.Namespace, rule.MatchText, LogLevel.Error, from, now, ct);
                if (!res.IsSuccess) return new(false, "", "");
                double perMin = res.Data / (double)Math.Max(1, w);
                string ns = rule.Namespace is null ? "all namespaces" : $"namespace {rule.Namespace}";
                return new(perMin > rule.Threshold,
                    $"error logs {perMin:F1}/min > {rule.Threshold:F1}/min ({ns})",
                    $"Error/fatal log rate was {perMin:F1}/min ({res.Data} lines) in {ns} over the last {w} minutes.");
            }
            default:
                return new(false, "", "");
        }
    }

    private static AlertIncident NewIncident(TelemetryAlertRule rule, Guid clusterId, string fingerprint, Evaluation ev, DateTime now)
        => new()
        {
            ClusterId = clusterId,
            Fingerprint = fingerprint,
            AlertName = rule.Name,
            Severity = rule.Severity,
            Summary = ev.Summary,
            Description = ev.Description,
            RunbookUrl = rule.RunbookUrl ?? "",
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
        inc.Severity = rule.Severity;
        inc.Summary = ev.Summary;
        inc.Description = ev.Description;
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
