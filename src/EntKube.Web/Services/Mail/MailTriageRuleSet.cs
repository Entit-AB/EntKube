using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The phrases the mailbox watches for, as the analyst sees them.
///
/// <para>Pure and immutable: built either from a tenant's configured rules or from
/// <see cref="BuiltIn"/>, then handed to the analyst. Keeping the matching here rather
/// than in the analyst means it can be checked without a database, and means a tenant's
/// rules and the built-in ones behave identically by construction.</para>
/// </summary>
/// <param name="Rules">Every rule, in match order.</param>
public sealed record MailTriageRuleSet(IReadOnlyList<MailTriageRule> Rules)
{
    /// <summary>
    /// The phrases a new tenant starts with — Swedish and English, because the agreement
    /// is Swedish and the mail will not be consistent about it. Examples to be edited,
    /// not a specification: the point of the table being configuration is that whoever
    /// reads the mail every day knows better than this list does.
    /// </summary>
    public static MailTriageRuleSet BuiltIn { get; } = new([.. Defaults()]);

    /// <summary>
    /// The priority a message's text proposes, with the §14.2 criterion behind it. P3 when
    /// nothing matches — the middle of the scale, so a wrong guess is wrong in both
    /// directions equally.
    /// </summary>
    public (TicketPriority Priority, string? Criterion) ProposePriority(string lowercaseText)
    {
        foreach (TicketPriority band in (ReadOnlySpan<TicketPriority>)
                 [TicketPriority.P1, TicketPriority.P2, TicketPriority.P4])
        {
            MailTriageRule? hit = Active(MailSignal.Priority)
                .FirstOrDefault(r => r.Priority == band && Matches(r, lowercaseText));

            if (hit is not null)
            {
                return (band, hit.Criterion);
            }
        }

        return (TicketPriority.P3, null);
    }

    /// <summary>Whether the text reads like §15 new development rather than management.</summary>
    public bool LooksLikeDevelopment(string lowercaseText) =>
        Active(MailSignal.Development).Any(r => Matches(r, lowercaseText));

    /// <summary>
    /// Whether a third party is named. Naming one is not proof we are waiting on them, so
    /// this only ever feeds a suggestion.
    /// </summary>
    public bool MentionsThirdParty(string lowercaseText) =>
        Active(MailSignal.ThirdParty).Any(r => Matches(r, lowercaseText));

    private IEnumerable<MailTriageRule> Active(MailSignal signal) =>
        Rules.Where(r => r.IsEnabled && r.Signal == signal).OrderBy(r => r.SortOrder);

    private static bool Matches(MailTriageRule rule, string lowercaseText) =>
        !string.IsNullOrWhiteSpace(rule.Phrase)
        && lowercaseText.Contains(rule.Phrase.Trim().ToLowerInvariant());

    /// <summary>
    /// The seed rows, unattached to a tenant. <c>MailTriageRuleService</c> stamps a tenant
    /// on copies of these when somebody chooses to start from them.
    /// </summary>
    public static IEnumerable<MailTriageRule> Defaults()
    {
        int order = 0;

        MailTriageRule Rule(MailSignal signal, string phrase, TicketPriority? priority, string? criterion) =>
            new()
            {
                Id = Guid.NewGuid(),
                Signal = signal,
                Phrase = phrase,
                Priority = priority,
                Criterion = criterion,
                SortOrder = order++,
            };

        // §14.2's P1: wholly unavailable for all users, or a risk to patient safety, data
        // loss or unauthorised access to personal data.
        yield return Rule(MailSignal.Priority, "patientsäkerhet", TicketPriority.P1, "risk to patient safety");
        yield return Rule(MailSignal.Priority, "patient safety", TicketPriority.P1, "risk to patient safety");
        yield return Rule(MailSignal.Priority, "dataläcka", TicketPriority.P1, "suspected data leak");
        yield return Rule(MailSignal.Priority, "data leak", TicketPriority.P1, "suspected data leak");
        yield return Rule(MailSignal.Priority, "dataintrång", TicketPriority.P1, "unauthorised access");
        yield return Rule(MailSignal.Priority, "helt nere", TicketPriority.P1, "wholly unavailable");
        yield return Rule(MailSignal.Priority, "totalt nere", TicketPriority.P1, "wholly unavailable");
        yield return Rule(MailSignal.Priority, "completely down", TicketPriority.P1, "wholly unavailable");
        yield return Rule(MailSignal.Priority, "ingen kan logga in", TicketPriority.P1, "login impossible for all users");
        yield return Rule(MailSignal.Priority, "ingen kommer in", TicketPriority.P1, "login impossible for all users");
        yield return Rule(MailSignal.Priority, "alla användare", TicketPriority.P1, "affects all users");
        yield return Rule(MailSignal.Priority, "all users", TicketPriority.P1, "affects all users");
        yield return Rule(MailSignal.Priority, "svarar inte", TicketPriority.P1, "the service is not responding");
        yield return Rule(MailSignal.Priority, "dataförlust", TicketPriority.P1, "risk of data loss");

        // §14.2's P2: a material function unavailable or wrong for a larger group.
        yield return Rule(MailSignal.Priority, "fungerar inte", TicketPriority.P2, "a function is unavailable");
        yield return Rule(MailSignal.Priority, "går inte att", TicketPriority.P2, "a function is unavailable");
        yield return Rule(MailSignal.Priority, "not working", TicketPriority.P2, "a function is unavailable");
        yield return Rule(MailSignal.Priority, "felaktigt resultat", TicketPriority.P2, "a function returns the wrong result");
        yield return Rule(MailSignal.Priority, "mycket långsam", TicketPriority.P2, "severely degraded response time");
        yield return Rule(MailSignal.Priority, "väldigt långsamt", TicketPriority.P2, "severely degraded response time");
        yield return Rule(MailSignal.Priority, "integration", TicketPriority.P2, "an integration is affected");
        yield return Rule(MailSignal.Priority, "export", TicketPriority.P2, "a central function is affected");

        // §14.2's P4: cosmetic, documentation, questions, suggestions.
        yield return Rule(MailSignal.Priority, "stavfel", TicketPriority.P4, "a typo");
        yield return Rule(MailSignal.Priority, "typo", TicketPriority.P4, "a typo");
        yield return Rule(MailSignal.Priority, "fråga om", TicketPriority.P4, "a question");
        yield return Rule(MailSignal.Priority, "undrar", TicketPriority.P4, "a question");
        yield return Rule(MailSignal.Priority, "hur gör man", TicketPriority.P4, "a question about how something works");
        yield return Rule(MailSignal.Priority, "förslag", TicketPriority.P4, "a suggestion");
        yield return Rule(MailSignal.Priority, "önskemål", TicketPriority.P4, "a wish");

        // §15: new functionality, quoted separately and outside the hour bank.
        foreach (string phrase in (string[])
                 ["ny funktion", "nytt fält", "kan ni bygga", "kan vi få", "vi skulle vilja ha",
                  "nyutveckling", "vidareutveckling", "new feature", "would like to add"])
        {
            yield return Rule(MailSignal.Development, phrase, null, null);
        }

        // §14.4: parties whose involvement can stop the resolution clock.
        foreach (string phrase in (string[])
                 ["väntar på", "leverantören", "driftleverantör", "molnleverantör", "tredje part",
                  "hosting", "waiting for", "third party"])
        {
            yield return Rule(MailSignal.ThirdParty, phrase, null, null);
        }
    }
}
