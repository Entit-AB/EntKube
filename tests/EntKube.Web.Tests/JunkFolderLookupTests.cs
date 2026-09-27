using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether the Junk folder can be found at all.
///
/// <para>The support mailbox sweeps Junk so a customer's request cannot be lost to the spam filter,
/// and it finds that folder by its <c>SPECIAL-USE</c> attribute rather than by name — the folder is
/// "Junk" on one server and "Junk Mail" on the next, and guessing wrong would silently do nothing.
/// Which makes the attribute a dependency worth a test: if a server does not advertise it, the sweep
/// protects nothing and says nothing, and a support email disappears with no trace anywhere.</para>
///
/// <para>This is the one part of the sweep that was left uncovered when it shipped, on the grounds
/// that an IMAP fake was not worth building. It was.</para>
/// </summary>
public class JunkFolderLookupTests
{
    private static async Task<ImapClient> ConnectAsync(ImapSink sink)
    {
        ImapClient client = new();
        await client.ConnectAsync("127.0.0.1", sink.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("support@example.com", "irrelevant");
        return client;
    }

    [Fact]
    public async Task A_server_advertising_the_attribute_yields_its_junk_folder_whatever_it_is_called()
    {
        // "Junk Mail", not "Junk" — deliberately the name a lookup by string would miss.
        using ImapSink sink = new();
        sink.Folders.Add(new ImapFolder("Junk Mail", "\\Junk"));

        using ImapClient client = await ConnectAsync(sink);

        IMailFolder junk = client.GetFolder(SpecialFolder.Junk);

        junk.Should().NotBeNull();
        junk.Name.Should().Be("Junk Mail");
    }

    [Fact]
    public async Task A_server_that_does_not_advertise_it_yields_null_rather_than_an_error()
    {
        // The failure this documents. There is no Junk folder to be found, the sweep returns zero, and
        // the only trace is a debug line — so a customer's mail filed as spam is simply never read.
        // Worth pinning, because the behaviour is silent by design and the alternative to knowing it
        // is discovering it from a customer asking why nobody replied.
        using ImapSink sink = new();
        sink.Folders.Add(new ImapFolder("Junk", null));

        using ImapClient client = await ConnectAsync(sink);

        // Null, not an exception — measured, not assumed. And null even though a folder plainly named
        // "Junk" is right there, because a name is not an attribute. The code assumed an exception and
        // logged on that path; the path that actually happens returned silently, which is how a
        // protection an operator believes in could be absent with nothing anywhere saying so.
        client.GetFolder(SpecialFolder.Junk).Should().BeNull();
    }

    [Fact]
    public async Task The_messages_in_junk_can_be_read_back_off_the_wire()
    {
        // That the sweep can actually fetch what it found, rather than only locate the folder.
        using ImapSink sink = new();
        ImapFolder junk = new("Junk", "\\Junk");
        junk.Messages.Add(
            "From: customer@customer.example\r\n"
            + "To: support@example.com\r\n"
            + "Subject: printer is down\r\n"
            + "Message-Id: <one@customer.example>\r\n"
            + "\r\n"
            + "It stopped this morning.\r\n");
        sink.Folders.Add(junk);

        using ImapClient client = await ConnectAsync(sink);

        IMailFolder folder = client.GetFolder(SpecialFolder.Junk);
        await folder.OpenAsync(FolderAccess.ReadOnly);

        IList<UniqueId> ids = await folder.SearchAsync(
            MailKit.Search.SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-14)));

        ids.Should().HaveCount(1);

        MimeKit.MimeMessage message = await folder.GetMessageAsync(ids[0]);

        message.From.Mailboxes.First().Address.Should().Be("customer@customer.example");
        message.MessageId.Should().Be("one@customer.example");
    }
}
