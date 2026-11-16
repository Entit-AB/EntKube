using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using EntKube.Agents.Protocol;
using EntKube.Web.Services.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Tests;

/// <summary>
/// What EntKube's end of the link does with a lease.
///
/// <para><b>Why a fake socket rather than the existing harness.</b> <c>EgressAgentLinkTests</c>
/// runs both halves for real — a Kestrel host, a real <c>ClientWebSocket</c> and a loopback
/// listener — which is the right shape for proving bytes survive a round trip and the wrong shape
/// for proving a refusal. It also carries four independent wall-clock deadlines, and this machine
/// sleeps mid-run: the clock jumps while nothing else does. Every test here completes on the
/// test's own action — it pushes the frame it wants to see answered — so no elapsed time is ever
/// asserted.</para>
///
/// <para><b>What these do not cover, stated rather than implied.</b> They pin EntKube's end. The
/// agent's end of the same conversation is pinned by <see cref="AgentCapabilityTests"/> as a pure
/// decision; the socket wiring that connects the two is one line in <c>AgentClient</c> and is
/// covered only by the live round trip in <c>EgressAgentLinkTests</c>.</para>
///
/// <para>⚠️ One guard is deliberately uncovered, and measured as such: <c>AgentClient</c> refuses
/// to answer a <c>Lease</c> from a server that declared tunnel-only. <c>AgentClient.RunAsync</c>
/// constructs its own <c>ClientWebSocket</c> inline and <c>ReceiveLoopAsync</c> takes the concrete
/// type, so there is no seam to drive it through, and reaching it would mean a second live Kestrel
/// harness for one <c>if</c>. The rule it consults — <c>RequiredVersion(LeaseAck) == 2</c> — is
/// pinned in <see cref="AgentTaskFrameTests"/>; that the agent consults it is not. Stated rather
/// than implied, because a mutation run will show it escaping.</para>
/// </summary>
public class AgentLeaseDispatchTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);

    // A backstop so a broken assertion fails the test instead of hanging the suite. It is never
    // a timing assertion: every await below is completed by the test's own next statement.
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_tunnel_only_agent_is_never_sent_a_lease()
    {
        // The whole reason the version is negotiated on the handshake. An agent built before the
        // task vocabulary decodes 0x07, finds nothing that handles it, and — on the strict build
        // every deployed agent is running — tears down the link and every TCP stream on it.
        await using Harness harness = Harness.Start(protocolVersion: 1);

        Func<Task> dispatch = () => harness.Connection.RunLeaseAsync("component.scan", Deadline);

        (await dispatch.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*version 1*leases need 2*");

        harness.Sent.Should().BeEmpty("nothing may reach the socket before the version is checked");
    }

    [Fact]
    public async Task A_capability_name_that_carries_an_instruction_never_reaches_the_socket()
    {
        await using Harness harness = Harness.Start();

        Func<Task> dispatch = () =>
            harness.Connection.RunLeaseAsync("kubectl apply -f -", Deadline);

        (await dispatch.Should().ThrowAsync<ArgumentException>()).WithMessage("*never a command*");

        harness.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lease_the_agent_refuses_comes_back_as_a_refusal_with_its_reason()
    {
        await using Harness harness = Harness.Start();

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);

        LeasePayload lease = await harness.NextSent<LeasePayload>(AgentFrameType.Lease);
        lease.Capability.Should().Be("component.scan");
        lease.DeadlineMs.Should().Be((int)Deadline.TotalMilliseconds);

        harness.Push(
            AgentFrameType.LeaseAck,
            new LeaseAckPayload(lease.LeaseId, Accepted: false, "this agent implements no capabilities"));

        ResultPayload result = await dispatch.WaitAsync(Backstop);

        result.Outcome.Should().Be(LeaseOutcome.Refused);
        result.Reason.Should().Be("this agent implements no capabilities");

        // A refusal is an ordinary answer, not something that poisons the connection — the same
        // property a refused Open has.
        harness.Connection.IsAlive.Should().BeTrue();
    }

    [Fact]
    public async Task A_lease_the_agent_accepts_runs_to_its_result()
    {
        await using Harness harness = Harness.Start();

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);
        LeasePayload lease = await harness.NextSent<LeasePayload>(AgentFrameType.Lease);

        harness.Connection.ActiveLeases.Should().Be(1);

        harness.Push(AgentFrameType.LeaseAck, new LeaseAckPayload(lease.LeaseId, Accepted: true));
        harness.Push(
            AgentFrameType.Result,
            new ResultPayload(
                lease.LeaseId, LeaseOutcome.Succeeded, null,
                JsonSerializer.Deserialize<JsonElement>("""{"releases":7}""")));

        ResultPayload result = await dispatch.WaitAsync(Backstop);

        result.Outcome.Should().Be(LeaseOutcome.Succeeded);
        result.Output!.Value.GetProperty("releases").GetInt32().Should().Be(7);
        harness.Connection.ActiveLeases.Should().Be(0, "the lease is no longer in flight");
    }

    [Fact]
    public async Task Partial_output_reaches_the_caller_in_the_order_it_arrived()
    {
        await using Harness harness = Harness.Start();

        List<int> chunks = [];
        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync(
            "component.scan", Deadline,
            onProgress: p => { chunks.Add(p.Sequence); return Task.CompletedTask; });

        LeasePayload lease = await harness.NextSent<LeasePayload>(AgentFrameType.Lease);
        harness.Push(AgentFrameType.LeaseAck, new LeaseAckPayload(lease.LeaseId, Accepted: true));

        for (int sequence = 0; sequence < 3; sequence++)
        {
            harness.Push(
                AgentFrameType.Progress,
                new ProgressPayload(
                    lease.LeaseId, sequence,
                    JsonSerializer.Deserialize<JsonElement>($$"""{"n":{{sequence}}}""")));
        }

        harness.Push(
            AgentFrameType.Result, new ResultPayload(lease.LeaseId, LeaseOutcome.Succeeded));

        await dispatch.WaitAsync(Backstop);

        chunks.Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task A_progress_handler_that_throws_does_not_take_the_link_down()
    {
        // The handler belongs to a caller. If it could kill the link it would also kill every
        // tunnelled TCP connection sharing it, which is a blast radius no callback should have.
        await using Harness harness = Harness.Start();

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync(
            "component.scan", Deadline,
            onProgress: _ => throw new InvalidOperationException("the caller's handler is broken"));

        LeasePayload lease = await harness.NextSent<LeasePayload>(AgentFrameType.Lease);
        harness.Push(AgentFrameType.LeaseAck, new LeaseAckPayload(lease.LeaseId, Accepted: true));
        harness.Push(
            AgentFrameType.Progress,
            new ProgressPayload(lease.LeaseId, 0, JsonSerializer.Deserialize<JsonElement>("1")));
        harness.Push(
            AgentFrameType.Result, new ResultPayload(lease.LeaseId, LeaseOutcome.Succeeded));

        (await dispatch.WaitAsync(Backstop)).Outcome.Should().Be(LeaseOutcome.Succeeded);
        harness.Connection.IsAlive.Should().BeTrue();
    }

    [Fact]
    public async Task A_frame_this_build_does_not_handle_is_reported_and_leaves_the_link_working()
    {
        await using Harness harness = Harness.Start();

        // A server newer than this one. The old behaviour was to throw out of the receive loop,
        // which ended the link — the single thing that made adding a frame type a flag day.
        harness.PushRaw(AgentProtocol.Encode((AgentFrameType)0xfe, 0, "from the future"));
        await harness.Settled();

        // Reported, not merely survived. Neither frame switch had a default arm, so an unfamiliar
        // frame decoded and then vanished — the quietest possible version-skew failure, and the
        // one an operator would have no way to diagnose.
        harness.Logged.Should().ContainMatch("*0xfe*does not handle*");

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);
        LeasePayload lease = await harness.NextSent<LeasePayload>(AgentFrameType.Lease);

        harness.Push(AgentFrameType.LeaseAck, new LeaseAckPayload(lease.LeaseId, Accepted: true));
        harness.Push(AgentFrameType.Result, new ResultPayload(lease.LeaseId, LeaseOutcome.Succeeded));

        (await dispatch.WaitAsync(Backstop)).Outcome.Should().Be(LeaseOutcome.Succeeded);
    }

    [Fact]
    public async Task What_the_agent_declared_narrows_what_this_process_will_dispatch()
    {
        await using Harness harness = Harness.Start();

        harness.Push(
            AgentFrameType.Capability,
            new CapabilityPayload(2, "1.2.3", ["cluster.inventory"]));

        await harness.Settled();

        harness.Connection.DeclaredCapabilities!.AgentVersion.Should().Be("1.2.3");

        Func<Task> dispatch = () => harness.Connection.RunLeaseAsync("component.scan", Deadline);

        (await dispatch.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*did not declare 'component.scan'*cluster.inventory*");
    }

    [Fact]
    public async Task An_agent_that_has_not_declared_yet_is_not_narrowed_into_uselessness()
    {
        // A declaration may narrow; its absence may not. Treating "has not said yet" as "can do
        // nothing" would lose every lease dispatched in the window after the handshake.
        await using Harness harness = Harness.Start();

        harness.Connection.DeclaredCapabilities.Should().BeNull();

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);

        (await harness.NextSent<LeasePayload>(AgentFrameType.Lease)).Capability
            .Should().Be("component.scan");

        harness.Push(
            AgentFrameType.LeaseAck, new LeaseAckPayload(Guid.Empty, Accepted: false, "no"));
        harness.Complete();
        await dispatch.WaitAsync(Backstop);
    }

    [Fact]
    public async Task A_link_that_drops_mid_lease_tells_the_caller_rather_than_hanging()
    {
        // There is no durable lease store yet (item 11), so a dropped link is the end of the
        // lease. The caller has to be told that, not left waiting out a deadline.
        await using Harness harness = Harness.Start();

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);
        LeasePayload lease = await harness.NextSent<LeasePayload>(AgentFrameType.Lease);
        harness.Push(AgentFrameType.LeaseAck, new LeaseAckPayload(lease.LeaseId, Accepted: true));

        harness.Complete();

        ResultPayload result = await dispatch.WaitAsync(Backstop);

        result.Outcome.Should().Be(LeaseOutcome.Failed);

        // ⚠️ This used to assert only Contain("link closed"), which BOTH exits from a teardown
        // emit — so it could not tell them apart and a mutation that removed the lease resolution
        // entirely still passed. Pinned to the exact reason now.
        result.Reason.Should().Be("link closed");
    }

    [Fact]
    public async Task A_link_that_drops_before_the_agent_answers_is_not_reported_as_a_refusal()
    {
        // The bug: TearDownAsync resolves the ack waiter with a synthetic
        // LeaseAckPayload(Accepted: false), which read as the agent declining the work. Refused
        // means "the agent will not do this" — a configuration answer an operator goes and acts
        // on — and the agent never saw this lease at all. It was not even consistently wrong:
        // the caller has two exits for one event, and which won depended on scheduling, except
        // when teardown finished first, where WaitAsync returns the completed task before it
        // looks at the token and the refusal won every time.
        await using Harness harness = Harness.Start();

        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);
        await harness.NextSent<LeasePayload>(AgentFrameType.Lease);

        // No ack. The link simply goes away while the dispatch is waiting for one.
        harness.Complete();

        ResultPayload result = await dispatch.WaitAsync(Backstop);

        result.Outcome.Should().NotBe(
            LeaseOutcome.Refused, "the agent never answered — it cannot have refused");
        result.Outcome.Should().Be(LeaseOutcome.Failed);
        result.Reason.Should().Be("link closed");
    }

    [Fact]
    public async Task A_capability_frame_with_no_capability_list_does_not_take_the_link_down()
    {
        // System.Text.Json does not enforce non-nullable reference types, so a frame that omits
        // the field decodes cleanly and leaves a null behind. Reading .Count off it threw a
        // NullReferenceException straight through a catch that named only InvalidDataException,
        // out of the receive loop, into TearDownAsync — killing every multiplexed TCP stream on
        // the link over one malformed frame from an authenticated but buggy agent. That is the
        // precise outcome this whole change exists to prevent.
        await using Harness harness = Harness.Start();

        harness.Push(AgentFrameType.Capability, new CapabilityPayload(2, "1.2.3", null));
        await harness.Settled();

        harness.Connection.IsAlive.Should().BeTrue();
        harness.Connection.DeclaredCapabilities!.AgentVersion.Should().Be("1.2.3");

        // "Has not said" is the honest reading of a peer that did not say, so it must not narrow.
        Task<ResultPayload> dispatch = harness.Connection.RunLeaseAsync("component.scan", Deadline);
        (await harness.NextSent<LeasePayload>(AgentFrameType.Lease)).Capability
            .Should().Be("component.scan");

        harness.Complete();
        await dispatch.WaitAsync(Backstop);
    }

    [Fact]
    public async Task A_frame_whose_body_is_nonsense_does_not_take_the_link_down()
    {
        // The general form of the same rule: framing is strict, handling is not. Any failure
        // while handling one frame is that frame's problem, never the link's.
        await using Harness harness = Harness.Start();

        harness.PushRaw(AgentProtocol.Encode(AgentFrameType.Capability, 0, "{not json at all"));
        harness.PushRaw(AgentProtocol.Encode(AgentFrameType.Result, 0, "[]"));
        harness.PushRaw(AgentProtocol.Encode(AgentFrameType.Progress, 0, ""));
        await harness.Settled();

        harness.Connection.IsAlive.Should().BeTrue();
    }

    [Fact]
    public async Task A_result_for_a_lease_nobody_is_waiting_on_does_not_disturb_the_link()
    {
        await using Harness harness = Harness.Start();

        harness.Push(
            AgentFrameType.Result, new ResultPayload(Guid.NewGuid(), LeaseOutcome.Succeeded));

        await harness.Settled();

        harness.Connection.IsAlive.Should().BeTrue();
        harness.Connection.ActiveLeases.Should().Be(0);
    }

    [Fact]
    public async Task A_lease_needs_a_deadline_to_count_down_from()
    {
        await using Harness harness = Harness.Start();

        Func<Task> dispatch = () => harness.Connection.RunLeaseAsync("component.scan", TimeSpan.Zero);

        (await dispatch.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .WithMessage("*deadline*");
    }

    // ──────── Running out of patience ────────

    [Fact]
    public void An_agent_that_never_acknowledged_is_not_reported_as_having_run_out_of_time()
    {
        // The bug this replaced: one catch served both of RunLeaseAsync's waits and reported the
        // second one's budget for either, so a lease the agent never acknowledged inside 30s came
        // back as "sent no verdict within 70s" — a duration that had not elapsed, describing a
        // wait that had not happened, and pointing an operator at the agent's work rather than at
        // the agent's silence.
        ResultPayload silent = AgentConnection.DescribeTimeout(
            Guid.Empty, Guid.Empty, acknowledged: false,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(70));

        silent.Outcome.Should().Be(LeaseOutcome.Failed);
        silent.Reason.Should().Contain("did not acknowledge").And.Contain("30s");
        silent.Reason.Should().NotContain("70", "that budget was never spent");
    }

    [Fact]
    public void An_agent_that_accepted_and_went_quiet_is_reported_against_its_own_deadline()
    {
        ResultPayload quiet = AgentConnection.DescribeTimeout(
            Guid.Empty, Guid.Empty, acknowledged: true,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(70));

        quiet.Outcome.Should().Be(LeaseOutcome.Expired);
        quiet.Reason.Should().Contain("accepted").And.Contain("70s");
    }

    [Fact]
    public void The_two_timeouts_are_different_answers()
    {
        // Asserted against each other as well as individually: a change that collapsed both
        // branches into one answer would still pass the two tests above if their expectations
        // happened to drift together.
        ResultPayload silent = AgentConnection.DescribeTimeout(
            Guid.Empty, Guid.Empty, false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        ResultPayload quiet = AgentConnection.DescribeTimeout(
            Guid.Empty, Guid.Empty, true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        silent.Outcome.Should().NotBe(quiet.Outcome);
        silent.Reason.Should().NotBe(quiet.Reason);
    }

    /// <summary>One <see cref="AgentConnection"/> over a socket the test drives by hand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly FakeWebSocket socket;
        private readonly RecordingLogger logger;
        private readonly Task loop;

        private Harness(FakeWebSocket socket, RecordingLogger logger, AgentConnection connection, Task loop)
        {
            this.socket = socket;
            this.logger = logger;
            Connection = connection;
            this.loop = loop;
        }

        public AgentConnection Connection { get; }

        public IReadOnlyList<byte[]> Sent => socket.Sent;

        /// <summary>Everything the connection logged, so "reported" can be asserted rather than assumed.</summary>
        public IReadOnlyList<string> Logged => logger.Messages;

        public static Harness Start(int protocolVersion = 2)
        {
            FakeWebSocket socket = new();
            RecordingLogger logger = new();
            AgentConnection connection = new(
                Guid.NewGuid(), "198.51.100.7", socket, logger, protocolVersion);

            return new Harness(
                socket, logger, connection, connection.ReceiveLoopAsync(CancellationToken.None));
        }

        /// <summary>Hands the connection a frame, as the agent would.</summary>
        public void Push<T>(AgentFrameType type, T payload)
            => PushRaw(AgentTaskFrames.Encode(type, payload));

        public void PushRaw(byte[] frame) => socket.Inbound.Writer.TryWrite(frame);

        /// <summary>Ends the link, as a disconnect would.</summary>
        public void Complete() => socket.Inbound.Writer.TryComplete();

        /// <summary>Waits until everything pushed so far has been read and handled.</summary>
        public async Task Settled()
        {
            // A marker frame this build does not handle: reading it proves the loop has already
            // passed everything pushed before it. No sleeping, no polling, no clock.
            PushRaw(AgentProtocol.Encode((AgentFrameType)0xfd, 0));
            await socket.Marker.Task.WaitAsync(Backstop);
        }

        /// <summary>Reads the next frame EntKube sent and decodes it, asserting its type.</summary>
        public async Task<T> NextSent<T>(AgentFrameType expected)
        {
            byte[] frame = await socket.Outbound.Reader.ReadAsync(
                new CancellationTokenSource(Backstop).Token);

            AgentFrame decoded = AgentProtocol.Decode(frame);
            decoded.Type.Should().Be(expected);
            decoded.StreamId.Should().Be(0u, "a task frame belongs to the link, not to a stream");

            return AgentTaskFrames.Decode<T>(decoded.Payload);
        }

        public async ValueTask DisposeAsync()
        {
            Complete();
            await loop.WaitAsync(Backstop);
            await Connection.DisposeAsync();
        }
    }

    /// <summary>Keeps what the connection logged, so a test can assert it reported something.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> messages = [];

        public List<string> Messages
        {
            get { lock (messages) { return messages.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages) { messages.Add(formatter(state, exception)); }
        }
    }

    /// <summary>
    /// A WebSocket with no socket. Inbound frames come from the test; outbound frames are
    /// captured for it to assert on.
    /// </summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly List<byte[]> sent = [];

        public Channel<byte[]> Inbound { get; } = Channel.CreateUnbounded<byte[]>();

        public Channel<byte[]> Outbound { get; } = Channel.CreateUnbounded<byte[]>();

        /// <summary>Completes once the loop has read the marker frame <see cref="Harness.Settled"/> sends.</summary>
        public TaskCompletionSource Marker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<byte[]> Sent
        {
            get { lock (sent) { return sent.ToList(); } }
        }

        public override WebSocketState State { get; } = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] frame;

            try
            {
                frame = await Inbound.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }

            if (frame.Length >= 1 && frame[0] == 0xfd) Marker.TrySetResult();

            frame.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Binary, endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            byte[] frame = buffer.ToArray();

            lock (sent) { sent.Add(frame); }

            Outbound.Writer.TryWrite(frame);
            return Task.CompletedTask;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Abort() => Inbound.Writer.TryComplete();

        public override void Dispose() => Inbound.Writer.TryComplete();
    }
}
