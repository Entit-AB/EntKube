using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using MimeKit;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Turns a fetched message into the row we keep.
///
/// <para><b>Why this is its own class.</b> None of it needs a mail server, and all of it
/// is where the mistakes are: a missing Message-Id, a subject that is null rather than
/// empty, an HTML-only body, a Date header from a machine whose clock is wrong. Each of
/// those has a right answer, and each of them is checkable at a desk.</para>
/// </summary>
public static class MailMessageReader
{
    /// <summary>
    /// How far ahead of our own clock a Date header is still believed. Mail crosses
    /// machines whose clocks disagree by a little, and rejecting those would be worse than
    /// accepting them.
    /// </summary>
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(15);

    /// <summary>Reads a message into the row we keep.</summary>
    /// <param name="message">The fetched message.</param>
    /// <param name="tenantId">Whose mailbox it arrived in.</param>
    /// <param name="receivedAt">When we fetched it. UTC.</param>
    /// <param name="trustedServer">
    /// The <c>authserv-id</c> whose <c>Authentication-Results</c> are believed. Absent, the
    /// sender's authenticity is recorded as unknown rather than assumed — see
    /// <see cref="SenderAuthentication"/> for why it cannot be worked out from the message.
    /// </param>
    public static InboundMailMessage Read(
        MimeMessage message, Guid tenantId, DateTime receivedAt, string? trustedServer = null)
    {
        MailboxAddress? from = message.From.Mailboxes.FirstOrDefault();

        return new InboundMailMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MessageId = IdentityOf(message),
            InReplyTo = ThreadParentOf(message),
            FromAddress = from?.Address ?? "",
            ToAddresses = string.Join(' ', ClaimedRecipientsOf(message)),
            DeliveredTo = string.Join(' ', EnvelopeRecipientsOf(message)),
            FromName = string.IsNullOrWhiteSpace(from?.Name) ? null : from.Name,
            Subject = message.Subject ?? "",
            Body = BodyOf(message),
            SentAt = SentAtOf(message, receivedAt),
            ReceivedAt = receivedAt,
            SenderAuthenticity = SenderAuthentication.Of(message, trustedServer),
            State = MailTriageState.Received,
        };
    }

    /// <summary>
    /// The addresses the <em>sender</em> put in To and Cc, lower-cased and without
    /// duplicates.
    ///
    /// <para><b>A claim, not a fact.</b> Anybody can name any address there, including a
    /// customer's own support alias, and the message need never have gone near it. Kept
    /// because it is usually true and always evidence of what was intended — but routing
    /// on it alone would let a stranger place their mail in a chosen customer's queue.</para>
    /// </summary>
    public static IReadOnlyList<string> ClaimedRecipientsOf(MimeMessage message)
    {
        HashSet<string> addresses = new(StringComparer.OrdinalIgnoreCase);

        foreach (MailboxAddress mailbox in message.To.Mailboxes.Concat(message.Cc.Mailboxes))
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Address))
            {
                addresses.Add(mailbox.Address.Trim().ToLowerInvariant());
            }
        }

        return [.. addresses];
    }

    /// <summary>
    /// The addresses our own delivering server recorded.
    ///
    /// <para>This is where an alias survives — it is expanded before the message is
    /// written, so an address a customer was given appears in no header the sender
    /// composed. It is also the only recipient evidence a sender cannot forge.</para>
    /// </summary>
    public static IReadOnlyList<string> EnvelopeRecipientsOf(MimeMessage message)
    {
        HashSet<string> addresses = new(StringComparer.OrdinalIgnoreCase);

        foreach (string header in (string[])["Delivered-To", "X-Original-To", "X-Envelope-To"])
        {
            foreach (Header found in message.Headers.Where(
                h => h.Field.Equals(header, StringComparison.OrdinalIgnoreCase)))
            {
                // These carry a bare address far more often than a display name, but parse
                // properly anyway: a server is free to write "Support <a@b>".
                string? address = MailboxAddress.TryParse(found.Value, out MailboxAddress? parsed)
                    ? parsed.Address
                    : found.Value.Trim().Trim('<', '>');

                if (!string.IsNullOrWhiteSpace(address) && address.Contains('@', StringComparison.Ordinal))
                {
                    addresses.Add(address.Trim().ToLowerInvariant());
                }
            }
        }

        return [.. addresses];
    }

    /// <summary>
    /// The message's identity, which is what stops the same mail becoming two tickets.
    ///
    /// <para>Message-Id is required by RFC 5322 and supplied by everything that sends mail
    /// properly, so it is used when it is there. When it is not — some appliances and
    /// scripts omit it — a digest of the parts that do not change stands in. It has to be
    /// derived, not random: a synthesised id that differed between two polls would defeat
    /// the very check it exists for, and the message would be taken in again every couple
    /// of minutes for as long as it sat in the mailbox.</para>
    /// </summary>
    public static string IdentityOf(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return message.MessageId.Trim();
        }

        // A separator no header can contain, so two different messages cannot produce the
        // same material by running one field into the next — "A B" plus "C" must not digest
        // the same as "A" plus "B C".
        //
        // Written as an escape and not as the character itself: a raw NUL in a source file
        // is invisible in an editor and the first tool to strip it would leave an empty
        // separator, which is exactly the collision this exists to prevent.
        const string Separator = "\u0000";

        string material = string.Join(
            Separator,
            message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            message.Subject ?? "",
            message.Date.UtcDateTime.ToString("O"),
            message.TextBody ?? message.HtmlBody ?? "");

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return $"synthesised-{Convert.ToHexString(digest)[..32].ToLowerInvariant()}@entkube";
    }

    /// <summary>
    /// What this message is a reply to, for threading it onto an existing ticket.
    ///
    /// <para>In-Reply-To names the immediate parent and is preferred. Some clients send
    /// only References, whose last entry is the same thing, so that is the fallback —
    /// without it a reply from those clients opens a second ticket about the fault
    /// already being worked.</para>
    /// </summary>
    public static string? ThreadParentOf(MimeMessage message)
    {
        // One of ours anywhere in the chain wins. In a long conversation the immediate
        // parent is often a colleague's message, and answering "what is this a reply to"
        // with that loses the only thing in the thread that names a ticket.
        if (SupportMessageId.OursIn(
                [message.InReplyTo, .. message.References ?? []]) is string ours)
        {
            return ours.Trim();
        }

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            return message.InReplyTo.Trim();
        }

        string? last = message.References?.LastOrDefault();

        return string.IsNullOrWhiteSpace(last) ? null : last.Trim();
    }

    /// <summary>
    /// When the message was sent, which is what a response time is counted from.
    ///
    /// <para><b>The Date header is not trusted past our own clock.</b> It is written by
    /// the sender, and a sender's clock can be wrong or its header forged. A date in the
    /// future would start an SLA clock before the mail existed and make a response look
    /// late — far enough out, it would make one look answered before it was asked.
    /// Anything ahead of the moment we fetched it, beyond ordinary skew, falls back to
    /// that moment: we certainly had it by then.</para>
    ///
    /// <para>A date in the past is left alone. Mail really does sit in queues, and the
    /// customer is entitled to have the clock run from when they sent it.</para>
    /// </summary>
    public static DateTime SentAtOf(MimeMessage message, DateTime receivedAt)
    {
        DateTime sent = message.Date.UtcDateTime;

        return sent == default || sent > receivedAt + ClockSkewAllowance ? receivedAt : sent;
    }

    /// <summary>
    /// The text of the message.
    ///
    /// <para>The plain-text part when there is one. When the sender wrote only HTML — most
    /// mail from a corporate client — the markup is reduced to something readable, because
    /// the analyst matches phrases against this and an operator reads it in the queue.
    /// Neither of those is served by a wall of tags.</para>
    /// </summary>
    public static string BodyOf(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            return message.TextBody.ReplaceLineEndings("\n").Trim();
        }

        return string.IsNullOrWhiteSpace(message.HtmlBody) ? "" : PlainTextFrom(message.HtmlBody);
    }

    /// <summary>The block-level tags worth a line break; every other tag just disappears.</summary>
    private static readonly HashSet<string> BreakingTags =
    [
        "br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "table", "blockquote", "pre",
    ];

    /// <summary>
    /// HTML reduced to its text.
    ///
    /// <para>Deliberately crude: it drops tags, unwraps the handful of entities that turn
    /// up in ordinary prose, and turns block elements into line breaks. It is not a
    /// renderer and does not need to be — what reads this is a phrase matcher and a person
    /// skimming a queue, and both want the words.</para>
    ///
    /// <para>Script and style are removed with their contents rather than flattened into
    /// the text, or a marketing mail would arrive in the queue as a page of JavaScript.</para>
    /// </summary>
    public static string PlainTextFrom(string html)
    {
        StringBuilder text = new(html.Length);
        int i = 0;

        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                text.Append(html[i]);
                i++;
                continue;
            }

            int close = html.IndexOf('>', i);

            if (close < 0)
            {
                // An unterminated tag: whatever follows is markup, not prose.
                break;
            }

            string name = new([.. html[(i + 1)..close].TrimStart('/').TrimStart()
                .TakeWhile(char.IsLetterOrDigit)]);

            if (name.Equals("script", StringComparison.OrdinalIgnoreCase)
                || name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                int end = html.IndexOf($"</{name}", close, StringComparison.OrdinalIgnoreCase);
                int after = end < 0 ? -1 : html.IndexOf('>', end);

                if (after < 0)
                {
                    break;
                }

                i = after + 1;
                continue;
            }

            if (BreakingTags.Contains(name.ToLowerInvariant()))
            {
                text.Append('\n');
            }

            i = close + 1;
        }

        string plain = text.ToString()
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase)
            // Last, so an escaped entity in the source does not decode twice.
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .ReplaceLineEndings("\n");

        // Dropping tags leaves runs of blank lines behind; collapse them to one.
        StringBuilder result = new();
        bool lastWasBlank = true;

        foreach (string line in plain.Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0)
            {
                if (!lastWasBlank)
                {
                    result.Append('\n');
                }

                lastWasBlank = true;
                continue;
            }

            result.Append(line).Append('\n');
            lastWasBlank = false;
        }

        return result.ToString().Trim();
    }
}
