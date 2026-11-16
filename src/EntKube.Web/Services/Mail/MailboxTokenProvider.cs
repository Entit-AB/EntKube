using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Access tokens for a mailbox whose mail server authenticates against OIDC.
///
/// <para>A mail server whose directory is OIDC accepts nothing else. Upstream: "one directory is
/// resolved per domain and it serves every protocol and both credential types", and "only an
/// OIDC-type directory can validate bearer tokens, and only LDAP or SQL can validate passwords".
/// So an account password and an app password are equally useless there — not stored wrongly, but
/// unverifiable — and a bearer token is the only credential a poller can present.</para>
///
/// <para>Client credentials rather than a password grant, because there is no user: the identity is
/// the client, and the mailbox it maps to comes from a claim the client's own mapper hardcodes.</para>
/// </summary>
public class MailboxTokenProvider(
    IHttpClientFactory httpFactory,
    ILogger<MailboxTokenProvider> logger)
{
    /// <summary>
    /// The bearer token for a mailbox whose directory is OIDC, from that mailbox's own client
    /// credentials.
    ///
    /// <para>⚠️ <b>Two callers read this credential and they disagreed.</b> The poller read
    /// <c>OAUTH_CLIENT_SECRET</c>; the submission path in <c>SmtpSettings</c> read
    /// <c>PASSWORD</c> — the mailbox's IMAP password — and sent it to the token endpoint as a
    /// client secret. An OIDC directory accepts only bearer tokens, so there was no password
    /// fallback to mask it: outbound support mail could not authenticate at all, while fetching
    /// worked perfectly.</para>
    ///
    /// <para>⚠️ <b>This is not yet the only place, and saying so matters more than the tidiness
    /// would.</b> <c>SupportMailboxService</c> still acquires its own token inline, because it
    /// reports two distinct failures to the operator — no service account, and no secret for one
    /// — and routing it through here would flatten both into a null. What IS single is the vault
    /// accessor: both callers now read <c>GetSupportMailboxOAuthSecretAsync</c>, which is the one
    /// place the secret's NAME appears. The remaining duplication is the token request, not the
    /// credential.</para>
    ///
    /// <para><paramref name="fallbackScopes"/> is a parameter rather than a default here because
    /// the two call sites disagreed about that too — the poller fell back to <c>""</c> and the
    /// submission path to <c>"openid email"</c>. That is a real difference in what is requested
    /// and not one to settle silently while fixing the secret, so each caller still passes its
    /// own and the disagreement is visible rather than averaged.</para>
    /// </summary>
    public async Task<string?> ForMailboxAsync(
        VaultService vault, SupportMailbox mailbox, string fallbackScopes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mailbox.OAuthClientId)
            || string.IsNullOrWhiteSpace(mailbox.OAuthTokenEndpoint))
        {
            return null;
        }

        // ⚠️ The OAuth secret, not the mailbox password. They are two different vault entries —
        // VaultService.SupportMailboxOAuthSecretName and SupportMailboxPasswordName — and the
        // token endpoint rejects the wrong one as an invalid client.
        string? clientSecret = await vault.GetSupportMailboxOAuthSecretAsync(
            mailbox.TenantId, mailbox.Id, ct);

        return string.IsNullOrEmpty(clientSecret)
            ? null
            : await GetAsync(
                mailbox.Id, mailbox.OAuthTokenEndpoint!, mailbox.OAuthClientId!, clientSecret,
                mailbox.OAuthScopes ?? fallbackScopes, ct);
    }

    /// <summary>
    /// Tokens live minutes and a poll happens every few of them, so a cache turns one token request
    /// per poll into one per token lifetime. Keyed by mailbox, because two mailboxes are two clients.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, CachedToken> Cache = new();

    /// <summary>
    /// How long before expiry a token stops being reused.
    ///
    /// <para>A token that passes this check and then expires in flight fails the connection, and the
    /// poll reports a broken mailbox rather than retrying — so the margin is generous relative to how
    /// long a connection takes to open.</para>
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private sealed record CachedToken(string Token, DateTime ExpiresAt);

    /// <summary>
    /// A token for this mailbox, from cache when one is still good for long enough.
    /// </summary>
    /// <param name="mailboxId">Cache key. One client per mailbox.</param>
    /// <param name="tokenEndpoint">The realm's token endpoint.</param>
    /// <param name="clientId">The service account client.</param>
    /// <param name="clientSecret">Its secret, read from the vault by the caller.</param>
    /// <param name="scopes">
    /// Scopes the mail server's directory requires. Requested explicitly because the directory
    /// validates them — its default is <c>openid email</c> — and a client-credentials token carries
    /// only what the client is configured to grant.
    /// </param>
    public async Task<string> GetAsync(
        Guid mailboxId, string tokenEndpoint, string clientId, string clientSecret, string scopes,
        CancellationToken ct = default)
    {
        if (Cache.TryGetValue(mailboxId, out CachedToken? cached)
            && cached.ExpiresAt - ExpiryMargin > DateTime.UtcNow)
        {
            return cached.Token;
        }

        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("client_secret", clientSecret),
        ];

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            form.Add(new("scope", scopes.Trim()));
        }

        HttpClient http = httpFactory.CreateClient(nameof(MailboxTokenProvider));

        HttpResponseMessage resp = await http.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
            Headers = { Accept = { new MediaTypeWithQualityHeaderValue("application/json") } },
        }, ct);

        string body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // The body carries the provider's own error, which is the useful half — an unknown client,
            // a wrong secret and a refused scope are three different fixes and read identically
            // without it.
            throw new InvalidOperationException(
                $"The identity provider refused a token for '{clientId}': {(int)resp.StatusCode} {body}");
        }

        using JsonDocument doc = JsonDocument.Parse(body);

        string token = doc.RootElement.TryGetProperty("access_token", out JsonElement t)
            ? t.GetString() ?? ""
            : "";

        if (token.Length == 0)
        {
            throw new InvalidOperationException(
                $"The identity provider answered without an access token for '{clientId}': {body}");
        }

        // Absent expires_in, assume the shortest thing worth caching rather than the longest: a token
        // cached past its life fails every poll until something evicts it.
        int lifetime = doc.RootElement.TryGetProperty("expires_in", out JsonElement e)
            && e.TryGetInt32(out int seconds) && seconds > 0
                ? seconds
                : 60;

        Cache[mailboxId] = new CachedToken(token, DateTime.UtcNow.AddSeconds(lifetime));

        logger.LogDebug(
            "Minted a mail access token for client {Client}, good for {Lifetime}s.", clientId, lifetime);

        return token;
    }

    /// <summary>
    /// Drops the cached token, so the next poll asks for a new one.
    ///
    /// <para>Called when a connection is refused: a token can stop working before it expires — the
    /// client disabled, its secret rotated, the mailbox renamed — and without this the poller would
    /// keep presenting the same rejected token until the cache aged out on its own.</para>
    /// </summary>
    public static void Forget(Guid mailboxId) => Cache.TryRemove(mailboxId, out _);
}
