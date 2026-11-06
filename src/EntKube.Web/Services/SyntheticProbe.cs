using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services.Outbound;

namespace EntKube.Web.Services;

/// <summary>What one request found.</summary>
public sealed record SyntheticProbeResult(bool Passed, int? StatusCode, int DurationMs, string? Failure);

/// <summary>
/// Makes the request behind a synthetic check.
///
/// <para><b>Why the connection is pinned here.</b> <see cref="OutboundUrlGuard"/> validates a URL and
/// says plainly that DNS rebinding gets past it: a host can resolve to a public address when checked
/// and a private one when connected to. A webhook fires now and then, but a check is re-resolved every
/// few minutes on a schedule the attacker can watch, and its result reveals whether something answered.
/// So every address is validated in the connect callback, at the moment of connecting, and the socket
/// goes to exactly the address that was validated. There is no gap to rebind in.</para>
///
/// <para>Redirects are not followed: a 302 to the metadata address is the obvious way round the
/// callback's caller, and a redirect is an answer the check can judge by itself. Connections are not
/// reused, so each run measures a real connection and resolves afresh.</para>
/// </summary>
public static class SyntheticProbe
{
    public const int MaxBodyBytes = 256 * 1024;

    private const string Refused = "The address is not one EntKube may reach.";

    /// <summary>Raised from the connect callback so the probe can tell a refusal from a network failure.</summary>
    private sealed class DestinationRefusedException() : Exception(Refused);

    /// <param name="allowPrivateTargets">Instance-wide, from <c>Synthetics:AllowPrivateTargets</c>.</param>
    /// <param name="resolve">DNS, replaceable so a test can answer as a rebinding server would.</param>
    public static SocketsHttpHandler CreateHandler(
        bool allowPrivateTargets, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        resolve ??= (host, ct) => Dns.GetHostAddressesAsync(host, ct);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, ct) =>
            {
                string host = context.DnsEndPoint.Host;

                IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
                    ? [literal]
                    : await resolve(host, ct);

                // Every address, not just the one used: a host answering with both is the shape of
                // a rebinding attempt, and the same rule the guard applies.
                if (addresses.Length == 0
                    || (!allowPrivateTargets && addresses.Any(a => !OutboundUrlGuard.IsPubliclyRoutable(a))))
                {
                    throw new DestinationRefusedException();
                }

                Exception? last = null;

                foreach (IPAddress address in addresses)
                {
                    Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                }

                throw last!;
            },
        };
    }

    public static async Task<SyntheticProbeResult> RunAsync(
        HttpClient client, SyntheticCheck check, TimeSpan timeout, CancellationToken stopping)
    {
        Stopwatch watch = Stopwatch.StartNew();

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(timeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, check.Url);
            request.Headers.UserAgent.ParseAdd("EntKube-Synthetic/1.0");

            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, limit.Token);

            int status = (int)response.StatusCode;

            if (!StatusPasses(check.ExpectedStatus, status))
            {
                return new SyntheticProbeResult(false, status, Elapsed(watch), $"Answered {status}.");
            }

            if (!string.IsNullOrEmpty(check.ExpectedText)
                && !await BodyContainsAsync(response, check.ExpectedText, limit.Token))
            {
                return new SyntheticProbeResult(false, status, Elapsed(watch), "The expected text was not in the response.");
            }

            return new SyntheticProbeResult(true, status, Elapsed(watch), null);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SyntheticProbeResult(false, null, Elapsed(watch), "Timed out.");
        }
        catch (HttpRequestException ex)
        {
            // A fixed phrase and never the exception's text: on a refused or internal target that
            // text would describe what EntKube can see from where it sits.
            return new SyntheticProbeResult(false, null, Elapsed(watch), Describe(ex));
        }
    }

    public static bool StatusPasses(int? expected, int status) =>
        expected is { } exact ? status == exact : status is >= 200 and < 400;

    private static string Describe(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is DestinationRefusedException) return Refused;
            if (e is System.Security.Authentication.AuthenticationException) return "The TLS certificate was not accepted.";
        }

        return "Could not connect.";
    }

    /// <summary>Reads at most <see cref="MaxBodyBytes"/>, so a huge response cannot exhaust the probe.</summary>
    private static async Task<bool> BodyContainsAsync(HttpResponseMessage response, string text, CancellationToken ct)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(ct);

        byte[] buffer = new byte[MaxBodyBytes];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total).Contains(text, StringComparison.Ordinal);
    }

    private static int Elapsed(Stopwatch watch) => (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue);
}
