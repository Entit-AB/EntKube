using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s;

namespace EntKube.Web.Services.Jit;

/// <summary>
/// Supplies the <see cref="HttpClient"/> the JIT proxy uses to reach an API server, built so that
/// it can only ever authenticate as the token it is given.
///
/// This exists because of a trap. EntKube's stored kubeconfig usually authenticates with a client
/// certificate, and Kubernetes runs its authenticators in order with x509 first — so a request that
/// presents both a valid client certificate and a bearer token is authenticated as the
/// <em>certificate</em>, and the token is never looked at. Forwarding a customer's request over a
/// connection built from the ordinary kubeconfig would therefore execute it as cluster-admin while
/// appearing, everywhere in the code, to execute it as the grant's ServiceAccount.
///
/// So the handler here is constructed deliberately: the CA is taken from the kubeconfig so the
/// server is still verified, and the client certificate is deliberately not. The only credential
/// that can reach the API server through this client is the bearer token the caller attaches.
/// </summary>
public sealed class JitUpstreamClientPool(ILogger<JitUpstreamClientPool> logger) : IDisposable
{
    /// <summary>Rebuild an entry once it is this old, so a rotated CA is picked up.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// No timeout on the client itself. A watch or a <c>logs -f</c> is a long-lived response by
    /// design, and a client-side timeout would cut it off mid-stream; the request's own
    /// cancellation token is what ends it.
    /// </summary>
    private static readonly TimeSpan NoTimeout = Timeout.InfiniteTimeSpan;

    public sealed record Upstream(HttpClient Client, Uri BaseAddress);

    private sealed record Entry(HttpClient Client, Uri BaseAddress, HttpMessageHandler Handler, DateTime CreatedUtc);

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private bool disposed;

    /// <summary>
    /// Returns a client for the cluster this kubeconfig describes. The caller must not dispose it —
    /// the pool owns its lifetime.
    /// </summary>
    public Upstream Get(string kubeconfig)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kubeconfig)));

        if (entries.TryGetValue(key, out Entry? existing))
        {
            if (DateTime.UtcNow - existing.CreatedUtc < MaxAge)
                return new Upstream(existing.Client, existing.BaseAddress);

            if (entries.TryRemove(key, out Entry? stale))
                Retire(stale);
        }

        Entry created = Build(kubeconfig);
        Entry actual = entries.GetOrAdd(key, created);

        // Lost a race — another thread built one first. Drop ours rather than leaking it.
        if (!ReferenceEquals(actual, created)) Retire(created);

        return new Upstream(actual.Client, actual.BaseAddress);
    }

    private Entry Build(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config =
            KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);

        HttpClientHandler handler = new()
        {
            // Redirects are not followed. The API server does not issue them, and following one
            // would send the grant's token to whatever the redirect named.
            AllowAutoRedirect = false,
        };

        // ClientCertificates is deliberately left empty. See the class comment: presenting one
        // would make the API server authenticate the certificate and ignore the bearer token.

        if (config.SkipTlsVerify)
        {
            // Inherited from the stored kubeconfig rather than chosen here. Worth a warning: it
            // means the proxy cannot tell the real API server from anything that answers on its
            // address, and the grant's token is what would be handed over.
            logger.LogWarning(
                "JIT upstream for {Host} skips TLS verification because its kubeconfig does",
                config.Host);
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (config.SslCaCerts is { Count: > 0 })
        {
            X509Certificate2Collection roots = config.SslCaCerts;
            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, chain, errors) => ValidateAgainst(roots, certificate, chain, errors);
        }

        HttpClient client = new(handler, disposeHandler: false) { Timeout = NoTimeout };

        return new Entry(client, new Uri(config.Host), handler, DateTime.UtcNow);
    }

    /// <summary>
    /// Verifies the server certificate against the kubeconfig's CA rather than the machine's trust
    /// store. A cluster CA is almost always private, so the default validator would reject it —
    /// and relaxing the check instead is how "skip TLS verify" ends up switched on everywhere.
    /// </summary>
    private static bool ValidateAgainst(
        X509Certificate2Collection roots,
        X509Certificate2? certificate,
        X509Chain? chain,
        System.Net.Security.SslPolicyErrors errors)
    {
        if (certificate is null) return false;

        // A name mismatch is not something a custom root can excuse — only the chain is being
        // re-rooted here, not the identity check.
        if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch))
            return false;

        using X509Chain verification = new();
        verification.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        verification.ChainPolicy.CustomTrustStore.AddRange(roots);
        verification.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        if (chain is not null)
        {
            foreach (X509ChainElement element in chain.ChainElements)
                verification.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        return verification.Build(certificate);
    }

    private static void Retire(Entry entry)
    {
        entry.Client.Dispose();
        entry.Handler.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (Entry entry in entries.Values) Retire(entry);
        entries.Clear();
    }
}
