using EntKube.Contracts.Telemetry;
using EntKube.Web.Data.Modules;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ContractIncidentStatus = EntKube.Contracts.Telemetry.IncidentStatus;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Telemetry's implementation of <see cref="ITelemetryApi"/>, over its own module context.
///
/// <para>Tenancy reaches an incident through its cluster, which is where the tenant lives in the
/// schema, so every query goes <c>i.Cluster.TenantId</c> rather than taking a column at face
/// value — the same shape as <c>CatalogApi</c>, for the same reason.</para>
/// </summary>
public sealed class TelemetryApi(
    IDbContextFactory<TelemetryDbContext> dbFactory,
    EntKube.Web.Services.ErrorBudgetService errorBudgets,
    EntKube.Web.Services.FeedbackService feedback,
    EntKube.Web.Services.LogPipelineService logPipelines,
    EntKube.Web.Services.SyntheticCheckService syntheticChecks,
    EntKube.Web.Services.ErrorTrackingService errorTracking,
    EntKube.Web.Services.SloService slos) : ITelemetryApi
{
    public async Task<IReadOnlyList<SloSummary>> GetSlosAsync(Guid tenantId, CancellationToken ct = default)
    {
        List<EntKube.Web.Services.SloRow> rows = await slos.ListAsync(tenantId, DateTime.UtcNow, ct);

        return rows
            .Select(r => new SloSummary(
                r.Slo.Id, r.Slo.Name, r.Slo.Kind.ToString(), r.Slo.TargetPercent, r.Slo.WindowDays,
                r.Status.Good, r.Status.Total, r.Status.AttainedPercent, r.Status.BudgetSpentFraction,
                r.Status.BurnRate, r.Status.IsBreached))
            .ToList();
    }

    public async Task<IReadOnlyList<ErrorIssueSummary>> GetErrorIssuesAsync(
        Guid tenantId, string? status = null, int limit = 200, CancellationToken ct = default)
    {
        Data.ErrorIssueStatus? wanted = status is null ? null : Enum.Parse<Data.ErrorIssueStatus>(status, ignoreCase: true);

        List<Data.ErrorIssue> issues = await errorTracking.ListAsync(tenantId, wanted, limit, ct);

        return issues
            .Select(i => new ErrorIssueSummary(
                i.Id, i.Source.ToString(), i.ScopeName, i.Namespace, i.Workload, i.Title, i.SampleMessage,
                i.Occurrences, i.CountIsPartial, i.Status.ToString(), i.Regressions, i.FirstSeenAt, i.LastSeenAt))
            .ToList();
    }

    public async Task<IReadOnlyList<FeedbackEntry>> GetFeedbackAsync(
        Guid tenantId, string? status = null, int limit = 200, CancellationToken ct = default)
    {
        Data.FeedbackStatus? wanted = status is null ? null : Enum.Parse<Data.FeedbackStatus>(status, ignoreCase: true);

        List<EntKube.Web.Services.FeedbackRow> rows = await feedback.ListAsync(tenantId, null, wanted, limit, ct);

        return rows
            .Select(r => new FeedbackEntry(
                r.Item.Id, r.Item.Kind.ToString(), r.Item.Status.ToString(), r.Item.Rating, r.Item.Message,
                r.Item.ContactEmail, r.Item.SiteName, r.Item.PagePath, r.HasScreenshot, r.Item.TicketId,
                r.Item.CreatedAt))
            .ToList();
    }

    public async Task<LogQueryOutcome> QueryLogsAsync(
        Guid tenantId, Guid clusterId, string query, DateTime from, DateTime to, CancellationToken ct = default)
    {
        EntKube.Web.Services.LogPipelineOutcome outcome =
            await logPipelines.RunAsync(tenantId, clusterId, query, from, to, ct);

        if (outcome.SyntaxError is { } error)
        {
            return new LogQueryOutcome(null, $"{error.Message} (at character {error.Position + 1})", null);
        }

        if (outcome.Result is not { } result)
        {
            return new LogQueryOutcome(null, null, outcome.Failure);
        }

        return new LogQueryOutcome(
            new LogQueryTable(
                [.. result.Columns.Select(c => c.Name)],
                [.. result.Rows.Select(r => (IReadOnlyList<string?>)[.. r.Select(EntKube.Telemetry.LogPipelines.LogPipelineFormat.Cell)])],
                result.LinesScanned,
                result.Partial),
            null,
            null);
    }

    public async Task<IReadOnlyList<SyntheticCheckSummary>> GetSyntheticChecksAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        List<EntKube.Web.Services.SyntheticCheckRow> rows = await syntheticChecks.ListAsync(tenantId, ct);

        return rows
            .Select(r => new SyntheticCheckSummary(
                r.Check.Id, r.Check.Name, r.Check.Url, r.Check.IsEnabled, r.Check.State.ToString(),
                r.Check.ConsecutiveFailures, r.UptimePercent, r.Check.LastStatusCode, r.Check.LastDurationMs,
                r.Check.LastFailure, r.Check.LastRunAt))
            .ToList();
    }

    public async Task<IReadOnlyList<IncidentSummary>> GetIncidentsAsync(
        Guid tenantId, bool openOnly = false, int limit = 200, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<Data.AlertIncident> query = Scoped(db, tenantId);

        if (openOnly)
        {
            query = query.Where(i => i.Status != Data.IncidentStatus.Resolved);
        }

        // The rows come back carrying the ENTITY's enum, and the contract's copy is mapped in
        // memory below. That is not a style choice: AlertIncident.Status is persisted as text
        // (HasConversion<string>() in TelemetryModel), so a cast inside this projection would
        // become CAST("Status" AS integer) in SQL — which PostgreSQL rejects outright and SQLite
        // answers with 0. That exact mistake took the cost report down (#223) and passed its unit
        // tests the whole time.
        List<Row> rows = await query
            .OrderByDescending(i => i.StartsAt)
            .Take(limit)
            .Select(ToRow)
            .ToListAsync(ct);

        return [.. rows.Select(ToContract)];
    }

    /// <summary>
    /// Delegates to Telemetry's own <c>ErrorBudgetService</c>, which composes Support's objectives
    /// with Delivery's health samples.
    ///
    /// <para>⚠️ <b>The delegation is the point, not a shortcut.</b> A public API route could have
    /// injected that service directly and saved this method — and
    /// <c>PublicApiContractAdoptionTests</c> would have gone red for it, because
    /// <c>ModuleServiceMap</c> places <c>ErrorBudgetService</c> in this module and reaching past a
    /// contract into a module's services is the same boundary break as reading its tables, one
    /// layer further in. The ratchet is what chose this shape.</para>
    /// </summary>
    public async Task<IReadOnlyList<ErrorBudgetSummary>> GetErrorBudgetsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        List<Services.ErrorBudgetStatus> statuses =
            await errorBudgets.GetTenantErrorBudgetsAsync(tenantId, ct);

        return
        [
            .. statuses.Select(s => new ErrorBudgetSummary(
                s.DeploymentId,
                s.DeploymentName,
                s.AppName,
                s.CustomerId,
                s.CustomerName,
                s.ClusterId,
                s.TargetPercent,
                s.WindowDays,
                s.AchievedPercent,
                s.BudgetConsumedFraction,
                s.BurnRateRecent,
                s.HoursToExhaustion,
                // Both are computed properties on the source record rather than stored fields, so
                // they are carried across explicitly. A client cannot re-derive IsBreached from a
                // rounded percentage, and the > vs >= distinction is exactly where it would differ.
                s.HasData,
                s.IsBreached))
        ];
    }

    private static IQueryable<Data.AlertIncident> Scoped(TelemetryDbContext db, Guid tenantId)
        => db.AlertIncidents.AsNoTracking().Where(i => i.Cluster.TenantId == tenantId);

    /// <summary>The row as the database hands it over, carrying Telemetry's own enum.</summary>
    private sealed record Row(
        Guid Id,
        Guid ClusterId,
        string ClusterName,
        string AlertName,
        string Summary,
        string Severity,
        Data.IncidentStatus Status,
        DateTime StartsAt,
        DateTime? ResolvedAt,
        string? AcknowledgedBy,
        string? RunbookUrl);

    /// <summary>Applied after any filter — see CatalogApi for why the order matters.</summary>
    private static readonly Expression<Func<Data.AlertIncident, Row>> ToRow =
        i => new Row(
            i.Id,
            i.ClusterId,
            i.Cluster.Name,
            i.AlertName,
            i.Summary,
            i.Severity,
            i.Status,
            i.StartsAt,
            i.ResolvedAt,
            i.AcknowledgedBy,
            i.RunbookUrl);

    /// <summary>
    /// Entity enum to contract enum by ordinal, which is sound only while the two agree member for
    /// member — so <c>ModuleApiTests</c> checks that they do rather than trusting it. The public
    /// API publishes these names.
    /// </summary>
    private static IncidentSummary ToContract(Row r) => new(
        r.Id,
        r.ClusterId,
        r.ClusterName,
        r.AlertName,
        r.Summary,
        r.Severity,
        (ContractIncidentStatus)(int)r.Status,
        r.StartsAt,
        r.ResolvedAt,
        r.AcknowledgedBy,
        r.RunbookUrl);
}
