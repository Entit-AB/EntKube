using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace EntKube.Mcp;

/// <summary>The outcome of an API call, kept as a result rather than an exception so tool
/// handlers can hand a readable failure back to the model instead of crashing the server.</summary>
public sealed record ApiResult(bool Success, string Body, int StatusCode)
{
    public static ApiResult Failed(string message, int status = 0) => new(false, message, status);
}

/// <summary>
/// Thin REST client for EntKube's public API.
///
/// Talks to the same <c>/api/v1</c> surface as any other client and carries an ordinary
/// scoped API token — the MCP server has no privileged path into EntKube, so whatever
/// the token cannot do, the model cannot do either.
/// </summary>
public sealed class EntKubeApiClient : IDisposable
{
    private readonly HttpClient http;

    public EntKubeApiClient(string baseUrl, string token, TimeSpan? timeout = null)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("entkube-mcp/1.0");
    }

    public Task<ApiResult> GetAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, ct);

    public Task<ApiResult> PostAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, ct);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(method, path.TrimStart('/'));
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return new ApiResult(true, body, (int)response.StatusCode);
            }

            // Translate the statuses this API uses deliberately into advice the model can
            // act on, rather than passing through a bare status number.
            string explanation = (int)response.StatusCode switch
            {
                401 => "The EntKube API token is missing, invalid, expired or revoked.",
                403 => "The API token lacks the scope this operation requires. "
                     + "Grant the scope on the token in EntKube under the tenant's API tokens tab.",
                404 => "Not found — the id may belong to another tenant, or may not exist.",
                503 => "EntKube has not completed a background sweep for this data yet. "
                     + "This is not an error: ask again later, or trigger a scan in the UI.",
                _ => $"EntKube returned HTTP {(int)response.StatusCode}.",
            };

            return new ApiResult(false, $"{explanation}\n\n{body}".TrimEnd(), (int)response.StatusCode);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult.Failed("The request to EntKube timed out.");
        }
        catch (HttpRequestException ex)
        {
            return ApiResult.Failed($"Could not reach EntKube: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a query string from non-null parameters, escaping each value. Returns an
    /// empty string when nothing is set, so callers can append it unconditionally.
    /// </summary>
    public static string QueryString(params (string Key, string? Value)[] parameters)
    {
        List<string> parts = [];
        foreach ((string key, string? value) in parameters)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Reads a required string argument from a tool call's arguments object.
    /// Throws <see cref="ArgumentException"/> so the caller can turn it into a tool error.
    /// </summary>
    public static string RequireString(JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"The '{name}' argument is required.")
            : value.Trim();
    }

    public static string? OptionalString(JsonObject? args, string name)
    {
        JsonNode? node = args?[name];
        if (node is null) return null;

        // Accept a bool or number as well as a string: models routinely pass the right
        // value in the wrong JSON type, and rejecting that is unhelpful pedantry.
        try { return node.GetValue<string>(); }
        catch (InvalidOperationException) { return node.ToJsonString().Trim('"'); }
    }

    public void Dispose() => http.Dispose();
}
