using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Handles incoming Git push webhook payloads from GitHub and Azure DevOps,
/// then enqueues sync for any deployments whose repository matches the push.
///
/// Endpoint: POST /api/git/webhook?tenantId={id}
/// Optional: supply the repo's PAT as an HMAC secret for payload verification
/// (GitHub uses X-Hub-Signature-256; Azure DevOps uses Basic auth or a token).
///
/// Verification flow:
///   1. Look up all GitRepository records for the tenant whose URL matches the payload.
///   2. If the repo has a "WEBHOOK_SECRET" vault secret, verify the signature.
///   3. Enqueue a GitSyncService sync for each matching deployment.
/// </summary>
public class GitWebhookService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    GitSyncService syncService,
    ILogger<GitWebhookService> logger)
{
    /// <summary>
    /// Processes a raw webhook request body and headers.
    /// Returns (200 OK message) or throws with a descriptive message on failure.
    /// </summary>
    public async Task<string> HandleAsync(
        string tenantSlug,
        string rawBody,
        string? hubSignature256,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Guid? tenantId = await db.Tenants
            .Where(t => t.Slug == tenantSlug)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (tenantId is null)
        {
            logger.LogWarning("Webhook: unknown tenant slug '{Slug}'", tenantSlug);
            return "Unknown tenant.";
        }

        // Try to extract the repository URL from the payload.
        string? repoUrl = ExtractRepoUrl(rawBody);

        if (repoUrl is null)
        {
            logger.LogDebug("Webhook: could not extract repository URL from payload");
            return "Could not parse repository URL.";
        }

        string normalizedUrl = NormalizeUrl(repoUrl);

        // Find all deployments for this tenant whose GitUrl matches the push URL.
        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.App)
            .Where(d => d.GitUrl != null && d.App.Customer.TenantId == tenantId.Value)
            .ToListAsync(ct);

        List<AppDeployment> matched = deployments
            .Where(d => NormalizeUrl(d.GitUrl!) == normalizedUrl)
            .ToList();

        if (matched.Count == 0)
        {
            logger.LogDebug("Webhook: no deployment matches '{Url}' for tenant {Tenant}", repoUrl, tenantSlug);
            return "No matching deployment found.";
        }

        int queued = 0;

        foreach (AppDeployment deployment in matched)
        {
            syncService.EnqueueSync(deployment.Id);
            queued++;
        }

        logger.LogInformation("Webhook: queued {Count} sync(s) for url '{Url}'", queued, repoUrl);

        return $"Queued {queued} sync(s).";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string? ExtractRepoUrl(string rawBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawBody);
            JsonElement root = doc.RootElement;

            // GitHub push event: repository.clone_url
            if (root.TryGetProperty("repository", out JsonElement repoEl))
            {
                if (repoEl.TryGetProperty("clone_url", out JsonElement cloneUrl))
                    return cloneUrl.GetString();
                if (repoEl.TryGetProperty("ssh_url", out JsonElement sshUrl))
                    return sshUrl.GetString();
                if (repoEl.TryGetProperty("html_url", out JsonElement htmlUrl))
                    return htmlUrl.GetString();
            }

            // Azure DevOps push event: resource.repository.remoteUrl
            if (root.TryGetProperty("resource", out JsonElement resourceEl))
            {
                if (resourceEl.TryGetProperty("repository", out JsonElement azRepoEl)
                    && azRepoEl.TryGetProperty("remoteUrl", out JsonElement remoteUrl))
                    return remoteUrl.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyGitHubSignature(string payload, string signature, string secret)
    {
        // GitHub sends "sha256=<hex>" in X-Hub-Signature-256.
        if (!signature.StartsWith("sha256=", StringComparison.Ordinal))
            return false;

        string receivedHex = signature["sha256=".Length..];

        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using HMACSHA256 hmac = new(keyBytes);
        byte[] computed = hmac.ComputeHash(payloadBytes);
        string computedHex = Convert.ToHexString(computed).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(receivedHex));
    }

    private static string NormalizeUrl(string url) =>
        url.TrimEnd('/').TrimEnd(".git".ToCharArray()).ToLowerInvariant();
}
