using System.Net.Http.Headers;
using System.Net.Http.Json;
using EntKube.Web.Services;

namespace EntKube.TelemetryNode;

/// <summary>
/// Calls another node's query API. The shared plumbing behind <see cref="HttpLogBackend"/> and
/// <see cref="HttpTraceBackend"/>: bearer auth, JSON, and turning transport failures into failed results
/// rather than exceptions.
///
/// Results, not exceptions, because the caller is a federation that can still return the half it owns —
/// an indexer restarting should cost you the newest unsealed minutes, not the whole query.
///
/// The client comes from <see cref="IHttpClientFactory"/> per call rather than being held for the object's
/// lifetime, so the factory's handler rotation still applies. That matters here: the indexer is reached by
/// Service DNS, and a Service recreated with a new ClusterIP would otherwise be cached forever.
/// </summary>
public sealed class NodeHttpApi(
    IHttpClientFactory clients, string clientName, string routePrefix, NodeOptions options, ILogger logger)
{
    /// <summary>Which route group on the remote node this instance targets, e.g. <c>internal/traces</c>.</summary>
    public string RoutePrefix => routePrefix;

    public async Task<KubernetesOperationResult<T>> GetAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using HttpClient http = clients.CreateClient(clientName);
            using HttpRequestMessage request = new(HttpMethod.Get, $"{routePrefix}/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.QueryToken);
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Remote query {Prefix}/{Path} failed.", routePrefix, path);
            return KubernetesOperationResult<T>.Failure(ex.Message);
        }
    }

    public async Task<KubernetesOperationResult<T>> PostAsync<T, TBody>(string path, TBody body, CancellationToken ct)
    {
        try
        {
            using HttpClient http = clients.CreateClient(clientName);
            using HttpRequestMessage request = new(HttpMethod.Post, $"{routePrefix}/{path}")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.QueryToken);
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Remote query {Prefix}/{Path} failed.", routePrefix, path);
            return KubernetesOperationResult<T>.Failure(ex.Message);
        }
    }

    private static async Task<KubernetesOperationResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            return KubernetesOperationResult<T>.Failure($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }

        T? value = await response.Content.ReadFromJsonAsync<T>(ct);
        return value is null
            ? KubernetesOperationResult<T>.Failure("The remote node returned an empty body.")
            : KubernetesOperationResult<T>.Success(value);
    }
}
