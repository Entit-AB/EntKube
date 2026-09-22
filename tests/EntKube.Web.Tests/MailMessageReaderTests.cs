using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using MimeKit;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading a fetched message into the row we keep.
///
/// <para>None of this needs a mail server, and all of it is where a support mailbox goes
/// wrong: a message with no Message-Id taken in again every two minutes, a reply that
/// opens a second ticket about the fault already being worked, an HTML-only body that
/// arrives as a wall of markup nothing can match a phrase against — and a Date header
/// that starts an SLA clock before the message existed.</para>
/// </summary>
public class MailMessageReaderTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    /// <summary>Tuesday 22 September 2026, 09:00 UTC.</summary>
    private static readonly DateTime Fetched = new(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc);

    private static MimeMessage Message(
        string from = "anna@capio.se",
        string? fromName = "Anna Lindqvist",
        string? subject = "Journalen svarar inte",
        string? text = "Vi kommer inte in i journalsystemet.",
        string? html = null,
        string? messageId = "<abc123@capio.se>",
        string? inReplyTo = null,
        IEnumerable<string>? references = null,
        DateTimeOffset? date = null)
    {
        MimeMessage message = new();
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(new MailboxAddress("Support", "support@entit.se"));

        if (subject is null)
        {
            // MimeKit refuses a null subject, so the header is removed instead — which is
            // what a message with no Subject: line actually looks like.
            message.Headers.Remove(HeaderId.Subject);
        }
        else
        {
            message.Subject = subject;
        }

        if (messageId is null)
        {
            // MimeKit invents one on construction; a message from an appliance that omits
            // the header has none at all.
            message.Headers.Remove(HeaderId.MessageId);
        }
        else
        {
            message.MessageId = messageId;
        }

        if (inReplyTo is not null)
        {
            message.InReplyTo = inReplyTo;
        }

        foreach (string reference in references ?? [])
        {
            message.References.Add(reference);
        }

        if (date is DateTimeOffset when)
        {
            message.Date = when;
        }

        BodyBuilder body = new();

        if (text is not null)
        {
            body.TextBody = text;
        }

        if (html is not null)
        {
            body.HtmlBody = html;
        }

        message.Body = body.ToMessageBody();

        return message;
    }

    // ---- The plain reading --------------------------------------------------------------

    [Fact]
    public void A_message_is_read_into_the_row_we_keep()
    {
        InboundMailMessage row = MailMessageReader.Read(
            Message(date: new DateTimeOffset(Fetched.AddMinutes(-4))), Tenant, Fetched);

        row.TenantId.Should().Be(Tenant);
        row.MessageId.Should().Be("abc123@capio.se");
        row.FromAddress.Should().Be("anna@capio.se");
        row.FromName.Should().Be("Anna Lindqvist");
        row.Subject.Should().Be("Journalen svarar inte");
        row.Body.Should().Be("Vi kommer inte in i journalsystemet.");
        row.SentAt.Should().Be(Fetched.AddMinutes(-4));
        row.ReceivedAt.Should().Be(Fetched);
        row.State.Should().Be(MailTriageState.Received);
    }

    /// <summary>A subject can be absent; the column cannot.</summary>
    [Fact]
    public void A_message_with_no_subject_gets_an_empty_one() =>
        MailMessageReader.Read(Message(subject: null), Tenant, Fetched)
            .Subject.Should().Be("");

    /// <summary>A display name is optional, and a blank one is not a name.</summary>
    [Fact]
    public void A_sender_with_no_display_name_has_none_recorded() =>
        MailMessageReader.Read(Message(fromName: ""), Tenant, Fetched)
            .FromName.Should().BeNull();

    // ---- Identity -------------------------------------------------------------------------

    /// <summary>
    /// The synthesised id has to be the same every time the same message is read, or the
    /// check that stops one mail becoming two tickets would never fire and the message
    /// would be taken in again on every poll for as long as it sat in the mailbox.
    /// </summary>
    [Fact]
    public void A_message_with_no_id_gets_the_same_synthesised_one_every_time()
    {
        DateTimeOffset sent = new(2026, 9, 22, 8, 55, 0, TimeSpan.Zero);

        string first = MailMessageReader.IdentityOf(Message(messageId: null, date: sent));
        string second = MailMessageReader.IdentityOf(Message(messageId: null, date: sent));

        first.Should().Be(second);
        first.Should().StartWith("synthesised-");
    }

    [Fact]
    public void Two_different_messages_with_no_id_are_told_apart()
    {
        DateTimeOffset sent = new(2026, 9, 22, 8, 55, 0, TimeSpan.Zero);

        MailMessageReader.IdentityOf(Message(messageId: null, subject: "One", date: sent))
            .Should().NotBe(
                MailMessageReader.IdentityOf(Message(messageId: null, subject: "Two", date: sent)));
    }

    // ---- Threading --------------------------------------------------------------------------

    [Fact]
    public void A_reply_names_what_it_replies_to() =>
        MailMessageReader.Read(Message(inReplyTo: "<first@capio.se>"), Tenant, Fetched)
            .InReplyTo.Should().Be("first@capio.se");

    /// <summary>
    /// Some clients send only References. Without reading its last entry, a reply from
    /// one of them opens a second ticket about the fault already being worked.
    /// </summary>
    [Fact]
    public void A_reply_with_only_References_still_threads() =>
        MailMessageReader.Read(
            Message(references: ["<first@capio.se>", "<second@capio.se>"]), Tenant, Fetched)
            .InReplyTo.Should().Be("second@capio.se");

    [Fact]
    public void A_message_that_starts_a_thread_replies_to_nothing() =>
        MailMessageReader.Read(Message(), Tenant, Fetched).InReplyTo.Should().BeNull();

    // ---- When it was sent ---------------------------------------------------------------------

    /// <summary>
    /// Mail sits in queues, and the customer is entitled to have the response clock run
    /// from when they sent it.
    /// </summary>
    [Fact]
    public void A_message_delayed_in_transit_counts_from_when_it_was_sent()
    {
        DateTime sent = Fetched.AddHours(-3);

        MailMessageReader.Read(Message(date: new DateTimeOffset(sent)), Tenant, Fetched)
            .SentAt.Should().Be(sent);
    }

    /// <summary>
    /// The Date header is written by the sender and can be wrong or forged. A date in the
    /// future would start a response clock before the message existed — far enough out, it
    /// would make an answer look given before the question was asked. We certainly had it
    /// by the time we fetched it, so that is what is used.
    /// </summary>
    [Fact]
    public void A_date_in_the_future_is_not_believed()
    {
        MailMessageReader.Read(
            Message(date: new DateTimeOffset(Fetched.AddDays(30))), Tenant, Fetched)
            .SentAt.Should().Be(Fetched);
    }

    /// <summary>
    /// Clocks disagree by a little all the time, and treating that as forgery would move
    /// every such message's clock later — against the customer.
    /// </summary>
    [Fact]
    public void A_slightly_fast_clock_is_believed()
    {
        DateTime sent = Fetched.AddMinutes(5);

        MailMessageReader.Read(Message(date: new DateTimeOffset(sent)), Tenant, Fetched)
            .SentAt.Should().Be(sent);
    }

    // ---- The body ------------------------------------------------------------------------------

    [Fact]
    public void A_plain_text_body_is_taken_as_it_is() =>
        MailMessageReader.Read(Message(text: "Line one\r\nLine two"), Tenant, Fetched)
            .Body.Should().Be("Line one\nLine two");

    /// <summary>
    /// Most mail from a corporate client is HTML only. The analyst matches phrases against
    /// this text and an operator reads it in the queue, and neither is served by markup.
    /// </summary>
    [Fact]
    public void An_HTML_only_body_is_reduced_to_its_words()
    {
        InboundMailMessage row = MailMessageReader.Read(
            Message(
                text: null,
                html: "<html><body><p>Journalen svarar <b>inte</b>.</p><p>Akut.</p></body></html>"),
            Tenant,
            Fetched);

        row.Body.Should().Be("Journalen svarar inte.\n\nAkut.");
    }

    /// <summary>
    /// A marketing mail that arrived as a page of JavaScript would fill the queue entry
    /// and give the phrase matcher a great deal of nothing to read.
    /// </summary>
    [Fact]
    public void Script_and_style_are_dropped_with_their_contents()
    {
        MailMessageReader.PlainTextFrom(
            "<style>.x{color:red}</style><p>Hello</p><script>alert('hi')</script>")
            .Should().Be("Hello");
    }

    [Fact]
    public void The_entities_that_turn_up_in_prose_are_unwrapped() =>
        MailMessageReader.PlainTextFrom("<p>Drift &amp; underh&#229;ll &lt;akut&gt;</p>")
            .Should().Be("Drift & underh&#229;ll <akut>");

    /// <summary>
    /// Nested markup leaves long runs of blank lines behind — six here. They collapse to
    /// the one that separates two paragraphs, because a queue entry that is mostly
    /// whitespace hides the line that says what broke.
    /// </summary>
    [Fact]
    public void Runs_of_blank_lines_left_by_the_markup_collapse_to_one() =>
        MailMessageReader.PlainTextFrom("<div><div><div><p>One</p></div></div></div><p>Two</p>")
            .Should().Be("One\n\nTwo");

    /// <summary>Unterminated markup should stop the reader, not run it off the end.</summary>
    [Fact]
    public void An_unterminated_tag_ends_the_reading() =>
        MailMessageReader.PlainTextFrom("<p>Before</p><div class=\"unclosed")
            .Should().Be("Before");

    [Fact]
    public void A_message_with_no_body_at_all_reads_as_empty() =>
        MailMessageReader.Read(Message(text: null), Tenant, Fetched).Body.Should().Be("");
}
