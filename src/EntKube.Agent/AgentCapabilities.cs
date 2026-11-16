using System.Reflection;
using EntKube.Agents.Protocol;

namespace EntKube.Agent;

/// <summary>
/// What this agent can be asked to do, and the decision it makes about a lease.
///
/// <para><b>The table is empty, and that is the current correct answer.</b> This agent implements
/// no capabilities, so it refuses every lease and says why. A refusal is an ordinary answer: it
/// must leave the link and every tunnel stream on it untouched, exactly as a refused
/// <see cref="AgentFrameType.Open"/> does.</para>
///
/// <para><b>Why there is no "run the work" path yet.</b> It would be code that cannot execute —
/// there is nothing for it to run — shipped to a customer's box as a binary EntKube cannot
/// upgrade. Item 12 of docs/decomposition.md adds the first capability, a read-only component
/// scan, and adds the execution path with it. What exists now is the vocabulary, the negotiation,
/// and an honest refusal.</para>
///
/// <para><b>And why this is a pure function.</b> The decision is the part worth pinning; the
/// socket wiring around it is one line that a test of this function deliberately does not cover.</para>
/// </summary>
public static class AgentCapabilities
{
    /// <summary>
    /// Capability names this binary implements. Item 12's first row is <c>component.scan</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> Supported = [];

    /// <summary>This agent's own version, for an operator diagnosing a fleet.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>What this agent tells EntKube about itself once the link is up.</summary>
    public static CapabilityPayload Declare()
        => new(AgentProtocol.ProtocolVersion, Version, Supported);

    /// <summary>
    /// Decides whether this agent will take a lease.
    ///
    /// <para>The reason matters as much as the verdict. A refusal travels to an operator who has
    /// to act on it, and "refused" with no reason is indistinguishable from a bug at the other
    /// end — so every path here names what was wrong.</para>
    /// </summary>
    public static LeaseAckPayload Decide(LeasePayload lease)
    {
        if (!AgentTaskFrames.IsValidCapabilityName(lease.Capability))
        {
            return new LeaseAckPayload(
                lease.LeaseId, Accepted: false,
                $"'{lease.Capability}' is not a capability name.");
        }

        if (lease.DeadlineMs <= 0)
        {
            return new LeaseAckPayload(
                lease.LeaseId, Accepted: false,
                $"A lease needs a deadline to count down from; this one carried {lease.DeadlineMs}ms.");
        }

        if (!Supported.Contains(lease.Capability))
        {
            return new LeaseAckPayload(
                lease.LeaseId, Accepted: false,
                Supported.Count == 0
                    ? $"This agent implements no capabilities, so it cannot run '{lease.Capability}'."
                    : $"This agent does not implement '{lease.Capability}'. It implements: "
                        + $"{string.Join(", ", Supported)}.");
        }

        return new LeaseAckPayload(lease.LeaseId, Accepted: true);
    }
}
