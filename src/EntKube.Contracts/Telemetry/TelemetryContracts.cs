namespace EntKube.Contracts.Telemetry;

/// <summary>
/// Where an incident has got to. Mirrors Telemetry's own members in order — see
/// <c>ModuleApiTests</c>, which holds them there, because the public API publishes these names.
/// </summary>
public enum IncidentStatus
{
    Active,
    Acknowledged,
    Resolved,
}

/// <summary>
/// An alert incident, as everyone outside Telemetry sees it.
///
/// <para>Tenancy reaches an incident through its cluster, which is also where the tenant lives in
/// the schema — so <c>i.Cluster.TenantId</c> is the filter, and getting that wrong is how one
/// customer's incidents would show up under another.</para>
/// </summary>
/// <param name="Id">The incident's id.</param>
/// <param name="ClusterId">The cluster it fired on.</param>
/// <param name="ClusterName">That cluster's name, to save a second lookup.</param>
/// <param name="AlertName">The alert rule's name.</param>
/// <param name="Summary">One line about what is wrong.</param>
/// <param name="Severity">The rule's severity label, as the rule wrote it.</param>
/// <param name="Status">Active, acknowledged or resolved.</param>
/// <param name="StartsAt">When it began.</param>
/// <param name="ResolvedAt">When it stopped, if it has.</param>
/// <param name="AcknowledgedBy">Who acknowledged it, if anyone.</param>
/// <param name="RunbookUrl">Where to look, if the rule said.</param>
public sealed record IncidentSummary(
    Guid Id,
    Guid ClusterId,
    string ClusterName,
    string AlertName,
    string Summary,
    string Severity,
    IncidentStatus Status,
    DateTime StartsAt,
    DateTime? ResolvedAt,
    string? AcknowledgedBy,
    string? RunbookUrl);

/// <summary>
/// What Telemetry offers the rest of EntKube.
///
/// <para>One method, because one consumer outside the module reads these tables through a service:
/// the public API's <c>/incidents</c> route. Written that way on purpose — four earlier contracts
/// were written as complete-looking surfaces and had no callers at all (#138), so this one starts
/// at its caller and grows when a second appears.</para>
///
/// <para><b>What measuring found, and why this contract is smaller than the raw count suggests.</b>
/// Thirty foreign reads touch Telemetry's tables, but twenty of them are <c>BackupService</c>'s
/// whole-database export and <c>TenantService</c>'s tenant purge — an export and a cascade of
/// deletes, neither of which a per-module <em>read</em> contract can serve. Eight more are in Razor
/// pages, which are not modules and hold a context anyway. That left one.</para>
///
/// <para>⚠️ <b>Notification provider configuration is deliberately absent.</b>
/// <c>SmtpSettingsResolver</c> reads <c>NotificationProviderConfigs</c>, and that row's
/// <c>ConfigurationJson</c> carries the SMTP <b>password</b>. A read method here would publish a
/// credential to every module that can reach this contract — the same reason
/// <c>Fleet.ClusterSummary</c> omits the kubeconfig. That read stays inside the service that needs
/// it until there is somewhere credential-shaped for it to go.</para>
/// </summary>
/// <summary>
/// One deployment's standing against its SLA over the objective's own window.
///
/// <para><b>Why the API needed this.</b> Nothing on the public API could answer "are we meeting
/// the SLAs" — a grep for budget, availability, uptime, sla and maintenance across all 936 lines
/// of the endpoint file returned nothing at all. An operator could read it in the browser and a
/// support agreement could be invoiced against it, but no CI pipeline, status page or ticketing
/// system could ask.</para>
///
/// <para>⚠️ <b><paramref name="AchievedPercent"/> is nullable and that is the interesting
/// case.</b> It is null when the deployment has no health samples in the window — which is not
/// 0% availability, and a client that renders a missing number as zero will report a total outage
/// for a deployment that was merely registered yesterday. <paramref name="HasData"/> is the
/// question to ask first.</para>
/// </summary>
/// <param name="BudgetConsumedFraction">
/// Fraction of the error budget spent: 0 is pristine, 1.0 is exactly exhausted, above 1.0 is
/// breached. Null when there is no data.
/// </param>
/// <param name="BurnRateRecent">
/// The last day's error rate divided by the sustainable rate. Above 1.0 means burning faster than
/// the window allows, even when the budget is still in credit — this is the field that answers
/// "will we breach" rather than "have we breached".
/// </param>
/// <param name="HoursToExhaustion">
/// At the recent burn rate, hours until the remaining budget is gone. Null when not on track to
/// exhaust — which includes a budget already breached, so it is not an ordering key.
/// </param>
/// <param name="IsBreached">
/// ⚠️ Strictly greater than 1.0. A budget exactly spent MEETS the objective; this was once
/// <c>&gt;=</c> and reported a breach for availability exactly equal to its target.
/// </param>
public sealed record ErrorBudgetSummary(
    Guid DeploymentId,
    string DeploymentName,
    string AppName,
    Guid? CustomerId,
    string? CustomerName,
    Guid ClusterId,
    double TargetPercent,
    int WindowDays,
    double? AchievedPercent,
    double? BudgetConsumedFraction,
    double? BurnRateRecent,
    double? HoursToExhaustion,
    bool HasData,
    bool IsBreached);

/// <summary>A log query's table. Cells are text, so one shape carries any query's columns.</summary>
/// <param name="Columns">The output columns, in order.</param>
/// <param name="Rows">Rows aligned with <paramref name="Columns"/>. A null cell has no value.</param>
/// <param name="LinesScanned">How many log lines were read.</param>
/// <param name="Partial">True when reading stopped at its limit, so counts are lower bounds.</param>
public sealed record LogQueryTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int LinesScanned,
    bool Partial);

/// <summary>What became of a log query. Exactly one of the three is set.</summary>
/// <param name="Table">The answer.</param>
/// <param name="SyntaxError">Why the query was refused, for the caller to correct.</param>
/// <param name="Failure">Why a valid query could not run, which is not the caller's doing.</param>
public sealed record LogQueryOutcome(LogQueryTable? Table, string? SyntaxError, string? Failure);

public interface ITelemetryApi
{
    /// <summary>
    /// One tenant's incidents, newest first.
    /// </summary>
    /// <param name="openOnly">Excludes resolved incidents when true.</param>
    /// <param name="limit">
    /// A cap, because this feeds a list endpoint. Passed rather than fixed so the caller's own
    /// contract — the public API takes 200 — stays visible at the call.
    /// </param>
    Task<IReadOnlyList<IncidentSummary>> GetIncidentsAsync(
        Guid tenantId, bool openOnly = false, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Every managed deployment's standing against its SLA, computed on read.
    ///
    /// <para>Composed from two other modules' contracts — the objectives are Support's and the
    /// health samples are Delivery's — because error budgets own no table of their own. That is
    /// why this sits on Telemetry's contract rather than on either of theirs: it is the module
    /// that already owns the question, and <c>ModuleServiceMap</c> places the service that answers
    /// it here.</para>
    ///
    /// <para>Deployments that are not managed are omitted rather than reported with a null target:
    /// an unmanaged deployment has no objective to miss.</para>
    /// </summary>
    Task<IReadOnlyList<ErrorBudgetSummary>> GetErrorBudgetsAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Runs a log query over one of the tenant's clusters. The caller has already established that
    /// the cluster is the tenant's.
    /// </summary>
    Task<LogQueryOutcome> QueryLogsAsync(
        Guid tenantId, Guid clusterId, string query, DateTime from, DateTime to, CancellationToken ct = default);
}
