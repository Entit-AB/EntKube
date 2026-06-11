using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using k8s;
using k8s.Models;

namespace EntKube.Web.Services;

/// <summary>
/// Routes Amazon S3 SDK requests through the Kubernetes API server service proxy so that
/// cluster-internal MinIO pods are reachable from EntKube.Web (same mechanism as kubectl proxy).
///
/// AWS Signature V4 includes the Host header. The proxy handler signs requests for
/// podIP:podPort (what MinIO actually receives) and rewrites the URL to the K8s service-proxy
/// path before forwarding. The signature stays valid because K8s forwards Host=podIP:podPort
/// to the backend pod.
///
/// Multiple service candidates are tried in priority order (headless → ClusterIP).
/// On a 5xx from K8s the handler advances to the next candidate transparently, so the
/// caller never needs to know which service/port ultimately worked.
/// </summary>
public static class KubernetesS3Proxy
{
    // ── URL Parsing ──

    /// <summary>
    /// Parses a cluster-internal service URL of the form
    /// http(s)://svcName.namespace.svc.cluster.local[:port].
    /// Returns null if the URL doesn't match that pattern.
    /// </summary>
    public static (string Service, string Namespace, int Port)? ParseInternalServiceUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;

        string[] parts = uri.Host.Split('.');
        if (parts.Length < 3 || !parts[2].Equals("svc", StringComparison.OrdinalIgnoreCase))
            return null;

        return (parts[0], parts[1], uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80));
    }

    // ── Open ──

    /// <summary>
    /// Resolves a ready MinIO pod, builds a correctly-signed S3 client that routes
    /// through the Kubernetes API server service proxy, and returns both as a single
    /// disposable. Returns null if the endpoint URL cannot be parsed as a cluster-internal URL.
    ///
    /// The underlying handler tries multiple K8s service candidates in priority order
    /// (headless service first, then ClusterIP). On a 5xx from K8s it automatically
    /// advances to the next candidate so callers do not need retry logic.
    ///
    /// Usage:
    ///   using var proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, endpoint, key, secret);
    ///   var result = await proxied.S3.ListBucketsAsync();
    /// </summary>
    public static async Task<ProxiedS3Client?> OpenAsync(
        string kubeconfig,
        string internalEndpoint,
        string accessKey,
        string secretKey,
        string region = "us-east-1",
        CancellationToken ct = default)
    {
        var parsed = ParseInternalServiceUrl(internalEndpoint);
        if (parsed is null)
            return null;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration k8sCfg = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        Kubernetes k8s = new(k8sCfg);

        (string podName, string podIP) = await FindReadyPodAsync(
            k8s, parsed.Value.Namespace, parsed.Value.Service, ct);

        bool endpointTls = internalEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        string svcSchemePrefix = endpointTls ? "https:" : "";
        string k8sBase = k8s.BaseUri.ToString().TrimEnd('/');
        string ns = parsed.Value.Namespace;

        // Build an ordered list of proxy-base URLs to try.  For MinIO operator the
        // headless service ({tenant}-hl) directly exposes the pod port without
        // ClusterIP translation; the main service ({tenant}) maps 443→9000.
        // The retrying handler will try them in order and skip 5xx responses.
        IReadOnlyList<(string SvcName, int SvcPort)> candidates =
            await ResolveServiceCandidatesAsync(k8s, ns, parsed.Value.Service, parsed.Value.Port, ct);

        List<string> proxyCandidates = [..candidates
            .Select(c => $"{k8sBase}/api/v1/namespaces/{ns}/services/{svcSchemePrefix}{c.SvcName}:{c.SvcPort}/proxy")];

        // Sign requests for the host MinIO actually sees: podIP:podPort.
        string signingScheme = endpointTls ? "https" : "http";
        string signingBaseUrl = $"{signingScheme}://{podIP}:{parsed.Value.Port}";

        HttpClient proxyHttpClient = new(new RetryingKubernetesProxyHandler(k8s.HttpClient, proxyCandidates));

        AmazonS3Config config = new()
        {
            ServiceURL           = signingBaseUrl,
            ForcePathStyle       = true,
            AuthenticationRegion = region,
            UseHttp              = !endpointTls,
            Timeout              = TimeSpan.FromSeconds(30),
            MaxErrorRetry        = 0,
            HttpClientFactory    = new SingletonHttpClientFactory(proxyHttpClient)
        };

        AmazonS3Client s3 = new(new BasicAWSCredentials(accessKey, secretKey), config);
        return new ProxiedS3Client(k8s, s3, proxyHttpClient);
    }

    // ── Service Candidate Resolution ──

    /// <summary>
    /// Returns an ordered list of (serviceName, servicePort) candidates to try as
    /// K8s service-proxy targets for the given pod port.
    ///
    /// Priority:
    ///   1. Headless service ({svc}-hl) with a port that directly equals podPort
    ///   2. Headless service ({svc}-hl) with a port whose targetPort resolves to podPort
    ///   3. Main service ({svc}) with a port whose targetPort resolves to podPort
    ///   4. Main service ({svc}) with a port that directly equals podPort
    ///   5. Main service ({svc}) first port as last resort
    ///   6. Hard fallback: ({svc}, endpointTls ? 443 : podPort)
    /// </summary>
    private static async Task<IReadOnlyList<(string SvcName, int SvcPort)>> ResolveServiceCandidatesAsync(
        Kubernetes k8s, string ns, string svc, int podPort, CancellationToken ct)
    {
        List<(string, int)> results = [];

        // 1 & 2: Headless service.
        try
        {
            V1Service hl = await k8s.CoreV1.ReadNamespacedServiceAsync($"{svc}-hl", ns, cancellationToken: ct);
            IList<V1ServicePort>? hlPorts = hl.Spec?.Ports;

            V1ServicePort? direct = hlPorts?.FirstOrDefault(p => p.Port == podPort);
            if (direct is not null)
                results.Add(($"{svc}-hl", direct.Port));
            else
            {
                V1ServicePort? byTarget = hlPorts?.FirstOrDefault(p => p.TargetPort?.Value == podPort.ToString());
                if (byTarget is not null)
                    results.Add(($"{svc}-hl", byTarget.Port));
            }
        }
        catch { }

        // 3, 4 & 5: Main ClusterIP service.
        try
        {
            V1Service main = await k8s.CoreV1.ReadNamespacedServiceAsync(svc, ns, cancellationToken: ct);
            IList<V1ServicePort>? ports = main.Spec?.Ports;

            V1ServicePort? byTarget = ports?.FirstOrDefault(p => p.TargetPort?.Value == podPort.ToString());
            if (byTarget is not null)
            {
                results.Add((svc, byTarget.Port));
            }
            else
            {
                V1ServicePort? direct = ports?.FirstOrDefault(p => p.Port == podPort);
                int fallbackPort = direct?.Port ?? ports?.FirstOrDefault()?.Port ?? podPort;
                results.Add((svc, fallbackPort));
            }
        }
        catch { }

        // Hard fallback so there is always at least one candidate.
        if (results.Count == 0)
            results.Add((svc, podPort));

        return results;
    }

    // ── Pod Discovery ──

    private static async Task<(string PodName, string PodIP)> FindReadyPodAsync(
        Kubernetes k8s, string ns, string tenantName, CancellationToken ct)
    {
        // Primary: MinIO operator labels pods with v1.min.io/tenant=<tenantName>.
        V1PodList pods = await k8s.CoreV1.ListNamespacedPodAsync(
            ns, labelSelector: $"v1.min.io/tenant={tenantName}", cancellationToken: ct);

        var found = PickReadyPodWithIP(pods);
        if (found is not null)
            return found.Value;

        // Fallback: standalone MinIO or different label convention.
        V1PodList all = await k8s.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
        found = PickReadyPodWithIP(all);

        return found ?? throw new InvalidOperationException(
            $"No running MinIO pods with a pod IP found in namespace '{ns}'. " +
            "Ensure MinIO is healthy before accessing bucket management.");
    }

    private static (string PodName, string PodIP)? PickReadyPodWithIP(V1PodList podList)
    {
        foreach (V1Pod pod in podList.Items)
        {
            if (pod.Status?.Phase != "Running") continue;
            if (string.IsNullOrEmpty(pod.Status.PodIP)) continue;
            if (pod.Status.ContainerStatuses?.All(cs => cs.Ready) != true) continue;
            return (pod.Metadata.Name, pod.Status.PodIP);
        }
        return null;
    }

    // ── Retrying Proxy Handler ──

    /// <summary>
    /// Intercepts Amazon SDK requests and rewrites the URL to successive K8s service-proxy
    /// candidates until one succeeds (non-5xx) or all are exhausted.
    ///
    /// - Never throws: on total failure the last 5xx response is returned so the AWS SDK
    ///   can produce a proper AmazonServiceException that callers can catch.
    /// - Content bodies are buffered before the first attempt so the stream can be
    ///   replayed on each retry.
    /// - Host is stripped from forwarded headers so the K8s API server does not reject
    ///   the connection for a hostname mismatch.
    /// </summary>
    private sealed class RetryingKubernetesProxyHandler(
        HttpClient k8sClient,
        IReadOnlyList<string> proxyCandidates) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer body upfront so it can be resent on each candidate retry.
            byte[]? bodyBytes = null;
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null;
            if (request.Content is not null)
            {
                bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                contentHeaders = [..request.Content.Headers];
            }

            HttpResponseMessage? lastResponse = null;

            foreach (string proxyBase in proxyCandidates)
            {
                string pathAndQuery = request.RequestUri?.PathAndQuery ?? "/";
                string targetUrl = proxyBase.TrimEnd('/') + pathAndQuery;

                HttpRequestMessage proxied = new(request.Method, targetUrl);

                // Copy all headers except Host (K8s API validates Host against its own name).
                foreach (KeyValuePair<string, IEnumerable<string>> h in request.Headers)
                {
                    if (string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase))
                        continue;
                    proxied.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                if (bodyBytes is not null)
                {
                    ByteArrayContent body = new(bodyBytes);
                    foreach (KeyValuePair<string, IEnumerable<string>> ch in contentHeaders!)
                        body.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
                    proxied.Content = body;
                }

                HttpResponseMessage response = await k8sClient.SendAsync(proxied, cancellationToken);

                if ((int)response.StatusCode < 500)
                    return response;

                // Dispose the previous failing response before moving to the next candidate.
                lastResponse?.Dispose();
                lastResponse = response;
            }

            // All candidates exhausted — return the last 5xx response so the AWS SDK
            // raises a proper AmazonServiceException (catchable by callers).
            return lastResponse!;
        }
    }

    // ── Amazon SDK HttpClientFactory injection ──

    private sealed class SingletonHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}

/// <summary>
/// A paired K8s client + proxied S3 client. Dispose to release all resources.
/// </summary>
public sealed class ProxiedS3Client(Kubernetes k8s, AmazonS3Client s3, HttpClient proxyHttpClient) : IDisposable
{
    public AmazonS3Client S3 { get; } = s3;

    public void Dispose()
    {
        S3.Dispose();
        proxyHttpClient.Dispose();
        k8s.Dispose();
    }
}
