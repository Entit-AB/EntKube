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
///
/// <para>⚠️ <b>Two instances evaluate the same rule at the same minute.</b> Deduping by a
/// fingerprint LOOKUP decided nothing: both instances found no incident and both inserted one,
/// against a unique (ClusterId, Fingerprint) index, and the loser's save threw. Because there
/// was exactly one <c>SaveChangesAsync</c> for the whole installation, after every tenant had
/// been evaluated, that single collision rolled back every tenant's raises and resolutions and
/// skipped the dispatch loop — so one duplicate incident on one rule meant nobody anywhere was
/// notified of anything. Each state change is now claimed on its own row (see the claim helpers
/// below) and the installation-wide save carries nothing anyone is notified about.</para>
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

                    // ⭐ Every state CHANGE below is claimed, and only the instance that wins the
                    // claim puts the incident in a dispatch bucket. The lookup above is a read, and
                    // a read decides nothing on its own: between it and the write, the other
                    // instance has done the same thing. See the claim helpers at the foot of this
                    // file for what each one costs and why it is shaped the way it is.
                    if (ev.Status == EvalStatus.Firing)
                    {
                        if (inc is null)
                        {
                            inc = NewIncident(rule, clusterId, fingerprint, ev, now);
                            if (!await TryRaiseAsync(db, inc, ct))
                            {
                                // Not an error: it is the mechanism working. The instance that got
                                // the row in first will notify for it, and this cycle says nothing.
                                logger.LogDebug(
                                    "Incident {Fingerprint} on cluster {Cluster} was raised by another instance; leaving the notification to it.",
                                    fingerprint, clusterId);
                                continue;
                            }
                            firing.Add(inc);
                        }
                        else if (inc.Status == IncidentStatus.Resolved)
                        {
                            if (!await TryReactivateAsync(db, inc, rule.Severity, ev.Summary, ev.Description, now, ct))
                                continue;   // another instance re-raised it and is notifying
                            firing.Add(inc);
                        }
                        else
                        {
                            // Still firing — refresh fields, do not re-notify. Deliberately NOT
                            // claimed: nobody is paged off the back of it, both instances write the
                            // same values, and it is the only thing left in the batched save below.
                            inc.Summary = Truncate(ev.Summary, 500); inc.Description = Truncate(ev.Description, 2000);
                            inc.Severity = Truncate(rule.Severity, 20); inc.UpdatedAt = now;
                        }
                    }
                    else if (inc is not null && inc.Status != IncidentStatus.Resolved)
                    {
                        if (!await TryResolveAsync(db, inc, now, ct))
                            continue;       // another instance resolved it and is notifying
                        resolved.Add(inc);
                    }
                }
            }
        }

        // ⚠️ This used to be the ONLY SaveChanges in the cycle, for the whole installation, and
        // every raise and every resolution rode on it. One unique violation on one rule — two
        // instances both inserting the same (ClusterId, Fingerprint) — threw here, rolled back
        // every tenant's incident changes, and skipped the dispatch loop below, so nobody was
        // notified of anything. (The NewIncident truncation comment names the same failure by a
        // different route; this was a second road to it.)
        //
        // Every change that anybody is notified about is now durable before we get here, written
        // by its own claim: what is left is the idempotent "still firing" field refresh. So the
        // save can fail without costing a notification, and dispatch no longer depends on it —
        // which is exactly the coupling that made one collision silence the installation.
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex,
                "Refreshing fields on still-firing telemetry incidents failed; the next cycle will redo it. Notifications are unaffected.");
            db.ChangeTracker.Clear();
        }

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

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // The claims
    //
    // ⭐ A claim is not a lock. Two instances evaluate the same rule at the same second, both
    // race, exactly one wins, and the loser does nothing and does not throw. There is no leader
    // election, no advisory lock and no lease table in this codebase, and this does not add one:
    // it is the same compare-and-set shape as the per-cluster period claim in
    // Services/Cost/CostLedgerWriter.cs, which exists for the same reason — to stop a second
    // instance doing a thing that must happen exactly once (there, billing an hour twice; here,
    // raising a duplicate incident and paging twice for it).
    //
    // ⚠️ The row the database refuses to duplicate IS the claim for a raise. The unique index on
    // (ClusterId, Fingerprint) (Data/Modules/TelemetryModel.cs:100) already decides the winner;
    // what was missing was anybody asking it the question at a point where losing is survivable.
    //
    // These are public only so a test can run two of them against one database and assert that
    // one effect comes out. Nothing else calls them.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises <paramref name="incident"/>, returning false if another instance raised the same
    /// (ClusterId, Fingerprint) first.
    ///
    /// <para>The insert gets its own <c>SaveChangesAsync</c> deliberately. It used to ride on the
    /// one save at the end of the cycle, so the unique violation surfaced there, after every
    /// tenant had been evaluated, and took the whole cycle — and the dispatch that follows it —
    /// down with it. Saving here means the collision is raised and absorbed at the one row that
    /// caused it.</para>
    ///
    /// <para>⚠️ A failed save rolls back whatever else was pending in the context (the
    /// still-firing field refreshes). That is harmless: those entities stay Modified and the save
    /// at the end of the cycle writes them. Only the incident that collided is detached, so a
    /// later save does not retry an insert the database has already refused.</para>
    /// </summary>
    public static async Task<bool> TryRaiseAsync(
        TelemetryDbContext db, AlertIncident incident, CancellationToken ct = default)
    {
        db.AlertIncidents.Add(incident);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another instance got there first; theirs is as good as ours.
            db.Entry(incident).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Re-raises a resolved incident, returning false if another instance re-raised it first.
    ///
    /// <para>Compare-and-set on the status the caller read: <c>Status == Resolved</c> is the
    /// condition that made this a re-raise, so it is also the condition the write insists on. The
    /// whole transition goes in that one statement rather than being left for a later save,
    /// because a half-applied re-raise (active again, but still carrying the old summary and a
    /// stale ResolvedAt) is what the on-call sees.</para>
    ///
    /// <para>On a win the in-memory entity is brought level with what was written and marked
    /// Unchanged — the dispatcher is handed the incident as it now stands in the database, and
    /// the batched save does not write it a second time.</para>
    /// </summary>
    public static async Task<bool> TryReactivateAsync(
        TelemetryDbContext db, AlertIncident incident,
        string? severity, string? summary, string? description, DateTime now, CancellationToken ct = default)
    {
        string sev = Truncate(severity, 20), sum = Truncate(summary, 500), desc = Truncate(description, 2000);

        int claimed = await db.AlertIncidents
            .Where(i => i.Id == incident.Id && i.Status == IncidentStatus.Resolved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IncidentStatus.Active)
                .SetProperty(i => i.StartsAt, now)
                .SetProperty(i => i.EndsAt, (DateTime?)null)
                .SetProperty(i => i.ResolvedAt, (DateTime?)null)
                .SetProperty(i => i.AcknowledgedBy, (string?)null)
                .SetProperty(i => i.AcknowledgedAt, (DateTime?)null)
                .SetProperty(i => i.Severity, sev)
                .SetProperty(i => i.Summary, sum)
                .SetProperty(i => i.Description, desc)
                .SetProperty(i => i.UpdatedAt, now), ct);

        if (claimed == 0) return false;

        incident.Status = IncidentStatus.Active;
        incident.StartsAt = now;
        incident.EndsAt = null;
        incident.ResolvedAt = null;
        incident.AcknowledgedBy = null;
        incident.AcknowledgedAt = null;
        incident.Severity = sev;
        incident.Summary = sum;
        incident.Description = desc;
        incident.UpdatedAt = now;
        db.Entry(incident).State = EntityState.Unchanged;
        return true;
    }

    /// <summary>
    /// Resolves an open incident, returning false if another instance resolved it first.
    ///
    /// <para>⚠️ Without the claim this was not an exception — both instances would write the same
    /// three fields and both would succeed. It is here because both would then also tell the
    /// dispatcher, and the customer gets the all-clear twice. A duplicate page is the duplication
    /// §9.3 is about; a thrown save is only the loudest form of it.</para>
    ///
    /// <para><c>EndsAt</c> keeps its <c>??=</c> semantics in SQL: the end of the alert is when it
    /// stopped firing, if something already knew that, not when we noticed.</para>
    /// </summary>
    public static async Task<bool> TryResolveAsync(
        TelemetryDbContext db, AlertIncident incident, DateTime now, CancellationToken ct = default)
    {
        int claimed = await db.AlertIncidents
            .Where(i => i.Id == incident.Id && i.Status != IncidentStatus.Resolved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IncidentStatus.Resolved)
                .SetProperty(i => i.EndsAt, i => i.EndsAt ?? now)
                .SetProperty(i => i.ResolvedAt, now)
                .SetProperty(i => i.UpdatedAt, now), ct);

        if (claimed == 0) return false;

        incident.Status = IncidentStatus.Resolved;
        incident.EndsAt ??= now;
        incident.ResolvedAt = now;
        incident.UpdatedAt = now;
        db.Entry(incident).State = EntityState.Unchanged;
        return true;
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

    // Reactivate() used to live here, mutating the tracked entity and leaving the write to the
    // cycle's single save. It is now the body of TryReactivateAsync, which writes the same fields
    // as one claimed statement — the re-raise and the claim cannot be separated without leaving a
    // window in which both instances believe they are the one re-raising.

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
