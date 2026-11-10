using System.Buffers;
using System.Net.WebSockets;
using System.Security.Claims;

namespace EntKube.Web.Services;

/// <summary>Which of a VM's two consoles.</summary>
public enum VirtualMachineConsoleKind
{
    /// <summary>The graphical screen, as VNC (RFB) bytes. Rendered in the browser by noVNC.</summary>
    Vnc,

    /// <summary>The first serial port, as raw terminal bytes. Rendered in the browser by xterm.js.</summary>
    Serial
}

/// <summary>
/// The browser end of a VM console: <c>/vm-console/{tenant}/{cluster}/{namespace}/{vm}/{vnc|serial}</c>.
///
/// <para><b>Who may open one.</b> The request is a cookie-authenticated websocket upgrade, not a Blazor
/// circuit, so nothing the tenant pages check has been checked for it. The tenant comes from the URL, and
/// the URL is the caller's to write — so this endpoint asks <see cref="UserAccessService"/> itself whether
/// the signed-in person belongs to that tenant before the cluster seam is asked anything.
/// <c>IClusterClientFactory.ForAsync</c> then confirms the cluster is that tenant's.</para>
///
/// <para><b>Who may NOT.</b> A websocket handshake carries cookies but is exempt from CORS and from
/// antiforgery, so a page on another site could otherwise open a console as whoever visits it. The
/// <c>Origin</c> header is the defence the browser gives, and it is checked against this host.</para>
///
/// <para><b>Mounted outside <c>/api/v1</c></b> on purpose: it is a session for a person, not an API
/// operation, and has no token, scope, CLI verb or MCP tool.</para>
/// </summary>
public static class VirtualMachineConsoleEndpoint
{
    public const string RoutePrefix = "/vm-console";

    public static IEndpointConventionBuilder MapVirtualMachineConsole(this IEndpointRouteBuilder app) =>
        app.Map(RoutePrefix + "/{tenantId:guid}/{clusterId:guid}/{ns}/{name}/{kind}", async (
                HttpContext http, Guid tenantId, Guid clusterId, string ns, string name, string kind,
                UserAccessService access, VirtualMachineService vms) =>
            {
                string? refusal = ConsoleRequest.Refusal(
                    http.WebSockets.IsWebSocketRequest, http.Request.Headers.Origin.ToString(),
                    http.Request.Scheme, http.Request.Host, kind, out VirtualMachineConsoleKind consoleKind);

                if (refusal is not null)
                {
                    http.Response.StatusCode = refusal == ConsoleRequest.NotAWebSocket
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsync(refusal, http.RequestAborted);
                    return;
                }

                string? userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId is null || !await access.HasTenantAccessAsync(userId, tenantId, http.RequestAborted))
                {
                    // 404, not 403: whether a tenant id exists is not something to confirm to a stranger.
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                WebSocket upstream;
                try
                {
                    upstream = await vms.OpenConsoleAsync(
                        tenantId, clusterId, ns, name, consoleKind,
                        http.User.Identity?.Name ?? userId, http.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Before the browser's upgrade is accepted, so the page can show why.
                    http.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await http.Response.WriteAsync($"Could not open the console: {ex.Message}", http.RequestAborted);
                    return;
                }

                using (upstream)
                using (WebSocket browser = await http.WebSockets.AcceptWebSocketAsync())
                {
                    await ConsoleBridge.PumpAsync(browser, upstream, http.RequestAborted);
                }
            })
            .RequireAuthorization();
}

/// <summary>The checks a console request must pass before anyone is asked about tenants or clusters.</summary>
public static class ConsoleRequest
{
    public const string NotAWebSocket = "A console is a websocket; open it from the Virtual Machines page.";
    public const string ForeignOrigin = "This console was requested by a page on another site.";
    public const string UnknownKind = "The console must be 'vnc' or 'serial'.";

    /// <summary>Null when the request may proceed; otherwise the reason it may not.</summary>
    public static string? Refusal(
        bool isWebSocket, string? origin, string scheme, HostString host, string kind,
        out VirtualMachineConsoleKind consoleKind)
    {
        consoleKind = VirtualMachineConsoleKind.Vnc;

        if (!isWebSocket) return NotAWebSocket;
        if (!IsSameOrigin(origin, scheme, host)) return ForeignOrigin;

        switch (kind)
        {
            case "vnc": consoleKind = VirtualMachineConsoleKind.Vnc; return null;
            case "serial": consoleKind = VirtualMachineConsoleKind.Serial; return null;
            default: return UnknownKind;
        }
    }

    /// <summary>
    /// The page that asked is served by this host. A browser always sends Origin on a websocket
    /// handshake, so a missing one is refused rather than trusted.
    /// </summary>
    public static bool IsSameOrigin(string? origin, string scheme, HostString host)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? o)) return false;
        if (!host.HasValue) return false;

        int hostPort = host.Port ?? (scheme == Uri.UriSchemeHttps ? 443 : 80);
        return string.Equals(o.Host, host.Host, StringComparison.OrdinalIgnoreCase) && o.Port == hostPort;
    }
}

/// <summary>Copies frames both ways between the browser and the cluster until either side ends.</summary>
public static class ConsoleBridge
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Runs until one side closes or fails, then closes the other. Everything is forwarded as binary:
    /// KubeVirt's consoles are byte streams, and a browser's text frame (a keystroke typed into the
    /// terminal) is the same bytes.
    /// </summary>
    public static async Task PumpAsync(WebSocket browser, WebSocket upstream, CancellationToken ct)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task toCluster = CopyAsync(browser, upstream, stop.Token);
        Task toBrowser = CopyAsync(upstream, browser, stop.Token);

        await Task.WhenAny(toCluster, toBrowser);
        await stop.CancelAsync();

        // The other direction is now cancelled; its exception is the expected end, not news.
        try { await Task.WhenAll(toCluster, toBrowser); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }

        await CloseQuietlyAsync(browser);
        await CloseQuietlyAsync(upstream);
    }

    private static async Task CopyAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult received = await from.ReceiveAsync(buffer.AsMemory(), ct);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await to.CloseOutputAsync(
                            from.CloseStatus ?? WebSocketCloseStatus.NormalClosure, from.CloseStatusDescription, ct);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary,
                    received.EndOfMessage, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A polite close where one is still possible, an abort where it is not.</summary>
    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            if (socket.State != WebSocketState.Closed) socket.Abort();
            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            socket.Abort();
        }
    }
}
