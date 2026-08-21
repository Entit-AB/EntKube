using System.Buffers.Binary;
using System.Text;

namespace EntKube.Agents.Protocol;

/// <summary>
/// Frame types on the agent link. One WebSocket binary message carries exactly
/// one frame.
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
    Pong = 0x06
}

/// <summary>
/// One frame on the agent link: a type, the stream it belongs to, and a payload.
/// </summary>
/// <param name="Type">What the frame does.</param>
/// <param name="StreamId">Which multiplexed stream it belongs to; 0 for link-level frames.</param>
/// <param name="Payload">Frame body, empty for frames that carry none.</param>
public readonly record struct AgentFrame(AgentFrameType Type, uint StreamId, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Wire format for the EntKube egress agent link, shared verbatim by both ends
/// (the agent project compiles this same file) so the two cannot drift apart.
///
/// The link multiplexes many TCP streams over a single outbound WebSocket. Only
/// the agent ever dials out to EntKube; EntKube never connects to the agent's
/// network, which is the entire point — a customer network that permits no
/// inbound traffic can still be reached this way.
///
/// Frames carry opaque bytes. TLS is negotiated end-to-end between EntKube and
/// the ultimate destination, so the agent relays ciphertext it cannot read, and
/// certificate validation and request signing at the EntKube end still apply to
/// the real endpoint.
///
/// Layout: [type:1][streamId:4 big-endian][payload:remainder]
/// </summary>
public static class AgentProtocol
{
    /// <summary>Bytes of framing that precede every payload.</summary>
    public const int HeaderSize = 5;

    /// <summary>
    /// Largest payload a single Data frame may carry. Bounded so one stream
    /// cannot monopolise the link and so a hostile peer cannot force a huge
    /// allocation; the stream layer splits anything larger.
    /// </summary>
    public const int MaxPayloadSize = 64 * 1024;

    /// <summary>Header value carrying the enrolment token on the WebSocket handshake.</summary>
    public const string TokenHeader = "X-EntKube-Agent-Token";

    /// <summary>Path the agent connects to.</summary>
    public const string EndpointPath = "/agent/connect";

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
    /// Decodes a frame. Throws on anything malformed rather than guessing — the
    /// peer is authenticated but a desynchronised link must fail loudly instead
    /// of silently mis-routing bytes between streams.
    /// </summary>
    public static AgentFrame Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"Agent frame of {message.Length} bytes is shorter than the {HeaderSize} byte header.");
        }

        byte rawType = message.Span[0];

        if (!Enum.IsDefined(typeof(AgentFrameType), rawType))
        {
            throw new InvalidDataException($"Unknown agent frame type 0x{rawType:x2}.");
        }

        return new AgentFrame(
            (AgentFrameType)rawType,
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
