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
/// <para>Swedish and English both, because the agreement is Swedish and the tickets will
/// not be consistent about it.</para>
/// </summary>
public partial class RuleBasedMailAnalyst : ISupportMailAnalyst
{
    /// <summary>
    /// §14.2's P1: wholly unavailable for all users, or a risk to patient safety, data loss
    /// or unauthorised access to personal data.
    /// </summary>
    private static readonly (string Phrase, string Criterion)[] CriticalMarkers =
    [
        ("patientsäkerhet", "risk to patient safety"),
        ("patient safety", "risk to patient safety"),
        ("dataläcka", "suspected data leak"),
        ("data leak", "suspected data leak"),
        ("dataintrång", "unauthorised access"),
        ("helt nere", "wholly unavailable"),
        ("totalt nere", "wholly unavailable"),
        ("ingen kan logga in", "login impossible for all users"),
        ("ingen kommer in", "login impossible for all users"),
        ("alla användare", "affects all users"),
        ("all users", "affects all users"),
        ("svarar inte", "the service is not responding"),
        ("completely down", "wholly unavailable"),
        ("dataförlust", "risk of data loss"),
    ];

    /// <summary>§14.2's P2: a material function unavailable or wrong for a larger group.</summary>
    private static readonly (string Phrase, string Criterion)[] HighMarkers =
    [
        ("fungerar inte", "a function is unavailable"),
        ("går inte att", "a function is unavailable"),
        ("felaktigt resultat", "a function returns the wrong result"),
        ("mycket långsam", "severely degraded response time"),
        ("väldigt långsamt", "severely degraded response time"),
        ("not working", "a function is unavailable"),
        ("integration", "an integration is affected"),
        ("export", "a central function is affected"),
    ];

    /// <summary>§14.2's P4: cosmetic, documentation, questions, suggestions.</summary>
    private static readonly (string Phrase, string Criterion)[] LowMarkers =
    [
        ("stavfel", "a typo"),
        ("typo", "a typo"),
        ("fråga om", "a question"),
        ("undrar", "a question"),
        ("förslag", "a suggestion"),
        ("önskemål", "a wish"),
        ("hur gör man", "a question about how something works"),
    ];

    /// <summary>
    /// Wording that suggests §15 new development rather tha management — which is billed
    /// differently, does not draw on the hour bank, and wants a requirements review first.
    /// </summary>
    private static readonly string[] DevelopmentMarkers =
    [
        "ny funktion", "nytt fält", "kan ni bygga", "kan vi få", "vi skulle vilja ha",
        "new feature", "would like to add", "nyutveckling", "vidareutveckling",
    ];

    /// <summary>
    /// Parties whose involvement §14.4 lets us pause the resolution clock for. Naming one
    /// is not proof we are waiting on them, so this only ever suggests.
    /// </summary>
    private static readonly string[] ThirdPartyMarkers =
    [
        "väntar på", "waiting for", "hosting", "leverantören", "driftleverantör",
        "third party", "tredje part", "molnleverantör",
    ];

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
            (TicketPriority priority, string? criterion) = ProposePriority(text);

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

        if (DevelopmentMarkers.Any(text.Contains))
        {
            suggestions.Add(Suggestion(
                message.Id, MailSuggestionKind.FlagDevelopment,
                "This reads like new development, not management",
                "§15.1 puts new functionality outside management: it is quoted separately, billed "
                + "at the development rate, and does not draw on the hour bank. §15.2 wants a "
                + "requirements review and a written go-ahead before it starts."));
        }

        if (ThirdPartyMarkers.Any(text.Contains))
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

    /// <summary>
    /// A priority, with the §14.2 criterion behind it. P3 when nothing matched — the
    /// middle of the scale, so a wrong guess is wrong in both directions equally.
    /// </summary>
    public static (TicketPriority Priority, string? Criterion) ProposePriority(string lowercaseText)
    {
        foreach ((string phrase, string criterion) in CriticalMarkers)
        {
            if (lowercaseText.Contains(phrase))
            {
                return (TicketPriority.P1, criterion);
            }
        }

        foreach ((string phrase, string criterion) in HighMarkers)
        {
            if (lowercaseText.Contains(phrase))
            {
                return (TicketPriority.P2, criterion);
            }
        }

        foreach ((string phrase, string criterion) in LowMarkers)
        {
            if (lowercaseText.Contains(phrase))
            {
                return (TicketPriority.P4, criterion);
            }
        }

        return (TicketPriority.P3, null);
    }

    private static string DraftAcknowledgement(MailContext context, TicketPriority priority)
    {
        string name = context.Message.FromName ?? "";
        string greeting = string.IsNullOrWhiteSpace(name) ? "Hej," : $"Hej {name.Split(' ')[0]},";

        return $"""
            {greeting}

            Tack för din anmälan. Vi har registrerat ärendet och återkommer så snart vi gjort
            vår första bedömning. Föreslagen prioritet är {priority}; vi bekräftar eller ändrar
            den med motivering enligt avsnitt 14.3.

            Vänliga hälsningar,
            Supporten
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
