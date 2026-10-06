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
public interface IAdvisorApi
{
    /// <summary>Every recorded decision for a tenant, keyed by finding key.</summary>
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
