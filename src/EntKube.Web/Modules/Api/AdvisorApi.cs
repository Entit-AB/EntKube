using EntKube.Contracts.Advisor;
using ContractDigestFrequency = EntKube.Contracts.Advisor.DigestFrequency;
using EntKube.Web.Data;
using EntKube.Web.Services;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// The Advisor module's implementation of <see cref="IAdvisorApi"/>.
///
/// <para>A thin adapter over the module's own services, whose only real job is to stop
/// EF entities crossing the boundary. That mapping looks like busywork and is not: an
/// entity carries navigation properties and a change tracker, so handing one to another
/// module hands it a live route into the database. A record is a promise about data.</para>
///
/// <para>When this module's surface becomes HTTP, this is the class the endpoints bind to —
/// which is the point of writing the contract in-process first.</para>
/// </summary>
public sealed class AdvisorApi(
    AdvisorStateService state,
    AdvisorDigestConfigService digest) : IAdvisorApi
{
    public async Task<IReadOnlyDictionary<string, FindingState>> GetFindingStatesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        Dictionary<string, AdvisorFindingState> rows = await state.GetStatesAsync(tenantId, ct);

        return rows.ToDictionary(r => r.Key, r => ToContract(r.Value));
    }

    public Task AcknowledgeAsync(Guid tenantId, string findingKey, string by, CancellationToken ct = default)
        => state.AcknowledgeAsync(tenantId, findingKey, by, ct);

    public Task SnoozeAsync(Guid tenantId, string findingKey, DateTime until, string by, CancellationToken ct = default)
        => state.SnoozeAsync(tenantId, findingKey, until, by, ct);

    public Task DismissAsync(Guid tenantId, string findingKey, string by, CancellationToken ct = default)
        => state.DismissAsync(tenantId, findingKey, by, ct);

    public Task ReactivateAsync(Guid tenantId, string findingKey, CancellationToken ct = default)
        => state.ReactivateAsync(tenantId, findingKey, ct);

    public Task AssignAsync(Guid tenantId, string findingKey, string? assignee, CancellationToken ct = default)
        => state.AssignAsync(tenantId, findingKey, assignee, ct);

    public async Task<DigestSettings> GetDigestSettingsAsync(Guid tenantId, CancellationToken ct = default)
    {
        AdvisorDigestConfig config = await digest.GetAsync(tenantId, ct);

        return new DigestSettings(
            (ContractDigestFrequency)(int)config.Frequency,
            config.HourUtc,
            config.WeeklyDay,
            config.LastSentAt);
    }

    public Task SaveDigestSettingsAsync(
        Guid tenantId, ContractDigestFrequency frequency, int hourUtc, DayOfWeek weeklyDay,
        CancellationToken ct = default)
        => digest.SaveAsync(tenantId, (Data.DigestFrequency)(int)frequency, hourUtc, weeklyDay, ct);

    private static FindingState ToContract(AdvisorFindingState s) => new(
        s.FindingKey,
        (FindingStatus)(int)s.Status,
        s.SnoozedUntil,
        s.AcknowledgedBy,
        s.AcknowledgedAt,
        s.AssignedTo,
        s.Note,
        s.FirstSeenAt,
        s.LastSeenAt);
}
