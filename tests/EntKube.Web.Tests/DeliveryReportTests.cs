using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using MimeKit;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading a bounce.
///
/// <para>This exists because a receipt refused by Gmail came back to the support address,
/// was not recognised as a bounce, was placed with the customer by the address it had been
/// delivered to, and opened a ticket — in a daemon's name, about our own failure to deliver.
/// Whether a message was machine-generated had been decided from headers a particular server
/// may simply not send. A delivery report says what it is in its content type, and that is
/// what is read now.</para>
/// </summary>
public class DeliveryReportTests
{
    /// <summary>A bounce shaped the way RFC 3462 says, with the headers returned.</summary>
    private static MimeMessage Bounce(
        string failedMessageId,
        string recipient = "marie.stahle@gmail.com",
        string reportType = "delivery-status",
        bool wholeMessage = false)
    {
        MimeMessage original = new();
        original.From.Add(new MailboxAddress("Support", "support@entit.se"));
        original.To.Add(new MailboxAddress("", recipient));
        original.Subject = "[EK-412-K7QX9] Journalen svarar inte";
        original.MessageId = failedMessageId;
        original.Body = new TextPart("plain") { Text = "We have received your report." };

        MultipartReport report = new(reportType);

        report.Add(new TextPart("plain")
        {
            Text = $"<{recipient}> (host 'gmail-smtp-in.l.google.com' rejected command "
                 + "'BDAT 2084 LAST' with code 550 (5.7.25) 'The IP address sending this "
                 + "message does not have a PTR record setup')",
        });

        MessageDeliveryStatus status = new();
        status.StatusGroups.Add(new HeaderList { { "Reporting-MTA", "dns; mail.entit.se" } });
        status.StatusGroups.Add(new HeaderList
        {
            { "Final-Recipient", $"rfc822;{recipient}" },
            { "Action", "failed" },
            { "Status", "5.7.25" },
        });
        report.Add(status);

        if (wholeMessage)
        {
            report.Add(new MessagePart { Message = original });
        }
        else
        {
            // What a server configured not to return the body sends: the headers alone.
            report.Add(new TextPart("rfc822-headers")
            {
                Text = $"From: Support <support@entit.se>\r\nTo: {recipient}\r\n"
                     + $"Subject: {original.Subject}\r\nMessage-Id: <{failedMessageId}>\r\n",
            });
        }

        MimeMessage bounce = new();
        bounce.From.Add(new MailboxAddress("Mail Delivery Subsystem", "MAILER-DAEMON@mail.entit.se"));
        bounce.To.Add(new MailboxAddress("", "support@entit.se"));
        bounce.Subject = "Delivery Status Notification (Failure)";
        bounce.Body = report;

        return bounce;
    }

    /// <summary>
    /// The content type is the answer. Not a guess from the From address, which is how this
    /// was being decided and is why one server's bounce got through.
    /// </summary>
    [Fact]
    public void A_delivery_report_says_what_it_is()
    {
        DeliveryReport.Is(Bounce("ticket-412.abc@entkube")).Should().BeTrue();
    }

    [Fact]
    public void An_ordinary_message_is_not_one()
    {
        MimeMessage ordinary = new();
        ordinary.From.Add(new MailboxAddress("Karin", "karin@kund.example"));
        ordinary.Body = new TextPart("plain") { Text = "Journalen svarar inte." };

        DeliveryReport.Is(ordinary).Should().BeFalse();
    }

    /// <summary>
    /// A read receipt and a spam complaint are also multipart/report, and also
    /// machine-generated — but neither is a failure to deliver, and writing one onto a
    /// ticket as one would say the customer never got something they did get.
    /// </summary>
    [Theory]
    [InlineData("disposition-notification")]
    [InlineData("feedback-report")]
    public void Another_kind_of_report_is_not_a_delivery_failure(string reportType)
    {
        DeliveryReport.Is(Bounce("ticket-412.abc@entkube", reportType: reportType))
            .Should().BeFalse();
    }

    /// <summary>
    /// <b>Which of our messages failed.</b> Both shapes the format allows: the whole
    /// returned message, and the headers alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_failed_message_is_named(bool wholeMessage)
    {
        string id = SupportMessageId.For(412);

        DeliveryReport.FailedMessageId(Bounce(id, wholeMessage: wholeMessage))
            .Should().Be(id);
    }

    /// <summary>
    /// And that is what threads it onto its ticket. Most servers do not put the failed id
    /// in In-Reply-To, so without reading the report there is nothing to thread on.
    /// </summary>
    [Fact]
    public void A_bounce_threads_onto_the_ticket_whose_mail_failed()
    {
        MimeMessage bounce = Bounce(SupportMessageId.For(412));

        bounce.InReplyTo.Should().BeNull("a server does not normally set it on a report");

        SupportMessageId.TicketNumberIn(MailMessageReader.ThreadParentOf(bounce))
            .Should().Be(412);
    }

    [Fact]
    public void Who_it_failed_for_comes_from_the_machine_readable_part()
    {
        DeliveryReport.FailedRecipient(Bounce(SupportMessageId.For(412)))
            .Should().Be("marie.stahle@gmail.com");
    }

    /// <summary>
    /// Read into the row we keep: a bounce is machine-generated — which it already was, by
    /// its MAILER-DAEMON sender — and is now also marked as the particular kind of
    /// machine-generated that means our own mail came back.
    /// </summary>
    [Fact]
    public void The_row_records_both_things_about_it()
    {
        InboundMailMessage read = MailMessageReader.Read(
            Bounce(SupportMessageId.For(412)), Guid.NewGuid(), DateTime.UtcNow);

        read.IsMachineGenerated.Should().BeTrue();
        read.IsDeliveryReport.Should().BeTrue();
        read.FailedRecipient.Should().Be("marie.stahle@gmail.com");
        read.InReplyTo.Should().StartWith("ticket-412.");

        // The diagnostic the operator needs is in the body, where the report put it.
        read.Body.Should().Contain("PTR record");
    }

    /// <summary>
    /// A report we cannot find the original in is still a report. Recognising it is what
    /// keeps it out of the ticket queue; the thread is a bonus.
    /// </summary>
    [Fact]
    public void A_report_with_no_original_is_still_a_report()
    {
        MultipartReport bare = new("delivery-status");
        bare.Add(new TextPart("plain") { Text = "Mailbox full." });

        MimeMessage bounce = new();
        bounce.From.Add(new MailboxAddress("", "MAILER-DAEMON@mail.entit.se"));
        bounce.Body = bare;

        DeliveryReport.Is(bounce).Should().BeTrue();
        DeliveryReport.FailedMessageId(bounce).Should().BeNull();
    }
}
