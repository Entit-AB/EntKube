using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// That a support mailbox refuses to read mail over an unencrypted connection.
///
/// <para>This began as an end-to-end test of the Junk sweep against <see cref="ImapSink"/> and could
/// not be written, which turned out to be the more useful result. The mailbox never connects in
/// plaintext: a blank TLS choice means STARTTLS, not "none", so a server that does not offer the
/// extension is refused. Testing the sweep over a socket therefore needs a fake speaking TLS with a
/// certificate that chains to something trusted — waiving the name check, which the derived path does,
/// is not enough, because a self-signed certificate fails on the chain rather than the name.</para>
///
/// <para>So the boundary is recorded instead of worked around. Relaxing the transport to let a test
/// through would trade a real protection for coverage of one, on a mailbox carrying a customer's own
/// account of their own systems. What the sweep can be tested on without a socket is in
/// <see cref="JunkFolderLookupTests"/> — the folder lookup that used to fail silently — and its domain
/// rule is <c>SenderDomain.Claims</c>, which has its own tests.</para>
/// </summary>
public class UnencryptedMailboxTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private static readonly Guid MailboxId = Guid.NewGuid();

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportMailboxService mailboxes;
    private readonly VaultService vault;
    private readonly Guid tenantId = Guid.NewGuid();

    public UnencryptedMailboxTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        vault = new VaultService(factory, new VaultEncryptionService(TestRootKey));

        SupportMailService mail = new(
            factory,
            new RuleBasedMailAnalyst(TestTicketReference.Instance),
            new MailTriageRuleService(factory),
            new TicketService(factory, contracts, SilentTicketNotifier.For(factory)),
            new TimeService(factory, contracts),
            TestTicketReference.Instance,
            new SupportDutyService(factory));

        mailboxes = new SupportMailboxService(
            factory, vault, mail,
            new MailboxTokenProvider(new PlainClientFactory(), NullLogger<MailboxTokenProvider>.Instance),
            null!,
            NullLogger<SupportMailboxService>.Instance);
    }

    /// <summary>Never used: this mailbox is not on an OIDC server, so no token is minted.</summary>
    private sealed class PlainClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task A_server_that_cannot_encrypt_is_refused_rather_than_read_in_the_clear()
    {
        // The sink speaks IMAP and advertises no STARTTLS, which is exactly a server that cannot
        // encrypt. The mailbox must decline it: a support mailbox carries a customer's own account of
        // their own systems and a service account's credential, and reading that off an unencrypted
        // socket in order to make a poll succeed is the wrong trade.
        using ImapSink sink = new();
        sink.Folders.Add(new ImapFolder("Junk", "\\Junk"));

        db.SupportMailboxes.Add(new SupportMailbox
        {
            Id = MailboxId,
            TenantId = tenantId,
            Host = "127.0.0.1",
            Port = sink.Port,
            // No implicit TLS, which means STARTTLS rather than nothing — the distinction this asserts.
            UseSsl = false,
            Username = "support@entit.example",
            Folder = "INBOX",
            IsEnabled = true,
            Disposition = MailboxDisposition.LeaveAlone,
        });
        await db.SaveChangesAsync();

        // After the row: the vault secret has a foreign key to the mailbox it belongs to.
        await vault.SetSupportMailboxPasswordAsync(tenantId, MailboxId, "irrelevant");

        MailPollResult result = await mailboxes.PollAsync(tenantId);

        result.Ok.Should().BeFalse();
        result.Taken.Should().Be(0);

        // And names which of the several things went wrong, rather than reporting an empty mailbox.
        result.Error.Should().Contain("STARTTLS");
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
