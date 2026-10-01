using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The default analyst: reference numbers, application names and the vocabulary §14.2 uses
/// to describe each priority.
///
/// <para>Deliberately dull. It threads a reply onto the right ticket, spots which
/// application is being talked about, and proposes a priority with the criterion it
/// matched — so the operator confirming it under §14.3 can see the reasoning rather than
/// being handed a verdict. Everything it produces is a proposal.</para>
///
/// <para>The phrases themselves are not here. They live as tenant configuration, because
/// they are whatever the customers actually write — necessarily in the customer's language,
/// and changing as a portfolio does. The set in force arrives on the context.</para>
/// </summary>
public class RuleBasedMailAnalyst(TicketReference references) : ISupportMailAnalyst
{
    public Task<IReadOnlyList<MailSuggestion>> AnalyseAsync(
        MailContext context, CancellationToken ct = default)
    {
        InboundMailMessage message = context.Message;
        string text = $"{message.Subject}\n{message.Body}".ToLowerInvariant();

        List<MailSuggestion> suggestions = [];

        // A bounce, before anything else — including before the sender is looked at. It is
        // not a message from a person or from a colleague; it is our own mail returned, and
        // the sender is a daemon on some server that may or may not be ours. Left to fall
        // through it is placed by whichever address it was delivered to, which for a
        // customer's own support address places it with that customer and opens a ticket
        // in a daemon's name about our own failure to deliver.
        if (context.IsDeliveryReport)
        {
            return Task.FromResult(FromABounce(context));
        }

        // One of our own people, before anything else is asked. Everything below this
        // decides which customer a message is from and what to open for them, and none of it
        // applies: a colleague's mail is not a report, and the customer it concerns is the
        // ticket's rather than the sender's. Left to fall through, a technician's answer
        // would be flagged as an unrecognised sender and queued for somebody to puzzle over.
        if (context.SenderIsOneOfOurs)
        {
            return Task.FromResult(FromOneOfOurs(context));
        }

        if (context.Customer is null)
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.FlagUnknownSender,
                $"{message.FromAddress} is not a recorded contact for any customer",
                "§14.1 notifies the customer's designated contact, and §23 wants that person named. "
                + "An unrecognised sender may still be genuine — but who they are needs deciding "
                + "before a ticket is opened in somebody's name."));

            return Task.FromResult<IReadOnlyList<MailSuggestion>>(suggestions);
        }

        // Our own server says the From address is not who it claims to be. Flagged, not
        // refused: a forwarded message and a mailing list both fail this honestly, and a
        // rule that dropped mail would drop a genuine P1 sooner or later. What it must not
        // do is stay quiet, because the registers that placed this message read the very
        // header that failed.
        if (message.SenderAuthenticity == SenderAuthenticity.Failed)
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.FlagForgedSender,
                $"Our mail server could not verify that this came from {message.FromAddress}",
                context.PlacedOnTheFromAddress
                    ? $"SPF, DKIM or DMARC failed — and the only thing placing this with "
                      + $"{context.Customer.Name} is that same From address, matched against "
                      + "the §23 contacts or their registered domains. Anybody can write that "
                      + "header. A forward or a mailing list fails this innocently; so does "
                      + "somebody writing as a person named in the agreement."
                    : "SPF, DKIM or DMARC failed, so the sender may not be who the message "
                      + "says. The customer was placed by where the message was delivered "
                      + "rather than by this address, so the routing stands — but a ticket "
                      + "is about to be opened in this person's name."));
        }

        if (context.PlacedOnTheSendersWord)
        {
            // Placed, but on a header the sender wrote. Worth a second look rather than a
            // refusal: naming a customer's address in Cc is what a consultant reporting on
            // their behalf does, and also what somebody would do to put their message in
            // front of that customer's queue.
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.FlagUnknownSender,
                $"Placed with {context.Customer.Name} because their address is in To or Cc",
                $"{message.FromAddress} is not a recorded contact and their domain is not "
                + "registered, so the only thing connecting this message to the customer is "
                + "an address the sender typed. Our own server did not record it as delivered "
                + "there. Usually genuine; worth confirming before a ticket is opened in "
                + "their name."));
        }

        // A reply to an existing ticket, by reference number or by mail threading.
        Ticket? existing = MatchTicket(message, context.OpenTickets);
        App? app = MatchApp(text, context.Apps);

        if (existing is not null)
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.AppendToTicket,
                $"Add to ticket #{existing.Number} — {existing.Title}",
                "The message references that ticket, so it belongs on its history rather than "
                + "opening a second one for the same thing.",
                ticketId: existing.Id));
        }
        else
        {
            (TicketPriority priority, string? criterion) = context.Rules.ProposePriority(text);

            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.OpenTicket,
                app is null
                    ? $"Open a ticket: {Truncate(message.Subject)}"
                    : $"Open a ticket on {app.Name}: {Truncate(message.Subject)}",
                app is null
                    ? "No application name was recognised in the text, so which one this is about "
                      + "needs choosing — the support window and the SLA clock come from it."
                    : $"The message names {app.Name}.",
                appId: app?.Id));

            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.ProposePriority,
                $"Propose {priority}",
                criterion is null
                    ? "Nothing in the text matched §14.2's wording for a higher priority, so P3 is "
                      + "the default rather than an assessment. Confirming it is still a judgement."
                    : $"§14.2 — matched: {criterion}. A phrase is not an assessment; §14.3 makes "
                      + "confirming the priority a written, reasoned act.",
                priority: priority));

            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.DraftReply,
                "Draft an acknowledgement",
                "Ready to send once somebody has read it.",
                draftText: DraftAcknowledgement(context, priority)));
        }

        if (context.Rules.LooksLikeDevelopment(text))
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.FlagDevelopment,
                "This reads like new development, not management",
                "§15.1 puts new functionality outside management: it is quoted separately, billed "
                + "at the development rate, and does not draw on the hour bank. §15.2 wants a "
                + "requirements review and a written go-ahead before it starts."));
        }

        if (context.Rules.MentionsThirdParty(text))
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.SuggestPause,
                "A third party is mentioned — the clock may be pausable",
                "§14.4 stops the resolution clock while we wait on the customer or one of their "
                + "suppliers, and requires the wait to be documented with the party and the reason. "
                + "Whether we are actually waiting is for you to say.",
                ticketId: existing?.Id));
        }

        if (context.TimebankExhausted)
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.FlagTimebank,
                "The hour bank for this month is spent",
                "§11.1 wants the customer's go-ahead before work beyond the bank — except for P1 "
                + "and P2, which proceed without delay and are billed without separate approval."));
        }

        return Task.FromResult<IReadOnlyList<MailSuggestion>>(suggestions);
    }

    /// <summary>
    /// What to do with a bounce.
    ///
    /// <para>Never a ticket. The useful thing is the ticket it already belongs to: when the
    /// receipt for #412 is refused, that customer never learned their number and nothing
    /// anywhere said so. Putting it on #412 is the only place anybody would look — not
    /// customer-visible, because it is about our own plumbing and the one person who must
    /// not be told about it this way is the person it failed to reach.</para>
    ///
    /// <para>A bounce for something we cannot place is still said out loud rather than
    /// dismissed: mail leaving the building and being refused is worth somebody's attention
    /// even when we cannot say which conversation it belonged to.</para>
    /// </summary>
    private IReadOnlyList<MailSuggestion> FromABounce(MailContext context)
    {
        InboundMailMessage message = context.Message;
        Ticket? about = MatchTicket(message, context.OpenTickets);

        return [Suggestion(
            message.Id, MailSuggestionKind.FlagDeliveryFailure,
            about is null
                ? "Mail we sent was refused, and it names no open ticket"
                : $"Mail we sent about ticket #{about.Number} was refused — they never got it",
            about is null
                ? "A delivery report came back for a message that does not name a ticket we "
                  + "still have open. Nothing has been opened for it; the report is below."
                : "A delivery report for one of our own messages on that ticket. Whoever it "
                  + "was addressed to did not receive it — if it was the receipt, they never "
                  + "learned their ticket number. Recorded on the ticket, and not shown to "
                  + "the customer.",
            ticketId: about?.Id)];
    }

    /// <summary>
    /// What to do with a message from one of our own people.
    ///
    /// <para>There is one useful thing it can be: an answer for the customer, written above
    /// the cut line in a mail we sent about a ticket. That needs both halves — a reference
    /// saying which ticket, and something written above the line — and when either is
    /// missing the honest answer is to say so and let a person look. Guessing would either
    /// send the wrong thing to a customer or relay the quoted deadline and the reporter's
    /// name along with it.</para>
    ///
    /// <para>The text that will be sent is carried on the suggestion, so that what an
    /// operator reads in the queue is the thing that goes out and not a description of
    /// it.</para>
    /// </summary>
    private IReadOnlyList<MailSuggestion> FromOneOfOurs(MailContext context)
    {
        InboundMailMessage message = context.Message;
        Ticket? about = MatchTicket(message, context.OpenTickets);

        if (about is null)
        {
            return [Suggestion(
                message.Id, MailSuggestionKind.FlagInternalSender,
                $"{message.FromAddress} is one of ours, and this names no open ticket",
                "A reply to the customer is sent on the ticket it answers, and nothing in the "
                + "subject or the thread says which one. Nothing has been sent.")];
        }

        if (SupportReplyBody.Above(message.Body) is not string said)
        {
            return [Suggestion(
                message.Id, MailSuggestionKind.FlagInternalSender,
                $"Nothing was written above the cut line, on ticket #{about.Number}",
                $"A reply is the part above \"{SupportReplyBody.Sentinel}\". There is nothing "
                + "there, so there is nothing to send — and what is below the line is what we "
                + "sent them, which the customer must not be handed. Nothing has been sent.",
                ticketId: about.Id)];
        }

        return [Suggestion(
            message.Id, MailSuggestionKind.ReplyToCustomer,
            $"Send this to the customer on ticket #{about.Number} — {about.Title}",
            "Written above the cut line by one of our own people, so it is an answer for the "
            + "customer rather than a note on the ticket. It goes from the support address, "
            + "with the reference in the subject, and onto the history as customer-visible.",
            draftText: said,
            ticketId: about.Id)];
    }

    /// <summary>
    /// The ticket a message belongs to: a "#123" in the subject, or a mail thread we can
    /// follow. Nothing fuzzier — attaching a message to the wrong ticket corrupts the
    /// history §14.6 makes evidence.
    /// </summary>
    public Ticket? MatchTicket(InboundMailMessage message, IReadOnlyList<Ticket> open)
    {
        // What the message is a reply to, first. A subject can be edited, translated by a
        // client, or lost to a forward; the thread headers survive all three, and a reply
        // that opens a second ticket about the fault already being worked is the failure
        // this exists to prevent.
        if (SupportMessageId.TicketNumberIn(message.InReplyTo) is int threaded)
        {
            Ticket? byThread = open.FirstOrDefault(t => t.Number == threaded);

            if (byThread is not null)
            {
                return byThread;
            }
        }

        if (ReferencedNumber(references, message.Subject) is int number)
        {
            Ticket? byNumber = open.FirstOrDefault(t => t.Number == number);
            if (byNumber is not null)
            {
                return byNumber;
            }
        }

        return null;
    }

    /// <summary>
    /// The ticket number quoted in a subject line, or null when there is none.
    ///
    /// <para>Public because the mailbox has to ask the same question before it knows which
    /// tickets to consider: a ticket that has been moved to another customer is not in the
    /// set the sender's own customer owns, and the number in the subject is what says to go
    /// looking for it. One reader, so the queue and the matcher cannot come to different
    /// conclusions about what a subject says.</para>
    /// </summary>
    public static int? ReferencedNumber(TicketReference references, string? subject) =>
        references.InSubject(subject)?.Number;

    /// <summary>
    /// The application a message names. Longest name first, so "Journal export" is not
    /// matched as "Journal" when both exist.
    /// </summary>
    public static App? MatchApp(string lowercaseText, IReadOnlyList<App> apps) =>
        apps.Where(a => a.Name.Length > 2)
            .OrderByDescending(a => a.Name.Length)
            .FirstOrDefault(a => lowercaseText.Contains(a.Name.ToLowerInvariant()));

    private static string DraftAcknowledgement(MailContext context, TicketPriority priority)
    {
        string name = context.Message.FromName ?? "";
        string greeting = string.IsNullOrWhiteSpace(name) ? "Hello," : $"Hello {name.Split(' ')[0]},";

        return $"""
            {greeting}

            Thank you for your report. We have registered the ticket and will come back to you
            as soon as we have made our first assessment. The proposed priority is {priority};
            we will confirm or change it with reasons, under section 14.3.

            Kind regards,
            Support
            """;
    }

    private static MailSuggestion Suggestion(
        Guid messageId,
        MailSuggestionKind kind,
        string summary,
        string? reasoning = null,
        string? draftText = null,
        TicketPriority? priority = null,
        Guid? ticketId = null,
        Guid? appId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            Kind = kind,
            Summary = summary,
            Reasoning = reasoning,
            DraftText = draftText,
            Priority = priority,
            TicketId = ticketId,
            AppId = appId,
        };

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..57] + "…";

}
