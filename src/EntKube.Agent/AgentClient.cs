using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using EntKube.Agents.Protocol;

namespace EntKube.Agent;

/// <summary>
/// The agent's side of the link: dials EntKube, then serves the TCP connections
/// EntKube asks for — subject to this agent's own allowlist.
///
/// All traffic is opaque here. The bytes relayed are TLS records negotiated
/// end-to-end between EntKube and the destination, so this process cannot read or
/// alter them; it only decides which destinations it is willing to reach.
/// </summary>
public sealed class AgentClient(AgentOptions options, Action<string> log)
{
    private readonly ConcurrentDictionary<uint, TcpRelay> relays = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private ClientWebSocket? socket;

    /// <summary>
    /// Connects and serves until cancelled, reconnecting with backoff whenever the
    /// link drops. Returns only when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int delay = options.ReconnectSeconds;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(ct);

                // A clean disconnect still means reconnecting, but promptly.
                delay = options.ReconnectSeconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"Link failed: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;

            log($"Reconnecting in {delay}s...");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = Math.Min(delay * 2, options.MaxReconnectSeconds);
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        Uri endpoint = BuildEndpoint(options.ServerUrl);

        using ClientWebSocket ws = new();
        ws.Options.SetRequestHeader(AgentProtocol.TokenHeader, options.Token);
        ws.Options.SetRequestHeader("X-EntKube-Agent-Allowlist", options.DescribeAllowlist());

        // Keep the link warm through NAT and idle-timeout proxies.
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        log($"Connecting to {endpoint}...");
        await ws.ConnectAsync(endpoint, ct);
        socket = ws;

        log($"Connected. Allowing {options.AllowedHosts.Count} host pattern(s) on port(s) "
            + $"{string.Join(", ", options.AllowedPorts)}.");

        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        finally
        {
            socket = null;

            foreach (TcpRelay relay in relays.Values)
            {
                relay.Dispose();
            }

            relays.Clear();
            log("Disconnected.");
        }
    }

    /// <summary>
    /// Turns the configured base URL into the WebSocket endpoint, mapping the
    /// scheme so operators can paste the same URL they use in a browser.
    /// </summary>
    public static Uri BuildEndpoint(string serverUrl)
    {
        UriBuilder builder = new(serverUrl.TrimEnd('/') + AgentProtocol.EndpointPath)
        {
            Scheme = serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss"
        };

        return builder.Uri;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[AgentProtocol.HeaderSize + AgentProtocol.MaxPayloadSize];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using MemoryStream message = new();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close) return;

                message.Write(buffer, 0, result.Count);

                if (message.Length > AgentProtocol.HeaderSize + AgentProtocol.MaxPayloadSize)
                {
                    throw new InvalidDataException("Server sent a frame larger than the protocol allows.");
                }
            }
            while (!result.EndOfMessage);

            AgentFrame frame = AgentProtocol.Decode(message.ToArray());

            switch (frame.Type)
            {
                case AgentFrameType.Open:
                    // Do not await: dialling can block, and the link must keep
                    // serving other streams meanwhile.
                    _ = HandleOpenAsync(frame, ct);
                    break;

                case AgentFrameType.Data:
                    if (relays.TryGetValue(frame.StreamId, out TcpRelay? relay))
                    {
                        await relay.WriteAsync(frame.Payload, ct);
                    }
                    break;

                case AgentFrameType.Close:
                    if (relays.TryRemove(frame.StreamId, out TcpRelay? closing))
                    {
                        closing.Dispose();
                    }
                    break;

                case AgentFrameType.Ping:
                    await SendAsync(AgentProtocol.Encode(AgentFrameType.Pong, 0), ct);
                    break;

                case AgentFrameType.OpenAck:
                case AgentFrameType.Pong:
                    // Agent-to-server frames; ignore if echoed back.
                    break;
            }
        }
    }

    private async Task HandleOpenAsync(AgentFrame frame, CancellationToken ct)
    {
        uint streamId = frame.StreamId;
        string target = Encoding.UTF8.GetString(frame.Payload.Span);

        try
        {
            (string host, int port) = AgentProtocol.ParseTarget(target);

            if (!options.IsAllowed(host, port))
            {
                // The refusal is explicit so the operator sees a configuration
                // answer in EntKube rather than a mysterious timeout.
                log($"REFUSED {host}:{port} — not in this agent's allowlist.");
                await SendAsync(AgentProtocol.EncodeOpenAck(
                    streamId, false, $"{host}:{port} is not in this agent's allowlist"), ct);
                return;
            }

            TcpClient client = new() { NoDelay = true };

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
                await client.ConnectAsync(host, port, timeout.Token);
            }

            TcpRelay relay = new(streamId, client,
                (id, data, token) => SendAsync(AgentProtocol.Encode(AgentFrameType.Data, id, data.Span), token),
                id => SendAsync(AgentProtocol.Encode(AgentFrameType.Close, id), CancellationToken.None),
                id => relays.TryRemove(id, out _));

            relays[streamId] = relay;

            await SendAsync(AgentProtocol.EncodeOpenAck(streamId, true), ct);

            if (options.VerboseLogging) log($"OPEN  {host}:{port} (stream {streamId})");

            relay.Start(ct);
        }
        catch (Exception ex)
        {
            log($"FAILED {target}: {ex.Message}");

            try
            {
                await SendAsync(AgentProtocol.EncodeOpenAck(streamId, false, ex.Message), ct);
            }
            catch
            {
                // Link is gone; nothing to report to.
            }
        }
    }

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        ClientWebSocket? ws = socket;
        if (ws is null || ws.State != WebSocketState.Open) return;

        await sendLock.WaitAsync(ct);

        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// One dialled TCP connection, pumping everything it receives back up the link
    /// as Data frames until either side closes.
    /// </summary>
    private sealed class TcpRelay(
        uint streamId,
        TcpClient client,
        Func<uint, ReadOnlyMemory<byte>, CancellationToken, Task> send,
        Func<uint, Task> sendClose,
        Action<uint> onFinished) : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private bool disposed;

        public void Start(CancellationToken ct)
            => _ = PumpAsync(CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token).Token);

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (disposed) return;

            try
            {
                await client.GetStream().WriteAsync(data, ct);
            }
            catch
            {
                Dispose();
            }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[AgentProtocol.MaxPayloadSize];

            try
            {
                NetworkStream stream = client.GetStream();

                while (!ct.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;

                    await send(streamId, buffer.AsMemory(0, read), ct);
                }
            }
            catch
            {
                // Connection reset or link gone — both end the stream.
            }
            finally
            {
                if (!disposed)
                {
                    try { await sendClose(streamId); } catch { /* link gone */ }
                }

                Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            try { lifetime.Cancel(); } catch { /* already disposed */ }

            client.Dispose();
            lifetime.Dispose();
            onFinished(streamId);
        }
    }
}
