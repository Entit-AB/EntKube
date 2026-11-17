using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 2c-2 (docs/helpdesk.md, decision 3): mail written on a ticket in rich text — sanitised
/// before it is stored or sent, sent as HTML with a plain part made from the same content, to
/// the right people, with the agreement's clock treated as a reply should treat it. Asserted on
/// the message that reached a real SMTP listener, not on an intention.
/// </summary>
public class TicketMailTests : IDisposable
{
    private readonly SmtpSink sink = new();
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;

    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();

    public TicketMailTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customer, TenantId = tenant, Name = "Vårdbolaget" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        IConfiguration smtp = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = sink.Host,
            ["Smtp:Port"] = sink.Port.ToString(),
            ["Smtp:FromAddress"] = "support@entit.se",
        }).Build();

        tickets = new TicketService(factory, new ContractService(factory), new TicketNotifier(
            factory, new SmtpSettingsResolver(factory, smtp), new OnCallService(factory),
            TestTicketReference.Instance, NullLogger<TicketNotifier>.Instance));
    }

    public void Dispose()
    {
        sink.Dispose();
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(string? reporter = "karin@kund.example") => tickets.CreateAsync(
        tenant, customer, null, "Login fails", "", TicketChannel.Email, TicketPriority.P3,
        DateTime.UtcNow.AddMinutes(-30), "Karin", reporter);

    private MimeMessage LastMail() => sink.Received.Last().Message;

    // ───────────────────────────── Sanitising ─────────────────────────────

    [Theory]
    [InlineData("<p>Hi<script>alert(1)</script></p>", "<p>Hi</p>")]
    [InlineData("<p onclick=\"x()\" style=\"color:red\" class=\"btn\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\"><p>x</p>", "<p>x</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a target=\"_blank\" rel=\"noopener noreferrer nofollow\">x</a>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe><p>ok</p>", "<p>ok</p>")]
    public void Only_the_formatting_the_composer_makes_survives(string html, string safe) =>
        RichText.Sanitize(html).Should().Be(safe);

    [Fact]
    public void A_link_keeps_its_address_and_opens_away_from_us() =>
        RichText.Sanitize("<a href=\"https://status.entit.example/x\">status</a>")
            .Should().Be("<a href=\"https://status.entit.example/x\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">status</a>");

    // ───────────────────────────── Sending ─────────────────────────────

    [Fact]
    public async Task A_reply_goes_as_html_with_a_plain_part_made_from_the_same_safe_content()
    {
        Ticket t = await Raise();

        await tickets.SendMailAsync(tenant, t.Id, TicketService.MailKind.Reply,
            "<p>We are <strong>on it</strong>.</p><script>steal()</script>", [], [], "nils", DateTime.UtcNow);

        MimeMessage mail = LastMail();
        mail.To.Mailboxes.Select(m => m.Address).Should().Equal("karin@kund.example");
        mail.HtmlBody.Should().Contain("<strong>on it</strong>").And.NotContain("script").And.NotContain("steal");
        mail.TextBody.Should().Contain("We are on it.").And.NotContain("steal");
        mail.Subject.Should().Contain(TestTicketReference.Instance.For(t.Number));
        mail.Headers.Contains("Auto-Submitted").Should().BeFalse("a person wrote this");
    }

    [Fact]
    public async Task A_reply_is_the_first_response_and_is_on_the_history_as_the_customer_sees_it()
    {
        Ticket t = await Raise();
        DateTime at = DateTime.UtcNow;

        await tickets.SendMailAsync(tenant, t.Id, TicketService.MailKind.Reply, "<p>Hello</p>", [], [], "nils", at);

        db.ChangeTracker.Clear();
        Ticket after = db.Tickets.Include(x => x.Events).Single(x => x.Id == t.Id);
        after.FirstResponseAt.Should().Be(at);
        TicketEvent reply = after.Events.Single(e => e.DetailHtml != null);
        reply.CustomerVisible.Should().BeTrue();
        reply.Detail.Should().StartWith("Replied to karin@kund.example");
        reply.DetailHtml.Should().Be("<p>Hello</p>");
    }

    [Fact]
    public async Task A_forward_is_internal_and_is_not_a_response()
    {
        Ticket t = await Raise();

        await tickets.SendMailAsync(tenant, t.Id, TicketService.MailKind.Forward, "<p>Can you check?</p>",
            ["vendor@supplier.example"], [], "nils", DateTime.UtcNow);

        LastMail().To.Mailboxes.Select(m => m.Address).Should().Equal("vendor@supplier.example");
        LastMail().Subject.Should().StartWith("Fwd: ");

        db.ChangeTracker.Clear();
        Ticket after = db.Tickets.Include(x => x.Events).Single(x => x.Id == t.Id);
        after.FirstResponseAt.Should().BeNull("telling a supplier is not answering the customer");
        after.Events.Single(e => e.DetailHtml != null).CustomerVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Reply_all_copies_the_others_on_the_customers_last_mail_but_not_our_own_address()
    {
        Ticket t = await Raise();
        db.SupportMailboxes.Add(new SupportMailbox { Id = Guid.NewGuid(), TenantId = tenant, Address = "support@entit.se", Host = "imap.entit.example", Username = "support" });
        db.InboundMailMessages.Add(new InboundMailMessage
        {
            Id = Guid.NewGuid(), TenantId = tenant, MessageId = "m1", FromAddress = "karin@kund.example",
            Subject = "Re", TicketId = t.Id, ToAddresses = "support@entit.se, lars@kund.example",
        });
        await db.SaveChangesAsync();

        List<string> candidates = await tickets.ReplyAllCandidatesAsync(tenant, t.Id);
        candidates.Should().Equal("lars@kund.example");

        await tickets.SendMailAsync(tenant, t.Id, TicketService.MailKind.ReplyAll, "<p>Both of you</p>", candidates, [], "nils", DateTime.UtcNow);

        // The sink also holds the arrival receipt; the reply is the one carrying this text.
        sink.Received.Where(r => r.Message.HtmlBody?.Contains("Both of you") == true)
            .Select(r => r.Message.MessageId).Distinct().Should().HaveCount(1, "one message, with everyone visible on it");
        LastMail().Cc.Mailboxes.Select(m => m.Address).Should().Equal("lars@kund.example");
    }

    [Fact]
    public async Task An_attachment_goes_with_the_mail()
    {
        Ticket t = await Raise();

        await tickets.SendMailAsync(tenant, t.Id, TicketService.MailKind.Reply, "<p>See attached.</p>", [],
            [new OutboundAttachment("guide.pdf", "application/pdf", [1, 2, 3])], "nils", DateTime.UtcNow);

        LastMail().Attachments.OfType<MimePart>().Select(p => p.FileName).Should().Equal("guide.pdf");
    }

    [Fact]
    public async Task Nothing_to_say_or_nobody_to_say_it_to_is_refused_before_anything_is_sent()
    {
        Ticket noReporter = await Raise(reporter: null);

        Func<Task> empty = () => tickets.SendMailAsync(tenant, noReporter.Id, TicketService.MailKind.Reply, "<p> </p><script>x</script>", [], [], "nils", DateTime.UtcNow);
        await empty.Should().ThrowAsync<ArgumentException>().WithMessage("*nothing to send*");

        Func<Task> nobody = () => tickets.SendMailAsync(tenant, noReporter.Id, TicketService.MailKind.Reply, "<p>Hi</p>", [], [], "nils", DateTime.UtcNow);
        await nobody.Should().ThrowAsync<ArgumentException>().WithMessage("*no reporter address*");

        Func<Task> badAddress = () => tickets.SendMailAsync(tenant, noReporter.Id, TicketService.MailKind.Forward, "<p>Hi</p>", ["not an address"], [], "nils", DateTime.UtcNow);
        await badAddress.Should().ThrowAsync<ArgumentException>();

        sink.Received.Where(r => r.Message.Subject.Contains("Login fails") && r.Message.HtmlBody != null).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reply_the_relay_refuses_is_recorded_as_not_sent_and_the_clock_keeps_running()
    {
        Ticket t = await Raise();
        sink.Reject.Add("karin@kund.example");

        TicketService.MailOutcome? outcome = await tickets.SendMailAsync(tenant, t.Id, TicketService.MailKind.Reply, "<p>Hello</p>", [], [], "nils", DateTime.UtcNow);

        outcome!.Value.Reached.Should().BeEmpty();

        db.ChangeTracker.Clear();
        Ticket after = db.Tickets.Include(x => x.Events).Single(x => x.Id == t.Id);
        after.FirstResponseAt.Should().BeNull();
        after.Events.Single(e => e.DetailHtml != null).Should().Match<TicketEvent>(e =>
            !e.CustomerVisible && e.Detail.StartsWith("A mail could not be sent"));
    }
}
