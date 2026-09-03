using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Services.Upgrades;

namespace EntKube.Web.Services;

/// <summary>
/// Time-to-impact bucket for a finding — answers the operator's question
/// "when does this need doing?". Ordered most-urgent-first.
/// </summary>
public enum AdvisorHorizon
{
    /// <summary>The deadline has already passed — should have been done already.</summary>
    Overdue = 0,
    /// <summary>Impact within ~48h — do it today.</summary>
    Today = 1,
    /// <summary>Impact within ~a week.</summary>
    ThisWeek = 2,
    /// <summary>On the radar but no near-term deadline (best-practice gaps, distant expiries).</summary>
    Later = 3,
}

public enum AdvisorSeverity
{
    Critical = 0,
    Warning = 1,
    Info = 2,
}

/// <summary>Broad problem domain, used for grouping and iconography.</summary>
public enum AdvisorCategory
{
    Security,        // certificates, kubeconfigs, OAuth client credentials
    Reliability,     // firing / unresolved incidents
    DataProtection,  // backups missing, failed, or stale
    Capacity,        // resources trending toward their ceiling (PVC fill, memory-vs-limit)
    Maintenance,     // components behind upstream, Kubernetes versions nearing end-of-life
    SupplyChain,     // vulnerable, unscanned or unsigned images running in the fleet
}

/// <summary>
/// A single normalized, actionable item synthesized from an existing operational
/// signal (expiring secret, open incident, stale backup, …). Findings are computed
/// on read — they are not persisted — so this is a plain DTO, never an EF entity.
/// </summary>
public sealed record OperationsFinding
{
    /// <summary>Stable synthetic key, e.g. "cert:{secretId}" — lets the UI de-dupe/track a row.</summary>
    public required string Id { get; init; }
    public required AdvisorCategory Category { get; init; }
    public required AdvisorSeverity Severity { get; init; }
    public required AdvisorHorizon Horizon { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }

    /// <summary>Human-readable scope, e.g. "Acme / Production" or "Cluster: prod-eu-west-1".</summary>
    public string ScopeLabel { get; init; } = "";

    /// <summary>Pre-rendered timing phrase, e.g. "in 5 d", "expired 3 d ago", "firing for 6 h".</summary>
    public string? TimingText { get; init; }

    /// <summary>The deterministic deadline, when there is one. Drives sort order within a horizon.</summary>
    public DateTime? DueAt { get; init; }

    /// <summary>1.0 = deterministic fact (expiry date, incident state). &lt;1.0 = forecast (future).</summary>
    public double Confidence { get; init; } = 1.0;

    public string? Remediation { get; init; }

    /// <summary>Direct runbook URL to act on this finding, when the source supplies one.</summary>
    public string? RunbookUrl { get; init; }

    /// <summary>Tenant-explorer section key to deep-link to, e.g. "secret-expiry", "incidents".</summary>
    public string? LinkSection { get; init; }

    /// <summary>Owning customer, when the finding is scoped to one of the tenant's customers.
    /// Null = tenant-infrastructure finding (surfaced to ops only).</summary>
    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }

    public Guid? ClusterId { get; init; }

    /// <summary>For an incident finding: the representative (lead) incident to open directly,
    /// so a deep link can jump straight to its detail instead of the incidents list.</summary>
    public Guid? IncidentId { get; init; }

    /// <summary>Which signal produced this — "certificate" | "incident" | "backup".</summary>
    public string Source { get; init; } = "";

    // ── Lifecycle state (merged from AdvisorFindingState; defaults when untouched) ──
    public AdvisorFindingStatus State { get; init; } = AdvisorFindingStatus.Active;
    public DateTime? SnoozedUntil { get; init; }
    public string? AcknowledgedBy { get; init; }
    public string? AssignedTo { get; init; }
    public DateTime? FirstSeenAt { get; init; }

    /// <summary>Whole days since the finding was first observed (0 when unknown/new).</summary>
    public int AgeDays { get; init; }

    /// <summary>Overdue, aged past the escalation threshold, and not yet acknowledged.</summary>
    public bool IsEscalated { get; init; }

    /// <summary>First surfaced within the last day (or not yet recorded) — worth a "new" flag.</summary>
    public bool IsNew { get; init; }

    /// <summary>Composite urgency score used to rank findings within a horizon (higher = more urgent).</summary>
    public double PriorityScore { get; init; }
}

/// <summary>
/// The synthesized advisor report for a tenant: every finding, sorted
/// most-urgent-first, plus small helpers the UI uses for its summary strip.
/// </summary>
public sealed record AdvisorReport
{
    public required IReadOnlyList<OperationsFinding> Findings { get; init; }
    public required DateTime GeneratedAt { get; init; }

    public IEnumerable<OperationsFinding> InHorizon(AdvisorHorizon h) => Findings.Where(f => f.Horizon == h);
    public int CountHorizon(AdvisorHorizon h) => Findings.Count(f => f.Horizon == h);
    public int CountSeverity(AdvisorSeverity s) => Findings.Count(f => f.Severity == s);

    /// <summary>Distinct customers that own at least one finding — the customer-lens picker.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Customers =>
        Findings.Where(f => f.CustomerId is not null)
                .Select(f => (f.CustomerId!.Value, f.CustomerName ?? "—"))
                .Distinct()
                .OrderBy(c => c.Item2)
                .ToList();
}

/// <summary>
/// Synthesizes an operator/customer-facing "what needs doing" feed from signals
/// that already exist across the platform. Read-only and deterministic: every
/// finding here is a hard fact (an expiry date, an open incident, a missing
/// backup), so confidence is 1.0. Forecast-based findings (saturation trends,
/// error-budget burn) are a later addition and would carry lower confidence.
/// </summary>
public class OperationsAdvisorService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SecretExpiryService secretExpiry,
    IncidentCorrelationService correlation,
    ErrorBudgetService errorBudget,
    AdvisorStateService stateService,
    EntKube.Web.Services.Upgrades.ComponentUpgradeService componentUpgrades,
    EntKube.Web.Services.Upgrades.DriftScanCache driftCache,
    EntKube.Web.Services.SupplyChain.SupplyChainScanCache supplyChainCache,
    EntKube.Web.Services.Dr.DrScanCache drCache,
    ILogger<OperationsAdvisorService> logger)
{
    /// <summary>Overdue findings older than this, still unacknowledged, are escalated.</summary>
    private static readonly TimeSpan EscalateAfter = TimeSpan.FromDays(3);

    public async Task<AdvisorReport> GetReportAsync(Guid tenantId, CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        var findings = new List<OperationsFinding>();

        findings.AddRange(await BuildSecretFindingsAsync(tenantId, now, ct));
        findings.AddRange(await BuildIncidentFindingsAsync(tenantId, now, ct));
        findings.AddRange(await BuildSloFindingsAsync(tenantId, now, ct));
        findings.AddRange(await BuildBackupFindingsAsync(tenantId, now, ct));
        findings.AddRange(await BuildPostureFindingsAsync(tenantId, ct));
        findings.AddRange(await BuildCapacityFindingsAsync(tenantId, now, ct));
        findings.AddRange(await BuildBlueprintFindingsAsync(tenantId, now, ct));
        findings.AddRange(await BuildUpgradeFindingsAsync(tenantId, now, ct));
        findings.AddRange(BuildDriftFindings(tenantId, now));
        findings.AddRange(BuildSupplyChainFindings(tenantId, now));
        findings.AddRange(BuildDrFindings(tenantId, now));

        var merged = await MergeStateAsync(tenantId, findings, now, ct);

        // Grouped by horizon in the UI; within a horizon, rank by composite priority.
        var ordered = merged
            .OrderBy(f => f.Horizon)
            .ThenByDescending(f => f.PriorityScore)
            .ThenBy(f => f.DueAt ?? DateTime.MaxValue)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AdvisorReport { Findings = ordered, GeneratedAt = now };
    }

    /// <summary>
    /// Attaches persisted lifecycle state (ack/snooze/dismiss/assign), computes aging/escalation,
    /// a "new" flag, and the composite priority score used for ranking.
    /// </summary>
    private async Task<List<OperationsFinding>> MergeStateAsync(
        Guid tenantId, List<OperationsFinding> findings, DateTime now, CancellationToken ct)
    {
        Dictionary<string, AdvisorFindingState> states = await stateService.GetStatesAsync(tenantId, ct);

        var result = new List<OperationsFinding>(findings.Count);
        foreach (OperationsFinding f in findings)
        {
            if (!states.TryGetValue(f.Id, out AdvisorFindingState? s))
            {
                // No state row yet — treat as freshly surfaced.
                result.Add(f with { IsNew = true, PriorityScore = Score(f, escalated: false) });
                continue;
            }

            int age = Math.Max(0, (int)(now - s.FirstSeenAt).TotalDays);
            bool escalated = f.Horizon == AdvisorHorizon.Overdue
                             && (now - s.FirstSeenAt) >= EscalateAfter
                             && s.Status is not AdvisorFindingStatus.Acknowledged and not AdvisorFindingStatus.Dismissed;

            result.Add(f with
            {
                State = s.Status,
                SnoozedUntil = s.SnoozedUntil,
                AcknowledgedBy = s.AcknowledgedBy,
                AssignedTo = s.AssignedTo,
                FirstSeenAt = s.FirstSeenAt,
                AgeDays = age,
                IsEscalated = escalated,
                IsNew = (now - s.FirstSeenAt) < TimeSpan.FromDays(1),
                PriorityScore = Score(f, escalated),
            });
        }
        return result;
    }

    /// <summary>Composite urgency: severity + horizon + escalation, scaled down for low-confidence forecasts.</summary>
    private static double Score(OperationsFinding f, bool escalated)
    {
        double s = f.Severity switch
        {
            AdvisorSeverity.Critical => 100,
            AdvisorSeverity.Warning => 50,
            _ => 10,
        };
        s += f.Horizon switch
        {
            AdvisorHorizon.Overdue => 40,
            AdvisorHorizon.Today => 30,
            AdvisorHorizon.ThisWeek => 12,
            _ => 0,
        };
        if (escalated) s += 25;
        return s * (f.Confidence <= 0 ? 1.0 : f.Confidence);
    }

    // ── Security: certificates, kubeconfigs, OAuth client credentials ──
    private async Task<List<OperationsFinding>> BuildSecretFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();

        List<ExpiringSecretInfo> secrets = await secretExpiry.GetExpiringSecretsAsync(tenantId, ct: ct);
        if (secrets.Count == 0) return result;

        Dictionary<Guid, (Guid CustomerId, string CustomerName)> appOwner = await LoadAppOwnersAsync(tenantId, ct);

        foreach (ExpiringSecretInfo s in secrets)
        {
            (Guid Id, string Name)? owner = s.AppId is not null && appOwner.TryGetValue(s.AppId.Value, out var o)
                ? (o.CustomerId, o.CustomerName)
                : null;

            // OAuth client with no expiry recorded — can't be tracked; nudge to set one.
            if (s.SecretType == VaultSecretType.OAuthClient && !s.HasExpiry)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"secret-noexpiry:{s.SecretId}",
                    Category = AdvisorCategory.Security,
                    Severity = AdvisorSeverity.Info,
                    Horizon = AdvisorHorizon.Later,
                    Title = $"OAuth client “{s.Name}” has no expiry set",
                    Detail = "Expiry is entered manually for OAuth/OIDC clients; without it the credential can lapse unnoticed.",
                    ScopeLabel = s.ScopeLabel,
                    TimingText = "no deadline",
                    Remediation = "Record the credential's expiry in Vault so it can be tracked.",
                    LinkSection = "secret-expiry",
                    CustomerId = owner?.Id,
                    CustomerName = owner?.Name,
                    Source = "certificate",
                });
                continue;
            }

            if (!s.HasExpiry) continue;

            int days = s.DaysUntilExpiry ?? int.MaxValue;
            // Not yet actionable — ignore anything comfortably far out.
            if (!s.IsExpired && days > 60) continue;

            AdvisorHorizon horizon = HorizonFor(s.ExpiresAt, now, alreadyBreached: s.IsExpired);
            AdvisorSeverity severity = s.IsExpired
                ? AdvisorSeverity.Critical
                : days <= 14 ? AdvisorSeverity.Warning : AdvisorSeverity.Info;

            string what = s.IsExpired ? "has expired" : $"expires {DueText(s.ExpiresAt, now)}";

            result.Add(new OperationsFinding
            {
                Id = $"secret:{s.SecretId}",
                Category = AdvisorCategory.Security,
                Severity = severity,
                Horizon = horizon,
                Title = $"{s.TypeLabel} “{s.Name}” {what}",
                Detail = s.Detail,
                ScopeLabel = s.ScopeLabel,
                TimingText = DueText(s.ExpiresAt, now),
                DueAt = s.ExpiresAt,
                Remediation = s.SecretType == VaultSecretType.Kubeconfig
                    ? "Rotate the cluster credential and re-upload the kubeconfig, or cluster access will break."
                    : "Rotate the credential and re-upload it in Vault before it lapses.",
                LinkSection = "secret-expiry",
                CustomerId = owner?.Id,
                CustomerName = owner?.Name,
                Source = "certificate",
            });
        }

        return result;
    }

    // ── Reliability: firing / unresolved incidents, correlated by root cause ──
    // The same alert firing across many pods collapses into ONE finding with a
    // count, so a node failure or bad deploy doesn't bury the feed in duplicates.
    private async Task<List<OperationsFinding>> BuildIncidentFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();

        List<IncidentGroup> groups = await correlation.GetActiveGroupsForTenantAsync(tenantId, ct);
        foreach (IncidentGroup g in groups)
        {
            double ageHours = (now - g.EarliestStartsAt).TotalHours;
            AdvisorSeverity severity = g.Severity?.ToLowerInvariant() switch
            {
                "critical" => AdvisorSeverity.Critical,
                "warning" => AdvisorSeverity.Warning,
                _ => AdvisorSeverity.Info,
            };

            // Acute now; but anything unresolved for over a day is past due.
            AdvisorHorizon horizon = ageHours > 24 ? AdvisorHorizon.Overdue : AdvisorHorizon.Today;

            bool storm = g.Count > 1;
            string title = storm
                ? $"{g.Summary} — {g.Count} firing"
                : g.Summary;

            string nsBlurb = g.Namespaces.Count switch
            {
                0 => "",
                1 => $" · namespace {g.Namespaces[0]}",
                _ => $" · {g.Namespaces.Count} namespaces",
            };
            string acked = g.AnyAcknowledged ? " · acknowledged" : "";

            result.Add(new OperationsFinding
            {
                Id = $"incident-group:{g.Key}",
                Category = AdvisorCategory.Reliability,
                Severity = severity,
                Horizon = horizon,
                Title = title,
                Detail = storm
                    ? $"{g.Count} incidents of “{g.AlertName}” are firing together — likely one root cause."
                    : g.AlertName,
                ScopeLabel = $"Cluster: {g.ClusterName}{nsBlurb}{acked}",
                TimingText = $"firing for {HumanSpan(TimeSpan.FromHours(ageHours))}",
                DueAt = g.EarliestStartsAt,
                Remediation = string.IsNullOrWhiteSpace(g.RunbookUrl)
                    ? "Triage the root cause, then acknowledge or resolve the group."
                    : "Follow the runbook, then acknowledge or resolve the group.",
                RunbookUrl = string.IsNullOrWhiteSpace(g.RunbookUrl) ? null : g.RunbookUrl,
                LinkSection = "incidents",
                ClusterId = g.ClusterId,
                IncidentId = g.LeadIncidentId,
                Source = "incident",
            });
        }

        return result;
    }

    // ── Reliability: SLO error budgets — breached, forecast-to-breach, or at risk ──
    private async Task<List<OperationsFinding>> BuildSloFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();

        List<ErrorBudgetStatus> budgets = await errorBudget.GetTenantErrorBudgetsAsync(tenantId, ct);
        foreach (ErrorBudgetStatus b in budgets)
        {
            if (!b.HasData || b.BudgetConsumedFraction is not double consumed) continue;

            string svc = $"{b.AppName} / {b.DeploymentName}";
            string scope = $"{svc} · target {b.TargetPercent:0.##}% over {b.WindowDays}d";
            string achieved = b.AchievedPercent is double a ? $"{a:0.##}%" : "n/a";

            // Already breached — the objective is missed for this window.
            if (consumed >= 1.0)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"slo-breach:{b.DeploymentId}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Critical,
                    Horizon = AdvisorHorizon.Overdue,
                    Title = $"SLO breached for {svc}",
                    Detail = $"Availability {achieved} is below the {b.TargetPercent:0.##}% objective — the error budget is spent.",
                    ScopeLabel = scope,
                    TimingText = "budget exhausted",
                    Remediation = "Freeze risky changes and stabilize; review recent incidents for the cause.",
                    LinkSection = "sla",
                    CustomerId = b.CustomerId,
                    CustomerName = b.CustomerName,
                    ClusterId = b.ClusterId,
                    Source = "slo",
                });
                continue;
            }

            // Forecast: burning fast enough to run out soon (a modelled estimate, not a fact).
            if (b.HoursToExhaustion is double hrs && hrs <= 24 * 7)
            {
                DateTime due = now.AddHours(hrs);
                result.Add(new OperationsFinding
                {
                    Id = $"slo-burn:{b.DeploymentId}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Warning,
                    Horizon = hrs <= 48 ? AdvisorHorizon.Today : AdvisorHorizon.ThisWeek,
                    Title = $"SLO for {svc} is burning fast",
                    Detail = $"At the last-day burn rate ({b.BurnRateRecent:0.#}×), the remaining error budget runs out {DueText(due, now)}.",
                    ScopeLabel = scope,
                    TimingText = $"budget out {DueText(due, now)}",
                    DueAt = due,
                    Confidence = 0.6,
                    Remediation = "Slow the burn: pause deploys, scale up, or fix the failing component now.",
                    LinkSection = "sla",
                    CustomerId = b.CustomerId,
                    CustomerName = b.CustomerName,
                    ClusterId = b.ClusterId,
                    Source = "slo",
                });
                continue;
            }

            // At risk: most of the budget is already gone, but not actively burning out.
            if (consumed >= 0.8)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"slo-risk:{b.DeploymentId}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Warning,
                    Horizon = AdvisorHorizon.ThisWeek,
                    Title = $"SLO error budget low for {svc}",
                    Detail = $"{consumed * 100:0}% of the error budget is spent (availability {achieved}).",
                    ScopeLabel = scope,
                    TimingText = $"{(1 - consumed) * 100:0}% budget left",
                    Confidence = 0.9,
                    Remediation = "Protect the remaining budget — hold non-essential changes this window.",
                    LinkSection = "sla",
                    CustomerId = b.CustomerId,
                    CustomerName = b.CustomerName,
                    ClusterId = b.ClusterId,
                    Source = "slo",
                });
            }
        }

        return result;
    }

    // ── Data protection: datastore backups missing, failed, or stale ──
    // Staleness assumes a roughly-daily cadence (the common case). CNPG, Mongo, RabbitMQ
    // and scheduled Keycloak realms share the scheduled-backup shape via EvaluateScheduledBackup;
    // an unscheduled (on-demand) Keycloak realm only surfaces a failed export.
    private async Task<List<OperationsFinding>> BuildBackupFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Postgres (CloudNativePG)
        foreach (CnpgCluster c in await db.CnpgClusters
            .Where(c => c.TenantId == tenantId && c.Status == CnpgClusterStatus.Running)
            .Include(c => c.Backups).ToListAsync(ct))
        {
            List<CnpgBackup> done = c.Backups.Where(b => b.Status == CnpgBackupStatus.Completed).ToList();
            CnpgBackup? newest = c.Backups.OrderByDescending(b => b.StartedAt).FirstOrDefault();
            EvaluateScheduledBackup(result, now, "Postgres", "cnpg", c.Id, c.Name, c.KubernetesClusterId, "databases",
                !string.IsNullOrWhiteSpace(c.BackupSchedule), c.StorageLinkId is not null,
                done.Count > 0 ? done.Max(b => b.CompletedAt ?? b.StartedAt) : null,
                newest?.Status == CnpgBackupStatus.Failed ? newest.StartedAt : null,
                newest?.Status == CnpgBackupStatus.Failed ? newest.LastError : null);
        }

        // MongoDB (Percona)
        foreach (MongoCluster c in await db.MongoClusters
            .Where(c => c.TenantId == tenantId && c.Status == MongoClusterStatus.Running)
            .Include(c => c.Backups).ToListAsync(ct))
        {
            List<MongoBackup> done = c.Backups.Where(b => b.Status == MongoBackupStatus.Completed).ToList();
            MongoBackup? newest = c.Backups.OrderByDescending(b => b.StartedAt).FirstOrDefault();
            EvaluateScheduledBackup(result, now, "MongoDB", "mongo", c.Id, c.Name, c.KubernetesClusterId, "databases",
                !string.IsNullOrWhiteSpace(c.BackupSchedule), c.StorageLinkId is not null,
                done.Count > 0 ? done.Max(b => b.CompletedAt ?? b.StartedAt) : null,
                newest?.Status == MongoBackupStatus.Failed ? newest.StartedAt : null,
                null);
        }

        // RabbitMQ (definitions export)
        foreach (RabbitMQCluster c in await db.RabbitMQClusters
            .Where(c => c.TenantId == tenantId && c.Status == RabbitMQClusterStatus.Running)
            .Include(c => c.Backups).ToListAsync(ct))
        {
            List<RabbitMQBackup> done = c.Backups.Where(b => b.Status == RabbitMQBackupStatus.Ready).ToList();
            RabbitMQBackup? newest = c.Backups.OrderByDescending(b => b.CreatedAt).FirstOrDefault();
            EvaluateScheduledBackup(result, now, "RabbitMQ", "rabbit", c.Id, c.Name, c.KubernetesClusterId, "databases",
                !string.IsNullOrWhiteSpace(c.BackupSchedule), c.StorageLinkId is not null,
                done.Count > 0 ? done.Max(b => b.CompletedAt ?? b.CreatedAt) : null,
                newest?.Status == RabbitMQBackupStatus.Failed ? newest.CreatedAt : null,
                newest?.Status == RabbitMQBackupStatus.Failed ? newest.LastError : null);
        }

        // Keycloak realms — scheduled realms get full never/stale/failed evaluation (the
        // in-app scheduler runs their cron); on-demand realms only flag a failed export.
        foreach (KeycloakRealm r in await db.KeycloakRealms
            .Where(r => r.TenantId == tenantId)
            .Include(r => r.Backups).ToListAsync(ct))
        {
            List<KeycloakBackup> done = r.Backups.Where(b => b.Status == KeycloakBackupStatus.Ready).ToList();
            KeycloakBackup? newest = r.Backups.OrderByDescending(b => b.CreatedAt).FirstOrDefault();
            DateTime? lastSuccess = done.Count > 0 ? done.Max(b => b.CompletedAt ?? b.CreatedAt) : null;

            if (!string.IsNullOrWhiteSpace(r.BackupSchedule))
            {
                EvaluateScheduledBackup(result, now, "Keycloak realm", "keycloak", r.Id, r.RealmName, null, "identity",
                    scheduleConfigured: true, hasDestination: r.StorageLinkId is not null,
                    lastSuccess,
                    newest?.Status == KeycloakBackupStatus.Failed ? newest.CreatedAt : null,
                    newest?.Status == KeycloakBackupStatus.Failed ? newest.LastError : null);
                continue;
            }

            // On-demand realm: surface only a failed latest export.
            if (newest?.Status == KeycloakBackupStatus.Failed)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"backup-failed:keycloak:{r.Id}",
                    Category = AdvisorCategory.DataProtection,
                    Severity = AdvisorSeverity.Warning,
                    Horizon = AdvisorHorizon.Today,
                    Title = $"Latest Keycloak backup for realm “{r.RealmName}” failed",
                    Detail = string.IsNullOrWhiteSpace(newest.LastError) ? "The most recent realm export failed." : newest.LastError,
                    ScopeLabel = $"Keycloak realm: {r.RealmName}",
                    TimingText = $"failed {DueText(newest.CreatedAt, now)}",
                    DueAt = newest.CreatedAt,
                    Remediation = "Re-run the realm export and check object-storage connectivity.",
                    LinkSection = "identity",
                    Source = "backup",
                });
            }
        }

        return result;
    }

    // Shared evaluation for the scheduled-backup datastores (CNPG / Mongo / RabbitMQ):
    // unconfigured → never-completed → latest-failed → stale, escalating by age.
    private void EvaluateScheduledBackup(
        List<OperationsFinding> result, DateTime now,
        string kind, string idKey, Guid entityId, string name, Guid? clusterId, string linkSection,
        bool scheduleConfigured, bool hasDestination,
        DateTime? lastSuccessAt, DateTime? latestFailedAt, string? latestFailError)
    {
        string scope = $"{kind}: {name}";

        if (!scheduleConfigured)
        {
            result.Add(new OperationsFinding
            {
                Id = $"backup-unconfigured:{idKey}:{entityId}",
                Category = AdvisorCategory.DataProtection,
                Severity = AdvisorSeverity.Info,
                Horizon = AdvisorHorizon.Later,
                Title = hasDestination
                    ? $"{kind} “{name}” has no backup schedule"
                    : $"{kind} “{name}” has no backup destination",
                Detail = hasDestination
                    ? "Backups can only be taken on demand; there is no automated protection."
                    : "No S3 storage is linked, so no automated backups are possible.",
                ScopeLabel = scope,
                TimingText = "no deadline",
                Remediation = hasDestination
                    ? "Configure a cron backup schedule for automated protection."
                    : "Link an S3 bucket, then set a backup schedule.",
                LinkSection = linkSection,
                ClusterId = clusterId,
                Source = "backup",
            });
            return;
        }

        if (lastSuccessAt is null)
        {
            result.Add(new OperationsFinding
            {
                Id = $"backup-never:{idKey}:{entityId}",
                Category = AdvisorCategory.DataProtection,
                Severity = AdvisorSeverity.Critical,
                Horizon = AdvisorHorizon.Overdue,
                Title = $"{kind} “{name}” has never completed a backup",
                Detail = "A backup schedule is configured but no backup has ever succeeded.",
                ScopeLabel = scope,
                TimingText = "overdue",
                Remediation = "Trigger a backup now and verify the schedule is actually running.",
                LinkSection = linkSection,
                ClusterId = clusterId,
                Source = "backup",
            });
            return;
        }

        // Regression: the newest attempt failed after the last good backup.
        if (latestFailedAt is DateTime failedAt && failedAt > lastSuccessAt.Value)
        {
            result.Add(new OperationsFinding
            {
                Id = $"backup-failed:{idKey}:{entityId}",
                Category = AdvisorCategory.DataProtection,
                Severity = AdvisorSeverity.Warning,
                Horizon = AdvisorHorizon.Today,
                Title = $"Latest {kind} backup for “{name}” failed",
                Detail = string.IsNullOrWhiteSpace(latestFailError) ? "The most recent backup attempt failed." : latestFailError,
                ScopeLabel = scope,
                TimingText = $"failed {DueText(failedAt, now)}",
                DueAt = failedAt,
                Remediation = "Investigate the failure; the last good backup is ageing in the meantime.",
                LinkSection = linkSection,
                ClusterId = clusterId,
                Source = "backup",
            });
        }

        TimeSpan age = now - lastSuccessAt.Value;
        if (age <= TimeSpan.FromDays(2)) return;

        (AdvisorSeverity sev, AdvisorHorizon horizon) = age > TimeSpan.FromDays(7)
            ? (AdvisorSeverity.Critical, AdvisorHorizon.Overdue)
            : (AdvisorSeverity.Warning, AdvisorHorizon.Today);

        result.Add(new OperationsFinding
        {
            Id = $"backup-stale:{idKey}:{entityId}",
            Category = AdvisorCategory.DataProtection,
            Severity = sev,
            Horizon = horizon,
            Title = $"{kind} “{name}” backup is {HumanSpan(age)} old",
            Detail = "The last successful backup is older than the expected daily cadence.",
            ScopeLabel = scope,
            TimingText = $"last backup {DueText(lastSuccessAt.Value, now)}",
            DueAt = lastSuccessAt.Value,
            Remediation = "Check the backup schedule and object storage; run a fresh backup.",
            LinkSection = linkSection,
            ClusterId = clusterId,
            Source = "backup",
        });
    }

    // ── Posture: deployment drift + best-practice gaps (all DB-cheap; no live cluster calls) ──
    // k8s-version EOL and PVC/memory-trend finders are intentionally omitted here — they need
    // live Prometheus/external-EOL data unsuitable for compute-on-read; a cached collector is TODO.
    private async Task<List<OperationsFinding>> BuildPostureFindingsAsync(Guid tenantId, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        var deps = await db.AppDeployments
            .Where(d => d.App.Customer.TenantId == tenantId && d.IsManaged)
            .Select(d => new
            {
                d.Id, d.Name, d.AppId, AppName = d.App.Name,
                CustomerId = d.App.CustomerId, CustomerName = d.App.Customer.Name,
                d.EnvironmentId, EnvName = d.Environment.Name, d.ClusterId,
                d.SyncStatus, d.HealthStatus, d.StatusMessage,
                Exposes = d.App.ServicePorts.Any(),
            })
            .ToListAsync(ct);

        if (deps.Count == 0) return result;

        // ── Reliability: deployments whose live state has drifted or degraded ──
        foreach (var d in deps)
        {
            bool degraded = d.HealthStatus is HealthStatus.Degraded or HealthStatus.Missing;
            bool syncFailed = d.SyncStatus == SyncStatus.Failed;
            bool outOfSync = d.SyncStatus == SyncStatus.OutOfSync;
            if (!degraded && !syncFailed && !outOfSync) continue;

            string scope = $"{d.AppName} / {d.EnvName}";
            if (degraded || syncFailed)
            {
                string what = syncFailed ? "failed to sync"
                    : d.HealthStatus == HealthStatus.Missing ? "has missing resources" : "is degraded";
                result.Add(new OperationsFinding
                {
                    Id = $"deploy:{d.Id}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Warning,
                    Horizon = AdvisorHorizon.Today,
                    Title = $"Deployment “{d.Name}” {what}",
                    Detail = string.IsNullOrWhiteSpace(d.StatusMessage)
                        ? "Live state does not match the desired/healthy state." : d.StatusMessage,
                    ScopeLabel = scope,
                    TimingText = "unhealthy now",
                    Remediation = "Check the workload's pods and events, then re-sync or roll back.",
                    CustomerId = d.CustomerId, CustomerName = d.CustomerName, ClusterId = d.ClusterId,
                    Source = "deployment",
                });
            }
            else // out of sync but healthy — drift
            {
                result.Add(new OperationsFinding
                {
                    Id = $"deploy-drift:{d.Id}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Info,
                    Horizon = AdvisorHorizon.ThisWeek,
                    Title = $"Deployment “{d.Name}” has drifted from its declared spec",
                    Detail = string.IsNullOrWhiteSpace(d.StatusMessage)
                        ? "Live state is out of sync with the declared spec." : d.StatusMessage,
                    ScopeLabel = scope,
                    TimingText = "out of sync",
                    Remediation = "Review the diff and sync, or adopt the live changes.",
                    CustomerId = d.CustomerId, CustomerName = d.CustomerName, ClusterId = d.ClusterId,
                    Source = "deployment",
                });
            }
        }

        // ── Security: exposed apps with no NetworkPolicy baseline ──
        HashSet<(Guid, Guid)> netpolKeys = (await db.AppNetworkPolicies
                .Where(p => p.App.Customer.TenantId == tenantId)
                .Select(p => new { p.AppId, p.EnvironmentId }).ToListAsync(ct))
            .Select(x => (x.AppId, x.EnvironmentId)).ToHashSet();

        var seenNetpol = new HashSet<(Guid, Guid)>();
        foreach (var d in deps.Where(x => x.Exposes))
        {
            var key = (d.AppId, d.EnvironmentId);
            if (!seenNetpol.Add(key) || netpolKeys.Contains(key)) continue;
            result.Add(new OperationsFinding
            {
                Id = $"netpol:{d.AppId}:{d.EnvironmentId}",
                Category = AdvisorCategory.Security,
                Severity = AdvisorSeverity.Info,
                Horizon = AdvisorHorizon.Later,
                Title = $"No network policy for “{d.AppName}” in {d.EnvName}",
                Detail = "This app exposes ports but has no NetworkPolicy baseline — pod traffic is unrestricted.",
                ScopeLabel = $"{d.AppName} / {d.EnvName}",
                TimingText = "no deadline",
                Remediation = "Define a least-privilege NetworkPolicy for this app and environment.",
                LinkSection = "networking",
                CustomerId = d.CustomerId, CustomerName = d.CustomerName, ClusterId = d.ClusterId,
                Source = "posture",
            });
        }

        // ── Reliability: KEDA installed on the cluster but the workload has no autoscaler ──
        HashSet<Guid> kedaClusters = (await db.ClusterComponents
                .Where(c => c.Status == ComponentStatus.Installed
                         && (c.Name == "keda" || c.HelmChartName == "keda" || c.ReleaseName == "keda"))
                .Select(c => c.ClusterId).ToListAsync(ct))
            .ToHashSet();
        if (kedaClusters.Count > 0)
        {
            HashSet<(Guid, Guid)> scalerKeys = (await db.KedaScalers
                    .Where(s => s.TenantId == tenantId)
                    .Select(s => new { s.AppId, s.EnvironmentId }).ToListAsync(ct))
                .Select(x => (x.AppId, x.EnvironmentId)).ToHashSet();

            var seenKeda = new HashSet<(Guid, Guid)>();
            foreach (var d in deps.Where(x => kedaClusters.Contains(x.ClusterId)))
            {
                var key = (d.AppId, d.EnvironmentId);
                if (!seenKeda.Add(key) || scalerKeys.Contains(key)) continue;
                result.Add(new OperationsFinding
                {
                    Id = $"noautoscaler:{d.AppId}:{d.EnvironmentId}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Info,
                    Horizon = AdvisorHorizon.Later,
                    Title = $"No autoscaler for “{d.AppName}” in {d.EnvName}",
                    Detail = "KEDA is installed on this cluster but this workload has no ScaledObject — it can't scale to load.",
                    ScopeLabel = $"{d.AppName} / {d.EnvName}",
                    TimingText = "no deadline",
                    Remediation = "Add a KEDA scaler, or confirm fixed replicas are intended.",
                    CustomerId = d.CustomerId, CustomerName = d.CustomerName, ClusterId = d.ClusterId,
                    Source = "posture",
                });
            }
        }

        // ── Security: Kyverno installed for an environment but no policies defined ──
        HashSet<Guid> kyvernoEnvs = (await db.KubernetesClusters
                .Where(cl => cl.TenantId == tenantId && db.ClusterComponents.Any(c =>
                    c.ClusterId == cl.Id && c.Status == ComponentStatus.Installed
                    && (c.Name == "kyverno" || c.HelmChartName == "kyverno" || c.ReleaseName == "kyverno")))
                .Select(cl => cl.EnvironmentId).ToListAsync(ct))
            .ToHashSet();
        if (kyvernoEnvs.Count > 0)
        {
            HashSet<Guid> policyEnvs = (await db.KyvernoPolicies
                    .Where(p => p.TenantId == tenantId)
                    .Select(p => p.EnvironmentId).ToListAsync(ct))
                .ToHashSet();
            Dictionary<Guid, string> envNames = await db.Environments
                .Where(e => e.TenantId == tenantId)
                .ToDictionaryAsync(e => e.Id, e => e.Name, ct);

            foreach (Guid envId in kyvernoEnvs)
            {
                if (policyEnvs.Contains(envId)) continue;
                string envName = envNames.GetValueOrDefault(envId, "—");
                result.Add(new OperationsFinding
                {
                    Id = $"nokyverno:{envId}",
                    Category = AdvisorCategory.Security,
                    Severity = AdvisorSeverity.Info,
                    Horizon = AdvisorHorizon.Later,
                    Title = $"Kyverno installed in {envName} but no policies defined",
                    Detail = "The admission controller is running but enforcing nothing — no guardrails are active.",
                    ScopeLabel = $"Environment: {envName}",
                    TimingText = "no deadline",
                    Remediation = "Define baseline Kyverno policies (or uninstall it if unused).",
                    Source = "posture",
                });
            }
        }

        return result;
    }

    // ── Capacity: resources trending toward their ceiling (forecast, confidence < 1) ──
    // Reads cached ResourceUsageSnapshot samples (written by ResourceUsageCollectorService)
    // and linearly projects time-to-full. Never queries Prometheus on the read path.
    private async Task<List<OperationsFinding>> BuildCapacityFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<Guid> clusterIds = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId).Select(c => c.Id).ToListAsync(ct);
        if (clusterIds.Count == 0) return result;

        DateTime since = now.AddDays(-7);
        var samples = await db.ResourceUsageSnapshots
            .Where(s => clusterIds.Contains(s.ClusterId) && s.SnapshotAt >= since)
            .Select(s => new { s.ClusterId, s.Kind, s.Namespace, s.Name, s.Fraction, s.SnapshotAt })
            .ToListAsync(ct);
        if (samples.Count == 0) return result;

        foreach (var g in samples.GroupBy(s => (s.ClusterId, s.Kind, s.Namespace, s.Name)))
        {
            var series = g.OrderBy(s => s.SnapshotAt).ToList();
            double current = series[^1].Fraction;

            // Ignore comfortably-provisioned resources.
            if (current < 0.70) continue;

            // Linear trend over the window (fraction per hour).
            double slopePerHour = 0;
            if (series.Count >= 2)
            {
                double hours = (series[^1].SnapshotAt - series[0].SnapshotAt).TotalHours;
                if (hours >= 1) slopePerHour = (series[^1].Fraction - series[0].Fraction) / hours;
            }

            (AdvisorSeverity sev, AdvisorHorizon horizon, string timing, double conf)? verdict = null;

            if (current >= 0.95)
            {
                verdict = (AdvisorSeverity.Critical, AdvisorHorizon.Overdue, "at capacity now", 0.9);
            }
            else if (slopePerHour > 0)
            {
                double hoursToFull = (1.0 - current) / slopePerHour;
                if (hoursToFull <= 48)
                    verdict = (AdvisorSeverity.Warning, AdvisorHorizon.Today, $"full in ~{FmtHours(hoursToFull)}", 0.6);
                else if (hoursToFull <= 24 * 7)
                    verdict = (AdvisorSeverity.Info, AdvisorHorizon.ThisWeek, $"full in ~{FmtHours(hoursToFull)}", 0.55);
                else if (current >= 0.90)
                    verdict = (AdvisorSeverity.Warning, AdvisorHorizon.ThisWeek, $"{current * 100:0}% used", 0.7);
            }
            else if (current >= 0.90)
            {
                // High and steady — not imminent, but worth right-sizing.
                verdict = (AdvisorSeverity.Info, AdvisorHorizon.ThisWeek, $"{current * 100:0}% used, steady", 0.7);
            }

            if (verdict is not (AdvisorSeverity sv, AdvisorHorizon hz, string tt, double cf)) continue;

            bool pvc = g.Key.Kind == ResourceUsageKind.PvcFill;
            string what = pvc
                ? $"PVC “{g.Key.Name}” is {current * 100:0}% full"
                : $"Pod “{g.Key.Name}” memory is {current * 100:0}% of its limit";

            result.Add(new OperationsFinding
            {
                Id = $"capacity:{(pvc ? "pvc" : "mem")}:{g.Key.ClusterId}:{g.Key.Namespace}:{g.Key.Name}",
                Category = AdvisorCategory.Capacity,
                Severity = sv,
                Horizon = hz,
                Title = what,
                Detail = pvc
                    ? "The volume is filling up; a full PVC stalls writes and can crash the workload."
                    : "Memory is approaching the limit; the container risks OOM-kill and restarts.",
                ScopeLabel = $"{g.Key.Namespace}",
                TimingText = tt,
                Confidence = cf,
                Remediation = pvc
                    ? "Expand the volume, clean up data, or add retention/rotation."
                    : "Raise the memory limit or reduce the workload's footprint.",
                LinkSection = "infrastructure",
                ClusterId = g.Key.ClusterId,
                Source = "capacity",
            });
        }

        return result;
    }

    // ── Reliability: cluster bootstrap (blueprint) runs that failed or stalled ──
    private async Task<List<OperationsFinding>> BuildBlueprintFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        var runs = await db.BootstrapRuns
            .Where(r => r.Cluster.TenantId == tenantId)
            .Select(r => new { r.ClusterId, ClusterName = r.Cluster.Name, r.BlueprintName, r.Status, r.CreatedAt, r.StartedAt })
            .ToListAsync(ct);
        if (runs.Count == 0) return result;

        foreach (var g in runs.GroupBy(r => r.ClusterId))
        {
            var latest = g.OrderByDescending(r => r.CreatedAt).First();

            if (latest.Status == BootstrapRunStatus.Failed)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"bootstrap-failed:{latest.ClusterId}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Warning,
                    Horizon = AdvisorHorizon.Today,
                    Title = $"Cluster bootstrap failed on “{latest.ClusterName}”",
                    Detail = $"Applying blueprint “{latest.BlueprintName}” failed and is halted — it can be resumed.",
                    ScopeLabel = $"Cluster: {latest.ClusterName}",
                    TimingText = $"failed {DueText(latest.CreatedAt, now)}",
                    DueAt = latest.CreatedAt,
                    Remediation = "Open Blueprints and resume the bootstrap, or fix the failing step.",
                    LinkSection = "blueprints",
                    ClusterId = latest.ClusterId,
                    Source = "blueprint",
                });
            }
            else if (latest.Status is BootstrapRunStatus.Queued or BootstrapRunStatus.Running
                     && now - (latest.StartedAt ?? latest.CreatedAt) > TimeSpan.FromHours(2))
            {
                result.Add(new OperationsFinding
                {
                    Id = $"bootstrap-stalled:{latest.ClusterId}",
                    Category = AdvisorCategory.Reliability,
                    Severity = AdvisorSeverity.Info,
                    Horizon = AdvisorHorizon.Today,
                    Title = $"Cluster bootstrap stalled on “{latest.ClusterName}”",
                    Detail = $"Blueprint “{latest.BlueprintName}” has been {latest.Status.ToString().ToLowerInvariant()} for over 2 hours.",
                    ScopeLabel = $"Cluster: {latest.ClusterName}",
                    TimingText = $"since {DueText(latest.StartedAt ?? latest.CreatedAt, now)}",
                    DueAt = latest.StartedAt ?? latest.CreatedAt,
                    Remediation = "Check the bootstrap runner and the step it's on.",
                    LinkSection = "blueprints",
                    ClusterId = latest.ClusterId,
                    Source = "blueprint",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Turns the fleet upgrade report into advisor findings.
    ///
    /// Deliberately aggregated per cluster rather than one finding per component: a
    /// ten-cluster fleet running thirty components each would otherwise bury every
    /// other signal in the feed under routine patch drift. Only two things earn a
    /// row of their own — a chart version the author has deprecated, and a cluster
    /// carrying major-version-behind components — because those are the ones with a
    /// real deadline behind them.
    /// </summary>
    private async Task<List<OperationsFinding>> BuildUpgradeFindingsAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        var result = new List<OperationsFinding>();

        UpgradeReport report;
        try
        {
            report = await componentUpgrades.GetTenantReportAsync(tenantId, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Chart repositories are third-party and off the critical path. Losing them must
            // never take down the whole advisor feed, which carries expiry and incident
            // findings an operator depends on.
            logger.LogWarning(ex, "Upgrade findings skipped for tenant {TenantId}", tenantId);
            return result;
        }

        foreach (ComponentUpgrade deprecated in report.Components.Where(c => c.Status == UpgradeStatus.Deprecated))
        {
            result.Add(new OperationsFinding
            {
                Id = $"chart-deprecated:{deprecated.ComponentId}",
                Category = AdvisorCategory.Maintenance,
                Severity = AdvisorSeverity.Warning,
                Horizon = AdvisorHorizon.ThisWeek,
                Title = $"{deprecated.DisplayName} runs a deprecated chart version",
                Detail = $"Chart {deprecated.ChartName} {deprecated.InstalledVersion} is marked deprecated by its "
                    + "author and will stop receiving fixes.",
                ScopeLabel = $"Cluster: {deprecated.ClusterName}",
                Remediation = "Move to a supported chart version, or replace the component.",
                LinkSection = "upgrades",
                ClusterId = deprecated.ClusterId,
                Source = "upgrade",
            });
        }

        foreach (IGrouping<Guid, ComponentUpgrade> cluster in report.Components
            .Where(c => c.Status == UpgradeStatus.UpgradeAvailable)
            .GroupBy(c => c.ClusterId))
        {
            List<ComponentUpgrade> upgrades = [.. cluster];
            string clusterName = upgrades[0].ClusterName;

            List<ComponentUpgrade> major = [.. upgrades.Where(u => u.Lag == VersionLag.Major)];
            if (major.Count > 0)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"chart-major:{cluster.Key}",
                    Category = AdvisorCategory.Maintenance,
                    Severity = AdvisorSeverity.Warning,
                    Horizon = AdvisorHorizon.ThisWeek,
                    Title = major.Count == 1
                        ? $"{major[0].DisplayName} is a major version behind on “{clusterName}”"
                        : $"{major.Count} components are a major version behind on “{clusterName}”",
                    Detail = DescribeUpgrades(major),
                    ScopeLabel = $"Cluster: {clusterName}",
                    Remediation = "Review the upstream changelogs, then upgrade from the Upgrades tab.",
                    LinkSection = "upgrades",
                    ClusterId = cluster.Key,
                    Source = "upgrade",
                });
            }

            List<ComponentUpgrade> routine = [.. upgrades.Where(u => u.Lag != VersionLag.Major)];
            if (routine.Count > 0)
            {
                result.Add(new OperationsFinding
                {
                    Id = $"chart-behind:{cluster.Key}",
                    Category = AdvisorCategory.Maintenance,
                    Severity = AdvisorSeverity.Info,
                    Horizon = AdvisorHorizon.Later,
                    Title = $"{routine.Count} component{(routine.Count == 1 ? " has" : "s have")} "
                        + $"updates available on “{clusterName}”",
                    Detail = DescribeUpgrades(routine),
                    ScopeLabel = $"Cluster: {clusterName}",
                    Remediation = "Apply the pending chart updates from the Upgrades tab.",
                    LinkSection = "upgrades",
                    ClusterId = cluster.Key,
                    Source = "upgrade",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Turns the last drift sweep into findings.
    ///
    /// Reads the cache and never triggers a sweep: a sweep forks a server-side dry-run
    /// per managed deployment, and the advisor report is built on every page render.
    /// A tenant with no completed sweep yet simply contributes nothing, rather than
    /// making the whole feed wait on kubectl.
    /// </summary>
    private List<OperationsFinding> BuildDriftFindings(Guid tenantId, DateTime now)
    {
        var result = new List<OperationsFinding>();

        DriftReport? report = driftCache.Get(tenantId);
        if (report is null)
        {
            return result;
        }

        foreach (DriftResult drifted in report.Results.Where(r => r.State == DriftState.Drifted))
        {
            result.Add(new OperationsFinding
            {
                Id = $"drift:{drifted.DeploymentId}",
                Category = AdvisorCategory.Maintenance,
                Severity = AdvisorSeverity.Warning,
                Horizon = AdvisorHorizon.ThisWeek,
                Title = $"“{drifted.AppName} / {drifted.DeploymentName}” has drifted from its manifest",
                Detail = $"{drifted.ChangedLines} changed line{(drifted.ChangedLines == 1 ? "" : "s")} between "
                    + $"the desired manifest and live state in {drifted.Namespace}. "
                    + "Someone changed this outside EntKube, and the next sync will overwrite it.",
                ScopeLabel = drifted.EnvironmentName is null
                    ? $"Cluster: {drifted.ClusterName}"
                    : $"{drifted.EnvironmentName} · {drifted.ClusterName}",
                TimingText = $"detected {DueText(report.GeneratedAt, now)}",
                Remediation = "Review the diff, then re-apply to converge — or fold the change into the manifest.",
                LinkSection = "drift",
                ClusterId = drifted.ClusterId,
                CustomerId = drifted.CustomerId,
                CustomerName = drifted.CustomerName,
                Source = "drift",
            });
        }

        // Removed APIs are grouped per cluster: they share one deadline (that cluster's next
        // control-plane upgrade), so they are one decision, not one per object.
        foreach (IGrouping<Guid, DriftResult> cluster in report.Results
            .Where(r => r.DeprecatedApis.Count > 0)
            .GroupBy(r => r.ClusterId))
        {
            List<DeprecatedApiUsage> usages = [.. cluster.SelectMany(r => r.DeprecatedApis)];
            string clusterName = cluster.First().ClusterName;
            string earliest = usages
                .Select(u => u.RemovedInMinor)
                .OrderBy(v => SemVer.Parse(v))
                .First();

            result.Add(new OperationsFinding
            {
                Id = $"deprecated-api:{cluster.Key}",
                Category = AdvisorCategory.Maintenance,
                Severity = AdvisorSeverity.Warning,
                Horizon = AdvisorHorizon.Later,
                Title = $"{usages.Count} manifest object{(usages.Count == 1 ? " uses" : "s use")} "
                    + $"a removed Kubernetes API on “{clusterName}”",
                Detail = string.Join(", ", usages
                    .Select(u => $"{u.Kind} {u.ApiVersion} → {u.ReplacedBy}")
                    .Distinct()
                    .Take(4)) + $". Earliest removal: Kubernetes {earliest}.",
                ScopeLabel = $"Cluster: {clusterName}",
                Remediation = "Update the manifests to the replacement API versions before upgrading the control plane.",
                LinkSection = "drift",
                ClusterId = cluster.Key,
                Source = "drift",
            });
        }

        return result;
    }

    /// <summary>
    /// Turns the last supply-chain sweep into findings. Reads the cache only, for the
    /// same reason as drift: the sweep walks every cluster and registry.
    ///
    /// Grouped per cluster rather than per image. A base-image CVE typically lands in
    /// every service at once, and thirty rows saying the same thing would bury the one
    /// finding that is actually distinct.
    /// </summary>
    private List<OperationsFinding> BuildSupplyChainFindings(Guid tenantId, DateTime now)
    {
        var result = new List<OperationsFinding>();

        EntKube.Web.Services.SupplyChain.SupplyChainReport? report = supplyChainCache.Get(tenantId);
        if (report is null)
        {
            return result;
        }

        foreach (var cluster in report.Images
            .Where(i => i.State == EntKube.Web.Services.SupplyChain.ImageScanState.Vulnerable)
            .GroupBy(i => i.ClusterId))
        {
            var images = cluster.ToList();
            int critical = images.Sum(i => i.CriticalCount);
            int high = images.Sum(i => i.HighCount);
            int fixable = images.Sum(i => i.FixableCount);
            string clusterName = images[0].ClusterName;

            result.Add(new OperationsFinding
            {
                Id = $"cve:{cluster.Key}",
                Category = AdvisorCategory.SupplyChain,
                // Critical CVEs in running images are a today problem; High alone can wait
                // for the week's patching window.
                Severity = critical > 0 ? AdvisorSeverity.Critical : AdvisorSeverity.Warning,
                Horizon = critical > 0 ? AdvisorHorizon.Today : AdvisorHorizon.ThisWeek,
                Title = $"{images.Count} running image{(images.Count == 1 ? "" : "s")} on “{clusterName}” "
                    + $"carry {critical} critical and {high} high vulnerabilities",
                Detail = string.Join(", ", images
                    .OrderByDescending(i => i.CriticalCount)
                    .Take(3)
                    .Select(i => $"{i.Image} ({i.CriticalCount}C/{i.HighCount}H)"))
                    + (fixable > 0 ? $". {fixable} have a fix available." : ". No fixes published yet."),
                ScopeLabel = $"Cluster: {clusterName}",
                Remediation = fixable > 0
                    ? "Rebuild against patched base images and redeploy."
                    : "Track upstream for fixes; consider mitigations in the meantime.",
                LinkSection = "supply-chain",
                ClusterId = cluster.Key,
                Source = "supply-chain",
            });
        }

        foreach (var cluster in report.Images
            .Where(i => i.State == EntKube.Web.Services.SupplyChain.ImageScanState.Unscanned)
            .GroupBy(i => i.ClusterId))
        {
            var images = cluster.ToList();
            string clusterName = images[0].ClusterName;

            result.Add(new OperationsFinding
            {
                Id = $"unscanned-images:{cluster.Key}",
                Category = AdvisorCategory.SupplyChain,
                Severity = AdvisorSeverity.Warning,
                Horizon = AdvisorHorizon.Later,
                Title = $"{images.Count} running image{(images.Count == 1 ? " has" : "s have")} "
                    + $"no scan result on “{clusterName}”",
                // An unscanned image is not a clean one — say so, because the natural
                // reading of "no findings" is "no problems".
                Detail = "These images are in a managed registry but have no completed scan, so their "
                    + "vulnerability status is unknown — not clean.",
                ScopeLabel = $"Cluster: {clusterName}",
                Remediation = "Trigger a Harbor scan for these repositories, or enable scan-on-push.",
                LinkSection = "supply-chain",
                ClusterId = cluster.Key,
                Source = "supply-chain",
            });
        }

        return result;
    }

    /// <summary>
    /// Turns the last disaster-recovery sweep into findings.
    ///
    /// One finding per gap rather than aggregated per cluster: unlike patch drift, each
    /// of these is a distinct decision — an unavailable bucket, a paused schedule and an
    /// untested restore need three different actions from three different people.
    /// </summary>
    private List<OperationsFinding> BuildDrFindings(Guid tenantId, DateTime now)
    {
        var result = new List<OperationsFinding>();

        IReadOnlyList<EntKube.Web.Services.Dr.ClusterDrStatus>? statuses = drCache.Get(tenantId);
        if (statuses is null)
        {
            return result;
        }

        foreach (EntKube.Web.Services.Dr.ClusterDrStatus status in statuses)
        {
            foreach (EntKube.Web.Services.Dr.DrGap gap in
                     EntKube.Web.Services.Dr.DrReadiness.Evaluate(status, now))
            {
                result.Add(new OperationsFinding
                {
                    Id = gap.Key,
                    Category = AdvisorCategory.DataProtection,
                    Severity = gap.Severity switch
                    {
                        EntKube.Web.Services.Dr.DrSeverity.Critical => AdvisorSeverity.Critical,
                        EntKube.Web.Services.Dr.DrSeverity.Warning => AdvisorSeverity.Warning,
                        _ => AdvisorSeverity.Info,
                    },
                    // A cluster with nothing to restore from is a today problem: the exposure
                    // is total and every hour it persists is more data that cannot come back.
                    Horizon = gap.Severity switch
                    {
                        EntKube.Web.Services.Dr.DrSeverity.Critical => AdvisorHorizon.Today,
                        EntKube.Web.Services.Dr.DrSeverity.Warning => AdvisorHorizon.ThisWeek,
                        _ => AdvisorHorizon.Later,
                    },
                    Title = gap.Title,
                    Detail = gap.Detail,
                    ScopeLabel = $"Cluster: {status.ClusterName}",
                    Remediation = gap.Remediation,
                    LinkSection = "disaster-recovery",
                    ClusterId = status.ClusterId,
                    Source = "dr",
                });
            }
        }

        return result;
    }

    /// <summary>Renders up to a handful of upgrades as "name 1.2.3 → 1.3.0", then a tail count.</summary>
    private static string DescribeUpgrades(List<ComponentUpgrade> upgrades)
    {
        const int MaxListed = 4;

        IEnumerable<string> listed = upgrades
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListed)
            .Select(u => $"{u.DisplayName} {u.InstalledVersion} → {u.LatestVersion}");

        string text = string.Join(", ", listed);
        int remaining = upgrades.Count - MaxListed;
        return remaining > 0 ? $"{text}, and {remaining} more." : $"{text}.";
    }

    private static string FmtHours(double hours)
    {
        if (hours < 1) return "under 1 h";
        if (hours < 48) return $"{(int)hours} h";
        return $"{(int)(hours / 24)} d";
    }

    private async Task<Dictionary<Guid, (Guid CustomerId, string CustomerName)>> LoadAppOwnersAsync(
        Guid tenantId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        var rows = await db.Apps
            .Where(a => a.Customer.TenantId == tenantId)
            .Select(a => new { a.Id, a.CustomerId, CustomerName = a.Customer.Name })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => (r.CustomerId, r.CustomerName));
    }

    // ── timing helpers ──

    private static AdvisorHorizon HorizonFor(DateTime? dueUtc, DateTime now, bool alreadyBreached)
    {
        if (alreadyBreached) return AdvisorHorizon.Overdue;
        if (dueUtc is null) return AdvisorHorizon.Later;

        double hours = (dueUtc.Value - now).TotalHours;
        if (hours <= 0) return AdvisorHorizon.Overdue;
        if (hours <= 48) return AdvisorHorizon.Today;
        if (hours <= 24 * 8) return AdvisorHorizon.ThisWeek;
        return AdvisorHorizon.Later;
    }

    private static string DueText(DateTime? dueUtc, DateTime now)
    {
        if (dueUtc is null) return "no deadline";
        TimeSpan delta = dueUtc.Value - now;
        return delta.Ticks < 0
            ? $"{HumanSpan(delta.Negate())} ago"
            : $"in {HumanSpan(delta)}";
    }

    private static string HumanSpan(TimeSpan span)
    {
        if (span < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)span.TotalMinutes)} m";
        if (span < TimeSpan.FromDays(2)) return $"{(int)span.TotalHours} h";
        return $"{(int)span.TotalDays} d";
    }
}
