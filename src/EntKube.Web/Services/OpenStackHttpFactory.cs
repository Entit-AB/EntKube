using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using EntKube.Web.Services.Agents;

namespace EntKube.Web.Services;

/// <summary>
/// Outbound proxy settings for one OpenStack connection. When set, every call
/// EntKube makes to that cloud (Keystone, Nova/Neutron/Glance, Swift and the
/// S3-compatible endpoint) is tunnelled through the proxy so the request reaches
/// OpenStack from the proxy's IP rather than the EntKube server's.
///
/// This exists for clouds that restrict their API to an IP allowlist which the
/// EntKube server is not on. Running any proxy on a permitted network — including
/// a plain <c>ssh -D</c> SOCKS tunnel — is enough to get through.
/// </summary>
/// <param name="Url">Proxy URL, e.g. "socks5://10.0.0.5:1080" or "http://proxy.corp:3128". No credentials.</param>
/// <param name="Username">Optional proxy username. The matching password comes from the vault.</param>
/// <param name="Password">Optional proxy password (decrypted from the vault; never persisted in the DB).</param>
public sealed record OpenStackProxy(string Url, string? Username = null, string? Password = null)
{
    /// <summary>Identity of the underlying connection pool — distinct settings must not share a handler.</summary>
    internal string CacheKey => $"{Url}\n{Username}\n{Password}";
}

/// <summary>
/// How a connection's traffic should leave EntKube. Either an explicit proxy, or
/// a relay running inside a cluster EntKube already manages — see
/// <see cref="ClusterEgressRelay"/> for why the cluster option exists.
/// </summary>
/// <param name="Proxy">Explicit outbound proxy, or null.</param>
/// <param name="RelayClusterId">Cluster whose in-cluster relay to route through, or null.</param>
public sealed record OpenStackEgress(
    OpenStackProxy? Proxy = null, Guid? RelayClusterId = null, Guid? AgentId = null)
{
    /// <summary>True when this describes no egress hop at all — a direct connection.</summary>
    public bool IsDirect => Proxy is null && RelayClusterId is null && AgentId is null;
}

/// <summary>
/// An <see cref="OpenStackEgress"/> after any tunnel has been established: the
/// concrete transport a client can be built on, synchronously.
///
/// Separate from <see cref="OpenStackEgress"/> because bringing a relay tunnel up
/// is async, while the AWS SDK builds its clients synchronously — resolving once
/// up front keeps that boundary clean.
/// </summary>
/// <param name="Proxy">Proxy to route through, or null.</param>
/// <param name="RelayLocalPort">Loopback port forwarding to a cluster relay, or null.</param>
/// <param name="AgentId">Egress agent to open streams through, or null.</param>
public sealed record ResolvedEgress(
    OpenStackProxy? Proxy = null, int? RelayLocalPort = null, Guid? AgentId = null)
{
    /// <summary>True when no hop is configured and traffic should leave directly.</summary>
    public bool IsDirect => Proxy is null && RelayLocalPort is null && AgentId is null;

    /// <summary>Identity of the underlying connection pool — distinct transports must not share a handler.</summary>
    internal string CacheKey => this switch
    {
        { Proxy: not null } => $"proxy\n{Proxy.CacheKey}",
        { AgentId: not null } => $"agent\n{AgentId}",
        _ => $"relay\n{RelayLocalPort}"
    };
}

/// <summary>
/// Hands out <see cref="HttpClient"/> instances (and an AWS SDK factory) that
/// honour a connection's <see cref="ResolvedEgress"/>. Registered as a singleton
/// because it pools one <see cref="SocketsHttpHandler"/> per distinct transport —
/// building a handler per request would leak sockets.
///
/// A null egress falls through to the default direct client, so connections that
/// do not need this pay nothing.
/// </summary>
public sealed class OpenStackHttpFactory(IHttpClientFactory inner, AgentRegistry agents) : IDisposable
{
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> handlers = new();

    /// <summary>Schemes .NET's <see cref="WebProxy"/> understands. SOCKS is what an SSH tunnel gives you.</summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "socks4", "socks4a", "socks5"];

    /// <summary>
    /// Returns an HTTP client routed through <paramref name="proxy"/>, or the
    /// default client when it is null. The caller owns the returned client, but
    /// disposing it leaves the pooled handler (and its connections) intact.
    /// </summary>
    public HttpClient CreateClient(ResolvedEgress? egress)
    {
        if (egress is null || egress.IsDirect)
        {
            return inner.CreateClient();
        }

        SocketsHttpHandler handler = handlers.GetOrAdd(egress.CacheKey, _ => BuildHandler(egress));
        return new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>
    /// Returns an AWS SDK client factory that routes S3 traffic through the same
    /// pooled handler, or null when there is no proxy. Assign to
    /// <c>AmazonS3Config.HttpClientFactory</c>.
    ///
    /// The SDK's own <c>ProxyHost</c>/<c>ProxyPort</c> fields are deliberately not
    /// used: they only speak HTTP proxies, and the SOCKS case is the one that
    /// matters here.
    /// </summary>
    public HttpClientFactory? CreateAwsHttpClientFactory(ResolvedEgress? egress)
        => egress is null || egress.IsDirect ? null : new ProxiedAwsHttpClientFactory(this, egress);

    private SocketsHttpHandler BuildHandler(ResolvedEgress egress)
    {
        SocketsHttpHandler handler = new()
        {
            // Recycle pooled connections so a restarted tunnel or proxy is picked up
            // without an app restart.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        if (egress.Proxy is { } proxy)
        {
            WebProxy webProxy = new(ParseProxyUri(proxy.Url));

            if (!string.IsNullOrWhiteSpace(proxy.Username))
            {
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;
            return handler;
        }

        if (egress.AgentId is { } agentId)
        {
            // Ask the agent to dial the destination from inside its own network and
            // hand back the stream. The request URI is untouched, so TLS is still
            // negotiated end-to-end with the real host — the agent relays ciphertext
            // and certificate validation and request signing are unaffected.
            handler.UseProxy = false;
            handler.ConnectCallback = async (context, ct) =>
                await agents.OpenStreamAsync(
                    agentId, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);

            return handler;
        }

        int port = egress.RelayLocalPort
            ?? throw new InvalidOperationException("Egress has no proxy, relay port or agent.");

        // Dial the forwarded loopback port instead of the real endpoint, while
        // leaving the request URI untouched. TLS is negotiated end-to-end with the
        // real host, so SNI (which is what the relay routes on), certificate
        // validation and S3 request signing all continue to see the true endpoint.
        handler.UseProxy = false;
        handler.ConnectCallback = async (context, ct) =>
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        return handler;
    }

    /// <summary>
    /// Validates a proxy URL and fills in the default SOCKS port when omitted.
    /// Public so the connection form can reject a bad value before it is saved.
    /// </summary>
    /// <exception cref="InvalidOperationException">The URL is malformed, uses an
    /// unsupported scheme, or embeds credentials.</exception>
    public static Uri ParseProxyUri(string rawUrl)
    {
        string trimmed = (rawUrl ?? "").Trim();

        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Proxy URL is empty.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"'{trimmed}' is not a valid proxy URL. Expected something like 'socks5://10.0.0.5:1080'.");
        }

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Proxy scheme '{uri.Scheme}' is not supported. Use one of: {string.Join(", ", AllowedSchemes)}.");
        }

        if (uri.Host.Length == 0)
        {
            throw new InvalidOperationException("Proxy URL is missing a host.");
        }

        // Credentials in the URL would be persisted in plaintext alongside the
        // connection; the username field plus the vault-held password is the
        // supported route.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Do not put credentials in the proxy URL — use the proxy username field so the password can be stored in the vault.");
        }

        // Uri does not know a default port for the socks schemes, leaving Port at -1.
        if (uri.Port < 0)
        {
            uri = new UriBuilder(uri) { Port = 1080 }.Uri;
        }

        return uri;
    }

    public void Dispose()
    {
        foreach (SocketsHttpHandler handler in handlers.Values)
        {
            handler.Dispose();
        }

        handlers.Clear();
    }

    /// <summary>
    /// Bridges the pooled proxied handler into the AWS SDK. Caching and disposal
    /// are turned off on the SDK side because <see cref="OpenStackHttpFactory"/>
    /// already owns the handler's lifetime.
    /// </summary>
    private sealed class ProxiedAwsHttpClientFactory(OpenStackHttpFactory owner, ResolvedEgress egress) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => owner.CreateClient(egress);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

        public override string GetConfigUniqueString(IClientConfig clientConfig) => egress.CacheKey;
    }
}
