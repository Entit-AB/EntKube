namespace EntKube.Web.Services.Agents;

/// <summary>
/// What a capability does to the world it runs in.
///
/// <para>⚠️ <b>There is exactly one member, and that is the design.</b> docs/decomposition.md
/// §5.4.1 decided that an in-cluster agent may read and may propose, but that every mutation needs
/// a human approver. The weakest way to hold that line is a field the agent is asked to check; the
/// strongest is to make a mutating capability <em>inexpressible</em>, so there is no value to set
/// wrongly and no branch to get wrong.</para>
///
/// <para>Adding a second member is therefore not a small edit. It means EntKube can now ask an
/// agent to change something, which needs the durable approval queue §5.4 names as the
/// prerequisite — a persisted change with its diff, and an out-of-band approval carrying who
/// approved it and when. <c>AgentCapabilityTests</c> asserts this enum's size so that decision
/// cannot arrive as a one-line diff nobody read.</para>
/// </summary>
public enum AgentCapabilityEffect
{
    /// <summary>
    /// The capability observes and changes nothing.
    ///
    /// <para>A claim, like <c>ChangeIntent.Read</c>, and auditable the same way: every registered
    /// capability is enumerated by name in a test a human can actually read, and the claim is
    /// visible in the diff that introduces it.</para>
    /// </summary>
    Read = 0,
}

/// <summary>
/// One thing EntKube knows how to ask an agent to do.
/// </summary>
/// <param name="Name">
/// The dotted name that travels in a <see cref="Agents.Protocol.LeasePayload"/>, e.g.
/// <c>component.scan</c>.
/// </param>
/// <param name="Effect">What it does to the cluster. Required, with no default, because the
/// registration is the only place anyone states it.</param>
/// <param name="What">
/// What it actually runs, in a sentence, for the human reading the enumeration in the test. Not
/// a description of the feature — a description of the work, so "lists the helm releases in every
/// namespace" rather than "component scanning".
/// </param>
public sealed record AgentCapability(string Name, AgentCapabilityEffect Effect, string What);

/// <summary>
/// The capabilities EntKube is willing to dispatch, and the only authority on what a capability
/// name means.
///
/// <para><b>Why the server decides and not the agent.</b> An agent's <c>Capability</c> frame is
/// informational, exactly like the allowlist header it sits beside: the agent's local config is
/// authoritative about what it will <em>do</em>, and EntKube's registry is authoritative about
/// what it will <em>ask</em>. Dispatchable work is the intersection. Without that, an agent — a
/// binary running on a customer's box, which EntKube cannot upgrade and does not build — could
/// advertise a capability into existence by naming it.</para>
///
/// <para><b>It is empty.</b> That is item 9 finishing where it should: the mechanism exists so
/// that item 12's read-only component scan is a row rather than an invention. An empty registry
/// means every dispatch is refused, which is the correct behaviour for a product that has no agent
/// capabilities yet.</para>
/// </summary>
public static class AgentCapabilityRegistry
{
    private static readonly Dictionary<string, AgentCapability> Registered =
        new(StringComparer.Ordinal)
        {
            // Item 12 adds the first row here: component.scan, Read, "lists the helm releases
            // installed in every namespace". Nothing before then.
        };

    /// <summary>Every registered capability, for dispatch and for the test that enumerates them.</summary>
    public static IReadOnlyCollection<AgentCapability> All => Registered.Values;

    /// <summary>
    /// Resolves a name, or null when EntKube does not know it. A null here is a refusal to
    /// dispatch, not a lookup miss to work around.
    /// </summary>
    public static AgentCapability? Find(string name)
        => Registered.TryGetValue(name, out AgentCapability? capability) ? capability : null;
}
