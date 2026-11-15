using System.Net.Http.Headers;
using System.Text.Json;
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
/// A typed response: the outcome, and the deserialised body when there is one.
///
/// <para>⚠️ <see cref="Body"/> is kept beside <see cref="Value"/> rather than replaced by it.
/// The MCP server hands the raw body to a model to read, so dropping it would force that caller
/// to re-serialise what it had already received.</para>
/// </summary>
public sealed record ApiResponse(bool Success, int StatusCode, string Body)
{
    /// <inheritdoc cref="ApiResult.Partial"/>
    public bool Partial => StatusCode == ApiResult.MultiStatus;

    public static ApiResponse From(ApiResult result) =>
        new(result.Success, result.StatusCode, result.Body);
}

/// <summary>
/// A typed response carrying the body the document says the operation answers with.
/// </summary>
/// <typeparam name="T">The generated response type for this operation.</typeparam>
public sealed record ApiResponse<T>(bool Success, int StatusCode, string Body, T? Value)
{
    /// <inheritdoc cref="ApiResult.Partial"/>
    public bool Partial => StatusCode == ApiResult.MultiStatus;

    /// <summary>
    /// ⚠️ Every generated property carries an explicit <c>[JsonPropertyName]</c> read out of the
    /// document, so no naming policy is involved and none can be changed under the client. That
    /// is what keeps SCIM's capital-R <c>Resources</c> correct here without a special case.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ApiResponse<T> From(ApiResult result)
    {
        if (!result.Success)
        {
            return new(false, result.StatusCode, result.Body, default);
        }

        try
        {
            return new(true, result.StatusCode, result.Body,
                JsonSerializer.Deserialize<T>(result.Body, Options));
        }
        catch (JsonException ex)
        {
            // ⚠️ NOT a success with a null value. A 200 whose body does not match the schema is
            // the API contradicting its own document, and a caller that read Value as null would
            // treat it as "nothing there" — an empty list of clusters rather than a broken
            // contract. The body is kept so the mismatch can be read.
            return new(false, result.StatusCode,
                $"EntKube answered {result.StatusCode} with a body this client could not read as "
                + $"{typeof(T).Name}: {ex.Message}\n\n{result.Body}", default);
        }
    }
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
    /// A bodyless DELETE.
    ///
    /// <para>⚠️ This used to say there was "deliberately no <c>PutAsync</c> here, because every
    /// PUT on the public API carries a JSON body and this client cannot send one". The second
    /// half stopped being true when <see cref="SendJsonAsync"/> was added for the generated
    /// client, so the first half is no longer a decision — it is just the absence of a wrapper.
    /// The paragraph below is kept because what it describes is still exactly true.</para>
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

    /// <summary>
    /// Sends <paramref name="body"/> as JSON. Added for the generated client, which has to be
    /// able to reach every operation the document describes — including the one PUT that carries
    /// a body.
    /// </summary>
    public Task<ApiResult> SendJsonAsync(
        HttpMethod method, string path, object payload, CancellationToken ct = default) =>
        SendAsync(method, path, ct, payload);

    private async Task<ApiResult> SendAsync(
        HttpMethod method, string path, CancellationToken ct, object? payload = null)
    {
        try
        {
            using HttpRequestMessage request = new(method, path.TrimStart('/'));

            if (payload is not null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    System.Text.Encoding.UTF8,
                    "application/json");
            }
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
