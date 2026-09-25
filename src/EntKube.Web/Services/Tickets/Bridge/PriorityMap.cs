using EntKube.Web.Data;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>
/// Turning a sending system's priority into one of §14.2's.
///
/// <para><b>Configured, never guessed.</b> Jira's priorities are whatever a project
/// administrator last typed into a dropdown; ServiceNow computes one from impact ×
/// urgency; §14.2's four are contractual definitions with money attached — a P1 carries a
/// two-hour response, a call-out rate and 10% of the window fee if it is missed. Deciding
/// that "Highest" means P1 is a decision about the agreement, and it belongs to whoever
/// configured the connection rather than to a table of guesses shipped in the product.</para>
///
/// <para><b>And unmapped is a real answer.</b> A value nobody mapped produces no proposal
/// at all, which §14.3 already has an answer for: we make the first assessment and confirm
/// a priority in writing. Falling back to P3 would look like the customer had proposed one,
/// and quietly settle a question nobody had been asked.</para>
/// </summary>
public static class PriorityMap
{
    /// <summary>
    /// Reads the configured lines — <c>their value=P1</c>, one per line. Blank lines and
    /// anything after a <c>#</c> are ignored, so the configuration can explain itself.
    /// </summary>
    public static IReadOnlyDictionary<string, TicketPriority> Parse(string? configured)
    {
        Dictionary<string, TicketPriority> map = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return map;
        }

        foreach (string raw in configured.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Split('#', 2)[0].Trim();

            if (line.Length == 0)
            {
                continue;
            }

            string[] halves = line.Split('=', 2);

            if (halves.Length != 2)
            {
                continue;
            }

            string from = halves[0].Trim();
            string to = halves[1].Trim();

            if (from.Length == 0 || !TryPriority(to, out TicketPriority priority))
            {
                continue;
            }

            // First wins, so a duplicated line is not a silent reordering.
            map.TryAdd(from, priority);
        }

        return map;
    }

    /// <summary>
    /// What §14.2 priority a sending system's value proposes, or null for none.
    /// </summary>
    public static TicketPriority? Proposed(string? theirs, IReadOnlyDictionary<string, TicketPriority> map)
    {
        if (string.IsNullOrWhiteSpace(theirs))
        {
            return null;
        }

        return map.TryGetValue(theirs.Trim(), out TicketPriority mapped) ? mapped : null;
    }

    /// <summary>
    /// Accepts <c>P1</c> or a bare <c>1</c>, because both are what people write.
    /// </summary>
    private static bool TryPriority(string value, out TicketPriority priority)
    {
        string text = value.Trim().TrimStart('p', 'P');

        if (int.TryParse(text, out int number) && number is >= 1 and <= 4)
        {
            priority = (TicketPriority)number;
            return true;
        }

        priority = default;
        return false;
    }
}
