using System.Collections.Concurrent;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// When a cluster whose last apply failed may be written to again.
///
/// <para><b>Why a failing write waits and a failing read does not.</b> The gate records every
/// unattended write before it happens and nothing ever deletes those rows, so a cluster that refuses
/// the same apply every five minutes would fill the audit trail with it. A read leaves nothing
/// behind.</para>
///
/// <para>Five minutes after the first failure, twice that after each further one, up to six hours.
/// A success clears it, and so does a different set of changes: whatever was wrong with the last one
/// says nothing about this one. In memory, on the host that sweeps; a restart tries again at once.</para>
/// </summary>
public sealed class ScrapeMonitorBackoff(TimeProvider clock)
{
    public static readonly TimeSpan First = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Longest = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<Guid, Refusal> refusals = new();

    private sealed record Refusal(string Changes, TimeSpan Wait, DateTimeOffset Until);

    /// <summary>Whether these changes may be attempted on this cluster now.</summary>
    /// <param name="changes">Identifies the set of changes; the same set gives the same value.</param>
    public bool MayAttempt(Guid clusterId, string changes)
        => !refusals.TryGetValue(clusterId, out Refusal? last)
           || last.Changes != changes
           || clock.GetUtcNow() >= last.Until;

    public void Failed(Guid clusterId, string changes)
    {
        TimeSpan wait = refusals.TryGetValue(clusterId, out Refusal? last) && last.Changes == changes
            ? TimeSpan.FromTicks(Math.Min(last.Wait.Ticks * 2, Longest.Ticks))
            : First;

        refusals[clusterId] = new Refusal(changes, wait, clock.GetUtcNow() + wait);
    }

    public void Succeeded(Guid clusterId) => refusals.TryRemove(clusterId, out _);
}
