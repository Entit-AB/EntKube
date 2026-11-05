using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace EntKube.ApiClient;

/// <summary>The outcome of an API call, kept as a result rather than an exception so tool
/// handlers can hand a readable failure back to the model instead of crashing the server.</summary>
public sealed record ApiResult(bool Success, string Body, int StatusCode)
{
    /// <summary>
    /// The status the API uses to say <em>some of it worked</em>.
    ///
    /// <para><c>POST /apps/{appId}/secrets/sync</c> answers 207 when any binding failed, carrying
    /// <c>failed: true</c> and a per-binding <c>error</c>. ⚠️ 207 is a 2xx, so
    /// <c>HttpResponseMessage.IsSuccessStatusCode</c> is true for it and
    /// <see cref="Success"/> is too — which is right: the request was answered and the body is
    /// the answer. What is NOT right is a client stopping there, because the answer says part of
    /// the work did not happen.</para>
    /// </summary>
    public const int MultiStatus = 207;

    /// <summary>
    /// True when the API answered that part of the work failed. Derived from the status rather
    /// than stored beside it, so the two cannot disagree.
    ///
    /// <para>⚠️ <b>Deliberately separate from <see cref="Success"/> rather than folded into
    /// it.</b> Flipping <c>Success</c> to false for a 207 would make the MCP server report the
    /// whole call as an error and hide a body the model needs to read — the body is where the
    /// partial result lives. A caller that cares (the CLI, which has an exit code to set) asks
    /// this; a caller that hands the body to a reader (the MCP server) does not have to.</para>
    /// </summary>
    public bool Partial => StatusCode == MultiStatus;

    public static ApiResult Failed(string message, int status = 0) => new(false, message, status);
}

/// <summary>
/// Thin REST client for EntKube's public API.
///
/// Shared by every first-party client (the MCP server, the CLI) so the mapping from
/// HTTP status to operator-facing advice stays in one place. Two copies would drift,
/// and the advice for a 403 or a 503 is exactly the kind of thing worth saying the
/// same way everywhere.
///
/// Carries an ordinary scoped API token and talks to the same <c>/api/v1</c> surface
/// as any other client — no first-party client has a privileged path into EntKube.
/// </summary>
public sealed class EntKubeApiClient : IDisposable
{
    private readonly HttpClient http;

    /// <param name="transport">
    /// The message handler to send through. Defaults to the ordinary network stack.
    ///
    /// <para>This exists so a test can learn <b>which route a caller actually requests</b> without
    /// a server. <c>McpEndpointParityTests</c> needs that: an <c>McpTool</c>'s route lives inside
    /// a handler delegate that interpolates it, so the only way to know what a tool requests is to
    /// run it. The alternative — a declarative path template on <c>McpTool</c> beside the handler —
    /// would be the same fact written twice, and two copies of a fact drift. (That is not
    /// hypothetical in this codebase: the scope a route requires was stored twice and had to be
    /// made derived.)</para>
    /// </param>
    public EntKubeApiClient(
        string baseUrl, string token, TimeSpan? timeout = null, HttpMessageHandler? transport = null)
    {
        http = transport is null ? new HttpClient() : new HttpClient(transport);
        Configure(baseUrl, token, timeout);
    }

    private void Configure(string baseUrl, string token, TimeSpan? timeout)
    {
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.Timeout = timeout ?? TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("entkube-mcp/1.0");
    }

    public Task<ApiResult> GetAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, ct);

    public Task<ApiResult> PostAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, ct);

    /// <summary>
    /// A bodyless DELETE. There is deliberately no <c>PutAsync</c> here, because every PUT on the
    /// public API carries a JSON body and this client cannot send one.
    ///
    /// <para>⚠️ <b>The reason first written here was wrong, and wrong in the reassuring
    /// direction.</b> It said a bodyless PUT would be read as "no fields supplied", turning a
    /// partial update into a silent no-op. It would not: <c>CostRateInput</c> is a non-nullable
    /// body parameter whose first four members are non-nullable <c>decimal</c>s, so parameter
    /// binding refuses the request before the handler runs — a 400, which is the safe outcome, not
    /// a quiet one. The genuinely dangerous shape is the one next door: a body of <c>{}</c> binds,
    /// and the four prices take <c>default(decimal)</c>, so it reads as "every price is zero"
    /// rather than "leave them alone". <c>McpEndpointParityTests</c> asserts the 400 so this
    /// paragraph cannot quietly become untrue — a ratchet asserts its number, never its
    /// explanation, so an unasserted reason stays green forever.</para>
    /// </summary>
    public Task<ApiResult> DeleteAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, path, ct);

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
