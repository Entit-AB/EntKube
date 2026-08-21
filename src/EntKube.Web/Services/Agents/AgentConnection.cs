using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using EntKube.Agents.Protocol;

namespace EntKube.Web.Services.Agents;

/// <summary>
/// EntKube's end of one live agent link: the WebSocket the agent dialled in on,
/// the streams multiplexed over it, and the loop that reads frames.
///
/// Owned by <see cref="AgentRegistry"/> for as long as the agent stays connected.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<uint, AgentStream> streams = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(bool Success, string? Error)>> pendingOpens = new();

    // WebSocket.SendAsync must not be called concurrently, and every stream on the
    // link shares this one socket.
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();

    private uint nextStreamId;
    private volatile bool closed;

    public AgentConnection(Guid agentId, string remoteAddress, WebSocket socket, ILogger logger)
    {
        AgentId = agentId;
        RemoteAddress = remoteAddress;
        this.socket = socket;
        this.logger = logger;
        ConnectedAt = DateTime.UtcNow;
    }

    public Guid AgentId { get; }
    public string RemoteAddress { get; }
    public DateTime ConnectedAt { get; }

    /// <summary>True while the link is usable.</summary>
    public bool IsAlive => !closed && socket.State == WebSocketState.Open;

    /// <summary>Streams currently open over this link — surfaced for diagnostics.</summary>
    public int ActiveStreams => streams.Count;

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

            case AgentFrameType.Open:
            case AgentFrameType.Ping:
                // Server-to-agent frames; an agent sending these is misbehaving.
                logger.LogWarning("Agent {AgentId} sent an unexpected {Type} frame", AgentId, frame.Type);
                break;
        }
    }

    /// <summary>Sends a liveness probe. Used by the registry to notice half-open links.</summary>
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
}
