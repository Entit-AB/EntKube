using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EntKube.Agents.Protocol;

namespace EntKube.Web.Services.Agents;

/// <summary>
/// EntKube's end of one live agent link: the WebSocket the agent dialled in on,
/// the streams multiplexed over it, the leases dispatched over it, and the loop
/// that reads frames.
///
/// Owned by <see cref="AgentRegistry"/> for as long as the agent stays connected.
///
/// <para>⚠️ <b>A lease can only be dispatched by the process holding the link.</b> This object is
/// process-local and there is no backplane, so <see cref="RunLeaseAsync"/> is reachable only from
/// the instance the agent happened to connect to. In a multi-instance deployment that is a real
/// constraint, not a detail — see <see cref="AgentRegistry"/>.</para>
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    /// <summary>How long the agent has to accept or refuse a lease before the dispatch gives up.</summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after the agent's own deadline the server keeps waiting for the terminal frame.
    /// The agent counts its deadline from receipt and EntKube from send, so this covers the
    /// round trip plus the agent noticing its own expiry and saying so.
    /// </summary>
    private static readonly TimeSpan ResultGrace = TimeSpan.FromSeconds(10);

    private readonly WebSocket socket;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<uint, AgentStream> streams = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(bool Success, string? Error)>> pendingOpens = new();
    private readonly ConcurrentDictionary<Guid, PendingLease> leases = new();

    // WebSocket.SendAsync must not be called concurrently, and every stream on the
    // link shares this one socket.
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();

    private uint nextStreamId;
    private volatile bool closed;
    private volatile CapabilityPayload? declared;

    public AgentConnection(
        Guid agentId, string remoteAddress, WebSocket socket, ILogger logger, int protocolVersion)
    {
        AgentId = agentId;
        RemoteAddress = remoteAddress;
        this.socket = socket;
        this.logger = logger;
        ProtocolVersion = protocolVersion;
        ConnectedAt = DateTime.UtcNow;
    }

    public Guid AgentId { get; }
    public string RemoteAddress { get; }
    public DateTime ConnectedAt { get; }

    /// <summary>
    /// The protocol version this agent declared on the handshake. 1 means a tunnel-only agent that
    /// predates the task vocabulary — it is not broken and must keep working, but nothing above
    /// <see cref="AgentFrameType.Pong"/> may be sent to it.
    /// </summary>
    public int ProtocolVersion { get; }

    /// <summary>
    /// What the agent said it can do, or null when it has not said yet. <b>Informational.</b> It
    /// narrows what this process will dispatch; it never authorizes anything, because the server's
    /// own registry decides what a capability name means.
    /// </summary>
    public CapabilityPayload? DeclaredCapabilities => declared;

    /// <summary>True while the link is usable.</summary>
    public bool IsAlive => !closed && socket.State == WebSocketState.Open;

    /// <summary>Streams currently open over this link — surfaced for diagnostics.</summary>
    public int ActiveStreams => streams.Count;

    /// <summary>Leases currently in flight over this link — surfaced for diagnostics.</summary>
    public int ActiveLeases => leases.Count;

    /// <summary>
    /// Opens a TCP stream to <paramref name="host"/>:<paramref name="port"/> from
    /// the agent's network and returns it as a <see cref="Stream"/>.
    ///
    /// The agent enforces its own allowlist and may refuse; a refusal surfaces
    /// here as an <see cref="IOException"/> carrying the agent's reason, because
    /// "the agent will not dial that host" is a configuration answer the operator
    /// needs to see rather than a transport failure to retry.
    /// </summary>
    public async Task<Stream> OpenStreamAsync(string host, int port, CancellationToken ct = default)
    {
        if (!IsAlive)
        {
            throw new IOException("The egress agent is not connected.");
        }

        uint streamId = Interlocked.Increment(ref nextStreamId);

        TaskCompletionSource<(bool, string?)> ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOpens[streamId] = ack;

        AgentStream stream = new(
            streamId,
            (id, data, token) => SendAsync(AgentProtocol.Encode(AgentFrameType.Data, id, data.Span), token),
            id => SendAsync(AgentProtocol.Encode(AgentFrameType.Close, id), CancellationToken.None),
            () => streams.TryRemove(streamId, out _));

        streams[streamId] = stream;

        try
        {
            await SendAsync(
                AgentProtocol.Encode(AgentFrameType.Open, streamId, AgentProtocol.FormatTarget(host, port)), ct);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            (bool success, string? error) = await ack.Task.WaitAsync(timeout.Token);

            if (!success)
            {
                throw new IOException(
                    $"The egress agent refused to connect to {host}:{port}: {error ?? "no reason given"}");
            }

            return stream;
        }
        catch
        {
            streams.TryRemove(streamId, out _);
            pendingOpens.TryRemove(streamId, out _);
            await stream.DisposeAsync();
            throw;
        }
        finally
        {
            pendingOpens.TryRemove(streamId, out _);
        }
    }

    /// <summary>
    /// Dispatches one unit of work to this agent and waits for its verdict.
    ///
    /// <para><b>Sending the Lease is the dispatch, and there is nowhere later to gate it.</b> The
    /// change gate is an in-process scoped service and the agent is a separate binary in the
    /// customer's network, so once this frame is on the socket nothing in EntKube can intervene.
    /// Everything that decides whether this work may happen therefore happens above this line:
    /// the capability must be one the server registered (<see cref="AgentCapabilityRegistry"/>),
    /// and the registry has no way to express a mutating capability at all.</para>
    ///
    /// <para><b>What this is not.</b> There is no durable store behind it, so a link that drops
    /// mid-lease loses the lease and the caller is told so. Dispatch, deadline, retry and result
    /// persistence are item 11 of docs/decomposition.md; this is the wire half.</para>
    /// </summary>
    /// <param name="capability">A registered name such as <c>component.scan</c> — what to do, never how.</param>
    /// <param name="deadline">How long the agent has, counted from its own receipt of the frame.</param>
    /// <param name="onProgress">Called for each partial output frame, in arrival order.</param>
    /// <exception cref="InvalidOperationException">
    /// The agent speaks a protocol that predates leases, or has declared that it cannot do this.
    /// </exception>
    public async Task<ResultPayload> RunLeaseAsync(
        string capability,
        TimeSpan deadline,
        JsonElement? input = null,
        Func<ProgressPayload, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!IsAlive)
        {
            throw new IOException("The egress agent is not connected.");
        }

        if (!AgentTaskFrames.IsValidCapabilityName(capability))
        {
            throw new ArgumentException(
                $"'{capability}' is not a capability name. A capability names what to do, in lowercase "
                + "dotted segments such as 'component.scan' — never a command to run.",
                nameof(capability));
        }

        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadline), "A lease needs a deadline the agent can count down from.");
        }

        int required = AgentProtocol.RequiredVersion(AgentFrameType.Lease);

        if (ProtocolVersion < required)
        {
            // Refusing here is the whole point of the handshake. Sending this frame to an agent
            // that predates it would decode into a type it does not handle and, on any agent built
            // before the protocol became tolerant, tear down the link and every TCP stream on it.
            throw new InvalidOperationException(
                $"Agent {AgentId} speaks protocol version {ProtocolVersion} and leases need {required}. "
                + "Upgrade the agent, or use it as a tunnel only.");
        }

        if (declared is { Capabilities: { } names } && !names.Contains(capability))
        {
            throw new InvalidOperationException(
                $"Agent {AgentId} did not declare '{capability}'. It declared: "
                + $"{(names.Count == 0 ? "nothing" : string.Join(", ", names))}.");
        }

        Guid leaseId = Guid.NewGuid();
        PendingLease pending = new(onProgress);
        leases[leaseId] = pending;

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);

        try
        {
            await SendAsync(
                AgentTaskFrames.Encode(
                    AgentFrameType.Lease,
                    new LeasePayload(leaseId, capability, (int)deadline.TotalMilliseconds, input)),
                linked.Token);

            LeaseAckPayload ack = await pending.Ack.Task.WaitAsync(AckTimeout, linked.Token);

            if (!ack.Accepted)
            {
                // A refusal is an ordinary answer, not a fault: the agent is telling us it cannot
                // or will not do this, which is a configuration answer an operator needs to read.
                return new ResultPayload(
                    leaseId, LeaseOutcome.Refused, ack.Reason ?? "the agent gave no reason");
            }

            return await pending.Result.Task.WaitAsync(deadline + ResultGrace, linked.Token);
        }
        catch (TimeoutException)
        {
            await TryCancelAsync(leaseId, "the deadline passed with no result");

            return new ResultPayload(
                leaseId, LeaseOutcome.Expired,
                $"Agent {AgentId} sent no verdict within {(deadline + ResultGrace).TotalSeconds:0}s.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCancelAsync(leaseId, "the caller cancelled");
            throw;
        }
        catch (OperationCanceledException)
        {
            // The link itself went away; TearDownAsync has already resolved what it could.
            return new ResultPayload(
                leaseId, LeaseOutcome.Failed, "The agent link closed before the lease finished.");
        }
        finally
        {
            leases.TryRemove(leaseId, out _);
        }
    }

    /// <summary>
    /// Reads frames until the link closes. Runs for the lifetime of the agent's
    /// HTTP request, so the request handler must await it.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        byte[] buffer = new byte[AgentProtocol.HeaderSize + AgentProtocol.MaxPayloadSize];

        try
        {
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;

                // A frame may span several WebSocket reads; reassemble before decoding.
                do
                {
                    result = await socket.ReceiveAsync(buffer, linked.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);

                    if (message.Length > AgentProtocol.HeaderSize + AgentProtocol.MaxPayloadSize)
                    {
                        throw new InvalidDataException("Agent sent a frame larger than the protocol allows.");
                    }
                }
                while (!result.EndOfMessage);

                await HandleFrameAsync(AgentProtocol.Decode(message.ToArray()), linked.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or the agent going away — not an error.
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation("Agent {AgentId} link dropped: {Reason}", AgentId, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent {AgentId} link failed", AgentId);
        }
        finally
        {
            await TearDownAsync("link closed");
        }
    }

    private async Task HandleFrameAsync(AgentFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case AgentFrameType.OpenAck:
                if (pendingOpens.TryGetValue(frame.StreamId, out var ack))
                {
                    ack.TrySetResult(AgentProtocol.DecodeOpenAck(frame.Payload));
                }
                break;

            case AgentFrameType.Data:
                if (streams.TryGetValue(frame.StreamId, out AgentStream? target))
                {
                    await target.FeedAsync(frame.Payload, ct);
                }
                break;

            case AgentFrameType.Close:
                if (streams.TryRemove(frame.StreamId, out AgentStream? closing))
                {
                    string? reason = frame.Payload.Length > 0
                        ? Encoding.UTF8.GetString(frame.Payload.Span)
                        : null;
                    closing.CompleteFeed(reason);
                }
                break;

            case AgentFrameType.Pong:
                // Liveness only; nothing to do.
                break;

            case AgentFrameType.Capability:
                HandleCapability(frame);
                break;

            case AgentFrameType.LeaseAck:
                HandleLeaseFrame<LeaseAckPayload>(
                    frame, p => p.LeaseId, (pending, p) => pending.Ack.TrySetResult(p));
                break;

            case AgentFrameType.Result:
                HandleLeaseFrame<ResultPayload>(
                    frame, p => p.LeaseId, (pending, p) => pending.Result.TrySetResult(p));
                break;

            case AgentFrameType.Progress:
                await HandleProgressAsync(frame);
                break;

            case AgentFrameType.Open:
            case AgentFrameType.Ping:
            case AgentFrameType.Lease:
            case AgentFrameType.LeaseCancel:
                // Server-to-agent frames; an agent sending these is misbehaving.
                logger.LogWarning("Agent {AgentId} sent an unexpected {Type} frame", AgentId, frame.Type);
                break;

            default:
                // A peer newer than this build. Reported rather than ignored, because a frame that
                // decodes and then vanishes is the quietest possible version-skew failure — and
                // reported rather than fatal, because dropping the link would take every tunnel
                // stream with it over something that is merely unfamiliar.
                logger.LogWarning(
                    "Agent {AgentId} sent frame type 0x{Type:x2}, which this build does not handle. "
                    + "The agent may be newer than this EntKube.",
                    AgentId, (byte)frame.Type);
                break;
        }
    }

    private void HandleCapability(AgentFrame frame)
    {
        try
        {
            CapabilityPayload payload = AgentTaskFrames.Decode<CapabilityPayload>(frame.Payload);
            declared = payload;

            logger.LogInformation(
                "Agent {AgentId} declared agent version {AgentVersion}, protocol {Protocol}, capabilities: {Capabilities}",
                AgentId, payload.AgentVersion, payload.ProtocolVersion,
                payload.Capabilities.Count == 0 ? "none" : string.Join(", ", payload.Capabilities));

            if (payload.ProtocolVersion != ProtocolVersion)
            {
                // Not fatal — the handshake is what this process acts on — but a disagreement
                // between an agent's two statements about itself is worth seeing in a log.
                logger.LogWarning(
                    "Agent {AgentId} declared protocol {Declared} in its Capability frame but {Handshake} on the handshake.",
                    AgentId, payload.ProtocolVersion, ProtocolVersion);
            }
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Agent {AgentId} sent a Capability frame that could not be read", AgentId);
        }
    }

    private void HandleLeaseFrame<T>(AgentFrame frame, Func<T, Guid> leaseId, Action<PendingLease, T> apply)
    {
        try
        {
            T payload = AgentTaskFrames.Decode<T>(frame.Payload);
            Guid id = leaseId(payload);

            if (leases.TryGetValue(id, out PendingLease? pending))
            {
                apply(pending, payload);
            }
            else
            {
                // Deliberately logged, unlike a Data frame for an unknown stream. A late Result is
                // work the agent actually did, and a link drop or an expiry leaves no other trace
                // of it; silence here is how an agent ends up doing work nobody is waiting for.
                logger.LogInformation(
                    "Agent {AgentId} sent a {Type} for lease {LeaseId}, which is no longer in flight.",
                    AgentId, frame.Type, id);
            }
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Agent {AgentId} sent a {Type} frame that could not be read", AgentId, frame.Type);
        }
    }

    private async Task HandleProgressAsync(AgentFrame frame)
    {
        ProgressPayload payload;

        try
        {
            payload = AgentTaskFrames.Decode<ProgressPayload>(frame.Payload);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Agent {AgentId} sent a Progress frame that could not be read", AgentId);
            return;
        }

        if (!leases.TryGetValue(payload.LeaseId, out PendingLease? pending))
        {
            logger.LogInformation(
                "Agent {AgentId} sent progress for lease {LeaseId}, which is no longer in flight.",
                AgentId, payload.LeaseId);
            return;
        }

        if (pending.OnProgress is null) return;

        try
        {
            await pending.OnProgress(payload);
        }
        catch (Exception ex)
        {
            // A caller's handler must not be able to kill the link, and therefore every tunnel
            // stream on it, by throwing on one chunk.
            logger.LogWarning(
                ex, "The progress handler for lease {LeaseId} on agent {AgentId} threw",
                payload.LeaseId, AgentId);
        }
    }

    private async Task TryCancelAsync(Guid leaseId, string reason)
    {
        try
        {
            await SendAsync(
                AgentTaskFrames.Encode(AgentFrameType.LeaseCancel, new LeaseCancelPayload(leaseId, reason)),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Best effort: the link may already be gone, which is often why we are cancelling.
            logger.LogDebug(ex, "Could not cancel lease {LeaseId} on agent {AgentId}", leaseId, AgentId);
        }
    }

    /// <summary>Sends a liveness probe.</summary>
    /// <remarks>
    /// ⚠️ Nothing calls this. Its doc comment used to claim the registry used it to notice
    /// half-open links; it does not, and there is no timer anywhere that does. Left in place
    /// because the agent answers it correctly and a heartbeat is wanted once leases have deadlines
    /// to measure against — but said plainly, because a liveness probe nobody sends is worse than
    /// none at all if it is mistaken for one.
    /// </remarks>
    public Task PingAsync(CancellationToken ct = default)
        => SendAsync(AgentProtocol.Encode(AgentFrameType.Ping, 0), ct);

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        if (closed) throw new IOException("The egress agent link is closed.");

        await sendLock.WaitAsync(ct);

        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task TearDownAsync(string reason)
    {
        if (closed) return;
        closed = true;

        foreach (AgentStream stream in streams.Values)
        {
            stream.CompleteFeed(reason);
        }

        streams.Clear();

        foreach (var pending in pendingOpens.Values)
        {
            pending.TrySetResult((false, reason));
        }

        pendingOpens.Clear();

        foreach ((Guid leaseId, PendingLease lease) in leases)
        {
            // Every waiter is resolved rather than left hanging: a lease has no durable record, so
            // a link drop is the end of it and the caller has to be told rather than timing out.
            lease.Ack.TrySetResult(new LeaseAckPayload(leaseId, Accepted: false, reason));
            lease.Result.TrySetResult(new ResultPayload(leaseId, LeaseOutcome.Failed, reason));
        }

        leases.Clear();
        await lifetime.CancelAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync("disposed");

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None);
            }
            catch { /* the peer may already be gone */ }
        }

        sendLock.Dispose();
        lifetime.Dispose();
    }

    /// <summary>One dispatched lease, and whoever is waiting on it.</summary>
    private sealed class PendingLease(Func<ProgressPayload, Task>? onProgress)
    {
        public TaskCompletionSource<LeaseAckPayload> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ResultPayload> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<ProgressPayload, Task>? OnProgress { get; } = onProgress;
    }
}
