using MimeKit;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Reading a bounce: whether a message is one, and which of our own messages failed.
///
/// <para><b>Why this is read from the structure rather than guessed at.</b> Whether a
/// message was machine-generated was decided from <c>Auto-Submitted</c>, a list of bulk
/// <c>Precedence</c> values, a null <c>Return-Path</c> and a set of local parts nobody
/// reads — all good signals, and all ones a particular server may simply not send. A
/// delivery report says what it is in its own content type: RFC 3462 defines it as
/// <c>multipart/report</c> with <c>report-type=delivery-status</c>. That is not a heuristic
/// with a success rate, it is the thing's self-description, and it is what should have been
/// read first.</para>
///
/// <para><b>Why finding the failed message matters more than suppressing the bounce.</b> A
/// bounce that is merely recognised and dropped is worse than one that opens a ticket: when
/// the receipt for ticket #412 is refused, the customer never learned their number, and
/// nobody at our end learned it either. The report carries the original message's headers —
/// RFC 3462 puts them in a <c>message/rfc822</c> or <c>text/rfc822-headers</c> part — and
/// the <c>Message-Id</c> among them is one of ours, naming its ticket
/// (<see cref="SupportMessageId"/>). So the failure can be put on the ticket it happened to,
/// which is the only place anybody would look.</para>
/// </summary>
public static class DeliveryReport
{
    /// <summary>
    /// Whether this message says it is a delivery status notification.
    ///
    /// <para>Read from the content type, which the sending server sets because the format
    /// requires it. A server that sends a bounce as plain text is still caught by the older
    /// signals in <see cref="MailMessageReader.LooksAutomated"/>; this one is simply the
    /// answer that cannot be wrong when it is available.</para>
    /// </summary>
    public static bool Is(MimeMessage message)
    {
        ContentType? type = message.Body?.ContentType;

        if (type is null || !type.IsMimeType("multipart", "report"))
        {
            return false;
        }

        string? reportType = type.Parameters["report-type"];

        // RFC 3462 names delivery-status; the other report types in the wild are
        // disposition-notification (a read receipt) and feedback-report (a spam complaint).
        // Those are machine-generated too, but they are not a failure to deliver and must
        // not be written onto a ticket as one.
        return string.Equals(reportType, "delivery-status", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>Message-Id</c> of the message that failed, from the original headers the
    /// report carries, or null when the report does not include them.
    ///
    /// <para>Both shapes RFC 3462 allows are read: the whole returned message, and the
    /// headers alone — which is what a server configured not to return the body sends, and
    /// is the more common of the two for a large message.</para>
    /// </summary>
    public static string? FailedMessageId(MimeMessage message)
    {
        if (!Is(message))
        {
            return null;
        }

        foreach (MimeEntity part in message.BodyParts)
        {
            string? id = part switch
            {
                // message/rfc822 — the returned message, parsed.
                MessagePart returned => returned.Message?.MessageId,

                // text/rfc822-headers — the headers alone, as a body part.
                TextPart headers when part.ContentType.IsMimeType("text", "rfc822-headers") =>
                    MessageIdIn(headers.Text),

                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(id))
            {
                return id.Trim().Trim('<', '>');
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>Message-Id</c> out of a block of header text.
    ///
    /// <para>Parsed by handing the text to MimeKit as a message with no body, rather than
    /// by matching on the line: header folding, continuation lines and the odd server that
    /// writes the field in a different case are all somebody else's solved problem.</para>
    /// </summary>
    private static string? MessageIdIn(string headerText)
    {
        try
        {
            using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(
                headerText.ReplaceLineEndings("\r\n").TrimEnd() + "\r\n\r\n"));

            return MimeMessage.Load(stream).MessageId;
        }
        catch
        {
            // A report we cannot parse is still a report. Returning null costs the thread,
            // not the recognition — the bounce is still kept out of the ticket queue.
            return null;
        }
    }

    /// <summary>
    /// Who it failed for, as the report's own status part says — <c>Final-Recipient</c>,
    /// falling back to <c>Original-Recipient</c>.
    ///
    /// <para>Taken from the machine-readable part rather than from the human-readable one,
    /// because the latter is whatever the sending server felt like writing.</para>
    /// </summary>
    public static string? FailedRecipient(MimeMessage message)
    {
        if (!Is(message))
        {
            return null;
        }

        foreach (MimeEntity part in message.BodyParts)
        {
            if (part is not MessageDeliveryStatus status)
            {
                continue;
            }

            foreach (HeaderList fields in status.StatusGroups)
            {
                string? recipient = fields["Final-Recipient"] ?? fields["Original-Recipient"];

                if (!string.IsNullOrWhiteSpace(recipient))
                {
                    // "rfc822; somebody@example.com" — the address type, then the address.
                    string[] parts = recipient.Split(';', 2);

                    return (parts.Length == 2 ? parts[1] : parts[0]).Trim();
                }
            }
        }

        return null;
    }
}
