using System.Net;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Which secret a mailbox authenticates to its OIDC token endpoint with.
///
/// <para><b>The defect.</b> A support mailbox holds two different vault secrets —
/// <c>PASSWORD</c>, the IMAP/SMTP credential, and <c>OAUTH_CLIENT_SECRET</c>, the service
/// account's. The poller read the second. The <em>submission</em> path in <c>SmtpSettings</c>
/// read the first and sent it to the token endpoint as a client secret.</para>
///
/// <para>⚠️ <b>Why it was invisible.</b> An OIDC directory accepts only bearer tokens, so there
/// was no password fallback to mask the failure, and the two halves fail independently: fetching
/// mail worked perfectly while nothing could be sent. Every screen looked healthy — which is the
/// same shape as the defect <see cref="SmtpSettingsResolverTests"/> was written for, where
/// sending was derived from a row nothing said was needed.</para>
///
/// <para>⚠️ It also put the mailbox's IMAP password in a token request, where a rejected
/// <c>client_secret</c> is exactly the kind of thing an identity provider logs.</para>
///
/// <para>Asserted on the REQUEST that leaves, not on which method was called: the wire is what
/// the identity provider judges, and a test that checked the accessor would pass on a second
/// accessor that read the same wrong entry.</para>
///
/// <para>⚠️ <b>What these do NOT cover, stated rather than implied.</b> They pin
/// <see cref="MailboxTokenProvider.ForMailboxAsync"/> — the credential decision. They do not pin
/// that <c>SmtpSettings.TokenAsync</c> still CALLS it: reintroducing the inline password read
/// there passes everything here. Measured, not assumed — that mutation was run and escaped.</para>
///
/// <para>Reaching it needs a Stalwart component, its config, an account and a domain seeded so
/// that <c>MailboxConnectionResolver.ResolveSubmission</c> returns a connection with
/// <c>UseOAuth</c> set, which is a fixture several times the size of the fix. The decision is
/// tested and the one line of wiring is not, which is the trade being made on purpose rather
/// than a gap nobody noticed.</para>
/// </summary>
public class MailboxTokenCredentialTests : IDisposable
{
    private const string ImapPassword = "theimappassword";
    private const string OAuthClientSecret = "theoauthclientsecret";

    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;

    private readonly Guid tenantId = Guid.NewGuid();

    /// <summary>
    /// ⚠️ Fresh per test instance. <c>MailboxTokenProvider</c> caches tokens in a STATIC
    /// dictionary keyed by mailbox id, so a shared id would let one test answer another's token
    /// request from the cache and never reach the handler at all.
    /// </summary>
    private readonly Guid mailboxId = Guid.NewGuid();

    private readonly List<string> tokenRequests = [];

    public MailboxTokenCredentialTests()
    {
        testDb = new InterceptingTestDb(RootKey);
        db = testDb.CreateContext();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.SaveChanges();

        db.Set<SupportMailbox>().Add(new SupportMailbox
        {
            Id = mailboxId,
            TenantId = tenantId,
            Address = "support@example.com",
            Host = "mail.example.com",
            Username = "support@example.com",
            OAuthClientId = "mailbox-service-account",
            OAuthTokenEndpoint = "https://id.example.com/token",
        });
        db.SaveChanges();

        vault = testDb.CreateVaultService();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"a-bearer-token","expires_in":3600}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class OneHandler(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private MailboxTokenProvider Provider() =>
        new(new OneHandler(new RecordingHandler(tokenRequests)),
            NullLogger<MailboxTokenProvider>.Instance);

    private SupportMailbox Mailbox() => db.Set<SupportMailbox>().First(m => m.Id == mailboxId);

    /// <summary>
    /// ⚠️ The headline. Both secrets are stored, and they are different, so the request can only
    /// carry one of them — and a test with one stored would pass on either accessor.
    /// </summary>
    [Fact]
    public async Task The_token_request_carries_the_oauth_secret_and_not_the_mailbox_password()
    {
        await vault.SetSupportMailboxPasswordAsync(tenantId, mailboxId, ImapPassword);
        await vault.SetSupportMailboxOAuthSecretAsync(tenantId, mailboxId, OAuthClientSecret);

        string? token = await Provider().ForMailboxAsync(vault, Mailbox(), "openid email", default);

        token.Should().Be("a-bearer-token");

        string request = tokenRequests.Should().ContainSingle().Subject;

        request.Should().Contain(OAuthClientSecret, "the service account's secret is what the "
            + "token endpoint authenticates the client with");
        request.Should().NotContain(ImapPassword, "the mailbox's IMAP password is a different "
            + "credential, and sending it here both fails and discloses it");
    }

    /// <summary>
    /// With no OAuth secret stored there is nothing to authenticate as, and the answer is no
    /// token rather than a token request made with the password instead.
    /// </summary>
    [Fact]
    public async Task A_mailbox_with_only_a_password_gets_no_token()
    {
        await vault.SetSupportMailboxPasswordAsync(tenantId, mailboxId, ImapPassword);

        string? token = await Provider().ForMailboxAsync(vault, Mailbox(), "openid email", default);

        token.Should().BeNull("no service account secret is stored");
        tokenRequests.Should().BeEmpty("and nothing was sent to the identity provider");
    }

    /// <summary>
    /// The client id is the service account's, and travels with the secret. Pinned because the
    /// two are read from different places — the id off the mailbox row, the secret out of the
    /// vault — and a token request with one of them belonging to something else is refused with
    /// the same opaque <c>invalid_client</c> as the wrong secret.
    /// </summary>
    [Fact]
    public async Task The_token_request_names_the_service_account()
    {
        await vault.SetSupportMailboxOAuthSecretAsync(tenantId, mailboxId, OAuthClientSecret);

        await Provider().ForMailboxAsync(vault, Mailbox(), "openid email", default);

        tokenRequests.Should().ContainSingle().Subject
            .Should().Contain("mailbox-service-account");
    }
}
