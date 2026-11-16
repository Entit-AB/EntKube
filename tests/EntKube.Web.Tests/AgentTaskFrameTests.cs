using System.Text;
using System.Text.Json;
using EntKube.Agents.Protocol;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The task vocabulary on the wire (docs/decomposition.md §5.2).
///
/// <para><b>What breaking any of this costs.</b> The agent ships as a self-contained binary a
/// customer runs under systemd. EntKube cannot upgrade it, cannot see its version except through
/// the handshake, and cannot recover a link it has dropped — an agent that disagrees with the
/// server about the wire reconnects, disagrees again, and flaps until someone logs into the
/// customer's box. So these tests pin the bytes and the version rules literally rather than
/// comparing two declarations to each other, which would prove only that this build agrees with
/// itself.</para>
/// </summary>
public class AgentTaskFrameTests
{
    // ──────── Frame numbering ────────

    [Theory]
    [InlineData(AgentFrameType.Open, 0x01)]
    [InlineData(AgentFrameType.OpenAck, 0x02)]
    [InlineData(AgentFrameType.Data, 0x03)]
    [InlineData(AgentFrameType.Close, 0x04)]
    [InlineData(AgentFrameType.Ping, 0x05)]
    [InlineData(AgentFrameType.Pong, 0x06)]
    [InlineData(AgentFrameType.Lease, 0x07)]
    [InlineData(AgentFrameType.LeaseAck, 0x08)]
    [InlineData(AgentFrameType.Progress, 0x09)]
    [InlineData(AgentFrameType.Result, 0x0a)]
    [InlineData(AgentFrameType.Capability, 0x0b)]
    [InlineData(AgentFrameType.LeaseCancel, 0x0c)]
    public void Every_frame_type_keeps_the_byte_it_shipped_with(AgentFrameType type, byte expected)
    {
        // Reordering the enum would renumber the wire for every deployed agent at once, and
        // nothing else in the build would notice.
        ((byte)type).Should().Be(expected);
    }

    // ──────── Version negotiation ────────

    [Theory]
    [InlineData(AgentFrameType.Open)]
    [InlineData(AgentFrameType.Data)]
    [InlineData(AgentFrameType.Close)]
    [InlineData(AgentFrameType.Ping)]
    [InlineData(AgentFrameType.Pong)]
    public void A_tunnel_frame_may_be_sent_to_any_agent(AgentFrameType type)
    {
        AgentProtocol.RequiredVersion(type).Should().Be(AgentProtocol.TunnelOnlyVersion);
        AgentProtocol.IsTaskFrame(type).Should().BeFalse();
    }

    [Theory]
    [InlineData(AgentFrameType.Lease)]
    [InlineData(AgentFrameType.LeaseAck)]
    [InlineData(AgentFrameType.Progress)]
    [InlineData(AgentFrameType.Result)]
    [InlineData(AgentFrameType.Capability)]
    [InlineData(AgentFrameType.LeaseCancel)]
    public void A_task_frame_needs_a_peer_that_declared_version_two(AgentFrameType type)
    {
        AgentProtocol.RequiredVersion(type).Should().Be(2);
        AgentProtocol.IsTaskFrame(type).Should().BeTrue();
        AgentProtocol.ProtocolVersion.Should().BeGreaterThanOrEqualTo(
            AgentProtocol.RequiredVersion(type), "this build must be able to speak what it defines");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("one")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("2.0")]
    public void A_peer_that_declares_nothing_usable_is_read_as_a_tunnel(string? header)
    {
        // The agents that declare nothing are exactly the ones that predate the task frames and
        // cannot survive one, so every unreadable answer has to fall that way.
        AgentProtocol.ParsePeerVersion(header).Should().Be(AgentProtocol.TunnelOnlyVersion);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("7", 7)]
    public void A_declared_version_is_taken_at_its_word(string header, int expected)
        => AgentProtocol.ParsePeerVersion(header).Should().Be(expected);

    [Fact]
    public void The_handshake_header_names_are_the_ones_deployed_agents_send()
    {
        // Renaming either of these silently stops every deployed agent being understood.
        AgentProtocol.ProtocolHeader.Should().Be("X-EntKube-Agent-Protocol");
        AgentProtocol.AllowlistHeader.Should().Be("X-EntKube-Agent-Allowlist");
        AgentProtocol.TokenHeader.Should().Be("X-EntKube-Agent-Token");
    }

    // ──────── Unknown types decode; bad framing does not ────────

    [Fact]
    public void A_frame_type_this_build_does_not_know_decodes_instead_of_killing_the_link()
    {
        // This replaced a test that asserted the opposite, and the replacement is the point:
        // throwing here unwound the receive loop, tore down the link and took every multiplexed
        // TCP stream with it — over a frame that is merely newer than this binary.
        AgentFrame frame = AgentProtocol.Decode(new byte[] { 0xff, 0, 0, 0, 9, 1, 2, 3 });

        AgentProtocol.IsKnown(frame.Type).Should().BeFalse();
        ((byte)frame.Type).Should().Be(0xff);
        frame.StreamId.Should().Be(9u);
        frame.Payload.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void A_frame_shorter_than_its_header_is_still_rejected()
    {
        // Framing stays strict. An unknown type cannot desynchronise anything — one WebSocket
        // message is one frame — but a truncated header means the link is mis-reading bytes.
        Action act = () => AgentProtocol.Decode(new byte[] { 0x07, 0, 0, 0 });

        act.Should().Throw<InvalidDataException>().WithMessage("*header*");
    }

    [Fact]
    public void Every_defined_frame_type_is_known()
    {
        foreach (AgentFrameType type in Enum.GetValues<AgentFrameType>())
        {
            AgentProtocol.IsKnown(type).Should().BeTrue($"{type} is defined by this build");
        }
    }

    // ──────── Wire layout ────────

    [Fact]
    public void A_lease_is_one_frame_of_json_on_the_link_itself()
    {
        Guid leaseId = new("8f1d4a6c-0000-4000-8000-000000000001");

        byte[] frame = AgentTaskFrames.Encode(
            AgentFrameType.Lease, new LeasePayload(leaseId, "component.scan", 30_000));

        frame[0].Should().Be(0x07);

        // StreamId 0: a lease belongs to the link, not to a tunnelled TCP stream. The stream
        // counter increments from 0, so 0 can never be a real stream and cannot collide.
        frame[1..5].Should().Equal(0, 0, 0, 0);

        // Pinned as literal JSON rather than by round-tripping, because a round trip only proves
        // this build agrees with itself — and the peer is a binary built months ago.
        Encoding.UTF8.GetString(frame[AgentProtocol.HeaderSize..]).Should().Be(
            """{"LeaseId":"8f1d4a6c-0000-4000-8000-000000000001","Capability":"component.scan","DeadlineMs":30000}""");
    }

    [Fact]
    public void An_optional_field_that_is_absent_is_omitted_rather_than_sent_as_null()
    {
        byte[] frame = AgentTaskFrames.Encode(
            AgentFrameType.LeaseAck, new LeaseAckPayload(Guid.Empty, Accepted: true));

        Encoding.UTF8.GetString(frame[AgentProtocol.HeaderSize..])
            .Should().Be("""{"LeaseId":"00000000-0000-0000-0000-000000000000","Accepted":true}""");
    }

    [Fact]
    public void A_refusal_carries_its_reason_to_the_other_end()
    {
        byte[] frame = AgentTaskFrames.Encode(
            AgentFrameType.LeaseAck,
            new LeaseAckPayload(Guid.Empty, Accepted: false, "this agent implements no capabilities"));

        LeaseAckPayload read = AgentTaskFrames.Decode<LeaseAckPayload>(
            frame.AsMemory(AgentProtocol.HeaderSize));

        read.Accepted.Should().BeFalse();
        read.Reason.Should().Be("this agent implements no capabilities");
    }

    [Fact]
    public void A_result_survives_the_buffer_it_was_read_from()
    {
        // The output is a JsonElement, which can be a window onto a document someone else owns.
        // If it were, the answer would decay into garbage the moment the frame buffer was reused.
        byte[] frame = AgentTaskFrames.Encode(
            AgentFrameType.Result,
            new ResultPayload(
                Guid.Empty, LeaseOutcome.Succeeded, null,
                JsonSerializer.Deserialize<JsonElement>("""{"releases":4}""")));

        byte[] payload = frame[AgentProtocol.HeaderSize..];
        ResultPayload read = AgentTaskFrames.Decode<ResultPayload>(payload);

        Array.Clear(payload);
        Array.Clear(frame);

        read.Output!.Value.GetProperty("releases").GetInt32().Should().Be(4);
    }

    [Fact]
    public void An_outcome_this_build_does_not_define_is_read_rather_than_refused()
    {
        // A newer agent reporting a finer verdict must not break an older server. Anything
        // unrecognised is still a failure to the caller — it is simply not a decode error.
        ResultPayload read = AgentTaskFrames.Decode<ResultPayload>(
            Encoding.UTF8.GetBytes(
                """{"LeaseId":"00000000-0000-0000-0000-000000000000","Outcome":97}"""));

        read.Outcome.Should().NotBe(LeaseOutcome.Succeeded);
        Enum.IsDefined(read.Outcome).Should().BeFalse();
    }

    [Fact]
    public void A_body_that_is_not_the_json_this_frame_carries_is_refused()
    {
        Action act = () => AgentTaskFrames.Decode<LeasePayload>(Encoding.UTF8.GetBytes("not json"));

        act.Should().Throw<InvalidDataException>().WithMessage("*not valid JSON*");
    }

    // ──────── The deadline is a duration ────────

    [Fact]
    public void No_task_payload_puts_an_instant_on_the_wire()
    {
        // The agent runs on a box EntKube does not manage and whose clock it cannot vouch for, so
        // an absolute deadline would need a clock agreement that nothing keeps. A duration also
        // makes a test deterministic: no clock is read at all, on a machine that sleeps mid-run.
        Type[] payloads =
        [
            typeof(LeasePayload), typeof(LeaseAckPayload), typeof(ProgressPayload),
            typeof(ResultPayload), typeof(CapabilityPayload), typeof(LeaseCancelPayload),
        ];

        foreach (Type payload in payloads)
        {
            foreach (var property in payload.GetProperties())
            {
                property.PropertyType.Should().NotBe(
                    typeof(DateTime), $"{payload.Name}.{property.Name} would be an instant");
                property.PropertyType.Should().NotBe(
                    typeof(DateTimeOffset), $"{payload.Name}.{property.Name} would be an instant");
                property.PropertyType.Should().NotBe(
                    typeof(DateTime?), $"{payload.Name}.{property.Name} would be an instant");
            }
        }
    }

    // ──────── Size ────────

    [Fact]
    public void An_oversized_task_payload_is_refused_with_advice_rather_than_thrown()
    {
        // Letting this reach the frame encoder throws on the sender after the work is done; letting
        // it reach the peer trips a reassembly guard that drops the link and every tunnel stream
        // on it. Neither is an acceptable answer to "the output grew".
        JsonElement big = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new string('x', AgentProtocol.MaxTaskPayloadSize)));

        bool encoded = AgentTaskFrames.TryEncode(
            AgentFrameType.Result,
            new ResultPayload(Guid.Empty, LeaseOutcome.Succeeded, null, big),
            out byte[] frame, out string? refusal);

        encoded.Should().BeFalse();
        frame.Should().BeEmpty();
        refusal.Should().Contain("Progress").And.Contain("task limit");
    }

    [Fact]
    public void A_payload_that_fits_encodes()
    {
        bool encoded = AgentTaskFrames.TryEncode(
            AgentFrameType.Progress,
            new ProgressPayload(Guid.Empty, 0, JsonSerializer.Deserialize<JsonElement>("""{"a":1}""")),
            out byte[] frame, out string? refusal);

        encoded.Should().BeTrue();
        refusal.Should().BeNull();
        frame[0].Should().Be(0x09);
    }

    [Fact]
    public void The_task_bound_leaves_room_for_framing_inside_the_frame_bound()
    {
        AgentProtocol.MaxTaskPayloadSize.Should().BeLessThan(
            AgentProtocol.MaxPayloadSize - AgentProtocol.HeaderSize);
    }

    // ──────── A capability names what, never how ────────

    [Theory]
    [InlineData("component.scan")]
    [InlineData("cluster.inventory")]
    [InlineData("a.b")]
    [InlineData("helm.release.list")]
    public void A_dotted_lowercase_name_is_a_capability(string name)
        => AgentTaskFrames.IsValidCapabilityName(name).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scan")]                        // one segment is a word, not a capability
    [InlineData("Component.Scan")]              // case is part of the identity
    [InlineData("component .scan")]
    [InlineData("component.scan; rm -rf /")]    // the shape exists to make this unsayable
    [InlineData("kubectl apply -f -")]
    [InlineData("component..scan")]
    [InlineData("component.")]
    [InlineData(".scan")]
    [InlineData("1.scan")]
    [InlineData("component.scan\n")]
    [InlineData("component.scan/../../etc")]
    public void Anything_that_could_carry_an_instruction_is_not_a_capability(string? name)
    {
        // "What to do, never how." A name that cannot hold a space, a slash or a quote cannot
        // smuggle a command through a field the receiver treats as an identifier — which is the
        // thirty-call-site problem of §5.4.2 arriving on the wire.
        AgentTaskFrames.IsValidCapabilityName(name).Should().BeFalse();
    }

    [Fact]
    public void A_capability_name_cannot_be_unbounded()
        => AgentTaskFrames.IsValidCapabilityName("a." + new string('b', 64)).Should().BeFalse();
}
