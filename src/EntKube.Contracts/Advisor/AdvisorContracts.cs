namespace EntKube.Contracts.Advisor;

/// <summary>What someone has decided about a finding.</summary>
public enum FindingStatus
{
    /// <summary>Open and unhandled.</summary>
    Active,

    /// <summary>Someone is on it. Still counts, but flagged as handled.</summary>
    Acknowledged,

    /// <summary>Hidden until <see cref="FindingState.SnoozedUntil"/> passes.</summary>
    Snoozed,

    /// <summary>Accepted or ignored; hidden unless explicitly shown.</summary>
    Dismissed,
}

/// <summary>How often a tenant receives the advisor digest.</summary>
/// <summary>Broad problem domain a finding belongs to.</summary>
/// <remarks>
/// ⚠️ Mirrors <c>AdvisorCategory</c> member for member, and the names are <b>published</b>: the
/// public API's <c>/advisor/findings</c> route emits <c>category</c> as <c>ToString()</c> on this
/// value. Renaming a member here renames a field in a response the CLI and MCP server read, so
/// <c>AdvisorContractEnumTests</c> pins the names rather than only the ordinals — the same reason
/// <c>PublishedProvisioningStatus</c> exists for Fleet, where the entity and contract disagreed on
/// a name and projecting the contract value would have changed the API by accident.
/// </remarks>
public enum FindingCategory
{
    Security,
    Reliability,
    DataProtection,
    Capacity,
    Maintenance,
    SupplyChain,
}

/// <summary>How bad a finding is. Mirrors <c>AdvisorSeverity</c>; names are published.</summary>
public enum FindingSeverity
{
    Critical = 0,
    Warning = 1,
    Info = 2,
}

/// <summary>When a finding needs acting on. Mirrors <c>AdvisorHorizon</c>; names are published.</summary>
public enum FindingHorizon
{
    /// <summary>The deadline has already passed.</summary>
    Overdue = 0,

    /// <summary>Impact within ~48h.</summary>
    Today = 1,

    /// <summary>Impact within ~a week.</summary>
    ThisWeek = 2,

    /// <summary>On the radar, with no near-term deadline.</summary>
    Later = 3,
}

public enum DigestFrequency
{
    Off,
    Daily,
    Weekly,
}

/// <summary>
/// The human decision layered over one finding.
///
/// <para>Findings themselves are computed on read and never stored, so this is the only
/// part of the advisor that persists. It is keyed by the finding's stable synthetic key
/// (<c>secret:{id}</c>, <c>slo-breach:{id}</c> and so on) rather than by a row id,
/// because the finding it describes is recomputed every sixty seconds.</para>
/// </summary>
/// <param name="FindingKey">The finding's stable synthetic key.</param>
/// <param name="Status">What was decided.</param>
/// <param name="SnoozedUntil">When a snooze expires; null unless snoozed.</param>
/// <param name="AcknowledgedBy">Who acknowledged it.</param>
/// <param name="AcknowledgedAt">When they did.</param>
/// <param name="AssignedTo">Who is currently handling it, free text.</param>
/// <param name="Note">Any note left with the decision.</param>
/// <param name="FirstSeenAt">First observation — this is what drives aging and escalation.</param>
/// <param name="LastSeenAt">Most recent observation. Stale rows are pruned so a recurrence starts fresh.</param>
public sealed record FindingState(
    string FindingKey,
    FindingStatus Status,
    DateTime? SnoozedUntil,
    string? AcknowledgedBy,
    DateTime? AcknowledgedAt,
    string? AssignedTo,
    string? Note,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);

/// <summary>
/// A tenant's digest settings. <paramref name="LastSentAt"/> is persisted so the cadence
/// survives a redeploy without double-sending.
/// </summary>
/// <param name="Frequency">Off, daily or weekly.</param>
/// <param name="HourUtc">Hour of day (UTC, 0–23) at or after which the digest goes out.</param>
/// <param name="WeeklyDay">Which day, when the frequency is weekly.</param>
/// <param name="LastSentAt">When it was last actually sent.</param>
public sealed record DigestSettings(
    DigestFrequency Frequency,
    int HourUtc,
    DayOfWeek WeeklyDay,
    DateTime? LastSentAt);

/// <summary>
/// What the Advisor module offers the rest of EntKube.
///
/// <para>Advisor is the module the decomposition uses as its proof: it reads from
/// everywhere and owns almost nothing, so if it can be written entirely against other
/// modules' contracts then the boundaries are real. This interface is the other half —
/// what everyone else is allowed to know about <em>it</em>.</para>
///
/// <para>Computing the findings is not here. That is Advisor's own work, and exposing it
/// would mean exposing every other module's data through the back door.</para>
/// </summary>
/// <summary>
/// One synthesized finding, as the rest of the product reads it.
///
/// <para><b>Thirteen fields, because thirteen are read.</b> <c>OperationsFinding</c> carries
/// twenty-six — timing phrases, a priority score, escalation and "new" flags, a confidence,
/// deep-link hints, the lead incident id. Those exist for the operator tab that renders them and
/// for the sort order inside a horizon; nothing outside the module asks for any of them. The
/// public API projects exactly the set below, which is where this shape came from.</para>
///
/// <para>Findings are computed on read and never stored, so there is no id to fetch one by —
/// <see cref="Id"/> is a stable synthetic key like <c>cert:{secretId}</c>, which is also what the
/// state methods take.</para>
/// </summary>
public sealed record FindingSummary(
    string Id,
    FindingCategory Category,
    FindingSeverity Severity,
    FindingHorizon Horizon,
    string Title,
    string? Detail,
    string ScopeLabel,
    DateTime? DueAt,
    string? Remediation,
    string Source,
    FindingStatus State,
    Guid? ClusterId,
    string? CustomerName);

public interface IAdvisorApi
{
    /// <summary>Every recorded decision for a tenant, keyed by finding key.</summary>
    /// <summary>
    /// Every finding the advisor currently reports for a tenant, most urgent first.
    ///
    /// <para><b>Added because the one caller outside the module could not use this contract at
    /// all.</b> The public API's <c>GET /advisor/findings</c> injected
    /// <c>OperationsAdvisorService</c> directly — a module's own service, reached across the
    /// boundary — because the only read here returned raw state rows keyed by finding id, which is
    /// the module's internal join and not a question anyone outside it asks. The findings are the
    /// answer; the states are how Advisor computes it.</para>
    ///
    /// <para>No paging and no filter: the report is computed on read in one pass over a tenant's
    /// signals, so there is nothing cheaper to ask for. A caller wanting one horizon filters the
    /// result, as the operator tab already does.</para>
    /// </summary>
    Task<IReadOnlyList<FindingSummary>> GetFindingsAsync(
        Guid tenantId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, FindingState>> GetFindingStatesAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>Marks a finding as being handled.</summary>
    Task AcknowledgeAsync(Guid tenantId, string findingKey, string by, CancellationToken ct = default);

    /// <summary>Hides a finding until <paramref name="until"/>.</summary>
    Task SnoozeAsync(Guid tenantId, string findingKey, DateTime until, string by, CancellationToken ct = default);

    /// <summary>Accepts a finding, hiding it unless explicitly shown.</summary>
    Task DismissAsync(Guid tenantId, string findingKey, string by, CancellationToken ct = default);

    /// <summary>Returns a finding to <see cref="FindingStatus.Active"/>.</summary>
    Task ReactivateAsync(Guid tenantId, string findingKey, CancellationToken ct = default);

    /// <summary>Sets or clears who is handling a finding.</summary>
    Task AssignAsync(Guid tenantId, string findingKey, string? assignee, CancellationToken ct = default);

    /// <summary>A tenant's digest settings, or the defaults when it has none.</summary>
    Task<DigestSettings> GetDigestSettingsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Stores a tenant's digest settings.</summary>
    Task SaveDigestSettingsAsync(
        Guid tenantId, DigestFrequency frequency, int hourUtc, DayOfWeek weeklyDay,
        CancellationToken ct = default);
}
