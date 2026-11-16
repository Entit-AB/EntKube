using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntKube.Agents.Protocol;

/// <summary>
/// How a lease ended, as the agent reports it.
///
/// <para>Values are explicit because they are on the wire as numbers, and a peer may send one this
/// build does not define. That is deliberate and mirrors <see cref="AgentProtocol.Decode"/>:
/// an unrecognised outcome deserializes rather than throwing, so a newer agent reporting a
/// finer-grained verdict does not break an older server. Treat anything unrecognised as a
/// failure — never as success.</para>
/// </summary>
public enum LeaseOutcome
{
    /// <summary>The work ran and produced its answer.</summary>
    Succeeded = 0,

    /// <summary>The work ran and could not complete. <c>Reason</c> says why.</summary>
    Failed = 1,

    /// <summary>
    /// The agent never started: it does not implement the capability, or its local configuration
    /// does not permit it. A refusal is an ordinary answer and must leave the link intact.
    /// </summary>
    Refused = 2,

    /// <summary>The deadline passed before the work finished.</summary>
    Expired = 3,

    /// <summary>
    /// The output exceeded <see cref="AgentProtocol.MaxTaskPayloadSize"/>. Reported rather than
    /// thrown, because the alternative — letting the frame encoder throw, or letting an oversized
    /// frame reach the peer's reassembly guard — tears down the link and every unrelated TCP
    /// stream on it. Bulk output belongs in <see cref="AgentFrameType.Progress"/> frames.
    /// </summary>
    PayloadTooLarge = 4,

    /// <summary>The server asked the agent to stop (<see cref="AgentFrameType.LeaseCancel"/>).</summary>
    Cancelled = 5,
}

/// <summary>
/// One unit of work, as the server states it.
/// </summary>
/// <param name="LeaseId">
/// Identifies this lease for the life of the link, and is the key the durable store will use.
/// A Guid rather than the frame's <c>StreamId</c>: that counter is an instance field of one
/// connection, it restarts at 0 on every reconnect, and a single agent may legitimately hold
/// several concurrent links — so two live leases would collide on id 1, silently.
/// </param>
/// <param name="Capability">
/// <b>What</b> to do, never <b>how</b>. A registered name such as <c>component.scan</c>, which the
/// server resolves against its own registry before dispatch and the agent resolves against its
/// compiled-in handlers on receipt. It is deliberately not a command line or an argument list:
/// a capability whose meaning is supplied by the caller is the one shape whose intent cannot be
/// declared once, which is the thirty-call-site problem docs/decomposition.md §5.4.2 describes,
/// recreated on the wire. <see cref="AgentTaskFrames.IsValidCapabilityName"/> enforces the shape.
/// </param>
/// <param name="DeadlineMs">
/// How long the agent has, counted from <b>its own receipt</b> of this frame.
///
/// <para><b>A duration, never an instant.</b> The agent is a binary a customer runs on a box
/// EntKube does not manage and whose clock it cannot vouch for, so an absolute deadline would
/// require the two ends to agree on a clock that nothing keeps in step. The small asymmetry is in
/// the safe direction: the server starts counting at send and the agent at receipt, so the server
/// gives up no earlier than the agent does. It also makes a test deterministic — the duration is a
/// literal and no clock is read at all.</para>
/// </param>
/// <param name="Input">
/// Capability-specific arguments, or null. Shaped by the capability, not by this protocol.
/// </param>
public sealed record LeasePayload(
    Guid LeaseId,
    string Capability,
    int DeadlineMs,
    JsonElement? Input = null);

/// <summary>The agent's immediate answer: it has the work, or it will not take it.</summary>
/// <param name="Reason">
/// Why it was refused, in words an operator can act on. Required on a refusal: a refusal with no
/// reason is indistinguishable from a bug at the other end.
/// </param>
public sealed record LeaseAckPayload(Guid LeaseId, bool Accepted, string? Reason = null);

/// <summary>
/// Partial output, and the heartbeat the deadline is measured against.
/// </summary>
/// <param name="Sequence">
/// Monotonic from 0 within one lease, so the receiver can tell "nothing yet" from "I missed
/// something".
/// </param>
/// <param name="Chunk">One piece of the output. Bounded by <see cref="AgentProtocol.MaxTaskPayloadSize"/>.</param>
/// <remarks>
/// Carries no timestamp on purpose. A heartbeat means "alive when you received this", which only
/// the receiver's clock can say; a sender's timestamp would be a number from an unmanaged box that
/// the receiver would then have to decide whether to believe.
/// </remarks>
public sealed record ProgressPayload(Guid LeaseId, int Sequence, JsonElement Chunk);

/// <summary>
/// The terminal frame for a lease: a verdict and, for small answers, the answer.
/// </summary>
/// <param name="Output">
/// The whole answer when it fits, otherwise null with the body already delivered as
/// <see cref="AgentFrameType.Progress"/> frames. A Result is a verdict, not a body.
/// </param>
public sealed record ResultPayload(
    Guid LeaseId,
    LeaseOutcome Outcome,
    string? Reason = null,
    JsonElement? Output = null);

/// <summary>
/// What an agent can do, sent once per link after the handshake has settled the version.
/// </summary>
/// <param name="ProtocolVersion">Repeats what the handshake already said, so a mismatch is visible in a log.</param>
/// <param name="AgentVersion">The agent binary's version, for an operator diagnosing a fleet.</param>
/// <param name="Capabilities">
/// Names this agent implements. <b>Informational, exactly like the allowlist header.</b> It may
/// narrow what the server dispatches; it can never authorize anything. The server dispatches only
/// names in its own registry, so an agent advertising something EntKube does not know about is
/// ignored rather than obeyed — which is what stops a rogue or rolled-forward agent arguing a
/// capability into existence.
/// </param>
public sealed record CapabilityPayload(
    int ProtocolVersion,
    string AgentVersion,
    IReadOnlyList<string> Capabilities);

/// <summary>Stop working on this lease.</summary>
public sealed record LeaseCancelPayload(Guid LeaseId, string? Reason = null);

/// <summary>
/// Encodes and decodes the task vocabulary (docs/decomposition.md §5.2).
///
/// <para>UTF-8 JSON inside the existing five-byte framing, so there is still exactly one framing
/// path and the tunnel's bytes are untouched. The serializer options are <b>wire contract</b>, not
/// a caller's choice, which is why they are pinned here rather than passed in — the same reason
/// <c>McpProtocol</c> owns its writer.</para>
/// </summary>
public static class AgentTaskFrames
{
    /// <summary>
    /// The one serializer configuration both ends use.
    ///
    /// <para>Unknown members are ignored, which is the property that lets a version 2 server add a
    /// field without breaking a version 2 agent that predates it. Nulls are omitted so an absent
    /// optional reads as absent rather than as an explicit null.</para>
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A registered capability name: lowercase dotted segments, such as <c>component.scan</c>.
    ///
    /// <para>The shape is the point. A name that cannot contain a space, a slash, a quote or an
    /// argument cannot smuggle a command line through a field the receiver is meant to treat as an
    /// identifier, so "the lease says what, never how" is enforced by the parser rather than by
    /// everyone remembering it. At least two segments, so a bare word is not a capability.</para>
    /// </summary>
    public static bool IsValidCapabilityName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;

        string[] segments = name.Split('.');
        if (segments.Length < 2) return false;

        foreach (string segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetterLower(segment[0])) return false;

            foreach (char c in segment)
            {
                if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds a task frame, or explains why it cannot be sent. Never throws on an oversized body.
    ///
    /// <para>This is the only supported way to produce a task frame. Going around it reaches
    /// <see cref="AgentProtocol.Encode(AgentFrameType, uint, ReadOnlySpan{byte})"/>, which throws
    /// at 64 KiB — on the sender, after the work is already done, and with no way to tell the peer
    /// what happened.</para>
    /// </summary>
    /// <returns>True when <paramref name="frame"/> is ready to send; false with <paramref name="refusal"/> set.</returns>
    public static bool TryEncode<T>(
        AgentFrameType type, T payload, out byte[] frame, out string? refusal)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);

        if (body.Length > AgentProtocol.MaxTaskPayloadSize)
        {
            frame = [];
            refusal =
                $"A {type} payload of {body.Length} bytes exceeds the "
                + $"{AgentProtocol.MaxTaskPayloadSize} byte task limit. Send bulk output as "
                + $"{nameof(AgentFrameType.Progress)} frames and keep the "
                + $"{nameof(AgentFrameType.Result)} to a verdict.";
            return false;
        }

        // StreamId 0: task frames belong to the link, not to a multiplexed TCP stream. The
        // counter that allocates stream ids increments from 0, so 0 is never a real stream.
        frame = AgentProtocol.Encode(type, 0, body);
        refusal = null;
        return true;
    }

    /// <summary>
    /// Builds a task frame, throwing if it does not fit. For payloads whose size is bounded by
    /// construction — an ack, a cancel, a verdict — where a refusal path would be unreachable code.
    /// </summary>
    public static byte[] Encode<T>(AgentFrameType type, T payload)
        => TryEncode(type, payload, out byte[] frame, out string? refusal)
            ? frame
            : throw new ArgumentOutOfRangeException(nameof(payload), refusal);

    /// <summary>
    /// Reads a task frame's payload.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The payload is not the JSON this frame type carries. Thrown rather than returning null
    /// because, unlike an unrecognised frame <em>type</em>, a frame that claims a type and
    /// contradicts it is a peer that is wrong rather than merely newer.
    /// </exception>
    public static T Decode<T>(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload.Span, Json)
                ?? throw new InvalidDataException($"A {typeof(T).Name} frame carried a null body.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"A {typeof(T).Name} frame carried a body that is not valid JSON for it: {ex.Message}", ex);
        }
    }
}
