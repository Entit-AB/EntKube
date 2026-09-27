using System.Net;
using System.Text;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Minting the bearer tokens a support mailbox authenticates with, where its mail server's directory
/// is OIDC and so validates nothing else.
/// </summary>
public class MailboxTokenProviderTests
{
    private const string TokenEndpoint = "https://sso.example.com/realms/mail/protocol/openid-connect/token";

    /// <summary>Answers the token endpoint, and counts how many times it was asked.</summary>
    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Bodies.Add(await (request.Content?.ReadAsStringAsync(ct) ?? Task.FromResult("")));

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static MailboxTokenProvider Provider(HttpMessageHandler handler) =>
        new(new StubFactory(handler), NullLogger<MailboxTokenProvider>.Instance);

    [Fact]
    public async Task A_token_is_minted_by_client_credentials_and_reused_until_it_nearly_expires()
    {
        // Client credentials rather than a password grant, because there is no user: the identity is
        // the client, and the mailbox it maps to comes from a claim its own mapper hardcodes.
        StubHandler handler = new("""{"access_token":"tok-1","expires_in":300}""");
        MailboxTokenProvider provider = Provider(handler);
        Guid mailboxId = Guid.NewGuid();

        string first = await provider.GetAsync(
            mailboxId, TokenEndpoint, "entkube-support-mailbox-abc", "s3cr3t", "openid email");

        first.Should().Be("tok-1");

        // Cached: a poll happens every couple of minutes and a token lives for five, so asking the
        // provider again inside its life must not cost another round trip.
        string second = await provider.GetAsync(
            mailboxId, TokenEndpoint, "entkube-support-mailbox-abc", "s3cr3t", "openid email");

        second.Should().Be("tok-1");
        handler.Calls.Should().Be(1);

        // The grant, the client and the scopes the directory requires — requested explicitly, because
        // a client-credentials token carries only what its client grants and the directory rejects one
        // missing what it asked for.
        handler.Bodies[0].Should().Contain("grant_type=client_credentials");
        handler.Bodies[0].Should().Contain("client_id=entkube-support-mailbox-abc");
        handler.Bodies[0].Should().Contain("scope=openid+email");
    }

    [Fact]
    public async Task A_rejected_credential_is_not_reused()
    {
        // A token can stop working before it expires — the client disabled, its secret rotated, the
        // mailbox renamed. Without dropping it the poller would present the same rejected token on
        // every poll until the cache aged out on its own, so a fixed Keycloak would not recover.
        StubHandler handler = new("""{"access_token":"tok-1","expires_in":300}""");
        MailboxTokenProvider provider = Provider(handler);
        Guid mailboxId = Guid.NewGuid();

        await provider.GetAsync(mailboxId, TokenEndpoint, "client", "secret", "openid");
        handler.Calls.Should().Be(1);

        MailboxTokenProvider.Forget(mailboxId);

        await provider.GetAsync(mailboxId, TokenEndpoint, "client", "secret", "openid");
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Two_mailboxes_do_not_share_a_token()
    {
        // One client per mailbox, so one token per mailbox: handing a second mailbox the first one's
        // token would authenticate it as the wrong account.
        StubHandler handler = new("""{"access_token":"tok","expires_in":300}""");
        MailboxTokenProvider provider = Provider(handler);

        await provider.GetAsync(Guid.NewGuid(), TokenEndpoint, "client-a", "a", "openid");
        await provider.GetAsync(Guid.NewGuid(), TokenEndpoint, "client-b", "b", "openid");

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task The_providers_own_error_is_what_the_operator_is_told()
    {
        // An unknown client, a wrong secret and a refused scope are three different fixes and read
        // identically without the body — which is exactly the situation that made the mail work take
        // as long as it did.
        StubHandler handler = new(
            """{"error":"invalid_scope","error_description":"Invalid scopes: openid email"}""",
            HttpStatusCode.BadRequest);

        Func<Task> act = () => Provider(handler).GetAsync(
            Guid.NewGuid(), TokenEndpoint, "entkube-support-mailbox-abc", "s3cr3t", "openid email");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
                .Contain("entkube-support-mailbox-abc")
                .And.Contain("invalid_scope")
                .And.Contain("Invalid scopes");
    }

    [Fact]
    public async Task A_success_without_a_token_is_still_a_failure()
    {
        // A 200 carrying no access_token would otherwise be cached as an empty credential and
        // presented on every poll, which the mail server refuses without saying why.
        StubHandler handler = new("""{"token_type":"Bearer"}""");

        Func<Task> act = () => Provider(handler).GetAsync(
            Guid.NewGuid(), TokenEndpoint, "client", "secret", "openid");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_response_with_no_lifetime_is_cached_briefly_rather_than_for_ever()
    {
        // Absent expires_in the shortest sensible life is assumed, not the longest: a token cached
        // past its life fails every poll until something evicts it, and nothing would.
        StubHandler handler = new("""{"access_token":"tok-1"}""");
        MailboxTokenProvider provider = Provider(handler);
        Guid mailboxId = Guid.NewGuid();

        await provider.GetAsync(mailboxId, TokenEndpoint, "client", "secret", "openid");

        // 60s assumed, and the reuse margin is also 60s — so it is already considered too close to
        // expiry to reuse, and the next call mints again rather than presenting a stale one.
        await provider.GetAsync(mailboxId, TokenEndpoint, "client", "secret", "openid");

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task No_scope_is_requested_when_the_directory_asks_for_none()
    {
        // An empty scope parameter is not the same as none: some providers reject it outright, and a
        // directory with no requireScopes has nothing for it to satisfy.
        StubHandler handler = new("""{"access_token":"tok","expires_in":300}""");

        await Provider(handler).GetAsync(
            Guid.NewGuid(), TokenEndpoint, "client", "secret", "   ");

        handler.Bodies[0].Should().NotContain("scope=");
    }
}
