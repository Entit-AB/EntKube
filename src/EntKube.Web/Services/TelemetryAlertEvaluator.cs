using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

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
        using IDisposable unattended = scope.DeclareUnattended("telemetry-alert-evaluator");
        ITelemetryIngest store = scope.ServiceProvider.GetRequiredService<ITelemetryIngest>();
        if (!store.IsEnabled) return;

        using TelemetryDbContext db = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<TelemetryDbContext>>().CreateDbContext();

        if (!await db.TelemetryAlertRules.AnyAsync(r => r.IsEnabled, ct)) return;

        ITelemetryBackends backends = scope.ServiceProvider.GetRequiredService<ITelemetryBackends>();
        IRumQueryService rum = scope.ServiceProvider.GetRequiredService<IRumQueryService>();
        IncidentDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IncidentDispatcher>();

        DateTime now = DateTime.UtcNow;

        // Active maintenance windows: hold incident state for covered (tenant, cluster) rather than
        // advancing it in the DB and dropping the notification in the dispatcher — otherwise an alert that
        // fires (or clears) during a window is recorded but never notified, even after the window ends.
        //
        // Asked of Fleet, per tenant, INSIDE the loop below rather than once for everybody up here.
        // The old query read every tenant's windows and filtered afterwards, which is the
        // flattening §4.0.1.2 warns about: the sweep establishes a tenant and then consults a
        // structure built across all of them. It costs one query per tenant with enabled rules.
        EntKube.Contracts.Fleet.IFleetApi fleet =
            scope.ServiceProvider.GetRequiredService<EntKube.Contracts.Fleet.IFleetApi>();
        Dictionary<Guid, IReadOnlyList<EntKube.Contracts.Fleet.MaintenanceWindowSummary>> windowsByTenant = [];

        bool InMaintenance(Guid tenant, Guid cluster) =>
            windowsByTenant.TryGetValue(tenant, out var ws)
            && ws.Any(w => w.ClusterId == null || w.ClusterId == cluster);

        // Accumulate firing/resolved incidents per (tenant, cluster) to dispatch after the single save.
        Dictionary<(Guid Tenant, Guid Cluster), (List<AlertIncident> Firing, List<AlertIncident> Resolved)> byCluster = [];

        // One tenant at a time, outermost. The rules carry a TenantId and this used to read it off the
        // row it had just loaded — which is a tenant to label an incident with, but not a tenant to ASK
        // as: every query below it (the tenant's clusters, and now the telemetry backend's routing
        // decision) would be answered for whatever rows happened to be there. Iterating tenants makes
        // the tenant an input to the cycle, so a rule can only ever reach its own tenant's clusters and
        // its own tenant's telemetry. Same shape as AlertSyncService and the seven other sweeps.
        List<Guid> tenants = await db.Tenants.Select(x => x.Id).ToListAsync(ct);

        foreach (Guid tenantId in tenants)
        {
            List<TelemetryAlertRule> rules = await db.TelemetryAlertRules
                .Where(r => r.IsEnabled && r.TenantId == tenantId).ToListAsync(ct);
            if (rules.Count == 0) continue;

            // After the rules check, so a tenant with nothing enabled costs no extra query.
            windowsByTenant[tenantId] = await fleet.GetActiveMaintenanceWindowsAsync(tenantId, now, ct);

            ITraceQueryService trace = backends.TracesFor(tenantId);
            ILogBackend log = backends.LogsFor(tenantId);

            foreach (TelemetryAlertRule rule in rules)
            {
                List<Guid> clusters = await ResolveClustersAsync(db, tenantId, rule, trace, log, ct);

                foreach (Guid clusterId in clusters)
                {
                    if (InMaintenance(tenantId, clusterId)) continue;        // hold state during maintenance

                    Evaluation ev = await EvaluateRuleAsync(rule, clusterId, now, trace, log, rum, ct);
                    if (ev.Status == EvalStatus.Indeterminate) continue;     // transient failure / no data — hold state

                    string fingerprint = $"telemetry:{rule.Id:N}:{clusterId:N}";
                    AlertIncident? inc = await db.AlertIncidents
                        .FirstOrDefaultAsync(i => i.ClusterId == clusterId && i.Fingerprint == fingerprint, ct);

                    (List<AlertIncident> firing, List<AlertIncident> resolved) = Bucket(byCluster, tenantId, clusterId);

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
        TelemetryDbContext db, Guid tenantId, TelemetryAlertRule rule, ITraceQueryService trace,
        ILogBackend log, CancellationToken ct)
    {
        if (rule.ClusterId is Guid cid) return [cid];

        // RUM rules are site-scoped and must carry the (site's) cluster the incident is attributed to; never
        // fan a RUM rule out across all clusters (that would raise a duplicate incident per cluster).
        if (rule.Kind is TelemetryAlertKind.RumErrorRate or TelemetryAlertKind.RumLcpP75) return [];

        List<Guid> all = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId).Select(c => c.Id).ToListAsync(ct);
        List<Guid> withData = [];
        foreach (Guid c in all)
            if (await HasSignalAsync(rule.Kind, c, trace, log, ct)) withData.Add(c);
        return withData;
    }

    private static Task<bool> HasSignalAsync(
        TelemetryAlertKind kind, Guid clusterId, ITraceQueryService trace, ILogBackend log, CancellationToken ct)
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
        TelemetryAlertRule rule, Guid clusterId, DateTime now, ITraceQueryService trace, ILogBackend log, IRumQueryService rum, CancellationToken ct)
    {
        DateTime from = now.AddMinutes(-Math.Max(1, rule.WindowMinutes));
        int w = rule.WindowMinutes;

        switch (rule.Kind)
        {
            case TelemetryAlertKind.RumErrorRate:
            {
                if (rule.SiteId is not Guid errSite) return Evaluation.Clear;   // misconfigured rule can't fire
                RumSiteOverview? ov = await rum.GetOverviewAsync(rule.TenantId, errSite, from, now, ct);
                if (ov is null) return Evaluation.Unknown;                      // transient store failure — hold
                if (ov.PageViews == 0) return Evaluation.Unknown;              // no traffic — can't judge
                double perMin = ov.Errors / (double)Math.Max(1, w);
                return Evaluation.From(perMin > rule.Threshold,
                    $"browser errors {perMin:F1}/min > {rule.Threshold:F1}/min over {w}m",
                    $"Browser JS error rate was {perMin:F1}/min ({ov.Errors} errors across {ov.Sessions} sessions) over the last {w} minutes.");
            }
            case TelemetryAlertKind.RumLcpP75:
            {
                if (rule.SiteId is not Guid lcpSite) return Evaluation.Clear;
                RumSiteOverview? ov = await rum.GetOverviewAsync(rule.TenantId, lcpSite, from, now, ct);
                if (ov is null || ov.LcpP75 is null) return Evaluation.Unknown;   // failure or no page views — hold
                double p75 = ov.LcpP75.Value;
                return Evaluation.From(p75 > rule.Threshold,
                    $"LCP p75 {p75:F0}ms > {rule.Threshold:F0}ms over {w}m",
                    $"Largest Contentful Paint p75 was {p75:F0}ms (threshold {rule.Threshold:F0}ms) over the last {w} minutes.");
            }
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
