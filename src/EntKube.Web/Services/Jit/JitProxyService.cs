using System.Net;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Jit;

/// <summary>
/// Serves the API server to a grant holder, over EntKube rather than over the cluster's own
/// endpoint.
///
/// The customer's kubeconfig points here. Their credential is an EntKube token that means nothing
/// to Kubernetes; the cluster credential is the grant's bound ServiceAccount token, which never
/// leaves this process. That indirection is what makes revocation immediate — a revoked row stops
/// the next request here, without waiting for a bound token to expire — and it is what lets every
/// request be recorded against a named person.
/// </summary>
public class JitProxyService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    JitUpstreamClientPool clientPool,
    VaultEncryptionService encryption,
    AuditService auditService,
    ILogger<JitProxyService> logger)
{
    /// <summary>
    /// How often a grant's <see cref="JitGrant.LastUsedAt"/> is written. One kubectl command is
    /// many requests, and a write per request would turn a read-mostly table into a hot one for
    /// information that is only ever read at minute resolution.
    /// </summary>
    public static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Headers that describe the hop rather than the request. Forwarding them would either confuse
    /// the upstream (Host, connection management) or hand it the customer's credential
    /// (Authorization), which is the one header that must be replaced rather than passed through.
    /// </summary>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "Connection", "Keep-Alive", "Proxy-Authenticate",
        "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public async Task HandleAsync(HttpContext http, Guid grantId, string? path)
    {
        CancellationToken ct = http.RequestAborted;

        string? presented = ExtractToken(http.Request);

        (JitGrant? grant, string? clusterToken, string? kubeconfig) =
            await ResolveAsync(grantId, presented, ct);

        if (grant is null)
        {
            // One response for unknown grant, wrong token, expired and revoked alike. Telling them
            // apart would say which guesses were closer, and "this grant exists but is revoked" is
            // itself worth not confirming.
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteStatusAsync(http, HttpStatusCode.Unauthorized,
                "This access grant is not valid. It may have expired or been revoked.");
            return;
        }

        JitPathVerdict verdict = JitPathPolicy.Check(path, grant.Namespace);
        if (!verdict.Allowed)
        {
            logger.LogWarning(
                "JIT grant {GrantId} ({User}) refused {Method} {Path}: {Reason}",
                grant.Id, grant.UserId, http.Request.Method, path, verdict.Reason);

            await auditService.RecordAsync(null, "JitAccessRefused", "JitGrant", grant.Id.ToString(),
                $"{http.Request.Method} /{path} — {verdict.Reason}", grant.UserId, ct);

            await WriteStatusAsync(http, HttpStatusCode.Forbidden, verdict.Reason!);
            return;
        }

        if (clusterToken is null || kubeconfig is null)
        {
            // Approved, but its credential is gone — torn down, or never successfully minted.
            await WriteStatusAsync(http, HttpStatusCode.Unauthorized,
                "This grant has no active cluster credential.");
            return;
        }

        await RecordUseAsync(grant, http.Request.Method, path, ct);
        await ForwardAsync(http, grant, clusterToken, kubeconfig, path, ct);
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the grant behind a presented token, or returns nulls if anything about it is wrong.
    ///
    /// The grant id in the URL is not trusted on its own — the token is matched against it, so a
    /// valid token for one grant cannot be pointed at another by editing the kubeconfig.
    /// </summary>
    private async Task<(JitGrant?, string?, string?)> ResolveAsync(
        Guid grantId, string? presented, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presented)) return (null, null, null);

        string hash = KubernetesJitProvisioner.Hash(presented.Trim());

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        JitGrant? grant = await db.JitGrants
            .Include(g => g.KubernetesCluster)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct);

        if (grant?.TokenHash is null) return (null, null, null);

        // Fixed-time comparison. The hash is not a secret an attacker could usefully recover by
        // timing, but the habit costs nothing and the alternative invites the question.
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(hash), Convert.FromHexString(grant.TokenHash)))
        {
            return (null, null, null);
        }

        if (!grant.IsLiveAt(DateTime.UtcNow)) return (null, null, null);

        string? clusterToken = JitClusterTokenStore.Read(encryption, grant);
        return (grant, clusterToken, grant.KubernetesCluster?.Kubeconfig);
    }

    /// <summary>Reads the bearer token, ignoring anything that is not one.</summary>
    public static string? ExtractToken(HttpRequest request)
    {
        string? header = request.Headers.Authorization.FirstOrDefault();

        return header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    // ── Forwarding ────────────────────────────────────────────────────────────

    private async Task ForwardAsync(
        HttpContext http, JitGrant grant, string clusterToken, string kubeconfig,
        string? path, CancellationToken ct)
    {
        JitUpstreamClientPool.Upstream upstream;
        try
        {
            upstream = clientPool.Get(kubeconfig);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not build an upstream client for JIT grant {GrantId}", grant.Id);
            await WriteStatusAsync(http, HttpStatusCode.BadGateway,
                "The cluster could not be reached.");
            return;
        }

        Uri target = new(upstream.BaseAddress, "/" + (path ?? "").TrimStart('/') + http.Request.QueryString);

        using HttpRequestMessage request = new(new HttpMethod(http.Request.Method), target);

        if (CanHaveBody(http.Request.Method))
            request.Content = new StreamContent(http.Request.Body);

        foreach (var header in http.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
        }

        // The swap. Everything above this line carried the customer's identity; everything below
        // carries the grant's, which is confined to one namespace by its RoleBinding.
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", clusterToken);

        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead, not the default: a watch and a `logs -f` are responses that
            // never finish, and buffering one would hang the request instead of streaming it.
            response = await upstream.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // The client hung up. Nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JIT proxy request failed for grant {GrantId}", grant.Id);
            await WriteStatusAsync(http, HttpStatusCode.BadGateway, "The cluster could not be reached.");
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key)) continue;
                http.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in response.Content.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key)) continue;
                http.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Kestrel sets this itself, and a forwarded value conflicts with a chunked response.
            http.Response.Headers.Remove("transfer-encoding");

            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(ct);
                await body.CopyToAsync(http.Response.Body, ct);
                await http.Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A watch ends when the client stops watching. Expected, not an error.
            }
        }
    }

    /// <summary>
    /// Whether the method may carry a body. GET with a body is legal HTTP and meaningless to the
    /// API server, and attaching an empty StreamContent to one makes some servers wait for it.
    /// </summary>
    private static bool CanHaveBody(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
        && !HttpMethods.IsDelete(method) && !HttpMethods.IsOptions(method);

    // ── Recording ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the audit trail for one request and stamps the grant as used.
    ///
    /// Discovery paths are logged but not audited: kubectl fetches them before every command, they
    /// say nothing about what the customer looked at, and burying the requests that matter under
    /// them makes the trail less useful, not more.
    /// </summary>
    private async Task RecordUseAsync(JitGrant grant, string method, string? path, CancellationToken ct)
    {
        logger.LogInformation(
            "JIT grant {GrantId} ({User}) {Method} /{Path}", grant.Id, grant.UserId, method, path);

        bool isDiscovery = JitPathPolicy.Check(path, grant.Namespace).Allowed
                           && !(path ?? "").Contains("/namespaces/", StringComparison.Ordinal);

        if (!isDiscovery)
        {
            await auditService.RecordAsync(null, "JitAccessUsed", "JitGrant", grant.Id.ToString(),
                $"{method} /{path}", grant.UserId, ct);
        }

        DateTime now = DateTime.UtcNow;
        if (grant.LastUsedAt is not null && now - grant.LastUsedAt.Value < LastUsedThrottle) return;

        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            await db.JitGrants
                .Where(g => g.Id == grant.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now), ct);

            grant.LastUsedAt = now;
        }
        catch (Exception ex)
        {
            // Never fail a request because the bookkeeping failed.
            logger.LogWarning(ex, "Stamping LastUsedAt for JIT grant {GrantId} failed", grant.Id);
        }
    }

    /// <summary>
    /// Answers in the API server's own error shape, so kubectl prints the reason instead of
    /// "error: an error on the server has prevented the request from succeeding".
    /// </summary>
    private static async Task WriteStatusAsync(HttpContext http, HttpStatusCode code, string message)
    {
        http.Response.StatusCode = (int)code;
        http.Response.ContentType = "application/json";

        await http.Response.WriteAsJsonAsync(new
        {
            kind = "Status",
            apiVersion = "v1",
            status = "Failure",
            message,
            reason = code == HttpStatusCode.Unauthorized ? "Unauthorized" : "Forbidden",
            code = (int)code,
        }, http.RequestAborted);
    }
}
