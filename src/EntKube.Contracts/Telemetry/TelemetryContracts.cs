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
}
