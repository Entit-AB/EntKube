using System.Text.RegularExpressions;
using EntKube.Web.Data;

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
public partial class RuleBasedMailAnalyst : ISupportMailAnalyst
{
    public Task<IReadOnlyList<MailSuggestion>> AnalyseAsync(
        MailContext context, CancellationToken ct = default)
    {
        InboundMailMessage message = context.Message;
        string text = $"{message.Subject}\n{message.Body}".ToLowerInvariant();

        List<MailSuggestion> suggestions = [];

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
    /// The ticket a message belongs to: a "#123" in the subject, or a mail thread we can
    /// follow. Nothing fuzzier — attaching a message to the wrong ticket corrupts the
    /// history §14.6 makes evidence.
    /// </summary>
    public static Ticket? MatchTicket(InboundMailMessage message, IReadOnlyList<Ticket> open)
    {
        Match reference = TicketReference().Match(message.Subject);

        if (reference.Success && int.TryParse(reference.Groups[1].Value, out int number))
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

    [GeneratedRegex(@"#(\d{1,9})")]
    private static partial Regex TicketReference();
}
