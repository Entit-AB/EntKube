using System.Buffers.Binary;
using System.Text;

namespace EntKube.Agents.Protocol;

/// <summary>
/// Frame types on the agent link. One WebSocket binary message carries exactly
/// one frame.
///
/// <para><b>0x01–0x06 are the tunnel.</b> They have been on the wire since the link
/// shipped and every deployed agent understands them.</para>
///
/// <para><b>0x07–0x0c are task frames</b> (docs/decomposition.md §5.2). They may only be
/// sent to a peer that declared protocol <see cref="AgentProtocol.ProtocolVersion"/> 2 or
/// above on the handshake — see <see cref="AgentProtocol.RequiredVersion"/>. An agent that
/// predates them declares nothing, is treated as version 1, and keeps working as a pure
/// tunnel for as long as it is deployed.</para>
/// </summary>
public enum AgentFrameType : byte
{
    /// <summary>Server → agent: open a TCP connection. Payload is "host:port".</summary>
    Open = 0x01,

    /// <summary>Agent → server: result of an <see cref="Open"/>. Payload is a status byte then an optional UTF-8 reason.</summary>
    OpenAck = 0x02,

    /// <summary>Either direction: payload bytes for an established stream.</summary>
    Data = 0x03,

    /// <summary>Either direction: the stream is finished. Payload is an optional UTF-8 reason.</summary>
    Close = 0x04,

    /// <summary>Server → agent liveness probe. Stream id is unused.</summary>
    Ping = 0x05,

    /// <summary>Agent → server response to <see cref="Ping"/>.</summary>
    Pong = 0x06,

    /// <summary>
    /// Server → agent: one unit of work, with a deadline. Payload is a
    /// <see cref="LeasePayload"/> as UTF-8 JSON; <c>StreamId</c> is 0.
    /// </summary>
    Lease = 0x07,

    /// <summary>
    /// Agent → server: accepted, or refused with a reason. Payload is a
    /// <see cref="LeaseAckPayload"/> as UTF-8 JSON; <c>StreamId</c> is 0.
    /// </summary>
    LeaseAck = 0x08,

    /// <summary>
    /// Agent → server: partial output, and a heartbeat against the deadline. Payload is a
    /// <see cref="ProgressPayload"/> as UTF-8 JSON; <c>StreamId</c> is 0.
    ///
    /// <para>This is also the <b>only</b> bulk channel for a task. A <see cref="Result"/> is a
    /// verdict, not a body — see <see cref="AgentProtocol.MaxTaskPayloadSize"/>.</para>
    /// </summary>
    Progress = 0x09,

    /// <summary>
    /// Agent → server: terminal. Payload is a <see cref="ResultPayload"/> as UTF-8 JSON;
    /// <c>StreamId</c> is 0.
    /// </summary>
    Result = 0x0a,

    /// <summary>
    /// Agent → server, once per link after the handshake: what this agent can do, and its
    /// version. Payload is a <see cref="CapabilityPayload"/> as UTF-8 JSON; <c>StreamId</c> is 0.
    ///
    /// <para>⚠️ This frame does <b>not</b> negotiate the protocol version and cannot: the version
    /// has to be known before any frame above 0x06 is sent, and an agent that predates this
    /// vocabulary never sends one. The handshake settles the version (<see cref="ProtocolHeader"/>);
    /// this frame narrows what may be dispatched over a link already known to speak version 2.</para>
    /// </summary>
    Capability = 0x0b,

    /// <summary>
    /// Server → agent: stop working on this lease. Payload is a
    /// <see cref="LeaseCancelPayload"/> as UTF-8 JSON; <c>StreamId</c> is 0.
    ///
    /// <para><b>Why this is here and not deferred.</b> §5.2 lists five frames and none of them
    /// cancels, yet the <see cref="Lease"/> carries a deadline — so there is a moment when the
    /// server has given up and the agent has not been told. The tunnel half of this protocol has
    /// <see cref="Close"/> in either direction for exactly that reason. Adding a frame type later
    /// is a flag day against a customer-run binary EntKube cannot upgrade, so the cheap moment to
    /// add this one is the moment the others arrive.</para>
    /// </summary>
    LeaseCancel = 0x0c,
}

/// <summary>
/// One frame on the agent link: a type, the stream it belongs to, and a payload.
/// </summary>
/// <param name="Type">
/// What the frame does. May be a value this build does not define — see
/// <see cref="AgentProtocol.IsKnown"/> — because a peer may be newer than this binary.
/// </param>
/// <param name="StreamId">Which multiplexed stream it belongs to; 0 for link-level and task frames.</param>
/// <param name="Payload">Frame body, empty for frames that carry none.</param>
public readonly record struct AgentFrame(AgentFrameType Type, uint StreamId, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Wire format for the EntKube egress agent link.
///
/// The link multiplexes many TCP streams over a single outbound WebSocket. Only
/// the agent ever dials out to EntKube; EntKube never connects to the agent's
/// network, which is the entire point — a customer network that permits no
/// inbound traffic can still be reached this way.
///
/// Tunnel frames carry opaque bytes. TLS is negotiated end-to-end between EntKube and
/// the ultimate destination, so the agent relays ciphertext it cannot read, and
/// certificate validation and request signing at the EntKube end still apply to
/// the real endpoint.
///
/// Layout: [type:1][streamId:4 big-endian][payload:remainder]
///
/// <para><b>One assembly, two fleets.</b> Both ends reference this project, so the two
/// <em>sources</em> cannot drift. The two <em>deployments</em> drift constantly: the agent ships as
/// a self-contained binary a customer runs under systemd, with no update path, so a server is
/// routinely newer than the agents talking to it and the compiler can never warn about the gap.
/// Every frame this code sends must therefore be justified by something the <b>live link</b> said
/// — see <see cref="RequiredVersion"/> — never by what this binary's copy of the enum contains.</para>
/// </summary>
public static class AgentProtocol
{
    /// <summary>Bytes of framing that precede every payload.</summary>
    public const int HeaderSize = 5;

    /// <summary>
    /// Largest payload a single Data frame may carry. Bounded so one stream
    /// cannot monopolise the link and so a hostile peer cannot force a huge
    /// allocation; the stream layer splits anything larger.
    ///
    /// <para>⚠️ Effectively immutable. It is one const with six readers, but raising it is a flag
    /// day: a new server sending a larger frame trips the reassembly guard on every deployed agent
    /// and drops the link. Anything that needs a bigger budget needs a different mechanism, not a
    /// bigger number.</para>
    /// </summary>
    public const int MaxPayloadSize = 64 * 1024;

    /// <summary>
    /// Largest JSON body a task frame may carry — a quarter of <see cref="MaxPayloadSize"/>, so
    /// framing overhead can never be the thing that trips the limit.
    ///
    /// <para><b>Why a second, smaller bound.</b> The frame cap is enforced by throwing: on the
    /// sender <see cref="Encode(AgentFrameType, uint, ReadOnlySpan{byte})"/> throws, and on the
    /// receiver an over-length message tears down the whole link — every unrelated TCP stream with
    /// it. That is an acceptable answer to a corrupt peer and a terrible answer to a task whose
    /// output grew. So a task checks this bound <em>before</em> it builds a frame and reports
    /// <see cref="LeaseOutcome.PayloadTooLarge"/>, which is a message that is always encodable.</para>
    /// </summary>
    public const int MaxTaskPayloadSize = 16 * 1024;

    /// <summary>
    /// The protocol this build speaks. 1 is tunnel-only (<see cref="AgentFrameType.Open"/> through
    /// <see cref="AgentFrameType.Pong"/>); 2 adds the task frames.
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>What a peer that declares nothing is assumed to speak.</summary>
    public const int TunnelOnlyVersion = 1;

    /// <summary>Header value carrying the enrolment token on the WebSocket handshake.</summary>
    public const string TokenHeader = "X-EntKube-Agent-Token";

    /// <summary>Header the agent reports its willing destinations on. Informational; its local config is authoritative.</summary>
    public const string AllowlistHeader = "X-EntKube-Agent-Allowlist";

    /// <summary>
    /// Header each end declares its protocol version on: the agent on the request, EntKube on the
    /// 101 response.
    ///
    /// <para><b>Why the handshake and not a frame.</b> <see cref="Decode"/> used to throw on an
    /// unrecognised type byte, and neither receive loop survived that throw, so the first task
    /// frame ever sent would have dropped a live tunnel and every stream on it. Decode is tolerant
    /// now, but that only helps peers built after this change — the agents already deployed are
    /// strict and cannot be upgraded. A handshake header is the one channel an old peer ignores
    /// harmlessly in both directions, and it leaves no window: the version is known before the
    /// socket carries a single frame, where a Capability frame would race the first dispatch.</para>
    /// </summary>
    public const string ProtocolHeader = "X-EntKube-Agent-Protocol";

    /// <summary>Path the agent connects to.</summary>
    public const string EndpointPath = "/agent/connect";

    /// <summary>True when this build defines <paramref name="type"/>. False means the peer is newer.</summary>
    public static bool IsKnown(AgentFrameType type) => Enum.IsDefined(type);

    /// <summary>True for the task vocabulary (0x07 and above), false for the tunnel.</summary>
    public static bool IsTaskFrame(AgentFrameType type) => (byte)type > (byte)AgentFrameType.Pong;

    /// <summary>
    /// The lowest protocol version a peer must declare before this frame type may be sent to it.
    /// The single rule both ends consult, so "do not send a task frame to a tunnel-only agent" is
    /// a function rather than a convention someone has to remember.
    /// </summary>
    public static int RequiredVersion(AgentFrameType type)
        => IsTaskFrame(type) ? 2 : TunnelOnlyVersion;

    /// <summary>
    /// Reads a peer's declared version from its handshake header. Anything absent, blank,
    /// unparsable or below 1 is <see cref="TunnelOnlyVersion"/> — the safe reading, because the
    /// peers that declare nothing are precisely the ones that cannot survive a task frame.
    /// </summary>
    public static int ParsePeerVersion(string? header)
        => int.TryParse(header, out int version) && version >= TunnelOnlyVersion
            ? version
            : TunnelOnlyVersion;

    /// <summary>Encodes a frame into a newly allocated buffer.</summary>
    public static byte[] Encode(AgentFrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Frame payload of {payload.Length} bytes exceeds the {MaxPayloadSize} byte limit.");
        }

        byte[] buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1, 4), streamId);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    /// <summary>Encodes a frame whose payload is UTF-8 text.</summary>
    public static byte[] Encode(AgentFrameType type, uint streamId, string payload)
        => Encode(type, streamId, Encoding.UTF8.GetBytes(payload));

    /// <summary>Encodes a frame with no payload.</summary>
    public static byte[] Encode(AgentFrameType type, uint streamId)
        => Encode(type, streamId, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Decodes a frame. Throws on malformed <em>framing</em> rather than guessing — the peer is
    /// authenticated but a desynchronised link must fail loudly instead of silently mis-routing
    /// bytes between streams.
    ///
    /// <para><b>An unrecognised frame <em>type</em> is not malformed framing, and is no longer an
    /// error.</b> It used to be, and that made every future widening of this enum a flag day: one
    /// unknown byte unwound the receive loop, tore down the link and took every unrelated TCP
    /// stream with it, on a binary EntKube cannot upgrade. Tolerance is safe here precisely because
    /// framing stays strict — one WebSocket message carries exactly one frame, the header is a
    /// fixed five bytes and the payload is the remainder, so an unknown type cannot desynchronise
    /// anything. The receiver decides what to do with it; both ends log and ignore.</para>
    /// </summary>
    public static AgentFrame Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"Agent frame of {message.Length} bytes is shorter than the {HeaderSize} byte header.");
        }

        return new AgentFrame(
            (AgentFrameType)message.Span[0],
            BinaryPrimitives.ReadUInt32BigEndian(message.Span.Slice(1, 4)),
            message[HeaderSize..]);
    }

    /// <summary>Renders an Open frame's target.</summary>
    public static string FormatTarget(string host, int port) => $"{host}:{port}";

    /// <summary>
    /// Parses an Open frame's target back into host and port.
    /// Splits on the last colon so IPv6 literals survive.
    /// </summary>
    public static (string Host, int Port) ParseTarget(string target)
    {
        int separator = target.LastIndexOf(':');

        if (separator <= 0 || !int.TryParse(target[(separator + 1)..], out int port) || port is < 1 or > 65535)
        {
            throw new InvalidDataException($"'{target}' is not a valid host:port target.");
        }

        return (target[..separator], port);
    }

    /// <summary>Builds the payload for a successful OpenAck.</summary>
    public static byte[] EncodeOpenAck(uint streamId, bool success, string? error = null)
    {
        byte[] reason = error is null ? [] : Encoding.UTF8.GetBytes(error);
        byte[] payload = new byte[1 + reason.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        reason.CopyTo(payload, 1);
        return Encode(AgentFrameType.OpenAck, streamId, payload);
    }

    /// <summary>Reads an OpenAck payload.</summary>
    public static (bool Success, string? Error) DecodeOpenAck(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new InvalidDataException("OpenAck frame carried no status byte.");
        }

        bool success = payload.Span[0] == 1;
        string? error = payload.Length > 1 ? Encoding.UTF8.GetString(payload.Span[1..]) : null;
        return (success, error);
    }
}
